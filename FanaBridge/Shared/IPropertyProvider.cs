namespace FanaBridge.Shared
{
    /// <summary>
    /// Abstraction for reading SimHub property values.
    /// Implementations wrap PluginManager; tests inject mocks.
    /// </summary>
    public interface IPropertyProvider
    {
        /// <summary>
        /// Returns the current value of a named property, or null if not found.
        /// </summary>
        object GetValue(string propertyName);
    }
}
