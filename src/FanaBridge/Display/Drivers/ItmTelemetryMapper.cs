using System;
using System.Collections.Generic;
using GameReaderCommon;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Drivers
{
    /// <summary>
    /// Maps SimHub <see cref="GameData"/> telemetry to encoded <see cref="ItmValue"/> entries
    /// and display suffixes for the ITM display. This is the <b>device-agnostic</b> half of ITM:
    /// a parameter encodes from the telemetry frame the same way regardless of which display
    /// shows it, so a single flat <c>paramId → encoder</c> registry serves every device.
    ///
    /// Constructed once per display device by <see cref="ItmDisplayDriver"/>; the built-in
    /// encoder registry is the instance's default layer. Optional per-device
    /// <see cref="FieldMapping"/> overrides (source + format) sit on top: a resolved scalar
    /// still flows through the same typed encoder path (clamps, rounding, wire type). The
    /// wire-side vocabulary — the page catalog and subscription-report parsing — lives in
    /// <see cref="ItmTelemetry"/> (Protocol, no SimHub).
    /// </summary>
    public class ItmTelemetryMapper
    {
        // ── Typed encoder builders ───────────────────────────────────────
        // Each returns an encoder: read + encode one parameter from a frame at a handle.
        private static Func<StatusDataBase, byte, ItmValue> U8(ushort id, Func<StatusDataBase, byte> sel)
            => (d, h) => ItmValue.UInt8(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> I16(ushort id, Func<StatusDataBase, short> sel)
            => (d, h) => ItmValue.Int16(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> I32(ushort id, Func<StatusDataBase, int> sel)
            => (d, h) => ItmValue.Int32(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> F32(ushort id, Func<StatusDataBase, float> sel)
            => (d, h) => ItmValue.Float32(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> Str(ushort id, Func<StatusDataBase, string> sel)
            => (d, h) => ItmValue.Ascii(h, id, sel(d));

        // Flat paramId -> encoder registry (default layer). Device-agnostic: any subscribed
        // parameter, on any display, encodes from the same frame the same way. The Protocol
        // page catalog (ItmTelemetry.ParamsFor) says which params a page carries; this says
        // how to encode one. Built once per instance. A guard test asserts every catalog
        // param has an entry here (see HasEncoder).
        private readonly Dictionary<ushort, Func<StatusDataBase, byte, ItmValue>> _registry;

        // Natural pre-wire scalars (property-picker units) for itmField condition reads.
        // Parallel to the registry: same SimHub read + host-side rounding, WITHOUT wire
        // transforms (no brake-bias tenths, no CarAhead sign flip). ASCII params are absent
        // (skipped by param identity — no single numeric meaning).
        private readonly Dictionary<ushort, Func<StatusDataBase, double>> _naturalScalars;

        // Scalar encoder path: same clamps/rounding/wire type as the built-in registry,
        // fed a double from a FieldMapping override (or from a resolved built-in). Gear and
        // EngineMapping have no entry — they are override-excluded and keep special forms.
        private readonly Dictionary<ushort, Func<double, byte, ItmValue>> _scalarEncoders;

        // Per-device field overrides (validated snapshot). Empty when none; never null after
        // Configure / ConfigureFromPlans. Read on the DataUpdate thread only.
        private IReadOnlyDictionary<ushort, FieldMapping> _fieldMappings =
            EmptyMappings;

        // v2 plan layer (pre-E8 seam). Empty when the v1 Configure path is active.
        // SuffixOwner / AlignedSuffixText / EffectVisible live here; value source+format
        // are also projected into _fieldMappings so the encode path stays single.
        private IReadOnlyDictionary<ushort, FieldRegionPlan> _fieldPlans =
            EmptyPlans;

        // Shared property reader (the runtime's SimHubPropertySource). Null when no
        // customization is active — overrides then never fire. BeginFrame is the runtime's
        // job and must run before the driver's Update that reads here.
        private IPropertyReader _properties;

        private static readonly IReadOnlyDictionary<ushort, FieldMapping> EmptyMappings =
            new Dictionary<ushort, FieldMapping>();

        private static readonly IReadOnlyDictionary<ushort, FieldRegionPlan> EmptyPlans =
            new Dictionary<ushort, FieldRegionPlan>();

        /// <summary>Provisional field-value format: round resolved scalars to 1 decimal
        /// before the typed encoder (mirrors segment-plane <c>oneDecimal</c>).</summary>
        public const string FormatOneDecimal = "oneDecimal";

        /// <summary>
        /// Optional sink for the latest computed scalar per param (E7 itmField read path).
        /// Null by default — nothing live sets this yet; encode path is unchanged when
        /// null (one null-check). E8 wires an <see cref="ItmFieldValueBuffer"/> /
        /// <see cref="IItmFieldValueSink"/> so condition evaluation can read the stream.
        /// </summary>
        public IItmFieldValueSink ParamValueSink { get; set; }

        /// <summary>Builds a mapper with the built-in default encoder registry.</summary>
        public ItmTelemetryMapper()
        {
            _registry = BuildRegistry();
            _naturalScalars = BuildNaturalScalars();
            _scalarEncoders = BuildScalarEncoders();
        }

        /// <summary>
        /// Installs the device's validated field mappings and the shared property reader.
        /// Call from the runtime on every frame that may encode (or on config swap); a null
        /// mappings dict or null reader clears the override layer. Gear/EngineMapping are
        /// already stripped by the validator. Clears any v2 plan layer.
        /// </summary>
        public void Configure(
            IReadOnlyDictionary<ushort, FieldMapping> fieldMappings,
            IPropertyReader properties)
        {
            _fieldMappings = fieldMappings ?? EmptyMappings;
            _fieldPlans = EmptyPlans;
            _properties = properties;
        }

        /// <summary>
        /// v2 plan → mapper application seam (pre-E8). Installs per-field value source +
        /// format, <see cref="SuffixOwner"/> tri-state (with bare-format coercion when the
        /// plan paints the suffix), <see cref="FieldRegionPlan.AlignedSuffixText"/>, and
        /// effect visibility already resolved into that text by the composer. Does not
        /// touch double-tap cadences — those stay on the driver. Null/empty plans clear
        /// the plan layer (same as empty mappings).
        /// </summary>
        public void ConfigureFromPlans(
            IReadOnlyList<FieldRegionPlan> plans,
            IPropertyReader properties)
        {
            _properties = properties;
            if (plans == null || plans.Count == 0)
            {
                _fieldMappings = EmptyMappings;
                _fieldPlans = EmptyPlans;
                return;
            }

            var planMap = new Dictionary<ushort, FieldRegionPlan>();
            var mappings = new Dictionary<ushort, FieldMapping>();
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                if (plan == null)
                    continue;
                planMap[plan.ParamId] = plan;

                var mapping = MappingFromPlan(plan);
                if (mapping != null)
                    mappings[plan.ParamId] = mapping;
            }
            _fieldPlans = planMap;
            _fieldMappings = mappings.Count > 0 ? mappings : EmptyMappings;
        }

        /// <summary>
        /// Projects a field plan into a v1-shaped <see cref="FieldMapping"/> for the shared
        /// encode + format path. Suffix-owner Override/Blank coerces format to bare so the
        /// single mapper suffix path cannot re-fill the region (FrameComposerTypes rule).
        /// </summary>
        private static FieldMapping MappingFromPlan(FieldRegionPlan plan)
        {
            string format = plan.ValueFormat;
            // Bare-format coercion: plan paints the suffix → mapper must not emit
            // withTotal/unit into the same region.
            if (plan.SuffixOwner == SuffixOwner.Override
                || plan.SuffixOwner == SuffixOwner.Blank)
                format = FieldFormats.Bare;

            PropertySpec source = null;
            if (plan.ValueFromOverride || plan.ValueSource != null)
                source = ToPropertySpec(plan.ValueSource);

            // Text/message value content has no property source — encode path stays built-in
            // unless a source is present. Format alone still installs (suffix layer).
            if (source == null && string.IsNullOrEmpty(format))
                return null;

            return new FieldMapping { Source = source, Format = format };
        }

        private static PropertySpec ToPropertySpec(ValueSource source)
        {
            if (source == null)
                return null;
            var spec = new PropertySpec { Name = source.Name };
            if (!string.IsNullOrWhiteSpace(source.KindRaw))
                spec.KindRaw = source.KindRaw;
            else if (source.Kind != ValueSourceKind.Unknown)
                spec.KindRaw = EnumText.Write(source.Kind);
            return spec;
        }

        /// <summary>
        /// Settings-level total toggles (ItmShowLapTotal / ItmShowPositionTotal). Honored
        /// for one release as a resolve-on-read migration into the format layer: toggle
        /// false with no explicit format acts as <see cref="FieldFormats.Bare"/>. The
        /// mapper is the single owner of suffix decisions.
        /// </summary>
        public bool ShowLapTotal { get; set; } = true;

        /// <inheritdoc cref="ShowLapTotal"/>
        public bool ShowPositionTotal { get; set; } = true;

        private static Dictionary<ushort, Func<StatusDataBase, byte, ItmValue>> BuildRegistry()
            => new Dictionary<ushort, Func<StatusDataBase, byte, ItmValue>>
            {
                // SPEED + GEAR head every page. SpeedLocal honours the user's km/h vs mph choice,
                // matching the 7-seg driver.
                [ItmParam.Speed] = I16(ItmParam.Speed, d => ClampSpeed(d.SpeedLocal)),
                [ItmParam.Gear] = U8(ItmParam.Gear, d => EncodeGear(d.Gear)),

                // Lap Info
                [ItmParam.Lap] = U8(ItmParam.Lap, d => ClampByte(d.CurrentLap)),
                [ItmParam.Position] = U8(ItmParam.Position, d => ClampByte(d.Position)),
                [ItmParam.LapTime] = F32(ItmParam.LapTime, d => Seconds(d.CurrentLapTime)),
                [ItmParam.LastLapTime] = F32(ItmParam.LastLapTime, d => Seconds(d.LastLapTime)),

                // Fuel / ERS / DRS
                // Round each displayed float to its field's precision on the host: the firmware
                // renders a decimal field as whole.round(frac*10^N) with NO carry, so an
                // unrounded value just below a boundary misrenders (16.9692 -> "16.10"). The
                // official app pre-rounds the same way. Fuel = 1 dp; delta/gaps = 2 dp; time
                // fields truncate in firmware and are left unrounded.
                [ItmParam.Fuel] = F32(ItmParam.Fuel, d => (float)Math.Round(d.Fuel, 1)),
                [ItmParam.ErsLevel] = I32(ItmParam.ErsLevel, d => SafeRound(d.ERSPercent)),
                [ItmParam.DrsZone] = U8(ItmParam.DrsZone, d => (byte)(d.DRSAvailable != 0 ? 1 : 0)),
                [ItmParam.DrsActive] = U8(ItmParam.DrsActive, d => (byte)(d.DRSEnabled != 0 ? 1 : 0)),
                [ItmParam.DeltaOwnBest] = F32(ItmParam.DeltaOwnBest, d => (float)Math.Round(d.DeltaToSessionBest ?? 0.0, 2)),

                // Car Settings
                [ItmParam.TcSetting] = U8(ItmParam.TcSetting, d => ClampByte(d.TCLevel)),
                [ItmParam.AbsSetting] = U8(ItmParam.AbsSetting, d => ClampByte(d.ABSLevel)),
                // ENGINE_MAPPING is ASCII text on the wire — map 10 travels as "10", not 0x0A.
                // Sending the numeric byte wedges the firmware (verified via official capture).
                [ItmParam.EngineMapping] = Str(ItmParam.EngineMapping, d => EngineMapText(d.EngineMap)),
                [ItmParam.OilTemp] = U8(ItmParam.OilTemp, d => ClampByte(d.OilTemperature)),
                // BRAKE_BIAS is Int32 tenths of a percent (512 = 51.2%); see BrakeBiasTenths.
                [ItmParam.BrakeBias] = I32(ItmParam.BrakeBias, d => BrakeBiasTenths(d.BrakeBias)),

                // Lap Times
                [ItmParam.BestLapTime] = F32(ItmParam.BestLapTime, d => Seconds(d.BestLapTime)),
                // Car ahead is shown as a negative gap (you're behind them); car behind positive.
                [ItmParam.CarAhead] = F32(ItmParam.CarAhead, d => -(float)Math.Round(NearestGap(d.OpponentsAheadOnTrack), 2)),
                [ItmParam.CarBehind] = F32(ItmParam.CarBehind, d => (float)Math.Round(NearestGap(d.OpponentsBehindOnTrack), 2)),

                // Tyre Temps
                [ItmParam.TyreFlTemp] = U8(ItmParam.TyreFlTemp, d => ClampByte(d.TyreTemperatureFrontLeft)),
                [ItmParam.TyreRlTemp] = U8(ItmParam.TyreRlTemp, d => ClampByte(d.TyreTemperatureRearLeft)),
                [ItmParam.TyreFrTemp] = U8(ItmParam.TyreFrTemp, d => ClampByte(d.TyreTemperatureFrontRight)),
                [ItmParam.TyreRrTemp] = U8(ItmParam.TyreRrTemp, d => ClampByte(d.TyreTemperatureRearRight)),
            };

        // Natural pre-wire scalars — property-picker / itmField units. No Gear / EngineMapping
        // (ASCII params skipped by identity). BrakeBias stays percent; CarAhead is +gap.
        private static Dictionary<ushort, Func<StatusDataBase, double>> BuildNaturalScalars()
            => new Dictionary<ushort, Func<StatusDataBase, double>>
            {
                [ItmParam.Speed] = d => ClampSpeed(d.SpeedLocal),
                [ItmParam.Lap] = d => ClampByte(d.CurrentLap),
                [ItmParam.Position] = d => ClampByte(d.Position),
                [ItmParam.LapTime] = d => Seconds(d.CurrentLapTime),
                [ItmParam.LastLapTime] = d => Seconds(d.LastLapTime),
                [ItmParam.Fuel] = d => Math.Round(d.Fuel, 1),
                [ItmParam.ErsLevel] = d => SafeRound(d.ERSPercent),
                [ItmParam.DrsZone] = d => d.DRSAvailable != 0 ? 1 : 0,
                [ItmParam.DrsActive] = d => d.DRSEnabled != 0 ? 1 : 0,
                [ItmParam.DeltaOwnBest] = d => Math.Round(d.DeltaToSessionBest ?? 0.0, 2),
                [ItmParam.TcSetting] = d => ClampByte(d.TCLevel),
                [ItmParam.AbsSetting] = d => ClampByte(d.ABSLevel),
                [ItmParam.OilTemp] = d => ClampByte(d.OilTemperature),
                [ItmParam.BrakeBias] = d => d.BrakeBias,
                [ItmParam.BestLapTime] = d => Seconds(d.BestLapTime),
                [ItmParam.CarAhead] = d => Math.Round(NearestGap(d.OpponentsAheadOnTrack), 2),
                [ItmParam.CarBehind] = d => Math.Round(NearestGap(d.OpponentsBehindOnTrack), 2),
                [ItmParam.TyreFlTemp] = d => ClampByte(d.TyreTemperatureFrontLeft),
                [ItmParam.TyreRlTemp] = d => ClampByte(d.TyreTemperatureRearLeft),
                [ItmParam.TyreFrTemp] = d => ClampByte(d.TyreTemperatureFrontRight),
                [ItmParam.TyreRrTemp] = d => ClampByte(d.TyreTemperatureRearRight),
            };

        // Scalar path: same wire transforms as the registry selectors above, without the
        // GameData read. Override values feed through these so fuel still rounds 1dp into
        // Float32, brake bias still becomes tenths, etc.
        private static Dictionary<ushort, Func<double, byte, ItmValue>> BuildScalarEncoders()
            => new Dictionary<ushort, Func<double, byte, ItmValue>>
            {
                [ItmParam.Speed] = (n, h) => ItmValue.Int16(h, ItmParam.Speed, ClampSpeed(n)),
                [ItmParam.Lap] = (n, h) => ItmValue.UInt8(h, ItmParam.Lap, ClampByte(n)),
                [ItmParam.Position] = (n, h) => ItmValue.UInt8(h, ItmParam.Position, ClampByte(n)),
                [ItmParam.LapTime] = (n, h) => ItmValue.Float32(h, ItmParam.LapTime, (float)n),
                [ItmParam.LastLapTime] = (n, h) => ItmValue.Float32(h, ItmParam.LastLapTime, (float)n),
                [ItmParam.Fuel] = (n, h) => ItmValue.Float32(h, ItmParam.Fuel, (float)Math.Round(n, 1)),
                [ItmParam.ErsLevel] = (n, h) => ItmValue.Int32(h, ItmParam.ErsLevel, SafeRound(n)),
                [ItmParam.DrsZone] = (n, h) => ItmValue.UInt8(h, ItmParam.DrsZone, (byte)(n != 0 ? 1 : 0)),
                [ItmParam.DrsActive] = (n, h) => ItmValue.UInt8(h, ItmParam.DrsActive, (byte)(n != 0 ? 1 : 0)),
                [ItmParam.DeltaOwnBest] = (n, h) => ItmValue.Float32(h, ItmParam.DeltaOwnBest, (float)Math.Round(n, 2)),
                [ItmParam.TcSetting] = (n, h) => ItmValue.UInt8(h, ItmParam.TcSetting, ClampByte(n)),
                [ItmParam.AbsSetting] = (n, h) => ItmValue.UInt8(h, ItmParam.AbsSetting, ClampByte(n)),
                [ItmParam.OilTemp] = (n, h) => ItmValue.UInt8(h, ItmParam.OilTemp, ClampByte(n)),
                [ItmParam.BrakeBias] = (n, h) => ItmValue.Int32(h, ItmParam.BrakeBias, BrakeBiasTenths(n)),
                [ItmParam.BestLapTime] = (n, h) => ItmValue.Float32(h, ItmParam.BestLapTime, (float)n),
                // Car ahead reads negative on the wire (you're behind them). GapAhead /
                // override scalars resolve as unsigned +gap — negate for any path so the
                // wire convention holds for built-in and overrides alike (Round 2dp).
                [ItmParam.CarAhead] = (n, h) => ItmValue.Float32(h, ItmParam.CarAhead, -(float)Math.Round(n, 2)),
                [ItmParam.CarBehind] = (n, h) => ItmValue.Float32(h, ItmParam.CarBehind, (float)Math.Round(n, 2)),
                [ItmParam.TyreFlTemp] = (n, h) => ItmValue.UInt8(h, ItmParam.TyreFlTemp, ClampByte(n)),
                [ItmParam.TyreRlTemp] = (n, h) => ItmValue.UInt8(h, ItmParam.TyreRlTemp, ClampByte(n)),
                [ItmParam.TyreFrTemp] = (n, h) => ItmValue.UInt8(h, ItmParam.TyreFrTemp, ClampByte(n)),
                [ItmParam.TyreRrTemp] = (n, h) => ItmValue.UInt8(h, ItmParam.TyreRrTemp, ClampByte(n)),
            };

        /// <summary>
        /// Whether this parameter has a value encoder. Used by the catalog guard test to prove
        /// every param a page can carry (<see cref="ItmTelemetry.ParamsFor"/>) is encodable.
        /// </summary>
        public bool HasEncoder(ushort paramId) => _registry.ContainsKey(paramId);

        // ── Suffixes ─────────────────────────────────────────────────────

        // Temperatures whose value carries a unit label. SimHub delivers both the converted
        // value AND its unit in the same frame (StatusDataBase.TemperatureUnit), so we read the
        // label from that snapshot — no out-of-band settings lookup, always consistent with the
        // value. (Fuel is NOT unit-labeled; it uses a "/capacity" total, with the unit only as a
        // no-capacity fallback.)
        private static readonly HashSet<ushort> TempParams = new HashSet<ushort>
        {
            ItmParam.OilTemp, ItmParam.TyreFlTemp, ItmParam.TyreFrTemp,
            ItmParam.TyreRlTemp, ItmParam.TyreRrTemp,
        };

        /// <summary>
        /// The unit suffix a temperature's value should display (single char, e.g. "C"/"F"/"K"),
        /// or false if the parameter isn't a temperature. Read from the frame's
        /// <c>TemperatureUnit</c> so it stays consistent with the already-converted value.
        /// Honours the format layer: <see cref="FieldFormats.Bare"/> clears the unit.
        /// When a v2 plan owns the suffix (<see cref="SuffixOwner.Override"/> /
        /// <see cref="SuffixOwner.Blank"/>), returns the plan's
        /// <see cref="FieldRegionPlan.AlignedSuffixText"/> instead.
        /// </summary>
        public bool TryGetUnitSuffix(ushort paramId, GameData data, out string suffix)
        {
            if (TryGetPlanOwnedSuffix(paramId, out suffix))
                return true;

            if (!TempParams.Contains(paramId))
            {
                suffix = null;
                return false;
            }
            // bare → blank " " so a prior unit is actively cleared (same wire convention
            // as ShowTotalFor=false on totals).
            if (string.Equals(EffectiveFormat(paramId), FieldFormats.Bare, StringComparison.Ordinal))
            {
                suffix = " ";
                return true;
            }
            suffix = UnitLabel(data?.NewData?.TemperatureUnit, "C");
            return true;
        }

        /// <summary>
        /// Unified ParamDefs suffix resolve for the v2 plan layer (and a drop-in for E8):
        /// plan-owned Override/Blank → <see cref="FieldRegionPlan.AlignedSuffixText"/>;
        /// otherwise the existing unit / total owners. Returns false when no suffix applies.
        /// </summary>
        public bool TryResolveSuffix(ushort paramId, GameData data, out string suffix)
        {
            if (TryGetPlanOwnedSuffix(paramId, out suffix))
                return true;
            if (TryGetUnitSuffix(paramId, data, out suffix))
                return true;
            if (TryResolveTotalSuffix(paramId, data, out suffix))
                return true;
            return false;
        }

        /// <summary>
        /// When a plan paints the suffix region, return its resolved text (already
        /// alignment-clamped / blink-blanked by the composer). BaseComputed is not plan-
        /// owned — the mapper's unit/total path remains the single dynamic owner.
        /// </summary>
        private bool TryGetPlanOwnedSuffix(ushort paramId, out string suffix)
        {
            if (!_fieldPlans.TryGetValue(paramId, out var plan) || plan == null)
            {
                suffix = null;
                return false;
            }
            if (plan.SuffixOwner != SuffixOwner.Override
                && plan.SuffixOwner != SuffixOwner.Blank)
            {
                suffix = null;
                return false;
            }
            // EffectVisible false already folded into AlignedSuffixText as width-blank.
            suffix = plan.AlignedSuffixText
                ?? (plan.SuffixOwner == SuffixOwner.Blank ? " " : plan.SuffixText);
            if (suffix == null)
                suffix = " ";
            return true;
        }

        /// <summary>The fuel unit as a single-char label (e.g. "L"/"G"), from the frame's
        /// <c>FuelUnit</c>. Used only as a fallback when no tank capacity is available.</summary>
        public string FuelUnitLabel(GameData data) => UnitLabel(data?.NewData?.FuelUnit, "L");

        // A unit string's first letter, uppercased — a single char for the display's tight space,
        // robust to formats like "C" / "°C" / "Celsius" / "gal". Falls back when empty.
        private static string UnitLabel(string raw, string fallback)
        {
            if (!string.IsNullOrEmpty(raw))
                foreach (var c in raw)
                    if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
            return fallback;
        }

        /// <summary>Whether a parameter carries a "/total" suffix (lap, position, or fuel/capacity).</summary>
        public bool IsTotalParam(ushort paramId)
            => paramId == ItmParam.Lap || paramId == ItmParam.Position || paramId == ItmParam.Fuel;

        /// <summary>
        /// The "/total" suffix for a parameter that has one — lap of total laps ("/34"),
        /// position of field size ("/20"), fuel of tank capacity ("/23") — computed from the
        /// current telemetry. The firmware cannot know these, so the host supplies them.
        ///
        /// Returns false (no suffix) when the parameter has no total, there is no telemetry
        /// frame, or the game does not report a plausible total — i.e. the total must be present
        /// and at least the current value. This drops misleading "/0" / "/2" suffixes from games
        /// (e.g. Forza Horizon) that don't expose a race structure, while real races still show
        /// the total.
        ///
        /// Does <b>not</b> apply the format layer — callers that need format-aware emit use
        /// <see cref="TryResolveTotalSuffix"/>.
        /// </summary>
        public bool TryGetTotalSuffix(ushort paramId, GameData data, out string suffix)
        {
            suffix = null;
            var s = data?.NewData;
            if (s == null) return false;

            switch (paramId)
            {
                case ItmParam.Lap:
                    if (s.TotalLaps > 0 && s.TotalLaps >= s.CurrentLap)
                        suffix = "/" + s.TotalLaps;
                    return suffix != null;
                case ItmParam.Position:
                    int field = s.OpponentsCount;   // SimHub's opponents list already includes the player
                    if (field > 1 && field >= s.Position)
                        suffix = "/" + field;
                    return suffix != null;
                case ItmParam.Fuel:
                    // Fuel of tank capacity (e.g. "/90"), in the user's SimHub unit. Suppress
                    // when no capacity is reported so we never show a bare "/0".
                    if (s.MaxFuel > 0 && s.MaxFuel >= s.Fuel)
                        suffix = "/" + (int)Math.Round(s.MaxFuel);
                    return suffix != null;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Format-aware total/fuel suffix for ParamDefs: <see cref="FieldFormats.Bare"/>
        /// (or the migrated Show*Total=false / overridden-source default) clears with " ";
        /// <see cref="FieldFormats.WithTotal"/> emits the total when available, else the fuel
        /// unit-label fallback or a blank for lap/position. Single owner of suffix decisions.
        /// When a v2 plan owns the suffix, returns the plan text (same as
        /// <see cref="TryGetUnitSuffix"/>).
        /// </summary>
        public bool TryResolveTotalSuffix(ushort paramId, GameData data, out string suffix)
        {
            if (TryGetPlanOwnedSuffix(paramId, out suffix))
                return true;

            if (!IsTotalParam(paramId))
            {
                suffix = null;
                return false;
            }

            bool wantTotal = !string.Equals(
                EffectiveFormat(paramId), FieldFormats.Bare, StringComparison.Ordinal);

            if (wantTotal && TryGetTotalSuffix(paramId, data, out var total))
            {
                suffix = total;
                return true;
            }

            // bare, or withTotal with no plausible total: fuel falls back to the unit
            // label when withTotal still wants decoration; bare always blanks.
            if (wantTotal && paramId == ItmParam.Fuel)
            {
                suffix = FuelUnitLabel(data);
                return true;
            }

            suffix = " ";
            return true;
        }

        /// <summary>
        /// Effective format for <paramref name="paramId"/> after explicit Format, the
        /// overridden-source default-bare rule, and the Show*Total toggle migration.
        /// Null when the param has no format family. Delegates to the shared
        /// <see cref="FieldFormats.EffectiveFormat"/> helper.
        /// </summary>
        internal string EffectiveFormat(ushort paramId)
        {
            bool hasOverride = _fieldMappings.TryGetValue(paramId, out var mapping);
            return FieldFormats.EffectiveFormat(
                paramId,
                hasOverride ? mapping?.Format : null,
                hasOverride,
                ShowLapTotal,
                ShowPositionTotal);
        }

        // ── Value encoding ───────────────────────────────────────────────

        /// <summary>
        /// Encodes a single subscribed parameter's current value at <paramref name="handle"/>,
        /// for the firmware-driven path. Returns false when there is no telemetry frame or the
        /// parameter has no known encoder.
        /// </summary>
        public bool TryEncodeParam(ushort paramId, byte handle, GameData data, out ItmValue value)
            => TryEncodeParam(paramId, handle, data, 0, out value);

        /// <summary>
        /// Encodes a single subscribed parameter honouring the firmware's declared slot type
        /// (<paramref name="dataType"/>, from the subscription push; 0 = unknown). The type
        /// matters for GEAR, whose wire form differs per display: a PBME declares u8 (0x12)
        /// and ignores ASCII, while a Formula V3 takes ASCII chars ('n', '1'..'9', 'r') — both
        /// hardware/capture-verified against the official software. Other parameters encode
        /// the same regardless.
        ///
        /// When a <see cref="FieldMapping"/> is configured, resolves the override source
        /// through the shared <see cref="IPropertyReader"/> and feeds the scalar through the
        /// param's typed encoder path. Resolution failure falls back to the built-in default
        /// for that frame (never a stale overridden value). Gear/EngineMapping are never
        /// overridden (validator + excluded scalar path).
        /// </summary>
        public bool TryEncodeParam(ushort paramId, byte handle, GameData data, byte dataType, out ItmValue value)
        {
            value = default;
            var status = data?.NewData;
            if (status == null)
                return false;

            // Gear text form is firmware-slot-driven and never remapped. ASCII by identity
            // — no itmField publish (no single numeric meaning).
            if (paramId == ItmParam.Gear && ItmTelemetry.IsTextType(dataType))
            {
                value = ItmValue.Ascii(handle, ItmParam.Gear, GearText(status.Gear));
                return true;
            }

            // Source override: resolve this frame; miss → built-in default (below).
            if (TryEncodeOverride(paramId, handle, out value))
                return true;

            if (!_registry.TryGetValue(paramId, out var encode))
                return false;
            value = encode(status, handle);
            PublishNatural(paramId, status);
            return true;
        }

        // Returns true when an override was resolved and encoded; false means "use built-in"
        // (no mapping, excluded param, no reader, or resolution miss this frame).
        private bool TryEncodeOverride(ushort paramId, byte handle, out ItmValue value)
        {
            value = default;
            // Standing law: no per-field hardcoded exclusion — envelope DATA decides.
            if (!_fieldMappings.TryGetValue(paramId, out var mapping) || mapping?.Source == null)
                return false;
            if (_properties == null)
                return false;
            if (!_properties.TryGetNumber(mapping.Source, out double n))
                return false;   // miss → caller falls back to built-in for this frame
            if (!_scalarEncoders.TryGetValue(paramId, out var encodeScalar))
                return false;
            n = ApplyValueFormat(n, mapping.Format);
            value = encodeScalar(n, handle);
            // ONE publish site: natural pre-wire scalar (property-picker unit), same as built-in.
            PublishNaturalScalar(paramId, n);
            return true;
        }

        /// <summary>
        /// Host-side value-format transforms applied before the typed encoder. Today only
        /// <see cref="FormatOneDecimal"/>; withTotal/bare/unit are suffix-only keys.
        /// </summary>
        private static double ApplyValueFormat(double n, string format)
        {
            if (string.Equals(format, FormatOneDecimal, StringComparison.Ordinal))
                return Math.Round(n, 1);
            return n;
        }

        /// <summary>
        /// True for ASCII-on-wire params skipped by itmField publish (param identity, not Size).
        /// </summary>
        public static bool IsAsciiParam(ushort paramId)
            => paramId == ItmParam.Gear || paramId == ItmParam.EngineMapping;

        /// <summary>
        /// Publish the natural pre-wire scalar for a built-in encode (status already validated).
        /// No-op for ASCII params and when the sink is null.
        /// </summary>
        private void PublishNatural(ushort paramId, StatusDataBase status)
        {
            if (IsAsciiParam(paramId))
                return;
            if (!_naturalScalars.TryGetValue(paramId, out var select))
                return;
            PublishNaturalScalar(paramId, select(status));
        }

        /// <summary>
        /// Single publish site for itmField condition reads. Emits the natural pre-wire
        /// scalar (property-picker unit) — never the wire encoding. ASCII skipped by identity.
        /// </summary>
        private void PublishNaturalScalar(ushort paramId, double natural)
        {
            if (IsAsciiParam(paramId))
                return;
            ParamValueSink?.Publish(paramId, natural);
        }

        /// <summary>
        /// Encodes the current telemetry for <paramref name="page"/> into value entries.
        /// Handles are assigned <paramref name="handleBase"/>..+N-1 in the page's catalog order
        /// (<see cref="ItmTelemetry.ParamsFor"/>). Returns an empty list when there is no
        /// telemetry frame or the page carries no parameters.
        /// Routes through the same publish site as <see cref="TryEncodeParam"/>.
        /// </summary>
        public IReadOnlyList<ItmValue> BuildValues(ItmPage page, GameData data, byte handleBase = 0)
        {
            var status = data?.NewData;
            var ids = ItmTelemetry.ParamsFor(page);
            if (status == null || ids.Count == 0)
                return Array.Empty<ItmValue>();

            var values = new ItmValue[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                ushort paramId = ids[i];
                byte handle = (byte)(handleBase + i);
                // BuildValues is the offline/catalog path — still honour overrides when
                // configured so tests and any future consumer see the same layering.
                // Same single publish site as the live TryEncodeParam path.
                if (TryEncodeOverride(paramId, handle, out var overridden))
                    values[i] = overridden;
                else
                {
                    values[i] = _registry[paramId](status, handle);
                    PublishNatural(paramId, status);
                }
            }
            return values;
        }

        // ── Encoding helpers ─────────────────────────────────────────────

        private static float Seconds(TimeSpan t) => (float)t.TotalSeconds;

        // The nearest on-track gap (seconds) among a set of opponents, or 0 if none / unknown.
        // SimHub gives no scalar gap-to-car-ahead/behind, so take the smallest
        // |RelativeGapToPlayer| from the ahead/behind list (robust to list ordering).
        // Internal: SimHubPropertySource reads the same value for the rule engine's
        // GapAhead/GapBehind built-ins, so both surfaces agree on what "the gap" is.
        // Static: pure helper, shared by the built-in registry and SimHubPropertySource.
        internal static float NearestGap(IEnumerable<Opponent> opponents)
        {
            if (opponents == null) return 0f;
            double best = double.MaxValue;
            foreach (var o in opponents)
            {
                double? g = o?.RelativeGapToPlayer;
                if (g.HasValue)
                {
                    double a = Math.Abs(g.Value);
                    if (a < best) best = a;
                }
            }
            return best == double.MaxValue ? 0f : (float)best;
        }

        // Round a possibly non-finite value to int, mapping NaN/Infinity to 0. A NaN would
        // otherwise slip through range comparisons (all false) into an undefined cast — the same
        // firmware-safety concern BrakeBiasTenths guards against.
        private static int SafeRound(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? 0 : (int)Math.Round(value);

        // Speed is non-negative; clamp to [0, Int16 max] (the SPEED param is Int16).
        private static short ClampSpeed(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            double v = Math.Round(value);
            if (v < 0) return 0;
            if (v > short.MaxValue) return short.MaxValue;
            return (short)v;
        }

        private static byte ClampByte(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            double v = Math.Round(value);
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        private static byte ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        // BRAKE_BIAS is an Int32 in tenths of a percent (confirmed by capture: 51.2% => 512).
        // SimHub reports a percentage, so send round(percent * 10), clamped to [0, 1000]
        // (0–100.0%).
        private static int BrakeBiasTenths(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent)) return 0;
            int v = (int)Math.Round(percent * 10.0);
            if (v < 0) return 0;
            if (v > 1000) return 1000;
            return v;
        }

        // ENGINE_MAPPING is rendered by the firmware as text (e.g. "1", "10"). SimHub reports the
        // map as an int; send its decimal string. Clamp to [0, 99] so the payload stays within
        // two ASCII bytes (the range the official software was observed to use).
        private static string EngineMapText(int map)
        {
            if (map < 0) map = 0;
            if (map > 99) map = 99;
            return map.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Reverse sentinel: -1 as a Uint8, which the firmware renders as "r"
        /// (hardware-verified on a PBME under clean single-writer conditions).
        /// </summary>
        private const byte GearReverse = 0xFF;

        /// <summary>
        /// Parses SimHub's gear string to the ITM Uint8 gear value: "N"/empty = 0, "1".."9" =
        /// that number, "R" = <see cref="GearReverse"/>. Forward gears are literal, confirmed
        /// against official-software PBME captures (N=0, gears 1..4 literal). Used when the
        /// firmware declares GEAR as a numeric slot (or the type is unknown); text-declared
        /// displays take <see cref="GearText"/> instead.
        /// </summary>
        private static byte EncodeGear(string gear)
        {
            if (string.IsNullOrEmpty(gear))
                return 0;

            gear = gear.Trim().ToUpperInvariant();
            if (gear == "R" || gear == "REVERSE") return GearReverse;
            if (gear == "N" || gear == "NEUTRAL") return 0;

            return int.TryParse(gear, out int g) && g >= 0 && g <= 254 ? (byte)g : (byte)0;
        }

        /// <summary>
        /// Parses SimHub's gear string to the ITM ASCII gear form used by text-declared
        /// displays (e.g. Formula V3): lowercase "n" for neutral, lowercase "r" for reverse,
        /// forward gears as their decimal digits — exactly the characters the official
        /// software puts on the wire (capture-verified: 'n', '5'..'1', 'r').
        /// </summary>
        private static string GearText(string gear)
        {
            if (string.IsNullOrEmpty(gear))
                return "n";

            gear = gear.Trim().ToUpperInvariant();
            if (gear == "R" || gear == "REVERSE") return "r";
            if (gear == "N" || gear == "NEUTRAL") return "n";

            return int.TryParse(gear, out int g) && g >= 1 && g <= 99
                ? g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "n";
        }
    }
}
