using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Logging;
using FanaBridge.Tests.TestDoubles;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Profiles
{
    /// <summary>
    /// Issue #81: every lookup and deletion path must resolve an attachment to
    /// the same profile, whatever order the profile files were created or the
    /// directory is enumerated in. Runs against the real store, writing user
    /// profiles into the directory it reads.
    /// </summary>
    public class WheelProfileResolutionTests : IDisposable
    {
        // A wheel code no built-in profile matches, so the tests own every candidate.
        private const string TestWheel = "FB81WHEEL";

        private readonly TempUserProfiles _profiles = new TempUserProfiles();

        public void Dispose() => _profiles.Dispose();

        private static IEnumerable<string> Ascending(string dir) =>
            WheelProfileStore.DefaultEnumerateUserFiles(dir).OrderBy(f => f, StringComparer.Ordinal);

        private static IEnumerable<string> Descending(string dir) =>
            WheelProfileStore.DefaultEnumerateUserFiles(dir).OrderByDescending(f => f, StringComparer.Ordinal);

        private sealed class CapturingSink : ILogSink
        {
            public List<string> Warnings { get; } = new List<string>();
            public void Info(string message) { }
            public void Warn(string message) => Warnings.Add(message);
            public void Error(string message) { }
            public void Debug(string message) { }
        }

        // ── Same-source duplicate ids ─────────────────────────────────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SameSourceDuplicateId_FirstFileByNameWins_WhateverTheCreationOrder(bool aFirst)
        {
            if (aFirst)
            {
                _profiles.Write("fb81-a.json", "FB81_DUP", TestWheel);
                _profiles.Write("fb81-b.json", "FB81_DUP", TestWheel);
            }
            else
            {
                _profiles.Write("fb81-b.json", "FB81_DUP", TestWheel);
                _profiles.Write("fb81-a.json", "FB81_DUP", TestWheel);
            }
            _profiles.Reload();

            AssertDuplicateResolvesToA();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SameSourceDuplicateId_FirstFileByNameWins_WhateverTheEnumerationOrder(bool ascending)
        {
            _profiles.Write("fb81-a.json", "FB81_DUP", TestWheel);
            _profiles.Write("fb81-b.json", "FB81_DUP", TestWheel);
            WheelProfileStore.EnumerateUserFiles = ascending
                ? (Func<string, IEnumerable<string>>)Ascending
                : Descending;
            _profiles.Reload();

            AssertDuplicateResolvesToA();
        }

        private static void AssertDuplicateResolvesToA()
        {
            // The rejected duplicate is in neither index, so every path agrees.
            var loaded = WheelProfileStore.GetAll().Where(p => p.Id == "FB81_DUP").ToList();
            var only = Assert.Single(loaded);
            Assert.Equal("fb81-a.json", Path.GetFileName(only.SourcePath));

            Assert.Same(only, WheelProfileStore.GetById("FB81_DUP"));
            Assert.Same(only, WheelProfileStore.GetById("FB81_DUP", ProfileSource.User));
            Assert.Same(only, WheelProfileStore.ResolveOverrideKey("FB81_DUP:User"));
            Assert.Same(only, WheelProfileStore.FindByWheelType(TestWheel));
            Assert.Same(only, Assert.Single(WheelProfileStore.FindAllForWheel(TestWheel)));
        }

        [Fact]
        public void SameSourceDuplicateId_WarningNamesBothFiles()
        {
            _profiles.Write("fb81-a.json", "FB81_DUP", TestWheel);
            _profiles.Write("fb81-b.json", "FB81_DUP", TestWheel);

            var sink = new CapturingSink();
            var previous = Log.Sink;
            Log.Sink = sink;
            try { _profiles.Reload(); }
            finally { Log.Sink = previous; }

            Assert.Contains(sink.Warnings, w => w.Contains(
                "User profile 'FB81_DUP' in fb81-b.json ignored: id already used by fb81-a.json"));
        }

        // ── Across sources ────────────────────────────────────────────────

        [Fact]
        public void SameIdUserProfile_WinsTheIdIndexAndResolution_ButTheBuiltInStaysOffered()
        {
            _profiles.Write("fb81-pswbmw.json", "PSWBMW", "PSWBMW");
            _profiles.Reload();

            var user = WheelProfileStore.GetById("PSWBMW", ProfileSource.User);
            var builtIn = WheelProfileStore.GetById("PSWBMW", ProfileSource.BuiltIn);
            Assert.NotNull(user);
            Assert.NotNull(builtIn);

            Assert.Same(user, WheelProfileStore.GetById("PSWBMW"));
            Assert.Same(user, WheelProfileStore.FindByWheelType("PSWBMW"));
            Assert.Equal(new[] { builtIn, user }, WheelProfileStore.FindAllForWheel("PSWBMW"));
        }

        // ── Overrides ─────────────────────────────────────────────────────

        [Fact]
        public void MatchingOverride_IsHonoured()
        {
            _profiles.Write("fb81-alt.json", "PSWBMW_FB81", "PSWBMW");
            _profiles.Reload();

            var resolved = WheelProfileStore.Resolve("PSWBMW", null, "PSWBMW_FB81:User", out var overridden);

            Assert.Equal("PSWBMW_FB81", resolved?.Id);
            Assert.Same(overridden, resolved);
        }

        [Fact]
        public void OverrideForADifferentAttachment_IsIgnored()
        {
            var resolved = WheelProfileStore.Resolve("PSWBMW", null, "PHUB:BuiltIn", out var overridden);

            // The override resolves, but to another wheel's profile.
            Assert.Equal("PHUB", overridden?.Id);
            Assert.Equal("PSWBMW", resolved?.Id);
            Assert.Equal("PSWBMW", WheelProfileStore.FindByWheelType("PSWBMW", null, "PHUB:BuiltIn")?.Id);
        }

        [Fact]
        public void OverrideWithoutAModule_IsIgnoredForAModuleAttachment()
        {
            var resolved = WheelProfileStore.FindByWheelType("PHUB", "PBMR", "PHUB:BuiltIn");

            Assert.Equal("PHUB_PBMR", resolved?.Id);
        }

        // ── Match-scan tier ───────────────────────────────────────────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MatchScan_PicksTheFirstCandidateInLoadOrder_WhateverTheEnumerationOrder(bool ascending)
        {
            // Neither id is the match key, so resolution falls to the scan. The
            // ids sort opposite to the files, so this is file order, not id order.
            _profiles.Write("fb81-a.json", "FB81_Z", TestWheel);
            _profiles.Write("fb81-b.json", "FB81_Y", TestWheel);
            WheelProfileStore.EnumerateUserFiles = ascending
                ? (Func<string, IEnumerable<string>>)Ascending
                : Descending;
            _profiles.Reload();

            Assert.Equal("FB81_Z", WheelProfileStore.FindByWheelType(TestWheel)?.Id);
            Assert.Equal(new[] { "FB81_Z", "FB81_Y" },
                WheelProfileStore.FindAllForWheel(TestWheel).Select(p => p.Id));
        }

        [Fact]
        public void MatchKeyNamedProfile_BeatsAnEarlierScanCandidate()
        {
            _profiles.Write("fb81-a.json", "FB81_OTHER", TestWheel);
            _profiles.Write("fb81-b.json", TestWheel, TestWheel);
            _profiles.Reload();

            Assert.Equal(TestWheel, WheelProfileStore.FindByWheelType(TestWheel)?.Id);
        }

        // ── Module attachments ────────────────────────────────────────────

        [Fact]
        public void FindAllForWheel_ExcludesModuleLessProfiles_ForAModuleAttachment()
        {
            _profiles.Write("fb81-hub.json", "PHUB_FB81", "PHUB");
            _profiles.Reload();

            var candidates = WheelProfileStore.FindAllForWheel("PHUB", "PBMR");

            Assert.NotEmpty(candidates);
            Assert.All(candidates, p => Assert.Equal("PBMR", p.Match.ModuleType));
        }

        [Fact]
        public void ModuleWithNoProfile_HasNoBareWheelFallback()
        {
            Assert.Null(WheelProfileStore.FindByWheelType("PHUB", "FB81MODULE"));
            Assert.Empty(WheelProfileStore.FindAllForWheel("PHUB", "FB81MODULE"));
        }

        // ── Deletion ──────────────────────────────────────────────────────

        [Fact]
        public void Delete_RemovesTheProfileTheResolverPicked()
        {
            var a = _profiles.Write("fb81-a.json", "FB81_DEL", TestWheel);
            var b = _profiles.Write("fb81-b.json", "FB81_DEL", TestWheel);
            _profiles.Reload();

            var resolved = WheelProfileStore.FindByWheelType(TestWheel);
            Assert.Equal(a, resolved?.SourcePath);

            Assert.True(WheelProfileStore.DeleteUserProfile(resolved!.Id));

            Assert.False(File.Exists(a));
            Assert.True(File.Exists(b));
            // The store now holds what a fresh load gives: the remaining file.
            Assert.Equal(b, WheelProfileStore.FindByWheelType(TestWheel)?.SourcePath);
        }

        [Fact]
        public void Delete_OfASameIdUserProfile_HandsTheIdBackToTheBuiltIn()
        {
            var file = _profiles.Write("fb81-pswbmw.json", "PSWBMW", "PSWBMW");
            _profiles.Reload();
            var resolved = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.Equal(ProfileSource.User, resolved?.Source);

            Assert.True(WheelProfileStore.DeleteUserProfile(resolved!.Id));

            Assert.False(File.Exists(file));
            Assert.Equal(ProfileSource.BuiltIn, WheelProfileStore.GetById("PSWBMW")?.Source);
            Assert.Equal(ProfileSource.BuiltIn, WheelProfileStore.FindByWheelType("PSWBMW")?.Source);
        }
    }
}
