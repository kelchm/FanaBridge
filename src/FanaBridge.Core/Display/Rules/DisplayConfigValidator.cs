using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Load-time validation and normalization for <see cref="DisplayCustomizationConfig"/>.
    /// The contract is warn-and-degrade, never throw: the document arrives from
    /// per-device settings (possibly written by a future version), so a bad element costs
    /// exactly that element — an invalid rule is degraded (never dropped), an unrenderable
    /// legacy screen is skipped, a broken field mapping is dropped — and the rest of the
    /// document loads. Degradations of things a FUTURE version may understand are
    /// runtime-only: an unrecognized kind marks the rule
    /// <see cref="DisplayRule.DegradedAtLoad"/> (the persisted
    /// <see cref="DisplayRule.Enabled"/> stays the user's own choice) and unknown enum
    /// text is coerced through the runtime-only hooks — so the rule survives a load/save
    /// round-trip byte-for-byte for the version that knows it. Broken CURRENT-version
    /// data (an impossible hold combination, an out-of-range period) is normalized in
    /// place: there is nothing forward-compatible to preserve.
    ///
    /// After <see cref="Normalize"/> the invariants the rule engine relies on hold for
    /// every effectively-enabled rule: a recognized condition kind with a usable source
    /// (and a finite value where the comparison needs one), a resolvable target, a hold
    /// compatible with the condition's family (edge/event conditions never carry
    /// WhileActive — there is no "still active" to track), a positive ForDuration window,
    /// a clamped alternate period, a known eligibility, and a unique non-empty id.
    /// </summary>
    public static class DisplayConfigValidator
    {
        /// <summary>
        /// Validates and normalizes <paramref name="config"/> in place, reporting every
        /// degradation through <paramref name="log"/>. Returns the (same) normalized
        /// instance; a null input yields a fresh default document. Runs before the config
        /// is published, which is what keeps the immutable-after-load convention honest.
        /// </summary>
        public static DisplayCustomizationConfig Normalize(
            DisplayCustomizationConfig config, Action<string> log)
        {
            Action<string> warn = m => log?.Invoke("DisplayConfig: " + m);

            if (config == null)
                config = new DisplayCustomizationConfig();

            if (config.SchemaVersion <= 0)
            {
                warn("missing schema version — assuming "
                    + DisplayCustomizationConfig.CurrentSchemaVersion);
                config.SchemaVersion = DisplayCustomizationConfig.CurrentSchemaVersion;
            }
            else if (config.SchemaVersion > DisplayCustomizationConfig.CurrentSchemaVersion)
            {
                warn("config uses newer schema version " + config.SchemaVersion
                    + " (current is " + DisplayCustomizationConfig.CurrentSchemaVersion
                    + ") — this build may not honor all of it");
            }

            if (config.Itm == null) config.Itm = new ItmRuleSet();
            if (config.Itm.Rules == null) config.Itm.Rules = new List<DisplayRule>();
            if (config.Legacy == null) config.Legacy = new LegacyRuleSet();
            if (config.Legacy.Rules == null) config.Legacy.Rules = new List<DisplayRule>();
            if (config.Legacy.Screens == null) config.Legacy.Screens = new List<LegacyScreen>();
            if (config.FieldMappings == null)
                config.FieldMappings = new Dictionary<ushort, FieldMapping>();

            // The base page accessor falls back to LapInfo on its own; unrecognized text
            // just deserves a warning (and stays in the document for the round-trip).
            if (config.Itm.BasePageRaw != null
                && EnumText.ParseNullable<ItmPage>(config.Itm.BasePageRaw) == null)
            {
                warn("unrecognized base page '" + config.Itm.BasePageRaw + "' — using "
                    + config.Itm.BasePage);
            }

            // Screens first: rules validate their screen targets against the survivors.
            var screenIds = NormalizeScreens(config.Legacy, warn);

            if (string.IsNullOrEmpty(config.Legacy.BaseScreenId))
            {
                config.Legacy.BaseScreenId = null;   // blank display when nothing is active
            }
            else if (!screenIds.Contains(config.Legacy.BaseScreenId))
            {
                warn("base screen '" + config.Legacy.BaseScreenId
                    + "' does not exist — the legacy display will be blank when no rule is active");
                config.Legacy.BaseScreenId = null;
            }

            // Rule ids are unique across both sets, so activity events and UI selection
            // can key on id alone.
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            config.Itm.Rules.RemoveAll(r => r == null);
            config.Legacy.Rules.RemoveAll(r => r == null);
            foreach (var rule in config.Itm.Rules)
                NormalizeRule(rule, "ITM", isLegacySet: false, screenIds, seenIds, warn);
            foreach (var rule in config.Legacy.Rules)
                NormalizeRule(rule, "legacy", isLegacySet: true, screenIds, seenIds, warn);

            NormalizeFieldMappings(config.FieldMappings, warn);
            return config;
        }

        // ── Screens ──────────────────────────────────────────────────────

        private static HashSet<string> NormalizeScreens(LegacyRuleSet legacy, Action<string> warn)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<LegacyScreen>(legacy.Screens.Count);

            foreach (var screen in legacy.Screens)
            {
                if (screen == null)
                    continue;
                if (string.IsNullOrWhiteSpace(screen.Id))
                {
                    warn("legacy screen '" + (screen.Name ?? screen.Text ?? "?")
                        + "' skipped — no id");
                    continue;
                }
                if (!LegacyScreen.IsRenderableText(screen.Text))
                {
                    warn("legacy screen '" + screen.Id + "' skipped — text "
                        + (screen.Text == null ? "(none)" : "'" + screen.Text + "'")
                        + " is not renderable (1-3 seven-segment positions)");
                    continue;
                }
                if (!ids.Add(screen.Id))
                {
                    warn("duplicate legacy screen id '" + screen.Id + "' — keeping the first");
                    continue;
                }
                kept.Add(screen);
            }

            legacy.Screens.Clear();
            legacy.Screens.AddRange(kept);
            return ids;
        }

        // ── Rules ────────────────────────────────────────────────────────

        private static void NormalizeRule(DisplayRule rule, string setName, bool isLegacySet,
            HashSet<string> screenIds, HashSet<string> seenIds, Action<string> warn)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
                warn(setName + " rule '" + Label(rule) + "' had no id — assigned one");
            }
            if (!seenIds.Add(rule.Id))
            {
                warn(setName + " rule '" + Label(rule) + "' duplicates id '" + rule.Id
                    + "' — assigned a new one");
                rule.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(rule.Id);
            }

            void Disable(string reason)
            {
                rule.DegradedAtLoad = true;
                warn(setName + " rule '" + Label(rule) + "' disabled — " + reason);
            }

            // Condition. An unrecognized kind (future version) degrades rather than
            // drops — runtime-only, so the rule survives a round-trip byte-for-byte
            // for the version that knows it.
            var c = rule.When;
            bool kindKnown = false;
            if (c == null)
            {
                Disable("no condition");
            }
            else if (c.Kind == ConditionKind.Unknown)
            {
                Disable(c.KindRaw == null ? "no condition kind"
                    : "unrecognized condition kind '" + c.KindRaw + "'");
            }
            else
            {
                kindKnown = true;
                string bad = InvalidConditionSourceReason(c);
                if (bad != null)
                    Disable(bad);
                if (c.Kind.RequiresValue())
                {
                    if (c.Value == null)
                        Disable("condition has no comparison value");
                    else if (!IsFinite(c.Value.Value))
                        Disable("comparison value is not a finite number");
                }
                if (c.Hysteresis != null)
                {
                    if (!c.Kind.IsLevel())
                    {
                        c.Hysteresis = null;    // only level kinds have a releasing direction
                    }
                    else if (!IsFinite(c.Hysteresis.Value))
                    {
                        // NaN would make every release comparison false — the condition
                        // would flap active/inactive on alternating ticks.
                        warn(setName + " rule '" + Label(rule)
                            + "': hysteresis is not a finite number — using 0");
                        c.Hysteresis = 0;
                    }
                    else if (c.Hysteresis < 0)
                    {
                        warn(setName + " rule '" + Label(rule)
                            + "': negative hysteresis clamped to 0");
                        c.Hysteresis = 0;
                    }
                }
            }

            bool isLevel = kindKnown && c.Kind.IsLevel();

            // Target.
            var t = rule.Show;
            if (t == null)
            {
                Disable("no target");
            }
            else
            {
                switch (t.Kind)
                {
                    case TargetKind.Page:
                        if (isLegacySet)
                            Disable("legacy rules can only target legacy screens");
                        else if (t.Page == null)
                            Disable(t.PageRaw == null ? "no target page"
                                : "unrecognized page '" + t.PageRaw + "'");
                        break;

                    case TargetKind.LegacyScreen:
                        if (string.IsNullOrWhiteSpace(t.ScreenId))
                            Disable("no target screen id");
                        else if (!screenIds.Contains(t.ScreenId))
                            Disable("targets legacy screen '" + t.ScreenId
                                + "' which does not exist");
                        break;

                    case TargetKind.Alternate:
                        if (isLegacySet)
                        {
                            Disable("legacy rules can only target legacy screens");
                        }
                        else if (t.PageA == null || t.PageB == null)
                        {
                            Disable("missing or unrecognized alternate pages");
                        }
                        else if (t.PeriodMs < RuleTarget.MinAlternatePeriodMs)
                        {
                            warn(setName + " rule '" + Label(rule) + "': alternate period "
                                + t.PeriodMs + "ms clamped to "
                                + RuleTarget.MinAlternatePeriodMs + "ms");
                            t.PeriodMs = RuleTarget.MinAlternatePeriodMs;
                        }
                        break;

                    default:
                        Disable(t.KindRaw == null ? "no target kind"
                            : "unrecognized target kind '" + t.KindRaw + "'");
                        break;
                }
            }

            // Hold. Missing hold gets the condition family's natural default silently;
            // impossible combinations are coerced with a warning. An unrecognized kind
            // (future version) is coerced at runtime only — the document keeps the text.
            if (rule.Hold == null)
                rule.Hold = new HoldSpec
                {
                    Kind = isLevel ? HoldKind.WhileActive : HoldKind.ForDuration,
                };
            if (rule.Hold.Kind == HoldKind.Unknown)
            {
                var coerced = isLevel ? HoldKind.WhileActive : HoldKind.ForDuration;
                warn(setName + " rule '" + Label(rule) + "': "
                    + (rule.Hold.KindRaw == null ? "no hold kind"
                        : "unrecognized hold kind '" + rule.Hold.KindRaw + "'")
                    + " — using " + coerced);
                if (rule.Hold.KindRaw == null)
                    rule.Hold.Kind = coerced;        // nothing to preserve
                else
                    rule.Hold.CoerceKind(coerced);   // runtime-only
            }
            if (kindKnown && !isLevel && rule.Hold.Kind == HoldKind.WhileActive)
            {
                warn(setName + " rule '" + Label(rule)
                    + "': WhileActive needs a level condition — coerced to ForDuration "
                    + HoldSpec.DefaultDurationMs + "ms");
                rule.Hold.Kind = HoldKind.ForDuration;
                rule.Hold.DurationMs = HoldSpec.DefaultDurationMs;
            }
            if (rule.Hold.Kind == HoldKind.ForDuration && rule.Hold.DurationMs <= 0)
            {
                warn(setName + " rule '" + Label(rule)
                    + "': hold duration must be positive — using "
                    + HoldSpec.DefaultDurationMs + "ms");
                rule.Hold.DurationMs = HoldSpec.DefaultDurationMs;
            }

            // Eligibility. Unrecognized text (future version) coerces at runtime only.
            if (rule.Eligible == RuleEligibility.Unknown)
            {
                warn(setName + " rule '" + Label(rule) + "': unrecognized eligibility '"
                    + rule.EligibleRaw + "' — using InGame");
                rule.CoerceEligible(RuleEligibility.InGame);
            }
        }

        // net48 has no double.IsFinite; NaN and the infinities poison comparisons
        // (every relational test is false), so they must never reach the engine.
        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        // Why a condition's source is unusable, or null if it is fine. Event conditions
        // only need a name (matched against triggered actions); value conditions need a
        // readable source — an action is not one, and a built-in name must be in the
        // closed set the adapter interprets.
        private static string InvalidConditionSourceReason(RuleCondition c)
        {
            if (c.Source == null)
                return "no source property";
            if (string.IsNullOrWhiteSpace(c.Source.Name))
                return "source has no name";
            if (c.Source.Kind == PropertyKind.Unknown)
                return c.Source.KindRaw == null ? "no source kind"
                    : "unrecognized source kind '" + c.Source.KindRaw + "'";
            if (c.Kind.IsEvent())
                return null;
            if (c.Source.Kind == PropertyKind.FanaBridgeAction)
                return "an action is not a readable value";
            if (c.Source.Kind == PropertyKind.BuiltIn && !BuiltInProperties.IsKnown(c.Source.Name))
                return "unknown built-in property '" + c.Source.Name + "'";
            return null;
        }

        private static string Label(DisplayRule rule)
            => !string.IsNullOrWhiteSpace(rule.Name) ? rule.Name : (rule.Id ?? "?");

        // ── Field mappings ───────────────────────────────────────────────

        private static void NormalizeFieldMappings(
            Dictionary<ushort, FieldMapping> mappings, Action<string> warn)
        {
            List<ushort> drop = null;
            foreach (var kv in mappings)
            {
                string reason = InvalidMappingReason(kv.Value);
                if (reason == null)
                    continue;
                warn("field mapping for param " + kv.Key + " dropped — " + reason);
                (drop ?? (drop = new List<ushort>())).Add(kv.Key);
            }
            if (drop != null)
                foreach (var key in drop)
                    mappings.Remove(key);
        }

        private static string InvalidMappingReason(FieldMapping mapping)
        {
            if (mapping == null)
                return "no mapping body";
            if (mapping.Source == null)
                return "no source property";
            if (string.IsNullOrWhiteSpace(mapping.Source.Name))
                return "source has no name";
            switch (mapping.Source.Kind)
            {
                case PropertyKind.BuiltIn:
                    return BuiltInProperties.IsKnown(mapping.Source.Name)
                        ? null
                        : "unknown built-in property '" + mapping.Source.Name + "'";
                case PropertyKind.SimHubProperty:
                    return null;
                case PropertyKind.FanaBridgeAction:
                    return "an action cannot feed a value field";
                default:
                    return mapping.Source.KindRaw == null ? "no source kind"
                        : "unrecognized source kind '" + mapping.Source.KindRaw + "'";
            }
        }
    }
}
