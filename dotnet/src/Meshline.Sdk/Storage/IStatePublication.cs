namespace Meshline.Storage;

// A shared view over existing publication rows, not an EF entity or another store.
interface IStatePublication
{
    string? IntentJson { get; set; }
    string? RequestJson { get; set; }
    long Generation { get; set; }
    PublicationRequestState RequestState { get; set; }
    bool Pending { get; set; }
    DateTimeOffset? NextAttemptAt { get; set; }
}
