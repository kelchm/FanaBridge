using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Validated vocabulary for field format keys — a small per-param family of keys
    /// the ITM mapper understands. Unknown text is warn-and-dropped at load (format only;
    /// the mapping itself stays).
    /// <para>
    /// Standing law: capability facts live in catalog/envelope DATA, never as per-field
    /// code law. There is no hardcoded per-param exclusion list — envelope
    /// <c>overridable</c> / region capability gates replace any former code lock.
    /// </para>
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

        /// <summary>Gear: fold empty/blank input to neutral (kept P10a behavior).</summary>
        public const string Neutral = "neutral";

        /// <summary>Gear: allow blank display for empty gear input (blank-vs-neutral pair).</summary>
        public const string Blank = "blank";

        /// <summary>Speed: whole-number display (family default).</summary>
        public const string Whole = "whole";

        /// <summary>Speed: round resolved scalars to one decimal before the typed encoder.</summary>
        public const string OneDecimal = "oneDecimal";

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

        /// <summary>Whether <paramref name="paramId"/> is the gear param.</summary>
        public static bool IsGearParam(ushort paramId)
            => paramId == ItmParam.Gear;

        /// <summary>Whether <paramref name="paramId"/> is the speed param.</summary>
        public static bool IsSpeedParam(ushort paramId)
            => paramId == ItmParam.Speed;

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
            if (IsGearParam(paramId)) return GearFormats;
            if (IsSpeedParam(paramId)) return SpeedFormats;
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

        /// <summary>
        /// Effective format for a param after the full precedence chain:
        /// explicit Format &gt; source-override default-bare &gt; Show*Total toggle
        /// migration &gt; family default. Null when the param has no format family.
        /// Shared by the ITM mapper (wire) and the Pages editor (dropdown selection)
        /// so the two cannot drift.
        /// </summary>
        /// <param name="paramId">ITM parameter id.</param>
        /// <param name="explicitFormat">Mapping's Format when set; null/empty otherwise.</param>
        /// <param name="hasSourceOverride">True when a source or format override is present
        /// — empty format then defaults bare for
        /// total/temp params.</param>
        /// <param name="showLapTotal">Settings toggle; false with no explicit format
        /// acts as <see cref="Bare"/> for Lap.</param>
        /// <param name="showPositionTotal">Same for Position.</param>
        public static string EffectiveFormat(
            ushort paramId,
            string explicitFormat,
            bool hasSourceOverride,
            bool showLapTotal,
            bool showPositionTotal)
        {
            if (!string.IsNullOrEmpty(explicitFormat))
                return explicitFormat;

            // A Source override keeps total/unit suffixes only when the format
            // explicitly asks for them — otherwise default to bare (suffixes come
            // from GameData, not the override source).
            if (hasSourceOverride)
            {
                if (IsTotalParam(paramId) || IsTempParam(paramId))
                    return Bare;
                if (IsGearParam(paramId))
                    return Neutral;
                if (IsSpeedParam(paramId))
                    return Whole;
                return null;
            }

            // Toggle migration: settings toggle=false with no explicit format → bare.
            if (paramId == ItmParam.Lap)
                return showLapTotal ? WithTotal : Bare;
            if (paramId == ItmParam.Position)
                return showPositionTotal ? WithTotal : Bare;
            if (paramId == ItmParam.Fuel)
                return WithTotal;
            if (IsTempParam(paramId))
                return Unit;
            if (IsGearParam(paramId))
                return Neutral;
            if (IsSpeedParam(paramId))
                return Whole;
            return null;
        }

        private static readonly IReadOnlyList<string> TotalFormats =
            Array.AsReadOnly(new[] { WithTotal, Bare });

        private static readonly IReadOnlyList<string> TempFormats =
            Array.AsReadOnly(new[] { Unit, Bare });

        /// <summary>Gear blank-vs-neutral family (design round 8c).</summary>
        private static readonly IReadOnlyList<string> GearFormats =
            Array.AsReadOnly(new[] { Neutral, Blank });

        /// <summary>Speed format family (design round 8c equivalents).</summary>
        private static readonly IReadOnlyList<string> SpeedFormats =
            Array.AsReadOnly(new[] { Whole, OneDecimal });
    }
}
