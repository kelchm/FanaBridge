using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Composition
{
    /// <summary>
    /// Per-tick inputs for <see cref="DisplayCompositionV2"/>. Core is SimHub-free, so
    /// the runtime (later round) maps PluginManager/GameData onto this shape; tests
    /// supply it directly. No <c>BeginFrame</c> — property framing is runtime-owned
    /// (e8-seam-adjudication design review correction #3).
    /// </summary>
    public sealed class DisplayCompositionV2TickInput
    {
        /// <summary>Session state: in-game vs idle floor (v9 parity).</summary>
        public bool InGame { get; set; } = true;

        /// <summary>
        /// Game-identity edge (ACC → iRacing). Seat arbiter resets the manual row.
        /// </summary>
        public bool GameChanged { get; set; }

        /// <summary>Caller-injected game identity; empty/null = unspecified.</summary>
        public string GameId { get; set; }

        /// <summary>
        /// Segment content sources (speed/gear/… + optional property reader). When
        /// <see cref="SegmentContentContext.Properties"/> is null, composition uses the
        /// ctor-injected reader.
        /// </summary>
        public SegmentContentContext Content { get; set; } = new SegmentContentContext();
    }

    /// <summary>
    /// Optional construction extras that do not change the §3.2 dependency set.
    /// Catalog-derived envelopes default from the catalog; encoder probe is optional
    /// (null = treat every condition param as encodable — ConditionParamPlanner law).
    /// </summary>
    public sealed class DisplayCompositionV2Options
    {
        /// <summary>Device key stamped on composed-resolution slices.</summary>
        public string DeviceKey { get; set; } = "";

        /// <summary>
        /// Optional encoder availability probe for <see cref="Arbitration.ConditionParamPlanner"/>.
        /// Null = all params encodable.
        /// </summary>
        public Func<ushort, bool> HasEncoder { get; set; }

        /// <summary>
        /// Default wire page used when resolving <see cref="DisplayCompositionV2.BaseWirePage"/>
        /// (device settings default). 0 = table fallback only.
        /// </summary>
        public byte DefaultWirePage { get; set; }
    }
}
