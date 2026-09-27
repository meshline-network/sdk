namespace Meshline.Components;

/// <summary>
/// Describes a background failure and the operation and resource associated with it.
/// </summary>
public sealed class BackgroundErrorEventArgs : EventArgs
{
    /// <summary>
    /// The background activity that failed.
    /// </summary>
    public BackgroundOperation Operation { get; }
    /// <summary>
    /// The resource identifier associated with the synchronization or error, when available.
    /// </summary>
    public string? Resource { get; }
    /// <summary>
    /// The error associated with this result or diagnostic.
    /// </summary>
    public Exception Error { get; }

    internal BackgroundErrorEventArgs(BackgroundOperation operation, string? resource, Exception error)
    {
        Operation = operation;
        Resource = resource;
        Error = error;
    }
}
