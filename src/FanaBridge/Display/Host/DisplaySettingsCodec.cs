using System;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Pure persistence boundary for the display settings stored in a device instance's
    /// custom-settings JObject. Legacy blobs are migrated on read without rewriting them.
    /// </summary>
    public static class DisplaySettingsCodec
    {
        public static DisplaySettings Read(JObject source, bool itmCapable)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            string displayMode = (string)source["displayMode"] ?? DisplaySettings.DefaultMode;
            bool itmEnabled = (bool?)source["itmEnabled"] ?? DisplaySettings.DefaultItmEnabled;
            string displayControl = CanonicalControl((string)source["displayControl"]);

            if (displayControl == null)
            {
                if (!itmEnabled && displayMode == DisplaySettings.ModeNone)
                    displayControl = DisplaySettings.ControlOff;
                else if (itmCapable && itmEnabled)
                    displayControl = DisplaySettings.ControlItm;
                else
                    displayControl = DisplaySettings.ControlLegacy;
            }

            // Scalar whitelist: every display settings key this build reads/writes is listed
            // below (and mirrored in Write / WriteDefaults). Add any new key here — a key
            // present only on the JObject (near-miss precedent: legacyModeMigrated) is
            // invisible to the codec until it is.
            return new DisplaySettings
            {
                DisplayControl = displayControl,
                DisplayMode = displayMode,
                ItmEnabled = itmEnabled,
                ItmShowLapTotal = (bool?)source["itmShowLapTotal"] ?? DisplaySettings.DefaultShowLapTotal,
                ItmShowPositionTotal = (bool?)source["itmShowPositionTotal"] ?? DisplaySettings.DefaultShowPositionTotal,
                ItmDefaultPage = (byte?)source["itmDefaultPage"] ?? DisplaySettings.DefaultItmDefaultPage,
                // Absent key = false so fresh / pre-9a blobs still run the mode→world step.
                LegacyModeMigrated = (bool?)source["legacyModeMigrated"] ?? false,
            };
        }

        public static void Write(JObject destination, DisplaySettings settings)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            string displayControl = CanonicalControl(settings.DisplayControl)
                ?? DisplaySettings.ControlItm;
            bool itmEnabled = displayControl == DisplaySettings.ControlItm;

            settings.DisplayControl = displayControl;
            settings.ItmEnabled = itmEnabled;

            // Scalar whitelist (must stay in lockstep with Read): every new settings key
            // must be added here too.
            destination["displayMode"] = settings.DisplayMode;
            destination["displayControl"] = displayControl;
            destination["itmEnabled"] = itmEnabled;
            destination["itmShowLapTotal"] = settings.ItmShowLapTotal;
            destination["itmShowPositionTotal"] = settings.ItmShowPositionTotal;
            destination["itmDefaultPage"] = settings.ItmDefaultPage;
            // Always emit so a save after migration cannot re-synthesize on next load.
            destination["legacyModeMigrated"] = settings.LegacyModeMigrated;
        }

        public static void WriteDefaults(JObject destination, bool itmCapable)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            destination["displayMode"] = DisplaySettings.DefaultMode;
            // Only bake displayControl when the device is ITM-capable (Itm is the fixed
            // point of the migration matrix for default itmEnabled/mode). Non-ITM defaults
            // leave the key absent so resolve-on-read can still promote to Itm if caps later
            // become ITM-capable — writing Legacy here would permanently freeze the control
            // (stored values are honored even when caps change).
            if (itmCapable)
                destination["displayControl"] = DisplaySettings.ControlItm;
            else
                destination.Remove("displayControl");
            // Preserve the old default byte for downgrade safety. Basic wheels have always
            // persisted true here; the value is capability-gated dead weight in old builds.
            destination["itmEnabled"] = DisplaySettings.DefaultItmEnabled;
            destination["itmShowLapTotal"] = DisplaySettings.DefaultShowLapTotal;
            destination["itmShowPositionTotal"] = DisplaySettings.DefaultShowPositionTotal;
            destination["itmDefaultPage"] = DisplaySettings.DefaultItmDefaultPage;
            // Omit: absent = false so LoadDefaultSettings still migrates the default Gear.
            destination.Remove("legacyModeMigrated");
        }

        private static string CanonicalControl(string value)
        {
            if (string.Equals(value, DisplaySettings.ControlItm, StringComparison.OrdinalIgnoreCase))
                return DisplaySettings.ControlItm;
            if (string.Equals(value, DisplaySettings.ControlLegacy, StringComparison.OrdinalIgnoreCase))
                return DisplaySettings.ControlLegacy;
            if (string.Equals(value, DisplaySettings.ControlOff, StringComparison.OrdinalIgnoreCase))
                return DisplaySettings.ControlOff;
            return null;
        }
    }
}
