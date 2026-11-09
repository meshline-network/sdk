using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using System.Text.Json;

namespace Meshline.Backups;

internal static class BackupData
{
    internal static JsonElement Record(string type, object data) =>
        JsonSerializer.SerializeToElement(new
        {
            type,
            data
        }, BackupCodec.Json);

    internal static (string Type, JsonElement Data) ReadRecord(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object
            || !record.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(type.GetString())
            || !record.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A backup record requires a nonempty string type and an object data field.");
        RequireFields(record, "type data");
        return (type.GetString()!, data);
    }

    internal static (string Type, JsonElement Data) ValidateRecord(JsonElement record, params string[] types)
    {
        var frame = ReadRecord(record);
        if (!types.Contains(frame.Type, StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported component backup record: " + frame.Type);
        BackupCodec.ValidateRecord(record);
        return frame;
    }

    internal static T Protocol<T>(JsonElement value) where T : ProtocolModel => ProtocolModel.FromJson<T>(value.GetRawText()) ?? throw new InvalidDataException("Missing protocol value.");
    internal static void Validate(ProtocolModel value, Meshline.Models.NetworkContext context) => Check(value.Validate(context));
    internal static void Check(Meshline.Validation.ProtocolViolation? violation)
    {
        if (violation is not null)
            throw new InvalidDataException(violation.Message);
    }
    internal static void RequireFields(JsonElement data, string fields)
    {
        var allowed = fields.Split(' ').ToHashSet(StringComparer.Ordinal);
        foreach (var field in data.EnumerateObject())
            if (!allowed.Contains(field.Name))
                throw new InvalidDataException("Unsupported backup field: " + field.Name);
    }
    internal static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    internal static JsonElement Project<T>(T value, string fields)
    {
        var source = JsonSerializer.SerializeToElement(value, BackupCodec.Json);
        return JsonSerializer.SerializeToElement(fields.Split(' ').ToDictionary(name => name, name => source.GetProperty(name)), BackupCodec.Json);
    }
    internal static T Read<T>(JsonElement value) => value.Deserialize<T>(BackupCodec.Json) ?? throw new InvalidDataException("Missing backup record.");
    internal const string EventFields = "group_id sequence payload_json certificate_json epoch message_id sender sender_device_id created_at decrypted_payload_json rejection is_message";
    internal static bool ValidSelection(BackupSelection? selection, bool group, CancellationToken token)
    {
        if (selection is null)
            return false;
        foreach (var id in selection.Ids)
        {
            token.ThrowIfCancellationRequested();
            if (id is null)
                return false;
            try
            {
                if ((group ? Identifiers.ValidateGroupId(id) : AccountAdapter.ValidateAccountId(id)) is not null)
                    return false;
            }
            catch (NotSupportedException) { return false; }
        }
        return true;
    }

    internal static BackupSelection ReadSelection(SelectionData? data, bool group, CancellationToken token)
    {
        if (data?.Ids is null || data.Mode is not ("all" or "none" or "only") || data.Mode != "only" && data.Ids.Length != 0)
            throw new InvalidDataException("Invalid backup history selection.");
        var selection = data.Mode switch
        {
            "all" => BackupSelection.All,
            "none" => BackupSelection.None,
            _ => BackupSelection.Only(data.Ids)
        };
        if (!ValidSelection(selection, group, token))
            throw new InvalidDataException("Invalid backup history selection identifiers.");
        return selection;
    }

    internal sealed record SelectionData(string Mode, string[] Ids);
    internal sealed record Manifest(string Network, string Account, long CreatedAt, SelectionData DirectHistory, SelectionData GroupHistory);
    internal sealed record End(long Records);
    internal sealed record MessageData(string Sender, string MessageId, string SenderDeviceId, string Recipient, long CreatedAt, JsonElement Payload);
    internal sealed record ChannelData(string ChannelId, string RelayId, bool IsFollowed);
    internal sealed record GroupData(string GroupId, string RelayId, bool LocallyClosed);
    internal sealed record NicknameData(string GroupId, string AccountId, long JoinedAtSequence, long Sequence, string? Nickname);
    internal sealed record EpochData(string GroupId, long Epoch, string? Commitment = null, byte[]? MemberPublicKey = null,
        byte[]? ClientSecret = null, byte[]? ApplicationSecret = null);
    internal sealed record SecretData(string GroupId, string Kind, string Id, byte[] Secret);
}
