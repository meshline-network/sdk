namespace Meshline.Components;

enum DeviceStatePublicationFailure
{
    StateChanged,
    PendingRequestConflict
}

sealed class DeviceStatePublicationException(DeviceStatePublicationFailure failure, string message) : InvalidOperationException(message)
{
    public DeviceStatePublicationFailure Failure { get; } = failure;
}
