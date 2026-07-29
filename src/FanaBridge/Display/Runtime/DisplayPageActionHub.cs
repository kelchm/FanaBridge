using System;
using SimHub.Plugins;

namespace FanaBridge.Display.Runtime
{
    /// <summary>
    /// Registers the two plugin-level display walk actions against each SimHub
    /// plugin-manager generation. The plugin instance survives in-process manager
    /// restarts, while the manager's action registry does not.
    /// </summary>
    internal sealed class DisplayPageActionHub
    {
        internal const string NextActionName = "DisplayNextPage";
        internal const string PreviousActionName = "DisplayPreviousPage";

        // Control Mapper persists PluginManager's generated-action dictionary key.
        internal const string NextMappedTarget = "FanatecPlugin." + NextActionName;
        internal const string PreviousMappedTarget = "FanatecPlugin." + PreviousActionName;

        private readonly Action<int> _step;
        private object _registeredWith;

        internal DisplayPageActionHub(Action<int> step)
        {
            _step = step ?? throw new ArgumentNullException(nameof(step));
        }

        internal void EnsureRegistered(PluginManager pluginManager)
        {
            if (pluginManager == null)
                return;

            EnsureRegistered(pluginManager, (name, fire) =>
                pluginManager.AddAction(
                    name,
                    typeof(FanatecPlugin),
                    (manager, inputName) => fire()));
        }

        /// <summary>
        /// Registration core, seam-injected for tests. Re-registers for a new manager
        /// token and is a no-op for repeat calls against the same manager.
        /// </summary>
        internal void EnsureRegistered(
            object registrationToken,
            Action<string, Action> register)
        {
            if (registrationToken == null
                || register == null
                || ReferenceEquals(registrationToken, _registeredWith))
                return;

            _registeredWith = registrationToken;
            register(NextActionName, () => _step(+1));
            register(PreviousActionName, () => _step(-1));
        }
    }
}
