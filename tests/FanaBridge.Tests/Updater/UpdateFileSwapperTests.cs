using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FanaBridge.Updater;
using Xunit;

namespace FanaBridge.Tests.Updater
{
    public class UpdateFileSwapperTests
    {
        [Fact]
        public void Apply_HappyPath_SwapsDll_CopiesLogos_LeavesUserData()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("OLD"));
                string userData = Path.Combine(install, "FanaBridge", "Profiles");
                Directory.CreateDirectory(userData);
                string profile = Path.Combine(userData, "user.json");
                File.WriteAllText(profile, "{\"ok\":true}");

                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));
                string stagedLogos = Path.Combine(staging, UpdatePackage.LogosDirName);
                Directory.CreateDirectory(stagedLogos);
                File.WriteAllBytes(Path.Combine(stagedLogos, "wheel.png"), new byte[] { 1, 2, 3 });

                var swapper = new UpdateFileSwapper(readFileVersion: _ => "0.7.0.0");
                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.True(result.Success, result.Error);
                Assert.Equal("NEW", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName)));
                Assert.Equal("OLD", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.OldSuffix)));
                Assert.False(File.Exists(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.NewSuffix)));
                Assert.True(File.Exists(Path.Combine(install, UpdatePackage.LogosDirName, "wheel.png")));
                Assert.Equal("{\"ok\":true}", File.ReadAllText(profile));
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_RenameWhileOpen_SucceedsWithShareDelete()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                string live = Path.Combine(install, UpdatePackage.DllName);
                File.WriteAllBytes(live, Encoding.UTF8.GetBytes("OLD"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));

                using (var hold = new FileStream(live, FileMode.Open, FileAccess.Read,
                           FileShare.Read | FileShare.Delete))
                {
                    var swapper = new UpdateFileSwapper(readFileVersion: _ => "0.7.0.0");
                    SwapResult result = swapper.Apply(staging, install, "0.7.0");
                    Assert.True(result.Success, result.Error);
                }

                Assert.Equal("NEW", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName)));
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_CommitRename2Fails_RollsBack()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("OLD"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));

                int moveCount = 0;
                var swapper = new UpdateFileSwapper(
                    move: (src, dst) =>
                    {
                        moveCount++;
                        if (moveCount == 2)
                            throw new IOException("simulated commit failure");
                        File.Move(src, dst);
                    },
                    readFileVersion: _ => "0.7.0.0");

                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.False(result.Success);
                Assert.True(result.RolledBack);
                Assert.Equal("OLD", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName)));
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_CommitAndRestoreFail_ErrorMentionsOld_NoThrow()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("OLD"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));

                int moveCount = 0;
                var warns = new List<string>();
                var swapper = new UpdateFileSwapper(
                    move: (src, dst) =>
                    {
                        moveCount++;
                        if (moveCount == 1)
                        {
                            File.Move(src, dst); // live → .old
                            return;
                        }
                        // commit and restore both fail
                        throw new IOException("blocked");
                    },
                    readFileVersion: _ => "0.7.0.0",
                    logWarn: warns.Add);

                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.False(result.Success);
                Assert.False(result.RolledBack);
                Assert.Contains(".old", result.Error, StringComparison.OrdinalIgnoreCase);
                Assert.NotEmpty(warns);
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_UndeletablePreexistingOld_FailClosed()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("LIVE"));
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.OldSuffix),
                    Encoding.UTF8.GetBytes("STALE"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));

                var swapper = new UpdateFileSwapper(
                    delete: path => throw new IOException("locked"),
                    readFileVersion: _ => "0.7.0.0");

                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.False(result.Success);
                Assert.Equal("LIVE", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName)));
                Assert.False(File.Exists(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.NewSuffix)));
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_VersionMismatch_FailClosedBeforeRename()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("LIVE"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));

                bool moved = false;
                var swapper = new UpdateFileSwapper(
                    move: (src, dst) =>
                    {
                        moved = true;
                        File.Move(src, dst);
                    },
                    readFileVersion: _ => "0.1.0.0");

                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.False(result.Success);
                Assert.False(moved);
                Assert.Equal("LIVE", File.ReadAllText(Path.Combine(install, UpdatePackage.DllName)));
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_UnauthorizedAccess_SetsAccessDenied()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("LIVE"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));

                var swapper = new UpdateFileSwapper(
                    move: (src, dst) => throw new UnauthorizedAccessException("denied"),
                    readFileVersion: _ => "0.7.0.0");

                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.False(result.Success);
                Assert.True(result.AccessDenied);
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void Apply_LogoCopyFailure_StillSuccess_Warns()
        {
            string install = MakeTempDir("install");
            string staging = MakeTempDir("staging");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName), Encoding.UTF8.GetBytes("OLD"));
                File.WriteAllBytes(Path.Combine(staging, UpdatePackage.DllName), Encoding.UTF8.GetBytes("NEW"));
                string stagedLogos = Path.Combine(staging, UpdatePackage.LogosDirName);
                Directory.CreateDirectory(stagedLogos);
                File.WriteAllBytes(Path.Combine(stagedLogos, "x.png"), new byte[] { 1 });

                var warns = new List<string>();
                var swapper = new UpdateFileSwapper(
                    copyOverwrite: (src, dst) =>
                    {
                        if (dst.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            throw new IOException("logo blocked");
                        File.Copy(src, dst, overwrite: true);
                    },
                    readFileVersion: _ => "0.7.0.0",
                    logWarn: warns.Add);

                SwapResult result = swapper.Apply(staging, install, "0.7.0");

                Assert.True(result.Success, result.Error);
                Assert.Contains(warns, w => w.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(staging);
            }
        }

        [Fact]
        public void CleanupStaleArtifacts_RemovesOldNewAndTempDirs_NeverThrows()
        {
            string install = MakeTempDir("install");
            string tempRoot = MakeTempDir("temp");
            try
            {
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.OldSuffix), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.NewSuffix), new byte[] { 2 });
                string stale = Path.Combine(tempRoot, "FanaBridge-update-abc");
                Directory.CreateDirectory(stale);
                File.WriteAllText(Path.Combine(stale, "x.bin"), "x");

                UpdateFileSwapper.CleanupStaleArtifacts(install, tempRoot, null);

                Assert.False(File.Exists(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.OldSuffix)));
                Assert.False(File.Exists(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.NewSuffix)));
                Assert.False(Directory.Exists(stale));

                // Locked file: open .old then cleanup should not throw.
                File.WriteAllBytes(Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.OldSuffix), new byte[] { 3 });
                using (var hold = new FileStream(
                           Path.Combine(install, UpdatePackage.DllName + UpdateFileSwapper.OldSuffix),
                           FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    UpdateFileSwapper.CleanupStaleArtifacts(install, tempRoot, _ => { });
                }
            }
            finally
            {
                DeleteQuiet(install);
                DeleteQuiet(tempRoot);
            }
        }

        private static string MakeTempDir(string label)
        {
            string path = Path.Combine(Path.GetTempPath(), "FanaBridge-swap-" + label + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteQuiet(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
