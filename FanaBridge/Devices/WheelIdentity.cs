namespace FanaBridge.Devices
{
    /// <summary>
    /// An immutable, display-oriented snapshot of the connected base + attached
    /// wheel/hub + module identity, built by <c>FanatecPlugin</c> from the peripheral
    /// view and the resolved capabilities. This is the read-model the settings UI and
    /// the diagnostics report consume — it replaces reaching into the old
    /// <c>FanatecWheelbase</c> singleton, and exposes the same property names so those
    /// consumers read it unchanged.
    /// </summary>
    public sealed class WheelIdentity
    {
        /// <summary>Whether the base's FF 08 identity has been read since connecting.</summary>
        public bool HasIdentity { get; }

        /// <summary>Whether the attachment identity is settled (not mid-transition).</summary>
        public bool IdentityStable { get; }

        /// <summary>Raw BaseType byte (FF 08 offset 0x02).</summary>
        public byte BaseType { get; }

        /// <summary>FanaBridge wheelbase code (e.g. "CSDDPlus"), or null if unrecognized.</summary>
        public string BaseCode { get; }

        /// <summary>Friendly wheelbase name (e.g. "ClubSport DD+"), or null if unrecognized.</summary>
        public string BaseFriendlyName { get; }

        /// <summary>Whether a wheel or hub is currently attached.</summary>
        public bool WheelDetected { get; }

        /// <summary>Profile-match code for the attached wheel/hub, or null.</summary>
        public string WheelCode { get; }

        /// <summary>Raw attachment wire code (FF 08 offset 0x18). 0 when nothing attached.</summary>
        public byte WheelWireCode { get; }

        /// <summary>Whether the attachment is a hub (accepts a button module).</summary>
        public bool IsHub { get; }

        /// <summary>Friendly attached wheel/hub name, or null if unrecognized.</summary>
        public string AttachmentFriendlyName { get; }

        /// <summary>FanaBridge button-module code ("PBME"/"PBMR"), or null when none.</summary>
        public string ModuleCode { get; }

        /// <summary>Raw module wire byte (FF 08 offset 0x1F). 0 when no module.</summary>
        public byte ModuleWireCode { get; }

        /// <summary>Friendly button-module name, or null when none/unrecognized.</summary>
        public string ModuleFriendlyName { get; }

        /// <summary>Combined display name for the attachment (profile name, else code).</summary>
        public string DisplayName { get; }

        /// <summary>The most recent raw FF 08 frame, or null. For the diagnostics capture.</summary>
        public byte[] LastRawReport { get; }

        /// <summary>An empty identity (nothing connected).</summary>
        public static readonly WheelIdentity None = new WheelIdentity();

        private WheelIdentity()
        {
            DisplayName = "No wheel attached";
        }

        public WheelIdentity(
            bool hasIdentity, bool identityStable,
            byte baseType, string baseCode, string baseFriendlyName,
            bool wheelDetected, string wheelCode, byte wheelWireCode, bool isHub, string attachmentFriendlyName,
            string moduleCode, byte moduleWireCode, string moduleFriendlyName,
            string displayName, byte[] lastRawReport)
        {
            HasIdentity = hasIdentity;
            IdentityStable = identityStable;
            BaseType = baseType;
            BaseCode = baseCode;
            BaseFriendlyName = baseFriendlyName;
            WheelDetected = wheelDetected;
            WheelCode = wheelCode;
            WheelWireCode = wheelWireCode;
            IsHub = isHub;
            AttachmentFriendlyName = attachmentFriendlyName;
            ModuleCode = moduleCode;
            ModuleWireCode = moduleWireCode;
            ModuleFriendlyName = moduleFriendlyName;
            DisplayName = displayName;
            LastRawReport = lastRawReport;
        }
    }
}
