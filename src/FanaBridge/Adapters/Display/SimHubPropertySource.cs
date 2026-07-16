using System;
using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Customization;
using GameReaderCommon;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// The rule engine's window onto live SimHub data — the production
    /// <see cref="IPropertyReader"/>. Frame-scoped: the device instance calls
    /// <see cref="BeginFrame"/> once per DataUpdate before the engines tick, and every
    /// read until the next BeginFrame refers to that frame.
    ///
    /// Two namespaces, two paths:
    /// <list type="bullet">
    /// <item><see cref="PropertyKind.BuiltIn"/> — the closed <see cref="BuiltInProperties"/>
    /// set, read from typed <c>GameData.NewData</c> fields (no name lookup, no boxing
    /// beyond the frame SimHub already built). These mirror what the ITM field mapper
    /// reads, so a rule on "Fuel" watches exactly the value the display shows.</item>
    /// <item><see cref="PropertyKind.SimHubProperty"/> — user-picked names resolved via
    /// <c>PluginManager.GetPropertyValue</c>, memoized per frame so N rules on one
    /// property cost one lookup. The raw lookup sits behind an injectable seam because
    /// <c>GetPropertyValue</c> is non-virtual host code (tests fake the seam).</item>
    /// </list>
    ///
    /// Failure is always "the read returns false" (the rule stays armed — see
    /// <see cref="IPropertyReader"/>): no exception may escape into the frame loop, so
    /// host lookups are wrapped and warned once per property name.
    /// </summary>
    public sealed class SimHubPropertySource : IPropertyReader
    {
        private readonly Action<string> _log;
        // Raw named-property lookup. Production resolves through the frame's
        // PluginManager; tests inject a dictionary-backed Func (GetPropertyValue is
        // non-virtual host code and cannot be faked directly).
        private readonly Func<string, object> _rawLookup;

        // ── Frame scope (set by BeginFrame, valid until the next one) ─────
        private PluginManager _pm;
        private StatusDataBase _status;
        // Per-frame memo for named lookups; null values are memoized too (a missing
        // property stays missing for the whole frame).
        private readonly Dictionary<string, object> _memo =
            new Dictionary<string, object>(StringComparer.Ordinal);

        // Names whose host lookup threw — warned once per source lifetime.
        private HashSet<string> _warnedNames;

        public SimHubPropertySource(Action<string> log = null, Func<string, object> rawLookup = null)
        {
            _log = log ?? (_ => { });
            _rawLookup = rawLookup;
        }

        /// <summary>
        /// Scopes subsequent reads to this frame. Call once per DataUpdate, before the
        /// engines tick. A null <paramref name="pm"/> fails every named-property read; a
        /// null frame (or null <c>NewData</c>) fails every built-in read.
        /// </summary>
        public void BeginFrame(PluginManager pm, GameData data)
        {
            _pm = pm;
            _status = data?.NewData;
            _memo.Clear();
        }

        // ── IPropertyReader ──────────────────────────────────────────────

        public bool TryGetNumber(PropertySpec spec, out double value)
        {
            value = 0;
            if (spec == null || string.IsNullOrEmpty(spec.Name))
                return false;

            switch (spec.Kind)
            {
                case PropertyKind.BuiltIn:
                    return TryGetBuiltIn(spec.Name, out value);
                case PropertyKind.SimHubProperty:
                    return TryCoerceNumber(ReadNamed(spec.Name), out value);
                default:
                    return false;   // actions are events, never readable values
            }
        }

        public bool TryGetBool(PropertySpec spec, out bool value)
        {
            value = false;
            if (spec == null || string.IsNullOrEmpty(spec.Name))
                return false;

            switch (spec.Kind)
            {
                case PropertyKind.BuiltIn:
                    // Built-ins are all numeric-backed; 0/1 telemetry convention. NaN is
                    // the "no data" sentinel (gap/delta fields with no reference car) — a
                    // gap is not a value, and NaN != 0 would read "no data" as TRUE, so
                    // the read fails instead (the rule stays armed).
                    if (!TryGetBuiltIn(spec.Name, out double n) || double.IsNaN(n))
                        return false;
                    value = n != 0;
                    return true;
                case PropertyKind.SimHubProperty:
                    return TryCoerceBool(ReadNamed(spec.Name), out value);
                default:
                    return false;
            }
        }

        // ── Built-ins (typed GameData fast path) ─────────────────────────

        // One resolver per BuiltInProperties constant, in lockstep with that list (a
        // guard test iterates BuiltInProperties.All against this table). Returning
        // null means "unavailable this frame" (e.g. no delta reference) — the read
        // fails, the rule stays armed.
        private static readonly Dictionary<string, Func<StatusDataBase, double?>> BuiltIns =
            new Dictionary<string, Func<StatusDataBase, double?>>(StringComparer.OrdinalIgnoreCase)
            {
                // SpeedLocal honours the user's km/h vs mph choice — same field the
                // ITM speed encoder reads.
                [BuiltInProperties.Speed] = d => d.SpeedLocal,
                [BuiltInProperties.Gear] = d => GearNumber(d.Gear),
                [BuiltInProperties.CurrentLap] = d => d.CurrentLap,
                [BuiltInProperties.TotalLaps] = d => d.TotalLaps,
                [BuiltInProperties.Position] = d => d.Position,
                [BuiltInProperties.OpponentsCount] = d => d.OpponentsCount,
                [BuiltInProperties.CurrentLapTime] = d => d.CurrentLapTime.TotalSeconds,
                [BuiltInProperties.LastLapTime] = d => d.LastLapTime.TotalSeconds,
                [BuiltInProperties.BestLapTime] = d => d.BestLapTime.TotalSeconds,
                [BuiltInProperties.Fuel] = d => d.Fuel,
                [BuiltInProperties.MaxFuel] = d => d.MaxFuel,
                [BuiltInProperties.FuelPercent] = d => d.FuelPercent,
                [BuiltInProperties.ErsPercent] = d => d.ERSPercent,
                [BuiltInProperties.DrsAvailable] = d => d.DRSAvailable,
                [BuiltInProperties.DrsEnabled] = d => d.DRSEnabled,
                // Nullable in SimHub (no session best yet) — null fails the read.
                [BuiltInProperties.DeltaToSessionBest] = d => d.DeltaToSessionBest,
                [BuiltInProperties.TcLevel] = d => d.TCLevel,
                [BuiltInProperties.AbsLevel] = d => d.ABSLevel,
                [BuiltInProperties.EngineMap] = d => d.EngineMap,
                [BuiltInProperties.OilTemperature] = d => d.OilTemperature,
                [BuiltInProperties.BrakeBias] = d => d.BrakeBias,
                // Same nearest-|gap| the ITM CAR AHEAD/BEHIND fields show (unsigned;
                // the display's sign convention is presentation, not data).
                [BuiltInProperties.GapAhead] = d => ItmTelemetryMapper.NearestGap(d.OpponentsAheadOnTrack),
                [BuiltInProperties.GapBehind] = d => ItmTelemetryMapper.NearestGap(d.OpponentsBehindOnTrack),
                [BuiltInProperties.IsInPitLane] = d => d.IsInPitLane,
                [BuiltInProperties.PitLimiterOn] = d => d.PitLimiterOn,
                [BuiltInProperties.TyreTempFrontLeft] = d => d.TyreTemperatureFrontLeft,
                [BuiltInProperties.TyreTempFrontRight] = d => d.TyreTemperatureFrontRight,
                [BuiltInProperties.TyreTempRearLeft] = d => d.TyreTemperatureRearLeft,
                [BuiltInProperties.TyreTempRearRight] = d => d.TyreTemperatureRearRight,
            };

        private bool TryGetBuiltIn(string name, out double value)
        {
            value = 0;
            var status = _status;
            if (status == null || !BuiltIns.TryGetValue(name, out var read))
                return false;
            double? v = read(status);
            if (v == null)
                return false;
            value = v.Value;
            return true;
        }

        // SimHub's gear is a string ("N", "R", "1".."9"); rules need a number.
        // N = 0, R = -1 (distinct from neutral so "Gear = -1" can watch reverse),
        // forward gears literal. Anything unparsable fails the read.
        private static double? GearNumber(string gear)
        {
            if (string.IsNullOrEmpty(gear))
                return null;
            gear = gear.Trim().ToUpperInvariant();
            if (gear == "N" || gear == "NEUTRAL") return 0;
            if (gear == "R" || gear == "REVERSE") return -1;
            return int.TryParse(gear, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g)
                ? g : (double?)null;
        }

        // ── Named properties (memoized PluginManager lookup) ─────────────

        private object ReadNamed(string name)
        {
            if (_memo.TryGetValue(name, out object memoized))
                return memoized;

            object value = null;
            try
            {
                if (_rawLookup != null)
                    value = _rawLookup(name);
                else if (_pm != null)
                    value = _pm.GetPropertyValue(name);
            }
            catch (Exception ex)
            {
                // GetPropertyValue is host code — contain it. The failed (null) result
                // is memoized like any other so the frame doesn't retry per rule.
                if (_warnedNames == null)
                    _warnedNames = new HashSet<string>(StringComparer.Ordinal);
                if (_warnedNames.Add(name))
                    _log("DisplayRules: reading property '" + name + "' failed — "
                        + ex.Message);
            }
            _memo[name] = value;
            return value;
        }

        // Numeric types → double; bool → 1/0; string → invariant parse; anything
        // else fails the read.
        private static bool TryCoerceNumber(object raw, out double value)
        {
            value = 0;
            switch (raw)
            {
                case null: return false;
                case double d: value = d; return true;
                case float f: value = f; return true;
                case int i: value = i; return true;
                case long l: value = l; return true;
                case short s: value = s; return true;
                case byte b: value = b; return true;
                case sbyte sb: value = sb; return true;
                case ushort us: value = us; return true;
                case uint ui: value = ui; return true;
                case ulong ul: value = ul; return true;
                case decimal m: value = (double)m; return true;
                case bool flag: value = flag ? 1.0 : 0.0; return true;
                case string text:
                    return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out value);
                default: return false;
            }
        }

        // bool direct; numerics != 0; strings "true"/"false" then numeric parse.
        private static bool TryCoerceBool(object raw, out bool value)
        {
            value = false;
            if (raw is bool flag)
            {
                value = flag;
                return true;
            }
            if (raw is string text)
            {
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }
                // fall through to the numeric parse ("1", "0", "2.5"…)
            }
            if (TryCoerceNumber(raw, out double n))
            {
                // NaN is the "no data" convention on gap/delta properties — not a truth
                // value (NaN != 0 is the one comparison NaN satisfies). Fail the read.
                if (double.IsNaN(n))
                    return false;
                value = n != 0;
                return true;
            }
            return false;
        }
    }
}
