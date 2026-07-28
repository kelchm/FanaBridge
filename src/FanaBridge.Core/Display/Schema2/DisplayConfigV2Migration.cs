using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Pure v1 → v2 document converter (phase E2). Maps a post-Normalize
    /// <see cref="DisplayCustomizationConfig"/> to a <see cref="DisplayConfigV2"/> per
    /// spec-schema-v2 §9. Deterministic and total: every v1 document produces a v2
    /// document; degraded v1 rules carry (enabled + raw spellings). No runtime wiring,
    /// no settings-store integration, no bake-on-sight marker.
    /// </summary>
    public static class DisplayConfigV2Migration
    {
        /// <summary>
        /// Reserved hosted-page id for bare Legacy targets when <c>baseScreenId</c> is
        /// absent. Intentionally resolves to no page entry (degraded-visible).
        /// </summary>
        public const string BareLegacyHostedPageId = "p-v1-legacy";

        /// <summary>
        /// Reserved hosted-page id prefix. User/migrated screen ids that already start
        /// with this prefix are namespace-escaped so they never collide with the
        /// bare-Legacy placeholder (see <see cref="EscapeReservedHostedPageId"/>).
        /// </summary>
        public const string ReservedHostedPageIdPrefix = "p-v1-";

        /// <summary>Prefix applied when escaping a pre-existing v1 id under the reserved namespace.</summary>
        public const string ReservedHostedPageIdEscapePrefix = "u-";

        /// <summary>Reserved lifetime extension key for coerced/unknown edge hold spellings.</summary>
        public const string V1HoldKindKey = "v1HoldKind";

        /// <summary>Reserved seat-row extension key for the complete unknown-show payload.</summary>
        public const string V1ShowKey = "v1Show";

        /// <summary>
        /// Converts a v1 document (post <see cref="DisplayConfigValidator.Normalize"/>)
        /// into a v2 document. Null input yields a default blank v2 document.
        /// Does not mutate <paramref name="v1"/>.
        /// </summary>
        public static DisplayConfigV2 Convert(DisplayCustomizationConfig v1)
        {
            var v2 = new DisplayConfigV2
            {
                SchemaVersion = DisplayConfigV2.CurrentSchemaVersion,
                Pages = new List<PageEntry>(),
                Cycles = new List<CycleEntry>(),
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>(),
                    Rest = new RestBlock
                    {
                        // Migration default (§9): no v1 idle concept → blank.
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
                Fields = new Dictionary<ushort, FieldEntry>(),
                WheelScreen = new WheelScreenPlane
                {
                    Rules = new List<WheelScreenRule>(),
                },
                Settings = new SettingsBlock(),
            };

            if (v1 == null)
                return v2;

            v2.ProfileId = v1.ProfileId;

            // Dissolved container unknowns (itm / segmentDisplay) + root unknowns → root.
            // Collisions are namespaced with source-path prefixes (every value preserved).
            v2.ExtensionData = MergeExtensionDataPrefixed(
                CopyExtensionData(v1.ExtensionData),
                v1.Itm != null ? v1.Itm.ExtensionData : null, "itm.",
                v1.Legacy != null ? v1.Legacy.ExtensionData : null, "segmentDisplay.");

            string baseScreenId = v1.Legacy != null ? v1.Legacy.BaseScreenId : null;

            // screens[] → hosted pages + pageOrder (inRotation membership).
            MigrateScreens(v1, v2);

            // fieldMappings → fields.{param}.base (overrides empty).
            MigrateFieldMappings(v1, v2);

            // Rules: itm.rules rank above segmentDisplay.rules; specials → wheelScreen.
            // Deterministic id allocator spans both families (first valid kept; synth stable).
            var ids = new RuleIdAllocator();
            var ladder = new LadderBuilder(v2, baseScreenId, ids);
            if (v1.Itm != null && v1.Itm.Rules != null)
            {
                foreach (var rule in v1.Itm.Rules)
                {
                    if (rule != null)
                        ladder.AddRule(rule);
                }
            }
            if (v1.Legacy != null && v1.Legacy.Rules != null)
            {
                foreach (var rule in v1.Legacy.Rules)
                {
                    if (rule != null)
                        ladder.AddRule(rule);
                }
            }

            // Rest floor: basePage / baseScreenId.
            MigrateRest(v1, v2, baseScreenId);

            return v2;
        }

        // ── Screens ──────────────────────────────────────────────────────────

        private static void MigrateScreens(DisplayCustomizationConfig v1, DisplayConfigV2 v2)
        {
            if (v1.Legacy == null || v1.Legacy.Screens == null)
                return;

            List<PageRef> pageOrder = null;
            int screenCount = 0;
            foreach (var screen in v1.Legacy.Screens)
            {
                if (screen == null)
                    continue;

                screenCount++;
                // Escape reserved p-v1-* ids so they never collide with the bare-Legacy
                // placeholder (user page literally named p-v1-legacy → u-p-v1-legacy).
                string hostedId = EscapeReservedHostedPageId(screen.Id);

                var page = new PageEntry
                {
                    Kind = PageEntryKind.HostedPage,
                    Id = hostedId,
                    Name = screen.Name,
                    Base = MigrateScreenBase(screen),
                    ExtensionData = CopyExtensionData(screen.ExtensionData),
                };
                v2.Pages.Add(page);

                if (screen.InRotation)
                {
                    if (pageOrder == null)
                        pageOrder = new List<PageRef>();
                    pageOrder.Add(new PageRef
                    {
                        Kind = PageRefKind.HostedPage,
                        Id = hostedId,
                    });
                }
            }

            // pageOrder: screens exist but none inRotation → explicit []; no screens → absent.
            // Absent means compiled default; [] means empty walk (FZ-003 / §5b).
            if (screenCount > 0)
                v2.PageOrder = pageOrder ?? new List<PageRef>();
        }

        /// <summary>
        /// Deterministically namespace-escapes a v1 screen id that falls under the reserved
        /// <see cref="ReservedHostedPageIdPrefix"/> so migration never emits a colliding
        /// user page for the bare-Legacy placeholder.
        /// </summary>
        public static string EscapeReservedHostedPageId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return id;
            if (!id.StartsWith(ReservedHostedPageIdPrefix, StringComparison.Ordinal))
                return id;
            // Keep applying the escape prefix until the result is outside the reserved namespace.
            string escaped = ReservedHostedPageIdEscapePrefix + id;
            while (escaped.StartsWith(ReservedHostedPageIdPrefix, StringComparison.Ordinal))
                escaped = ReservedHostedPageIdEscapePrefix + escaped;
            return escaped;
        }

        private static ContentWithEffect MigrateScreenBase(LegacyScreen screen)
        {
            var content = new ContentObject();

            // Preserve raw contentKind spelling when present; default text when omitted.
            if (!string.IsNullOrWhiteSpace(screen.ContentKindRaw))
                content.KindRaw = screen.ContentKindRaw;
            else
                content.Kind = ContentKind.Text;

            // text / message carry the static string; property carries source + format.
            var kind = screen.ContentKind;
            if (kind == LegacyContentKind.Text || kind == LegacyContentKind.Message
                || kind == LegacyContentKind.Unknown)
            {
                if (screen.Text != null)
                    content.Text = screen.Text;
            }

            if (kind == LegacyContentKind.Property || screen.Source != null)
            {
                if (screen.Source != null)
                    content.Source = MigrateValueSource(screen.Source);
                if (screen.Format != null)
                    content.Format = screen.Format;
            }

            // Non-property kinds with a format key (reserved in v1) still carry it on content.
            if (kind != LegacyContentKind.Property && screen.Format != null && content.Format == null)
                content.Format = screen.Format;

            var bas = new ContentWithEffect { Content = content };

            // Carry effect only when non-default / explicitly spelled (none is suppressed).
            if (!string.IsNullOrWhiteSpace(screen.EffectRaw)
                && !string.Equals(screen.EffectRaw, "none", StringComparison.OrdinalIgnoreCase))
            {
                bas.EffectRaw = screen.EffectRaw;
            }
            else if (screen.Effect != LegacyEffect.None && screen.Effect != LegacyEffect.Unknown)
            {
                bas.EffectRaw = EnumText.Write(screen.Effect);
            }

            return bas;
        }

        // ── Field mappings ───────────────────────────────────────────────────

        private static void MigrateFieldMappings(DisplayCustomizationConfig v1, DisplayConfigV2 v2)
        {
            if (v1.FieldMappings == null || v1.FieldMappings.Count == 0)
                return;

            foreach (var kv in v1.FieldMappings)
            {
                var mapping = kv.Value;
                if (mapping == null)
                    continue;

                var fieldBase = new FieldBase
                {
                    Format = mapping.Format,
                    ExtensionData = CopyExtensionData(mapping.ExtensionData),
                };
                if (mapping.Source != null)
                    fieldBase.Source = MigrateValueSource(mapping.Source);

                v2.Fields[kv.Key] = new FieldEntry
                {
                    Base = fieldBase,
                    // Migration starts with an empty override ladder (§9).
                    Overrides = new List<FieldOverride>(),
                };
            }
        }

        // ── Rest floor ───────────────────────────────────────────────────────

        private static void MigrateRest(
            DisplayCustomizationConfig v1, DisplayConfigV2 v2, string baseScreenId)
        {
            string basePageRaw = v1.Itm != null ? v1.Itm.BasePageRaw : null;
            bool hasItmRules = v1.Itm != null && v1.Itm.Rules != null && v1.Itm.Rules.Count > 0;
            bool hasExplicitBasePage = !string.IsNullOrWhiteSpace(basePageRaw);
            // ITM-shaped document: explicit basePage and/or itm rules. Otherwise a
            // baseScreenId-only document is treated as segment-only.
            bool isItmShaped = hasExplicitBasePage || hasItmRules;

            if (hasExplicitBasePage)
            {
                if (IsLegacyPageToken(basePageRaw))
                {
                    // Bare-Legacy rest → hosted from baseScreenId, or reserved placeholder.
                    v2.Priority.Rest.InSessionPage = ResolveLegacyHostedRef(baseScreenId);
                }
                else
                {
                    v2.Priority.Rest.InSessionPage = ItmPageRef(basePageRaw);
                }
            }

            if (!string.IsNullOrWhiteSpace(baseScreenId))
            {
                string hostedBase = EscapeReservedHostedPageId(baseScreenId);
                if (isItmShaped)
                {
                    // ITM wheel: baseScreenId reborn as landingPage (own member).
                    v2.Priority.Rest.LandingPage = HostedPageRef(hostedBase);
                }
                else if (v2.Priority.Rest.InSessionPage == null)
                {
                    // Segment-only: baseScreenId → inSessionPage.
                    v2.Priority.Rest.InSessionPage = HostedPageRef(hostedBase);
                }
            }
        }

        // ── Ladder builder (rules → seats / satellites / wheelScreen) ────────

        private sealed class LadderBuilder
        {
            private readonly DisplayConfigV2 _v2;
            private readonly string _baseScreenId;
            private readonly RuleIdAllocator _ids;
            // Destination key → home seat row (first encounter).
            private readonly Dictionary<string, PriorityRow> _homes =
                new Dictionary<string, PriorityRow>(StringComparer.Ordinal);
            // Destination key of the last priority row appended (null if none / special).
            private string _lastDestKey;

            public LadderBuilder(DisplayConfigV2 v2, string baseScreenId, RuleIdAllocator ids)
            {
                _v2 = v2;
                _baseScreenId = baseScreenId;
                _ids = ids;
            }

            public void AddRule(DisplayRule rule)
            {
                // Allocate a migration id without mutating the v1 rule.
                string ruleId = _ids.Allocate(rule.Id);

                var show = rule.Show;
                if (show != null && show.Kind == TargetKind.Special)
                {
                    _v2.WheelScreen.Rules.Add(MigrateSpecialRule(rule, ruleId));
                    // Specials are a separate channel — they do not break adjacency
                    // on the page ladder (they never competed for it in v1).
                    return;
                }

                PageRef target;
                string destKey;
                if (!TryResolveDestination(rule, ruleId, out target, out destKey))
                {
                    // No show / unusable — still total: seat with null target, unique key.
                    destKey = "degraded:" + ruleId;
                    target = null;
                }

                // Cycle rules also produce a cycles[] entry (side effect of resolve).
                var summon = MigrateSummon(rule, ruleId);
                PlaceSummon(destKey, target, summon, show);
            }

            private bool TryResolveDestination(
                DisplayRule rule, string ruleId, out PageRef target, out string destKey)
            {
                target = null;
                destKey = null;
                var show = rule.Show;
                if (show == null)
                    return false;

                switch (show.Kind)
                {
                    case TargetKind.Page:
                        if (IsLegacyPageToken(show.PageRaw))
                        {
                            // Bare-Legacy → hosted from baseScreenId, or reserved placeholder.
                            target = ResolveLegacyHostedRef(_baseScreenId);
                            destKey = "hosted:" + target.Id;
                            return true;
                        }
                        // Catalog page id = v1 ItmPage token (including unknown raw).
                        string catalogId = show.PageRaw;
                        if (string.IsNullOrWhiteSpace(catalogId) && show.Page != null)
                            catalogId = EnumText.Write(show.Page.Value);
                        if (string.IsNullOrWhiteSpace(catalogId))
                            return false;
                        destKey = "itm:" + catalogId;
                        target = ItmPageRef(catalogId);
                        return true;

                    case TargetKind.SegmentScreen:
                        if (string.IsNullOrWhiteSpace(show.ScreenId))
                            return false;
                        string segmentId = EscapeReservedHostedPageId(show.ScreenId);
                        destKey = "hosted:" + segmentId;
                        target = HostedPageRef(segmentId);
                        return true;

                    case TargetKind.Cycle:
                    {
                        // One cycles[] entry per cycle rule (unique destination).
                        string cycleId = "c-" + ruleId;
                        var cycle = new CycleEntry
                        {
                            Id = cycleId,
                            PeriodMs = show.PeriodMs,
                            Members = new List<PageRef>(),
                        };
                        if (show.PagesRaw != null)
                        {
                            foreach (string pageRaw in show.PagesRaw)
                            {
                                if (string.IsNullOrWhiteSpace(pageRaw))
                                    continue;
                                if (IsLegacyPageToken(pageRaw))
                                    cycle.Members.Add(ResolveLegacyHostedRef(_baseScreenId));
                                else
                                    cycle.Members.Add(ItmPageRef(pageRaw));
                            }
                        }
                        _v2.Cycles.Add(cycle);
                        destKey = "cycle:" + cycleId;
                        target = new PageRef { Kind = PageRefKind.Cycle, Id = cycleId };
                        return true;
                    }

                    case TargetKind.Unknown:
                    default:
                        // Unknown show kind: unique degraded destination; best-effort
                        // PageRef from page/screenId tokens; complete payload → v1Show.
                        destKey = "unknown:" + ruleId;
                        target = new PageRef { KindRaw = show.KindRaw };
                        if (!string.IsNullOrWhiteSpace(show.PageRaw))
                            target.CatalogPageId = show.PageRaw;
                        else if (!string.IsNullOrWhiteSpace(show.ScreenId))
                            target.Id = show.ScreenId;
                        return true;
                }
            }

            private void PlaceSummon(
                string destKey, PageRef target, Summon summon, RuleTarget show)
            {
                IDictionary<string, JToken> showExt = BuildShowExtensionData(show);

                // Adjacent same-destination → merge into the open row (home or satellite).
                if (_lastDestKey != null
                    && string.Equals(_lastDestKey, destKey, StringComparison.Ordinal)
                    && _v2.Priority.Rows.Count > 0)
                {
                    var open = _v2.Priority.Rows[_v2.Priority.Rows.Count - 1];
                    if (open.Summons == null)
                        open.Summons = new List<Summon>();
                    open.Summons.Add(summon);
                    open.ExtensionData = MergeExtensionDataPrefixed(
                        open.ExtensionData, showExt, "show.");
                    return;
                }

                PriorityRow home;
                if (!_homes.TryGetValue(destKey, out home))
                {
                    // First rule for this destination → home seat.
                    home = new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s-" + (summon.Id ?? "missing"),
                        Target = target,
                        Summons = new List<Summon> { summon },
                        ExtensionData = showExt,
                    };
                    _homes[destKey] = home;
                    _v2.Priority.Rows.Add(home);
                    _lastDestKey = destKey;
                    return;
                }

                // Later, separated same-destination rule → satellite seat.
                var sat = new PriorityRow
                {
                    Kind = PriorityRowKind.Satellite,
                    Id = "sat-" + (summon.Id ?? "missing"),
                    Target = target,
                    Summons = new List<Summon> { summon },
                    ExtensionData = showExt,
                };
                _v2.Priority.Rows.Add(sat);
                _lastDestKey = destKey;
            }
        }

        /// <summary>
        /// Show-unknowns for the produced row, plus (for unknown kinds) the complete
        /// original show payload under the reserved key <see cref="V1ShowKey"/>.
        /// </summary>
        private static IDictionary<string, JToken> BuildShowExtensionData(RuleTarget show)
        {
            if (show == null)
                return null;

            IDictionary<string, JToken> ext = CopyExtensionData(show.ExtensionData);

            if (show.Kind == TargetKind.Unknown
                || (show.Kind != TargetKind.Page
                    && show.Kind != TargetKind.SegmentScreen
                    && show.Kind != TargetKind.Cycle
                    && show.Kind != TargetKind.Special))
            {
                // Reserved payload wins the bare key; a colliding show-ext member is
                // namespaced under show. so both values survive.
                var payload = CaptureShowPayload(show);
                if (ext != null && ext.ContainsKey(V1ShowKey))
                {
                    JToken displaced = ext[V1ShowKey];
                    ext.Remove(V1ShowKey);
                    string prefixed = "show." + V1ShowKey;
                    if (!ext.ContainsKey(prefixed))
                        ext[prefixed] = displaced;
                }
                if (ext == null)
                    ext = new Dictionary<string, JToken>();
                ext[V1ShowKey] = payload;
            }

            return ext;
        }

        /// <summary>Complete original show object as a JSON object (typed fields + ext).</summary>
        private static JObject CaptureShowPayload(RuleTarget show)
        {
            var o = new JObject();
            if (!string.IsNullOrWhiteSpace(show.KindRaw))
                o["kind"] = show.KindRaw;
            if (!string.IsNullOrWhiteSpace(show.PageRaw))
                o["page"] = show.PageRaw;
            if (!string.IsNullOrWhiteSpace(show.ScreenId))
                o["screenId"] = show.ScreenId;
            if (show.PagesRaw != null)
            {
                var pages = new JArray();
                foreach (string p in show.PagesRaw)
                    pages.Add(p);
                o["pages"] = pages;
            }
            // Always emit periodMs so cycle-shaped unknowns keep the authored value.
            o["periodMs"] = show.PeriodMs;
            if (!string.IsNullOrWhiteSpace(show.CommandRaw))
                o["command"] = show.CommandRaw;
            if (show.ExtensionData != null)
            {
                foreach (var kv in show.ExtensionData)
                {
                    if (kv.Key == null || o.ContainsKey(kv.Key))
                        continue;
                    o[kv.Key] = kv.Value != null ? kv.Value.DeepClone() : JValue.CreateNull();
                }
            }
            return o;
        }

        // ── Summon / condition / lifetime ────────────────────────────────────

        private static Summon MigrateSummon(DisplayRule rule, string ruleId)
        {
            var summon = new Summon
            {
                Id = ruleId,
                Name = rule.Name,
                Enabled = rule.Enabled,
                ExtensionData = CopyExtensionData(rule.ExtensionData),
            };

            // runs: preserve raw when present (including unknown spellings).
            if (!string.IsNullOrWhiteSpace(rule.EligibleRaw))
                summon.RunsRaw = rule.EligibleRaw;

            Condition condition;
            Lifetime lifetime;
            MigrateWhenAndHold(rule, out condition, out lifetime);
            summon.Condition = condition;
            summon.Lifetime = lifetime;
            return summon;
        }

        private static WheelScreenRule MigrateSpecialRule(DisplayRule rule, string ruleId)
        {
            var ws = new WheelScreenRule
            {
                Id = ruleId,
                Name = rule.Name,
                Enabled = rule.Enabled,
                // Rule unknowns + show unknowns land on the wheel-screen rule
                // (no seat row for specials).
                ExtensionData = MergeExtensionData(
                    CopyExtensionData(rule.ExtensionData),
                    rule.Show != null ? rule.Show.ExtensionData : null),
            };

            if (rule.Show != null)
            {
                // Preserve command raw spelling (logo / logoInverted / unknown).
                if (!string.IsNullOrWhiteSpace(rule.Show.CommandRaw))
                    ws.ScreenRaw = rule.Show.CommandRaw;
                else if (rule.Show.Command != SpecialCommand.Unknown)
                    ws.ScreenRaw = SpecialCommands.Write(rule.Show.Command);
            }

            if (!string.IsNullOrWhiteSpace(rule.EligibleRaw))
                ws.RunsRaw = rule.EligibleRaw;

            Condition condition;
            Lifetime lifetime;
            MigrateWhenAndHold(rule, out condition, out lifetime);
            ws.Condition = condition;
            ws.Lifetime = lifetime;
            return ws;
        }

        private static void MigrateWhenAndHold(
            DisplayRule rule, out Condition condition, out Lifetime lifetime)
        {
            condition = null;
            lifetime = null;
            var when = rule.When;
            var hold = rule.Hold;

            bool isEdge = when != null && when.Kind.IsEdge();
            bool isEvent = when != null && when.Kind.IsEvent();

            if (when != null)
            {
                condition = new Condition
                {
                    ExtensionData = CopyExtensionData(when.ExtensionData),
                };

                if (isEvent)
                {
                    // actionTriggered → source.kind action + onChange (no operator).
                    condition.Source = new ValueSource
                    {
                        Kind = ValueSourceKind.Action,
                        Name = when.Source != null ? when.Source.Name : null,
                        ExtensionData = when.Source != null
                            ? CopyExtensionData(when.Source.ExtensionData)
                            : null,
                    };
                }
                else if (isEdge)
                {
                    // Edge-ness moves to lifetime; condition keeps source only.
                    if (when.Source != null)
                        condition.Source = MigrateValueSource(when.Source);
                }
                else
                {
                    // Level (or unknown) kinds: operator = when.kind raw; source carried.
                    if (when.Source != null)
                        condition.Source = MigrateValueSource(when.Source);

                    if (!string.IsNullOrWhiteSpace(when.KindRaw))
                        condition.OperatorRaw = when.KindRaw;
                    else if (when.Kind != ConditionKind.Unknown)
                        condition.OperatorRaw = EnumText.Write(when.Kind);

                    condition.Value = when.Value;
                    // Hysteresis carried (level only in v1; already stripped on edges by Normalize).
                    condition.Hysteresis = when.Hysteresis;
                }
            }

            if (isEdge || isEvent)
            {
                lifetime = MigrateEdgeOrEventLifetime(when, hold);
            }
            else
            {
                lifetime = MigrateLevelLifetime(hold);
            }
        }

        private static Lifetime MigrateLevelLifetime(HoldSpec hold)
        {
            if (hold == null)
                return null;

            var life = new Lifetime
            {
                ExtensionData = CopyExtensionData(hold.ExtensionData),
            };

            // Unknown / future hold spellings: Kind may be runtime-coerced (ForDuration
            // etc.) while KindRaw still holds the original text — preserve the raw.
            if (!string.IsNullOrWhiteSpace(hold.KindRaw)
                && EnumText.Parse(hold.KindRaw, HoldKind.Unknown) == HoldKind.Unknown)
            {
                life.KindRaw = hold.KindRaw;
                if (hold.DurationMs != HoldSpec.DefaultDurationMs)
                    life.DurationMs = hold.DurationMs;
                return life;
            }

            switch (hold.Kind)
            {
                case HoldKind.WhileActive:
                    // whileActive → whileTrue (rename).
                    life.Kind = LifetimeKind.WhileTrue;
                    break;
                case HoldKind.ForDuration:
                    life.Kind = LifetimeKind.ForDuration;
                    // Only author durationMs when non-default so absent-default round-trips
                    // match v1's DefaultValue suppression (v1 has no presence bit).
                    if (hold.DurationMs != HoldSpec.DefaultDurationMs)
                        life.DurationMs = hold.DurationMs;
                    break;
                case HoldKind.UntilDismissed:
                    life.Kind = LifetimeKind.UntilDismissed;
                    break;
                case HoldKind.Unknown:
                default:
                    if (!string.IsNullOrWhiteSpace(hold.KindRaw))
                        life.KindRaw = hold.KindRaw;
                    else
                        life.Kind = LifetimeKind.WhileTrue;
                    if (hold.DurationMs != HoldSpec.DefaultDurationMs)
                        life.DurationMs = hold.DurationMs;
                    break;
            }

            return life;
        }

        private static Lifetime MigrateEdgeOrEventLifetime(RuleCondition when, HoldSpec hold)
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                ExtensionData = hold != null ? CopyExtensionData(hold.ExtensionData) : null,
            };

            // Direction from edge kind (changes = any / suppressed).
            if (when != null)
            {
                if (when.Kind == ConditionKind.Increases)
                    life.Direction = ChangeDirection.Up;
                else if (when.Kind == ConditionKind.Decreases)
                    life.Direction = ChangeDirection.Down;
                // Changes → any (default, suppressed on write).
            }

            if (hold != null)
            {
                // untilDismissed → then (mutually exclusive with durationMs).
                // Use Kind (post-Normalize) so coerced whileActive→forDuration is honored;
                // also honor raw untilDismissed if Kind is Unknown but raw says so.
                bool untilDismissed = hold.Kind == HoldKind.UntilDismissed
                    || string.Equals(hold.KindRaw, "untilDismissed",
                        StringComparison.OrdinalIgnoreCase);

                if (untilDismissed)
                {
                    life.Then = LifetimeThen.UntilDismissed;
                }
                else if (hold.DurationMs != HoldSpec.DefaultDurationMs)
                {
                    life.DurationMs = hold.DurationMs;
                }
                // else: absent durationMs → runtime default 5000 (matches v1 suppression).

                // Coerced/unknown hold spellings re-home into onChange; preserve the
                // original spelling under the reserved extension key.
                if (ShouldPreserveV1HoldKind(hold))
                {
                    IDictionary<string, JToken> ext = life.ExtensionData;
                    if (ext == null)
                        ext = new Dictionary<string, JToken>();
                    ext[V1HoldKindKey] = new JValue(hold.KindRaw);
                    life.ExtensionData = ext;
                }
            }

            return life;
        }

        /// <summary>
        /// True when the v1 hold spelling cannot be recovered from the onChange shape alone
        /// (unknown/future kinds, or whileActive which edge/event paths re-home to duration).
        /// </summary>
        private static bool ShouldPreserveV1HoldKind(HoldSpec hold)
        {
            if (hold == null || string.IsNullOrWhiteSpace(hold.KindRaw))
                return false;

            // Clean onChange mappings: forDuration → durationMs, untilDismissed → then.
            if (string.Equals(hold.KindRaw, "forDuration", StringComparison.OrdinalIgnoreCase)
                || string.Equals(hold.KindRaw, "untilDismissed", StringComparison.OrdinalIgnoreCase))
                return false;

            // whileActive (if still spelled — pre-normalize or if Kind was only coerced)
            // and any unrecognized spelling need the reserved key.
            return true;
        }

        // ── Value sources ────────────────────────────────────────────────────

        private static ValueSource MigrateValueSource(PropertySpec source)
        {
            if (source == null)
                return null;

            var vs = new ValueSource
            {
                Name = source.Name,
                ExtensionData = CopyExtensionData(source.ExtensionData),
            };

            // Source kind spellings carried VERBATIM (builtIn / simHubProperty / …).
            if (!string.IsNullOrWhiteSpace(source.KindRaw))
                vs.KindRaw = source.KindRaw;
            else if (source.Kind != PropertyKind.Unknown)
                vs.KindRaw = EnumText.Write(source.Kind);

            return vs;
        }

        // ── Page refs / tokens ───────────────────────────────────────────────

        private static PageRef ItmPageRef(string catalogPageId)
            => new PageRef
            {
                Kind = PageRefKind.ItmPage,
                CatalogPageId = catalogPageId,
            };

        private static PageRef HostedPageRef(string id)
            => new PageRef
            {
                Kind = PageRefKind.HostedPage,
                Id = id,
            };

        /// <summary>
        /// Bare Legacy → hosted page from <paramref name="baseScreenId"/> when present
        /// (namespace-escaped if it collides with the reserved prefix); otherwise the
        /// reserved degraded placeholder <see cref="BareLegacyHostedPageId"/>.
        /// </summary>
        private static PageRef ResolveLegacyHostedRef(string baseScreenId)
            => HostedPageRef(
                !string.IsNullOrWhiteSpace(baseScreenId)
                    ? EscapeReservedHostedPageId(baseScreenId)
                    : BareLegacyHostedPageId);

        private static bool IsLegacyPageToken(string raw)
            => string.Equals(raw, "legacy", StringComparison.OrdinalIgnoreCase);

        // ── Rule id allocator ────────────────────────────────────────────────

        /// <summary>
        /// Deterministic occurrence-based ids for missing/duplicate v1 rule ids.
        /// First valid id is kept; later duplicates and blanks get stable synthesized
        /// ids. Never mutates the v1 document.
        /// </summary>
        private sealed class RuleIdAllocator
        {
            private readonly HashSet<string> _claimed =
                new HashSet<string>(StringComparer.Ordinal);
            private int _occurrence;

            public string Allocate(string v1Id)
            {
                _occurrence++;
                if (!string.IsNullOrWhiteSpace(v1Id) && _claimed.Add(v1Id))
                    return v1Id;

                // Occurrence-based synthesis; ensure uniqueness against claimed ids.
                string synth;
                int n = _occurrence;
                do
                {
                    synth = "r-v1-" + n;
                    n++;
                }
                while (!_claimed.Add(synth));
                return synth;
            }
        }

        // ── Extension data ───────────────────────────────────────────────────

        private static IDictionary<string, JToken> CopyExtensionData(
            IDictionary<string, JToken> source)
        {
            if (source == null || source.Count == 0)
                return null;

            var copy = new Dictionary<string, JToken>();
            foreach (var kv in source)
            {
                if (kv.Key == null)
                    continue;
                copy[kv.Key] = kv.Value != null ? kv.Value.DeepClone() : null;
            }
            return copy.Count > 0 ? copy : null;
        }

        /// <summary>
        /// Merges extension-data bags left-to-right without namespacing (earlier keys win).
        /// Used only for non-colliding destinations (wheel-screen rule = rule+show).
        /// </summary>
        private static IDictionary<string, JToken> MergeExtensionData(
            IDictionary<string, JToken> first,
            IDictionary<string, JToken> second,
            IDictionary<string, JToken> third = null)
        {
            IDictionary<string, JToken> result = first;
            result = MergeTwo(result, second, null);
            result = MergeTwo(result, third, null);
            return result;
        }

        /// <summary>
        /// Merges bags left-to-right; on collision the later source keeps its value under
        /// the deterministic source-path prefix (every value preserved).
        /// </summary>
        private static IDictionary<string, JToken> MergeExtensionDataPrefixed(
            IDictionary<string, JToken> first,
            IDictionary<string, JToken> second, string secondPrefix,
            IDictionary<string, JToken> third = null, string thirdPrefix = null)
        {
            IDictionary<string, JToken> result = first;
            result = MergeTwo(result, second, secondPrefix);
            result = MergeTwo(result, third, thirdPrefix);
            return result;
        }

        /// <summary>
        /// Two-bag merge. When <paramref name="collisionPrefix"/> is null, earlier wins
        /// (incoming dropped). When non-null, colliding keys land at prefix+key.
        /// </summary>
        private static IDictionary<string, JToken> MergeTwo(
            IDictionary<string, JToken> into,
            IDictionary<string, JToken> add,
            string collisionPrefix)
        {
            if (add == null || add.Count == 0)
                return into;
            if (into == null)
                return CopyExtensionData(add);

            foreach (var kv in add)
            {
                if (kv.Key == null)
                    continue;
                JToken value = kv.Value != null ? kv.Value.DeepClone() : null;
                if (!into.ContainsKey(kv.Key))
                {
                    into[kv.Key] = value;
                }
                else if (!string.IsNullOrEmpty(collisionPrefix))
                {
                    string prefixed = collisionPrefix + kv.Key;
                    // If the prefixed key is also taken, keep looking for a free slot
                    // by repeating the prefix (deterministic, rare).
                    while (into.ContainsKey(prefixed))
                        prefixed = collisionPrefix + prefixed;
                    into[prefixed] = value;
                }
                // else: earlier-wins drop (legacy merge for non-prefixed destinations)
            }
            return into;
        }
    }
}
