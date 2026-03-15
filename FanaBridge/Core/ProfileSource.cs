namespace FanaBridge.Core
{
    /// <summary>
    /// Indicates whether a profile was shipped with the plugin or created by the user.
    /// </summary>
    public enum ProfileSource
    {
        /// <summary>Embedded in the plugin assembly — immutable.</summary>
        BuiltIn,
        /// <summary>Loaded from the user profile directory on disk.</summary>
        User,
    }
}
