using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class GroupManager
{
    async Task RotateAsync(GroupRef group, bool rotateOwnerKey, CancellationToken cancellationToken)
    {
        await EnsureNoPendingOperationAsync(group, "group.secret.rotation.commit", cancellationToken).ConfigureAwait(false);
        await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
        await using var database = new MeshlineDbContext(databaseOptions);
        var state = (await database.Groups.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false))!;
        if (state.Role != GroupRole.Owner || state.Status != GroupStatus.Active || state.LocallyClosed) throw new UnauthorizedAccessException("Only the current owner of an active group can rotate its client secret.");
        var rotation = await database.GroupRotations.FindAsync([group.GroupId], cancellationToken).ConfigureAwait(false);
        if (rotation is not null && (rotation.BaseCommitment != state.Commitment || rotation.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()))
        {
            database.GroupRotations.Remove(rotation);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            rotation = null;
        }
        if (rotation is not null && (rotation.OwnerPublicKey is not null) != rotateOwnerKey) throw new InvalidOperationException("Retry the original owner-key choice while this rotation is pending.");
        byte[] secret;
        if (rotation is null)
        {
            secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                var commitment = Commitment(group.GroupId, secret);
                rotation = new() { GroupId = group.GroupId, BaseCommitment = state.Commitment!, Commitment = commitment,
                    ProtectedSecret = await secretProtector.ProtectAsync(secret, SecretPurpose(group.GroupId, "rotation", commitment), cancellationToken).ConfigureAwait(false) };
                if (rotateOwnerKey)
                {
                    var privateKey = RandomNumberGenerator.GetBytes(32);
                    try { rotation.OwnerPublicKey = await SaveMemberKeyAsync(database, group.GroupId, privateKey, cancellationToken).ConfigureAwait(false); }
                    finally { CryptographicOperations.ZeroMemory(privateKey); }
                }
                database.GroupRotations.Add(rotation);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch { CryptographicOperations.ZeroMemory(secret); throw; }
        }
        else secret = await secretProtector.UnprotectAsync(rotation.ProtectedSecret, SecretPurpose(group.GroupId, "rotation", rotation.Commitment), cancellationToken).ConfigureAwait(false);
        try
        {
            var relay = await GetRelayAsync(group.RelayId, cancellationToken).ConfigureAwait(false);
            string after = "";
            while (true)
            {
                var members = await database.GroupMembers.AsNoTracking().Where(value => value.GroupId == group.GroupId && string.Compare(value.AccountId, after) > 0).OrderBy(value => value.AccountId).Take(64).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (members.Length == 0) break;
                var boxes = members.ToImmutableDictionary(value => value.AccountId, value => SealClientSecret(group.GroupId, value.AccountId,
                    value.AccountId == Options.AccountId && rotation.OwnerPublicKey is not null ? rotation.OwnerPublicKey : value.PublicKey, rotation.Commitment, secret), StringComparer.Ordinal);
                var result = await relay.SendHttpAsync<GroupRotationPrepareResult>(HttpMethod.Patch, "group.secret.rotation.prepare", new GroupRotationPrepareRequest
                { GroupId = group.GroupId, BaseCommitment = rotation.BaseCommitment, ClientSecretCommitment = rotation.Commitment, ClientSecretBoxes = boxes }, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.Prepared < boxes.Count || rotation.ExpiresAt is { } previous && previous != result.ExpiresAt)
                    throw new InvalidDataException("The relay returned an invalid or changed rotation preparation interval.");
                rotation.ExpiresAt = result.ExpiresAt;
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                after = members[^1].AccountId;
            }
            var request = await SignAsync(new GroupSecretRotation { GroupId = group.GroupId, PrevHash = state.ManagementHash!, ClientSecretCommitment = rotation.Commitment,
                OwnerEncryptionPublicKey = rotation.OwnerPublicKey is { } key ? [.. key] : null, DeviceSignature = [] }, cancellationToken).ConfigureAwait(false);
            await CheckManagementAsync(group, request, cancellationToken).ConfigureAwait(false);
            var operation = NewOperation(group, "group.secret.rotation.commit", request);
            await SaveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            await SubmitOperationAsync(operation, recovering: false, cancellationToken).ConfigureAwait(false);
            await SynchronizeCoreAsync(group, cancellationToken).ConfigureAwait(false);
            await ShareCurrentKeyAsync(group, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
