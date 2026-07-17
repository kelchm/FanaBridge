namespace FanaBridge.Adapters
{
    /// <summary>
    /// The mapped-control add flow's on-demand role source, split out of
    /// <see cref="IDisplayPanelHost"/> so the Triggers editor's mapped-control dropdown
    /// receives only this narrow contract. Fetched when the dropdown opens. Read-only:
    /// no Control Mapper writes anywhere.
    /// </summary>
    internal interface IMappedRoleCatalog
    {
        /// <summary>Control Mapper roles for THIS rim. Reads the wheel's own button→role
        /// mappings when present (marked <see cref="MappedRolesSource.MappedOnThisWheel"/>),
        /// else falls back to the sanctioned role catalog
        /// (<see cref="MappedRolesSource.AllRoles"/>), else empty
        /// (<see cref="MappedRolesSource.None"/>) — so the UI can hint "mapped on this wheel"
        /// vs "all roles".</summary>
        MappedRoles GetMappedRoles();
    }
}
