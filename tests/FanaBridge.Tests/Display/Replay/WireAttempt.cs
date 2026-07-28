using System;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>Transport channel for a recorded write attempt (seam-map §4.3).</summary>
    internal enum Chan
    {
        Col01 = 0,
        Col03 = 1,
    }

    /// <summary>
    /// One attempted <see cref="FanaBridge.Transport.IDeviceTransport"/> write,
    /// recorded at SendCol01/SendCol03 entry (including declined sends).
    /// Equality for the diff excludes <see cref="TickMs"/> (diagnostic only).
    /// </summary>
    internal readonly struct WireAttempt : IEquatable<WireAttempt>
    {
        public WireAttempt(
            int frameIndex,
            int seqInFrame,
            long tickMs,
            Chan channel,
            byte[] payload,
            bool accepted)
        {
            FrameIndex = frameIndex;
            SeqInFrame = seqInFrame;
            TickMs = tickMs;
            Channel = channel;
            Payload = payload ?? Array.Empty<byte>();
            Accepted = accepted;
        }

        public int FrameIndex { get; }
        public int SeqInFrame { get; }
        public long TickMs { get; }
        public Chan Channel { get; }
        public byte[] Payload { get; }
        public bool Accepted { get; }

        public bool Equals(WireAttempt other)
            => FrameIndex == other.FrameIndex
            && SeqInFrame == other.SeqInFrame
            && Channel == other.Channel
            && Accepted == other.Accepted
            && PayloadBytesEqual(Payload, other.Payload);

        public override bool Equals(object? obj)
            => obj is WireAttempt other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = FrameIndex;
                h = (h * 397) ^ SeqInFrame;
                h = (h * 397) ^ (int)Channel;
                h = (h * 397) ^ (Accepted ? 1 : 0);
                h = (h * 397) ^ Payload.Length;
                for (int i = 0; i < Payload.Length && i < 8; i++)
                    h = (h * 397) ^ Payload[i];
                return h;
            }
        }

        public string ToHex()
            => BitConverter.ToString(Payload);

        public static bool PayloadBytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A declared legal divergence between v9 and v2 streams (seam-map §4.6).
    /// Null expected arrays mean "engine emitted nothing at this index".
    /// </summary>
    internal sealed class KnownDiff
    {
        public KnownDiff(
            string name,
            int frameIndex,
            Chan channel,
            byte[]? expectedV9,
            byte[]? expectedV2)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            FrameIndex = frameIndex;
            Channel = channel;
            ExpectedV9 = expectedV9;
            ExpectedV2 = expectedV2;
        }

        public string Name { get; }
        public int FrameIndex { get; }
        public Chan Channel { get; }
        public byte[]? ExpectedV9 { get; }
        public byte[]? ExpectedV2 { get; }
    }

    /// <summary>
    /// Named ruled diffs with expected wire bytes (FR-4). FrameIndex is the script frame
    /// of the first diverging write (matched ±2 after PageSet-retry collapse).
    /// </summary>
    internal static class ReplayKnownDiffs
    {
        // Gear "4" from frozen displayMode bake (LegacyModeMigration).
        // 01 F8 09 01 02 00 66 00 — verified against live streams after FR-1.
        public static readonly byte[] GearFourCol01 =
            { 0x01, 0xF8, 0x09, 0x01, 0x02, 0x00, 0x66, 0x00 };

        // Scripted dynamic gear faces (v9 mode-bake residual after baseline Gear-4 pin).
        // Gear "3" / "5" / "2" — Blank, DigitN, Blank (codex rows 1–4).
        public static readonly byte[] GearThreeCol01 =
            { 0x01, 0xF8, 0x09, 0x01, 0x02, 0x00, 0x4F, 0x00 };

        public static readonly byte[] GearFiveCol01 =
            { 0x01, 0xF8, 0x09, 0x01, 0x02, 0x00, 0x6D, 0x00 };

        public static readonly byte[] GearTwoCol01 =
            { 0x01, 0xF8, 0x09, 0x01, 0x02, 0x00, 0x5B, 0x00 };

        /// <summary>
        /// v9-only dynamic gear Col01 faces after the Gear-4/Speed-142 base pin.
        /// Optional per cell (script axes only emit the faces the script steps through).
        /// </summary>
        public static readonly byte[][] ModeBakeDynamicGearFacesV9Only =
        {
            GearThreeCol01,
            GearFiveCol01,
            GearTwoCol01,
        };

        // Speed "142" from first-hosted-page landing (spd).
        // 01 F8 09 01 02 06 66 5B — verified against live streams after FR-1.
        public static readonly byte[] Speed142Col01 =
            { 0x01, 0xF8, 0x09, 0x01, 0x02, 0x06, 0x66, 0x5B };

        /// <summary>
        /// v1 bakes pre-epic displayMode into a segment world; v2 never does (§9b).
        /// FrameIndex -1 = match at whichever frame the byte pair first diverges
        /// (script axes shift the index). Handler also pins scripted Gear 3/5/2 faces.
        /// </summary>
        public static readonly KnownDiff LegacyModeBakeIsV1Only = new KnownDiff(
            "legacy-mode-bake-is-v1-only",
            frameIndex: -1,
            Chan.Col01,
            GearFourCol01,
            Speed142Col01);

        /// <summary>
        /// PBME idle: v2 IdleCompile → firmware Blank special (subcommand 0x50) on col01.
        /// One-sided (v9 has no twin face at this index). Class C also pins one-sided
        /// v9 SetPage(device=3, page=2) including the unconfirmed retry (handler).
        /// </summary>
        public static readonly KnownDiff BlankCompileFirmwareIdle = new KnownDiff(
            "blank-compile-firmware-idle",
            frameIndex: -1,
            Chan.Col01,
            expectedV9: null,
            expectedV2: new byte[] { 0x01, 0xF8, 0x09, 0x01, 0x50, 0x00, 0x00, 0x00 });

        /// <summary>
        /// suffix-blink-v2-only kept cell: first value-update payload differs (blink plan).
        /// Bytes from live capture (param-value slot differs at offset 7).
        /// </summary>
        public static readonly KnownDiff SuffixBlinkV2Only = new KnownDiff(
            "suffix-blink-v2-only",
            frameIndex: -1,
            Chan.Col03,
            expectedV9: null, // matched via class handler on first value-update pair
            expectedV2: null);

        /// <summary>
        /// Full 64-byte ITM SetPage to Legacy (wire 6, device 3 = PBME).
        /// </summary>
        public static readonly byte[] SetPageLegacyPbme = MakeSetPage(deviceId: 3, page: 6);

        /// <summary>
        /// Full 64-byte ITM SetPage to Legacy (wire 5, device 4 = Bentley).
        /// </summary>
        public static readonly byte[] SetPageLegacyBentley = MakeSetPage(deviceId: 4, page: 5);

        /// <summary>
        /// Idle Class C residual: v9 device-3 / page-2 policy SetPage (not the
        /// dev-2/page-11 pseudo-keepalive). Includes f15 unconfirmed retry after normalize
        /// (gap &gt; MaxRetryFrameGap so both attempts remain).
        /// </summary>
        public static readonly byte[] SetPagePolicyPage2Pbme = MakeSetPage(deviceId: 3, page: 2);

        private static byte[] MakeSetPage(byte deviceId, byte page)
        {
            var b = new byte[64];
            b[0] = 0xFF;
            b[1] = 0x05;
            b[2] = 0x04;
            b[3] = deviceId;
            b[4] = page;
            return b;
        }

        private static byte[] MakePadded(params byte[] prefix)
        {
            var b = new byte[64];
            Buffer.BlockCopy(prefix, 0, b, 0, prefix.Length);
            return b;
        }

        // lapInfo rest-window paints (codex/opus itm-rest-page-is-v1-only). Zero-padded 64.
        public static readonly byte[] ValueUpdateLapInfoPbme = MakePadded(
            0xFF, 0x05, 0x01, 0x03, 0x00, 0x01, 0x00, 0x02, 0x8E, 0x00, 0x03, 0x01,
            0x04, 0x00, 0x01, 0x04, 0x03, 0x02, 0xF9, 0x01, 0x01, 0x03, 0x03, 0x03,
            0xF5, 0x01, 0x01, 0x02, 0x03, 0x04, 0xFD, 0x01, 0x04, 0x00, 0x00, 0x00,
            0x00, 0x03, 0x05, 0xFE, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00);

        public static readonly byte[] ParamDefsLapInfoPbme = MakePadded(
            0xFF, 0x05, 0x03, 0x03, 0x82, 0x00, 0x00, 0x03, 0x2F, 0x31, 0x32, 0x03,
            0x83, 0x00, 0x00, 0x03, 0x2F, 0x31, 0x36);

        public static readonly byte[] ValueUpdateLapInfoBentley = MakePadded(
            0xFF, 0x05, 0x01, 0x04, 0x00, 0x01, 0x00, 0x02, 0x8E, 0x00, 0x04, 0x01,
            0x04, 0x00, 0x01, 0x04, 0x04, 0x02, 0xF9, 0x01, 0x01, 0x03, 0x04, 0x03,
            0xF5, 0x01, 0x01, 0x02, 0x04, 0x04, 0xFD, 0x01, 0x04, 0x00, 0x00, 0x00,
            0x00, 0x04, 0x05, 0xFE, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00);

        public static readonly byte[] ParamDefsLapInfoBentley = MakePadded(
            0xFF, 0x05, 0x03, 0x04, 0x82, 0x00, 0x00, 0x03, 0x2F, 0x31, 0x32, 0x04,
            0x83, 0x00, 0x00, 0x03, 0x2F, 0x31, 0x36);

        /// <summary>
        /// v1 rests two planes (itm.basePage + segmentDisplay.baseScreenId); v2 has one
        /// rest floor. Hosted rest → no ITM rest page: v9 paints lapInfo (ValueUpdate +
        /// ParamDefs) until the segment-screen summon crosses to Legacy; v2 never does.
        /// Handler substitutes SetPage (both sides ≥1) + VAL/DEF (v9 ≥1 only).
        /// Supersedes segment-screen-setpage-ordering.
        /// </summary>
        public static readonly KnownDiff ItmRestPageIsV1OnlyPbme = new KnownDiff(
            "itm-rest-page-is-v1-only",
            frameIndex: -1,
            Chan.Col03,
            expectedV9: SetPageLegacyPbme,
            expectedV2: SetPageLegacyPbme);

        /// <summary>Bentley device-id variant of <see cref="ItmRestPageIsV1OnlyPbme"/>.</summary>
        public static readonly KnownDiff ItmRestPageIsV1OnlyBentley = new KnownDiff(
            "itm-rest-page-is-v1-only",
            frameIndex: -1,
            Chan.Col03,
            expectedV9: SetPageLegacyBentley,
            expectedV2: SetPageLegacyBentley);

        /// <summary>
        /// Legacy name retained for unit tests that still pin SetPage-only substitution.
        /// Segment-screen matrix cells now attach <see cref="ItmRestPageIsV1OnlyPbme"/>.
        /// </summary>
        public static readonly KnownDiff SegmentScreenSetPageOrdering = new KnownDiff(
            "segment-screen-setpage-ordering",
            frameIndex: -1,
            Chan.Col03,
            expectedV9: SetPageLegacyPbme,
            expectedV2: SetPageLegacyPbme);

        /// <summary>Bentley variant of segment-screen SetPage pin (unit-test residual).</summary>
        public static readonly KnownDiff SegmentScreenSetPageOrderingBentley = new KnownDiff(
            "segment-screen-setpage-ordering",
            frameIndex: -1,
            Chan.Col03,
            expectedV9: SetPageLegacyBentley,
            expectedV2: SetPageLegacyBentley);
    }
}
