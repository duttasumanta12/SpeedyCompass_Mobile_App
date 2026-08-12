using SpeedyCompass.Shared;
using SpeedyCompass.Shared.Constants;

namespace SpeedyCompass.Engines;

public class StateTransitionEventArgs : EventArgs
{
    public GroupState OldState { get; set; }
    public GroupState NewState { get; set; }
    public string TriggerUser { get; set; }
    public string Reason { get; set; }
    public bool IsForced { get; set; }
}

public class LobbyStateMachine
{
    private GroupState _currentState = GroupState.NotNavigating;
    private readonly object _stateLock = new();

    public GroupState CurrentState => _currentState;

    // Fired only when a valid transition occurs
    public event EventHandler<StateTransitionEventArgs> StateChanged;

    public void Initialize(GroupState initialState)
    {
        _currentState = initialState;
    }

    /// <summary>
    /// Pure logical evaluation of a state transition. Returns false if the transition is blocked.
    /// </summary>
    public bool TryTransition(GroupState newState, string triggerUser = "", string reason = "", bool forceSync = false)
    {
        lock (_stateLock)
        {
            if (!forceSync && _currentState == newState)
                return false;

            // =====================================================================
            // THE ULTIMATE SHIELD: Prevent silent background loops from 
            // destroying an active navigation session.
            // =====================================================================
            if (_currentState >= GroupState.Navigating && newState < GroupState.Navigating)
            {
                if (string.IsNullOrEmpty(triggerUser) && !forceSync)
                {
                    AppLogger.Info("StateMachine", $"Blocked illegal state downgrade to {newState} due to missing human trigger.");
                    return false;
                }
            }

            var oldState = _currentState;
            _currentState = newState;

            StateChanged?.Invoke(this, new StateTransitionEventArgs
            {
                OldState = oldState,
                NewState = newState,
                TriggerUser = triggerUser,
                Reason = reason,
                IsForced = forceSync
            });

            return true;
        }
    }
}