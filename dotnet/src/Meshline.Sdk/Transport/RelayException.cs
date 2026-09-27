using Meshline.Models.Protocol;

namespace Meshline.Transport;

/// <summary>
/// Reports a structured error returned by a relay.
/// </summary>
/// <param name="error">The failure to report.</param>
public sealed class RelayException(RelayError error) : Exception(error.Message)
{
    /// <summary>
    /// The structured relay error that caused this exception.
    /// </summary>
    public RelayError Error { get; } = error;
}
