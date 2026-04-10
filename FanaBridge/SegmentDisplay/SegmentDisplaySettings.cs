using System.Collections.ObjectModel;
using FanaBridge.Shared.Conditions;
using Newtonsoft.Json;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Segment display configuration as an ordered layer stack.
    /// Serialized to JSON as part of device instance settings.
    /// </summary>
    public class SegmentDisplaySettings
    {
        /// <summary>
        /// Ordered layer stack. Overlays are evaluated top-to-bottom (first active wins).
        /// Screens cycle in order via wheel buttons.
        /// </summary>
        [JsonProperty("Layers")]
        public ObservableCollection<SegmentDisplayLayer> Layers { get; set; }
            = new ObservableCollection<SegmentDisplayLayer>();

        /// <summary>Creates the default layer stack for a new device.</summary>
        public static SegmentDisplaySettings CreateDefault()
        {
            var settings = new SegmentDisplaySettings();
            settings.Layers.Add(CreateGearChangeOverlay());
            settings.Layers.Add(CreateGearScreen());
            return settings;
        }

        /// <summary>
        /// Migrates a legacy display mode string to the layer model.
        /// The old format stored "Gear", "Speed", or "GearAndSpeed".
        /// </summary>
        public static SegmentDisplaySettings MigrateFromLegacy(string displayMode)
        {
            var settings = new SegmentDisplaySettings();

            switch (displayMode)
            {
                case "Speed":
                    settings.Layers.Add(CreateSpeedScreen());
                    break;

                case "GearAndSpeed":
                    settings.Layers.Add(CreateGearChangeOverlay());
                    settings.Layers.Add(CreateSpeedScreen());
                    break;

                case "Gear":
                default:
                    settings.Layers.Add(CreateGearScreen());
                    break;
            }

            return settings;
        }

        // ── Factory helpers ─────────────────────────────────────────

        private static SegmentDisplayLayer CreateGearScreen()
        {
            return new SegmentDisplayLayer
            {
                Name = "Gear",
                CatalogKey = "Gear",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
                Alignment = AlignmentType.Center,
                Overflow = OverflowType.Auto,
                ShowWhenRunning = true,
                ShowWhenIdle = true,
            };
        }

        private static SegmentDisplayLayer CreateSpeedScreen()
        {
            return new SegmentDisplayLayer
            {
                Name = "Speed",
                CatalogKey = "Speed",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.SpeedLocal",
                    Format = SegmentFormat.Number,
                },
                Alignment = AlignmentType.Right,
                Overflow = OverflowType.Auto,
                ShowWhenRunning = true,
                ShowWhenIdle = false,
            };
        }

        private static SegmentDisplayLayer CreateGearChangeOverlay()
        {
            return new SegmentDisplayLayer
            {
                Name = "Gear Change",
                CatalogKey = "GearChange",
                Role = LayerRole.Overlay,
                Condition = new OnValueChange
                {
                    Property = "DataCorePlugin.GameData.Gear",
                    HoldMs = 2000,
                },
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
                Alignment = AlignmentType.Center,
                Overflow = OverflowType.Auto,
                ShowWhenRunning = true,
                ShowWhenIdle = false,
            };
        }
    }
}
