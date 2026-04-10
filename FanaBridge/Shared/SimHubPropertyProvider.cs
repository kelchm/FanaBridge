using SimHub.Plugins;

namespace FanaBridge.Shared
{
    /// <summary>
    /// <see cref="IPropertyProvider"/> backed by SimHub's <see cref="PluginManager"/>.
    /// </summary>
    public class SimHubPropertyProvider : IPropertyProvider
    {
        private readonly PluginManager _pm;

        public SimHubPropertyProvider(PluginManager pm)
        {
            _pm = pm;
        }

        public object GetValue(string propertyName)
        {
            if (_pm == null || string.IsNullOrEmpty(propertyName)) return null;
            try { return _pm.GetPropertyValue(propertyName); }
            catch { return null; }
        }
    }
}
