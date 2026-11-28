using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;

namespace FanaBridge.Core.Devices
{
    /// <summary>
    /// Immutable snapshot of a <see cref="FanatecWheelbase"/>'s published
    /// connection state, committed identity, stability flag, epochs, and
    /// resolved capabilities.
    /// </summary>
    /// <remarks>
    /// Published atomically through a single volatile field on
    /// <see cref="FanatecWheelbase"/> under its state lock so cross-thread
    /// readers (SimHub device threads, settings UI, Control Mapper,
    /// diagnostics) capture one self-consistent state per decision without
    /// taking a lock.
    /// </remarks>
    public sealed class WheelbaseSnapshot
    {
        public static readonly WheelbaseSnapshot Empty = CreateDisconnected(
            connectionEpoch: 0,
            attachmentEpoch: 0,
            wheelChangeCount: 0);

        /// <summary>
        /// Published connection state — what the connection path last decided.
        /// Cross-thread readers use this; live transport health stays on
        /// <see cref="IWheelbaseConnection.IsConnected"/> for heartbeat checks.
        /// </summary>
        public bool Connected { get; }

        /// <summary>The USB product ID of the connected wheelbase, or 0 if not connected.</summary>
        public int ProductId { get; }

        /// <summary>Product name from the HID descriptor of the connected device, or null if not connected.</summary>
        public string ProductName { get; }

        /// <summary>
        /// Monotonic counter that advances on every connect and on every disconnect.
        /// </summary>
        public long ConnectionEpoch { get; }

        /// <summary>
        /// Monotonic counter that advances whenever output ownership must be
        /// re-established: at every connect and disconnect, when an SRM kit is
        /// identified, and at the end of every settle (whether or not the
        /// attachment changed). Never bumped by capability refreshes.
        /// </summary>
        public long AttachmentEpoch { get; }

        /// <summary>Monotonic count of committed identity changes.</summary>
        public int WheelChangeCount { get; }

        /// <summary>Whether the attachment identity is settled (not mid-transition).</summary>
        public bool IdentityStable { get; }

        /// <summary>Raw BaseType byte from the FF 08 report (byte 0x02).</summary>
        public byte BaseType { get; }

        /// <summary>FanaBridge wheelbase code (e.g. "CSDDPlus"), or null if unrecognized.</summary>
        public string BaseCode { get; }

        /// <summary>Whether a wheel or hub is currently attached.</summary>
        public bool WheelDetected { get; }

        /// <summary>
        /// Stable profile-match code for the attached wheel/hub (e.g. "PSWBMW",
        /// "PHUB"), or null when nothing is attached / the wire code is unrecognized.
        /// </summary>
        public string WheelCode { get; }

        /// <summary>
        /// Raw attachment wire code (FF 08 byte 0x18). 0 when nothing is attached.
        /// </summary>
        public byte WheelWireCode { get; }

        /// <summary>Whether the attachment is a hub (accepts a button module).</summary>
        public bool IsHub { get; }

        /// <summary>FanaBridge button-module code ("PBME"/"PBMR"), or null when none.</summary>
        public string ModuleCode { get; }

        /// <summary>Raw module wire byte (FF 08 byte 0x1F). 0 when no module.</summary>
        public byte ModuleWireCode { get; }

        /// <summary>Whether the connected device is an SRM Conversion Kit.</summary>
        public bool IsSrmConverter { get; }

        /// <summary>SRM Conversion Kit firmware (e.g. "6.12") when <see cref="IsSrmConverter"/>, else null.</summary>
        public string SrmKitFirmware { get; }

        /// <summary>Resolved capability profile for this attachment (never null).</summary>
        public WheelCapabilities Capabilities { get; }

        /// <summary>Whether a wheel/hub is identified (attached and recognized).</summary>
        public bool WheelIdentified => WheelDetected && !string.IsNullOrEmpty(WheelCode);

        /// <summary>Whether the base's identity has been committed since connecting.</summary>
        public bool HasIdentity => BaseType != 0 || IsSrmConverter;

        /// <summary>Friendly wheelbase name (e.g. "ClubSport DD+"), or null if unrecognized.</summary>
        public string BaseFriendlyName => FanatecIdentity.FriendlyBase(BaseCode);

        /// <summary>Friendly attached wheel/hub name (e.g. "Podium Hub"), or null if unrecognized.</summary>
        public string AttachmentFriendlyName => FanatecIdentity.FriendlyAttachment(WheelCode);

        /// <summary>Friendly button-module name (e.g. "Button Module Rally"), or null when none/unrecognized.</summary>
        public string ModuleFriendlyName => FanatecIdentity.FriendlyModule(ModuleCode);

        /// <summary>
        /// Display name for the current wheel/hub: the matched profile's name, else
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
                    ModuleCode != null           ? " + " + ModuleCode :
                    IsHub && ModuleWireCode != 0 ? " + Module 0x" + ModuleWireCode.ToString("X2") + " (please report)" :
                                                   "";
                return label + module;
            }
        }

        internal WheelbaseSnapshot(
            bool connected,
            int productId,
            string productName,
            long connectionEpoch,
            long attachmentEpoch,
            int wheelChangeCount,
            bool identityStable,
            byte baseType,
            string baseCode,
            bool wheelDetected,
            string wheelCode,
            byte wheelWireCode,
            bool isHub,
            string moduleCode,
            byte moduleWireCode,
            bool isSrmConverter,
            string srmKitFirmware,
            WheelCapabilities capabilities)
        {
            Connected = connected;
            ProductId = productId;
            ProductName = productName;
            ConnectionEpoch = connectionEpoch;
            AttachmentEpoch = attachmentEpoch;
            WheelChangeCount = wheelChangeCount;
            IdentityStable = identityStable;
            BaseType = baseType;
            BaseCode = baseCode;
            WheelDetected = wheelDetected;
            WheelCode = wheelCode;
            WheelWireCode = wheelWireCode;
            IsHub = isHub;
            ModuleCode = moduleCode;
            ModuleWireCode = moduleWireCode;
            IsSrmConverter = isSrmConverter;
            SrmKitFirmware = srmKitFirmware;
            Capabilities = capabilities ?? WheelCapabilities.None;
        }

        internal static WheelbaseSnapshot CreateDisconnected(
            long connectionEpoch, long attachmentEpoch, int wheelChangeCount) =>
            new WheelbaseSnapshot(
                connected: false,
                productId: 0,
                productName: null,
                connectionEpoch: connectionEpoch,
                attachmentEpoch: attachmentEpoch,
                wheelChangeCount: wheelChangeCount,
                identityStable: true,
                baseType: 0,
                baseCode: null,
                wheelDetected: false,
                wheelCode: null,
                wheelWireCode: 0,
                isHub: false,
                moduleCode: null,
                moduleWireCode: 0,
                isSrmConverter: false,
                srmKitFirmware: null,
                capabilities: WheelCapabilities.None);

        internal static WheelbaseSnapshot CreateConnected(
            long connectionEpoch,
            long attachmentEpoch,
            int wheelChangeCount,
            int productId,
            string productName,
            bool identityStable) =>
            new WheelbaseSnapshot(
                connected: true,
                productId: productId,
                productName: productName,
                connectionEpoch: connectionEpoch,
                attachmentEpoch: attachmentEpoch,
                wheelChangeCount: wheelChangeCount,
                identityStable: identityStable,
                baseType: 0,
                baseCode: null,
                wheelDetected: false,
                wheelCode: null,
                wheelWireCode: 0,
                isHub: false,
                moduleCode: null,
                moduleWireCode: 0,
                isSrmConverter: false,
                srmKitFirmware: null,
                capabilities: WheelCapabilities.None);

        internal WheelbaseSnapshot WithIdentityStable(bool identityStable)
        {
            if (IdentityStable == identityStable)
                return this;

            return new WheelbaseSnapshot(
                Connected,
                ProductId,
                ProductName,
                ConnectionEpoch,
                AttachmentEpoch,
                WheelChangeCount,
                identityStable,
                BaseType,
                BaseCode,
                WheelDetected,
                WheelCode,
                WheelWireCode,
                IsHub,
                ModuleCode,
                ModuleWireCode,
                IsSrmConverter,
                SrmKitFirmware,
                Capabilities);
        }

        internal WheelbaseSnapshot WithSettleEndedUnchanged(WheelCapabilities capabilities) =>
            new WheelbaseSnapshot(
                Connected,
                ProductId,
                ProductName,
                ConnectionEpoch,
                AttachmentEpoch + 1,
                WheelChangeCount,
                identityStable: true,
                BaseType,
                BaseCode,
                WheelDetected,
                WheelCode,
                WheelWireCode,
                IsHub,
                ModuleCode,
                ModuleWireCode,
                IsSrmConverter,
                SrmKitFirmware,
                capabilities);

        internal WheelbaseSnapshot WithCapabilities(WheelCapabilities capabilities) =>
            new WheelbaseSnapshot(
                Connected,
                ProductId,
                ProductName,
                ConnectionEpoch,
                AttachmentEpoch,
                WheelChangeCount,
                IdentityStable,
                BaseType,
                BaseCode,
                WheelDetected,
                WheelCode,
                WheelWireCode,
                IsHub,
                ModuleCode,
                ModuleWireCode,
                IsSrmConverter,
                SrmKitFirmware,
                capabilities);
    }
}
