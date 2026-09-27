using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Components;

sealed partial class MessageManager
{
    internal async Task<IReadOnlyList<AccountMessage>> ReadTimelineAsync(long afterSequence, int count, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        await using var database = new MeshlineDbContext(databaseOptions);
        var records = await database.Messages.AsNoTracking().Where(value => value.LocalSequence > afterSequence)
            .OrderBy(value => value.LocalSequence).Take(count).ToListAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<AccountMessage>(records.Count);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await ReadPayloadAsync(record, cancellationToken).ConfigureAwait(false);
            messages.Add(new()
            {
                LocalSequence = record.LocalSequence,
                Sender = record.Sender,
                SenderDeviceId = record.SenderDeviceId,
                Recipient = record.Recipient,
                MessageId = record.MessageId,
                Payload = payload
            });
        }
        return messages;
    }

    async Task<TypedProtocolModel> ReadPayloadAsync(StoredMessageRecord record, CancellationToken cancellationToken)
    {
        if (record.ProtectedPayload is null) return ProtocolModel.FromJson<TypedProtocolModel>(record.PayloadJson!)!;
        var protector = secretProtector ?? throw new InvalidOperationException("A secret protector is required to read the stored group secrets.");
        var plaintext = await protector.UnprotectAsync(record.ProtectedPayload, StoredPayloadPurpose(record), cancellationToken).ConfigureAwait(false);
        try { return ProtocolModel.FromJson<TypedProtocolModel>(Encoding.UTF8.GetString(plaintext))!; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    async Task ProtectPayloadAsync(StoredMessageRecord record, TypedProtocolModel payload, CancellationToken cancellationToken)
    {
        if (!IsSecretPayload(payload)) return;
        var protector = secretProtector ?? throw new InvalidOperationException("A secret protector is required to persist group secret synchronization messages.");
        var plaintext = Encoding.UTF8.GetBytes(payload.ToJson());
        try { record.ProtectedPayload = await protector.ProtectAsync(plaintext, StoredPayloadPurpose(record), cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    string StoredPayloadPurpose(StoredMessageRecord record) => $"Meshline/{Options.Context}/{Options.AccountId}/{deviceManager.Local!.GetDeviceId(Context)}/messages/{record.Sender}/{record.MessageId}";
}
