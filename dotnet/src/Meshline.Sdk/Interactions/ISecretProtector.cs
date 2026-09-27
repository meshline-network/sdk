namespace Meshline.Interactions;

/// <summary>
/// Protects and restores locally stored secrets using an application-defined protection mechanism.
/// </summary>
/// <remarks>
/// Protection applies to selected stored secrets, not to the entire SQLite database. The application must preserve the ability to unprotect persisted data across restarts and enforce the purpose binding in its implementation.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Protects plaintext secret bytes for local storage under the specified purpose.
    /// </summary>
    /// <param name="plaintext">The plaintext content bytes.</param>
    /// <param name="purpose">A stable purpose string binding protection to its intended use; pass the same value when restoring the secret.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The opaque protected representation to persist instead of the plaintext secret.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken = default);
    /// <summary>
    /// Restores plaintext secret bytes previously protected under the same purpose.
    /// </summary>
    /// <param name="protectedData">The opaque bytes produced by the matching protection operation.</param>
    /// <param name="purpose">A stable purpose string binding protection to its intended use; pass the same value when restoring the secret.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The restored plaintext secret bytes; the caller is responsible for their lifetime.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> protectedData, string purpose, CancellationToken cancellationToken = default);
}
