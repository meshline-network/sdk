using Meshline.Models.Protocol;
using System.Collections.Immutable;

namespace Meshline.Validation;

static class MessageContentValidator
{
    public static ProtocolViolation? Validate(MessageBody? body, ImmutableArray<ContentReference>? attachments)
    {
        if (body?.Validate() is { } bodyViolation)
            return bodyViolation;
        if (ValidateAttachments(attachments) is { } attachmentsViolation)
            return attachmentsViolation;
        return body is null && (attachments is null || attachments.Value.IsEmpty)
            ? new(ProtocolViolationKind.Format, "A message must contain a body or at least one attachment.")
            : null;
    }

    public static ProtocolViolation? ValidateAttachments(ImmutableArray<ContentReference>? attachments)
    {
        if (attachments is not { } items)
            return null;
        if (items.IsDefault)
            return new(ProtocolViolationKind.Format, "Attachments must be an initialized array when present.");

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in items)
        {
            if (attachment is null)
                return new(ProtocolViolationKind.Format, "Attachments cannot contain null.");
            if (attachment.Validate() is { } violation)
                return violation;
            if (!hashes.Add(attachment.Hash))
                return new(ProtocolViolationKind.Format, "Attachment hashes must be unique within the message.");
        }
        return null;
    }
}
