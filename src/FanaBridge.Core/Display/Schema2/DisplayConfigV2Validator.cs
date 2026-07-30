using System;
using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Load-time validation and normalization for <see cref="DisplayConfigV2"/>
    /// (spec-schema-v2 §14): warn-and-degrade, never throw, never drop data, and never
    /// rewrite persisted members, except for the required standing manual row: when it
    /// is absent, Normalize restores it into the survivor document above the rest floor.
    /// All coercions and clamps are runtime-only (<c>DegradedAtLoad</c> / <c>Coerce*</c> /
    /// effective accessors); apart from the standing-row repair, load→save is
    /// byte-identical.
    ///
    /// Optional <see cref="WheelCatalog"/> enables the capability matrix; when absent,
    /// capability rules are skipped entirely.
    /// </summary>
    public static class DisplayConfigV2Validator
    {
        /// <summary>Firmware subscription cap used for the authoring-time budget warn.</summary>
        public const int SubscriptionBudget = 16;

        /// <summary>
        /// Validates and normalizes <paramref name="config"/> in place (runtime marks
        /// only), reporting every degradation through <paramref name="log"/>. Returns the
        /// same instance; a null input yields a fresh default document. Never throws.
        /// </summary>
        public static DisplayConfigV2 Normalize(
            DisplayConfigV2 config, Action<string> log, WheelCatalog catalog = null)
        {
            Action<string> warn = m =>
            {
                if (log == null) return;
                try { log("DisplayConfigV2: " + m); }
                catch { /* logger failures must not surface */ }
            };

            if (config == null)
                config = new DisplayConfigV2();

            // Schema version: warn only — never rewrite the stored number.
            if (config.SchemaVersion <= 0)
            {
                warn("missing schema version — treating as "
                    + DisplayConfigV2.CurrentSchemaVersion);
            }
            else if (config.SchemaVersion > DisplayConfigV2.CurrentSchemaVersion)
            {
                warn("config uses newer schema version " + config.SchemaVersion
                    + " (current is " + DisplayConfigV2.CurrentSchemaVersion
                    + ") — this build may not honor all of it");
            }

            // Identity indexes (first-wins survivors).
            var hostedPageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itmCatalogIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removedItmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cycleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playlistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var summonIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var overrideIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wheelRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Flagged children for materialization: (targetKey, synthetic seat id hint).
            var flaggedHostsNeedingSeat = new List<FlaggedHost>();

            NormalizePages(config, hostedPageIds, itmCatalogIds, removedItmIds,
                layerIds, summonIds, overrideIds, flaggedHostsNeedingSeat, catalog, warn);

            NormalizeCycles(config, cycleIds, hostedPageIds, itmCatalogIds, catalog, warn);

            NormalizePlaylists(config, playlistIds, hostedPageIds, itmCatalogIds, catalog, warn);

            // Shared ladder first (S1 authority): shared override ids register before
            // page-scoped fields so a same-id child on the inert side degrades as the
            // duplicate, never the shared one. Both document-order directions hold.
            NormalizeSharedFields(config, overrideIds, flaggedHostsNeedingSeat,
                removedItmIds, catalog, warn);

            NormalizeFields(config, overrideIds, flaggedHostsNeedingSeat,
                hostedPageIds, itmCatalogIds, removedItmIds, catalog, warn);

            NormalizePriority(config, rowIds, summonIds, hostedPageIds, itmCatalogIds,
                cycleIds, playlistIds, removedItmIds, catalog, flaggedHostsNeedingSeat, warn);

            // Second pass: childRef satellites need the finished page/field identity maps.
            ResolveChildRefSatellites(config, catalog, warn);

            NormalizePageOrder(config, hostedPageIds, itmCatalogIds, removedItmIds,
                catalog, warn);

            NormalizeWheelScreen(config, wheelRuleIds, catalog, warn);

            NormalizeSettings(config, warn);

            if (catalog != null)
                MaybeWarnSubscriptionBudget(config, warn);

            return config;
        }

        // ── Pages / layers ────────────────────────────────────────────────

        private static void NormalizePages(
            DisplayConfigV2 config,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> removedItmIds,
            HashSet<string> layerIds,
            HashSet<string> summonIds,
            HashSet<string> overrideIds,
            List<FlaggedHost> flaggedHostsNeedingSeat,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.Pages == null)
                return;

            foreach (var page in config.Pages)
            {
                if (page == null)
                    continue;

                switch (page.Kind)
                {
                    case PageEntryKind.HostedPage:
                        if (string.IsNullOrWhiteSpace(page.Id))
                        {
                            page.DegradedAtLoad = true;
                            warn("hosted page '" + (page.Name ?? "?") + "' degraded — no id");
                        }
                        else if (IsReservedRuntimeCarrierId(page.Id))
                        {
                            page.DegradedAtLoad = true;
                            warn("hosted page id '" + page.Id
                                + "' is a reserved runtime id — degraded");
                        }
                        else if (!hostedPageIds.Add(page.Id))
                        {
                            page.DegradedAtLoad = true;
                            warn("duplicate hosted page id '" + page.Id
                                + "' — keeping the first");
                        }
                        NormalizeContentWithEffect(page.Base, "hosted page '" + (page.Id ?? "?") + "' base", warn);
                        if (page.Layers != null)
                        {
                            foreach (var layer in page.Layers)
                            {
                                if (layer == null)
                                    continue;
                                NormalizeLayer(layer, page, layerIds, warn);
                                if (layer.ActsAsEntrypoint && !layer.ActsAsEntrypointIgnored
                                    && !page.DegradedAtLoad
                                    && !string.IsNullOrWhiteSpace(page.Id))
                                {
                                    flaggedHostsNeedingSeat.Add(new FlaggedHost
                                    {
                                        TargetKey = TargetKeyHosted(page.Id),
                                        HostedPageId = page.Id,
                                        SourceLabel = "layer '" + (layer.Id ?? "?") + "'",
                                    });
                                }
                            }
                        }
                        break;

                    case PageEntryKind.ItmPage:
                        if (string.IsNullOrWhiteSpace(page.CatalogPageId))
                        {
                            page.DegradedAtLoad = true;
                            warn("itm page entry degraded — no catalogPageId");
                        }
                        else if (!itmCatalogIds.Add(page.CatalogPageId))
                        {
                            page.DegradedAtLoad = true;
                            warn("duplicate itm page catalogPageId '" + page.CatalogPageId
                                + "' — keeping the first");
                        }
                        else
                        {
                            // pages[] itm entries are overlays on the catalog roster —
                            // they never mint identities. With a catalog, unknown ids
                            // degrade; resolution uses CatalogHasPage exclusively.
                            if (catalog != null && !CatalogHasPage(catalog, page.CatalogPageId))
                            {
                                page.DegradedAtLoad = true;
                                warn("itm page overlay '" + page.CatalogPageId
                                    + "' degraded — not in catalog roster");
                            }
                            if (page.Removed)
                                removedItmIds.Add(page.CatalogPageId);
                        }
                        break;

                    default:
                        page.DegradedAtLoad = true;
                        warn("page entry degraded — "
                            + (page.KindRaw == null ? "no kind"
                                : "unrecognized kind '" + page.KindRaw + "'"));
                        break;
                }
            }
        }

        private static void NormalizeLayer(
            LayerEntry layer, PageEntry hostPage, HashSet<string> layerIds, Action<string> warn)
        {
            string label = "layer '" + (layer.Id ?? layer.Name ?? "?") + "'";

            if (string.IsNullOrWhiteSpace(layer.Id))
            {
                layer.DegradedAtLoad = true;
                warn(label + " degraded — no id");
            }
            else if (IsReservedRuntimeCarrierId(layer.Id))
            {
                layer.DegradedAtLoad = true;
                warn(label + " degraded — id is a reserved runtime id");
            }
            else if (!layerIds.Add(layer.Id))
            {
                layer.DegradedAtLoad = true;
                warn("duplicate layer id '" + layer.Id + "' — keeping the first");
            }

            NormalizeContentObject(layer.Content, label, warn);
            NormalizeEffect(layer.Effect, layer.EffectRaw,
                e => layer.CoerceEffect(ContentEffect.Blink),
                label, warn, () => layer.DegradedAtLoad = true);
            NormalizeRuns(layer.Runs, layer.RunsRaw, label, warn,
                () => layer.DegradedAtLoad = true);

            NormalizeConditionLifetimePair(
                layer.Condition, layer.Lifetime,
                isFieldOverride: false,
                isFlaggedChild: layer.ActsAsEntrypoint,
                allowUntilDismissed: layer.ActsAsEntrypoint,
                bringUpDomain: false,
                label, warn,
                degrade: () => layer.DegradedAtLoad = true);

            // Unflagged untilDismissed/then already coerced inside pair helper.
            if (layer.ActsAsEntrypoint && hostPage != null && hostPage.Removed)
            {
                layer.ActsAsEntrypointIgnored = true;
                warn(label + ": bring-up flag inert — host page is removed");
            }
        }

        // ── Cycles ────────────────────────────────────────────────────────

        private static void NormalizeCycles(
            DisplayConfigV2 config,
            HashSet<string> cycleIds,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.Cycles == null)
                return;

            foreach (var cycle in config.Cycles)
            {
                if (cycle == null)
                    continue;

                string label = "cycle '" + (cycle.Id ?? cycle.Name ?? "?") + "'";

                if (string.IsNullOrWhiteSpace(cycle.Id))
                {
                    cycle.DegradedAtLoad = true;
                    warn(label + " degraded — no id");
                }
                else if (!cycleIds.Add(cycle.Id))
                {
                    cycle.DegradedAtLoad = true;
                    warn("duplicate cycle id '" + cycle.Id + "' — keeping the first");
                }

                int resolvable = 0;
                if (cycle.Members != null)
                {
                    foreach (var member in cycle.Members)
                    {
                        if (member == null)
                            continue;
                        if (!IsResolvablePageMember(member, hostedPageIds, itmCatalogIds, catalog,
                            allowCycle: false, out string reason))
                        {
                            member.DegradedAtLoad = true;
                            warn(label + ": member degraded — " + reason);
                        }
                        else
                        {
                            resolvable++;
                        }
                    }
                }

                if (resolvable < 2)
                {
                    cycle.DegradedAtLoad = true;
                    warn(label + " degraded — fewer than 2 resolvable members");
                }
            }
        }

        // ── Fields / overrides ────────────────────────────────────────────

        private static void NormalizeFields(
            DisplayConfigV2 config,
            HashSet<string> overrideIds,
            List<FlaggedHost> flaggedHostsNeedingSeat,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> removedItmIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.Fields == null)
                return;

            // Document encounter order. Newtonsoft populates Dictionary in JSON key
            // order; .NET Dictionary enumeration preserves insertion order for the
            // lifetime without removals (verified on net48). Do not re-sort by key.
            foreach (var kv in config.Fields)
            {
                var field = kv.Value;
                if (field == null)
                    continue;

                ushort paramId = kv.Key;
                string fieldLabel = "field " + paramId;
                NormalizeFieldEntry(field, paramId, fieldLabel, overrideIds,
                    flaggedHostsNeedingSeat, catalog, removedItmIds, warn);
            }
        }

        /// <summary>
        /// sharedFields[logicalId] — one FieldEntry per logical field. Without a catalog
        /// every entry is kept, inert, and warned once (S5). Unknown logical ids same
        /// treatment. Colliding fields[paramId] is the named inert side (S1: shared wins).
        /// </summary>
        private static void NormalizeSharedFields(
            DisplayConfigV2 config,
            HashSet<string> overrideIds,
            List<FlaggedHost> flaggedHostsNeedingSeat,
            HashSet<string> removedItmIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.SharedFields == null)
                return;

            // Track which params sharedFields claims (first shared wins among shared).
            var sharedParamOwners = new Dictionary<ushort, string>();
            bool warnedNoCatalog = false;

            foreach (var kv in config.SharedFields)
            {
                string logicalId = kv.Key;
                var entry = kv.Value;
                if (entry == null)
                    continue;

                string label = "sharedField '" + (logicalId ?? "?") + "'";

                if (string.IsNullOrWhiteSpace(logicalId))
                {
                    entry.DegradedAtLoad = true;
                    entry.DegradeReason = "empty logical id";
                    warn(label + " degraded — empty logical id");
                    continue;
                }

                if (catalog == null)
                {
                    // S5: no catalog → no logicalId→paramId binding. Kept, inert, warn once.
                    entry.DegradedAtLoad = true;
                    entry.DegradeReason = "no catalog — shared field inert";
                    if (!warnedNoCatalog)
                    {
                        warnedNoCatalog = true;
                        warn("sharedFields present but no catalog — entries kept inert "
                            + "(never resolved by guess)");
                    }
                    continue;
                }

                var def = CatalogFields.FindDefinition(catalog, logicalId);
                if (def == null)
                {
                    // Unknown logical id — survivors: kept, inert, warn once per id.
                    entry.DegradedAtLoad = true;
                    entry.DegradeReason = "unknown logical id '" + logicalId + "'";
                    warn(label + " degraded — unknown logical id on this wheel (inert)");
                    continue;
                }

                ushort paramId = def.ParamId;
                if (sharedParamOwners.ContainsKey(paramId))
                {
                    // Two sharedFields entries bind the same param — first wins.
                    entry.DegradedAtLoad = true;
                    entry.DegradeReason = "param " + paramId + " already addressed by shared field '"
                        + sharedParamOwners[paramId] + "'";
                    warn(label + " degraded — param " + paramId
                        + " already addressed by shared field '"
                        + sharedParamOwners[paramId] + "'");
                    continue;
                }
                sharedParamOwners[paramId] = logicalId;

                NormalizeFieldEntry(entry, paramId, label, overrideIds,
                    flaggedHostsNeedingSeat, catalog, removedItmIds, warn);

                // S1: sharedFields wins — mark the colliding fields[paramId] as named inert.
                if (config.Fields != null
                    && config.Fields.TryGetValue(paramId, out var colliding)
                    && colliding != null)
                {
                    colliding.DegradedAtLoad = true;
                    colliding.DegradeReason = "addressed by shared field '" + logicalId + "'";
                    warn("field " + paramId + " degraded — addressed by shared field '"
                        + logicalId + "' (sharedFields wins; fields entry kept inert)");
                }
            }

            // Placement integrity: every placement's logical id should resolve (degraded note).
            WarnUnresolvedPlacements(catalog, warn);
        }

        private static void WarnUnresolvedPlacements(WheelCatalog catalog, Action<string> warn)
        {
            if (catalog?.Itm?.Pages == null)
                return;
            var defs = CatalogFields.IndexByLogicalId(catalog);
            var warned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Placements == null)
                    continue;
                foreach (var pl in page.Placements)
                {
                    if (pl == null || string.IsNullOrEmpty(pl.Field))
                        continue;
                    if (defs.ContainsKey(pl.Field))
                        continue;
                    if (!warned.Add(pl.Field))
                        continue;
                    warn("catalog placement field '" + pl.Field + "' on page '"
                        + (page.Id ?? "?") + "' does not resolve to a definition");
                }
            }
        }

        private static void NormalizeFieldEntry(
            FieldEntry field,
            ushort paramId,
            string fieldLabel,
            HashSet<string> overrideIds,
            List<FlaggedHost> flaggedHostsNeedingSeat,
            WheelCatalog catalog,
            HashSet<string> removedItmIds,
            Action<string> warn)
        {
            if (field.Base != null)
                NormalizeValueSource(field.Base.Source, SourceSite.FieldBase,
                    fieldLabel + " base", warn, degradeCarrier: null);

            if (field.Overrides == null)
                return;

            foreach (var ov in field.Overrides)
            {
                if (ov == null)
                    continue;
                NormalizeOverride(ov, paramId, overrideIds, catalog, removedItmIds, warn);

                // Inert ladders (e.g. fields side after shared collision) still run id
                // uniqueness above; they must not materialize seats.
                if (!field.DegradedAtLoad
                    && ov.ActsAsEntrypoint && !ov.ActsAsEntrypointIgnored && !ov.DegradedAtLoad)
                {
                    // Host resolution needs catalog primaryHost; without catalog, skip
                    // materialization for field overrides (layers still materialize).
                    string hostCatalogId = ResolvePrimaryHostCatalogId(catalog, paramId);
                    if (hostCatalogId != null)
                    {
                        flaggedHostsNeedingSeat.Add(new FlaggedHost
                        {
                            TargetKey = TargetKeyItm(hostCatalogId),
                            ItmCatalogPageId = hostCatalogId,
                            SourceLabel = "override '" + (ov.Id ?? "?") + "' on " + fieldLabel,
                        });
                    }
                }
            }
        }

        private static void NormalizeOverride(
            FieldOverride ov, ushort paramId, HashSet<string> overrideIds,
            WheelCatalog catalog, HashSet<string> removedItmIds, Action<string> warn)
        {
            string label = "field " + paramId + " override '" + (ov.Id ?? "?") + "'";

            if (string.IsNullOrWhiteSpace(ov.Id))
            {
                ov.DegradedAtLoad = true;
                warn(label + " degraded — no id");
            }
            else if (IsReservedRuntimeCarrierId(ov.Id))
            {
                ov.DegradedAtLoad = true;
                warn(label + " degraded — id is a reserved runtime id");
            }
            else if (!overrideIds.Add(ov.Id))
            {
                ov.DegradedAtLoad = true;
                warn("duplicate override id '" + ov.Id + "' — keeping the first");
            }

            NormalizeContentObject(ov.Content, label, warn);
            NormalizeEffect(ov.Effect, ov.EffectRaw,
                e => ov.CoerceEffect(ContentEffect.Blink), label, warn,
                () => ov.DegradedAtLoad = true);
            NormalizeRuns(ov.Runs, ov.RunsRaw, label, warn,
                () => ov.DegradedAtLoad = true);

            if (ov.Writes == FieldWrites.Unknown)
            {
                ov.DegradedAtLoad = true;
                warn(label + " degraded — "
                    + (string.IsNullOrWhiteSpace(ov.WritesRaw) ? "no writes"
                        : "unrecognized writes '" + ov.WritesRaw + "'"));
            }

            if (ov.Alignment == FieldAlignment.Unknown
                && !string.IsNullOrWhiteSpace(ov.AlignmentRaw))
            {
                ov.DegradedAtLoad = true;
                warn(label + " degraded — unrecognized alignment '"
                    + ov.AlignmentRaw + "'");
            }

            NormalizeConditionLifetimePair(
                ov.Condition, ov.Lifetime,
                isFieldOverride: true,
                isFlaggedChild: ov.ActsAsEntrypoint,
                allowUntilDismissed: ov.ActsAsEntrypoint,
                bringUpDomain: false,
                label, warn,
                degrade: () => ov.DegradedAtLoad = true);

            if (catalog == null)
                return;

            // Capability matrix (tri-state: true / false / null=untested).
            var catField = FindCatalogField(catalog, paramId);
            if (ov.Writes == FieldWrites.Suffix || ov.Writes == FieldWrites.Both)
            {
                bool? suffixOk = catField?.Suffix?.Supported;
                if (suffixOk == false)
                {
                    ov.DegradedAtLoad = true;
                    warn(label + " degraded — suffix write on a no-suffix field");
                }
                else if (suffixOk == null)
                {
                    warn(label + ": suffix capability is untested (null) — not gated");
                }
            }

            if ((ov.Writes == FieldWrites.Value || ov.Writes == FieldWrites.Both)
                && ov.Content != null
                && (ov.Content.Kind == ContentKind.Text || ov.Content.Kind == ContentKind.Message))
            {
                bool? asciiOk = catField?.Value?.Ascii;
                if (asciiOk == false)
                {
                    ov.DegradedAtLoad = true;
                    warn(label + " degraded — text content in a non-ascii value region");
                }
                else if (asciiOk == null)
                {
                    warn(label + ": value.ascii capability is untested (null) — not gated");
                }
            }

            if (ov.ActsAsEntrypoint)
            {
                int primaryCount = CountPrimaryHosts(catalog, paramId);
                if (primaryCount != 1)
                {
                    ov.ActsAsEntrypointIgnored = true;
                    warn(label + ": bring-up flag inert — param has " + primaryCount
                        + " primaryHost designation(s) (need exactly one)");
                }
                else
                {
                    string hostId = ResolvePrimaryHostCatalogId(catalog, paramId);
                    if (hostId != null && removedItmIds.Contains(hostId))
                    {
                        ov.ActsAsEntrypointIgnored = true;
                        warn(label + ": bring-up flag inert — resolved host page is removed");
                    }
                }
            }
        }

        // ── Priority ladder ───────────────────────────────────────────────

        private static void NormalizePriority(
            DisplayConfigV2 config,
            HashSet<string> rowIds,
            HashSet<string> summonIds,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> cycleIds,
            HashSet<string> playlistIds,
            HashSet<string> removedItmIds,
            WheelCatalog catalog,
            List<FlaggedHost> flaggedHostsNeedingSeat,
            Action<string> warn)
        {
            // Priority / Rest authored nulls stay null. Rows normally preserve authored
            // survivors; the standing exception is a missing Manual row, which must be
            // added to the normalized document so clones/composition retain its seat.
            if (config.Priority == null)
                return;

            var homeSeatsByTarget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingSeatTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PriorityRow firstManual = null;
            var runtime = new List<PriorityRow>();

            // Pass 1: validate document rows; build runtime list (no materialization yet).
            // Rows may be null (explicit JSON null) — treat as empty storage, do not assign.
            IList<PriorityRow> storedRows = config.Priority.Rows;
            if (storedRows != null)
            {
                foreach (var row in storedRows)
                {
                    if (row == null)
                        continue;

                    NormalizePriorityRow(row, rowIds, summonIds, homeSeatsByTarget,
                        hostedPageIds, itmCatalogIds, cycleIds, catalog, warn);

                    if (row.Kind == PriorityRowKind.Seat && !row.DegradedAtLoad)
                    {
                        string key = TargetKey(row.Target);
                        if (key != null)
                            existingSeatTargets.Add(key);
                    }

                    if (row.Kind == PriorityRowKind.Manual)
                    {
                        if (firstManual == null)
                            firstManual = row;
                        // All manuals still appear in runtime (degraded extras stay visible).
                    }

                    runtime.Add(row);
                }

                ValidateSplitOrigins(storedRows, warn);
            }

            // Manual restoration FIRST. Unlike derived home-seat materialization, this
            // required standing survivor belongs to the normalized document.
            if (firstManual == null)
            {
                firstManual = new PriorityRow
                {
                    Kind = PriorityRowKind.Manual,
                    MaterializedAtLoad = true,
                };
                if (config.Priority.Rows == null)
                    config.Priority.Rows = new List<PriorityRow>();
                config.Priority.Rows.Add(firstManual);
                runtime.Add(firstManual);
                warn("missing manual row — restored above rest");
            }

            int manualIndex = runtime.IndexOf(firstManual);

            // Seat materialization: flagged children whose host has no seat, above manual,
            // document encounter order already captured in flaggedHostsNeedingSeat.
            var materialize = new List<PriorityRow>();
            var seenMaterialize = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fh in flaggedHostsNeedingSeat)
            {
                if (fh.TargetKey == null || existingSeatTargets.Contains(fh.TargetKey))
                    continue;
                if (!seenMaterialize.Add(fh.TargetKey))
                    continue;

                var seat = new PriorityRow
                {
                    Kind = PriorityRowKind.Seat,
                    Id = "materialized-" + fh.TargetKey,
                    MaterializedAtLoad = true,
                    Target = fh.HostedPageId != null
                        ? new PageRef { Kind = PageRefKind.HostedPage, Id = fh.HostedPageId }
                        : new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = fh.ItmCatalogPageId },
                };
                materialize.Add(seat);
                existingSeatTargets.Add(fh.TargetKey);
                warn("materialized home seat for " + fh.SourceLabel
                    + " host (runtime view only, above manual)");
            }

            if (materialize.Count > 0)
            {
                // Insert immediately above the manual row's current index.
                runtime.InsertRange(manualIndex, materialize);
            }

            config.Priority.RuntimeRows = runtime;

            // Rest block refs — leave Rest null when authored null.
            if (config.Priority.Rest != null)
                NormalizeRest(config.Priority.Rest, hostedPageIds, itmCatalogIds,
                    playlistIds, catalog, warn);
        }

        private static void ValidateSplitOrigins(
            IList<PriorityRow> rows, Action<string> warn)
        {
            foreach (var row in rows)
            {
                if (row?.SplitOrigin == null)
                    continue;

                string label = "priority row '" + (row.Id ?? row.KindRaw ?? "?") + "'";
                bool summonSatellite = row.Kind == PriorityRowKind.Satellite
                    && row.ChildRef == null
                    && row.Summons != null
                    && row.Summons.Count > 0;
                if (!summonSatellite)
                {
                    row.DegradedAtLoad = true;
                    warn(label + " degraded — splitOrigin is only valid on a summon satellite");
                    continue;
                }

                var origin = row.SplitOrigin;
                if (string.IsNullOrWhiteSpace(origin.RowId)
                    || !origin.SummonIndex.HasValue
                    || origin.SummonIndex.Value < 0)
                {
                    row.DegradedAtLoad = true;
                    warn(label + " degraded — splitOrigin is incomplete");
                    continue;
                }

                PriorityRow home = null;
                foreach (var candidate in rows)
                {
                    if (candidate != null
                        && candidate.Kind == PriorityRowKind.Seat
                        && string.Equals(
                            candidate.Id, origin.RowId, StringComparison.Ordinal))
                    {
                        home = candidate;
                        break;
                    }
                }

                if (home == null
                    || !string.Equals(
                        TargetKey(home.Target), TargetKey(row.Target),
                        StringComparison.Ordinal))
                {
                    row.DegradedAtLoad = true;
                    warn(label + " degraded — splitOrigin does not name its target's home seat");
                }
            }
        }

        private static void NormalizePriorityRow(
            PriorityRow row,
            HashSet<string> rowIds,
            HashSet<string> summonIds,
            HashSet<string> homeSeatsByTarget,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> cycleIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            string label = "priority row '" + (row.Id ?? row.KindRaw ?? "?") + "'";

            switch (row.Kind)
            {
                case PriorityRowKind.Seat:
                case PriorityRowKind.Satellite:
                    if (string.IsNullOrWhiteSpace(row.Id))
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — no id");
                    }
                    else if (IsReservedRuntimeCarrierId(row.Id))
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — id is a reserved runtime id");
                    }
                    else if (!rowIds.Add(row.Id))
                    {
                        row.DegradedAtLoad = true;
                        warn("duplicate priority row id '" + row.Id + "' — keeping the first");
                    }
                    break;

                case PriorityRowKind.Manual:
                    // Id-less; first-wins handled by caller.
                    // Detect extras: if id set already saw a manual via a side channel —
                    // use homeSeatsByTarget's sibling: we pass via a manual-seen pattern.
                    break;

                default:
                    row.DegradedAtLoad = true;
                    warn(label + " degraded — "
                        + (row.KindRaw == null ? "no kind"
                            : "unrecognized kind '" + row.KindRaw + "'"));
                    return;
            }

            // More-than-one manual: first wins (tracked by presence of prior non-degraded
            // manual in homeSeatsByTarget under a reserved key).
            if (row.Kind == PriorityRowKind.Manual)
            {
                const string manualKey = "__manual__";
                if (!homeSeatsByTarget.Add(manualKey))
                {
                    row.DegradedAtLoad = true;
                    warn("duplicate manual row — keeping the first");
                }
                return;
            }

            if (row.Kind == PriorityRowKind.Seat)
            {
                NormalizePageRefCarrier(row.Target, label + " target",
                    hostedPageIds, itmCatalogIds, cycleIds, catalog,
                    allowCycle: true, requirePresent: true, warn,
                    onDegrade: () => row.DegradedAtLoad = true);

                string tKey = TargetKey(row.Target);
                if (tKey != null && !row.Target.DegradedAtLoad)
                {
                    if (!homeSeatsByTarget.Add(tKey))
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — duplicate home seat for target");
                    }
                }

                if (row.Summons != null)
                {
                    foreach (var s in row.Summons)
                    {
                        if (s != null)
                            NormalizeSummon(s, summonIds, warn);
                    }
                }

                // bringUpLifetime domain: whileTrue | forDuration only.
                if (row.BringUpLifetime != null)
                {
                    NormalizeBringUpLifetime(row.BringUpLifetime, label + " bringUpLifetime", warn,
                        () => row.DegradedAtLoad = true);
                }
            }
            else if (row.Kind == PriorityRowKind.Satellite)
            {
                bool hasSummons = row.Summons != null && row.Summons.Count > 0;
                bool hasFieldShape = HasFieldChildRef(row.ChildRef);
                bool hasLayerShape = HasLayerChildRef(row.ChildRef);
                bool hasChildRef = hasFieldShape || hasLayerShape;

                if (hasSummons && hasChildRef)
                {
                    row.SummonsIgnored = true;
                    warn(label + ": satellite has both summons and childRef — childRef wins");
                    // Degrade-visible but still usable via childRef.
                    row.DegradedAtLoad = true;
                }
                else if (!hasSummons && !hasChildRef)
                {
                    row.DegradedAtLoad = true;
                    warn(label + " degraded — satellite has neither summons nor childRef");
                }

                // childRef path (also when both — childRef wins). Existence/flag check is
                // a second pass once all pages/fields are indexed.
                if (hasChildRef)
                {
                    // Target on childRef satellites is derived-only — stored target ignored.
                    if (row.Target != null)
                    {
                        row.TargetIgnored = true;
                        row.DegradedAtLoad = true;
                        warn(label + ": childRef satellite has stored target — ignored (derived-only)");
                    }

                    // Both field and layer shapes → ambiguous; no silent preference.
                    if (hasFieldShape && hasLayerShape)
                    {
                        row.ChildRefAmbiguous = true;
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — childRef has both field and layer shapes");
                    }

                    if (row.Lifetime != null)
                    {
                        NormalizeBringUpLifetime(row.Lifetime, label + " lifetime", warn,
                            () => row.DegradedAtLoad = true);
                    }
                }

                if (hasSummons && !row.SummonsIgnored)
                {
                    NormalizePageRefCarrier(row.Target, label + " target",
                        hostedPageIds, itmCatalogIds, cycleIds, catalog,
                        allowCycle: true, requirePresent: true, warn,
                        onDegrade: () => row.DegradedAtLoad = true);
                }

                // Always walk summons for id/condition marks (never drop data).
                if (hasSummons)
                {
                    foreach (var s in row.Summons)
                    {
                        if (s != null)
                            NormalizeSummon(s, summonIds, warn);
                    }
                }
            }
        }

        /// <summary>
        /// Resolves childRef satellites against the document: missing child → degraded;
        /// child exists but unflagged → degraded. Shape already filtered in the first pass.
        /// Field childRefs use one-ladder lookup (shared first).
        /// </summary>
        private static void ResolveChildRefSatellites(
            DisplayConfigV2 config, WheelCatalog catalog, Action<string> warn)
        {
            if (config.Priority?.Rows == null)
                return;

            foreach (var row in config.Priority.Rows)
            {
                if (row == null || row.Kind != PriorityRowKind.Satellite || row.ChildRef == null)
                    continue;
                if (row.SummonsIgnored == false
                    && row.Summons != null && row.Summons.Count > 0
                    && !HasFieldChildRef(row.ChildRef) && !HasLayerChildRef(row.ChildRef))
                    continue; // summons-only satellite

                // When both present, childRef wins — still resolve childRef.
                bool hasChild = HasFieldChildRef(row.ChildRef) || HasLayerChildRef(row.ChildRef);
                if (!hasChild)
                    continue;

                string label = "priority row '" + (row.Id ?? "?") + "' childRef";
                var cr = row.ChildRef;

                // Ambiguous dual-shape: already degraded; do not silently resolve either side.
                if (row.ChildRefAmbiguous || (HasFieldChildRef(cr) && HasLayerChildRef(cr)))
                {
                    row.ChildRefAmbiguous = true;
                    row.DegradedAtLoad = true;
                    continue;
                }

                if (HasFieldChildRef(cr))
                {
                    if (!ushort.TryParse(cr.Field, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out ushort paramId))
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — malformed field param id '" + cr.Field + "'");
                        continue;
                    }

                    // One-ladder lookup (shared first, page-scoped inert second).
                    if (!FieldLadderMap.TryFindOverride(
                            config, catalog, paramId, cr.OverrideId, out var ov)
                        || ov == null)
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — override '" + cr.OverrideId
                            + "' on field " + paramId + " not found");
                    }
                    else if (!ov.ActsAsEntrypoint)
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — referenced child is unflagged");
                    }
                }
                else if (HasLayerChildRef(cr))
                {
                    LayerEntry layer = null;
                    if (config.Pages != null)
                    {
                        foreach (var page in config.Pages)
                        {
                            if (page == null || page.Kind != PageEntryKind.HostedPage)
                                continue;
                            if (!string.Equals(page.Id, cr.PageId, StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (page.Layers == null)
                                break;
                            foreach (var l in page.Layers)
                            {
                                if (l != null && string.Equals(l.Id, cr.LayerId,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    layer = l;
                                    break;
                                }
                            }
                            break;
                        }
                    }

                    if (layer == null)
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — layer '" + cr.LayerId
                            + "' on page '" + cr.PageId + "' not found");
                    }
                    else if (!layer.ActsAsEntrypoint)
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — referenced child is unflagged");
                    }
                }
            }
        }

        private static void NormalizeSummon(
            Summon summon, HashSet<string> summonIds, Action<string> warn)
        {
            string label = "summon '" + (summon.Id ?? summon.Name ?? "?") + "'";

            if (string.IsNullOrWhiteSpace(summon.Id))
            {
                summon.DegradedAtLoad = true;
                warn(label + " degraded — no id");
            }
            else if (IsReservedRuntimeCarrierId(summon.Id))
            {
                summon.DegradedAtLoad = true;
                warn(label + " degraded — id is a reserved runtime id");
            }
            else if (!summonIds.Add(summon.Id))
            {
                summon.DegradedAtLoad = true;
                warn("duplicate summon id '" + summon.Id + "' — keeping the first");
            }

            NormalizeRuns(summon.Runs, summon.RunsRaw, label, warn,
                () => summon.DegradedAtLoad = true);

            NormalizeConditionLifetimePair(
                summon.Condition, summon.Lifetime,
                isFieldOverride: false,
                isFlaggedChild: false,
                allowUntilDismissed: true, // summons may latch
                bringUpDomain: false,
                label, warn,
                degrade: () => summon.DegradedAtLoad = true);
        }

        private static void NormalizeRest(
            RestBlock rest,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> playlistIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (rest.InSessionPage != null)
            {
                if (rest.InSessionPage.Kind == PageRefKind.Cycle)
                {
                    rest.InSessionPage.DegradedAtLoad = true;
                    rest.InSessionPageUseDefaultWalk = true;
                    warn("rest.inSessionPage references a cycle — degraded; runtime falls back to default walk");
                }
                else if (!IsResolvablePageMember(rest.InSessionPage, hostedPageIds, itmCatalogIds,
                    catalog, allowCycle: false, out string reason))
                {
                    rest.InSessionPage.DegradedAtLoad = true;
                    rest.InSessionPageUseDefaultWalk = true;
                    warn("rest.inSessionPage degraded — " + reason
                        + "; runtime falls back to default walk");
                }
            }

            // FA3 / FREEZE AMENDMENT 3: rest.landingPage removed. Bare-Legacy seed is
            // engine law (LegacySeedResolver: first non-degraded hosted page in strip
            // order) — no config member, no validator mark.

            if (rest.Idle != null)
                NormalizeIdle(rest.Idle, hostedPageIds, itmCatalogIds, playlistIds,
                    catalog, warn, site: "rest.idle", allowPlaylist: true);
        }

        /// <summary>
        /// Normalize an idle-shaped destination. <paramref name="allowPlaylist"/> is true
        /// only for the idle slot itself; playlist steps and any future non-idle carrier
        /// pass false so a playlist ref is degraded-visible (scope guard).
        /// </summary>
        private static void NormalizeIdle(
            IdleSpec idle,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> playlistIds,
            WheelCatalog catalog,
            Action<string> warn,
            string site,
            bool allowPlaylist)
        {
            switch (idle.Kind)
            {
                case IdleKind.Page:
                    if (idle.Page == null)
                    {
                        idle.DegradedAtLoad = true;
                        warn(site + ".page missing — degraded; runtime falls back to blank");
                    }
                    else if (idle.Page.Kind == PageRefKind.Cycle)
                    {
                        idle.Page.DegradedAtLoad = true;
                        idle.DegradedAtLoad = true;
                        warn(site + ".page references a cycle — degraded; runtime falls back to blank");
                    }
                    else if (!IsResolvablePageMember(idle.Page, hostedPageIds, itmCatalogIds,
                        catalog, allowCycle: false, out string reason))
                    {
                        idle.Page.DegradedAtLoad = true;
                        idle.DegradedAtLoad = true;
                        warn(site + ".page degraded — " + reason
                            + "; runtime falls back to blank");
                    }
                    break;

                case IdleKind.Screen:
                    // blank has its own kind — screen:"blank" is not a legal screen value.
                    if (idle.Screen == WheelScreenCommand.Blank)
                    {
                        idle.DegradedAtLoad = true;
                        idle.ScreenIgnored = true;
                        warn(site + " {kind:screen,screen:blank} degraded — use kind blank");
                    }
                    else if (idle.Screen == WheelScreenCommand.Unknown)
                    {
                        idle.DegradedAtLoad = true;
                        idle.ScreenIgnored = true;
                        warn(site + " screen unrecognized '" + idle.ScreenRaw
                            + "' — degraded");
                    }
                    else if (catalog != null)
                    {
                        bool? supported = ScreenCommandSupported(catalog, idle.Screen);
                        if (supported == false)
                        {
                            // rest.idle screen unsupported → degrade whole idle.
                            // Playlist step: mark destination degraded; program SKIPS
                            // the step (P6) rather than degrading the whole playlist.
                            idle.DegradedAtLoad = true;
                            idle.ScreenIgnored = true;
                            warn(site + " screen '" + idle.ScreenRaw
                                + "' not supported on this wheel — degraded");
                        }
                        else if (supported == null)
                        {
                            warn(site + " screen '" + idle.ScreenRaw
                                + "' capability is untested (null) — not gated");
                        }
                    }
                    break;

                case IdleKind.Blank:
                    if (catalog != null && IsItmWheel(catalog))
                    {
                        // Universal blank (owner ruling): the firmware command only
                        // where bench-CONFIRMED; untested counts as unavailable —
                        // the display drops to true legacy mode + painted-off segments.
                        bool? blankOk = catalog.ScreenCommands?.Blank;
                        if (blankOk != true)
                        {
                            idle.ParkOnLegacyForBlank = true;
                            warn(site + " blank without a confirmed blank command — legacy-mode blank compile policy");
                        }
                    }
                    break;

                case IdleKind.Playlist:
                    if (!allowPlaylist)
                    {
                        idle.DegradedAtLoad = true;
                        warn(site + " playlist ref is legal only on rest.idle — degraded");
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(idle.Playlist))
                    {
                        idle.DegradedAtLoad = true;
                        warn(site + ".playlist missing — degraded; runtime falls back to blank");
                    }
                    else if (playlistIds == null || !playlistIds.Contains(idle.Playlist))
                    {
                        // Keep the ref (spec §14); degrade idle so runtime falls back to blank.
                        idle.DegradedAtLoad = true;
                        warn(site + ".playlist '" + idle.Playlist
                            + "' unresolvable — degraded; runtime falls back to blank");
                    }
                    // All-skipped / missing-program floor: same universal-blank policy
                    // as blank idle (IdleCompile floor uses this flag).
                    if (catalog != null && IsItmWheel(catalog))
                    {
                        bool? blankOk = catalog.ScreenCommands?.Blank;
                        if (blankOk != true)
                            idle.ParkOnLegacyForBlank = true;
                    }
                    break;

                case IdleKind.Unknown:
                    idle.DegradedAtLoad = true;
                    warn(site + " unrecognized kind '" + idle.KindRaw + "' — degraded");
                    break;
            }
        }

        // ── Playlists ─────────────────────────────────────────────────────

        private static void NormalizePlaylists(
            DisplayConfigV2 config,
            HashSet<string> playlistIds,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.Playlists == null)
                return;

            // First-wins identity: seenIds consumes the id (including an invalid first);
            // playlistIds is the RESOLVABLE set only (rest.idle lookups). Later duplicates
            // degrade THEMSELVES only — they never remove a prior survivor's id.
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var playlist in config.Playlists)
            {
                if (playlist == null)
                    continue;

                string label = "playlist '" + (playlist.Id ?? playlist.Name ?? "?") + "'";
                bool isDuplicate = false;

                if (string.IsNullOrWhiteSpace(playlist.Id))
                {
                    playlist.DegradedAtLoad = true;
                    warn(label + " degraded — no id");
                }
                else if (IsReservedRuntimeCarrierId(playlist.Id))
                {
                    playlist.DegradedAtLoad = true;
                    warn(label + " degraded — id is a reserved runtime id");
                    // Invalid first still consumes the id so a later twin cannot win.
                    seenIds.Add(playlist.Id);
                }
                else if (!seenIds.Add(playlist.Id))
                {
                    isDuplicate = true;
                    playlist.DegradedAtLoad = true;
                    warn("duplicate playlist id '" + playlist.Id + "' — keeping the first");
                }

                // terminal: unknown coerce to hold at runtime, degrade-visible; raw preserved.
                if (playlist.Terminal == PlaylistTerminal.Unknown
                    && !string.IsNullOrWhiteSpace(playlist.TerminalRaw))
                {
                    playlist.TerminalCoercedAtLoad = true;
                    warn(label + " terminal '" + playlist.TerminalRaw
                        + "' unrecognized — runtime coerces to hold");
                }

                // Unknown terminal coerces to hold at runtime — treat as hold for duration rules.
                bool isHold = playlist.Terminal != PlaylistTerminal.Loop;

                // Filter-first duration legality (OQ-P3 / P6): normalize destinations, then
                // decide held-final among destination-survivors only. A missing-duration
                // step that becomes final after later steps are skipped is the legal held
                // terminal — do not degrade it for authored non-final position.
                var destinationSurvivors = new List<PlaylistStep>();
                if (playlist.Steps != null)
                {
                    int seen = 0;
                    for (int i = 0; i < playlist.Steps.Count; i++)
                    {
                        var step = playlist.Steps[i];
                        if (step == null)
                            continue;
                        string stepLabel = label + " step " + seen;
                        seen++;
                        NormalizePlaylistStepDestination(
                            step, stepLabel, hostedPageIds, itmCatalogIds, catalog, warn);
                        if (!step.DegradedAtLoad)
                            destinationSurvivors.Add(step);
                    }
                }

                for (int i = 0; i < destinationSurvivors.Count; i++)
                {
                    var step = destinationSurvivors[i];
                    bool isFinal = i == destinationSurvivors.Count - 1;
                    string stepLabel = label + " step (survivor " + i + ")";
                    ApplyPlaylistStepDurationRules(step, stepLabel, isFinal, isHold, warn);
                }

                // Count steps that remain program-usable after destination + duration rules.
                int resolvable = 0;
                for (int i = 0; i < destinationSurvivors.Count; i++)
                {
                    if (!destinationSurvivors[i].DegradedAtLoad)
                        resolvable++;
                }

                // P4: 1-step legal; 0 resolvable steps → degrade playlist whole.
                // Do NOT remove a first-wins id from seenIds; only withhold it from the
                // resolvable set (playlistIds). Invalid first still blocks later twins.
                if (resolvable < 1)
                {
                    playlist.DegradedAtLoad = true;
                    warn(label + " degraded — no resolvable steps");
                }
                else if (!isDuplicate
                    && !playlist.DegradedAtLoad
                    && !string.IsNullOrWhiteSpace(playlist.Id))
                {
                    playlistIds.Add(playlist.Id);
                }
            }
        }

        /// <summary>
        /// Destination-only normalize for a playlist step (nested playlist, page/screen
        /// resolve). Duration rules applied separately after survivor filtering.
        /// </summary>
        private static void NormalizePlaylistStepDestination(
            PlaylistStep step,
            string stepLabel,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (step.Destination == null)
            {
                step.DegradedAtLoad = true;
                warn(stepLabel + " degraded — destination missing");
                return;
            }

            // Nested playlist is illegal — step degraded (no nesting).
            if (step.Destination.Kind == IdleKind.Playlist)
            {
                step.DegradedAtLoad = true;
                step.Destination.DegradedAtLoad = true;
                warn(stepLabel + " degraded — nested playlist is illegal");
                return;
            }

            // Steps are not the idle slot: playlist ref inside destination is also nested.
            NormalizeIdle(
                step.Destination, hostedPageIds, itmCatalogIds,
                playlistIds: null, catalog, warn,
                site: stepLabel + " destination",
                allowPlaylist: false);

            if (step.Destination.DegradedAtLoad)
            {
                step.DegradedAtLoad = true;
                // warning already emitted by NormalizeIdle
            }
        }

        /// <summary>
        /// Duration rules (OQ-P3) against the post-filter survivor list: held final under
        /// hold may omit duration; present value on held final is ignored + visible.
        /// Under hold, missing duration on a non-final is a RUNTIME skip only (not load
        /// degrade) so a step that becomes final after capability filtering stays legal.
        /// Under loop, every step needs a duration — missing degrades at load.
        /// </summary>
        private static void ApplyPlaylistStepDurationRules(
            PlaylistStep step,
            string stepLabel,
            bool isFinal,
            bool isHoldTerminal,
            Action<string> warn)
        {
            if (step.DurationMsPresent)
            {
                if (isFinal && isHoldTerminal)
                {
                    step.DurationMsIgnored = true;
                    warn(stepLabel + " durationMs ignored on held final step (terminal hold)");
                }
                else if (step.DurationMs < SeatArbiter.MinDwellMs)
                {
                    // P2: runtime-only clamp; authored value preserved; degrade-visible note.
                    // Do NOT rewrite DurationMs.
                    warn(stepLabel + " durationMs " + step.DurationMs
                        + " below destination floor " + SeatArbiter.MinDwellMs
                        + " ms — runtime clamps (document unchanged)");
                }
            }
            else if (!isHoldTerminal)
            {
                // Loop: every step must contribute time — absent duration degrades.
                step.DegradedAtLoad = true;
                warn(stepLabel + " degraded — durationMs absent (required under terminal loop)");
            }
            // Hold + absent duration: leave undegraded. IdleCompile filter-first treats
            // the surviving final as the legal held terminal; earlier untimeable steps
            // are skipped at runtime without a load degrade (so capability filter can
            // still promote them to final).
        }

        // ── pageOrder ─────────────────────────────────────────────────────

        private static void NormalizePageOrder(
            DisplayConfigV2 config,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> removedItmIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.PageOrder == null || config.PageOrder.Count == 0)
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in config.PageOrder)
            {
                if (entry == null)
                    continue;

                if (entry.Kind == PageRefKind.Cycle)
                {
                    entry.DegradedAtLoad = true;
                    warn("pageOrder entry degraded — cycles are not walkable members");
                    continue;
                }

                string key = TargetKey(entry);
                if (key != null && !seen.Add(key))
                {
                    entry.DegradedAtLoad = true;
                    warn("pageOrder duplicate '" + key + "' — keeping the first");
                    continue;
                }

                if (entry.Kind == PageRefKind.ItmPage
                    && !string.IsNullOrWhiteSpace(entry.CatalogPageId)
                    && removedItmIds.Contains(entry.CatalogPageId))
                {
                    entry.DegradedAtLoad = true;
                    warn("pageOrder entry for removed itm page '" + entry.CatalogPageId
                        + "' — degraded, skipped by walk");
                    continue;
                }

                if (!IsResolvablePageMember(entry, hostedPageIds, itmCatalogIds, catalog,
                    allowCycle: false, out string reason))
                {
                    entry.DegradedAtLoad = true;
                    warn("pageOrder entry degraded — " + reason);
                }
            }
        }

        // ── Wheel screen ──────────────────────────────────────────────────

        private static void NormalizeWheelScreen(
            DisplayConfigV2 config,
            HashSet<string> wheelRuleIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            if (config.WheelScreen?.Rules == null)
                return;

            foreach (var rule in config.WheelScreen.Rules)
            {
                if (rule == null)
                    continue;

                string label = "wheel-screen rule '" + (rule.Id ?? rule.Name ?? "?") + "'";

                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    rule.DegradedAtLoad = true;
                    warn(label + " degraded — no id");
                }
                else if (IsReservedRuntimeCarrierId(rule.Id))
                {
                    rule.DegradedAtLoad = true;
                    warn(label + " degraded — id is a reserved runtime id");
                }
                else if (!wheelRuleIds.Add(rule.Id))
                {
                    rule.DegradedAtLoad = true;
                    warn("duplicate wheel-screen rule id '" + rule.Id + "' — keeping the first");
                }

                if (rule.Screen == WheelScreenCommand.Unknown)
                {
                    rule.DegradedAtLoad = true;
                    warn(label + " degraded — "
                        + (rule.ScreenRaw == null ? "no screen"
                            : "unrecognized screen '" + rule.ScreenRaw + "'"));
                }
                else if (catalog != null)
                {
                    bool? supported = ScreenCommandSupported(catalog, rule.Screen);
                    if (supported == false)
                    {
                        rule.DegradedAtLoad = true;
                        warn(label + " degraded — screen not supported on this wheel");
                    }
                    else if (supported == null)
                    {
                        warn(label + ": screen capability is untested (null) — not gated");
                    }
                }

                NormalizeRuns(rule.Runs, rule.RunsRaw, label, warn,
                    () => rule.DegradedAtLoad = true);

                NormalizeConditionLifetimePair(
                    rule.Condition, rule.Lifetime,
                    isFieldOverride: false,
                    isFlaggedChild: false,
                    allowUntilDismissed: true, // wheel-screen rules are dismissable
                    bringUpDomain: false,
                    label, warn,
                    degrade: () => rule.DegradedAtLoad = true);
            }
        }

        private static void NormalizeSettings(DisplayConfigV2 config, Action<string> warn)
        {
            if (config.Settings == null)
                return;
            if (config.Settings.Mode == SettingsMode.Unknown
                && !string.IsNullOrWhiteSpace(config.Settings.ModeRaw))
            {
                config.Settings.DegradedAtLoad = true;
                warn("settings.mode unrecognized '" + config.Settings.ModeRaw
                    + "' — degraded; raw preserved; runtime treats as on");
            }
        }

        // ── Condition / lifetime pairings ─────────────────────────────────

        private static void NormalizeConditionLifetimePair(
            Condition condition,
            Lifetime lifetime,
            bool isFieldOverride,
            bool isFlaggedChild,
            bool allowUntilDismissed,
            bool bringUpDomain,
            string label,
            Action<string> warn,
            Action degrade)
        {
            // Effective lifetime kind: absent ≡ whileTrue.
            LifetimeKind lifeKind = lifetime != null ? lifetime.Kind : LifetimeKind.WhileTrue;
            if (lifetime != null && lifeKind == LifetimeKind.Unknown)
            {
                // Unrecognized: coerce to whileTrue at runtime (preserve raw).
                lifetime.CoerceKind(LifetimeKind.WhileTrue);
                lifeKind = LifetimeKind.WhileTrue;
                warn(label + ": unrecognized lifetime kind '" + lifetime.KindRaw
                    + "' — using whileTrue");
                degrade?.Invoke();
            }

            if (bringUpDomain && lifetime != null)
                NormalizeBringUpLifetime(lifetime, label, warn, degrade);

            // Source validation.
            SourceSite site = isFieldOverride ? SourceSite.FieldOverrideCondition
                : SourceSite.Condition;
            if (condition != null)
            {
                NormalizeValueSource(condition.Source, site, label, warn,
                    degradeCarrier: () =>
                    {
                        if (condition.Source != null)
                            condition.Source.DegradedAtLoad = true;
                        degrade?.Invoke();
                    });
            }
            else
            {
                // Missing condition: degrade carrier (cannot fire).
                warn(label + " degraded — no condition");
                degrade?.Invoke();
            }

            bool hasOperator = condition != null && condition.Operator != null
                && condition.Operator != ConditionOperator.Unknown;
            bool operatorUnknown = condition != null
                && !string.IsNullOrWhiteSpace(condition.OperatorRaw)
                && condition.Operator == ConditionOperator.Unknown;

            if (operatorUnknown)
            {
                warn(label + ": unrecognized operator '" + condition.OperatorRaw
                    + "' — raw preserved");
                degrade?.Invoke();
            }

            // whileTrue is level-only: operator-less whileTrue degrades (FA2: action
            // source kind removed — no operator-exemption path remains).
            if (lifeKind == LifetimeKind.WhileTrue && !hasOperator)
            {
                warn(label + ": whileTrue requires a level operator — degraded");
                degrade?.Invoke();
            }

            // onChange + operator present → degrade.
            if (lifeKind == LifetimeKind.OnChange && hasOperator)
            {
                warn(label + ": onChange must not carry an operator — degraded");
                degrade?.Invoke();
            }

            // Hysteresis on non-level (no level operator / onChange).
            if (condition?.Hysteresis != null)
            {
                bool isLevel = hasOperator && lifeKind != LifetimeKind.OnChange;
                if (!isLevel)
                {
                    condition.HysteresisIgnored = true;
                    warn(label + ": hysteresis on non-level condition — ignored at runtime");
                    degrade?.Invoke();
                }
                else if (!IsFinite(condition.Hysteresis.Value) || condition.Hysteresis.Value < 0)
                {
                    // Runtime-only: ignore bad hysteresis rather than rewrite.
                    condition.HysteresisIgnored = true;
                    warn(label + ": hysteresis is not a finite non-negative number — ignored");
                    degrade?.Invoke();
                }
            }

            if (lifetime == null)
                return;

            // Direction domain: any | up | down. Leave parsed Direction as Unknown so
            // unknown-spelling round-trips still observe the fallback; engine uses Any.
            if (lifetime.Direction == ChangeDirection.Unknown
                && !string.IsNullOrWhiteSpace(lifetime.DirectionRaw))
            {
                lifetime.DirectionCoercedToAny = true;
                warn(label + ": direction outside any/up/down ('" + lifetime.DirectionRaw
                    + "') — using any");
                degrade?.Invoke();
            }

            // then domain: only untilDismissed.
            bool thenPresent = !string.IsNullOrWhiteSpace(lifetime.ThenRaw);
            if (thenPresent && lifetime.Then == LifetimeThen.Unknown)
            {
                lifetime.ThenIgnored = true;
                warn(label + ": then outside untilDismissed ('" + lifetime.ThenRaw
                    + "') — ignored");
                degrade?.Invoke();
            }

            bool thenActive = thenPresent && !lifetime.ThenIgnored
                && lifetime.Then == LifetimeThen.UntilDismissed;

            // then + durationMs mutual exclusivity: then wins, duration ignored at runtime.
            // Presence is tracked separately from the value — durationMs:5000 is still
            // "both present" (degrade-visible). Authored durationMs is never rewritten.
            if (thenActive)
            {
                lifetime.DurationMsIgnored = true;
                if (lifetime.DurationMsPresent)
                {
                    warn(label + ": then + durationMs together — durationMs ignored");
                    degrade?.Invoke();
                }
            }

            // untilDismissed / then on unflagged content child → coerce to forDuration.
            bool isUntil = lifeKind == LifetimeKind.UntilDismissed || thenActive;
            if (isUntil && !allowUntilDismissed)
            {
                lifetime.CoerceKind(LifetimeKind.ForDuration);
                lifetime.ThenIgnored = true;
                warn(label + ": untilDismissed/then on unflagged content child"
                    + " — coerced to forDuration");
                degrade?.Invoke();
            }
        }

        private static void NormalizeBringUpLifetime(
            Lifetime lifetime, string label, Action<string> warn, Action degrade)
        {
            var k = lifetime.Kind;
            if (k == LifetimeKind.WhileTrue || k == LifetimeKind.ForDuration)
                return;
            // Unknown already coerced by pair helper in some paths; still enforce domain.
            if (k == LifetimeKind.Unknown && string.IsNullOrWhiteSpace(lifetime.KindRaw))
            {
                // Absent kind on object — treat as whileTrue silently.
                lifetime.CoerceKind(LifetimeKind.WhileTrue);
                return;
            }
            lifetime.CoerceKind(LifetimeKind.WhileTrue);
            warn(label + ": bring-up lifetime domain is whileTrue|forDuration — coerced to whileTrue"
                + (lifetime.KindRaw != null ? " (was '" + lifetime.KindRaw + "')" : ""));
            // All coercions are degrade-visible on the owning row (§14 / FZ-011).
            degrade?.Invoke();
        }

        private static void NormalizeValueSource(
            ValueSource source, SourceSite site, string label, Action<string> warn,
            Action degradeCarrier)
        {
            if (source == null)
            {
                if (site == SourceSite.Content)
                {
                    // Property content with no source → no-data convention.
                    return;
                }
                warn(label + " degraded — no source");
                degradeCarrier?.Invoke();
                return;
            }

            if (string.IsNullOrWhiteSpace(source.Name))
            {
                source.DegradedAtLoad = true;
                warn(label + " degraded — source has no name");
                degradeCarrier?.Invoke();
                return;
            }

            switch (source.Kind)
            {
                case ValueSourceKind.BuiltIn:
                    if (!BuiltInProperties.IsKnown(source.Name))
                    {
                        source.DegradedAtLoad = true;
                        warn(label + " degraded — unknown built-in property '"
                            + source.Name + "'");
                        degradeCarrier?.Invoke();
                    }
                    break;

                case ValueSourceKind.ItmField:
                    if (string.Equals(source.Name, "self", StringComparison.OrdinalIgnoreCase))
                    {
                        if (site != SourceSite.FieldOverrideCondition)
                        {
                            source.DegradedAtLoad = true;
                            warn(label + " degraded — itmField 'self' outside a field override");
                            degradeCarrier?.Invoke();
                        }
                    }
                    else if (!ushort.TryParse(source.Name, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out _))
                    {
                        source.DegradedAtLoad = true;
                        warn(label + " degraded — malformed itmField param id '"
                            + source.Name + "'");
                        degradeCarrier?.Invoke();
                    }
                    break;

                case ValueSourceKind.Unknown:
                    source.DegradedAtLoad = true;
                    warn(label + " degraded — "
                        + (source.KindRaw == null ? "no source kind"
                            : "unrecognized source kind '" + source.KindRaw + "'"));
                    degradeCarrier?.Invoke();
                    break;

                case ValueSourceKind.SimHubProperty:
                    // Legal carry.
                    break;

                case ValueSourceKind.Script:
                    // Parsed-but-inert (ratified): preserve raw; source + carrier degraded.
                    source.DegradedAtLoad = true;
                    warn(label + " degraded — source kind 'script' is parsed but inert");
                    degradeCarrier?.Invoke();
                    break;
            }
        }

        private static void NormalizeContentObject(
            ContentObject content, string label, Action<string> warn)
        {
            if (content == null)
                return;

            if (content.Kind == ContentKind.Unknown)
            {
                content.DegradedAtLoad = true;
                warn(label + " degraded — "
                    + (string.IsNullOrWhiteSpace(content.KindRaw) ? "no content kind"
                        : "unrecognized content kind '" + content.KindRaw + "'"));
            }

            if (content.Kind == ContentKind.Property)
            {
                if (content.Source == null
                    || string.IsNullOrWhiteSpace(content.Source.Name)
                    || content.Source.Kind == ValueSourceKind.Unknown
                    || (content.Source.Kind == ValueSourceKind.BuiltIn
                        && !BuiltInProperties.IsKnown(content.Source.Name)))
                {
                    content.DegradedAtLoad = true;
                    warn(label + ": property content source unusable — renders no-data convention");
                    if (content.Source != null)
                        content.Source.DegradedAtLoad = true;
                }
                else
                {
                    NormalizeValueSource(content.Source, SourceSite.Content, label, warn,
                        degradeCarrier: () =>
                        {
                            content.DegradedAtLoad = true;
                            warn(label + ": property content source degraded — no-data convention");
                        });
                }
            }

            // Over-length text: clamp at runtime on FOLDED position count (≤ 3), never
            // raw char length — "A.b.c.d" is 4 folded positions, not 7 raw chars.
            if (content.Kind == ContentKind.Text && !string.IsNullOrEmpty(content.Text))
            {
                if (!FanaBridge.Display.SegmentText.IsRenderableText(content.Text))
                {
                    if (SevenSegment.EncodeWithDots(content.Text).Count > 3)
                    {
                        content.EffectiveText =
                            SevenSegment.TruncateToFoldedPositions(content.Text, 3);
                        content.DegradedAtLoad = true;
                        warn(label + ": over-length text clamped at runtime (document preserved)");
                    }
                }
            }

            CoerceFlashOnContent(content, label, warn);
        }

        private static void NormalizeContentWithEffect(
            ContentWithEffect cwe, string label, Action<string> warn)
        {
            if (cwe == null)
                return;
            NormalizeContentObject(cwe.Content, label, warn);
            NormalizeEffect(cwe.Effect, cwe.EffectRaw,
                e => cwe.CoerceEffect(ContentEffect.Blink), label, warn,
                () => cwe.DegradedAtLoad = true);
        }

        /// <summary>
        /// Effect domain: flash coerces to blink (degrade-visible); unknown spellings
        /// degrade the carrier while preserving raw text.
        /// </summary>
        private static void NormalizeEffect(
            ContentEffect effect, string effectRaw, Action<ContentEffect> coerce,
            string label, Action<string> warn, Action degrade)
        {
            if (effect == ContentEffect.Flash)
            {
                // Known spelling; runtime coerce only (raw preserved). Not an unknown-enum path.
                coerce(ContentEffect.Blink);
                warn(label + ": effect 'flash' is not implemented — using blink");
                return;
            }
            if (effect == ContentEffect.Unknown && !string.IsNullOrWhiteSpace(effectRaw))
            {
                warn(label + " degraded — unrecognized effect '" + effectRaw + "'");
                degrade?.Invoke();
            }
        }

        private static void NormalizeRuns(
            RunsWhen runs, string runsRaw, string label, Action<string> warn, Action degrade)
        {
            if (runs == RunsWhen.Unknown && !string.IsNullOrWhiteSpace(runsRaw))
            {
                warn(label + " degraded — unrecognized runs '" + runsRaw + "'");
                degrade?.Invoke();
            }
        }

        private static void CoerceFlashOnContent(ContentObject content, string label, Action<string> warn)
        {
            // ContentObject has no effect — effects live on carriers. No-op retained for symmetry.
        }

        /// <summary>
        /// Runtime carrier-id families reserved for plane floors and synthetic rows:
        /// bare <c>rest</c> / <c>manual</c> / <c>idle</c>, and the <c>rest:</c> prefix
        /// family (covers E6 floor id <c>rest:idle</c> and E4 rest destination spellings).
        /// Authored wheel rules, pages, summons, seats, layers, and overrides that claim
        /// these ids degrade at load so the §6.1 one-row-per-(CarrierId,SurfaceId) law
        /// cannot collide with a floor row. (FA2: migration-reserved
        /// hosted-page prefix is gone — only these E6-round runtime families remain.)
        /// </summary>
        internal static bool IsReservedRuntimeCarrierId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            if (string.Equals(id, "rest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "manual", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "idle", StringComparison.OrdinalIgnoreCase))
                return true;
            return id.StartsWith("rest:", StringComparison.OrdinalIgnoreCase);
        }

        // ── PageRef helpers ───────────────────────────────────────────────

        private static void NormalizePageRefCarrier(
            PageRef pref, string label,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            HashSet<string> cycleIds,
            WheelCatalog catalog,
            bool allowCycle, bool requirePresent, Action<string> warn,
            Action onDegrade)
        {
            if (pref == null)
            {
                if (requirePresent)
                {
                    warn(label + " missing — degraded");
                    onDegrade?.Invoke();
                }
                return;
            }

            if (pref.Kind == PageRefKind.Cycle && !allowCycle)
            {
                pref.DegradedAtLoad = true;
                warn(label + " is a cycle (illegal here) — degraded");
                onDegrade?.Invoke();
                return;
            }

            if (!IsResolvablePageMember(pref, hostedPageIds, itmCatalogIds, catalog,
                    allowCycle, out string reason)
                && !(pref.Kind == PageRefKind.Cycle && allowCycle
                    && !string.IsNullOrWhiteSpace(pref.Id) && cycleIds.Contains(pref.Id)))
            {
                // Cycle resolution uses cycleIds.
                if (pref.Kind == PageRefKind.Cycle && allowCycle)
                {
                    if (string.IsNullOrWhiteSpace(pref.Id) || !cycleIds.Contains(pref.Id))
                    {
                        pref.DegradedAtLoad = true;
                        warn(label + " degraded — "
                            + (string.IsNullOrWhiteSpace(pref.Id) ? "no cycle id"
                                : "unknown cycle '" + pref.Id + "'"));
                        onDegrade?.Invoke();
                    }
                    return;
                }

                pref.DegradedAtLoad = true;
                warn(label + " degraded — " + reason);
                onDegrade?.Invoke();
            }
            else if (pref.Kind == PageRefKind.Cycle && allowCycle)
            {
                if (string.IsNullOrWhiteSpace(pref.Id) || !cycleIds.Contains(pref.Id))
                {
                    pref.DegradedAtLoad = true;
                    warn(label + " degraded — "
                        + (string.IsNullOrWhiteSpace(pref.Id) ? "no cycle id"
                            : "unknown cycle '" + pref.Id + "'"));
                    onDegrade?.Invoke();
                }
            }
        }

        /// <summary>
        /// Page-member resolution (itmPage / hostedPage). Cycles are never resolvable
        /// here — callers that allow cycle refs check cycleIds separately.
        /// With a catalog, ITM resolvability is catalog roster only (pages[] overlays
        /// cannot mint identities). Without a catalog, any non-empty itm catalogPageId
        /// is treated as resolvable.
        /// </summary>
        private static bool IsResolvablePageMember(
            PageRef pref,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            WheelCatalog catalog,
            bool allowCycle,
            out string reason)
        {
            reason = null;
            if (pref == null)
            {
                reason = "null ref";
                return false;
            }

            switch (pref.Kind)
            {
                case PageRefKind.HostedPage:
                    if (string.IsNullOrWhiteSpace(pref.Id))
                    {
                        reason = "no hosted page id";
                        return false;
                    }
                    if (!hostedPageIds.Contains(pref.Id))
                    {
                        reason = "unknown hosted page '" + pref.Id + "'";
                        return false;
                    }
                    return true;

                case PageRefKind.ItmPage:
                    if (string.IsNullOrWhiteSpace(pref.CatalogPageId))
                    {
                        reason = "no catalogPageId";
                        return false;
                    }
                    if (catalog != null)
                    {
                        // Catalog roster is the sole identity source; pages[] overlays
                        // never mint resolvable ids (itmCatalogIds ignored here).
                        if (!CatalogHasPage(catalog, pref.CatalogPageId))
                        {
                            reason = "unknown itm page '" + pref.CatalogPageId + "'";
                            return false;
                        }
                        return true;
                    }
                    // Without catalog: non-empty id is enough (roster not available).
                    return true;

                case PageRefKind.Cycle:
                    if (!allowCycle)
                    {
                        reason = "cycle ref not legal here";
                        return false;
                    }
                    reason = "cycle ref needs cycle id check";
                    return false; // handled by caller with cycleIds

                default:
                    reason = pref.KindRaw == null ? "no pageRef kind"
                        : "unrecognized pageRef kind '" + pref.KindRaw + "'";
                    return false;
            }
        }

        // ── Capability / catalog helpers ──────────────────────────────────

        private static void MaybeWarnSubscriptionBudget(DisplayConfigV2 config, Action<string> warn)
        {
            int n = 0;
            if (config.Fields != null)
                n += config.Fields.Count;
            if (config.SharedFields != null)
                n += config.SharedFields.Count;
            if (n > SubscriptionBudget)
            {
                warn("subscription budget: " + n + " field entries exceed firmware cap of "
                    + SubscriptionBudget + " (authoring warning; engine enforces at re-plan)");
            }
        }

        private static CatalogFieldDefinition FindCatalogField(
            WheelCatalog catalog, ushort paramId)
            => CatalogFields.FindDefinitionByParam(catalog, paramId);

        private static int CountPrimaryHosts(WheelCatalog catalog, ushort paramId)
        {
            int n = 0;
            if (catalog?.Itm?.Pages == null)
                return 0;
            var defs = CatalogFields.IndexByLogicalId(catalog);
            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Placements == null)
                    continue;
                foreach (var pl in page.Placements)
                {
                    if (pl == null || pl.PrimaryHost != true || string.IsNullOrEmpty(pl.Field))
                        continue;
                    if (defs.TryGetValue(pl.Field, out var def)
                        && def != null && def.ParamId == paramId)
                        n++;
                }
            }
            return n;
        }

        private static string ResolvePrimaryHostCatalogId(WheelCatalog catalog, ushort paramId)
        {
            if (catalog?.Itm?.Pages == null)
                return null;
            var defs = CatalogFields.IndexByLogicalId(catalog);
            string found = null;
            int count = 0;
            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Placements == null)
                    continue;
                foreach (var pl in page.Placements)
                {
                    if (pl == null || pl.PrimaryHost != true || string.IsNullOrEmpty(pl.Field))
                        continue;
                    if (defs.TryGetValue(pl.Field, out var def)
                        && def != null && def.ParamId == paramId)
                    {
                        found = page.Id;
                        count++;
                    }
                }
            }
            return count == 1 ? found : null;
        }

        private static bool CatalogHasPage(WheelCatalog catalog, string catalogPageId)
        {
            if (catalog?.Itm?.Pages == null || string.IsNullOrWhiteSpace(catalogPageId))
                return false;
            foreach (var p in catalog.Itm.Pages)
            {
                if (p != null && string.Equals(p.Id, catalogPageId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool? ScreenCommandSupported(WheelCatalog catalog, WheelScreenCommand cmd)
        {
            var sc = catalog?.ScreenCommands;
            if (sc == null)
                return null;
            switch (cmd)
            {
                case WheelScreenCommand.Logo: return sc.Logo;
                case WheelScreenCommand.Blank: return sc.Blank;
                case WheelScreenCommand.White: return sc.White;
                case WheelScreenCommand.LogoInverted: return sc.LogoInverted;
                default: return null;
            }
        }

        private static bool IsItmWheel(WheelCatalog catalog)
            => catalog?.Itm?.Pages != null && catalog.Itm.Pages.Count > 0;

        // ── ChildRef / target keys ────────────────────────────────────────

        private static bool HasFieldChildRef(ChildRef cr)
            => cr != null && !string.IsNullOrWhiteSpace(cr.Field)
                && !string.IsNullOrWhiteSpace(cr.OverrideId);

        private static bool HasLayerChildRef(ChildRef cr)
            => cr != null && !string.IsNullOrWhiteSpace(cr.PageId)
                && !string.IsNullOrWhiteSpace(cr.LayerId);

        private static string TargetKey(PageRef pref)
        {
            if (pref == null)
                return null;
            switch (pref.Kind)
            {
                case PageRefKind.ItmPage:
                    return string.IsNullOrWhiteSpace(pref.CatalogPageId) ? null
                        : TargetKeyItm(pref.CatalogPageId);
                case PageRefKind.HostedPage:
                    return string.IsNullOrWhiteSpace(pref.Id) ? null
                        : TargetKeyHosted(pref.Id);
                case PageRefKind.Cycle:
                    return string.IsNullOrWhiteSpace(pref.Id) ? null
                        : "cycle:" + pref.Id;
                default:
                    return null;
            }
        }

        private static string TargetKeyHosted(string id) => "hosted:" + id;
        private static string TargetKeyItm(string catalogPageId) => "itm:" + catalogPageId;

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);

        private enum SourceSite
        {
            Condition,
            FieldOverrideCondition,
            FieldBase,
            Content,
        }

        private sealed class FlaggedHost
        {
            public string TargetKey;
            public string HostedPageId;
            public string ItmCatalogPageId;
            public string SourceLabel;
        }
    }
}
