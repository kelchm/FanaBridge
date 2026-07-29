namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// A rule's live state as shown in the v1 UI priority list.
    /// UI-coupled until E9-exit (DisplayTriggersView / Overview render).
    /// </summary>
    public enum RuleStatus
    {
        /// <summary>Turned off in the config (or degraded at load); never competes.</summary>
        Disabled,
        /// <summary>Targets a page this display does not have; never competes.</summary>
        Unavailable,
        /// <summary>Not eligible in the current session state (in-game vs idle).</summary>
        Ineligible,
        /// <summary>Eligible, condition not currently activating it.</summary>
        Armed,
        /// <summary>Activation live, but a higher-priority rule holds the screen.</summary>
        Waiting,
        /// <summary>The winning rule — its target is the emitted intent.</summary>
        OnScreen,
    }
}
