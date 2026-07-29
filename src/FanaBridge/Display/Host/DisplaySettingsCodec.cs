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

            // The pre-epic scalar reads remain the one-shot v2 bake source. Retired mode
            // values are intentionally read-only: once consumed, this build never emits them.
            return new DisplaySettings
            {
                DisplayControl = displayControl,
                DisplayMode = displayMode,
                ItmEnabled = itmEnabled,
                ItmShowLapTotal = (bool?)source["itmShowLapTotal"] ?? DisplaySettings.DefaultShowLapTotal,
                ItmShowPositionTotal = (bool?)source["itmShowPositionTotal"] ?? DisplaySettings.DefaultShowPositionTotal,
                ItmDefaultPage = (byte?)source["itmDefaultPage"] ?? DisplaySettings.DefaultItmDefaultPage,
            };
        }

        public static void Write(JObject destination, DisplaySettings settings)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            // Persist only live settings. The control/mode/mirror values are pre-epic
            // migration inputs; a post-bake write removes them.
            destination.Remove("displayMode");
            destination.Remove("displayControl");
            destination.Remove("itmEnabled");
            destination["itmShowLapTotal"] = settings.ItmShowLapTotal;
            destination["itmShowPositionTotal"] = settings.ItmShowPositionTotal;
            destination["itmDefaultPage"] = settings.ItmDefaultPage;
        }

        public static void WriteDefaults(JObject destination, bool itmCapable)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            destination.Remove("displayMode");
            destination.Remove("displayControl");
            destination.Remove("itmEnabled");
            destination["itmShowLapTotal"] = DisplaySettings.DefaultShowLapTotal;
            destination["itmShowPositionTotal"] = DisplaySettings.DefaultShowPositionTotal;
            destination["itmDefaultPage"] = DisplaySettings.DefaultItmDefaultPage;
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
