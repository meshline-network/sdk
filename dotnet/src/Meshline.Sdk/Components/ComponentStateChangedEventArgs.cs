namespace Meshline.Components;

/// <summary>
/// Contains the previous and current component lifecycle states.
/// </summary>
public sealed class ComponentStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// The component lifecycle state before the transition.
    /// </summary>
    public ComponentState PreviousState { get; }
    /// <summary>
    /// The lifecycle state after the transition.
    /// </summary>
    public ComponentState CurrentState { get; }

    internal ComponentStateChangedEventArgs(ComponentState previousState, ComponentState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }
}
