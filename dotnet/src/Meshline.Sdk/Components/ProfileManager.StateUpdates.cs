using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Meshline.Components;

sealed partial class ProfileManager
{
    ProfileStateUpdates? _updates;
    ProfileStateUpdates Updates => _updates ??= new ProfileStateUpdates(this, databaseOptions);

    /// <inheritdoc/>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Updates.Start(RuntimeCancellationToken);
        return Task.CompletedTask;
    }

    async Task InitializeProfileUpdatesAsync(CancellationToken token)
    {
        _ = Updates;
        await using var database = new MeshlineDbContext(databaseOptions);
        var pending = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "profile.publish" && value.Pending && value.IntentJson == null && value.DocumentJson != null, token).ConfigureAwait(false);
        if (pending is null) return;
        var profile = JsonNode.Parse(pending.DocumentJson!)!.AsObject();
        var patch = new JsonObject();
        foreach (var field in new[] { "nickname", "bio", "avatar", "public_discovery" }) patch[field] = profile[field]?.DeepClone();
        await Updates.ImportAsync(AccountId, patch, token).ConfigureAwait(false);
    }

    async Task<AccountProfile> UpdateLatestProfileAsync(ProfileUpdate update, CancellationToken token)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        ArgumentNullException.ThrowIfNull(update);
        if (update.PublicDiscovery.IsDeleted) throw new ArgumentException("PublicDiscovery cannot be deleted.", nameof(update));
        var patch = new JsonObject();
        StateUpdateJson.Field(patch, "nickname", update.Nickname);
        StateUpdateJson.Field(patch, "bio", update.Bio);
        StateUpdateJson.Field(patch, "avatar", update.Avatar);
        StateUpdateJson.Field(patch, "public_discovery", update.PublicDiscovery);
        // Validate supplied values before replacing a valid durable intent.
        var candidate = ProtocolModel.FromJson<AccountProfile>(StateUpdateJson.Apply(
            new AccountProfile { Account = AccountId, PublicDiscovery = false, UpdatedAt = 0, DeviceSignature = new byte[64].ToImmutableArray() }, patch,
            "nickname", "bio", "avatar", "public_discovery").ToJsonString())!;
        if (candidate.Validate() is { } invalid) throw new ArgumentException(invalid.Message, nameof(update));
        return await Updates.SubmitAsync<AccountProfile>(AccountId, patch, token).ConfigureAwait(false);
    }

    async Task<object?> PublishLatestProfileAsync(StateUpdateAttempt attempt, CancellationToken token)
    {
        var relayId = (await accountManager.GetRouteAsync(cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account route could not be found.")).RelayId;
        var certificate = deviceSigner.Certificate;
        await using var database = new MeshlineDbContext(databaseOptions);
        var saved = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "profile.publish", token).ConfigureAwait(false);
        var local = await database.AccountProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, token).ConfigureAwait(false);
        var basis = (await ResolveProfileAsync(AccountId, token, relayId).ConfigureAwait(false))?.Profile;
        foreach (var json in new[] { attempt.Intent["snapshot"]?.GetValue<string>(), local?.DocumentJson, saved is { Pending: true, DocumentJson: not null, RequestState: PublicationRequestState.Prepared or PublicationRequestState.Superseded } ? saved.DocumentJson : null, attempt.RequestJson })
        {
            if (json is null) continue;
            var candidate = ProtocolModel.FromJson<AccountProfile>(json)!;
            if (basis is null || candidate.UpdatedAt >= basis.UpdatedAt) basis = candidate;
        }
        basis ??= new() { Account = AccountId, PublicDiscovery = false, UpdatedAt = 0, DeviceSignature = [] };
        var request = ProtocolModel.FromJson<AccountProfile>(StateUpdateJson.Apply(basis, attempt.Intent, "nickname", "bio", "avatar", "public_discovery").ToJsonString())!;
        var retainedJson = attempt.RequestJson ?? (saved is { Pending: true, DocumentJson: not null, RequestState: PublicationRequestState.Prepared or PublicationRequestState.Superseded } && saved.RelayId == relayId ? saved.DocumentJson : null);
        var retained = retainedJson is null ? null : ProtocolModel.FromJson<AccountProfile>(retainedJson)!;
        if (retained is not null && request.ToJson() == retained.ToJson()) request = retained;
        else
        {
            request = request with { UpdatedAt = Math.Max(Clock.UtcNow.ToUnixTimeSeconds(), checked(Math.Max(basis.UpdatedAt, saved?.Revision ?? -1) + 1)), DeviceSignature = [] };
            request = request with { DeviceSignature = [.. await deviceSigner.SignAsync(request.GetSigningInput(Context), token).ConfigureAwait(false)] };
            if (request.Validate() is { } invalid) throw new ArgumentException(invalid.Message);
        }
        ValidateCachedProfile(new() { Profile = request, SignerCertificate = certificate }, AccountId);
        await attempt.CurrentAsync(async () =>
        {
            await using var write = new MeshlineDbContext(databaseOptions);
            var row = await write.SignedRequests.SingleAsync(value => value.Method == "profile.publish", token).ConfigureAwait(false);
            if (row.Revision > request.UpdatedAt) throw new InvalidOperationException("Profile version changed while signing.");
            row.DocumentJson = request.ToJson();
            row.RelayId = relayId;
            row.Revision = request.UpdatedAt;
            row.Pending = true;
            row.RequestState = PublicationRequestState.Prepared;
            await write.SaveChangesAsync(token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        var relay = await relayClients.GetAsync(relayId, deviceSigner, token).ConfigureAwait(false);
        await attempt.CheckAsync(token).ConfigureAwait(false);
        try { await relay.SendHttpAsync(HttpMethod.Put, "profile.publish", request, cancellationToken: token).ConfigureAwait(false); }
        catch (RelayException error) when (error.Error.IsDefinitiveRejection())
        {
            if (error.Error.Code != "clock_skew")
                await attempt.CurrentAsync(() => StateUpdateJson.RejectAsync(databaseOptions, "profile.publish", request.ToJson()), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var before = Profile?.ToJson();
        await attempt.CurrentAsync(() => SaveProfileAsync(new() { Profile = request, SignerCertificate = certificate }, token, published: true, notify: false), token).ConfigureAwait(false);
        if (before != Profile?.ToJson()) ProfileChanged?.Invoke(this, EventArgs.Empty);
        return request;
    }

    sealed class ProfileStateUpdates(ProfileManager owner, DatabaseOptions options) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "profile.publish";

        protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) =>
            await database.SignedRequests.FindAsync([Operation], token).ConfigureAwait(false);

        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) =>
            database.SignedRequests.Add(new() { Method = Operation, RelayId = intent["relay"]?.GetValue<string>() ?? "", Revision = -1, RequestState = PublicationRequestState.None }).Entity;

        protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
        {
            var rows = await database.SignedRequests.AsNoTracking().Where(value => value.Method == Operation && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token).ConfigureAwait(false);
            return rows.Select(value => Publication(owner.AccountId, value)).ToList();
        }

        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => owner.PublishLatestProfileAsync(attempt, token);

        protected override void Report(string resource, Exception error) => owner.ReportBackgroundError(BackgroundOperation.Synchronize, resource, error);

        protected override DateTimeOffset? GetNextAttemptAt(IStatePublication publication, Exception error, int failures)
        {
            var next = base.GetNextAttemptAt(publication, error, failures);
            if (next is not null && error is RelayException { Error.Code: "clock_skew" })
            {
                var revision = ((SignedRequestRecord)publication).Revision;
                if (revision is > 0 && revision <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                {
                    var issuedAt = DateTimeOffset.FromUnixTimeSeconds(revision);
                    if (issuedAt > next) next = issuedAt;
                }
            }
            return next;
        }
    }
}
