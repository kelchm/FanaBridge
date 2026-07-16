using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FanaBridge.Display
{
    /// <summary>
    /// Which namespace a <see cref="PropertySpec"/> name lives in — Core never resolves
    /// the name itself; the plugin adapter interprets it against its data sources.
    /// </summary>
    public enum PropertyKind
    {
        /// <summary>Lenient-load fallback for a kind this build does not recognize.
        /// A rule or field mapping with an unknown source kind is disabled/dropped at load.</summary>
        Unknown = 0,
        /// <summary>A name from the Core-owned closed set (<see cref="BuiltInProperties"/>) —
        /// the typed telemetry fields the built-in field mapper reads.</summary>
        BuiltIn,
        /// <summary>A SimHub property name, resolved by name each frame (user-picked).</summary>
        SimHubProperty,
        /// <summary>A FanaBridge action name — the mapped-control trigger path.</summary>
        FanaBridgeAction,
    }

    /// <summary>
    /// Names a value a rule condition or field mapping reads. This is the seam that keeps
    /// the config model SimHub-free: Core only carries the (kind, name) pair; the adapter
    /// above resolves it — typed telemetry fast-path for built-ins, name lookup for
    /// SimHub properties, action matching for triggers.
    /// </summary>
    public class PropertySpec
    {
        private string _kindRaw;
        private PropertyKind? _kind;

        /// <summary>Serialized form of <see cref="Kind"/>, preserved verbatim (see
        /// <see cref="RuleCondition.KindRaw"/>).</summary>
        [JsonProperty("kind")]
        public string KindRaw
        {
            get => _kindRaw;
            set { _kindRaw = value; _kind = null; }
        }

        /// <summary>How <see cref="Name"/> is resolved — <see cref="PropertyKind.Unknown"/>
        /// when missing or unrecognized (the rule is degraded / the mapping dropped).</summary>
        [JsonIgnore]
        public PropertyKind Kind
        {
            get => _kind ?? (_kind = EnumText.Parse(_kindRaw, PropertyKind.Unknown)).Value;
            set { _kind = value; _kindRaw = EnumText.Write(value); }
        }

        /// <summary>The name within the kind's namespace: a <see cref="BuiltInProperties"/>
        /// constant, a SimHub property name, or a FanaBridge action name.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>
    /// The closed set of built-in property names — the typed telemetry fields the built-in
    /// field mapper already reads each frame. Closed on purpose: a <see cref="PropertyKind.BuiltIn"/>
    /// spec whose name is not in this set is a config error (the rule is disabled / the
    /// mapping dropped at load), so the set only grows deliberately, in step with the
    /// adapter that interprets it. Names match case-insensitively at load.
    /// </summary>
    public static class BuiltInProperties
    {
        public const string Speed = "Speed";
        public const string Gear = "Gear";
        public const string CurrentLap = "CurrentLap";
        public const string TotalLaps = "TotalLaps";
        public const string Position = "Position";
        public const string OpponentsCount = "OpponentsCount";
        public const string CurrentLapTime = "CurrentLapTime";
        public const string LastLapTime = "LastLapTime";
        public const string BestLapTime = "BestLapTime";
        public const string Fuel = "Fuel";
        public const string MaxFuel = "MaxFuel";
        public const string FuelPercent = "FuelPercent";
        public const string ErsPercent = "ErsPercent";
        public const string DrsAvailable = "DrsAvailable";
        public const string DrsEnabled = "DrsEnabled";
        public const string DeltaToSessionBest = "DeltaToSessionBest";
        public const string TcLevel = "TcLevel";
        public const string AbsLevel = "AbsLevel";
        public const string EngineMap = "EngineMap";
        public const string OilTemperature = "OilTemperature";
        public const string BrakeBias = "BrakeBias";
        public const string GapAhead = "GapAhead";
        public const string GapBehind = "GapBehind";
        public const string IsInPitLane = "IsInPitLane";
        public const string PitLimiterOn = "PitLimiterOn";
        public const string TyreTempFrontLeft = "TyreTempFrontLeft";
        public const string TyreTempFrontRight = "TyreTempFrontRight";
        public const string TyreTempRearLeft = "TyreTempRearLeft";
        public const string TyreTempRearRight = "TyreTempRearRight";

        /// <summary>Every built-in name, in a stable order — what a UI picker lists.</summary>
        public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[]
        {
            Speed, Gear, CurrentLap, TotalLaps, Position, OpponentsCount,
            CurrentLapTime, LastLapTime, BestLapTime,
            Fuel, MaxFuel, FuelPercent, ErsPercent, DrsAvailable, DrsEnabled, DeltaToSessionBest,
            TcLevel, AbsLevel, EngineMap, OilTemperature, BrakeBias,
            GapAhead, GapBehind, IsInPitLane, PitLimiterOn,
            TyreTempFrontLeft, TyreTempFrontRight, TyreTempRearLeft, TyreTempRearRight,
        });

        private static readonly HashSet<string> Known =
            new HashSet<string>(All, StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="name"/> is in the closed set (case-insensitive).</summary>
        public static bool IsKnown(string name)
            => !string.IsNullOrEmpty(name) && Known.Contains(name);
    }
}
