using System;
using System.Collections.Generic;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Host
{
    /// <summary>
    /// Pure Phase 9a / P10a step: once per device settings bag, fold the frozen
    /// <see cref="DisplaySettings.DisplayMode"/> into a legacy world when the
    /// document's legacy world is empty. Never mutates a raw settings JObject — only the
    /// in-memory <see cref="DisplaySettings"/> marker and the parsed config graph.
    /// Composite frozen modes (GearAndSpeed, GearUpshiftBrackets) synthesize the layered
    /// trio: pure base screen + overlay-only screen + trigger.
    /// </summary>
    internal static class LegacyModeMigration
    {
        /// <summary>
        /// If <paramref name="settings"/> has not been migrated yet, synthesizes a legacy
        /// world from <see cref="DisplaySettings.DisplayMode"/> when the world is empty
        /// and the mode is a real content mode; always sets
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
                && TrySynthesize(settings.DisplayMode, out Action<LegacyRuleSet> graft))
            {
                if (config == null)
                    config = new DisplayCustomizationConfig();
                if (config.Legacy == null)
                    config.Legacy = new LegacyRuleSet();
                if (config.Legacy.Screens == null)
                    config.Legacy.Screens = new List<LegacyScreen>();
                if (config.Legacy.Rules == null)
                    config.Legacy.Rules = new List<DisplayRule>();

                graft(config.Legacy);
            }

            // Bake-on-sight: authored worlds and ModeNone bake without synthesis so a
            // later empty world cannot re-seed from the frozen mode.
            settings.LegacyModeMigrated = true;
            return config;
        }

        /// <summary>
        /// Maps a frozen display-mode string to a synthesis graft on an empty
        /// <see cref="LegacyRuleSet"/>. <see cref="DisplaySettings.ModeNone"/> → false.
        /// Unknown strings map to Gear (driver parity: unknown mode falls back to Gear).
        /// </summary>
        internal static bool TrySynthesize(string displayMode, out Action<LegacyRuleSet> graft)
        {
            if (string.Equals(displayMode, DisplaySettings.ModeNone, StringComparison.OrdinalIgnoreCase))
            {
                graft = null;
                return false;
            }

            if (string.Equals(displayMode, "Speed", StringComparison.OrdinalIgnoreCase))
            {
                graft = leg => AddSingleBase(leg, LegacyContentKind.Speed, "Speed");
                return true;
            }
            if (string.Equals(displayMode, "GearAndSpeed", StringComparison.OrdinalIgnoreCase))
            {
                graft = AddGearAndSpeedTrio;
                return true;
            }
            if (string.Equals(displayMode, "GearUpshiftBrackets", StringComparison.OrdinalIgnoreCase))
            {
                graft = AddGearUpshiftBracketsTrio;
                return true;
            }

            // Gear, default, and unknown → Gear (driver unknown-mode fallback).
            graft = leg => AddSingleBase(leg, LegacyContentKind.Gear, "Gear");
            return true;
        }

        private static void AddSingleBase(LegacyRuleSet leg, LegacyContentKind kind, string name)
        {
            var screen = NewScreen(name, kind, inRotation: true);
            leg.Screens.Add(screen);
            leg.BaseScreenId = screen.Id;
        }

        /// <summary>Speed base + Gear overlay (inRotation false) + Changes/Gear 2s hold.</summary>
        private static void AddGearAndSpeedTrio(LegacyRuleSet leg)
        {
            var speed = NewScreen("Speed", LegacyContentKind.Speed, inRotation: true);
            var gear = NewScreen("Gear", LegacyContentKind.Gear, inRotation: false);
            leg.Screens.Add(speed);
            leg.Screens.Add(gear);
            leg.BaseScreenId = speed.Id;
            leg.Rules.Add(new DisplayRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Gear change",
                When = new RuleCondition
                {
                    Kind = ConditionKind.Changes,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.Gear,
                    },
                },
                Show = new RuleTarget
                {
                    Kind = TargetKind.LegacyScreen,
                    ScreenId = gear.Id,
                },
                Hold = new HoldSpec
                {
                    Kind = HoldKind.ForDuration,
                    DurationMs = LegacyValueFormatter.GearOverlayMs,
                },
            });
        }

        /// <summary>Gear base + GearBrackets overlay + IsTrue/RedlineReached whileActive.</summary>
        private static void AddGearUpshiftBracketsTrio(LegacyRuleSet leg)
        {
            var gear = NewScreen("Gear", LegacyContentKind.Gear, inRotation: true);
            var brackets = NewScreen("Gear (brackets)", LegacyContentKind.GearBrackets, inRotation: false);
            leg.Screens.Add(gear);
            leg.Screens.Add(brackets);
            leg.BaseScreenId = gear.Id;
            leg.Rules.Add(new DisplayRule
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Redline",
                When = new RuleCondition
                {
                    Kind = ConditionKind.IsTrue,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.RedlineReached,
                    },
                },
                Show = new RuleTarget
                {
                    Kind = TargetKind.LegacyScreen,
                    ScreenId = brackets.Id,
                },
                Hold = new HoldSpec
                {
                    Kind = HoldKind.WhileActive,
                },
            });
        }

        private static LegacyScreen NewScreen(string name, LegacyContentKind kind, bool inRotation)
        {
            return new LegacyScreen
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                ContentKind = kind,
                InRotation = inRotation,
            };
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
