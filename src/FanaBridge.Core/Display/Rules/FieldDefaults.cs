using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// The built-in default source each ITM field reads when no Base is authored —
    /// paramId → <see cref="BuiltInProperties"/> name. Mirrors the mapper's built-in
    /// encoder registry (same key set, same semantics); a guard test pins the two.
    /// The UI uses this so an unmodified field can state what it actually reads
    /// instead of drawing a dash.
    /// </summary>
    public static class FieldDefaults
    {
        private static readonly Dictionary<ushort, string> BuiltInByParam =
            new Dictionary<ushort, string>
            {
                [ItmParam.Speed] = BuiltInProperties.Speed,
                [ItmParam.Gear] = BuiltInProperties.Gear,
                [ItmParam.Lap] = BuiltInProperties.CurrentLap,
                [ItmParam.Position] = BuiltInProperties.Position,
                [ItmParam.LapTime] = BuiltInProperties.CurrentLapTime,
                [ItmParam.LastLapTime] = BuiltInProperties.LastLapTime,
                [ItmParam.BestLapTime] = BuiltInProperties.BestLapTime,
                [ItmParam.Fuel] = BuiltInProperties.Fuel,
                [ItmParam.ErsLevel] = BuiltInProperties.ErsPercent,
                [ItmParam.DrsZone] = BuiltInProperties.DrsAvailable,
                [ItmParam.DrsActive] = BuiltInProperties.DrsEnabled,
                [ItmParam.DeltaOwnBest] = BuiltInProperties.DeltaToSessionBest,
                [ItmParam.TcSetting] = BuiltInProperties.TcLevel,
                [ItmParam.AbsSetting] = BuiltInProperties.AbsLevel,
                [ItmParam.EngineMapping] = BuiltInProperties.EngineMap,
                [ItmParam.OilTemp] = BuiltInProperties.OilTemperature,
                [ItmParam.BrakeBias] = BuiltInProperties.BrakeBias,
                [ItmParam.CarAhead] = BuiltInProperties.GapAhead,
                [ItmParam.CarBehind] = BuiltInProperties.GapBehind,
                [ItmParam.TyreFlTemp] = BuiltInProperties.TyreTempFrontLeft,
                [ItmParam.TyreFrTemp] = BuiltInProperties.TyreTempFrontRight,
                [ItmParam.TyreRlTemp] = BuiltInProperties.TyreTempRearLeft,
                [ItmParam.TyreRrTemp] = BuiltInProperties.TyreTempRearRight,
            };

        /// <summary>All mapped param ids (guard-test seam).</summary>
        public static IEnumerable<ushort> MappedParams => BuiltInByParam.Keys;

        /// <summary>
        /// The <see cref="BuiltInProperties"/> name a field reads by default, or false
        /// when the param has no built-in encoder (it renders as dashes).
        /// </summary>
        public static bool TryGetBuiltInDefault(ushort paramId, out string builtInName)
            => BuiltInByParam.TryGetValue(paramId, out builtInName);
    }
}
