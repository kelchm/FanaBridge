using System.Collections.Generic;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// The property picker's on-demand name source — the one thing the Triggers editor
    /// needs to offer a WHEN source, split out of <see cref="IDisplayPanelHost"/> so the
    /// property picker receives only this narrow contract. Fetched when the picker opens,
    /// never per frame (the list can hold thousands of names).
    /// </summary>
    internal interface IDisplayPropertyCatalog
    {
        /// <summary>Every SimHub property name the picker can offer, from
        /// <c>PluginManager.GetAllPropertiesNames()</c>. Defensively wrapped (null plugin
        /// manager or an exception yields an empty list).</summary>
        IReadOnlyList<string> GetAllPropertyNames();
    }
}
