using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    // Explicit recovery/migration prepares and publishes under one mutation gate.
    // A null lifetime preserves the existing device's lifetime and requires a local
    // certificate. Ordinary establishment retains its separate durable preparation.
    internal async Task<DeviceStatePublishResult> PublishDeviceStateAsync(string relayId, DeviceStatePublishOptions options,
        TimeSpan? certificateValidity, CancellationToken cancellationToken, string? pushGateway = null)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        if (certificateValidity is null)
        {
            var current = Local ?? throw new InvalidOperationException("A local device is required to transfer authorization.");
            if (current.NotBefore > Clock.UtcNow.ToUnixTimeSeconds())
                throw new InvalidOperationException("The local device certificate is not yet valid.");
            certificateValidity = TimeSpan.FromSeconds(current.ExpiresAt - current.NotBefore);
        }
        if (Local is null)
            await CreateDeviceCoreAsync(certificateValidity.Value, pushGateway, cancellationToken).ConfigureAwait(false);
        else if (!IsCurrentCertificate(Local))
            await RenewDeviceCoreAsync(certificateValidity.Value, default, cancellationToken).ConfigureAwait(false);
        return await PublishDeviceStateCoreAsync(relayId, null, options, cancellationToken).ConfigureAwait(false);
    }

    // Publish exactly one revision after a complete observed snapshot. A null snapshot
    // permits only revision-zero bootstrap; it never advances a hidden revision floor.
    async Task<DeviceStatePublishResult> PublishDeviceStateFromSnapshotAsync(string relayId,
        IReadOnlyList<DeviceCertificate> certificates, AccountDeviceState? previous, CancellationToken cancellationToken,
        string? operationKey = null, string? removedDeviceId = null, bool classifySubmissionFailure = false)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (previous is not null && (previous.Account != Options.AccountId || previous.Validate(Context) is not null))
            throw new ArgumentException("The previous device state must be valid and belong to this account.", nameof(previous));
        if (accountSigner is null)
            throw new InvalidOperationException("An account signer is required to publish device state.");
        var basis = previous?.Revision ?? -1;
        var revision = AccountDeviceState.GetNextRevision(null, basis);
        await using var database = new MeshlineDbContext(databaseOptions);
        var known = await database.DeviceStates.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == Options.AccountId, cancellationToken).ConfigureAwait(false);
        if (known is not null && (known.Revision > basis || known.Revision == basis && known.DocumentJson != previous?.ToJson()))
            throw new DeviceStatePublicationException(DeviceStatePublicationFailure.StateChanged,
                "A newer or conflicting device state was observed before publication.");
        var signed = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "device.state.publish", cancellationToken).ConfigureAwait(false);
        var pending = signed is { Pending: true }
            ? ProtocolModel.FromJson<AccountDeviceState>(signed.DocumentJson)! : null;
        var retryPending = pending is not null && signed!.RelayId == relayId && pending.Revision == revision
            && MatchesCertificates(pending, certificates);
        if (!retryPending && pending is not null && pending.Revision > basis)
            throw new DeviceStatePublicationException(DeviceStatePublicationFailure.PendingRequestConflict,
                "A different device-state submission is still unresolved. Reconcile it before changing its intent.");
        // Account signing may call user code or wait for a wallet. No SQLite
        // transaction spans it; its result is checked against fresh data below.
        var request = await PrepareDeviceStatePublicationAsync(signed, relayId, certificates, revision, cancellationToken).ConfigureAwait(false);
        await database.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var transaction = ((SqliteConnection)database.Database.GetDbConnection()).BeginTransaction(deferred: false))
        {
            await using var enlisted = await database.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            known = await database.DeviceStates.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == Options.AccountId, cancellationToken).ConfigureAwait(false);
            if (known is not null && (known.Revision > basis || known.Revision == basis && known.DocumentJson != previous?.ToJson()))
                throw new DeviceStatePublicationException(DeviceStatePublicationFailure.StateChanged,
                    "A newer or conflicting device state was observed while signing the publication.");
            signed = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "device.state.publish", cancellationToken).ConfigureAwait(false);
            if (signed is { Pending: true } && signed.Revision > basis
                && (signed.RelayId != relayId || signed.DocumentJson != request.ToJson()))
                throw new DeviceStatePublicationException(DeviceStatePublicationFailure.PendingRequestConflict,
                    "A different device-state submission is still unresolved. Reconcile it before changing its intent.");
            if (previous is not null && removedDeviceId is not null)
            {
                var issues = await CheckRemovalContinuityAsync(database, previous, removedDeviceId, certificates, cancellationToken).ConfigureAwait(false);
                if (issues.Count != 0)
                    throw new DeviceAuthorizationException(DeviceAuthorizationFailure.ContinuityUnverifiable,
                        "Contact authorization changed before device publication. No device was removed.")
                    { ContactIssues = issues };
            }
            signed = await SaveDeviceStatePublicationAsync(database, signed, relayId, request, cancellationToken, operationKey).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        // Pending is durable before the request leaves the process. Unknown
        // outcomes keep contact authorization writes blocked across restart.
        return await SubmitDeviceStatePublicationAsync(database, signed, request, cancellationToken,
            classifySubmissionFailure).ConfigureAwait(false);
    }

    static bool MatchesCertificates(AccountDeviceState state, IReadOnlyList<DeviceCertificate> certificates) =>
        state.Certificates.Select(static value => value.ToJson()).SequenceEqual(certificates.Select(static value => value.ToJson()));

    async Task<AccountDeviceState> PrepareDeviceStatePublicationAsync(SignedRequestRecord? signed, string relayId,
        IReadOnlyList<DeviceCertificate> certificates, long revision, CancellationToken cancellationToken)
    {
        var signer = accountSigner ?? throw new InvalidOperationException("An account signer is required to publish device state.");
        var request = signed is { Pending: true } && signed.RelayId == relayId && signed.Revision == revision
            ? ProtocolModel.FromJson<AccountDeviceState>(signed.DocumentJson)! : null;
        if (request is null || !MatchesCertificates(request, certificates))
        {
            request = new AccountDeviceState
            {
                Account = Options.AccountId,
                AccountPublicKey = signer.PublicKey,
                Revision = revision,
                Certificates = [.. certificates],
                AccountSignature = []
            };
            request = request with { AccountSignature = [.. await signer.SignAsync(request.GetSigningInput(Context), cancellationToken).ConfigureAwait(false)] };
            if (request.Validate(Context) is { } violation)
                throw new ArgumentException(violation.Message, nameof(certificates));
        }
        return request;
    }

    static async Task<SignedRequestRecord> SaveDeviceStatePublicationAsync(MeshlineDbContext database, SignedRequestRecord? signed,
        string relayId, AccountDeviceState request, CancellationToken cancellationToken, string? operationKey = null)
    {
        var json = request.ToJson();
        if (signed is { Pending: true } && signed.RelayId == relayId && signed.DocumentJson == json) return signed;
        if (signed is null)
        {
            signed = new() { Method = "device.state.publish", RelayId = relayId, DocumentJson = json, Revision = request.Revision, Pending = true, OperationKey = operationKey };
            database.SignedRequests.Add(signed);
        }
        else
        {
            signed.RelayId = relayId;
            signed.DocumentJson = json;
            signed.Revision = request.Revision;
            signed.Pending = true;
            signed.OperationKey = operationKey;
        }
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return signed;
    }

    async Task<DeviceStatePublishResult> SubmitDeviceStatePublicationAsync(MeshlineDbContext database, SignedRequestRecord signed,
        AccountDeviceState request, CancellationToken cancellationToken, bool classifySubmissionFailure = false)
    {
        DeviceStatePublishResponse response;
        var relayClient = await relayClients.GetAsync(signed.RelayId, accountSigner!, cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                response = await relayClient.SendHttpAsync<DeviceStatePublishResponse>(HttpMethod.Put, "device.state.publish", request, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException exception) when (exception.Error.IsDefinitiveRejection())
            {
                signed.Pending = false;
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                if (classifySubmissionFailure && !IsDeviceStateConflict(exception))
                    throw new DeviceAuthorizationException(DeviceAuthorizationFailure.PublicationRejected,
                        "The relay rejected device authorization.", exception);
                throw;
            }
            DateTimeOffset? stagedUntil = response.StagedUntil is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            if (response.Status == DeviceStatePublishStatus.Accepted)
            {
                await SaveDeviceStateAsync(request, cancellationToken).ConfigureAwait(false);
                CacheDeviceState(request, Clock.UtcNow);
            }
            else if (stagedUntil is null || stagedUntil <= Clock.UtcNow)
                throw new InvalidDataException("The relay returned a missing or expired staging interval.");
            // Staging and transport failures leave the request pending. Only authoritative
            // state reconciliation or a definitive rejection can finish that request.
            return new DeviceStatePublishResult { DeviceState = request, Status = response.Status, StagedUntil = stagedUntil };
        }
        catch (Exception exception) when (classifySubmissionFailure && IsAuthorizationFailure(exception, cancellationToken)
            && exception is not DeviceAuthorizationException
            && (exception is not RelayException relay || !relay.Error.IsDefinitiveRejection()))
        {
            throw new DeviceAuthorizationException(DeviceAuthorizationFailure.SubmissionUnknown,
                "The device publication outcome is unknown. Retry with the retained signed request.", exception);
        }
    }
}
