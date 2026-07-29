using System;
using System.Collections.Generic;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Pure host-local condition-param planner (E7, adjudicated). Collects every
    /// itmField-referenced param in a normalized v2 document and publishes ALL of them
    /// for the itmField value buffer — zero wire cost (the host does not subscribe; the
    /// firmware announces the page set). Gated only on encoder availability
    /// (<paramref name="hasEncoder"/>): a param with no encoder degrades visibly at
    /// validation / plan time.
    ///
    /// Not a wire subscription planner — the 16-param firmware cap is enforced only as a
    /// defensive assert at the ParamDefs/SendValues seam in <c>ItmDisplayDriver</c>.
    /// Not wired into the live path — E8 consumes the plan.
    /// </summary>
    public static class ConditionParamPlanner
    {
        /// <summary>
        /// Plan host-local condition params for the document. Deterministic: same inputs
        /// → same param order and degrade list.
        /// </summary>
        /// <param name="doc">Normalized v2 document.</param>
        /// <param name="hasEncoder">
        /// True when the mapper can encode the param (typically
        /// <c>ItmTelemetryMapper.HasEncoder</c>). Null = treat every param as encodable
        /// (tests that only care about collect order).
        /// </param>
        /// <param name="warn">Optional warn-once sink for no-encoder degrades.</param>
        public static ConditionParamPlan Plan(
            DisplayConfigV2 doc,
            Func<ushort, bool> hasEncoder = null,
            Action<string> warn = null)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            var collected = CollectConditionReferencedParams(doc);
            var selected = new List<ushort>(collected.Count);
            var degraded = new List<ushort>();
            var warned = new HashSet<string>(StringComparer.Ordinal);

            foreach (ushort pid in collected)
            {
                if (hasEncoder != null && !hasEncoder(pid))
                {
                    degraded.Add(pid);
                    if (warn != null && warned.Add("no-encoder:" + pid))
                    {
                        warn("condition param planner: param " + pid
                            + " has no encoder — condition degrades visibly (never fires)");
                    }
                    continue;
                }
                selected.Add(pid);
            }

            return new ConditionParamPlan(selected, degraded);
        }

        /// <summary>
        /// Condition-referenced itmField params in document order (priority seats →
        /// field overrides → hosted layers → wheel-screen rules). Degraded sources
        /// still contribute their param id when parseable (honest plan, gated later
        /// by <see cref="Plan"/>'s encoder check).
        /// </summary>
        public static IReadOnlyList<ushort> CollectConditionReferencedParams(DisplayConfigV2 doc)
        {
            var list = new List<ushort>();
            var seen = new HashSet<ushort>();

            void AddFromSource(ValueSource source)
            {
                if (source == null || source.Kind != ValueSourceKind.ItmField)
                    return;
                if (!TryParseParamId(source.Name, out ushort pid))
                    return;
                if (seen.Add(pid))
                    list.Add(pid);
            }

            void AddFromCondition(Condition condition)
            {
                if (condition?.Source != null)
                    AddFromSource(condition.Source);
            }

            // Priority ladder rows (document order).
            var rows = doc.Priority?.EffectiveRows ?? doc.Priority?.Rows;
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    if (row?.Summons == null)
                        continue;
                    foreach (var s in row.Summons)
                        AddFromCondition(s?.Condition);
                }
            }

            // Fields + sharedFields one-ladder — sorted key for determinism. Without a
            // catalog, sharedFields stay inert (no param binding) and contribute nothing
            // here; callers that have a catalog should prefer FieldLadderMap.Build.
            if (doc.Fields != null)
            {
                var keys = new List<ushort>(doc.Fields.Keys);
                keys.Sort();
                foreach (ushort key in keys)
                {
                    if (!doc.Fields.TryGetValue(key, out var entry) || entry?.Overrides == null)
                        continue;
                    if (entry.DegradedAtLoad)
                        continue;
                    foreach (var ov in entry.Overrides)
                        AddFromCondition(ov?.Condition);
                }
            }

            // Shared field overrides still contribute condition params when resolvable
            // is not required for itmField collection from conditions themselves —
            // walk them by document order so condition sources are never missed.
            if (doc.SharedFields != null)
            {
                foreach (var kv in doc.SharedFields)
                {
                    var entry = kv.Value;
                    if (entry?.Overrides == null || entry.DegradedAtLoad)
                        continue;
                    foreach (var ov in entry.Overrides)
                        AddFromCondition(ov?.Condition);
                }
            }

            // Hosted page layers (pages list order).
            if (doc.Pages != null)
            {
                foreach (var page in doc.Pages)
                {
                    if (page?.Kind != PageEntryKind.HostedPage || page.Layers == null)
                        continue;
                    foreach (var layer in page.Layers)
                        AddFromCondition(layer?.Condition);
                }
            }

            // Wheel-screen rules (array order).
            if (doc.WheelScreen?.Rules != null)
            {
                foreach (var rule in doc.WheelScreen.Rules)
                    AddFromCondition(rule?.Condition);
            }

            return list;
        }

        private static bool TryParseParamId(string name, out ushort paramId)
        {
            paramId = 0;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            // "self" has no absolute id at document collect time — owning field param is
            // already on the page via the field ladder host; skip.
            if (string.Equals(name.Trim(), "self", StringComparison.OrdinalIgnoreCase))
                return false;
            name = name.Trim();
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return ushort.TryParse(
                    name.Substring(2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out paramId);
            }
            return ushort.TryParse(
                name,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out paramId);
        }
    }

    /// <summary>Host-local condition-param plan (all encodable params published).</summary>
    public sealed class ConditionParamPlan
    {
        public ConditionParamPlan(
            IReadOnlyList<ushort> paramIds,
            IReadOnlyList<ushort> noEncoderParams)
        {
            ParamIds = paramIds ?? Array.Empty<ushort>();
            NoEncoderParams = noEncoderParams ?? Array.Empty<ushort>();
            Degraded = NoEncoderParams.Count > 0;
        }

        /// <summary>Params the host will compute and publish for itmField conditions.</summary>
        public IReadOnlyList<ushort> ParamIds { get; }

        /// <summary>Referenced params with no encoder — degrade-visible, never silent.</summary>
        public IReadOnlyList<ushort> NoEncoderParams { get; }

        public bool Degraded { get; }
    }
}
