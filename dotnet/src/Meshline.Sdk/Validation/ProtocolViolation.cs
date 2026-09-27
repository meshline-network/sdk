namespace Meshline.Validation;

/// <summary>
/// Describes a protocol validation failure and its optional diagnostic message.
/// </summary>
/// <param name="Kind">The category of protocol validation failure.</param>
/// <param name="Message">An optional diagnostic explanation of the failure.</param>
public sealed record ProtocolViolation(ProtocolViolationKind Kind, string? Message = null);
