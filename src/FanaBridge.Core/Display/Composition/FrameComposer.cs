using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Composition
{
    /// <summary>
    /// Pure frame composer (phase E5). Two responsibilities:
    /// <list type="bullet">
    /// <item><b>Segment face</b> — layer ladder on a hosted page → 3 segment bytes
    /// (LegacyValueFormatter + LegacyEffectClock). Buffer-continuity: always emits the
    /// frame for the caller-supplied hosted page (landing while ITM is displayed).
    /// Wheel-screen exclusivity: still composes, marks non-writable when held.</item>
    /// <item><b>Field plane</b> — one ladder per param, winner paints declared regions
    /// (suffix/value/both), base fills the rest, lower-ranked active child paints
    /// NOTHING. Output is a region plan (mapper owns ParamDefs). Suffix is ONE region
    /// with a single <see cref="SuffixOwner"/>.</item>
    /// </list>
    /// Never writes evaluator state. <see cref="FrameComposerTickInput.DismissedCarrierIds"/>
    /// is pure INPUT and does <b>not</b> gate painting (round-5 route law); E5 self-stamps
    /// <see cref="CarrierRowLabels.Dismissed"/> for merge honesty.
    /// </summary>
    public sealed class FrameComposer
    {
        private readonly DisplayConfigV2 _config;
        private readonly string _deviceKey;
        private readonly IReadOnlyDictionary<ushort, FieldCapability> _capabilities;
        private readonly IReadOnlyDictionary<ushort, string> _primaryHostByParam;
        private readonly Action<string> _warn;
        private readonly HashSet<string> _warnedKeys = new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, PageEntry> _hostedById =
            new Dictionary<string, PageEntry>(StringComparer.Ordinal);
        private readonly List<PageEntry> _hostedPages = new List<PageEntry>();
        private readonly List<PageEntry> _degradedHostedPages = new List<PageEntry>();
        private readonly List<KeyValuePair<ushort, FieldEntry>> _fields =
            new List<KeyValuePair<ushort, FieldEntry>>();

        public FrameComposer(DisplayConfigV2 config, FrameComposerOptions options = null)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            _config = config;
            options = options ?? new FrameComposerOptions();
            _deviceKey = options.DeviceKey ?? "";
            _capabilities = options.Capabilities
                ?? (IReadOnlyDictionary<ushort, FieldCapability>)new Dictionary<ushort, FieldCapability>();
            // ONE shared host map with E4 — never invent a second primary-host source.
            _primaryHostByParam = options.PrimaryHostByParam
                ?? FieldCapability.PrimaryHostMapFromCapabilities(_capabilities);
            _warn = options.Warn;

            if (_config.Pages != null)
            {
                foreach (var page in _config.Pages)
                {
                    if (page == null || page.Kind != PageEntryKind.HostedPage)
                        continue;
                    if (page.DegradedAtLoad)
                    {
                        _degradedHostedPages.Add(page);
                        continue;
                    }
                    if (string.IsNullOrEmpty(page.Id))
                        continue;
                    _hostedById[page.Id] = page;
                    _hostedPages.Add(page);
                }
            }

            if (_config.Fields != null)
            {
                foreach (var kv in _config.Fields.OrderBy(k => k.Key))
                {
                    if (kv.Value != null)
                        _fields.Add(new KeyValuePair<ushort, FieldEntry>(kv.Key, kv.Value));
                }
            }
        }

        /// <summary>Surface key for a hosted page's layer ladder (shared spelling).</summary>
        public static string PageSurfaceId(string hostedPageId)
            => DestinationIds.PageSurface(hostedPageId);

        /// <summary>Surface key for a field's override ladder (shared spelling).</summary>
        public static string FieldSurfaceId(ushort paramId)
            => DestinationIds.FieldSurface(paramId);

        /// <summary>
        /// Surface key from a raw param-id string; normalizes via
        /// <see cref="DestinationIds.FieldSurface(string)"/> (ushort.TryParse).
        /// </summary>
        public static string FieldSurfaceId(string paramKey)
            => DestinationIds.FieldSurface(paramKey);

        /// <summary>Compose one tick: segment face + field plans + resolution slice.</summary>
        public FrameComposerTickResult Tick(FrameComposerTickInput input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            var snaps = IndexSnapshots(input.CarrierSnapshots);
            var dismissed = IndexDismissed(input.DismissedCarrierIds);

            var surfaceWinners = new List<SurfaceWinner>();
            var statuses = new List<CarrierResolutionStatus>();
            var fieldPlans = new List<FieldRegionPlan>();

            // Degraded hosted pages: warn-once; rows kept where surface key is unambiguous.
            EmitDegradedPageDiagnostics(statuses, dismissed);

            // ── A+B. Layer ladders (all hosted pages) + segment face paint ─
            // Statuses for ALL pages; effect render ONLY for the segment target (E5-005).
            string segmentPageId = input.SegmentHostedPageId;
            string displayed = input.DisplayedDestinationId;
            byte[] segmentFrame = BlankFrame();
            string segmentWinner = null;
            string segmentText = null;
            ContentEffect segmentEffect = ContentEffect.None;
            bool segmentFormatDegraded = false;

            foreach (var page in _hostedPages)
            {
                bool isSegmentTarget = string.Equals(
                    page.Id, segmentPageId, StringComparison.Ordinal);
                var face = ResolveLayerLadder(
                    page, snaps, input.Content, input.NowMs, displayed, dismissed,
                    renderFrame: isSegmentTarget);

                if (isSegmentTarget)
                {
                    segmentFrame = face.Frame ?? BlankFrame();
                    segmentWinner = face.WinnerCarrierId;
                    segmentText = face.RenderedText;
                    segmentEffect = face.Effect;
                    segmentFormatDegraded = face.FormatDegraded;
                }

                string surface = PageSurfaceId(page.Id);
                string dest = DestinationIds.Hosted(page.Id);
                surfaceWinners.Add(new SurfaceWinner(surface, face.WinnerCarrierId, dest));
                statuses.AddRange(face.Statuses);
            }

            // Landing page may be absent from the document (degraded / wrong id) —
            // still emit a blank frame so the col01 stream never stalls; warn-once.
            if (!string.IsNullOrEmpty(segmentPageId) && !_hostedById.ContainsKey(segmentPageId))
            {
                segmentFrame = BlankFrame();
                segmentWinner = null;
                segmentText = null;
                segmentEffect = ContentEffect.None;
                WarnOnce(
                    "landing-missing:" + segmentPageId,
                    "segment landing hosted page '" + segmentPageId
                    + "' is not in the document — blank frame (stream continues)");
            }

            // ── C. Field ladders (device-wide) ────────────────────────────
            foreach (var kv in _fields)
            {
                var plan = ResolveFieldLadder(
                    kv.Key, kv.Value, snaps, input.NowMs, displayed);
                fieldPlans.Add(plan);

                string surface = FieldSurfaceId(kv.Key);
                string dest = FieldDestination(kv.Key);
                surfaceWinners.Add(new SurfaceWinner(surface, plan.WinnerCarrierId, dest));
                statuses.AddRange(BuildFieldStatuses(
                    kv.Key, kv.Value, snaps, plan.WinnerCarrierId,
                    displayed, dest, dismissed));
            }

            // Contract §6.2 / E6-OP-05: while the wheel-screen plane holds the glass,
            // page:{id} rows demote OnScreen → OffScreen (record honesty — at most one
            // OnScreen per physical surface across E5+E6). Field/ITM surfaces unchanged.
            // SurfaceWinner still carries the ladder winner id; presence is surrendered.
            if (input.SegmentSurfaceHeldByWheelScreen)
                DemotePageOnScreenWhileWheelScreenHolds(statuses);

            var resolution = new ComposedResolutionRecord(
                input.NowMs,
                _deviceKey,
                surfaceWinners,
                statuses,
                input.CarrierSnapshots ?? Array.Empty<CarrierTickSnapshot>());

            // Contract §6.2 law 3: ReclaimEdge forces a write on wheel-screen release even
            // when content is unchanged. E5 produces frame + marker; E7/E8 writes.
            bool reclaim = input.ReclaimEdge;
            bool writable = reclaim || !input.SegmentSurfaceHeldByWheelScreen;

            return new FrameComposerTickResult
            {
                SegmentFrame = segmentFrame,
                SegmentFrameWritable = writable,
                ReclaimFrame = reclaim,
                SegmentHostedPageId = segmentPageId,
                SegmentWinnerCarrierId = segmentWinner,
                SegmentRenderedText = segmentText,
                SegmentEffect = segmentEffect,
                SegmentContentFormatDegraded = segmentFormatDegraded,
                FieldPlans = fieldPlans,
                Resolution = resolution,
            };
        }

        // ═════════════════════════════════════════════════════════════════
        // Segment / layer ladder
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// While wheel-screen holds col01, demote page-surface OnScreen → OffScreen so the
        /// merged composed-resolution record never claims two things on the glass at once.
        /// </summary>
        private static void DemotePageOnScreenWhileWheelScreenHolds(
            List<CarrierResolutionStatus> statuses)
        {
            if (statuses == null)
                return;
            for (int i = 0; i < statuses.Count; i++)
            {
                var s = statuses[i];
                if (s.Presence != CarrierPresence.OnScreen)
                    continue;
                if (s.SurfaceId == null
                    || !s.SurfaceId.StartsWith("page:", StringComparison.Ordinal))
                    continue;
                statuses[i] = new CarrierResolutionStatus(
                    s.CarrierId, s.SurfaceId, s.DestinationId,
                    CarrierPresence.OffScreen, s.RemainingMs, s.RowLabels);
            }
        }

        private sealed class LayerResolveResult
        {
            public string WinnerCarrierId;
            public string RenderedText;
            public ContentEffect Effect;
            public byte[] Frame;
            public bool FormatDegraded;
            public List<CarrierResolutionStatus> Statuses;
        }

        private LayerResolveResult ResolveLayerLadder(
            PageEntry page,
            Dictionary<string, CarrierTickSnapshot> snaps,
            SegmentContentContext content,
            long nowMs,
            string displayedDestinationId,
            HashSet<string> dismissed,
            bool renderFrame)
        {
            string winnerId = null;
            ContentObject winContent = null;
            ContentEffect winEffect = ContentEffect.None;

            if (page.Layers != null)
            {
                foreach (var layer in page.Layers)
                {
                    if (layer == null || string.IsNullOrEmpty(layer.Id))
                        continue;
                    if (!layer.EffectivelyEnabled)
                        continue;
                    if (!IsActive(snaps, layer.Id))
                        continue;
                    // Top-first: first active layer wins ALL 3 chars.
                    winnerId = layer.Id;
                    winContent = layer.Content;
                    winEffect = layer.Effect;
                    break;
                }
            }

            if (winnerId == null)
            {
                // Base is the pinned floor (not a stored layer).
                if (page.Base?.Content != null
                    && page.Base.Content.Kind != ContentKind.Unknown)
                {
                    winContent = page.Base.Content;
                    winEffect = page.Base.Effect;
                }
            }

            string text = null;
            byte[] frame = BlankFrame();
            bool formatDegraded = false;

            if (renderFrame)
            {
                text = FormatContent(winContent, content, out formatDegraded, page.Id);
                if (text != null)
                {
                    var legacyEffect = ToLegacyEffect(winEffect);
                    frame = LegacyEffectClock.Apply(text, legacyEffect, nowMs);
                }
            }
            else
            {
                // Still resolve text for diagnostics without allocating effect frames.
                text = FormatContent(winContent, content, out formatDegraded, page.Id);
            }

            return new LayerResolveResult
            {
                WinnerCarrierId = winnerId,
                RenderedText = text,
                Effect = winEffect,
                Frame = frame,
                FormatDegraded = formatDegraded,
                Statuses = BuildLayerStatuses(
                    page, snaps, winnerId, displayedDestinationId, dismissed),
            };
        }

        private List<CarrierResolutionStatus> BuildLayerStatuses(
            PageEntry page,
            Dictionary<string, CarrierTickSnapshot> snaps,
            string winnerId,
            string displayedDestinationId,
            HashSet<string> dismissed)
        {
            var list = new List<CarrierResolutionStatus>();
            if (page.Layers == null)
                return list;

            string surface = PageSurfaceId(page.Id);
            string dest = DestinationIds.Hosted(page.Id);
            var pagePresence = ClassifyPagePresence(dest, displayedDestinationId);

            foreach (var layer in page.Layers)
            {
                if (layer == null || string.IsNullOrEmpty(layer.Id))
                    continue;

                snaps.TryGetValue(layer.Id, out var snap);
                bool hasSnap = snap.CarrierId != null;
                bool active = hasSnap && snap.Active;
                int? remaining = hasSnap ? snap.RemainingMs : null;
                bool eligible = !hasSnap || snap.Eligible;

                CarrierPresence? presence;
                if (!active)
                    presence = CarrierPresence.Waiting;
                else if (!string.Equals(layer.Id, winnerId, StringComparison.Ordinal))
                    presence = CarrierPresence.Outranked;
                else if (pagePresence == PresenceKind.Unknown)
                    presence = null;
                else if (pagePresence == PresenceKind.Off)
                    presence = CarrierPresence.OffScreen;
                else
                    presence = CarrierPresence.OnScreen;

                CarrierRowLabels labels = CarrierRowLabels.None;
                if (!layer.Enabled)
                    labels |= CarrierRowLabels.Off;
                if (layer.DegradedAtLoad)
                    labels |= CarrierRowLabels.KeptAsIs;
                if (hasSnap && !eligible)
                    labels |= CarrierRowLabels.OutOfSessionScope;
                if (dismissed.Contains(layer.Id))
                    labels |= CarrierRowLabels.Dismissed;

                list.Add(new CarrierResolutionStatus(
                    layer.Id, surface, dest, presence, remaining, labels));
            }

            return list;
        }

        private void EmitDegradedPageDiagnostics(
            List<CarrierResolutionStatus> statuses,
            HashSet<string> dismissed)
        {
            for (int i = 0; i < _degradedHostedPages.Count; i++)
            {
                var page = _degradedHostedPages[i];
                int layerCount = page.Layers?.Count(l => l != null) ?? 0;
                string idLabel = string.IsNullOrEmpty(page.Id) ? "<no-id>" : page.Id;
                WarnOnce(
                    "degraded-page:" + idLabel + ":" + i,
                    "hosted page '" + idLabel + "' degraded at load — its "
                    + layerCount + " layer(s) are inert");

                // Duplicate-id / empty-id: surface key would be ambiguous — warn only.
                if (string.IsNullOrEmpty(page.Id) || _hostedById.ContainsKey(page.Id))
                    continue;

                // Unambiguous degraded page (e.g. reserved-prefix): keep layer rows visible.
                string surface = PageSurfaceId(page.Id);
                string dest = DestinationIds.Hosted(page.Id);
                if (page.Layers == null)
                    continue;
                foreach (var layer in page.Layers)
                {
                    if (layer == null || string.IsNullOrEmpty(layer.Id))
                        continue;
                    CarrierRowLabels labels = CarrierRowLabels.KeptAsIs;
                    if (!layer.Enabled)
                        labels |= CarrierRowLabels.Off;
                    if (dismissed.Contains(layer.Id))
                        labels |= CarrierRowLabels.Dismissed;
                    statuses.Add(new CarrierResolutionStatus(
                        layer.Id, surface, dest, presence: null, remainingMs: null, labels));
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Field ladder
        // ═════════════════════════════════════════════════════════════════

        private FieldRegionPlan ResolveFieldLadder(
            ushort paramId,
            FieldEntry field,
            Dictionary<string, CarrierTickSnapshot> snaps,
            long nowMs,
            string displayedDestinationId)
        {
            bool hasCap = _capabilities.TryGetValue(paramId, out var cap);
            // Absent from capability map: param not on this wheel — whole ladder inert.
            bool paramAbsent = !hasCap;

            var degraded = new List<DegradedFieldChild>();
            string winnerId = null;
            FieldOverride winner = null;

            if (field.Overrides != null)
            {
                foreach (var ov in field.Overrides)
                {
                    if (ov == null || string.IsNullOrEmpty(ov.Id))
                        continue;

                    if (!ov.EffectivelyEnabled)
                    {
                        degraded.Add(new DegradedFieldChild
                        {
                            CarrierId = ov.Id,
                            Reason = FieldDegradeReason.Inert,
                            AuthoredText = AuthoredText(ov.Content),
                        });
                        continue;
                    }

                    if (paramAbsent)
                    {
                        // Activity-independent: impossible on this wheel.
                        degraded.Add(new DegradedFieldChild
                        {
                            CarrierId = ov.Id,
                            Reason = FieldDegradeReason.ParamNotOnWheel,
                            AuthoredText = AuthoredText(ov.Content),
                        });
                        continue;
                    }

                    // Capability gate — ACTIVITY-INDEPENDENT (E5-02): every enabled
                    // override is checked; inactive still cannot win the ladder.
                    var softClamp = false;
                    var reason = CapabilityGate(ov, cap, paramId, out softClamp);
                    if (reason != FieldDegradeReason.None && !softClamp)
                    {
                        degraded.Add(new DegradedFieldChild
                        {
                            CarrierId = ov.Id,
                            Reason = reason,
                            AuthoredText = AuthoredText(ov.Content),
                        });
                        continue;
                    }

                    if (!IsActive(snaps, ov.Id))
                        continue;

                    // Soft clamp (width overflow): still wins; authored preserved in degrade note.
                    if (softClamp)
                    {
                        degraded.Add(new DegradedFieldChild
                        {
                            CarrierId = ov.Id,
                            Reason = FieldDegradeReason.SuffixWidthOverflow,
                            AuthoredText = AuthoredText(ov.Content),
                        });
                    }

                    winnerId = ov.Id;
                    winner = ov;
                    break;
                }
            }

            if (paramAbsent && field.Overrides != null && field.Overrides.Count > 0)
            {
                WarnOnce(
                    "param-absent:" + paramId,
                    "param " + paramId
                    + " not in this wheel's catalog — field children inert");
            }

            var plan = new FieldRegionPlan
            {
                ParamId = paramId,
                WinnerCarrierId = winnerId,
                DegradedChildren = degraded,
            };

            FieldBase bas = field.Base;
            bool paintsValue = false;
            bool paintsSuffix = false;

            if (winner != null)
            {
                switch (winner.Writes)
                {
                    case FieldWrites.Value:
                        paintsValue = true;
                        break;
                    case FieldWrites.Suffix:
                        paintsSuffix = true;
                        break;
                    case FieldWrites.Both:
                        paintsValue = true;
                        paintsSuffix = true;
                        break;
                }

                plan.Effect = winner.Effect;
                // Shared LegacyEffectClock only. Value regions do NOT blink — effect
                // visibility applies to the suffix region (and segment face) alone.
                plan.EffectVisible = paintsSuffix
                    ? EffectIsVisible(winner.Effect, nowMs)
                    : true;
            }
            else
            {
                plan.Effect = ContentEffect.None;
                plan.EffectVisible = true;
            }

            // Value region: winner paints declared; else base fills.
            if (paintsValue)
            {
                plan.ValueFromOverride = true;
                plan.ValueContent = winner.Content;
                plan.ValueSource = winner.Content?.Source;
                plan.ValueFormat = winner.Content?.Format;
            }
            else
            {
                plan.ValueFromOverride = false;
                plan.ValueSource = bas?.Source;
                plan.ValueFormat = bas?.Format;
                plan.ValueContent = null;
            }

            // Suffix region: single owner (E5-01). Winner paints → Override + bare format.
            // Alignment applies ONLY when the winner paints suffix (E5-003).
            FieldAlignment alignForSuffix = FieldAlignment.Left;
            if (paintsSuffix)
            {
                plan.SuffixOwner = SuffixOwner.Override;
                alignForSuffix = winner.Alignment == FieldAlignment.Unknown
                    ? FieldAlignment.Left
                    : winner.Alignment;
                plan.Alignment = alignForSuffix;

                // Coerce value format to bare so the mapper's single suffix path cannot
                // re-fill the region (withTotal / unit). Composer never hands a format
                // that contradicts SuffixOwner.Override.
                plan.ValueFormat = FieldFormats.Bare;

                var content = winner.Content;
                if (content != null && content.Kind == ContentKind.Property)
                {
                    plan.SuffixSource = content.Source;
                    plan.SuffixFormat = content.Format;
                    plan.SuffixText = null; // property-sourced; E7 renders from Source+Format
                }
                else
                {
                    string raw = AuthoredText(content);
                    // Runtime clamp to catalog width (not inert).
                    if (cap?.SuffixWidth is int w && w >= 0 && raw != null && raw.Length > w)
                        plan.SuffixText = raw.Substring(0, w);
                    else
                        plan.SuffixText = raw;
                }

                // Per-tick resolved text: width-blank on blink off phase.
                if (!plan.EffectVisible)
                {
                    plan.AlignedSuffixText = WidthBlank(cap?.SuffixWidth, plan.SuffixText);
                    plan.SuffixText = plan.AlignedSuffixText;
                }
                else
                {
                    plan.AlignedSuffixText = AlignSuffix(
                        plan.SuffixText, alignForSuffix, cap?.SuffixWidth);
                }
            }
            else
            {
                // BaseComputed: mapper owns the dynamic region; SuffixText is advisory.
                plan.SuffixOwner = SuffixOwner.BaseComputed;
                plan.Alignment = FieldAlignment.Left;
                plan.SuffixText = bas?.BaseSuffix;
                plan.AlignedSuffixText = AlignSuffix(
                    plan.SuffixText, FieldAlignment.Left, cap?.SuffixWidth);
            }

            _ = displayedDestinationId;
            return plan;
        }

        private List<CarrierResolutionStatus> BuildFieldStatuses(
            ushort paramId,
            FieldEntry field,
            Dictionary<string, CarrierTickSnapshot> snaps,
            string winnerId,
            string displayedDestinationId,
            string destinationId,
            HashSet<string> dismissed)
        {
            var list = new List<CarrierResolutionStatus>();
            if (field.Overrides == null)
                return list;

            string surface = FieldSurfaceId(paramId);
            bool hasCap = _capabilities.TryGetValue(paramId, out var cap);
            bool paramAbsent = !hasCap;
            var pagePresence = paramAbsent
                ? PresenceKind.Unknown
                : ClassifyFieldPresence(paramId, cap, displayedDestinationId);

            foreach (var ov in field.Overrides)
            {
                if (ov == null || string.IsNullOrEmpty(ov.Id))
                    continue;

                snaps.TryGetValue(ov.Id, out var snap);
                bool hasSnap = snap.CarrierId != null;
                bool active = hasSnap && snap.Active;
                int? remaining = hasSnap ? snap.RemainingMs : null;
                bool eligible = !hasSnap || snap.Eligible;

                bool soft;
                bool capable = ov.EffectivelyEnabled
                    && !paramAbsent
                    && (CapabilityGate(ov, cap, paramId, out soft) == FieldDegradeReason.None
                        || soft);
                bool isWinner = capable
                    && string.Equals(ov.Id, winnerId, StringComparison.Ordinal);

                // Capability degrade is activity-independent for labels (E5-02).
                bool capabilityMiss = ov.EffectivelyEnabled
                    && (paramAbsent
                        || (CapabilityGate(ov, cap, paramId, out _) != FieldDegradeReason.None
                            && !IsSoftOnly(ov, cap, paramId)));

                CarrierPresence? presence;
                if (!active)
                    presence = CarrierPresence.Waiting;
                else if (!isWinner)
                    presence = CarrierPresence.Outranked;
                else if (pagePresence == PresenceKind.Unknown)
                    presence = null;
                else if (pagePresence == PresenceKind.Off)
                    presence = CarrierPresence.OffScreen;
                else
                    presence = CarrierPresence.OnScreen;

                CarrierRowLabels labels = CarrierRowLabels.None;
                if (!ov.Enabled)
                    labels |= CarrierRowLabels.Off;
                if (ov.DegradedAtLoad)
                    labels |= CarrierRowLabels.KeptAsIs;
                if (capabilityMiss)
                {
                    labels |= CarrierRowLabels.CantRunHere;
                    if (paramAbsent)
                        labels |= CarrierRowLabels.NoWheel;
                }
                if (hasSnap && !eligible)
                    labels |= CarrierRowLabels.OutOfSessionScope;
                if (dismissed.Contains(ov.Id))
                    labels |= CarrierRowLabels.Dismissed;

                list.Add(new CarrierResolutionStatus(
                    ov.Id, surface, destinationId, presence, remaining, labels));
            }

            return list;
        }

        private static bool IsSoftOnly(FieldOverride ov, FieldCapability cap, ushort paramId)
        {
            // Width overflow is soft (clamp); other gates are hard.
            if (ov == null)
                return false;
            bool wantsSuffix = ov.Writes == FieldWrites.Suffix || ov.Writes == FieldWrites.Both;
            if (!wantsSuffix)
                return false;
            string text = AuthoredText(ov.Content) ?? "";
            if (cap?.SuffixWidth is int width && width >= 0 && text.Length > width)
            {
                // Soft only when no hard gate also applies.
                if (cap.SuffixSupported == false)
                    return false;
                if (cap.Overridable == false)
                    return false;
                if (FieldContentGate(ov) != FieldDegradeReason.None)
                    return false;
                return true;
            }
            return false;
        }

        private FieldDegradeReason CapabilityGate(
            FieldOverride ov, FieldCapability cap, ushort paramId, out bool softClamp)
        {
            softClamp = false;

            if (ov.Writes == FieldWrites.Unknown)
                return FieldDegradeReason.UnknownWrites;

            // Catalog lock (Gear / EngineMapping) — activity-independent.
            if (cap != null && cap.Overridable == false)
                return FieldDegradeReason.ParamLocked;

            var contentReason = FieldContentGate(ov);
            if (contentReason != FieldDegradeReason.None)
                return contentReason;

            bool wantsSuffix = ov.Writes == FieldWrites.Suffix || ov.Writes == FieldWrites.Both;
            bool wantsValue = ov.Writes == FieldWrites.Value || ov.Writes == FieldWrites.Both;

            if (wantsSuffix)
            {
                if (cap != null && cap.SuffixSupported == false)
                    return FieldDegradeReason.SuffixNotSupported;
                if (cap != null && cap.SuffixSupported == null)
                    WarnOnce("cap-suffix-null:" + paramId,
                        "field:" + paramId + " suffix.supported untested — not gated");

                string text = AuthoredText(ov.Content) ?? "";
                // Width overflow = runtime clamp, not inert (E5-002).
                if (cap?.SuffixWidth is int width && width >= 0 && text.Length > width)
                    softClamp = true;
                if (cap != null && cap.SuffixWidth == null && cap.SuffixSupported == true)
                    WarnOnce("cap-suffix-width-null:" + paramId,
                        "field:" + paramId + " suffix.width untested — not gated");
            }

            if (wantsValue)
            {
                bool isText = IsAsciiTextContent(ov.Content);
                if (isText)
                {
                    if (cap != null && cap.ValueAscii == false)
                        return FieldDegradeReason.TextInNumericValue;
                    if (cap != null && cap.ValueAscii == null)
                        WarnOnce("cap-ascii-null:" + paramId,
                            "field:" + paramId + " value.ascii untested — not gated");
                }
            }

            return FieldDegradeReason.None;
        }

        /// <summary>
        /// Field-plane content roster: {text, message, property-with-source}.
        /// TODO(kelchm): add this roster row to display-model-v2 §14 capability matrix.
        /// </summary>
        private static FieldDegradeReason FieldContentGate(FieldOverride ov)
        {
            var content = ov?.Content;
            if (content == null)
                return FieldDegradeReason.None;

            switch (content.Kind)
            {
                case ContentKind.Text:
                case ContentKind.Message:
                    return FieldDegradeReason.None;
                case ContentKind.Property:
                    if (content.Source == null
                        || (string.IsNullOrEmpty(content.Source.Name)
                            && content.Source.Kind == ValueSourceKind.Unknown
                            && string.IsNullOrEmpty(content.Source.KindRaw)))
                        return FieldDegradeReason.UnrenderableContent;
                    return FieldDegradeReason.None;
                case ContentKind.Unknown:
                    // Empty / unparsed content: treat as no-op content when no kind.
                    return FieldDegradeReason.None;
                default:
                    // Dynamic kinds (speed/gear/fuel/rpm/position) — not on field plane.
                    return FieldDegradeReason.UnrenderableContent;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Content rendering (segment face) — REUSES LegacyValueFormatter
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Format content the same way <c>DisplayRuleStack.FormatScreen</c> does, using
        /// <see cref="LegacyValueFormatter"/> for the integer+D3 path (single-evaluator
        /// mandate). Format absent = exactly the v9 integer render.
        /// <para>
        /// <c>content.format</c> = <see cref="SegmentFormatOneDecimal"/> is consumed for
        /// numeric segment kinds (speed, rpm, position, fuel, property) — one decimal place,
        /// no leading-zero pad, rendered via <see cref="SevenSegment.EncodeWithDots"/> on
        /// the effect path. Unknown format spellings on dynamic kinds still warn-once and
        /// mark degraded-visible (raw preserved; integer path).
        /// </para>
        /// </summary>
        public static string FormatContent(ContentObject content, SegmentContentContext ctx)
            => FormatContent(content, ctx, out _, warnPageId: null, warn: null, warned: null);

        /// <summary>Provisional spelling for the ruled segment-decimal format (E5-06 (a)).</summary>
        public const string SegmentFormatOneDecimal = "oneDecimal";

        private string FormatContent(
            ContentObject content,
            SegmentContentContext ctx,
            out bool formatDegraded,
            string pageId)
            => FormatContent(
                content, ctx, out formatDegraded, pageId, _warn, _warnedKeys);

        private static string FormatContent(
            ContentObject content,
            SegmentContentContext ctx,
            out bool formatDegraded,
            string warnPageId,
            Action<string> warn,
            HashSet<string> warned)
        {
            formatDegraded = false;
            if (content == null)
                return null;
            ctx = ctx ?? new SegmentContentContext();
            bool inGame = ctx.InGame;
            bool oneDecimal = IsOneDecimalFormat(content.Format);

            // Unknown formats on dynamic kinds: warn-once + degraded-visible (integer path).
            // oneDecimal is consumed for numeric kinds and does not degrade.
            if (!string.IsNullOrEmpty(content.Format)
                && IsDynamicSegmentKind(content.Kind)
                && !(oneDecimal && IsNumericSegmentKind(content.Kind)))
            {
                formatDegraded = true;
                if (warn != null && warned != null)
                {
                    string key = "seg-format-ignored:" + (warnPageId ?? "") + ":" + content.Kind;
                    if (warned.Add(key))
                    {
                        warn("segment content.format '" + content.Format
                            + "' on kind " + content.Kind
                            + " is not consumed by the segment plane (D3 integer path)"
                            + " — degraded-visible");
                    }
                }
            }

            switch (content.Kind)
            {
                case ContentKind.Text:
                case ContentKind.Message:
                    return LegacyValueFormatter.FormatText(
                        content.EffectiveText ?? content.Text);

                case ContentKind.Speed:
                    if (!inGame || !ctx.SpeedLocal.HasValue)
                        return null;
                    if (oneDecimal)
                    {
                        string od = TryFormatOneDecimal(ctx.SpeedLocal.Value);
                        if (od != null)
                            return od;
                    }
                    return LegacyValueFormatter.FormatSpeed(ctx.SpeedLocal.Value);

                case ContentKind.Gear:
                    if (!inGame)
                        return null;
                    return LegacyValueFormatter.FormatGear(ctx.Gear);

                case ContentKind.GearBrackets:
                    if (!inGame)
                        return null;
                    return LegacyValueFormatter.FormatGearBrackets(ctx.Gear);

                case ContentKind.Rpm:
                    if (!inGame || !ctx.Rpms.HasValue)
                        return null;
                    if (oneDecimal)
                    {
                        // Same /10 scale as LegacyValueFormatter.FormatRpm.
                        string od = TryFormatOneDecimal(ctx.Rpms.Value / 10.0);
                        if (od != null)
                            return od;
                    }
                    return LegacyValueFormatter.FormatRpm(ctx.Rpms.Value);

                case ContentKind.Position:
                    if (!inGame || !ctx.Position.HasValue)
                        return null;
                    if (oneDecimal)
                    {
                        string od = TryFormatOneDecimal(ctx.Position.Value);
                        if (od != null)
                            return od;
                    }
                    return LegacyValueFormatter.FormatPosition(ctx.Position.Value);

                case ContentKind.Fuel:
                    if (!inGame || !ctx.Fuel.HasValue)
                        return null;
                    if (oneDecimal)
                    {
                        string od = TryFormatOneDecimal(ctx.Fuel.Value);
                        if (od != null)
                            return od;
                    }
                    return LegacyValueFormatter.FormatFuel(ctx.Fuel.Value);

                case ContentKind.Property:
                    if (oneDecimal)
                    {
                        var spec = ToPropertySpec(content.Source);
                        if (ctx.Properties != null && spec != null
                            && ctx.Properties.TryGetNumber(spec, out double propValue))
                        {
                            string od = TryFormatOneDecimal(propValue);
                            if (od != null)
                                return od;
                        }
                        // Missing property or overflow → same outcomes as integer path.
                    }
                    return LegacyValueFormatter.FormatProperty(
                        ctx.Properties, ToPropertySpec(content.Source));

                case ContentKind.Unknown:
                default:
                    return null;
            }
        }

        private static bool IsOneDecimalFormat(string format)
            => string.Equals(format, SegmentFormatOneDecimal, StringComparison.Ordinal);

        /// <summary>
        /// Formats <paramref name="value"/> with exactly one decimal place and no leading-zero
        /// pad (e.g. 4.2 → "4.2", 12.7 → "12.7"). Dot folds onto its digit via
        /// <see cref="SevenSegment.EncodeWithDots"/> on the render path.
        /// <para>
        /// Fallback rule (pinned): when the one-decimal form needs more than 3 segment
        /// positions after EncodeWithDots fold (magnitude ≥ 100 after round-to-1-decimal
        /// yields "XXX.Y" = 4 positions), return null so the caller uses the v9 integer+D3
        /// path. Negatives clamp to 0 (same lower bound as LegacyValueFormatter).
        /// </para>
        /// </summary>
        private static string TryFormatOneDecimal(double value)
        {
            if (value < 0)
                value = 0;
            double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            string text = rounded.ToString("0.0", CultureInfo.InvariantCulture);
            // Fit gate: EncodeWithDots position count must be ≤ 3 (hardware width).
            if (SevenSegment.EncodeWithDots(text).Count > 3)
                return null;
            return text;
        }

        private static bool IsNumericSegmentKind(ContentKind kind)
        {
            switch (kind)
            {
                case ContentKind.Speed:
                case ContentKind.Rpm:
                case ContentKind.Position:
                case ContentKind.Fuel:
                case ContentKind.Property:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsDynamicSegmentKind(ContentKind kind)
        {
            switch (kind)
            {
                case ContentKind.Speed:
                case ContentKind.Gear:
                case ContentKind.GearBrackets:
                case ContentKind.Rpm:
                case ContentKind.Position:
                case ContentKind.Fuel:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Apply effect via <see cref="LegacyEffectClock"/> at <paramref name="nowMs"/>.
        /// Exposed for parity fixtures that drive the v9 path side-by-side.
        /// </summary>
        public static byte[] ApplyEffect(string renderedText, ContentEffect effect, long nowMs)
            => LegacyEffectClock.Apply(renderedText, ToLegacyEffect(effect), nowMs);

        // ═════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════

        private enum PresenceKind { On, Off, Unknown }

        private PresenceKind ClassifyPagePresence(
            string pageDestinationId, string displayedDestinationId)
        {
            if (IsUnknownDisplayed(displayedDestinationId))
            {
                WarnOnce(
                    "displayed-unknown",
                    "DisplayedDestinationId is null/unknown/rest — presence left null"
                    + " (not OffScreen)");
                return PresenceKind.Unknown;
            }
            if (string.IsNullOrEmpty(pageDestinationId))
                return PresenceKind.Unknown;
            return string.Equals(
                pageDestinationId, displayedDestinationId, StringComparison.Ordinal)
                ? PresenceKind.On
                : PresenceKind.Off;
        }

        private PresenceKind ClassifyFieldPresence(
            ushort paramId, FieldCapability cap, string displayedDestinationId)
        {
            if (IsUnknownDisplayed(displayedDestinationId))
            {
                WarnOnce(
                    "displayed-unknown",
                    "DisplayedDestinationId is null/unknown/rest — presence left null"
                    + " (not OffScreen)");
                return PresenceKind.Unknown;
            }

            bool hasHosts = cap?.HostCatalogPageIds != null && cap.HostCatalogPageIds.Count > 0;
            bool hasPrimary = !string.IsNullOrEmpty(cap?.PrimaryHostCatalogPageId)
                || (_primaryHostByParam != null
                    && _primaryHostByParam.TryGetValue(paramId, out var ph)
                    && !string.IsNullOrEmpty(ph));

            // Present in catalog but hosts unknown → Presence null, never false OffScreen.
            if (!hasHosts && !hasPrimary)
            {
                WarnOnce(
                    "hosts-unknown:" + paramId,
                    "field:" + paramId
                    + " hosts unknown — presence left null (not OffScreen)");
                return PresenceKind.Unknown;
            }

            if (cap?.HostCatalogPageIds != null)
            {
                foreach (var host in cap.HostCatalogPageIds)
                {
                    if (string.Equals(
                            displayedDestinationId, DestinationIds.Itm(host),
                            StringComparison.Ordinal))
                        return PresenceKind.On;
                }
            }

            string primary = null;
            if (_primaryHostByParam != null
                && _primaryHostByParam.TryGetValue(paramId, out var mapped)
                && !string.IsNullOrEmpty(mapped))
                primary = mapped;
            else if (!string.IsNullOrEmpty(cap?.PrimaryHostCatalogPageId))
                primary = cap.PrimaryHostCatalogPageId;

            if (!string.IsNullOrEmpty(primary)
                && string.Equals(
                    displayedDestinationId, DestinationIds.Itm(primary),
                    StringComparison.Ordinal))
                return PresenceKind.On;

            return PresenceKind.Off;
        }

        private static bool IsUnknownDisplayed(string displayedDestinationId)
        {
            if (string.IsNullOrEmpty(displayedDestinationId))
                return true;
            if (DestinationIds.IsRest(displayedDestinationId))
                return true;
            // Known planes: itm: / hosted: / cycle: / manual:
            if (displayedDestinationId.StartsWith("itm:", StringComparison.Ordinal)
                || displayedDestinationId.StartsWith("hosted:", StringComparison.Ordinal)
                || displayedDestinationId.StartsWith("cycle:", StringComparison.Ordinal)
                || displayedDestinationId.StartsWith("manual:", StringComparison.Ordinal))
                return false;
            return true;
        }

        private string FieldDestination(ushort paramId)
        {
            // DestinationId owned by the shared primary-host map (same as E4).
            if (_primaryHostByParam != null
                && _primaryHostByParam.TryGetValue(paramId, out var host)
                && !string.IsNullOrEmpty(host))
                return DestinationIds.Itm(host);
            return null;
        }

        private static Dictionary<string, CarrierTickSnapshot> IndexSnapshots(
            IReadOnlyList<CarrierTickSnapshot> snapshots)
        {
            var map = new Dictionary<string, CarrierTickSnapshot>(StringComparer.Ordinal);
            if (snapshots == null)
                return map;
            foreach (var s in snapshots)
            {
                if (s.CarrierId == null)
                    continue;
                map[s.CarrierId] = s;
            }
            return map;
        }

        private static HashSet<string> IndexDismissed(IReadOnlyCollection<string> dismissed)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (dismissed == null)
                return set;
            foreach (var id in dismissed)
            {
                if (id != null)
                    set.Add(id);
            }
            return set;
        }

        private static bool IsActive(
            Dictionary<string, CarrierTickSnapshot> snaps, string id)
            => snaps.TryGetValue(id, out var s) && s.Active;

        private static byte[] BlankFrame()
            => new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank };

        private static string AuthoredText(ContentObject content)
        {
            if (content == null)
                return null;
            return content.EffectiveText ?? content.Text;
        }

        private static bool IsAsciiTextContent(ContentObject content)
        {
            if (content == null)
                return false;
            return content.Kind == ContentKind.Text || content.Kind == ContentKind.Message;
        }

        private static string AlignSuffix(string text, FieldAlignment alignment, int? width)
        {
            if (text == null)
                return null;
            if (width == null || width.Value <= 0 || text.Length >= width.Value)
                return text;

            int pad = width.Value - text.Length;
            if (alignment == FieldAlignment.Right)
                return new string(' ', pad) + text;
            return text + new string(' ', pad);
        }

        private static string WidthBlank(int? width, string text)
        {
            if (width is int w && w > 0)
                return new string(' ', w);
            if (text != null)
                return new string(' ', text.Length);
            return " ";
        }

        private static bool EffectIsVisible(ContentEffect effect, long nowMs)
        {
            switch (effect)
            {
                case ContentEffect.Blink:
                case ContentEffect.Flash:
                    return LegacyEffectClock.IsOnPhase(nowMs);
                default:
                    return true;
            }
        }

        private static LegacyEffect ToLegacyEffect(ContentEffect effect)
        {
            switch (effect)
            {
                case ContentEffect.Scroll: return LegacyEffect.Scroll;
                case ContentEffect.Blink: return LegacyEffect.Blink;
                case ContentEffect.Flash: return LegacyEffect.Flash;
                case ContentEffect.None: return LegacyEffect.None;
                default: return LegacyEffect.Unknown;
            }
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

        private void WarnOnce(string key, string message)
        {
            if (_warn == null || key == null)
                return;
            if (!_warnedKeys.Add(key))
                return;
            _warn(message);
        }
    }
}
