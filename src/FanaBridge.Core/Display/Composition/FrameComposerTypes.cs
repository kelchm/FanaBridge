using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Composition
{
    /// <summary>
    /// Construction options for <see cref="FrameComposer"/>. Catalog-derived maps are
    /// injected so the composer stays pure (no catalog I/O).
    /// </summary>
    public sealed class FrameComposerOptions
    {
        /// <summary>
        /// Per-param capability envelope (§14 gating). Missing key = param not on this
        /// wheel (ladder inert). Present with null tri-states = paint + warn.
        /// </summary>
        public IReadOnlyDictionary<ushort, FieldCapability> Capabilities { get; set; }

        /// <summary>
        /// Single primary-host map shared with E4 (<c>SeatArbiterOptions.PrimaryHostByParam</c>).
        /// DestinationId for field surfaces is owned by this map — no second host source.
        /// Build via <see cref="FieldCapability.PrimaryHostMapFromCapabilities"/> when
        /// catalog capabilities are the source of truth.
        /// </summary>
        public IReadOnlyDictionary<ushort, string> PrimaryHostByParam { get; set; }

        /// <summary>Device key stamped on the composed-resolution slice.</summary>
        public string DeviceKey { get; set; } = "";

        /// <summary>Optional diagnostic sink (capability null-untested warns, etc.).</summary>
        public Action<string> Warn { get; set; }
    }

    /// <summary>
    /// Injected game/property values for segment content rendering. Matches the v9
    /// <c>FormatScreen</c> idle law: dynamic kinds blank when not in-game; Text/Message
    /// always; Property via <see cref="Properties"/>.
    /// </summary>
    public sealed class SegmentContentContext
    {
        public bool InGame { get; set; } = true;

        public double? SpeedLocal { get; set; }
        public string Gear { get; set; }
        public double? Rpms { get; set; }
        public double? Position { get; set; }
        public double? Fuel { get; set; }

        /// <summary>Property content kind reader (may be null).</summary>
        public IPropertyReader Properties { get; set; }
    }

    /// <summary>One tick of frame-composition input. Pure: caller injects clock and snapshots.</summary>
    public sealed class FrameComposerTickInput
    {
        /// <summary>Injected engine clock (ms) — effect clocks key on this only.</summary>
        public long NowMs { get; set; }

        /// <summary>
        /// Hosted page id whose 3-char segment face is composed this tick (col01).
        /// Buffer-continuity law: when the display winner is an ITM page, pass the
        /// landing/remembered hosted page so col01 stays a continuous truthful stream.
        /// When a hosted page owns the display, pass that page id.
        /// <para>
        /// E7 is expected to resolve rest semantics (<c>rest:inSession</c> / <c>rest:idle</c>)
        /// to a concrete page destination before calling E5 when presence must be definite;
        /// raw rest / null destinations leave Presence null (unknown), never a false OffScreen.
        /// </para>
        /// </summary>
        public string SegmentHostedPageId { get; set; }

        /// <summary>
        /// Destination currently shown on the display plane (E4 intent effective page).
        /// Used for Presence: OffScreen when a surface's page is not this destination;
        /// null / rest / unknown → Presence null (honest unknown).
        /// </summary>
        public string DisplayedDestinationId { get; set; }

        /// <summary>
        /// When true, the wheel-screen plane holds col01 (special / logo / idle screen).
        /// Composer still produces <see cref="FrameComposerTickResult.SegmentFrame"/> for
        /// buffer continuity, but marks it non-writable — E7 must not write col01 and must
        /// reclaim on release (v9 DriveSpecialCommand exclusivity). See
        /// evaluated-carrier-contract.md wheel-screen/col01 exclusivity.
        /// </summary>
        public bool SegmentSurfaceHeldByWheelScreen { get; set; }

        /// <summary>
        /// Pre-evaluated snapshots for layers and field overrides. Composer NEVER writes
        /// evaluator state — activation is read-only input.
        /// </summary>
        public IReadOnlyList<CarrierTickSnapshot> CarrierSnapshots { get; set; }
            = Array.Empty<CarrierTickSnapshot>();

        /// <summary>
        /// E4 dismissal latch set (display-surface summon suppression). First-class
        /// INPUT only — a dismissed-but-still-Active layer/override PAINTS (round-5
        /// route law / suppress-the-summon-only). Never used as a paint gate.
        /// E5 self-stamps <see cref="CarrierRowLabels.Dismissed"/> from this set so
        /// merge with E4 cannot drop the label.
        /// </summary>
        public IReadOnlyCollection<string> DismissedCarrierIds { get; set; }
            = Array.Empty<string>();

        /// <summary>Segment content sources (speed/gear/… + property reader).</summary>
        public SegmentContentContext Content { get; set; } = new SegmentContentContext();
    }

    /// <summary>
    /// Single owner of the ITM suffix region for one param this tick.
    /// The wire has one producer (mapper suffix path); the plan must never hand E7
    /// contradictory instructions for the same region.
    /// </summary>
    public enum SuffixOwner
    {
        /// <summary>
        /// Mapper computes from telemetry via the existing single owner
        /// (TryGetUnitSuffix / TryResolveTotalSuffix). <see cref="FieldRegionPlan.SuffixText"/>
        /// is advisory/null — do <b>not</b> read null as "write blank".
        /// </summary>
        BaseComputed = 0,

        /// <summary>
        /// Winner paints the whole suffix region (indicator / override text).
        /// Plan coerces <see cref="FieldRegionPlan.ValueFormat"/> to bare so the mapper
        /// cannot re-fill the region with /total or unit letter.
        /// </summary>
        Override = 1,

        /// <summary>Explicit blank write (reserved; not used for resting base).</summary>
        Blank = 2,
    }

    /// <summary>
    /// Resolved field-plane plan for one param: value source/format + suffix text +
    /// alignment + effect. Wire encoding (ParamDefs) is the mapper's job — not here.
    /// </summary>
    public sealed class FieldRegionPlan
    {
        public ushort ParamId { get; set; }

        /// <summary>Top active capable override, or null when resting on base.</summary>
        public string WinnerCarrierId { get; set; }

        // ── Value region ─────────────────────────────────────────────────

        /// <summary>True when the winner paints the value region; false = base fills.</summary>
        public bool ValueFromOverride { get; set; }

        public ValueSource ValueSource { get; set; }
        public string ValueFormat { get; set; }

        /// <summary>Override content when <see cref="ValueFromOverride"/> (text/kind).</summary>
        public ContentObject ValueContent { get; set; }

        // ── Suffix region ────────────────────────────────────────────────

        /// <summary>Single owner of the suffix region this tick.</summary>
        public SuffixOwner SuffixOwner { get; set; } = SuffixOwner.BaseComputed;

        /// <summary>
        /// True when <see cref="SuffixOwner"/> is <see cref="SuffixOwner.Override"/>.
        /// Kept for callers that still key on the bool; prefer <see cref="SuffixOwner"/>.
        /// </summary>
        public bool SuffixFromOverride
        {
            get => SuffixOwner == SuffixOwner.Override;
            set => SuffixOwner = value ? SuffixOwner.Override : SuffixOwner.BaseComputed;
        }

        /// <summary>
        /// Resolved suffix text for this tick (override content, base.baseSuffix advisory,
        /// or width-blank on blink off). When <see cref="SuffixOwner"/> is
        /// <see cref="SuffixOwner.BaseComputed"/>, null means mapper computes — not blank.
        /// </summary>
        public string SuffixText { get; set; }

        /// <summary>
        /// Property-sourced suffix: source carried when content.kind=property.
        /// Null for text/message overrides and base-computed.
        /// </summary>
        public ValueSource SuffixSource { get; set; }

        /// <summary>
        /// Property-sourced suffix format key (with Source). Null when not property.
        /// </summary>
        public string SuffixFormat { get; set; }

        /// <summary>
        /// Multi-char suffix alignment (left default). Applied only when the winner
        /// paints the suffix; base-filled suffix always uses left/default.
        /// </summary>
        public FieldAlignment Alignment { get; set; } = FieldAlignment.Left;

        /// <summary>
        /// Suffix text after alignment / clamp / effect resolution for THIS tick.
        /// Off-phase blink → width-blank. E7 writes exactly this string for the
        /// override-owned region. When BaseComputed, may be null (mapper owns).
        /// </summary>
        public string AlignedSuffixText { get; set; }

        // ── Effect ───────────────────────────────────────────────────────

        /// <summary>Effect of the winning override (None when base fills both regions).</summary>
        public ContentEffect Effect { get; set; } = ContentEffect.None;

        /// <summary>
        /// Blink/flash visibility at <c>nowMs</c>: true = on (show), false = off phase.
        /// Always true for None/Scroll/Unknown. Uses shared
        /// <see cref="FanaBridge.Display.Legacy.LegacyEffectClock.IsOnPhase"/>.
        /// <b>Value regions do not blink</b> — plan effect applies to suffix (and
        /// segment face) only.
        /// </summary>
        public bool EffectVisible { get; set; } = true;

        // ── Diagnostics ──────────────────────────────────────────────────

        /// <summary>
        /// Overrides that were capability-inert, locked, unrenderable, or soft-clamped
        /// this tick. Authored text is preserved (document never rewritten).
        /// </summary>
        public IReadOnlyList<DegradedFieldChild> DegradedChildren { get; set; }
            = Array.Empty<DegradedFieldChild>();
    }

    /// <summary>A field override that did not compete / soft-degraded due to capability.</summary>
    public sealed class DegradedFieldChild
    {
        public string CarrierId { get; set; }
        public FieldDegradeReason Reason { get; set; }

        /// <summary>Authored content text preserved (document never rewritten).</summary>
        public string AuthoredText { get; set; }
    }

    /// <summary>Full pure-composer result for one tick.</summary>
    public sealed class FrameComposerTickResult
    {
        /// <summary>
        /// 3-byte segment frame for <see cref="FrameComposerTickInput.SegmentHostedPageId"/>.
        /// Always produced for buffer-continuity (blank when page missing / unreadable).
        /// Write only when <see cref="SegmentFrameWritable"/> is true — wheel-screen
        /// exclusivity may hold the surface.
        /// </summary>
        public byte[] SegmentFrame { get; set; }

        /// <summary>
        /// False when the wheel-screen plane holds col01 this tick
        /// (<see cref="FrameComposerTickInput.SegmentSurfaceHeldByWheelScreen"/>).
        /// E7 must not write <see cref="SegmentFrame"/> and must reclaim on release.
        /// </summary>
        public bool SegmentFrameWritable { get; set; } = true;

        /// <summary>Hosted page id that produced <see cref="SegmentFrame"/> (echo).</summary>
        public string SegmentHostedPageId { get; set; }

        /// <summary>Layer that painted the segment face, or null for base / blank.</summary>
        public string SegmentWinnerCarrierId { get; set; }

        /// <summary>Resolved content text before effect (diagnostics / parity).</summary>
        public string SegmentRenderedText { get; set; }

        /// <summary>Effect applied to the segment face.</summary>
        public ContentEffect SegmentEffect { get; set; }

        /// <summary>
        /// True when segment content carried an authored <c>format</c> the plane cannot
        /// consume (unknown spelling on a dynamic kind) — warned + degraded-visible; still
        /// rendered via LegacyValueFormatter (D3 integer path). <c>oneDecimal</c> on numeric
        /// kinds is consumed and does not set this flag.
        /// </summary>
        public bool SegmentContentFormatDegraded { get; set; }

        /// <summary>Per-param field region plans (device-wide ladders).</summary>
        public IReadOnlyList<FieldRegionPlan> FieldPlans { get; set; }
            = Array.Empty<FieldRegionPlan>();

        /// <summary>
        /// Composed-resolution slice for page:{id} and field:{param} surfaces only.
        /// Seat/wheel-screen surfaces are absent (E4/E6 own those).
        /// </summary>
        public ComposedResolutionRecord Resolution { get; set; }
    }
}
