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
                PropertyName = "DataCorePlugin.GameData.Gear",
                DisplayFormat = DisplayFormat.Gear,
            },
            new DisplayLayer
            {
                CatalogKey = "Speed", Name = "Speed",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.SpeedLocal",
                DisplayFormat = DisplayFormat.Number,
            },
            new DisplayLayer
            {
                CatalogKey = "Lap", Name = "Current Lap",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.CurrentLap",
                DisplayFormat = DisplayFormat.Number,
            },
            new DisplayLayer
            {
                CatalogKey = "FuelPct", Name = "Fuel",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.FuelPercent",
                DisplayFormat = DisplayFormat.Number,
            },
            new DisplayLayer
            {
                CatalogKey = "Position", Name = "Position",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.Position",
                DisplayFormat = DisplayFormat.Number,
            },
            new DisplayLayer
            {
                CatalogKey = "LapTime", Name = "Lap Time",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.CurrentLapTime",
                DisplayFormat = DisplayFormat.Time,
            },
            new DisplayLayer
            {
                CatalogKey = "BestLap", Name = "Best Lap",
                Mode = DisplayLayerMode.Constant,
                Source = DisplaySource.Property,
                PropertyName = "DataCorePlugin.GameData.BestLapTime",
                DisplayFormat = DisplayFormat.Time,
            },

            // ── Conditional overlays ─────────────────────────────────
            new DisplayLayer
            {
                CatalogKey = "GearChange", Name = "Gear Change",
                Mode = DisplayLayerMode.OnChange,
                Source = DisplaySource.Property,
                WatchProperty = "DataCorePlugin.GameData.Gear",
                PropertyName = "DataCorePlugin.GameData.Gear",
                DisplayFormat = DisplayFormat.Gear,
                DurationMs = 2000,
            },
            new DisplayLayer
            {
                CatalogKey = "PitLimiter", Name = "Pit Limiter",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.PitLimiterOn",
                FixedText = "PIT",
                DisplayFormat = DisplayFormat.Text,
            },
            new DisplayLayer
            {
                CatalogKey = "YellowFlag", Name = "Yellow Flag",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.Flag_Yellow",
                FixedText = "YEL",
                DisplayFormat = DisplayFormat.Text,
            },
            new DisplayLayer
            {
                CatalogKey = "BlueFlag", Name = "Blue Flag",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.Flag_Blue",
                FixedText = "BLU",
                DisplayFormat = DisplayFormat.Text,
            },
            new DisplayLayer
            {
                CatalogKey = "DRS", Name = "DRS",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.DRSAvailable",
                FixedText = "DRS",
                DisplayFormat = DisplayFormat.Text,
            },
            new DisplayLayer
            {
                CatalogKey = "LowFuel", Name = "Low Fuel",
                Mode = DisplayLayerMode.WhileTrue,
                Source = DisplaySource.FixedText,
                WatchProperty = "DataCorePlugin.GameData.FuelAlertActive",
                FixedText = "FUL",
                DisplayFormat = DisplayFormat.Text,
            },

            // ── Expression-based overlays ────────────────────────────
            new DisplayLayer
            {
                CatalogKey = "ShiftWarning", Name = "Shift Warning",
                Mode = DisplayLayerMode.Expression,
                Source = DisplaySource.Expression,
                Expression = "if([DataCorePlugin.GameData.CarSettings_RPMShiftLight2] == 1, "
                           + "if(blink('shiftflash', 150, true), "
                           + "' ' + [DataCorePlugin.GameData.Gear] + ' ', "
                           + "'[' + [DataCorePlugin.GameData.Gear] + ']'), '')",
                DisplayFormat = DisplayFormat.Text,
            },
            new DisplayLayer
            {
                CatalogKey = "LowFuelBlink", Name = "Low Fuel (Blink)",
                Mode = DisplayLayerMode.Expression,
                Source = DisplaySource.Expression,
                Expression = "if([DataCorePlugin.GameData.FuelAlertActive], "
                           + "if(blink('fuelblink', 500, true), 'FUL', '   '), '')",
                DisplayFormat = DisplayFormat.Text,
            },
        };

        /// <summary>Finds a catalog entry by key and returns a deep copy, or null.</summary>
        public static DisplayLayer FindByKey(string key)
        {
            return CreateFromCatalog(key);
        }

        /// <summary>Creates a deep copy of a catalog entry.</summary>
        public static DisplayLayer CreateFromCatalog(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var t = All.FirstOrDefault(e => e.CatalogKey == key);
            if (t == null) return null;

            return new DisplayLayer
            {
                CatalogKey = t.CatalogKey,
                Name = t.Name,
                Mode = t.Mode,
                Source = t.Source,
                PropertyName = t.PropertyName,
                DisplayFormat = t.DisplayFormat,
                TimeFormat = t.TimeFormat,
                FixedText = t.FixedText,
                Expression = t.Expression,
                WatchProperty = t.WatchProperty,
                DurationMs = t.DurationMs,
                ShowWhenRunning = t.ShowWhenRunning,
                ShowWhenIdle = t.ShowWhenIdle,
                ScrollSpeedMs = t.ScrollSpeedMs,
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
