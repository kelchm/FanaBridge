using FanaBridge.Display;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Settings
{
    /// <summary>
    /// The FanaBridge-owned settings of one device, as an immutable value.
    /// </summary>
    /// <remarks>
    /// Immutability is what lets a save read a coherent set of values without
    /// holding a lock across serialization, and what stops a cached settings
    /// panel from editing an object the device has already replaced.
    /// </remarks>
    internal sealed class FanatecSettingsSnapshot
    {
        public string DisplayMode { get; private set; }
        public bool ItmEnabled { get; private set; }
        public bool ItmShowLapTotal { get; private set; }
        public bool ItmShowPositionTotal { get; private set; }
        public byte ItmDefaultPage { get; private set; }

        /// <summary>
        /// The wheel's encoder mode, or null when the stored document never had
        /// one. Kept as a string, and absent rather than defaulted, so a value
        /// this build doesn't recognise still round-trips; it is only validated
        /// when a tuning command is actually sent.
        /// </summary>
        public string EncoderMode { get; private set; }

        public static FanatecSettingsSnapshot Defaults() => new FanatecSettingsSnapshot
        {
            DisplayMode = DisplaySettings.DefaultMode,
            ItmEnabled = DisplaySettings.DefaultItmEnabled,
            ItmShowLapTotal = DisplaySettings.DefaultShowLapTotal,
            ItmShowPositionTotal = DisplaySettings.DefaultShowPositionTotal,
            ItmDefaultPage = DisplaySettings.DefaultItmDefaultPage,
            EncoderMode = null,
        };

        /// <summary>
        /// Reads the typed settings out of a stored document. Missing or null
        /// values fall back to defaults, so a partial document still yields a
        /// usable device.
        /// </summary>
        public static FanatecSettingsSnapshot FromJson(JObject source) => new FanatecSettingsSnapshot
        {
            DisplayMode = (string)source["displayMode"] ?? DisplaySettings.DefaultMode,
            ItmEnabled = (bool?)source["itmEnabled"] ?? DisplaySettings.DefaultItmEnabled,
            ItmShowLapTotal = (bool?)source["itmShowLapTotal"] ?? DisplaySettings.DefaultShowLapTotal,
            ItmShowPositionTotal =
                (bool?)source["itmShowPositionTotal"] ?? DisplaySettings.DefaultShowPositionTotal,
            ItmDefaultPage = (byte?)source["itmDefaultPage"] ?? DisplaySettings.DefaultItmDefaultPage,
            EncoderMode = (string)source["encoderMode"],
        };

        /// <summary>Writes these values into a document being persisted.</summary>
        public void WriteTo(JObject target)
        {
            target["displayMode"] = DisplayMode;
            target["itmEnabled"] = ItmEnabled;
            target["itmShowLapTotal"] = ItmShowLapTotal;
            target["itmShowPositionTotal"] = ItmShowPositionTotal;
            target["itmDefaultPage"] = ItmDefaultPage;

            // Absent stays absent: writing a null would turn "never set" into a
            // stored value and change what a later build reads.
            if (EncoderMode != null)
                target["encoderMode"] = EncoderMode;
            else
                target.Remove("encoderMode");
        }

        // Deliberately no ToDisplaySettings(): the live DisplaySettings is
        // never replaced, so it is copied into field by field.

        public FanatecSettingsSnapshot WithDisplay(DisplaySettings display) =>
            new FanatecSettingsSnapshot
            {
                DisplayMode = display.DisplayMode ?? DisplaySettings.DefaultMode,
                ItmEnabled = display.ItmEnabled,
                ItmShowLapTotal = display.ItmShowLapTotal,
                ItmShowPositionTotal = display.ItmShowPositionTotal,
                ItmDefaultPage = display.ItmDefaultPage,
                EncoderMode = EncoderMode,
            };

        public FanatecSettingsSnapshot WithEncoderMode(string encoderMode) =>
            new FanatecSettingsSnapshot
            {
                DisplayMode = DisplayMode,
                ItmEnabled = ItmEnabled,
                ItmShowLapTotal = ItmShowLapTotal,
                ItmShowPositionTotal = ItmShowPositionTotal,
                ItmDefaultPage = ItmDefaultPage,
                EncoderMode = encoderMode,
            };
    }
}
