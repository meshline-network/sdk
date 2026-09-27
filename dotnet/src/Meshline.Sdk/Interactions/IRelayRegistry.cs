using Meshline.Models;
using Meshline.Models.Registry;

namespace Meshline.Interactions;

/// <summary>
/// Provides network identity and relay discovery from the application's registry integration.
/// </summary>
/// <remarks>
/// Return entries for the declared network context and preserve their registry status. Relay selection and transport validate whether an entry is usable for the requested operation.
/// </remarks>
public interface IRelayRegistry
{
    /// <summary>
    /// The network reference and registry contract identifying this Meshline network.
    /// </summary>
    NetworkContext Context { get; }

    /// <summary>
    /// Looks up a relay registration by its canonical relay identifier.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The matching registry entry, or <see langword="null"/> if the relay is not registered.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Enumerates the relay registrations available through the registry integration.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>An asynchronous sequence of registry entries; entries may have inactive statuses.</returns>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>.</exception>
    IAsyncEnumerable<RelayEntry> GetRelaysAsync(CancellationToken cancellationToken = default);
}
