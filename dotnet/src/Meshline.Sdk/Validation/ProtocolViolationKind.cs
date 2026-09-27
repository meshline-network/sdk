namespace Meshline.Validation;

/// <summary>
/// Classifies a protocol validation failure.
/// </summary>
public enum ProtocolViolationKind
{
    /// <summary>
    /// A field, encoding, size, or structure violates protocol constraints.
    /// </summary>
    Format,
    /// <summary>
    /// A cryptographic signature is invalid.
    /// </summary>
    Signature,
    /// <summary>
    /// An identifier does not match the supplied identity evidence.
    /// </summary>
    Identity,
    /// <summary>
    /// A timestamp or validity window violates protocol constraints.
    /// </summary>
    Time,
    /// <summary>
    /// The operation lacks the required authorization.
    /// </summary>
    Authorization,
    /// <summary>
    /// Protocol state conflicts with other supplied state.
    /// </summary>
    Conflict,
    /// <summary>
    /// Required verification evidence is unavailable.
    /// </summary>
    MissingEvidence,
    /// <summary>
    /// The protocol feature or identity namespace is unsupported.
    /// </summary>
    Unsupported
}
