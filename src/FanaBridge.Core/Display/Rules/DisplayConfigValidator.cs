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
            // keptIds is every id still in the document (including future-kind screens that
            // survive the round-trip but are not usable on this build).
            var screenIds = NormalizeScreens(config.Legacy, warn, out var keptIds);

            if (string.IsNullOrEmpty(config.Legacy.BaseScreenId))
            {
                config.Legacy.BaseScreenId = null;   // blank display when nothing is active
            }
            else if (screenIds.Contains(config.Legacy.BaseScreenId))
            {
                // Usable survivor — fine.
            }
            else if (keptIds.Contains(config.Legacy.BaseScreenId))
            {
                // Kept but unusable (e.g. unknown contentKind from a future version): preserve
                // BaseScreenId so a load/save round-trip stays byte-for-byte; the runtime
                // already resolves unknown-kind screens to blank (same as a null base).
                warn("base screen '" + config.Legacy.BaseScreenId
                    + "' is not usable on this build — the legacy display will be blank when no rule is active");
            }
            else
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

        /// <summary>
        /// Normalizes the screen library in place. Returns the survivor id set (usable
        /// screens rules may target). <paramref name="keptIds"/> receives every id still
        /// present in the document after normalization — including future-kind screens
        /// that are kept for the round-trip but excluded from survivors. Base-screen
        /// validation uses both: missing-from-kept clears BaseScreenId; kept-but-not-
        /// survivor leaves the text alone and warns that the base renders blank here.
        /// </summary>
        private static HashSet<string> NormalizeScreens(LegacyRuleSet legacy, Action<string> warn,
            out HashSet<string> keptIds)
        {
            // seenIds tracks every id we have kept (including unknown-kind screens that
            // stay in the document for the round-trip). survivorIds is the subset rules
            // may target — unknown contentKind is excluded so those rules degrade like a
            // missing screen.
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var survivorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (!seenIds.Add(screen.Id))
                {
                    warn("duplicate legacy screen id '" + screen.Id + "' — keeping the first");
                    continue;
                }

                // Effect: Flash parses but coerces to Blink (runtime-only); unknown
                // effect text is left alone (treated as None by the clock, raw survives).
                if (screen.Effect == LegacyEffect.Flash)
                {
                    warn("legacy screen '" + screen.Id
                        + "': effect 'flash' is not implemented — using blink");
                    screen.CoerceEffect(LegacyEffect.Blink);
                }

                // Format is reserved/uninterpreted in v1 — clear any non-empty text.
                if (!string.IsNullOrEmpty(screen.Format))
                {
                    warn("legacy screen '" + screen.Id
                        + "' — unrecognized format '" + screen.Format + "' cleared");
                    screen.Format = null;
                }

                bool usable;
                switch (screen.ContentKind)
                {
                    case LegacyContentKind.Text:
                        if (!LegacyScreen.IsRenderableText(screen.Text))
                        {
                            warn("legacy screen '" + screen.Id + "' skipped — text "
                                + (screen.Text == null ? "(none)" : "'" + screen.Text + "'")
                                + " is not renderable (1-3 seven-segment positions)");
                            // Drop from the document (current-version broken data) and
                            // free the id so a later sibling could claim it — matching
                            // the pre-growth skip behaviour for unrenderable text.
                            seenIds.Remove(screen.Id);
                            continue;
                        }
                        usable = true;
                        break;

                    case LegacyContentKind.Message:
                        if (!LegacyScreen.IsRenderableMessage(screen.Text))
                        {
                            warn("legacy screen '" + screen.Id + "' skipped — message "
                                + (screen.Text == null ? "(none)" : "'" + screen.Text + "'")
                                + " is not renderable (every char must be seven-segment, length ≥ 1)");
                            seenIds.Remove(screen.Id);
                            continue;
                        }
                        usable = true;
                        break;

                    case LegacyContentKind.Property:
                        string badSource = InvalidPropertySourceReason(screen.Source);
                        if (badSource != null)
                        {
                            warn("legacy screen '" + screen.Id + "' skipped — " + badSource);
                            seenIds.Remove(screen.Id);
                            continue;
                        }
                        usable = true;
                        break;

                    case LegacyContentKind.Speed:
                    case LegacyContentKind.Gear:
                    case LegacyContentKind.GearAndSpeed:
                    case LegacyContentKind.GearBrackets:
                    case LegacyContentKind.Rpm:
                    case LegacyContentKind.Position:
                    case LegacyContentKind.Fuel:
                        // Dynamic kinds ignore Text — nothing to validate at load.
                        usable = true;
                        break;

                    case LegacyContentKind.Unknown:
                    default:
                        // Future-version kind: keep for the round-trip, exclude from
                        // survivors (rules targeting it degrade like a missing screen).
                        warn("legacy screen '" + screen.Id + "' skipped — "
                            + (screen.ContentKindRaw == null ? "no content kind"
                                : "unrecognized content kind '" + screen.ContentKindRaw + "'"));
                        usable = false;
                        break;
                }

                kept.Add(screen);
                if (usable)
                    survivorIds.Add(screen.Id);
            }

            legacy.Screens.Clear();
            legacy.Screens.AddRange(kept);
            keptIds = seenIds;
            return survivorIds;
        }

        /// <summary>Why a Property-kind screen's source is unusable, or null if fine.</summary>
        private static string InvalidPropertySourceReason(PropertySpec source)
        {
            if (source == null)
                return "property kind requires a source";
            if (string.IsNullOrWhiteSpace(source.Name))
                return "source has no name";
            if (source.Kind == PropertyKind.Unknown)
                return source.KindRaw == null ? "no source kind"
                    : "unrecognized source kind '" + source.KindRaw + "'";
            if (source.Kind == PropertyKind.FanaBridgeAction)
                return "an action is not a readable value";
            if (source.Kind == PropertyKind.BuiltIn && !BuiltInProperties.IsKnown(source.Name))
                return "unknown built-in property '" + source.Name + "'";
            return null;
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
                        else if (t.PeriodMs < RuleTarget.MinCyclePeriodMs)
                        {
                            warn(setName + " rule '" + Label(rule) + "': alternate period "
                                + t.PeriodMs + "ms clamped to "
                                + RuleTarget.MinCyclePeriodMs + "ms");
                            t.PeriodMs = RuleTarget.MinCyclePeriodMs;
                        }
                        break;

                    case TargetKind.Cycle:
                        if (isLegacySet)
                        {
                            Disable("legacy rules can only target legacy screens");
                        }
                        else
                        {
                            var pages = t.CyclePages;
                            if (pages.Count < 2)
                            {
                                Disable("a cycle needs at least two pages");
                            }
                            else
                            {
                                bool pageOk = true;
                                for (int i = 0; i < pages.Count; i++)
                                {
                                    if (pages[i] != null)
                                        continue;
                                    string raw = t.PagesRaw != null && i < t.PagesRaw.Count
                                        ? t.PagesRaw[i] : null;
                                    Disable(raw == null
                                        ? "missing or unrecognized cycle page"
                                        : "missing or unrecognized cycle page '" + raw + "'");
                                    pageOk = false;
                                    break;
                                }
                                if (pageOk && t.PeriodMs < RuleTarget.MinCyclePeriodMs)
                                {
                                    warn(setName + " rule '" + Label(rule) + "': cycle period "
                                        + t.PeriodMs + "ms clamped to "
                                        + RuleTarget.MinCyclePeriodMs + "ms");
                                    t.PeriodMs = RuleTarget.MinCyclePeriodMs;
                                }
                            }
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
                // Gear / EngineMapping keep special wire text forms — overrides are
                // rejected whole (warn+drop). The Pages UI locks those fields too.
                if (FieldFormats.IsOverrideExcluded(kv.Key))
                {
                    warn("field mapping for param " + kv.Key
                        + " dropped — Gear and EngineMapping cannot be remapped");
                    (drop ?? (drop = new List<ushort>())).Add(kv.Key);
                    continue;
                }

                string reason = InvalidMappingReason(kv.Value);
                if (reason != null)
                {
                    warn("field mapping for param " + kv.Key + " dropped — " + reason);
                    (drop ?? (drop = new List<ushort>())).Add(kv.Key);
                    continue;
                }

                // Format is independent of the mapping body: unknown / disallowed text
                // is warn-and-dropped (cleared) while the source override stays — same
                // degrade style as NormalizeFieldMappings' other current-version data.
                NormalizeMappingFormat(kv.Key, kv.Value, warn);
            }
            if (drop != null)
                foreach (var key in drop)
                    mappings.Remove(key);
        }

        private static void NormalizeMappingFormat(
            ushort paramId, FieldMapping mapping, Action<string> warn)
        {
            if (string.IsNullOrEmpty(mapping.Format))
            {
                mapping.Format = null;
                return;
            }
            if (FieldFormats.IsAllowed(paramId, mapping.Format))
                return;
            warn("field mapping for param " + paramId
                + " — unrecognized format '" + mapping.Format + "' cleared");
            mapping.Format = null;
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
