namespace Meshline.Components;

// Only account initialization needs a submission outcome classification. Public
// profile operations continue to expose their original transport exceptions.
sealed class ProfilePublicationException(Exception innerException)
    : Exception("The profile publication outcome is unknown; the signed request has been retained.", innerException);
