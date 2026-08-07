using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FanaBridge.Updater;
using Xunit;

namespace FanaBridge.Tests.Updater
{
    public class UpdateServiceTests
    {
        [Fact]
        public async Task CheckAsync_NewerRelease_UpdateAvailable_FiresPhases()
        {
            var phases = new List<UpdatePhase>();
            var clock = new Clock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: GoodDigestHex())),
                utcNow: clock.Now);
            svc.Changed += s => phases.Add(s.Phase);

            await svc.CheckAsync();

            Assert.Equal(UpdatePhase.UpdateAvailable, svc.Snapshot.Phase);
            Assert.NotNull(svc.Snapshot.Release);
            Assert.Equal("0.7.0", svc.Snapshot.Release!.Version);
            Assert.Equal(new[] { UpdatePhase.Checking, UpdatePhase.UpdateAvailable }, phases);
        }

        [Fact]
        public async Task CheckAsync_SameOrOlder_UpToDate()
        {
            var svc = CreateService(
                currentVersion: "0.7.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: GoodDigestHex())));

            await svc.CheckAsync();
            Assert.Equal(UpdatePhase.UpToDate, svc.Snapshot.Phase);

            var svc2 = CreateService(
                currentVersion: "0.8.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: GoodDigestHex())));
            await svc2.CheckAsync();
            Assert.Equal(UpdatePhase.UpToDate, svc2.Snapshot.Phase);
        }

        [Fact]
        public async Task CheckAsync_FetchThrows_CheckFailed_NoEscape()
        {
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => throw new IOException("network down"));

            await svc.CheckAsync();
            Assert.Equal(UpdatePhase.CheckFailed, svc.Snapshot.Phase);
            Assert.Contains("network", svc.Snapshot.FailureDetail, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task DownloadAndApply_NotifyOnly_IsNoOp()
        {
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: null)));

            await svc.CheckAsync();
            Assert.Equal(UpdatePhase.UpdateAvailable, svc.Snapshot.Phase);
            Assert.False(svc.Snapshot.Release!.CanSelfInstall);

            int bytesCalls = 0;
            // Rebuild with fetchBytes counter — DownloadAndApply should not call it.
            var clock = new Clock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var svc2 = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: null)),
                fetchBytes: (_, __) =>
                {
                    bytesCalls++;
                    return Task.FromResult(Array.Empty<byte>());
                },
                utcNow: clock.Now);
            await svc2.CheckAsync();
            await svc2.DownloadAndApplyAsync();
            Assert.Equal(0, bytesCalls);
            Assert.Equal(UpdatePhase.UpdateAvailable, svc2.Snapshot.Phase);
        }

        [Fact]
        public async Task DownloadAndApply_DigestMismatch_Failed_SwapperNotInvoked()
        {
            bool swapperCalled = false;
            var swapper = new UpdateFileSwapper(
                move: (a, b) => { swapperCalled = true; File.Move(a, b); },
                copyOverwrite: (a, b) => { swapperCalled = true; File.Copy(a, b, true); },
                readFileVersion: _ => "0.7.0.0");

            string digest = new string('a', 64);
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: digest)),
                fetchBytes: (_, __) => Task.FromResult(Encoding.UTF8.GetBytes("not matching")),
                swapper: swapper);

            await svc.CheckAsync();
            await svc.DownloadAndApplyAsync();

            Assert.Equal(UpdatePhase.Failed, svc.Snapshot.Phase);
            Assert.Contains("checksum", svc.Snapshot.FailureDetail, StringComparison.OrdinalIgnoreCase);
            Assert.False(swapperCalled);
        }

        [Fact]
        public async Task DownloadAndApply_HappyPath_ReadyToRestart()
        {
            string install = Path.Combine(Path.GetTempPath(), "FanaBridge-svc-install-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-svc-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(install);
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("OLD"));

                byte[] zipBytes = BuildReleaseZip(Encoding.UTF8.GetBytes("NEW-DLL"));
                string hex = Sha256Hex(zipBytes);

                var phases = new List<UpdatePhase>();
                var swapper = new UpdateFileSwapper(readFileVersion: _ => "0.7.0.0");
                var svc = CreateService(
                    currentVersion: "0.6.0",
                    installDir: install,
                    fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: hex)),
                    fetchBytes: (_, __) => Task.FromResult(zipBytes),
                    swapper: swapper,
                    stagingDirFactory: () =>
                    {
                        Directory.CreateDirectory(staging);
                        return staging;
                    });
                svc.Changed += s => phases.Add(s.Phase);

                await svc.CheckAsync();
                await svc.DownloadAndApplyAsync();

                Assert.Equal(UpdatePhase.ReadyToRestart, svc.Snapshot.Phase);
                Assert.Contains(UpdatePhase.Downloading, phases);
                Assert.Contains(UpdatePhase.Applying, phases);
                Assert.Equal(UpdatePhase.ReadyToRestart, phases[phases.Count - 1]);
                Assert.Equal("NEW-DLL", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName)));

                // Terminal: both commands no-op.
                int before = phases.Count;
                await svc.CheckAsync();
                await svc.DownloadAndApplyAsync();
                Assert.Equal(before, phases.Count);
                Assert.Equal(UpdatePhase.ReadyToRestart, svc.Snapshot.Phase);
            }
            finally
            {
                try { if (Directory.Exists(install)) Directory.Delete(install, true); } catch { /* ignore */ }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public async Task CheckAsync_Debounce_SecondWithin30sNoOp_After40sRuns()
        {
            var clock = new Clock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            int fetches = 0;
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) =>
                {
                    fetches++;
                    return Task.FromResult(FeedJson("0.7.0", digest: GoodDigestHex()));
                },
                utcNow: clock.Now);

            await svc.CheckAsync();
            Assert.Equal(1, fetches);

            clock.Utc = clock.Utc.AddSeconds(10);
            await svc.CheckAsync();
            Assert.Equal(1, fetches);

            clock.Utc = clock.Utc.AddSeconds(30); // total +40 from first completion time base
            await svc.CheckAsync();
            Assert.Equal(2, fetches);
        }

        [Fact]
        public async Task CheckAsync_ConcurrentSecondCall_IsNoOp()
        {
            var tcs = new TaskCompletionSource<string>();
            int fetches = 0;
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: async (_, ct) =>
                {
                    Interlocked.Increment(ref fetches);
                    return await tcs.Task.ConfigureAwait(false);
                });

            Task first = svc.CheckAsync();
            // Allow first to enter fetch.
            await Task.Delay(50);
            Task second = svc.CheckAsync();
            await second;

            Assert.Equal(1, fetches);
            tcs.SetResult(FeedJson("0.7.0", digest: GoodDigestHex()));
            await first;
            Assert.Equal(UpdatePhase.UpdateAvailable, svc.Snapshot.Phase);
        }

        [Fact]
        public async Task CheckAsync_ThrowingSubscriber_DoesNotBreakTransition()
        {
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: GoodDigestHex())));
            svc.Changed += _ => throw new InvalidOperationException("boom");
            var saw = new List<UpdatePhase>();
            svc.Changed += s => saw.Add(s.Phase);

            await svc.CheckAsync();
            Assert.Equal(UpdatePhase.UpdateAvailable, svc.Snapshot.Phase);
            Assert.Contains(UpdatePhase.UpdateAvailable, saw);
        }

        [Fact]
        public async Task CheckAsync_UnparseableCurrentVersion_UpToDateEvenIfFeedNewer()
        {
            var warns = new List<string>();
            var svc = CreateService(
                currentVersion: "not-a-version",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: GoodDigestHex())),
                logWarn: warns.Add);

            await svc.CheckAsync();
            Assert.Equal(UpdatePhase.UpToDate, svc.Snapshot.Phase);
            Assert.Contains(warns, w => w.IndexOf("unparseable", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public async Task DownloadAndApply_CanceledAfterExtraction_RestoresUpdateAvailable_CleansStaging()
        {
            byte[] zip = BuildReleaseZip(new byte[] { 1, 2, 3 });
            string digest = Sha256Hex(zip);

            bool swapperCalled = false;
            var swapper = new UpdateFileSwapper(
                move: (_, __) => swapperCalled = true,
                copyOverwrite: (_, __) => swapperCalled = true,
                readFileVersion: _ => "0.7.0.0");

            string staging = Path.Combine(
                Path.GetTempPath(), "FanaBridge-update-cancel-" + Guid.NewGuid().ToString("N"));
            using var cts = new CancellationTokenSource();
            var svc = CreateService(
                currentVersion: "0.6.0",
                fetchText: (_, __) => Task.FromResult(FeedJson("0.7.0", digest: digest)),
                fetchBytes: (_, __) => Task.FromResult(zip),
                swapper: swapper,
                // Cancel after the pre-staging token check but before the
                // post-extraction one — extraction runs, then the last
                // cancellation point must clean up and restore.
                stagingDirFactory: () => { cts.Cancel(); return staging; });

            await svc.CheckAsync();
            Assert.Equal(UpdatePhase.UpdateAvailable, svc.Snapshot.Phase);

            await svc.DownloadAndApplyAsync(cts.Token);

            Assert.Equal(UpdatePhase.UpdateAvailable, svc.Snapshot.Phase);
            Assert.False(swapperCalled);
            Assert.False(Directory.Exists(staging));
        }

        private static UpdateService CreateService(
            string currentVersion,
            Func<string, CancellationToken, Task<string>>? fetchText = null,
            Func<string, CancellationToken, Task<byte[]>>? fetchBytes = null,
            string? installDir = null,
            UpdateFileSwapper? swapper = null,
            Func<string>? stagingDirFactory = null,
            Func<DateTime>? utcNow = null,
            Action<string>? logWarn = null)
        {
            return new UpdateService(
                currentVersion: currentVersion,
                installDir: installDir ?? Path.GetTempPath(),
                fetchText: fetchText ?? ((_, __) => Task.FromResult("{}")),
                fetchBytes: fetchBytes ?? ((_, __) => Task.FromResult(Array.Empty<byte>())),
                releaseFeedUrl: "https://example.com/releases/latest",
                logInfo: _ => { },
                logWarn: logWarn ?? (_ => { }),
                swapper: swapper,
                stagingDirFactory: stagingDirFactory,
                utcNow: utcNow);
        }

        private static string FeedJson(string version, string? digest)
        {
            string tag = "v" + version;
            string asset = "FanaBridge-" + version + ".zip";
            string digestJson = digest == null
                ? ""
                : @", ""digest"": ""sha256:" + digest + @"""";
            return @"{
  ""tag_name"": """ + tag + @""",
  ""html_url"": ""https://example.com/r/" + tag + @""",
  ""assets"": [{
    ""name"": """ + asset + @""",
    ""browser_download_url"": ""https://example.com/" + asset + @""",
    ""size"": 100
    " + digestJson + @"
  }]
}";
        }

        private static string GoodDigestHex() => new string('b', 64);

        private static byte[] BuildReleaseZip(byte[] dllBytes)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry e = zip.CreateEntry(UpdatePackage.DllName);
                using Stream s = e.Open();
                s.Write(dllBytes, 0, dllBytes.Length);
            }
            return ms.ToArray();
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private sealed class Clock
        {
            public DateTime Utc;
            public Clock(DateTime utc) => Utc = utc;
            public DateTime Now() => Utc;
        }
    }
}
