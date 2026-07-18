using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Pure Phase 9a step: once per device settings bag, fold the frozen
    /// <see cref="DisplaySettings.DisplayMode"/> into a legacy virtual page when the
    /// document's legacy world is empty. Never mutates a raw settings JObject — only the
    /// in-memory <see cref="DisplaySettings"/> marker and the parsed config graph.
    /// </summary>
    internal static class LegacyModeMigration
    {
        /// <summary>
        /// If <paramref name="settings"/> has not been migrated yet, synthesizes a base
        /// legacy screen from <see cref="DisplaySettings.DisplayMode"/> when the world is
        /// empty and the mode is a real content mode; always sets
        /// <see cref="DisplaySettings.LegacyModeMigrated"/> so the step is bake-on-sight.
        /// Returns the (possibly grafted / fresh) config, or the original null/empty when
        /// no synthesis ran.
        /// </summary>
        public static DisplayCustomizationConfig Apply(
            DisplaySettings settings, DisplayCustomizationConfig parsedOrNull)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (settings.LegacyModeMigrated)
                return parsedOrNull;

            DisplayCustomizationConfig config = parsedOrNull;

            if (!HasLegacyWorld(config)
                && TryMapMode(settings.DisplayMode, out LegacyContentKind kind, out string name))
            {
                var screen = new LegacyScreen
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = name,
                    ContentKind = kind,
                };

                if (config == null)
                    config = new DisplayCustomizationConfig();
                if (config.Legacy == null)
                    config.Legacy = new LegacyRuleSet();
                if (config.Legacy.Screens == null)
                    config.Legacy.Screens = new List<LegacyScreen>();

                config.Legacy.Screens.Add(screen);
                config.Legacy.BaseScreenId = screen.Id;
            }

            // Bake-on-sight: authored worlds and ModeNone bake without synthesis so a
            // later empty world cannot re-seed from the frozen mode.
            settings.LegacyModeMigrated = true;
            return config;
        }

        /// <summary>
        /// Maps a frozen display-mode string to a content kind + combo label.
        /// <see cref="DisplaySettings.ModeNone"/> → false (no synthesis). Unknown strings
        /// map to Gear (driver parity: unknown mode falls back to Gear).
        /// </summary>
        internal static bool TryMapMode(string displayMode,
            out LegacyContentKind kind, out string name)
        {
            if (string.Equals(displayMode, DisplaySettings.ModeNone, StringComparison.OrdinalIgnoreCase))
            {
                kind = default(LegacyContentKind);
                name = null;
                return false;
            }

            if (string.Equals(displayMode, "Speed", StringComparison.OrdinalIgnoreCase))
            {
                kind = LegacyContentKind.Speed;
                name = "Speed";
                return true;
            }
            if (string.Equals(displayMode, "GearAndSpeed", StringComparison.OrdinalIgnoreCase))
            {
                kind = LegacyContentKind.GearAndSpeed;
                name = "Gear + Speed";
                return true;
            }
            if (string.Equals(displayMode, "GearUpshiftBrackets", StringComparison.OrdinalIgnoreCase))
            {
                kind = LegacyContentKind.GearBrackets;
                name = "Gear + Upshift Brackets";
                return true;
            }

            // Gear, default, and unknown → Gear (driver unknown-mode fallback).
            kind = LegacyContentKind.Gear;
            name = "Gear";
            return true;
        }

        /// <summary>Same non-empty-world gate as <c>DisplayRuleStack.HasLegacyWorld</c>
        /// (screens or rules; BaseScreenId alone does not count). Inlined so Host stays
        /// free of a Runtime dependency.</summary>
        private static bool HasLegacyWorld(DisplayCustomizationConfig config)
        {
            var leg = config?.Legacy;
            if (leg == null)
                return false;
            return (leg.Rules != null && leg.Rules.Count > 0)
                || (leg.Screens != null && leg.Screens.Count > 0);
        }
    }
}
