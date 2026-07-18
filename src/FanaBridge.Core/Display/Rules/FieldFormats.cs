using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Validated vocabulary for <see cref="FieldMapping.Format"/> — a small per-param
    /// family of keys the ITM mapper understands. Unknown text is warn-and-dropped at
    /// load (format only; the mapping itself stays). Gear and EngineMapping cannot be
    /// remapped at all (special wire text forms); the Pages UI locks those fields.
    /// </summary>
    public static class FieldFormats
    {
        /// <summary>Total params (Lap / Position / Fuel): show the "/total" suffix.</summary>
        public const string WithTotal = "withTotal";

        /// <summary>Total params: suppress the "/total" suffix (blank " " on the wire).
        /// Temperature params: drop the C/F unit suffix the same way.</summary>
        public const string Bare = "bare";

        /// <summary>Temperature params: show the unit label (C/F/K) from the frame.</summary>
        public const string Unit = "unit";

        /// <summary>Whether <paramref name="paramId"/> is a total-suffix parameter
        /// (Lap, Position, or Fuel).</summary>
        public static bool IsTotalParam(ushort paramId)
            => paramId == ItmParam.Lap
            || paramId == ItmParam.Position
            || paramId == ItmParam.Fuel;

        /// <summary>Whether <paramref name="paramId"/> is a temperature parameter that
        /// carries a unit label.</summary>
        public static bool IsTempParam(ushort paramId)
            => paramId == ItmParam.OilTemp
            || paramId == ItmParam.TyreFlTemp
            || paramId == ItmParam.TyreFrTemp
            || paramId == ItmParam.TyreRlTemp
            || paramId == ItmParam.TyreRrTemp;

        /// <summary>Whether field-mapping overrides are forbidden for this param
        /// (Gear and EngineMapping keep special wire text forms).</summary>
        public static bool IsOverrideExcluded(ushort paramId)
            => paramId == ItmParam.Gear || paramId == ItmParam.EngineMapping;

        /// <summary>
        /// The formats allowed for <paramref name="paramId"/>, or an empty list when
        /// the param has no format options (validator clears unknown text). Order is
        /// the UI dropdown order; index 0 is the built-in default when no source
        /// override is present.
        /// </summary>
        public static IReadOnlyList<string> AllowedFor(ushort paramId)
        {
            if (IsTotalParam(paramId)) return TotalFormats;
            if (IsTempParam(paramId)) return TempFormats;
            return Array.Empty<string>();
        }

        /// <summary>Whether <paramref name="format"/> is in the allowed set for
        /// <paramref name="paramId"/> (case-sensitive; vocabulary is camelCase).</summary>
        public static bool IsAllowed(ushort paramId, string format)
        {
            if (string.IsNullOrEmpty(format)) return false;
            var allowed = AllowedFor(paramId);
            for (int i = 0; i < allowed.Count; i++)
                if (string.Equals(allowed[i], format, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static readonly IReadOnlyList<string> TotalFormats =
            Array.AsReadOnly(new[] { WithTotal, Bare });

        private static readonly IReadOnlyList<string> TempFormats =
            Array.AsReadOnly(new[] { Unit, Bare });
    }
}
