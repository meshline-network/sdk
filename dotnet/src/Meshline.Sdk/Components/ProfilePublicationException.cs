namespace Meshline.Components;

// Preserve the submission phase for account initialization, including when it
// adopts pending user edits. Public profile operations unwrap the original error.
sealed class ProfilePublicationException(Exception innerException)
    : Exception("The profile publication outcome is unknown; the signed request has been retained.", innerException)
{
    internal static Exception Unwrap(Exception error)
    {
        if (error is ProfilePublicationException { InnerException: { } cause }) return cause;
        if (error is not AggregateException aggregate) return error;
        var errors = aggregate.InnerExceptions.Select(Unwrap).ToArray();
        return errors.Where((value, index) => !ReferenceEquals(value, aggregate.InnerExceptions[index])).Any()
            ? new AggregateException(errors) : error;
    }
}
