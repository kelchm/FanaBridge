using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;

namespace FanaBridge.Core.Devices
{
    /// <summary>
    /// One immutable view of what is attached to a wheelbase: the decoded
    /// identity, whether it has settled, and the capabilities resolved for it.
    /// <see cref="FanatecWheelbase"/> publishes a new snapshot as a single
    /// reference, so a reader that takes one snapshot and reads only from it can
    /// never pair one attachment's identity with another's capabilities, or see
    /// an identity half-way through being replaced.
    /// </summary>
    public sealed class AttachmentSnapshot
    {
        /// <summary>Nothing connected or identified yet.</summary>
        internal static readonly AttachmentSnapshot Empty = new AttachmentSnapshot(
            generation: 0, baseType: 0, baseCode: null,
            wheelWireCode: 0, wheelCode: null, isHub: false,
            moduleWireCode: 0, moduleCode: null,
            isSrmConverter: false, srmKitFirmware: null,
            identityStable: true, capabilities: WheelCapabilities.None);

        internal AttachmentSnapshot(
            int generation,
            byte baseType, string baseCode,
            byte wheelWireCode, string wheelCode, bool isHub,
            byte moduleWireCode, string moduleCode,
            bool isSrmConverter, string srmKitFirmware,
            bool identityStable, WheelCapabilities capabilities)
        {
            Generation = generation;
            BaseType = baseType;
            BaseCode = baseCode;
            WheelWireCode = wheelWireCode;
            WheelCode = wheelCode;
            IsHub = isHub;
            ModuleWireCode = moduleWireCode;
            ModuleCode = moduleCode;
            IsSrmConverter = isSrmConverter;
            SrmKitFirmware = srmKitFirmware;
            IdentityStable = identityStable;
            Capabilities = capabilities ?? WheelCapabilities.None;
        }

        /// <summary>
        /// Which attachment this is. Bumped every time the identity is committed
        /// or reset, and kept when only the capabilities or the settling flag
        /// change, so a capability refresh can tell whether the attachment it
        /// resolved for is still the current one.
        /// </summary>
        public int Generation { get; }

        /// <summary>Raw BaseType byte from the FF 08 report (byte 0x02).</summary>
        public byte BaseType { get; }

        /// <summary>FanaBridge wheelbase code (e.g. "CSDDPlus"), or null if unrecognized.</summary>
        public string BaseCode { get; }

        /// <summary>Whether a wheel or hub is attached.</summary>
        public bool WheelDetected => WheelWireCode != 0;

        /// <summary>Raw attachment wire code (FF 08 byte 0x18). 0 when nothing is attached.</summary>
        public byte WheelWireCode { get; }

        /// <summary>
        /// Profile-match code for the attached wheel/hub (e.g. "PSWBMW", "PHUB"),
        /// or null when nothing is attached or the wire code is unrecognized.
        /// </summary>
        public string WheelCode { get; }

        /// <summary>Whether the attachment is a hub (accepts a button module).</summary>
        public bool IsHub { get; }

        /// <summary>Raw module wire byte (FF 08 byte 0x1F). 0 when no module, or not a hub.</summary>
        public byte ModuleWireCode { get; }

        /// <summary>FanaBridge button-module code ("PBME"/"PBMR"), or null when none.</summary>
        public string ModuleCode { get; }

        /// <summary>Whether the identity came from an SRM Conversion Kit's DE FA channel.</summary>
        public bool IsSrmConverter { get; }

        /// <summary>SRM Conversion Kit firmware when <see cref="IsSrmConverter"/>, else null.</summary>
        public string SrmKitFirmware { get; }

        /// <summary>
        /// Whether the identity is settled. False while a changed reading is still
        /// settling; the identity and capabilities are then still the previous
        /// attachment's, and consumers should not drive output.
        /// </summary>
        public bool IdentityStable { get; }

        /// <summary>
        /// The capabilities resolved for this attachment, or
        /// <see cref="WheelCapabilities.None"/> when no profile matches it.
        /// </summary>
        public WheelCapabilities Capabilities { get; }

        /// <summary>Whether a wheel/hub is attached AND recognized.</summary>
        public bool WheelIdentified => WheelDetected && !string.IsNullOrEmpty(WheelCode);

        /// <summary>Whether the base's identity has been read since connecting.</summary>
        public bool HasIdentity => BaseType != 0 || IsSrmConverter;

        /// <summary>
        /// Display name for the attached wheel/hub: the matched profile's name, else
        /// the FanaBridge code (or an EXT_INFO / unknown marker for unmapped bytes).
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!WheelDetected)
                    return "No wheel attached";
                if (!string.IsNullOrEmpty(Capabilities.Name))
                    return Capabilities.Name;
                string label =
                    WheelCode != null     ? WheelCode :
                    WheelWireCode == 0xFF ? "EXT_INFO (extended-identity wheel — please report)" :
                    "Unknown (0x" + WheelWireCode.ToString("X2") + ")";
                string module =
                    ModuleCode != null          ? " + " + ModuleCode :
                    IsHub && ModuleWireCode != 0 ? " + Module 0x" + ModuleWireCode.ToString("X2") + " (please report)" :
                                                   "";
                return label + module;
            }
        }

        /// <summary>Friendly wheelbase name (e.g. "ClubSport DD+"), or null if unrecognized.</summary>
        public string BaseFriendlyName => FanatecIdentity.FriendlyBase(BaseCode);

        /// <summary>Friendly attached wheel/hub name (e.g. "Podium Hub"), or null if unrecognized.</summary>
        public string AttachmentFriendlyName => FanatecIdentity.FriendlyAttachment(WheelCode);

        /// <summary>Friendly button-module name, or null when none/unrecognized.</summary>
        public string ModuleFriendlyName => FanatecIdentity.FriendlyModule(ModuleCode);

        /// <summary>This identity as a new attachment generation, with its capabilities.</summary>
        internal AttachmentSnapshot AsGeneration(int generation, WheelCapabilities capabilities) =>
            new AttachmentSnapshot(generation, BaseType, BaseCode, WheelWireCode, WheelCode, IsHub,
                ModuleWireCode, ModuleCode, IsSrmConverter, SrmKitFirmware, IdentityStable, capabilities);

        /// <summary>The same attachment with re-resolved capabilities.</summary>
        internal AttachmentSnapshot WithCapabilities(WheelCapabilities capabilities) =>
            AsGeneration(Generation, capabilities);

        /// <summary>The same attachment, settling or settled.</summary>
        internal AttachmentSnapshot WithIdentityStable(bool identityStable) =>
            new AttachmentSnapshot(Generation, BaseType, BaseCode, WheelWireCode, WheelCode, IsHub,
                ModuleWireCode, ModuleCode, IsSrmConverter, SrmKitFirmware, identityStable, Capabilities);
    }
}
