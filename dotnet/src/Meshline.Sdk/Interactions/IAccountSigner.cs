using System.Collections.Immutable;

namespace Meshline.Interactions;

/// <summary>
/// Supplies an account identity and signs protocol input using its account key.
/// </summary>
/// <remarks>
/// The implementation retains ownership of private key material. Sign the supplied protocol input using the account namespace's signing rules; the SDK verifies the resulting signature against the advertised identity.
/// </remarks>
public interface IAccountSigner
{
    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    string AccountId { get; }
    /// <summary>
    /// The account public key corresponding to <see cref="AccountId"/>.
    /// </summary>
    ImmutableArray<byte> PublicKey { get; }

    /// <summary>
    /// Signs the supplied bytes using the account signing key.
    /// </summary>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signature over the supplied input bytes.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
