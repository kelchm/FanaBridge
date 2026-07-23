using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// The production <see cref="IPropertyReader"/>: the typed built-in fast path (in
    /// lockstep with <see cref="BuiltInProperties"/> — the exhaustive test), the
    /// memoized named-property path (via the injected raw-lookup seam —
    /// <c>PluginManager.GetPropertyValue</c> is non-virtual host code), the coercion
    /// table, and exception containment.
    /// </summary>
    public class SimHubPropertySourceTests
    {
        // ── GameData (see ItmTelemetryTests) ─────────────────────────────
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);
        private static void Set(object s, string p, object? v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        private static readonly Type OpponentType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.Opponent");

        private static IList OpponentList(params double?[] relGaps)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(OpponentType))!;
            var setter = OpponentType.GetProperty("RelativeGapToPlayer")!.GetSetMethod(true)!;
            foreach (var g in relGaps)
            {
                var opp = FormatterServices.GetUninitializedObject(OpponentType);
                setter.Invoke(opp, new object?[] { g });
                list.Add(opp);
            }
            return list;
        }

        // A status frame with every built-in's backing field populated (non-default
        // where it helps catch wrong-field mixups).
        private static object FullStatus()
        {
            var s = NewStatus();
            Set(s, "SpeedLocal", 123.0);
            Set(s, "Gear", "3");
            Set(s, "CurrentLap", 5);
            Set(s, "TotalLaps", 30);
            Set(s, "Position", 7);
            Set(s, "OpponentsCount", 20);
            Set(s, "CurrentLapTime", TimeSpan.FromSeconds(83.2));
            Set(s, "LastLapTime", TimeSpan.FromSeconds(90.5));
            Set(s, "BestLapTime", TimeSpan.FromSeconds(81.9));
            Set(s, "Fuel", 45.5);
            Set(s, "MaxFuel", 100.0);
            Set(s, "FuelPercent", 45.5);
            Set(s, "ERSPercent", 62.0);
            Set(s, "DRSAvailable", 1);
            Set(s, "DRSEnabled", 1);
            Set(s, "DeltaToSessionBest", (double?)0.25);
            Set(s, "TCLevel", 3);
            Set(s, "ABSLevel", 2);
            Set(s, "EngineMap", 4);
            Set(s, "OilTemperature", 95.0);
            Set(s, "BrakeBias", 54.0);
            Set(s, "OpponentsAheadOnTrack", OpponentList(2.5, 0.8));
            Set(s, "OpponentsBehindOnTrack", OpponentList(-1.2));
            Set(s, "IsInPitLane", 1);
            Set(s, "PitLimiterOn", 1);
            Set(s, "TyreTemperatureFrontLeft", 80.0);
            Set(s, "TyreTemperatureFrontRight", 81.0);
            Set(s, "TyreTemperatureRearLeft", 82.0);
            Set(s, "TyreTemperatureRearRight", 83.0);
            return s;
        }

        private static PropertySpec BuiltIn(string name)
            => new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name };

        private static PropertySpec Named(string name)
            => new PropertySpec { Kind = PropertyKind.SimHubProperty, Name = name };

        // ── Built-ins ────────────────────────────────────────────────────

        [Fact]
        public void EveryBuiltInConstant_Resolves()
        {
            // The lockstep guard: BuiltInProperties (Core) and the adapter's resolver
            // table must cover exactly the same names — a constant added without an
            // adapter read fails here.
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(FullStatus()));

            foreach (var name in BuiltInProperties.All)
            {
                Assert.True(source.TryGetNumber(BuiltIn(name), out _),
                    "built-in '" + name + "' did not resolve");
                Assert.True(source.TryGetBool(BuiltIn(name), out _),
                    "built-in '" + name + "' did not resolve as bool");
            }
        }

        [Fact]
        public void BuiltIns_ReadTheExpectedFields()
        {
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(FullStatus()));

            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.Speed), out double speed));
            Assert.Equal(123.0, speed);
            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.CurrentLapTime), out double lapTime));
            Assert.Equal(83.2, lapTime, 3);
            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.BrakeBias), out double bias));
            Assert.Equal(54.0, bias);
            Assert.True(source.TryGetBool(BuiltIn(BuiltInProperties.PitLimiterOn), out bool limiter));
            Assert.True(limiter);
        }

        // Spec P10a: RedlineReached mirrors driver guard Rpms > 0 && redline flag.
        [Theory]
        [InlineData(0.0, 1.0, false)]   // engine off — no brackets even if flag set
        [InlineData(5000.0, 0.0, false)] // spinning under redline
        [InlineData(8000.0, 1.0, true)]  // redline reached with engine on
        public void RedlineReached_MatchesDriverGuard(double rpms, double redLine, bool expected)
        {
            Assert.Contains(BuiltInProperties.RedlineReached, BuiltInProperties.All);
            Assert.True(BuiltInProperties.IsKnown(BuiltInProperties.RedlineReached));

            var s = NewStatus();
            Set(s, "Rpms", rpms);
            Set(s, "CarSettings_RPMRedLineReached", redLine);
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(s));

            Assert.True(source.TryGetBool(BuiltIn(BuiltInProperties.RedlineReached), out bool value));
            Assert.Equal(expected, value);
            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.RedlineReached), out double n));
            Assert.Equal(expected ? 1.0 : 0.0, n);
        }

        // P10a review round: blank/unparseable gear folds to neutral — the same fold
        // as LegacyDisplayDriver.ParseGear, so valid→blank is a gear EDGE on both
        // paths (the migrated gear-change overlay depends on it). R stays distinct.
        [Theory]
        [InlineData("", 0.0)]
        [InlineData("   ", 0.0)]
        [InlineData("???", 0.0)]
        [InlineData("N", 0.0)]
        [InlineData("R", -1.0)]
        [InlineData("4", 4.0)]
        public void Gear_BlankAndUnparseableFoldToNeutral(string gear, double expected)
        {
            var s = NewStatus();
            Set(s, "Gear", gear);
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(s));

            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.Gear), out double value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void BuiltIn_NamesMatchCaseInsensitively()
        {
            // The validator accepts any casing (IsKnown is OrdinalIgnoreCase) — the
            // adapter must resolve the same names it lets through.
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(FullStatus()));
            Assert.True(source.TryGetNumber(BuiltIn("fuel"), out double fuel));
            Assert.Equal(45.5, fuel);
        }

        [Fact]
        public void BuiltIn_NullNewData_FailsTheRead()
        {
            var source = new SimHubPropertySource();
            source.BeginFrame(null, new GameData { NewData = null });
            Assert.False(source.TryGetNumber(BuiltIn(BuiltInProperties.Speed), out _));
            Assert.False(source.TryGetBool(BuiltIn(BuiltInProperties.DrsEnabled), out _));
        }

        [Theory]
        [InlineData("3", 3.0)]
        [InlineData("N", 0.0)]
        [InlineData("R", -1.0)]
        public void Gear_ParsesToANumber(string gear, double expected)
        {
            var s = FullStatus();
            Set(s, "Gear", gear);
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(s));
            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.Gear), out double value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void Gear_Unparsable_FoldsToNeutral()
        {
            // Re-anchored in the P10a review round (was: fails the read). Blank and
            // unparseable gear now fold to neutral, matching LegacyDisplayDriver.ParseGear
            // — cross-path parity for the migrated gear-change overlay requires the same
            // edges on both paths (see Gear_BlankAndUnparseableFoldToNeutral).
            var s = FullStatus();
            Set(s, "Gear", "?");
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(s));
            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.Gear), out double value));
            Assert.Equal(0.0, value);
        }

        [Fact]
        public void DeltaToSessionBest_Null_FailsTheRead()
        {
            // No session best yet: the rule stays armed rather than comparing against 0.
            var s = FullStatus();
            Set(s, "DeltaToSessionBest", (double?)null);
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(s));
            Assert.False(source.TryGetNumber(BuiltIn(BuiltInProperties.DeltaToSessionBest), out _));
        }

        [Fact]
        public void Gaps_UseTheNearestOpponent_LikeTheItmFields()
        {
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(FullStatus()));

            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.GapAhead), out double ahead));
            Assert.Equal(0.8, ahead, 3);   // nearest of 2.5 / 0.8
            Assert.True(source.TryGetNumber(BuiltIn(BuiltInProperties.GapBehind), out double behind));
            Assert.Equal(1.2, behind, 3);  // |−1.2|
        }

        // ── Named properties (seam-injected lookup) ──────────────────────

        [Fact]
        public void NamedProperty_MemoizedPerFrame()
        {
            int lookups = 0;
            var source = new SimHubPropertySource(rawLookup: name => { lookups++; return 42.0; });
            source.BeginFrame(null, Data(FullStatus()));

            Assert.True(source.TryGetNumber(Named("Some.Prop"), out double v1));
            Assert.True(source.TryGetNumber(Named("Some.Prop"), out double v2));
            Assert.True(source.TryGetBool(Named("Some.Prop"), out _));
            Assert.Equal(42.0, v1);
            Assert.Equal(42.0, v2);
            Assert.Equal(1, lookups);   // one lookup serves the whole frame

            source.BeginFrame(null, Data(FullStatus()));
            Assert.True(source.TryGetNumber(Named("Some.Prop"), out _));
            Assert.Equal(2, lookups);   // a new frame reads fresh
        }

        [Fact]
        public void NamedProperty_NullResult_MemoizedToo()
        {
            int lookups = 0;
            var source = new SimHubPropertySource(rawLookup: _ => { lookups++; return null; });
            source.BeginFrame(null, Data(FullStatus()));

            Assert.False(source.TryGetNumber(Named("Missing"), out _));
            Assert.False(source.TryGetNumber(Named("Missing"), out _));
            Assert.Equal(1, lookups);
        }

        [Fact]
        public void NamedProperty_WithoutPluginManagerOrSeam_Fails()
        {
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(FullStatus()));   // null pm, no seam
            Assert.False(source.TryGetNumber(Named("Anything"), out _));
            Assert.False(source.TryGetBool(Named("Anything"), out _));
        }

        [Theory]
        [InlineData(12.5, 12.5)]
        [InlineData(7, 7.0)]
        [InlineData(7L, 7.0)]
        [InlineData(2.5f, 2.5)]
        [InlineData((byte)9, 9.0)]
        [InlineData(true, 1.0)]
        [InlineData(false, 0.0)]
        [InlineData("12.5", 12.5)]
        public void Coercion_NumericBoolAndString_ToDouble(object raw, double expected)
        {
            var source = new SimHubPropertySource(rawLookup: _ => raw);
            source.BeginFrame(null, Data(FullStatus()));
            Assert.True(source.TryGetNumber(Named("P"), out double value));
            Assert.Equal(expected, value, 6);
        }

        [Fact]
        public void Coercion_InvariantParse_IgnoresLocaleCommas()
        {
            // "1,5" is not a decimal in the invariant culture (it's a thousands
            // separator form) — parsed as 15, never 1.5.
            var source = new SimHubPropertySource(rawLookup: _ => "1,500");
            source.BeginFrame(null, Data(FullStatus()));
            Assert.True(source.TryGetNumber(Named("P"), out double value));
            Assert.Equal(1500.0, value);
        }

        [Theory]
        [InlineData("garbage")]
        [InlineData(null)]
        public void Coercion_UnreadableValues_FailTheRead(object? raw)
        {
            var source = new SimHubPropertySource(rawLookup: _ => raw);
            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetNumber(Named("P"), out _));
        }

        [Fact]
        public void Coercion_ArbitraryObject_FailsTheRead()
        {
            var source = new SimHubPropertySource(rawLookup: _ => new object());
            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetNumber(Named("P"), out _));
            Assert.False(source.TryGetBool(Named("P"), out _));
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(1, true)]
        [InlineData(0, false)]
        [InlineData(2.5, true)]
        [InlineData("true", true)]
        [InlineData("FALSE", false)]
        [InlineData("1", true)]
        [InlineData("0", false)]
        public void Bool_Coercions(object raw, bool expected)
        {
            var source = new SimHubPropertySource(rawLookup: _ => raw);
            source.BeginFrame(null, Data(FullStatus()));
            Assert.True(source.TryGetBool(Named("P"), out bool value));
            Assert.Equal(expected, value);
        }

        [Fact]
        public void Bool_UnparsableString_Fails()
        {
            var source = new SimHubPropertySource(rawLookup: _ => "maybe");
            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetBool(Named("P"), out _));
        }

        [Fact]
        public void Bool_NaN_FailsTheRead()
        {
            // Gap/delta properties publish double.NaN as their "no data" convention —
            // and NaN != 0 is the one comparison NaN satisfies, so without the sentinel
            // guard an IsTrue rule would fire precisely while there is NO data.
            var source = new SimHubPropertySource(rawLookup: _ => double.NaN);
            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetBool(Named("P"), out _));

            // The numeric path still hands NaN through — the engine already treats
            // non-finite samples as gaps (edges) / unsatisfiable comparisons (levels).
            Assert.True(source.TryGetNumber(Named("P"), out double n));
            Assert.True(double.IsNaN(n));
        }

        [Fact]
        public void BuiltInBool_NaN_FailsTheRead()
        {
            // Same sentinel on the typed fast path (a gap field with no reference car).
            var s = FullStatus();
            Set(s, "SpeedLocal", double.NaN);
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(s));
            Assert.False(source.TryGetBool(BuiltIn(BuiltInProperties.Speed), out _));
        }

        // ── Containment ──────────────────────────────────────────────────

        [Fact]
        public void HostLookupThrows_ReadFails_WarnsOncePerName()
        {
            var log = new List<string>();
            var source = new SimHubPropertySource(log.Add,
                rawLookup: _ => throw new InvalidOperationException("host broke"));

            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetNumber(Named("Bad.Prop"), out _));
            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetNumber(Named("Bad.Prop"), out _));
            Assert.False(source.TryGetNumber(Named("Other.Prop"), out _));

            Assert.Single(log, m => m.Contains("Bad.Prop"));
            Assert.Single(log, m => m.Contains("Other.Prop"));
        }

        [Fact]
        public void ActionSpecs_AreNeverReadable()
        {
            var source = new SimHubPropertySource(rawLookup: _ => 1.0);
            source.BeginFrame(null, Data(FullStatus()));
            var action = new PropertySpec { Kind = PropertyKind.FanaBridgeAction, Name = "Fire" };
            Assert.False(source.TryGetNumber(action, out _));
            Assert.False(source.TryGetBool(action, out _));
        }

        [Fact]
        public void NullOrNamelessSpec_FailsTheRead()
        {
            var source = new SimHubPropertySource();
            source.BeginFrame(null, Data(FullStatus()));
            Assert.False(source.TryGetNumber(null, out _));
            Assert.False(source.TryGetNumber(new PropertySpec { Kind = PropertyKind.BuiltIn }, out _));
        }
    }
}
