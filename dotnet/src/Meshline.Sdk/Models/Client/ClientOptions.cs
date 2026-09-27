using Meshline.Identity;

namespace Meshline.Models.Client;

/// <summary>
/// Binds a client or component to one Meshline network and account.
/// </summary>
public sealed class ClientOptions
{
    /// <summary>
    /// The network reference and registry contract identifying this Meshline network.
    /// </summary>
    public required NetworkContext Context { get; init; }
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public required string AccountId { get; init; }

    /// <summary>
    /// Validates the configured network and account identifier.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configured network context is null.</exception>
    /// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
    /// <exception cref="NotSupportedException">The configured account uses an unsupported namespace.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Context);
        if (AccountAdapter.ValidateAccountId(AccountId) is { } violation)
            throw new ArgumentException(violation.Message ?? "Invalid account identifier.", nameof(AccountId));
    }
}
