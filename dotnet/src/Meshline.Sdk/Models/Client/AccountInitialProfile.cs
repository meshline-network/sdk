using Meshline.Models.Protocol;

namespace Meshline.Models.Client;

/// <summary>Creation-only profile values used by account establishment.</summary>
public sealed class AccountInitialProfile
{
    /// <summary>The initial nickname.</summary>
    public string? Nickname { get; set; }
    /// <summary>The initial biography.</summary>
    public string? Bio { get; set; }
    /// <summary>The initial avatar content reference.</summary>
    public ContentReference? Avatar { get; set; }
    /// <summary>Whether the initial profile permits public address discovery; defaults to false.</summary>
    public bool PublicDiscovery { get; set; }
}
