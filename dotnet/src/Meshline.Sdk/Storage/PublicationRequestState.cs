namespace Meshline.Storage;

// Relates the retained signed document to the latest intent. Generation still
// identifies callers; the numeric generation of an older document is not needed.
enum PublicationRequestState
{
    None = 0,
    Prepared = 1,
    Rejected = 2,
    Superseded = 3,
    SupersededRejected = 4
}

static class PublicationRequestStates
{
    public static bool IsCurrent(this PublicationRequestState state) =>
        state is PublicationRequestState.Prepared or PublicationRequestState.Rejected;

    // Reuse also requires the component to verify that the document matches its
    // latest fields and protocol preconditions, then mark it Prepared atomically.
    public static bool CanReuse(this PublicationRequestState state) =>
        state is PublicationRequestState.Prepared or PublicationRequestState.Superseded;

    public static PublicationRequestState Supersede(this PublicationRequestState state) => state switch
    {
        PublicationRequestState.Prepared => PublicationRequestState.Superseded,
        PublicationRequestState.Rejected => PublicationRequestState.SupersededRejected,
        _ => state
    };
}
