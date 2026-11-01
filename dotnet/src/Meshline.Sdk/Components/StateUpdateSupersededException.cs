namespace Meshline.Components;

/// <summary>A newer state update has replaced a pending publication.</summary>
/// <remarks>The superseded call fails without waiting for the earlier request. Remote acceptance is not undone.</remarks>
public sealed class StateUpdateSupersededException : InvalidOperationException
{
    /// <summary>Creates an exception identifying the resource whose publication was superseded.</summary>
    /// <param name="resource">The SDK resource identifier.</param>
    public StateUpdateSupersededException(string resource) : base("A newer state update superseded this publication.") => Resource = resource;

    /// <summary>The operation and resource whose publication was superseded, such as <c>profile.publish:&lt;account&gt;</c>.</summary>
    public string Resource { get; }
}
