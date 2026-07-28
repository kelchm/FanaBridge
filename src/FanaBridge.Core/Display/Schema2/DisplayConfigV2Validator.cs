using System;
using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Load-time validation and normalization for <see cref="DisplayConfigV2"/>
    /// (spec-schema-v2 §14). Contract mirrors <see cref="DisplayConfigValidator"/>:
    /// warn-and-degrade, never throw, never drop data, never rewrite persisted members.
    /// All coercions and clamps are runtime-only (<c>DegradedAtLoad</c> / <c>Coerce*</c> /
    /// effective accessors); a load→save round-trip is byte-identical for any document.
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

            NormalizeFields(config, overrideIds, flaggedHostsNeedingSeat,
                hostedPageIds, itmCatalogIds, removedItmIds, catalog, warn);

            NormalizePriority(config, rowIds, summonIds, hostedPageIds, itmCatalogIds,
                cycleIds, removedItmIds, catalog, flaggedHostsNeedingSeat, warn);

            // Second pass: childRef satellites need the finished page/field identity maps.
            ResolveChildRefSatellites(config, warn);

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
            else if (!layerIds.Add(layer.Id))
            {
                layer.DegradedAtLoad = true;
                warn("duplicate layer id '" + layer.Id + "' — keeping the first");
            }

            NormalizeContentObject(layer.Content, label, warn);
            CoerceFlashEffect(layer.Effect, raw => layer.CoerceEffect(ContentEffect.Blink),
                label, warn);

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

                if (field.Base != null)
                    NormalizeValueSource(field.Base.Source, SourceSite.FieldBase,
                        fieldLabel + " base", warn, degradeCarrier: null);

                if (field.Overrides == null)
                    continue;

                foreach (var ov in field.Overrides)
                {
                    if (ov == null)
                        continue;
                    NormalizeOverride(ov, paramId, overrideIds, catalog, removedItmIds, warn);

                    if (ov.ActsAsEntrypoint && !ov.ActsAsEntrypointIgnored && !ov.DegradedAtLoad)
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
            else if (!overrideIds.Add(ov.Id))
            {
                ov.DegradedAtLoad = true;
                warn("duplicate override id '" + ov.Id + "' — keeping the first");
            }

            NormalizeContentObject(ov.Content, label, warn);
            CoerceFlashEffect(ov.Effect, e => ov.CoerceEffect(ContentEffect.Blink), label, warn);

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
            HashSet<string> removedItmIds,
            WheelCatalog catalog,
            List<FlaggedHost> flaggedHostsNeedingSeat,
            Action<string> warn)
        {
            // Never assign serialized Priority / Rows / Rest — authored nulls stay null
            // and save as they loaded. Runtime projection lives on JsonIgnore members.
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
            }

            // Manual restoration FIRST (runtime view only).
            if (firstManual == null)
            {
                firstManual = new PriorityRow
                {
                    Kind = PriorityRowKind.Manual,
                    MaterializedAtLoad = true,
                };
                runtime.Add(firstManual);
                warn("missing manual row — restored above rest (runtime view only)");
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
                NormalizeRest(config.Priority.Rest, hostedPageIds, itmCatalogIds, catalog, warn);
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
                    NormalizeBringUpLifetime(row.BringUpLifetime, label + " bringUpLifetime", warn);
            }
            else if (row.Kind == PriorityRowKind.Satellite)
            {
                bool hasSummons = row.Summons != null && row.Summons.Count > 0;
                bool hasChildRef = row.ChildRef != null
                    && (HasFieldChildRef(row.ChildRef) || HasLayerChildRef(row.ChildRef));

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
                    if (row.Lifetime != null)
                        NormalizeBringUpLifetime(row.Lifetime, label + " lifetime", warn);
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
        /// </summary>
        private static void ResolveChildRefSatellites(DisplayConfigV2 config, Action<string> warn)
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

                if (HasFieldChildRef(cr))
                {
                    if (!ushort.TryParse(cr.Field, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out ushort paramId))
                    {
                        row.DegradedAtLoad = true;
                        warn(label + " degraded — malformed field param id '" + cr.Field + "'");
                        continue;
                    }

                    FieldOverride ov = null;
                    if (config.Fields != null
                        && config.Fields.TryGetValue(paramId, out var entry)
                        && entry?.Overrides != null)
                    {
                        foreach (var o in entry.Overrides)
                        {
                            if (o != null && string.Equals(o.Id, cr.OverrideId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                ov = o;
                                break;
                            }
                        }
                    }

                    if (ov == null)
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
            else if (!summonIds.Add(summon.Id))
            {
                summon.DegradedAtLoad = true;
                warn("duplicate summon id '" + summon.Id + "' — keeping the first");
            }

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

            if (rest.LandingPage != null)
            {
                if (rest.LandingPage.Kind == PageRefKind.Cycle)
                {
                    rest.LandingPage.DegradedAtLoad = true;
                    rest.LandingPageUseFallback = true;
                    warn("rest.landingPage references a cycle — degraded; runtime falls back");
                }
                else if (!IsResolvablePageMember(rest.LandingPage, hostedPageIds, itmCatalogIds,
                    catalog, allowCycle: false, out string reason))
                {
                    rest.LandingPage.DegradedAtLoad = true;
                    rest.LandingPageUseFallback = true;
                    warn("rest.landingPage degraded — " + reason + "; runtime falls back");
                }
            }

            if (rest.Idle != null)
                NormalizeIdle(rest.Idle, hostedPageIds, itmCatalogIds, catalog, warn);
        }

        private static void NormalizeIdle(
            IdleSpec idle,
            HashSet<string> hostedPageIds,
            HashSet<string> itmCatalogIds,
            WheelCatalog catalog,
            Action<string> warn)
        {
            switch (idle.Kind)
            {
                case IdleKind.Page:
                    if (idle.Page == null)
                    {
                        idle.DegradedAtLoad = true;
                        warn("rest.idle.page missing — degraded; runtime falls back to blank");
                    }
                    else if (idle.Page.Kind == PageRefKind.Cycle)
                    {
                        idle.Page.DegradedAtLoad = true;
                        idle.DegradedAtLoad = true;
                        warn("rest.idle.page references a cycle — degraded; runtime falls back to blank");
                    }
                    else if (!IsResolvablePageMember(idle.Page, hostedPageIds, itmCatalogIds,
                        catalog, allowCycle: false, out string reason))
                    {
                        idle.Page.DegradedAtLoad = true;
                        idle.DegradedAtLoad = true;
                        warn("rest.idle.page degraded — " + reason
                            + "; runtime falls back to blank");
                    }
                    break;

                case IdleKind.Screen:
                    if (idle.Screen == WheelScreenCommand.Unknown)
                    {
                        idle.DegradedAtLoad = true;
                        idle.ScreenIgnored = true;
                        warn("rest.idle screen unrecognized '" + idle.ScreenRaw
                            + "' — degraded");
                    }
                    else if (catalog != null)
                    {
                        bool? supported = ScreenCommandSupported(catalog, idle.Screen);
                        if (supported == false)
                        {
                            idle.DegradedAtLoad = true;
                            idle.ScreenIgnored = true;
                            warn("rest.idle screen '" + idle.ScreenRaw
                                + "' not supported on this wheel — degraded");
                        }
                        else if (supported == null)
                        {
                            warn("rest.idle screen '" + idle.ScreenRaw
                                + "' capability is untested (null) — not gated");
                        }
                    }
                    break;

                case IdleKind.Blank:
                    if (catalog != null && IsItmWheel(catalog))
                    {
                        bool? blankOk = catalog.ScreenCommands?.Blank;
                        if (blankOk == false)
                        {
                            idle.ParkOnLegacyForBlank = true;
                            warn("rest.idle blank on command-less ITM wheel — park-on-Legacy compile policy");
                        }
                    }
                    break;

                case IdleKind.Unknown:
                    idle.DegradedAtLoad = true;
                    warn("rest.idle unrecognized kind '" + idle.KindRaw + "' — degraded");
                    break;
            }
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
                warn("settings.mode unrecognized '" + config.Settings.ModeRaw
                    + "' — raw preserved; runtime treats as on");
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
                NormalizeBringUpLifetime(lifetime, label, warn);

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

            bool isActionSource = condition?.Source != null
                && condition.Source.Kind == ValueSourceKind.Action;
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

            // whileTrue + operator-less (except action sources, exempt) → degrade.
            if (lifeKind == LifetimeKind.WhileTrue && !hasOperator && !isActionSource)
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

            // then + durationMs mutual exclusivity: then wins, duration ignored.
            // durationMs default is 5000 and suppressed on write when default — "present"
            // means Then is set (duration always has a value). Spec: both present =
            // durationMs ignored. So when then is active, always ignore durationMs.
            if (thenActive)
            {
                lifetime.DurationMsIgnored = true;
                // Only warn when durationMs was explicitly non-default (authored intent).
                if (lifetime.DurationMs != Lifetime.DefaultDurationMs)
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
            Lifetime lifetime, string label, Action<string> warn)
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
                case ValueSourceKind.Action:
                case ValueSourceKind.Script:
                    // Legal carries; action is migration-only.
                    break;
            }
        }

        private static void NormalizeContentObject(
            ContentObject content, string label, Action<string> warn)
        {
            if (content == null)
                return;

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

            // Over-length text: clamp at runtime (segment text ≤ 3 positions).
            if (content.Kind == ContentKind.Text && !string.IsNullOrEmpty(content.Text))
            {
                if (!LegacyScreen.IsRenderableText(content.Text))
                {
                    // Still try a simple char clamp for over-length pure text.
                    if (content.Text.Length > 3)
                    {
                        content.EffectiveText = content.Text.Substring(0, 3);
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
            CoerceFlashEffect(cwe.Effect, e => cwe.CoerceEffect(ContentEffect.Blink), label, warn);
        }

        private static void CoerceFlashEffect(
            ContentEffect effect, Action<ContentEffect> coerce, string label, Action<string> warn)
        {
            if (effect == ContentEffect.Flash)
            {
                coerce(ContentEffect.Blink);
                warn(label + ": effect 'flash' is not implemented — using blink");
            }
        }

        private static void CoerceFlashOnContent(ContentObject content, string label, Action<string> warn)
        {
            // ContentObject has no effect — effects live on carriers. No-op retained for symmetry.
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
            if (config.Fields == null)
                return;
            int n = config.Fields.Count;
            if (n > SubscriptionBudget)
            {
                warn("subscription budget: " + n + " field entries exceed firmware cap of "
                    + SubscriptionBudget + " (authoring warning; engine enforces at re-plan)");
            }
        }

        private static CatalogField FindCatalogField(WheelCatalog catalog, ushort paramId)
        {
            if (catalog?.Itm?.Pages == null)
                return null;
            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Fields == null)
                    continue;
                foreach (var f in page.Fields)
                {
                    if (f != null && f.ParamId == paramId)
                        return f;
                }
            }
            return null;
        }

        private static int CountPrimaryHosts(WheelCatalog catalog, ushort paramId)
        {
            int n = 0;
            if (catalog?.Itm?.Pages == null)
                return 0;
            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Fields == null)
                    continue;
                foreach (var f in page.Fields)
                {
                    if (f != null && f.ParamId == paramId && f.PrimaryHost == true)
                        n++;
                }
            }
            return n;
        }

        private static string ResolvePrimaryHostCatalogId(WheelCatalog catalog, ushort paramId)
        {
            if (catalog?.Itm?.Pages == null)
                return null;
            string found = null;
            int count = 0;
            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Fields == null)
                    continue;
                foreach (var f in page.Fields)
                {
                    if (f != null && f.ParamId == paramId && f.PrimaryHost == true)
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
