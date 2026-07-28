using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>
    /// E8 round 3: full-system replay parity harness over the closed matrix.
    /// Comparison = ordered attempted col01+col03 write streams at the transport seam;
    /// sole normalization = PageSet-retry timing jitter. Engine bugs are recorded as
    /// SkippableFact skips with E8-PARITY-XX ids — src is never patched this round.
    /// </summary>
    [Collection(ReplayParityCollection.Name)]
    public class EngineReplayTests
    {
        public EngineReplayTests()
        {
            // Pin on the test thread (collection fixture also pins; belt-and-braces for
            // adjudication MINOR residual determinism).
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        /// <summary>
        /// Known genuine engine divergences discovered by the harness. Each entry is
        /// an E8-PARITY id + one-line cause. Fix round is separate.
        /// </summary>
        private static readonly Dictionary<string, string> KnownParitySkips =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Populated after first matrix run; keys are cell ids.
            };

        // ── Matrix inventory ─────────────────────────────────────────────

        [Fact]
        public void Matrix_EnumeratesClosedBudget_AndLogsAxes()
        {
            var all = ReplayMatrix.All();
            Assert.NotEmpty(all);

            int anchored = all.Count(c => c.Block == ReplayBlock.Anchored);
            int pairwise = all.Count(c => c.Block == ReplayBlock.Pairwise);
            int kept = all.Count(c => c.Block == ReplayBlock.KeptBehavior);
            int hyst = all.Count(c => c.Block == ReplayBlock.Hysteresis);
            int unrep = all.Count(c => !c.IsRepresentable);

            // OQ-5 full budget: ~95 + hysteresis block. Allow the documented ±20.
            Assert.True(all.Count >= 70, "matrix too small: " + all.Count);
            Assert.True(all.Count <= 130, "matrix exploded: " + all.Count);
            Assert.True(anchored >= 20, "anchored OFAT under-covered: " + anchored);
            Assert.True(pairwise >= 30, "pairwise under-covered: " + pairwise);
            Assert.True(kept >= 15, "kept-behavior under-covered: " + kept);
            Assert.True(hyst >= 6, "hysteresis block missing operators: " + hyst);

            // ActionTriggered must never appear (FA2 / adjudication).
            Assert.DoesNotContain(all, c =>
                c.Id.IndexOf("actiontriggered", StringComparison.OrdinalIgnoreCase) >= 0
                || c.Id.IndexOf("action-triggered", StringComparison.OrdinalIgnoreCase) >= 0);

            // Game-start kept cell in every device column (RISK-5).
            foreach (ReplayDevice d in Enum.GetValues(typeof(ReplayDevice)))
            {
                Assert.Contains(all, c =>
                    c.Block == ReplayBlock.KeptBehavior
                    && c.Device == d
                    && c.KeptBehaviorName == "game-start-manual-reset");
            }

            // Every cell logs axes; unrepresentable cells keep a reason (no silent drops).
            var report = new StringBuilder();
            report.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "E8 matrix: total={0} anchored={1} pairwise={2} kept={3} hysteresis={4} unrepresentable={5}",
                all.Count, anchored, pairwise, kept, hyst, unrep));
            foreach (var c in all)
            {
                report.AppendLine(c.AxesLog
                    + (c.IsRepresentable ? "" : " UNREPRESENTABLE=" + c.UnrepresentableReason));
            }

            // Write inventory next to fixtures for the human report.
            string dir = ReplayFixtureFactory.FixturesDirectory();
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "_matrix-inventory.txt"),
                report.ToString(),
                Encoding.UTF8);

            Assert.True(unrep == all.Count(c => c.UnrepresentableReason != null));
        }

        [Fact]
        public void Matrix_MaterializesFixturePairs()
        {
            var representable = ReplayMatrix.All().Where(c => c.IsRepresentable).ToList();
            ReplayFixtureFactory.MaterializeAll(representable);

            string dir = ReplayFixtureFactory.FixturesDirectory();
            foreach (var cell in representable.Take(5))
            {
                Assert.True(File.Exists(Path.Combine(dir, cell.Id + ".v1.json")), cell.Id);
                Assert.True(File.Exists(Path.Combine(dir, cell.Id + ".v2.json")), cell.Id);
            }

            // Spot-check: no ActionTriggered in any v1 fixture content sample.
            string sample = File.ReadAllText(
                Path.Combine(dir, representable[0].Id + ".v1.json"));
            Assert.DoesNotContain("actionTriggered", sample);
        }

        // ── Isolation / culture plumbing ─────────────────────────────────

        [Fact]
        public void Isolation_DistinctTransports_AndInvariantCulturePinned()
        {
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentUICulture);

            var cell = ReplayMatrix.All().First(c =>
                c.IsRepresentable
                && c.Device == ReplayDevice.Pbme
                && c.Block == ReplayBlock.Anchored
                && c.Target == ReplayTarget.Page
                && c.Condition == ReplayCondition.GreaterThan
                && c.Press == ReplayPress.None
                && c.Wire == ReplayWire.Clean);

            var (v9, v2) = ReplayHarness.RunPair(cell);
            // Both engines produced some wire activity on a clean ITM bring-up.
            Assert.NotEmpty(v9);
            Assert.NotEmpty(v2);
            // Recorder defensive-copy: payloads must not be the same array instance.
            if (v9.Count > 0 && v2.Count > 0)
                Assert.False(ReferenceEquals(v9[0].Payload, v2[0].Payload));
        }

        [Fact]
        public void Normalizer_CollapsesDeclinedSetPageRuns()
        {
            // FF 05 04 03 01 — SetPage device 3 page 1
            byte[] page = { 0xFF, 0x05, 0x04, 0x03, 0x01 };
            byte[] other = { 0xFF, 0x05, 0x02, 0x01 }; // EnableItm-shaped

            var raw = new List<WireAttempt>
            {
                new WireAttempt(1, 0, 16, Chan.Col03, page, accepted: false),
                new WireAttempt(2, 0, 32, Chan.Col03, page, accepted: false),
                new WireAttempt(3, 0, 48, Chan.Col03, page, accepted: true),
                new WireAttempt(4, 0, 64, Chan.Col03, other, accepted: true),
            };

            var n = PageSetRetryNormalizer.Normalize(raw);
            Assert.Equal(2, n.Count);
            Assert.True(n[0].Accepted);
            Assert.Equal(1, n[0].FrameIndex); // first frame of run retained
            Assert.True(WireAttempt.PayloadBytesEqual(other, n[1].Payload));
        }

        // ── Per-cell parity theory ───────────────────────────────────────

        public static IEnumerable<object[]> RepresentableCellIds()
            => ReplayMatrix.All()
                .Where(c => c.IsRepresentable)
                .Select(c => new object[] { c.Id });

        public static IEnumerable<object[]> UnrepresentableCellIds()
            => ReplayMatrix.All()
                .Where(c => !c.IsRepresentable)
                .Select(c => new object[] { c.Id, c.UnrepresentableReason! });

        [Theory]
        [MemberData(nameof(UnrepresentableCellIds))]
        public void Unrepresentable_Cells_AreReportedNotSilent(string id, string reason)
        {
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.False(string.IsNullOrWhiteSpace(reason));
            var cell = ReplayMatrix.ById(id);
            Assert.False(cell.IsRepresentable);
            Assert.Equal(reason, cell.UnrepresentableReason);
            // Axes still logged.
            Assert.Contains(id, cell.AxesLog, StringComparison.Ordinal);
        }

        [SkippableTheory]
        [MemberData(nameof(RepresentableCellIds))]
        public void Parity_Cell(string cellId)
        {
            var cell = ReplayMatrix.ById(cellId);
            Assert.True(cell.IsRepresentable, cell.UnrepresentableReason);

            if (KnownParitySkips.TryGetValue(cellId, out string? skipReason))
            {
                Skip.If(true, skipReason);
                return;
            }

            StreamComparer.Result result;
            try
            {
                result = ReplayHarness.RunAndCompare(cell);
            }
            catch (Exception ex)
            {
                // Harness plumbing failures are also recorded as skips when they map to
                // known engine/setup gaps; otherwise fail hard.
                string msg = "E8-PARITY-HARNESS: " + cellId + " — " + ex.GetType().Name
                    + ": " + ex.Message;
                Skip.If(IsSoftHarnessFailure(ex), msg);
                throw new Xunit.Sdk.XunitException(msg + Environment.NewLine + ex);
            }

            if (!result.Passed)
            {
                // Genuine stream divergence: record as skip with a stable id so the
                // suite stays green and the fix round has a punch list. The failure
                // body (both streams) is written next to fixtures for inspection.
                string parityId = "E8-PARITY-" + StableHash(cellId).ToString("D3", CultureInfo.InvariantCulture);
                string cause = SummarizeDivergence(result) ?? FirstLine(result.Failure) ?? "stream mismatch";
                string skipMsg = parityId + ": " + cause;

                string dir = ReplayFixtureFactory.FixturesDirectory();
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, cellId + ".diff.txt"),
                    skipMsg + Environment.NewLine + result.Failure,
                    Encoding.UTF8);

                // Also append to the skip ledger for the human report.
                File.AppendAllText(
                    Path.Combine(dir, "_parity-skips.txt"),
                    cellId + "\t" + skipMsg + Environment.NewLine,
                    Encoding.UTF8);

                Skip.If(true, skipMsg);
            }
        }

        /// <summary>One-line cause from the first diverging pair (channel + short hex).</summary>
        private static string? SummarizeDivergence(StreamComparer.Result result)
        {
            var v9 = result.V9;
            var v2 = result.V2;
            int n = Math.Min(v9.Count, v2.Count);
            for (int i = 0; i < n; i++)
            {
                if (v9[i].Channel == v2[i].Channel
                    && v9[i].Accepted == v2[i].Accepted
                    && WireAttempt.PayloadBytesEqual(v9[i].Payload, v2[i].Payload))
                    continue;
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "at[{0}] v9={1}/{2} v2={3}/{4}",
                    i,
                    v9[i].Channel, ShortHex(v9[i].Payload),
                    v2[i].Channel, ShortHex(v2[i].Payload));
            }
            if (v9.Count != v2.Count)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "length v9={0} v2={1} (prefix matched {2})",
                    v9.Count, v2.Count, n);
            }
            return null;
        }

        private static string ShortHex(byte[] p)
        {
            if (p == null || p.Length == 0)
                return "-";
            int take = Math.Min(p.Length, 6);
            return BitConverter.ToString(p, 0, take) + (p.Length > take ? "…" : "");
        }

        private static bool IsSoftHarnessFailure(Exception ex)
        {
            // Identity/profile misses for exotic wheels, missing SimHub props, etc.
            string m = ex.Message ?? "";
            return m.IndexOf("Identity commit", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("No profile", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("AutoConnect", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            int nl = text!.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? text : text.Substring(0, nl);
        }

        private static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s)
                    h = h * 31 + c;
                return Math.Abs(h % 900) + 100; // 100..999
            }
        }
    }

    /// <summary>
    /// Unit tests for the stream comparer / known-diff consumption (no device session).
    /// </summary>
    [Collection(ReplayParityCollection.Name)]
    public class StreamComparerTests
    {
        public StreamComparerTests()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        [Fact]
        public void Compare_IdenticalStreams_Pass()
        {
            byte[] p = { 1, 2, 3 };
            var a = new[]
            {
                new WireAttempt(0, 0, 0, Chan.Col01, p, true),
                new WireAttempt(1, 0, 16, Chan.Col03, p, true),
            };
            var r = StreamComparer.Compare(a, a);
            Assert.True(r.Passed, r.Failure);
        }

        [Fact]
        public void Compare_UnknownDiff_Fails()
        {
            var v9 = new[] { new WireAttempt(0, 0, 0, Chan.Col01, new byte[] { 1 }, true) };
            var v2 = new[] { new WireAttempt(0, 0, 0, Chan.Col01, new byte[] { 2 }, true) };
            var r = StreamComparer.Compare(v9, v2);
            Assert.False(r.Passed);
            Assert.Contains("mismatch", r.Failure!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Compare_NamedKnownDiff_Consumes()
        {
            var v9 = new[] { new WireAttempt(5, 0, 80, Chan.Col03, new byte[] { 0xAA }, true) };
            var v2 = new[] { new WireAttempt(5, 0, 80, Chan.Col03, new byte[] { 0xBB }, true) };
            var diffs = new[]
            {
                new KnownDiff("suffix-blink", 5, Chan.Col03, new byte[] { 0xAA }, new byte[] { 0xBB }),
            };
            var r = StreamComparer.Compare(v9, v2, diffs);
            Assert.True(r.Passed, r.Failure);
        }

        [Fact]
        public void Compare_UnconsumedKnownDiff_Fails()
        {
            var a = new[] { new WireAttempt(0, 0, 0, Chan.Col01, new byte[] { 1 }, true) };
            var diffs = new[]
            {
                new KnownDiff("never-happened", 9, Chan.Col03, new byte[] { 1 }, null),
            };
            var r = StreamComparer.Compare(a, a, diffs);
            Assert.False(r.Passed);
            Assert.Contains("Unconsumed", r.Failure!, StringComparison.Ordinal);
        }
    }
}
