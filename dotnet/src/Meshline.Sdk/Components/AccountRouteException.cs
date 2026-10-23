namespace Meshline.Components;

/// <summary>A machine-readable failure from ensuring the account's home route.</summary>
public enum AccountRouteFailure
{
    /// <summary>The requested relay differs from the retained route or unfinished publication.</summary>
    RelayMismatch,
    /// <summary>An account signer is needed for publication or account-authenticated replay.</summary>
    SignerRequired,
    /// <summary>The requested lifetime differs from the unfinished publication.</summary>
    OperationConflict,
    /// <summary>The submitted revision conflicts or its expired outcome cannot be reconciled.</summary>
    StateConflict,
    /// <summary>Route discovery, signing or a definitively rejected publication prevented completion.</summary>
    StateUnavailable,
    /// <summary>A submission may have reached the relay and must be reconciled.</summary>
    SubmissionUnknown,
    /// <summary>The account signer declined authorization.</summary>
    SignatureDeclined
}

/// <summary>Structured failure from AccountManager.EnsureRouteAsync.</summary>
/// <param name="failure">The machine-readable failure reason.</param>
/// <param name="message">A diagnostic description.</param>
/// <param name="innerException">The underlying error, when available.</param>
public sealed class AccountRouteException(AccountRouteFailure failure, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>The machine-readable failure reason.</summary>
    public AccountRouteFailure Failure { get; } = failure;
}
