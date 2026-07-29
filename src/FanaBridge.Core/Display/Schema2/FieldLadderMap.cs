using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// One-ladder resolution: merge <c>sharedFields</c> (logical id → FieldEntry) with
    /// param-keyed <c>fields</c> into a single param → FieldEntry map for composition.
    /// <para>
    /// Collision rule (S1): <c>sharedFields</c> wins; the <c>fields[paramId]</c> entry is
    /// the named inert side (kept, marked degraded, contributes nothing). Unresolvable
    /// logical ids are kept inert (never guessed). Without a catalog every sharedFields
    /// entry is inert + warn once (S5).
    /// </para>
    /// </summary>
    public static class FieldLadderMap
    {
        /// <summary>
        /// Build the effective param → FieldEntry map for the composer / condition paths.
        /// Does not mutate the document (degrade marks on entries are runtime-only and
        /// expected to already be set by the validator; this path re-applies inert
        /// filtering without rewriting).
        /// <para>
        /// Enumeration order is document insertion order (sharedFields encounter order,
        /// then fields encounter order) for diagnostics/aggregates; the wire path is
        /// order-stable independently of this list.
        /// </para>
        /// </summary>
        public static IReadOnlyList<KeyValuePair<ushort, FieldEntry>> Build(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            Action<string> warn = null)
        {
            var result = new List<KeyValuePair<ushort, FieldEntry>>();
            if (config == null)
                return result;

            // paramId → shared entry (winner side)
            var sharedByParam = new Dictionary<ushort, FieldEntry>();

            if (config.SharedFields != null)
            {
                // Document encounter order for sharedFields; stable for diagnostics.
                foreach (var kv in config.SharedFields)
                {
                    string logicalId = kv.Key;
                    var entry = kv.Value;
                    if (entry == null || string.IsNullOrEmpty(logicalId))
                        continue;

                    if (entry.DegradedAtLoad)
                        continue; // inert (unknown logical id / no catalog)

                    if (catalog == null)
                        continue; // S5: no binding without catalog

                    var def = CatalogFields.FindDefinition(catalog, logicalId);
                    if (def == null)
                        continue;

                    // First sharedFields entry for a param wins among shared (document order).
                    if (sharedByParam.ContainsKey(def.ParamId))
                        continue;
                    sharedByParam[def.ParamId] = entry;
                    // Preserve document insertion order — do not re-sort by param id.
                    result.Add(new KeyValuePair<ushort, FieldEntry>(def.ParamId, entry));
                }
            }

            // Param-keyed fields: document insertion order; skip inert / shared winners.
            if (config.Fields != null)
            {
                foreach (var kv in config.Fields)
                {
                    if (kv.Value == null)
                        continue;
                    if (sharedByParam.ContainsKey(kv.Key))
                        continue; // S1: shared wins; page-scoped side is named inert
                    if (kv.Value.DegradedAtLoad)
                        continue;
                    result.Add(new KeyValuePair<ushort, FieldEntry>(kv.Key, kv.Value));
                }
            }

            return result;
        }

        /// <summary>
        /// Enumerate every FieldEntry that may contribute overrides for condition/param
        /// planning: effective ladders only (shared winners + non-inert fields).
        /// </summary>
        public static IEnumerable<KeyValuePair<ushort, FieldEntry>> EnumerateEffective(
            DisplayConfigV2 config, WheelCatalog catalog)
            => Build(config, catalog, warn: null);

        /// <summary>
        /// One-ladder field-override lookup: <c>sharedFields</c> first (when the catalog
        /// binds a logical id to <paramref name="paramId"/>), then page-scoped
        /// <c>fields[paramId]</c> when that side is not the inert collision victim.
        /// Shared by the validator (childRef satellites), edit session, and composition.
        /// </summary>
        public static bool TryFindOverride(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            ushort paramId,
            string overrideId,
            out FieldOverride ov)
        {
            ov = null;
            if (config == null || string.IsNullOrEmpty(overrideId))
                return false;

            // Shared ladder owns the param when a non-inert shared entry binds to it.
            if (config.SharedFields != null && catalog != null)
            {
                foreach (var kv in config.SharedFields)
                {
                    var entry = kv.Value;
                    if (entry == null || entry.DegradedAtLoad)
                        continue;
                    if (string.IsNullOrEmpty(kv.Key))
                        continue;
                    var def = CatalogFields.FindDefinition(catalog, kv.Key);
                    if (def == null || def.ParamId != paramId)
                        continue;

                    // Shared owns this param — do not fall through to the inert fields side.
                    return TryMatchOverride(entry.Overrides, overrideId, out ov);
                }
            }

            if (config.Fields != null
                && config.Fields.TryGetValue(paramId, out var fieldEntry)
                && fieldEntry != null
                && !fieldEntry.DegradedAtLoad)
            {
                return TryMatchOverride(fieldEntry.Overrides, overrideId, out ov);
            }

            return false;
        }

        /// <summary>
        /// Resolve the effective <see cref="FieldEntry"/> for a param under the one-ladder
        /// rule (shared first). Null when neither side contributes an active ladder.
        /// </summary>
        public static FieldEntry FindEntry(
            DisplayConfigV2 config, WheelCatalog catalog, ushort paramId)
        {
            if (config == null)
                return null;

            if (config.SharedFields != null && catalog != null)
            {
                foreach (var kv in config.SharedFields)
                {
                    var entry = kv.Value;
                    if (entry == null || entry.DegradedAtLoad || string.IsNullOrEmpty(kv.Key))
                        continue;
                    var def = CatalogFields.FindDefinition(catalog, kv.Key);
                    if (def != null && def.ParamId == paramId)
                        return entry;
                }
            }

            if (config.Fields != null
                && config.Fields.TryGetValue(paramId, out var fieldEntry)
                && fieldEntry != null
                && !fieldEntry.DegradedAtLoad)
            {
                return fieldEntry;
            }

            return null;
        }

        private static bool TryMatchOverride(
            IList<FieldOverride> overrides, string overrideId, out FieldOverride ov)
        {
            ov = null;
            if (overrides == null)
                return false;
            for (int i = 0; i < overrides.Count; i++)
            {
                var o = overrides[i];
                if (o != null
                    && string.Equals(o.Id, overrideId, StringComparison.OrdinalIgnoreCase))
                {
                    ov = o;
                    return true;
                }
            }
            return false;
        }
    }
}
