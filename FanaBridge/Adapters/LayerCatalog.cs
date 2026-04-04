using System.Collections.Generic;
using System.Linq;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Unified catalog of curated display layers — both base data sources
    /// and conditional overlays. Used to populate the "Add layer" dropdown.
    /// </summary>
    public static class LayerCatalog
    {
        public static readonly IReadOnlyList<DisplayLayer> All = new List<DisplayLayer>
        {
            // ── Base data sources (Constant) ─────────────────────────
            new DisplayLayer
            {
                CatalogKey = "Gear", Name = "Gear",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.Gear",
                Format = "gear", CenterDisplay = true,
            },
            new DisplayLayer
            {
                CatalogKey = "SpeedKmh", Name = "Speed (km/h)",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.SpeedKmh",
                Format = "{0:0}",
            },
            new DisplayLayer
            {
                CatalogKey = "SpeedMph", Name = "Speed (mph)",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.SpeedMph",
                Format = "{0:0}",
            },
            new DisplayLayer
            {
                CatalogKey = "Lap", Name = "Current Lap",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.CurrentLap",
                Format = "{0:0}",
            },
            new DisplayLayer
            {
                CatalogKey = "FuelPct", Name = "Fuel %",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.FuelPercent",
                Format = "{0:0}",
            },
            new DisplayLayer
            {
                CatalogKey = "Position", Name = "Position",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.Position",
                Format = "{0:0}",
            },
            new DisplayLayer
            {
                CatalogKey = "LapTime", Name = "Lap Time",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.CurrentLapTime",
                Format = "ss\\.f",
            },
            new DisplayLayer
            {
                CatalogKey = "BestLap", Name = "Best Lap",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.NewData.BestLapTime",
                Format = "ss\\.f",
            },

            // ── Conditional overlays ─────────────────────────────────
            new DisplayLayer
            {
                CatalogKey = "GearChange", Name = "Gear change",
                Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.Property,
                WatchProperty = "DataCorePlugin.GameData.NewData.Gear",
                PropertyName = "DataCorePlugin.GameData.NewData.Gear",
                Format = "gear", CenterDisplay = true,
                DurationMs = 2000,
            },
            new DisplayLayer
            {
                CatalogKey = "PitLimiter", Name = "Pit limiter",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.NewData.PitLimiterOn",
                FixedText = "PIT",
            },
            new DisplayLayer
            {
                CatalogKey = "YellowFlag", Name = "Yellow flag",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.NewData.Flag_Yellow",
                FixedText = "YEL",
            },
            new DisplayLayer
            {
                CatalogKey = "BlueFlag", Name = "Blue flag",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.NewData.Flag_Blue",
                FixedText = "BLU",
            },
            new DisplayLayer
            {
                CatalogKey = "DRS", Name = "DRS available",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.NewData.DRSAvailable",
                FixedText = "DRS",
            },
            new DisplayLayer
            {
                CatalogKey = "LowFuel", Name = "Low fuel warning",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.NewData.FuelAlertActive",
                FixedText = "FUL",
            },
        };

        /// <summary>Finds a catalog entry by key, or null.</summary>
        public static DisplayLayer FindByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            return All.FirstOrDefault(e => e.CatalogKey == key);
        }

        /// <summary>Creates a deep copy of a catalog entry.</summary>
        public static DisplayLayer CreateFromCatalog(string key)
        {
            var t = FindByKey(key);
            if (t == null) return null;

            return new DisplayLayer
            {
                CatalogKey = t.CatalogKey,
                Name = t.Name,
                Mode = t.Mode,
                Source = t.Source,
                PropertyName = t.PropertyName,
                Format = t.Format,
                FixedText = t.FixedText,
                CenterDisplay = t.CenterDisplay,
                WatchProperty = t.WatchProperty,
                DurationMs = t.DurationMs,
                ShowWhenRunning = t.ShowWhenRunning,
                ShowWhenIdle = t.ShowWhenIdle,
                IsEnabled = true,
            };
        }

        /// <summary>Default layer stack for new devices.</summary>
        public static List<DisplayLayer> DefaultLayers()
        {
            return new List<DisplayLayer>
            {
                // Overlays first (higher priority)
                CreateFromCatalog("PitLimiter"),
                CreateFromCatalog("YellowFlag"),
                CreateFromCatalog("GearChange"),
                // Base last
                CreateFromCatalog("Gear"),
            };
        }

    }
}
