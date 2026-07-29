using System;
using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Schema2;
using Newtonsoft.Json.Linq;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Which schema member <see cref="DisplayConfigV2EditSession.SetActsAsEntrypoint"/>
    /// targets. Typed components — no delimiter-encoded path grammar.
    /// </summary>
    internal enum ActsAsEntrypointTarget
    {
        /// <summary>Field override: container = param id, member = override id.</summary>
        Field,

        /// <summary>Layer entry: container = page id, member = layer id.</summary>
        Layer,
    }

    /// <summary>
    /// Result of publishing a session's working document through the host write seam.
    /// Conflict when the live document identity no longer matches the identity captured
    /// at <see cref="DisplayConfigV2EditSession.Open"/> (CAS lost race).
    /// </summary>
    internal sealed class DisplayConfigV2ApplyResult
    {
        private DisplayConfigV2ApplyResult(
            bool succeeded, bool conflict, DisplayConfigV2 applied, string message)
        {
            Succeeded = succeeded;
            IsConflict = conflict;
            Applied = applied;
            Message = message;
        }

        /// <summary>True when the host accepted the publish.</summary>
        public bool Succeeded { get; }

        /// <summary>True when the live document identity drifted — surface
        /// <see cref="Message"/> (<see cref="DisplayCopy.ConfigEditConflict"/>).</summary>
        public bool IsConflict { get; }

        /// <summary>Live document after a successful apply; null on conflict / no-op.</summary>
        public DisplayConfigV2 Applied { get; }

        /// <summary>
        /// Ruled surface message when not succeeded; null on success. Views (Priority
        /// round) must show this string — they do not invent conflict copy.
        /// </summary>
        public string Message { get; }

        public static DisplayConfigV2ApplyResult Ok(DisplayConfigV2 applied)
            => new DisplayConfigV2ApplyResult(
                succeeded: true, conflict: false, applied, message: null);

        public static DisplayConfigV2ApplyResult Conflict()
            => new DisplayConfigV2ApplyResult(
                succeeded: false, conflict: true, applied: null,
                message: DisplayCopy.ConfigEditConflict);
    }

    /// <summary>
    /// Q14 edit-model substrate for Priority (and every later v2 editor): opens against a
    /// live document identity, holds an independent working clone, and every mutation
    /// produces a NEW document via <see cref="DisplayConfigV2Serializer.Clone"/> (fresh-
    /// document discipline; originals untouched; untouched members + extension data
    /// round-trip verbatim). Apply uses the host CAS seam
    /// (<see cref="IDisplayPanelHost.TryApplyDisplayConfigV2"/>) so a competing
    /// SetSettings/bake/Apply between open and publish yields
    /// <see cref="DisplayConfigV2ApplyResult.Conflict"/> with
    /// <see cref="DisplayCopy.ConfigEditConflict"/> on <see cref="DisplayConfigV2ApplyResult.Message"/>
    /// — never an overwrite. VIEW consumption of that message lands with the Priority
    /// round. Poll re-projection never mutates an open session (the session owns its clone).
    ///
    /// Validator is invoked post-mutation for notes only (survivors model): a degraded
    /// document is LEGAL and never blocked; the working document is not rewritten by
    /// validation (notes come from a separate Normalize pass on a throwaway clone).
    /// Clone failures fail closed (throw / non-clean notes) — never a silent default document.
    /// </summary>
    internal sealed class DisplayConfigV2EditSession
    {
        private readonly DisplayConfigV2 _openedAgainst;
        private DisplayConfigV2 _document;
        private int _generation;
        private IReadOnlyList<string> _validationNotes = Array.Empty<string>();

        private DisplayConfigV2EditSession(DisplayConfigV2 openedAgainst, DisplayConfigV2 working)
        {
            _openedAgainst = openedAgainst;
            _document = working ?? new DisplayConfigV2();
            RefreshValidationNotes();
        }

        /// <summary>
        /// Open a session against the live document. Captures document identity for the
        /// apply-time CAS check and holds a deep clone as the working document.
        /// Null live yields a session over a fresh default (apply will conflict unless
        /// the host is also null).
        /// </summary>
        public static DisplayConfigV2EditSession Open(DisplayConfigV2 live)
        {
            var working = DisplayConfigV2Serializer.Clone(live);
            return new DisplayConfigV2EditSession(live, working);
        }

        /// <summary>The live document identity captured at open (not the working clone).</summary>
        public DisplayConfigV2 OpenedAgainst => _openedAgainst;

        /// <summary>Current working document. Never mutate in place — use the helpers.</summary>
        public DisplayConfigV2 Document => _document;

        /// <summary>
        /// Monotonic edit generation: starts at 0, increments on every successful
        /// structural mutation. Views can key rebuilds off this without watching the
        /// document reference alone.
        /// </summary>
        public int Generation => _generation;

        /// <summary>
        /// Validation notes from the latest post-mutation Normalize pass (survivors).
        /// Never blocks mutation; empty when clean.
        /// </summary>
        public IReadOnlyList<string> ValidationNotes => _validationNotes;

        // ── Publish ──────────────────────────────────────────────────────

        /// <summary>
        /// Publish the working document through
        /// <see cref="IDisplayPanelHost.TryApplyDisplayConfigV2"/> using the identity
        /// captured at open as the CAS expected value. On conflict, returns
        /// <see cref="DisplayConfigV2ApplyResult.Conflict"/> with
        /// <see cref="DisplayCopy.ConfigEditConflict"/> on
        /// <see cref="DisplayConfigV2ApplyResult.Message"/> (views surface that string;
        /// Priority-round consumption).
        /// </summary>
        public DisplayConfigV2ApplyResult TryApply(IDisplayPanelHost host)
        {
            if (host == null)
                throw new ArgumentNullException(nameof(host));

            if (!host.TryApplyDisplayConfigV2(_openedAgainst, _document))
                return DisplayConfigV2ApplyResult.Conflict();

            return DisplayConfigV2ApplyResult.Ok(host.GetDisplayConfigV2());
        }

        // ── Mutation helpers (each → new document) ───────────────────────

        /// <summary>
        /// Reorder <see cref="PriorityLadder.Rows"/> (never EffectiveRows). No-op when
        /// either index is out of range or indices are equal.
        /// </summary>
        public DisplayConfigV2 MoveRow(int fromIndex, int toIndex)
        {
            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                if (fromIndex < 0 || fromIndex >= rows.Count
                    || toIndex < 0 || toIndex >= rows.Count
                    || fromIndex == toIndex)
                    return false;

                var item = rows[fromIndex];
                rows.RemoveAt(fromIndex);
                rows.Insert(toIndex, item);
                return true;
            });
        }

        /// <summary>
        /// Append a summon to the row identified by <paramref name="rowId"/>. Assigns a
        /// GUID id when the summon's id is blank. Clones the supplied summon first — the
        /// caller's instance is never mutated or retained. No-op when the row is missing.
        /// </summary>
        public DisplayConfigV2 AddSummon(string rowId, Summon summon)
        {
            if (summon == null)
                return _document;

            // Clone before any mutation so generated IDs never touch the caller's object.
            var owned = DisplayConfigV2Serializer.CloneNode(summon);
            if (string.IsNullOrWhiteSpace(owned.Id))
                owned.Id = Guid.NewGuid().ToString("N");

            return Mutate(doc =>
            {
                var row = FindRow(doc, rowId);
                if (row == null)
                    return false;

                if (row.Summons == null)
                    row.Summons = new List<Summon>();
                row.Summons.Add(owned);
                return true;
            });
        }

        /// <summary>
        /// Remove a summon from the row. When the row is a satellite and that was its
        /// last summon (and it has no ChildRef — <c>row.ChildRef == null</c> is the only
        /// absence test; malformed/future ChildRef shapes stay), the row itself is
        /// removed — seats keep an empty summons list (bring-up home still needs the seat).
        /// </summary>
        public DisplayConfigV2 RemoveSummon(string rowId, string summonId)
        {
            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                int rowIndex = IndexOfRow(rows, rowId);
                if (rowIndex < 0)
                    return false;

                var row = rows[rowIndex];
                if (row.Summons == null)
                    return false;

                int sIndex = IndexOfSummon(row.Summons, summonId);
                if (sIndex < 0)
                    return false;

                row.Summons.RemoveAt(sIndex);

                // ChildRef presence is reference-null only: extension-data-only / future
                // shapes must survive summon removal (degraded, preserved).
                if (row.Kind == PriorityRowKind.Satellite
                    && row.Summons.Count == 0
                    && row.ChildRef == null)
                {
                    rows.RemoveAt(rowIndex);
                }

                return true;
            });
        }

        /// <summary>Set <see cref="Summon.Enabled"/> on a summon. No-op when missing.</summary>
        public DisplayConfigV2 SetSummonEnabled(string rowId, string summonId, bool enabled)
        {
            return Mutate(doc =>
            {
                var row = FindRow(doc, rowId);
                if (row?.Summons == null)
                    return false;

                int sIndex = IndexOfSummon(row.Summons, summonId);
                if (sIndex < 0)
                    return false;

                row.Summons[sIndex].Enabled = enabled;
                return true;
            });
        }

        /// <summary>
        /// Move one summon out of <paramref name="sourceRowId"/> into a new satellite
        /// row (summons-satellite shape): Kind = Satellite, Target copied from the
        /// source when present, Summons = [that summon]. Inserted immediately after the
        /// source. No-op when the source or summon is missing.
        /// </summary>
        public DisplayConfigV2 SplitSatellite(string sourceRowId, string summonId)
        {
            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                int sourceIndex = IndexOfRow(rows, sourceRowId);
                if (sourceIndex < 0)
                    return false;

                var source = rows[sourceIndex];
                if (source.Summons == null)
                    return false;

                int sIndex = IndexOfSummon(source.Summons, summonId);
                if (sIndex < 0)
                    return false;

                var summon = source.Summons[sIndex];
                source.Summons.RemoveAt(sIndex);

                var satellite = new PriorityRow
                {
                    Kind = PriorityRowKind.Satellite,
                    Id = Guid.NewGuid().ToString("N"),
                    Target = source.Target,
                    Summons = new List<Summon> { summon },
                };
                rows.Insert(sourceIndex + 1, satellite);
                return true;
            });
        }

        /// <summary>
        /// Insert a ChildRef-satellite after <paramref name="afterRowId"/> (or at end
        /// when null/unknown). Digest shape: Kind = Satellite + <see cref="ChildRef"/>.
        /// Clones the supplied ChildRef before attachment.
        /// </summary>
        public DisplayConfigV2 SplitSatellite(string afterRowId, ChildRef childRef)
        {
            if (childRef == null)
                return _document;

            var owned = DisplayConfigV2Serializer.CloneNode(childRef);

            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                var satellite = new PriorityRow
                {
                    Kind = PriorityRowKind.Satellite,
                    Id = Guid.NewGuid().ToString("N"),
                    ChildRef = owned,
                };

                int insertAt = rows.Count;
                if (!string.IsNullOrEmpty(afterRowId))
                {
                    int after = IndexOfRow(rows, afterRowId);
                    if (after >= 0)
                        insertAt = after + 1;
                }

                rows.Insert(insertAt, satellite);
                return true;
            });
        }

        /// <summary>
        /// Set the manual row's <see cref="PriorityRow.ReturnToRestAfterMs"/>. When no
        /// manual row exists in authored Rows, one is written (materialization on edit).
        /// Null ms = off.
        /// </summary>
        public DisplayConfigV2 SetReturnToRestAfterMs(int? ms)
        {
            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                PriorityRow manual = null;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null && rows[i].Kind == PriorityRowKind.Manual)
                    {
                        manual = rows[i];
                        break;
                    }
                }

                if (manual == null)
                {
                    manual = new PriorityRow { Kind = PriorityRowKind.Manual };
                    rows.Add(manual);
                }

                manual.ReturnToRestAfterMs = ms;
                return true;
            });
        }

        /// <summary>
        /// Set <see cref="RestBlock.Idle"/>. Null clears to absent/blank default.
        /// Clones a non-null IdleSpec before attachment.
        /// </summary>
        public DisplayConfigV2 SetIdle(IdleSpec idle)
        {
            var owned = idle == null ? null : DisplayConfigV2Serializer.CloneNode(idle);

            return Mutate(doc =>
            {
                EnsurePriority(doc);
                if (doc.Priority.Rest == null)
                    doc.Priority.Rest = new RestBlock();
                doc.Priority.Rest.Idle = owned;
                return true;
            });
        }

        /// <summary>
        /// Set <see cref="FieldOverride.ActsAsEntrypoint"/> or
        /// <see cref="LayerEntry.ActsAsEntrypoint"/> via typed components (no delimiter
        /// path grammar). Id comparisons match the validator
        /// (<see cref="StringComparison.OrdinalIgnoreCase"/>). No-op when unresolved.
        /// </summary>
        public DisplayConfigV2 SetActsAsEntrypoint(
            ActsAsEntrypointTarget target, string containerId, string memberId, bool value)
        {
            if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(memberId))
                return _document;

            return Mutate(doc => TrySetActsAsEntrypoint(doc, target, containerId, memberId, value));
        }

        /// <summary>
        /// Clone-existing-then-mutate a summon. The existing node is fully cloned
        /// (Name / Runs / source variants / hysteresis / directions / extension data
        /// survive); only non-null authored fields on <paramref name="summon"/> replace
        /// the corresponding existing fields. Id is always <paramref name="summonId"/>.
        /// No-op when the row or summon is missing.
        /// </summary>
        public DisplayConfigV2 UpdateSummon(string rowId, string summonId, Summon summon)
        {
            if (summon == null || string.IsNullOrEmpty(summonId))
                return _document;

            return Mutate(doc =>
            {
                var row = FindRow(doc, rowId);
                if (row?.Summons == null)
                    return false;

                int sIndex = IndexOfSummon(row.Summons, summonId);
                if (sIndex < 0)
                    return false;

                var existing = row.Summons[sIndex];
                if (existing == null)
                    return false;

                var owned = DisplayConfigV2Serializer.CloneNode(existing);
                owned.Id = summonId;
                ApplySummonEdits(owned, summon);
                row.Summons[sIndex] = owned;
                return true;
            });
        }

        /// <summary>
        /// Set <see cref="RestBlock.InSessionPage"/> (Base page). Accepts only
        /// <see cref="PageRefKind.ItmPage"/> | <see cref="PageRefKind.HostedPage"/>;
        /// cycle (and other) refs are rejected with a validation note and leave the
        /// document unchanged. Null clears the choice (engine default walk).
        /// </summary>
        public DisplayConfigV2 SetInSessionPage(PageRef page)
        {
            if (page != null
                && page.Kind != PageRefKind.ItmPage
                && page.Kind != PageRefKind.HostedPage)
            {
                _validationNotes = new[] { DisplayCopy.InSessionPageMustBeItmOrHosted };
                return _document;
            }

            var owned = page == null ? null : DisplayConfigV2Serializer.CloneNode(page);

            return Mutate(doc =>
            {
                EnsurePriority(doc);
                if (doc.Priority.Rest == null)
                    doc.Priority.Rest = new RestBlock();
                doc.Priority.Rest.InSessionPage = owned;
                return true;
            });
        }

        /// <summary>
        /// Q2: ensure a MaterializedAtLoad seat exists in authored <see cref="PriorityLadder.Rows"/>
        /// before its first reorder. Inserts a full clone of <paramref name="seed"/>
        /// (BringUpLifetime, ChildRef, lifetime, timer, raw discriminators, extension data)
        /// when missing. No-op when already present.
        /// </summary>
        public DisplayConfigV2 EnsureAuthoredRow(PriorityRow seed)
        {
            if (seed == null || string.IsNullOrEmpty(seed.Id))
                return _document;

            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                if (IndexOfRow(rows, seed.Id) >= 0)
                    return false;

                var owned = DisplayConfigV2Serializer.CloneNode(seed);
                if (owned.Kind == PriorityRowKind.Unknown)
                    owned.Kind = PriorityRowKind.Seat;
                // Insert above any manual row (materialized seats sit above manual).
                int insertAt = rows.Count;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null && rows[i].Kind == PriorityRowKind.Manual)
                    {
                        insertAt = i;
                        break;
                    }
                }
                rows.Insert(insertAt, owned);
                return true;
            });
        }

        /// <summary>
        /// Owner ruling (removal a): remove every ranked row whose
        /// <see cref="PriorityRow.Target"/> resolves to <paramref name="target"/>.
        /// <see cref="PageEntry"/> and authored field overrides are untouched.
        /// Manual rows are never removed by target. No-op when target is null / unmatched.
        /// </summary>
        public DisplayConfigV2 RemoveRowsForTarget(PageRef target)
        {
            if (target == null)
                return _document;

            string key = TargetKey(target);
            if (key == null)
                return _document;

            return Mutate(doc =>
            {
                var rows = RowsOf(doc);
                bool any = false;
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    var row = rows[i];
                    if (row == null || row.Kind == PriorityRowKind.Manual)
                        continue;
                    if (!string.Equals(TargetKey(row.Target), key, StringComparison.Ordinal))
                        continue;
                    rows.RemoveAt(i);
                    any = true;
                }
                return any;
            });
        }

        /// <summary>
        /// Precomputed remove-all set: one session opens at confirm-entry, computes this
        /// set once under the exclusivity law, the confirm renders from it, and Yes
        /// applies <em>this</em> set via the same session (conflict → re-confirm).
        /// </summary>
        public sealed class PageContentRemovalPlan
        {
            internal PageContentRemovalPlan(
                PageRef target,
                string targetKey,
                int rankCount,
                int contentCount,
                HashSet<ushort> exclusiveParams,
                bool clearHostedLayers)
            {
                Target = target;
                TargetKey = targetKey;
                RankCount = rankCount;
                ContentCount = contentCount;
                ExclusiveParams = exclusiveParams ?? new HashSet<ushort>();
                ClearHostedLayers = clearHostedLayers;
            }

            public PageRef Target { get; }
            public int RankCount { get; }
            /// <summary>Exclusive override ladders (ITM) or hosted layers cleared.</summary>
            public int ContentCount { get; }
            internal string TargetKey { get; }
            internal HashSet<ushort> ExclusiveParams { get; }
            internal bool ClearHostedLayers { get; }
        }

        /// <summary>
        /// Compute the remove-all set once against this session's working document.
        /// Fail-closed when the target page is not resolvable in the catalog.
        /// </summary>
        public bool TryPlanRemovePageContent(
            PageRef target, WheelCatalog catalog, out PageContentRemovalPlan plan)
        {
            plan = null;
            if (!CanRemovePageContent(target, catalog))
                return false;

            string key = TargetKey(target);
            if (key == null)
                return false;

            int rankCount = 0;
            var rows = _document?.Priority?.Rows;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null || row.Kind == PriorityRowKind.Manual)
                        continue;
                    if (string.Equals(TargetKey(row.Target), key, StringComparison.Ordinal))
                        rankCount++;
                }
            }

            var exclusive = new HashSet<ushort>();
            int contentCount = 0;
            bool clearHosted = false;

            if (target.Kind == PageRefKind.ItmPage)
            {
                exclusive = ExclusiveParamsOnCatalogPage(catalog, target.CatalogPageId);
                if (_document?.Fields != null)
                {
                    foreach (var kv in _document.Fields)
                    {
                        if (!exclusive.Contains(kv.Key))
                            continue;
                        if (kv.Value?.Overrides != null)
                            contentCount += kv.Value.Overrides.Count;
                    }
                }
            }
            else if (target.Kind == PageRefKind.HostedPage)
            {
                clearHosted = true;
                if (_document?.Pages != null)
                {
                    for (int p = 0; p < _document.Pages.Count; p++)
                    {
                        var page = _document.Pages[p];
                        if (page == null
                            || page.Kind != PageEntryKind.HostedPage
                            || !string.Equals(page.Id, target.Id, StringComparison.Ordinal))
                            continue;
                        contentCount = page.Layers?.Count ?? 0;
                        break;
                    }
                }
            }

            plan = new PageContentRemovalPlan(
                target, key, rankCount, contentCount, exclusive, clearHosted);
            return true;
        }

        /// <summary>
        /// Apply a plan produced by <see cref="TryPlanRemovePageContent"/> on this
        /// session — the exclusive param set and row key are taken from the plan
        /// (not recomputed).
        /// </summary>
        public DisplayConfigV2 ApplyPageContentRemoval(PageContentRemovalPlan plan)
        {
            if (plan == null || plan.TargetKey == null)
                return _document;

            return Mutate(doc =>
            {
                bool any = false;
                string key = plan.TargetKey;

                var rows = RowsOf(doc);
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    var row = rows[i];
                    if (row == null || row.Kind == PriorityRowKind.Manual)
                        continue;
                    if (!string.Equals(TargetKey(row.Target), key, StringComparison.Ordinal))
                        continue;
                    rows.RemoveAt(i);
                    any = true;
                }

                if (plan.ExclusiveParams.Count > 0 && doc.Fields != null)
                {
                    foreach (var kv in doc.Fields)
                    {
                        if (!plan.ExclusiveParams.Contains(kv.Key))
                            continue;
                        var entry = kv.Value;
                        if (entry?.Overrides == null || entry.Overrides.Count == 0)
                            continue;
                        entry.Overrides.Clear();
                        any = true;
                    }
                }

                if (plan.ClearHostedLayers
                    && plan.Target != null
                    && !string.IsNullOrEmpty(plan.Target.Id)
                    && doc.Pages != null)
                {
                    for (int p = 0; p < doc.Pages.Count; p++)
                    {
                        var page = doc.Pages[p];
                        if (page == null
                            || page.Kind != PageEntryKind.HostedPage
                            || !string.Equals(page.Id, plan.Target.Id, StringComparison.Ordinal))
                            continue;
                        if (page.Layers != null && page.Layers.Count > 0)
                        {
                            page.Layers.Clear();
                            any = true;
                        }
                    }
                }

                return any;
            });
        }

        /// <summary>
        /// Owner ruling (removal b) + exclusivity law: plan then apply in one call.
        /// Prefer the plan/apply pair at the UI confirm boundary so count and delete
        /// share one set. No-op / fail-closed when the target is unresolvable.
        /// </summary>
        public DisplayConfigV2 RemovePageContent(PageRef target, WheelCatalog catalog = null)
        {
            if (!TryPlanRemovePageContent(target, catalog, out var plan))
                return _document;
            return ApplyPageContentRemoval(plan);
        }

        /// <summary>
        /// Whether the destructive remove-all option is available for
        /// <paramref name="target"/>. ITM requires the <em>target page</em> to resolve
        /// in the catalog (any-nonempty catalog is not resolution). Fail-closed.
        /// </summary>
        public static bool CanRemovePageContent(PageRef target, WheelCatalog catalog)
        {
            if (target == null)
                return false;
            if (target.Kind == PageRefKind.HostedPage)
                return !string.IsNullOrEmpty(target.Id);
            if (target.Kind == PageRefKind.ItmPage)
            {
                return !string.IsNullOrEmpty(target.CatalogPageId)
                    && FindCatalogPage(catalog, target.CatalogPageId) != null;
            }
            return false;
        }

        /// <summary>
        /// Count override ladders that would be deleted by remove-all under the
        /// exclusivity law (confirm copy helper; prefer a
        /// <see cref="PageContentRemovalPlan"/> at the UI boundary).
        /// </summary>
        public static int CountExclusiveOverridesForRemoval(
            DisplayConfigV2 config, PageRef target, WheelCatalog catalog)
        {
            if (target == null || config == null)
                return 0;

            if (target.Kind == PageRefKind.HostedPage && config.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var p = config.Pages[i];
                    if (p != null
                        && string.Equals(p.Id, target.Id, StringComparison.Ordinal)
                        && p.Layers != null)
                        return p.Layers.Count;
                }
                return 0;
            }

            if (target.Kind != PageRefKind.ItmPage
                || string.IsNullOrEmpty(target.CatalogPageId)
                || FindCatalogPage(catalog, target.CatalogPageId) == null
                || config.Fields == null)
                return 0;

            var exclusive = ExclusiveParamsOnCatalogPage(catalog, target.CatalogPageId);
            int count = 0;
            foreach (var kv in config.Fields)
            {
                if (!exclusive.Contains(kv.Key))
                    continue;
                if (kv.Value?.Overrides != null)
                    count += kv.Value.Overrides.Count;
            }
            return count;
        }

        /// <summary>Resolve a catalog page by id; null when missing (fail-closed).</summary>
        internal static CatalogPage FindCatalogPage(WheelCatalog catalog, string catalogPageId)
        {
            var pages = catalog?.Itm?.Pages;
            if (pages == null || string.IsNullOrEmpty(catalogPageId))
                return null;
            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                if (page != null
                    && string.Equals(page.Id, catalogPageId, StringComparison.Ordinal))
                    return page;
            }
            return null;
        }

        // ── Internals ────────────────────────────────────────────────────

        private DisplayConfigV2 Mutate(Func<DisplayConfigV2, bool> edit)
        {
            // Fresh-document discipline: always clone first so the prior document
            // identity (and any external holder of it) stays untouched. Clone fails
            // closed (throws) — never silently replaces with a default document.
            var next = DisplayConfigV2Serializer.Clone(_document);
            bool changed = edit(next);
            if (!changed)
                return _document;

            _document = next;
            _generation++;
            RefreshValidationNotes();
            return _document;
        }

        private void RefreshValidationNotes()
        {
            // Survivors model: notes only. Normalize a throwaway clone so the working
            // document is never rewritten by validation. Clone failure must not fail-open
            // into "clean notes on an empty default".
            try
            {
                var notes = new List<string>();
                var probe = DisplayConfigV2Serializer.Clone(_document);
                DisplayConfigV2Validator.Normalize(probe, msg =>
                {
                    if (!string.IsNullOrEmpty(msg))
                        notes.Add(msg);
                });
                _validationNotes = notes.Count == 0
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : notes;
            }
            catch (InvalidOperationException)
            {
                _validationNotes = new[] { DisplayCopy.ConfigEditCloneFailed };
            }
        }

        private static void EnsurePriority(DisplayConfigV2 doc)
        {
            if (doc.Priority == null)
                doc.Priority = new PriorityLadder();
            if (doc.Priority.Rows == null)
                doc.Priority.Rows = new List<PriorityRow>();
        }

        private static List<PriorityRow> RowsOf(DisplayConfigV2 doc)
        {
            EnsurePriority(doc);
            return doc.Priority.Rows;
        }

        private static PriorityRow FindRow(DisplayConfigV2 doc, string rowId)
        {
            if (string.IsNullOrEmpty(rowId))
                return null;
            var rows = RowsOf(doc);
            int i = IndexOfRow(rows, rowId);
            return i < 0 ? null : rows[i];
        }

        private static int IndexOfRow(List<PriorityRow> rows, string rowId)
        {
            if (rows == null || string.IsNullOrEmpty(rowId))
                return -1;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] != null
                    && string.Equals(rows[i].Id, rowId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static int IndexOfSummon(List<Summon> summons, string summonId)
        {
            if (summons == null || string.IsNullOrEmpty(summonId))
                return -1;
            for (int i = 0; i < summons.Count; i++)
            {
                if (summons[i] != null
                    && string.Equals(summons[i].Id, summonId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static bool TrySetActsAsEntrypoint(
            DisplayConfigV2 doc,
            ActsAsEntrypointTarget target,
            string containerId,
            string memberId,
            bool value)
        {
            if (target == ActsAsEntrypointTarget.Field)
            {
                if (!ushort.TryParse(containerId, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out ushort paramId))
                    return false;
                if (doc.Fields == null || !doc.Fields.TryGetValue(paramId, out var entry)
                    || entry?.Overrides == null)
                    return false;

                for (int i = 0; i < entry.Overrides.Count; i++)
                {
                    var ov = entry.Overrides[i];
                    if (ov != null
                        && string.Equals(ov.Id, memberId, StringComparison.OrdinalIgnoreCase))
                    {
                        ov.ActsAsEntrypoint = value;
                        return true;
                    }
                }
                return false;
            }

            if (target == ActsAsEntrypointTarget.Layer)
            {
                if (doc.Pages == null)
                    return false;
                for (int p = 0; p < doc.Pages.Count; p++)
                {
                    var page = doc.Pages[p];
                    if (page == null || page.Layers == null)
                        continue;
                    if (!string.Equals(page.Id, containerId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    for (int l = 0; l < page.Layers.Count; l++)
                    {
                        var layer = page.Layers[l];
                        if (layer != null
                            && string.Equals(layer.Id, memberId, StringComparison.OrdinalIgnoreCase))
                        {
                            layer.ActsAsEntrypoint = value;
                            return true;
                        }
                    }
                }
                return false;
            }

            return false;
        }

        /// <summary>
        /// Stable target key matching validator materialization keys:
        /// "itm:{catalogPageId}" / "hosted:{id}" / "cycle:{id}".
        /// </summary>
        private static string TargetKey(PageRef target)
        {
            if (target == null)
                return null;
            switch (target.Kind)
            {
                case PageRefKind.ItmPage:
                    return string.IsNullOrEmpty(target.CatalogPageId)
                        ? null
                        : "itm:" + target.CatalogPageId;
                case PageRefKind.HostedPage:
                    return string.IsNullOrEmpty(target.Id)
                        ? null
                        : "hosted:" + target.Id;
                case PageRefKind.Cycle:
                    return string.IsNullOrEmpty(target.Id)
                        ? null
                        : "cycle:" + target.Id;
                default:
                    return null;
            }
        }

        private static HashSet<ushort> ParamsOnCatalogPage(WheelCatalog catalog, string catalogPageId)
        {
            var set = new HashSet<ushort>();
            var pages = catalog?.Itm?.Pages;
            if (pages == null || string.IsNullOrEmpty(catalogPageId))
                return set;
            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                if (page == null
                    || !string.Equals(page.Id, catalogPageId, StringComparison.Ordinal))
                    continue;
                if (page.Fields == null)
                    break;
                for (int f = 0; f < page.Fields.Count; f++)
                {
                    if (page.Fields[f] != null)
                        set.Add(page.Fields[f].ParamId);
                }
                break;
            }
            return set;
        }

        /// <summary>
        /// Catalog placement = REACH, not ownership. A param is exclusive to
        /// <paramref name="catalogPageId"/> when it appears on that page and on no other
        /// catalog page. Shared ladders (e.g. pbme params 1/4 on all five ITM pages)
        /// are excluded.
        /// </summary>
        internal static HashSet<ushort> ExclusiveParamsOnCatalogPage(
            WheelCatalog catalog, string catalogPageId)
        {
            var exclusive = new HashSet<ushort>();
            var onPage = ParamsOnCatalogPage(catalog, catalogPageId);
            if (onPage.Count == 0)
                return exclusive;

            var pages = catalog?.Itm?.Pages;
            if (pages == null)
                return exclusive;

            foreach (ushort paramId in onPage)
            {
                int placements = 0;
                for (int i = 0; i < pages.Count; i++)
                {
                    var page = pages[i];
                    if (page?.Fields == null)
                        continue;
                    for (int f = 0; f < page.Fields.Count; f++)
                    {
                        if (page.Fields[f] != null && page.Fields[f].ParamId == paramId)
                        {
                            placements++;
                            break;
                        }
                    }
                    if (placements > 1)
                        break;
                }
                if (placements == 1)
                    exclusive.Add(paramId);
            }
            return exclusive;
        }

        /// <summary>
        /// Overlay form-edited fields onto a cloned existing summon. Condition merges
        /// Source/Operator/Value field-wise (source extension data survives; hysteresis
        /// only when the patch authors it). Lifetime merges field-wise: kind/duration
        /// from the form; direction, then-state, and lifetime extension data survive
        /// verbatim. Name/Enabled/Runs apply when the patch authors them; summon
        /// ExtensionData stays on the clone.
        /// </summary>
        private static void ApplySummonEdits(Summon target, Summon patch)
        {
            if (target == null || patch == null)
                return;

            if (patch.Condition != null)
            {
                if (target.Condition == null)
                {
                    target.Condition = DisplayConfigV2Serializer.CloneNode(patch.Condition);
                }
                else
                {
                    if (patch.Condition.Source != null)
                        MergeValueSource(target.Condition, patch.Condition.Source);
                    target.Condition.Operator = patch.Condition.Operator;
                    target.Condition.Value = patch.Condition.Value;
                    // Hysteresis: only overwrite when the patch authors one; otherwise keep.
                    if (patch.Condition.Hysteresis != null)
                        target.Condition.Hysteresis = patch.Condition.Hysteresis;
                }
            }

            if (patch.Lifetime != null)
                MergeLifetime(target, patch.Lifetime);

            if (patch.Name != null)
                target.Name = patch.Name;

            // Enabled: form/edit paths that preserve the prior value must copy it onto the
            // patch first (default true would otherwise re-enable a turned-off summon).
            target.Enabled = patch.Enabled;

            if (patch.RunsRaw != null)
                target.RunsRaw = patch.RunsRaw;

            // ExtensionData stays on the clone. Additive keys from a full-replace patch
            // are merged without dropping existing unknowns.
            if (patch.ExtensionData != null && patch.ExtensionData.Count > 0)
            {
                if (target.ExtensionData == null)
                    target.ExtensionData = new Dictionary<string, JToken>();
                foreach (var kv in patch.ExtensionData)
                    target.ExtensionData[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Field-wise source merge: kind + name from the patch; extension data on the
        /// existing source survives when the form does not author any.
        /// </summary>
        private static void MergeValueSource(Condition targetCondition, ValueSource patchSource)
        {
            if (targetCondition == null || patchSource == null)
                return;

            if (targetCondition.Source == null)
            {
                targetCondition.Source = DisplayConfigV2Serializer.CloneNode(patchSource);
                return;
            }

            var existing = targetCondition.Source;
            // Kind: prefer patch when authored.
            if (patchSource.Kind != ValueSourceKind.Unknown
                || !string.IsNullOrEmpty(patchSource.KindRaw))
            {
                existing.Kind = patchSource.Kind;
            }
            if (patchSource.Name != null)
                existing.Name = patchSource.Name;
            // ExtensionData: keep existing; only add keys the patch authors.
            if (patchSource.ExtensionData != null && patchSource.ExtensionData.Count > 0)
            {
                if (existing.ExtensionData == null)
                    existing.ExtensionData = new Dictionary<string, JToken>();
                foreach (var kv in patchSource.ExtensionData)
                    existing.ExtensionData[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Field-wise lifetime merge: kind + durationMs from the form patch;
        /// direction, then-state, and extension data on the existing lifetime survive
        /// when the form does not author them.
        /// </summary>
        private static void MergeLifetime(Summon target, Lifetime patchLife)
        {
            if (target == null || patchLife == null)
                return;

            if (target.Lifetime == null)
            {
                target.Lifetime = DisplayConfigV2Serializer.CloneNode(patchLife);
                return;
            }

            var existing = target.Lifetime;
            if (patchLife.Kind != LifetimeKind.Unknown
                || !string.IsNullOrEmpty(patchLife.KindRaw))
            {
                existing.Kind = patchLife.Kind;
            }
            if (patchLife.DurationMsPresent)
                existing.DurationMs = patchLife.DurationMs;
            // Direction / Then / ExtensionData: leave existing when patch is sparse
            // (form never authors them). Only overwrite when the patch supplies them.
            if (!string.IsNullOrEmpty(patchLife.DirectionRaw)
                || (patchLife.Direction != ChangeDirection.Any
                    && patchLife.Direction != ChangeDirection.Unknown))
            {
                existing.Direction = patchLife.Direction;
            }
            if (patchLife.Then != null || !string.IsNullOrEmpty(patchLife.ThenRaw))
                existing.Then = patchLife.Then;
            if (patchLife.ExtensionData != null && patchLife.ExtensionData.Count > 0)
            {
                if (existing.ExtensionData == null)
                    existing.ExtensionData = new Dictionary<string, JToken>();
                foreach (var kv in patchLife.ExtensionData)
                    existing.ExtensionData[kv.Key] = kv.Value;
            }
        }
    }
}
