using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Meshline.Validation;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline;

sealed partial class MeshlineClient
{
    static AccountEstablishmentOptions SnapshotEstablishmentOptions(AccountEstablishmentOptions? options)
    {
        if (options is null) return new();
        return new()
        {
            RelayId = options.RelayId,
            CertificateValidity = options.CertificateValidity,
            PushGateway = options.PushGateway,
            RouteValidity = options.RouteValidity,
            ReplaceDeviceId = options.ReplaceDeviceId,
            InitialProfile = options.InitialProfile is { } profile ? new()
            {
                Nickname = profile.Nickname,
                Bio = profile.Bio,
                Avatar = profile.Avatar,
                PublicDiscovery = profile.PublicDiscovery
            } : null
        };
    }

    void ValidateEstablishmentOptions(AccountEstablishmentOptions options)
    {
        if (options.PushGateway is not null && PushGatewayValidator.Validate(options.PushGateway) is { } gatewayViolation)
            throw new ArgumentException(gatewayViolation.Message, nameof(options));
        if (options.CertificateValidity < TimeSpan.FromSeconds(1) || options.CertificateValidity > TimeSpan.FromDays(720))
            throw new ArgumentOutOfRangeException(nameof(options), "Certificate validity must be between one second and 720 days.");
        if (options.RouteValidity < TimeSpan.FromSeconds(1) || options.RouteValidity > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(options), "Route validity must be between one second and 3650 days.");
        if (options.ReplaceDeviceId is { } replacement && Identifiers.ValidateDeviceId(replacement) is { } violation)
            throw new ArgumentException(violation.Message, nameof(options));
        if (options.InitialProfile is { } initial)
        {
            var profile = new AccountProfile
            {
                Account = Options.AccountId,
                Nickname = initial.Nickname,
                Bio = initial.Bio,
                Avatar = initial.Avatar,
                PublicDiscovery = initial.PublicDiscovery,
                UpdatedAt = 0,
                DeviceSignature = [.. new byte[64]]
            };
            if (profile.Validate(Context) is { } invalid)
                throw new ArgumentException(invalid.Message, nameof(options));
        }
    }

    async Task EstablishAccountCoreAsync(AccountEstablishmentOptions options, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(_databaseOptions);
        var setup = await database.AccountEstablishments.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        DeviceAuthorizationIntent? DeviceIntent() => setup is null ? null : new(setup.OperationId, setup.RelayId,
            TimeSpan.FromSeconds(setup.CertificateValiditySeconds), setup.ReplaceDeviceId, setup.PendingReplaceDeviceId,
            setup.PreviousLocalDeviceId, setup.InitialPushGateway);
        try
        {
            if (await database.HomeRelayMigrations.AnyAsync(cancellationToken).ConfigureAwait(false))
                throw new AccountEstablishmentException(AccountEstablishmentReason.OperationParametersConflict, "Complete the explicit home-relay migration before establishing this device.");
            var inspection = await DeviceManager.InspectAuthorizationAsync(null, cancellationToken,
                intent: DeviceIntent()).ConfigureAwait(false);
            // Discovery is read-only; publication replay belongs to the saved EnsuringRoute step.
            var route = await AccountManager.RefreshRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            // An unfinished standalone request constrains setup only when no usable route exists.
            var unconfirmedRoute = setup is null && route is null
                ? await AccountManager.GetUnconfirmedRouteAsync(cancellationToken).ConfigureAwait(false) : null;
            var originalRelay = Route?.RelayId ?? setup?.RelayId ?? unconfirmedRoute?.RelayId;
            if (originalRelay is not null && options.RelayId is not null && options.RelayId != originalRelay
                || setup is not null && originalRelay != setup.RelayId
                || unconfirmedRoute is not null && unconfirmedRoute.RelayId != originalRelay)
                throw new AccountEstablishmentException(AccountEstablishmentReason.RelayMismatch, "Account setup retains the existing home relay. Use explicit migration to change it.");
            if (unconfirmedRoute is not null && unconfirmedRoute.ExpiresAt - unconfirmedRoute.UpdatedAt
                != (long)options.RouteValidity.TotalSeconds)
                throw new AccountEstablishmentException(AccountEstablishmentReason.OperationParametersConflict,
                    "Retry the unfinished route publication with its original validity.");
            var relayId = originalRelay ?? await SelectRelayAsync(options.RelayId, cancellationToken).ConfigureAwait(false);
            if (route is not null)
            {
                inspection = await DeviceManager.InspectAuthorizationAsync(relayId, cancellationToken, inspection,
                    DeviceIntent()).ConfigureAwait(false);
            }
            if (route is not null && setup is null && options.ReplaceDeviceId is null && inspection.IsAuthorized)
            {
                EnsureAuthorizationEffective(relayId);
                return;
            }
            if (_runtimeActive || LifecycleState != ComponentState.Stopped)
                throw new InvalidOperationException("Stop background synchronization before changing account authorization.");
            // Existing device history needs original-route evidence unless this initial workflow created it.
            if (route is null && Route is null && (setup is { IsInitialState: false } || setup is null && DeviceState is not null))
                throw new AccountEstablishmentException(AccountEstablishmentReason.AccountStateUnavailable, "Existing account history has no original route. It cannot safely be reconstructed from a not_found response.");
            if (setup is null)
            {
                setup = new AccountEstablishmentRecord
                {
                    OperationId = Guid.NewGuid(),
                    RelayId = relayId,
                    CertificateValiditySeconds = (long)options.CertificateValidity.TotalSeconds,
                    RouteValiditySeconds = (long)options.RouteValidity.TotalSeconds,
                    InitialProfileJson = options.InitialProfile is null ? null : JsonSerializer.Serialize(options.InitialProfile),
                    InitialPushGateway = options.PushGateway,
                    IsInitialState = route is null && Route is null,
                    ReplaceDeviceId = options.ReplaceDeviceId,
                    PreviousLocalDeviceId = inspection.PreviousLocalDeviceId
                };
                database.AccountEstablishments.Add(setup);
            }
            else if (options.InitialProfile is not null && JsonSerializer.Serialize(options.InitialProfile) != setup.InitialProfileJson)
                throw new AccountEstablishmentException(AccountEstablishmentReason.OperationParametersConflict, "The initial profile is already fixed by the unfinished operation.");
            else if (options.PushGateway is not null && options.PushGateway != setup.InitialPushGateway)
                throw new AccountEstablishmentException(AccountEstablishmentReason.OperationParametersConflict, "The initial push gateway is already fixed by the unfinished operation.");

            var replacement = options.ReplaceDeviceId ?? setup.PendingReplaceDeviceId ?? setup.ReplaceDeviceId;
            if (replacement != (setup.PendingReplaceDeviceId ?? setup.ReplaceDeviceId))
            {
                // Effective or unresolved replacements freeze the choice; later revocation permits a new one.
                if (setup.DeviceAuthorizationConfirmed && inspection.IsAuthorized
                    || setup.PendingReplaceDeviceId is not null)
                    throw new AccountEstablishmentException(AccountEstablishmentReason.OperationParametersConflict,
                        "Complete the current device authorization before changing its replacement.");
                // Preserve the prior intent until the device owner accepts this proposal.
                setup.PendingReplaceDeviceId = replacement;
            }

            async Task SaveStepAsync(AccountEstablishmentStep step)
            {
                setup.Step = step;
                if (database.ChangeTracker.HasChanges())
                    await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            void AcceptReplacement()
            {
                if (setup.PendingReplaceDeviceId is not { } selected) return;
                setup.ReplaceDeviceId = selected;
                setup.PendingReplaceDeviceId = null;
            }

            async Task<DeviceAuthorizationStatus> AuthorizeDeviceAsync()
            {
                for (var attempt = 0; ; attempt++)
                {
                    setup.PreviousLocalDeviceId = inspection.PreviousLocalDeviceId;
                    await SaveStepAsync(AccountEstablishmentStep.AuthorizingDevice).ConfigureAwait(false);
                    try
                    {
                        return await DeviceManager.AuthorizeAsync(DeviceIntent()!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DeviceAuthorizationException exception)
                    {
                        if (exception.ReplacementDisposition == DeviceReplacementDisposition.Accepted)
                            AcceptReplacement();
                        else if (exception.ReplacementDisposition == DeviceReplacementDisposition.Rejected)
                            setup.PendingReplaceDeviceId = null;
                        await SaveStepAsync(AccountEstablishmentStep.AuthorizingDevice).ConfigureAwait(false);
                        if (exception.ReplacementDisposition == DeviceReplacementDisposition.Rejected
                            && options.ReplaceDeviceId is null && attempt < 2) continue;
                        throw;
                    }
                }
            }

            // Repair expired prerequisites before resuming. Bootstrap staging may need
            // replay, but confirmed device state requires route reconciliation first.
            var nextStep = setup.Step;
            if (route is null)
                nextStep = Route is not null || DeviceState is not null ? AccountEstablishmentStep.EnsuringRoute : AccountEstablishmentStep.AuthorizingDevice;
            else if (nextStep == AccountEstablishmentStep.InitializingProfile && !inspection.IsAuthorized)
                nextStep = AccountEstablishmentStep.AuthorizingDevice;
            await SaveStepAsync(nextStep).ConfigureAwait(false);
            while (true)
            {
                switch (setup.Step)
                {
                    case AccountEstablishmentStep.EnsuringRoute:
                        await AccountManager.EnsureRouteAsync(relayId, TimeSpan.FromSeconds(setup.RouteValiditySeconds),
                            cancellationToken).ConfigureAwait(false);
                        // Route confirmation must be followed by current device authorization.
                        await SaveStepAsync(AccountEstablishmentStep.AuthorizingDevice).ConfigureAwait(false);
                        break;
                    case AccountEstablishmentStep.AuthorizingDevice:
                        var authorization = await AuthorizeDeviceAsync().ConfigureAwait(false);
                        if (authorization == DeviceAuthorizationStatus.AwaitingRoute)
                        {
                            await SaveStepAsync(AccountEstablishmentStep.EnsuringRoute).ConfigureAwait(false);
                            break;
                        }
                        AcceptReplacement();
                        setup.DeviceAuthorizationConfirmed = true;
                        await SaveStepAsync(AccountEstablishmentStep.InitializingProfile).ConfigureAwait(false);
                        break;
                    case AccountEstablishmentStep.InitializingProfile:
                        await CompleteEstablishmentAsync(database, setup, cancellationToken).ConfigureAwait(false);
                        return;
                    default:
                        throw new InvalidDataException("The account establishment step is invalid.");
                }
            }
        }
        catch (DeviceAuthorizationException exception)
        {
            throw new AccountEstablishmentException(exception.Failure switch
            {
                DeviceAuthorizationFailure.SignerRequired => AccountEstablishmentReason.AccountSignerRequired,
                DeviceAuthorizationFailure.SignatureDeclined => AccountEstablishmentReason.SignatureDeclined,
                DeviceAuthorizationFailure.SelectionRequired => AccountEstablishmentReason.DeviceSelectionRequired,
                DeviceAuthorizationFailure.KeyAccessRequired => AccountEstablishmentReason.LocalKeyAccessRequired,
                DeviceAuthorizationFailure.StateUnavailable or DeviceAuthorizationFailure.PublicationRejected => AccountEstablishmentReason.AccountStateUnavailable,
                DeviceAuthorizationFailure.ContinuityUnverifiable => AccountEstablishmentReason.ContactContinuityUnverifiable,
                DeviceAuthorizationFailure.SubmissionUnknown => AccountEstablishmentReason.SubmissionOutcomeUnknown,
                DeviceAuthorizationFailure.NotEffective => AccountEstablishmentReason.AuthorizationNotYetEffective,
                DeviceAuthorizationFailure.StateConflict => AccountEstablishmentReason.StateConflict,
                DeviceAuthorizationFailure.OperationConflict or DeviceAuthorizationFailure.ReplacementConflict => AccountEstablishmentReason.OperationParametersConflict,
                _ => throw new InvalidOperationException("Unknown device authorization failure.", exception)
            }, exception.Message, exception.InnerException ?? exception)
            {
                KeyIssue = exception.KeyIssue,
                ContactIssues = exception.ContactIssues,
                DeviceSelection = exception.DeviceSelection
            };
        }
        catch (AccountRouteException exception)
        {
            var reason = exception.Failure switch
            {
                AccountRouteFailure.RelayMismatch => AccountEstablishmentReason.RelayMismatch,
                AccountRouteFailure.SignerRequired => AccountEstablishmentReason.AccountSignerRequired,
                AccountRouteFailure.OperationConflict => AccountEstablishmentReason.OperationParametersConflict,
                AccountRouteFailure.StateConflict => AccountEstablishmentReason.StateConflict,
                AccountRouteFailure.StateUnavailable => AccountEstablishmentReason.AccountStateUnavailable,
                AccountRouteFailure.SubmissionUnknown => AccountEstablishmentReason.SubmissionOutcomeUnknown,
                AccountRouteFailure.SignatureDeclined => AccountEstablishmentReason.SignatureDeclined,
                _ => throw new InvalidOperationException("Unknown route authorization failure.", exception)
            };
            throw new AccountEstablishmentException(reason, exception.Message, exception.InnerException ?? exception);
        }
        catch (ProfilePublicationException exception)
        {
            throw new AccountEstablishmentException(AccountEstablishmentReason.SubmissionOutcomeUnknown, exception.Message, exception.InnerException);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AccountEstablishmentException(AccountEstablishmentReason.SignatureDeclined, "Account authorization was declined.", exception);
        }
        catch (RelayException exception) when (_accountSigner is null && exception.Error.Code is "unauthorized" or "device_unknown")
        {
            throw new AccountEstablishmentException(AccountEstablishmentReason.AccountSignerRequired, "The relay requires account authorization to inspect this device's state.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or RelayException or InvalidDataException or CryptographicException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var reason = exception is RelayException { Error.Code: "stale_state" or "state_conflict" } ? AccountEstablishmentReason.StateConflict
                : AccountEstablishmentReason.AccountStateUnavailable;
            throw new AccountEstablishmentException(reason, "Account setup did not complete. The original state and pending operation have been retained.", exception);
        }
    }

    async Task CompleteEstablishmentAsync(MeshlineDbContext database, AccountEstablishmentRecord setup,
        CancellationToken cancellationToken)
    {
        var confirmedRoute = await AccountManager.RefreshRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (confirmedRoute is null || confirmedRoute.RelayId != setup.RelayId)
            throw new AccountEstablishmentException(AccountEstablishmentReason.AuthorizationNotYetEffective, "The original home route is not confirmed.");
        var initial = setup.InitialProfileJson is null ? new AccountInitialProfile() : JsonSerializer.Deserialize<AccountInitialProfile>(setup.InitialProfileJson)!;
        await ProfileManager.InitializeAccountProfileAsync(initial, setup.IsInitialState, cancellationToken).ConfigureAwait(false);
        EnsureAuthorizationEffective(setup.RelayId);

        // Database waits can outlive authorization. Roll back cleanup if it expires
        // before commit, or restore the same workflow if commit has already completed.
        await using (var completion = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            database.AccountEstablishments.Remove(setup);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            EnsureAuthorizationEffective(setup.RelayId);
            await completion.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        try { EnsureAuthorizationEffective(setup.RelayId); }
        catch (AccountEstablishmentException)
        {
            database.AccountEstablishments.Add(setup);
            await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    void EnsureAuthorizationEffective(string relayId)
    {
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        if (Route is not { } currentRoute || currentRoute.RelayId != relayId || currentRoute.ExpiresAt <= now
            || Device is not { } certificate || certificate.NotBefore > now || certificate.ExpiresAt <= now
            || DeviceState is not { } state || state.ValidateDeviceAuthorization(certificate.GetDeviceId(Context), Context) is not null)
            throw new AccountEstablishmentException(AccountEstablishmentReason.AuthorizationNotYetEffective,
                "Account authorization is no longer effective. Retry establishment to reconcile the invalid prerequisites.");
    }
}
