using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    enum DeviceAuthorizationStage { Inspecting, Preparing, Publishing, Confirming }

    internal async Task<DeviceAuthorizationInspection> InspectAuthorizationAsync(string? relayId, CancellationToken cancellationToken,
        DeviceAuthorizationInspection? inspection = null, DeviceAuthorizationIntent? intent = null)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            if (inspection is null)
            {
                DeviceCertificate? certificate = null;
                await _deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var local = await database.LocalDevices.AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    var binding = await database.Bindings.Select(value => new
                    {
                        value.DeviceId,
                        ActivatedOperationId = database.DevicePreparations.Select(preparation => preparation.ActivatedOperationId).SingleOrDefault()
                    }).SingleAsync(cancellationToken).ConfigureAwait(false);
                    if (local is null && binding.DeviceId is not null)
                        throw new DeviceAuthorizationException(DeviceAuthorizationFailure.KeyAccessRequired,
                            "The bound local device is missing. Restore the complete local data; the database has been preserved.")
                        { KeyIssue = Interactions.SecretAccessFailure.PermanentlyLost };
                    await ValidateLocalKeysAsync(local, cancellationToken).ConfigureAwait(false);
                    // A replay retains the original identity even after its own activation.
                    var previousLocalDeviceId = intent is not null && binding.ActivatedOperationId == intent.OperationId
                        ? intent.PreviousLocalDeviceId : binding.DeviceId;
                    inspection = new(false, previousLocalDeviceId);
                    if (local is not null)
                        certificate = ReadLocalCertificate(local);
                }
                finally { _deviceGate.Release(); }
                if (certificate is not null)
                    await relayClients.RebindDeviceAsync(inspection.PreviousLocalDeviceId, certificate).ConfigureAwait(false);
            }
            AccountDeviceState? state = null;
            var authorized = false;
            if (relayId is not null)
            {
                if (Local is null || Local.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()) RequireAuthorizationSigner();
                state = await ReadAuthorizationStateAsync(relayId, cancellationToken).ConfigureAwait(false)
                    ?? throw new DeviceAuthorizationException(DeviceAuthorizationFailure.StateUnavailable, "An existing route has no complete device state. No replacement state was published.");
                var replaceDeviceId = intent?.ProposedReplaceDeviceId ?? intent?.ReplaceDeviceId;
                authorized = IsLocalAuthorizationEffective(state)
                    && (replaceDeviceId is null || !state.Certificates.Any(value => value.GetDeviceId(Context) == replaceDeviceId));
                if (authorized && !await database.SignedRequests.AnyAsync(value => value.Method == "device.state.publish" && value.Pending, cancellationToken).ConfigureAwait(false)
                    && !await database.DevicePreparations.AnyAsync(value => value.OperationId != null, cancellationToken).ConfigureAwait(false))
                {
                    await relayClients.GetAsync(relayId, this, cancellationToken).ConfigureAwait(false);
                }
                else authorized = false;
            }
            return inspection with { IsAuthorized = authorized };
        }
        catch (Exception exception) when (IsAuthorizationFailure(exception, cancellationToken))
        {
            throw AuthorizationFailure(exception);
        }
    }

    internal async Task<DeviceAuthorizationStatus> AuthorizeAsync(DeviceAuthorizationIntent intent, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        var stage = DeviceAuthorizationStage.Preparing;
        var replacementDisposition = DeviceReplacementDisposition.Unresolved;
        var replacementValidated = false;
        try
        {
            var route = accountManager.Route;
            if (route is not null && (route.RelayId != intent.RelayId || route.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()))
                throw new DeviceAuthorizationException(DeviceAuthorizationFailure.StateUnavailable, "Confirm the original home route before authorizing this device.");
            var awaitingRoute = route is null;
            await using var database = new MeshlineDbContext(databaseOptions);
            if (intent.ProposedReplaceDeviceId is { } replacementDeviceId && replacementDeviceId != intent.ReplaceDeviceId)
            {
                // A submitted proposal survives a missed workflow checkpoint. Reuse its
                // execution evidence, then verify the exact snapshot and relay effect below.
                var current = awaitingRoute ? null : await ReadAuthorizationStateAsync(intent.RelayId, cancellationToken).ConfigureAwait(false);
                var signed = await database.SignedRequests.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Method == "device.state.publish", cancellationToken).ConfigureAwait(false);
                var proposed = signed?.RelayId == intent.RelayId
                    && signed.OperationKey == $"{intent.OperationId:N}:{replacementDeviceId}";
                var completed = IsLocalAuthorizationEffective(current)
                    && (intent.ReplaceDeviceId is null || !current!.Certificates.Any(value => value.GetDeviceId(Context) == intent.ReplaceDeviceId));
                if (!proposed && (completed || signed is { Pending: true }))
                {
                    replacementDisposition = DeviceReplacementDisposition.Rejected;
                    throw new DeviceAuthorizationException(DeviceAuthorizationFailure.ReplacementConflict, "Reconcile the previous device authorization before changing the replacement.");
                }
                intent = intent with { ReplaceDeviceId = replacementDeviceId, ProposedReplaceDeviceId = null };
                replacementValidated = true;
                // A fresh proposal has passed validation. Its failure may still be
                // safely recorded as the chosen intent if no publication was saved.
                if (!proposed) replacementDisposition = DeviceReplacementDisposition.Accepted;
            }
            var replaceDeviceId = intent.ReplaceDeviceId;
            var certificate = await PrepareLocalDeviceAsync(intent, intent.Validity, cancellationToken).ConfigureAwait(false);
            var previousLocalDeviceId = intent.PreviousLocalDeviceId;
            var deviceId = certificate.GetDeviceId(Context);
            if (replaceDeviceId == deviceId)
                throw new ArgumentException("ReplaceDeviceId cannot identify the device being enabled.", nameof(intent));

            AccountDeviceState? confirmed = null;
            var published = false;
            var conflicts = 0;
            while (true)
            {
                stage = DeviceAuthorizationStage.Inspecting;
                AccountDeviceState? state = null;
                if (!awaitingRoute)
                    state = await ReadAuthorizationStateAsync(intent.RelayId, cancellationToken).ConfigureAwait(false)
                        ?? throw new DeviceAuthorizationException(DeviceAuthorizationFailure.StateUnavailable, "The complete device state is unavailable.");
                var pending = await database.SignedRequests.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.Method == "device.state.publish" && value.Pending, cancellationToken).ConfigureAwait(false);
                if (!IsCurrentCertificate(certificate) && pending is null)
                {
                    stage = DeviceAuthorizationStage.Preparing;
                    certificate = await PrepareLocalDeviceAsync(intent, intent.Validity, cancellationToken).ConfigureAwait(false);
                    deviceId = certificate.GetDeviceId(Context);
                    if (!IsCurrentCertificate(certificate))
                        throw new DeviceAuthorizationException(DeviceAuthorizationFailure.NotEffective,
                            "The renewed certificate expired during preparation. Retry with the retained device keys.");
                    stage = DeviceAuthorizationStage.Inspecting;
                }
                var registered = state?.Certificates.FirstOrDefault(value => value.GetDeviceId(Context) == deviceId);
                var replacementPresent = replaceDeviceId is { } replaced && state?.Certificates.Any(value => value.GetDeviceId(Context) == replaced) == true;
                if (registered?.ToJson() == certificate.ToJson() && !replacementPresent && pending is null)
                {
                    confirmed = state;
                    break;
                }
                RequireAuthorizationSigner();
                var certificates = state?.Certificates.Where(value => value.GetDeviceId(Context) != deviceId
                    && value.GetDeviceId(Context) != replaceDeviceId).ToList() ?? [];
                certificates.Add(certificate);
                if (pending is not null)
                {
                    stage = DeviceAuthorizationStage.Publishing;
                    var request = ProtocolModel.FromJson<AccountDeviceState>(pending.DocumentJson)!;
                    // Reuse only the exact pending operation for this authorization.
                    // A usable old state cannot prove that another signed mutation finished.
                    if (pending.RelayId != intent.RelayId || pending.Revision != (state?.Revision ?? -1) + 1
                        || !request.Certificates.Select(value => value.ToJson()).Order(StringComparer.Ordinal)
                            .SequenceEqual(certificates.Select(value => value.ToJson()).Order(StringComparer.Ordinal)))
                        throw new DeviceStatePublicationException(DeviceStatePublicationFailure.PendingRequestConflict,
                            "A different device-state submission is unresolved. Reconcile it before completing authorization.");
                    certificates = [.. request.Certificates];
                }
                if (certificates.Count > 8)
                    throw new DeviceAuthorizationException(DeviceAuthorizationFailure.SelectionRequired, "The device list is full. Select a device explicitly before retrying.")
                    {
                        DeviceSelection = CreateDeviceSelection(state!, previousLocalDeviceId)
                    };
                if (replacementPresent)
                {
                    var issues = await CheckRemovalContinuityAsync(database, state!, replaceDeviceId!, certificates, cancellationToken).ConfigureAwait(false);
                    if (issues.Count != 0)
                        throw new DeviceAuthorizationException(DeviceAuthorizationFailure.ContinuityUnverifiable, "Replacement lacks confirmed contact-grant continuity. No device was removed.")
                        {
                            ContactIssues = issues,
                            DeviceSelection = CreateDeviceSelection(state!, previousLocalDeviceId)
                        };
                }
                stage = DeviceAuthorizationStage.Publishing;
                try
                {
                    var result = await PublishDeviceStateFromSnapshotAsync(intent.RelayId, certificates, state, cancellationToken,
                        $"{intent.OperationId:N}:{replaceDeviceId}", replacementPresent ? replaceDeviceId : null,
                        classifySubmissionFailure: true).ConfigureAwait(false);
                    // Route activation is an account concern. This is a pending outcome, never success.
                    if (awaitingRoute) return DeviceAuthorizationStatus.AwaitingRoute;
                    if (result.Status != DeviceStatePublishStatus.Accepted)
                        throw new DeviceAuthorizationException(DeviceAuthorizationFailure.NotEffective, "The relay only staged device authorization. It has not become effective.");
                    published = true;
                    // An accepted replay can contain an expired certificate. It
                    // resolves uncertainty, but renewal still precedes success.
                    if (!IsCurrentCertificate(certificate))
                    {
                        if (pending is null)
                            throw new DeviceAuthorizationException(DeviceAuthorizationFailure.NotEffective,
                                "The certificate expired during publication. Retry with the retained device keys.");
                        continue;
                    }
                    break;
                }
                catch (DeviceAuthorizationException exception) when (exception.Failure == DeviceAuthorizationFailure.ContinuityUnverifiable)
                {
                    throw new DeviceAuthorizationException(exception.Failure, exception.Message, exception)
                    {
                        ContactIssues = exception.ContactIssues,
                        DeviceSelection = CreateDeviceSelection(state!, previousLocalDeviceId)
                    };
                }
                catch (Exception exception) when (!awaitingRoute && IsDeviceStateConflict(exception) && conflicts++ < 2)
                {
                    // Re-read and apply the intent, never reuse an old full certificate list.
                }
            }
            stage = DeviceAuthorizationStage.Confirming;
            // Retire old authorization before the confirmation read authenticates a new session.
            if (published)
                await relayClients.InvalidateDeviceAsync().ConfigureAwait(false);
            // Unchanged authorization reuses the fresh read; mutations require post-publication confirmation.
            confirmed ??= await ReadAuthorizationStateAsync(intent.RelayId, cancellationToken).ConfigureAwait(false);
            if (!IsLocalAuthorizationEffective(confirmed)
                || replaceDeviceId is { } removed && confirmed!.Certificates.Any(value => value.GetDeviceId(Context) == removed))
                throw new DeviceAuthorizationException(DeviceAuthorizationFailure.NotEffective, "The relay has not confirmed the requested local authorization and replacement.");
            await relayClients.GetAsync(intent.RelayId, this, cancellationToken).ConfigureAwait(false);
            return DeviceAuthorizationStatus.Authorized;
        }
        catch (Exception exception) when (IsAuthorizationFailure(exception, cancellationToken))
        {
            var failure = AuthorizationFailure(exception);
            if (replacementValidated && failure.Failure == DeviceAuthorizationFailure.PublicationRejected)
                replacementDisposition = DeviceReplacementDisposition.Accepted;
            if (replacementDisposition == DeviceReplacementDisposition.Accepted
                && stage is DeviceAuthorizationStage.Publishing or DeviceAuthorizationStage.Confirming
                && failure.Failure != DeviceAuthorizationFailure.PublicationRejected)
            {
                // Signing failure precedes durable submission; transport/confirmation
                // failure can follow it. Read our execution evidence, not error kinds,
                // before telling the workflow whether the proposal is still unsettled.
                await using var database = new MeshlineDbContext(databaseOptions);
                var operationKey = $"{intent.OperationId:N}:{intent.ReplaceDeviceId}";
                if (await database.SignedRequests.AsNoTracking().AnyAsync(value => value.Method == "device.state.publish"
                    && value.RelayId == intent.RelayId && value.OperationKey == operationKey, cancellationToken).ConfigureAwait(false))
                    replacementDisposition = DeviceReplacementDisposition.Unresolved;
            }
            failure.ReplacementDisposition = replacementDisposition;
            throw failure;
        }
    }

    DeviceSelectionInfo CreateDeviceSelection(AccountDeviceState state, string? previousLocalDeviceId) => new()
    {
        Revision = state.Revision,
        PreviousLocalDeviceId = previousLocalDeviceId,
        Devices = state.Certificates.Select(certificate => new AccountDeviceInfo
        {
            DeviceId = certificate.GetDeviceId(Context),
            Certificate = certificate
        }).ToArray()
    };

    // Recovery and relay migration keep their own publication rules, but use the
    // same device-owned validity and relay-authentication checks as establishment.
    internal async Task ConfirmAuthorizationAsync(string relayId, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        using var mutation = await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        if (Local is null) throw new InvalidOperationException("No local device has been created.");
        var state = await ReadAuthorizationStateAsync(relayId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The home relay returned no device state after account publication.");
        if (!IsLocalAuthorizationEffective(state))
            throw new InvalidDataException("The home relay has not authorized the local device.");
        await relayClients.GetAsync(relayId, this, cancellationToken).ConfigureAwait(false);
    }

    bool IsLocalAuthorizationEffective(AccountDeviceState? state)
    {
        if (state is null || Local is not { } certificate || !IsCurrentCertificate(certificate)) return false;
        var deviceId = certificate.GetDeviceId(Context);
        // A still-valid predecessor does not confirm a locally committed renewal.
        return state.Certificates.FirstOrDefault(value => value.GetDeviceId(Context) == deviceId)?.ToJson() == certificate.ToJson();
    }

    static bool IsCurrentCertificate(DeviceCertificate certificate) =>
        certificate.NotBefore <= Clock.UtcNow.ToUnixTimeSeconds() && certificate.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds();

    // Bypass metadata freshness/cooldown: every merge uses a completed current read.
    async Task<AccountDeviceState?> ReadAuthorizationStateAsync(string relayId, CancellationToken cancellationToken)
    {
        var state = await ResolveDeviceStateCoreAsync(Options.AccountId, null, relayId,
            accountSigner is not null && (Local is null || Local.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()
            || DeviceState?.ValidateDeviceAuthorization(Local.GetDeviceId(Context), Context) is not null), cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            await ValidateRefreshedRevisionAsync(state, relayId, cancellationToken).ConfigureAwait(false);
            CacheDeviceState(state, Clock.UtcNow);
        }
        return state;
    }

    void RequireAuthorizationSigner()
    {
        if (accountSigner is null)
            throw new DeviceAuthorizationException(DeviceAuthorizationFailure.SignerRequired, "An account signer is required to authorize this device.");
    }

    static bool IsDeviceStateConflict(Exception exception) => exception is RelayException { Error.Code: "stale_state" or "state_conflict" }
        or DeviceStatePublicationException { Failure: DeviceStatePublicationFailure.StateChanged };

    static bool IsAuthorizationFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is DeviceAuthorizationException or DeviceStatePublicationException or UnauthorizedAccessException
            or HttpRequestException or TimeoutException or RelayException or InvalidDataException or CryptographicException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    DeviceAuthorizationException AuthorizationFailure(Exception exception)
    {
        if (exception is DeviceAuthorizationException failure)
            return failure;
        var reason = exception switch
        {
            DeviceStatePublicationException { Failure: DeviceStatePublicationFailure.PendingRequestConflict } => DeviceAuthorizationFailure.OperationConflict,
            _ when IsDeviceStateConflict(exception) => DeviceAuthorizationFailure.StateConflict,
            UnauthorizedAccessException => DeviceAuthorizationFailure.SignatureDeclined,
            RelayException { Error.Code: "unauthorized" or "device_unknown" } when accountSigner is null => DeviceAuthorizationFailure.SignerRequired,
            _ => DeviceAuthorizationFailure.StateUnavailable
        };
        return new(reason, "Device authorization did not complete. The original keys and pending publication have been retained.", exception);
    }
}
