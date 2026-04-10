using System.Collections.Generic;
using System.Linq;
using FanaBridge.Shared.Conditions;
using Newtonsoft.Json;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Catalog of predefined segment display layers — screens and overlays.
    /// Used to populate the "Add layer" UI and to build default settings.
    /// </summary>
    public static class SegmentLayerCatalog
    {
        /// <summary>All available catalog entries.</summary>
        public static readonly IReadOnlyList<SegmentDisplayLayer> All = new List<SegmentDisplayLayer>
        {
            // ── Screens ─────────────────────────────────────────────
            new SegmentDisplayLayer
            {
                CatalogKey = "Gear", Name = "Gear",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
                Alignment = AlignmentType.Center,
                ShowWhenRunning = true,
                ShowWhenIdle = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "Speed", Name = "Speed",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.SpeedLocal",
                    Format = SegmentFormat.Number,
                },
                Alignment = AlignmentType.Right,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "Lap", Name = "Current Lap",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.CurrentLap",
                    Format = SegmentFormat.Number,
                },
                Alignment = AlignmentType.Right,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "FuelPct", Name = "Fuel",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.FuelPercent",
                    Format = SegmentFormat.Number,
                },
                Alignment = AlignmentType.Right,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "Position", Name = "Position",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Position",
                    Format = SegmentFormat.Number,
                },
                Alignment = AlignmentType.Right,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "LapTime", Name = "Lap Time",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.CurrentLapTime",
                    Format = SegmentFormat.Time,
                },
                Alignment = AlignmentType.Right,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "BestLap", Name = "Best Lap",
                Role = LayerRole.Screen,
                Condition = new AlwaysActive(),
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.BestLapTime",
                    Format = SegmentFormat.Time,
                },
                Alignment = AlignmentType.Right,
                ShowWhenRunning = true,
            },

            // ── Overlays ────────────────────────────────────────────
            new SegmentDisplayLayer
            {
                CatalogKey = "GearChange", Name = "Gear Change",
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
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "PitLimiter", Name = "Pit Limiter",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.PitLimiterOn",
                },
                Content = new FixedTextContent { Text = "PIT" },
                Alignment = AlignmentType.Center,
                Effects = new SegmentEffect[] { new BlinkEffect { OnMs = 500, OffMs = 500 } },
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "YellowFlag", Name = "Yellow Flag",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.Flag_Yellow",
                },
                Content = new FixedTextContent { Text = "YEL" },
                Alignment = AlignmentType.Center,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "BlueFlag", Name = "Blue Flag",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.Flag_Blue",
                },
                Content = new FixedTextContent { Text = "BLU" },
                Alignment = AlignmentType.Center,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "DRS", Name = "DRS",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.DRSAvailable",
                },
                Content = new FixedTextContent { Text = "DRS" },
                Alignment = AlignmentType.Center,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "LowFuel", Name = "Low Fuel",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.FuelAlertActive",
                },
                Content = new FixedTextContent { Text = "FUL" },
                Alignment = AlignmentType.Center,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "LowFuelAlt", Name = "Low Fuel (Alternating)",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.FuelAlertActive",
                },
                Content = new SequenceContent
                {
                    Items = new ContentSource[]
                    {
                        new FixedTextContent { Text = "LOW" },
                        new FixedTextContent { Text = "FUL" },
                    },
                    IntervalMs = 800,
                },
                Alignment = AlignmentType.Center,
                ShowWhenRunning = true,
            },
            new SegmentDisplayLayer
            {
                CatalogKey = "ShiftWarning", Name = "Shift Warning",
                Role = LayerRole.Overlay,
                Condition = new WhilePropertyTrue
                {
                    Property = "DataCorePlugin.GameData.CarSettings_RPMShiftLight2",
                },
                Content = new PropertyContent
                {
                    PropertyName = "DataCorePlugin.GameData.Gear",
                    Format = SegmentFormat.Gear,
                },
                Alignment = AlignmentType.Center,
                Effects = new SegmentEffect[] { new FlashEffect { Count = 0, RateMs = 150 } },
                ShowWhenRunning = true,
            },
        };

        /// <summary>Creates a deep copy of a catalog entry by key, or null if not found.</summary>
        public static SegmentDisplayLayer CreateFromCatalog(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var template = All.FirstOrDefault(e => e.CatalogKey == key);
            if (template == null) return null;

            // Deep-copy via JSON round-trip to avoid shared object references
            string json = JsonConvert.SerializeObject(template);
            return JsonConvert.DeserializeObject<SegmentDisplayLayer>(json);
        }

        /// <summary>Default layer stack for new devices.</summary>
        public static List<SegmentDisplayLayer> DefaultLayers()
        {
            return new List<SegmentDisplayLayer>
            {
                CreateFromCatalog("PitLimiter"),
                CreateFromCatalog("YellowFlag"),
                CreateFromCatalog("GearChange"),
                CreateFromCatalog("Gear"),
            };
        }
    }
}
