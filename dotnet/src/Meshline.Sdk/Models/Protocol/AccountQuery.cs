namespace Meshline.Models.Protocol;

/// <summary>
/// Selects an account by its protocol identifier.
/// </summary>
public sealed record AccountQuery : ProtocolModel
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string Account { get; init; }
}
