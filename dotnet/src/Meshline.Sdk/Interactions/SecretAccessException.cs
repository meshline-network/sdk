namespace Meshline.Interactions;

/// <summary>A secret provider's diagnosis of a failed local key read.</summary>
public enum SecretAccessFailure
{
    /// <summary>The cause is unknown; the SDK must not replace or delete the key.</summary>
    Unknown,
    /// <summary>Access can return, for example after unlocking the platform keystore.</summary>
    TemporarilyUnavailable,
    /// <summary>The provider has positively established that the original key cannot be recovered. This diagnosis does not authorize automatic replacement or deletion.</summary>
    PermanentlyLost
}

/// <summary>Lets a secret provider distinguish temporary failure from positively established key loss.</summary>
/// <param name="failure">The provider's diagnosis; generic cryptographic failures are not proof of loss.</param>
/// <param name="message">The diagnostic description.</param>
/// <param name="innerException">The underlying platform error.</param>
public sealed class SecretAccessException(SecretAccessFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The provider's diagnosis of this failed read.</summary>
    public SecretAccessFailure Failure { get; } = failure;
}
