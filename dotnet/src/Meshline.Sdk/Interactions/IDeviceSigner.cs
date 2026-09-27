using Meshline.Models.Protocol;

namespace Meshline.Interactions;

/// <summary>
/// Supplies a device certificate and signs protocol input using its device signing key.
/// </summary>
/// <remarks>
/// The certificate and signing key must describe the same device. Device signatures use Ed25519 over the supplied bytes. The implementation retains ownership of its private key.
/// </remarks>
public interface IDeviceSigner
{
    /// <summary>
    /// The certificate corresponding to the device signing key used by this signer.
    /// </summary>
    DeviceCertificate Certificate { get; }

    /// <summary>
    /// Signs the supplied bytes using the device Ed25519 signing key.
    /// </summary>
    /// <param name="data">The exact bytes to sign or verify.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The signature over the supplied input bytes.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}
