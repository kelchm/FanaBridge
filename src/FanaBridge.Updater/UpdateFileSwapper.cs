#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace FanaBridge.Updater
{
    /// <summary>Outcome of an in-place DLL swap attempt.</summary>
    public sealed class SwapResult
    {
        /// <summary>True when the live DLL was replaced successfully.</summary>
        public bool Success { get; }

        /// <summary>Failure detail; non-null iff <see cref="Success"/> is false.</summary>
        public string? Error { get; }

        /// <summary>
        /// True when the second commit rename failed but the live DLL was restored
        /// from <c>.old</c>, so the install is intact at the previous version.
        /// </summary>
        public bool RolledBack { get; }

        /// <summary>
        /// True when the failure was caused by <see cref="UnauthorizedAccessException"/>
        /// or an access-denied <see cref="IOException"/> (HRESULT 0x80070005).
        /// </summary>
        public bool AccessDenied { get; }

        /// <summary>Creates a swap outcome.</summary>
        public SwapResult(bool success, string? error, bool rolledBack, bool accessDenied)
        {
            Success = success;
            Error = error;
            RolledBack = rolledBack;
            AccessDenied = accessDenied;
        }

        /// <summary>Successful swap.</summary>
        public static SwapResult Ok() => new SwapResult(true, null, false, false);

        /// <summary>Failed swap with optional rollback / access-denied flags.</summary>
        public static SwapResult Fail(string error, bool rolledBack = false, bool accessDenied = false)
            => new SwapResult(false, error, rolledBack, accessDenied);
    }

    /// <summary>
    /// Commits a staged update into the SimHub install directory using a
    /// write-then-two-rename strategy so the crash window after the live DLL is
    /// touched is two metadata operations. User data under
    /// <c>installDir\FanaBridge\</c> is never read or written.
    /// </summary>
    public sealed class UpdateFileSwapper
    {
        /// <summary>Suffix of the previous live DLL kept for rollback.</summary>
        public const string OldSuffix = ".old";

        /// <summary>Suffix of the fully-written staged DLL before the commit rename.</summary>
        public const string NewSuffix = ".new";

        // ERROR_ACCESS_DENIED — IOException HResult on net48 for ACL/share denial.
        private const int HResultAccessDenied = unchecked((int)0x80070005);

        private readonly Action<string, string> _move;
        private readonly Action<string, string> _copyOverwrite;
        private readonly Action<string> _delete;
        private readonly Func<string, bool> _exists;
        private readonly Func<string, string?> _readFileVersion;
        private readonly Action<string> _logWarn;

        /// <summary>
        /// All seams optional; defaults use System.IO
        /// (<see cref="File.Move(string,string)"/>, <see cref="File.Copy(string,string,bool)"/>
        /// overwrite, <see cref="File.Delete"/>, <see cref="File.Exists"/>,
        /// <see cref="FileVersionInfo.GetVersionInfo"/> FileVersion).
        /// </summary>
        public UpdateFileSwapper(
            Action<string, string>? move = null,
            Action<string, string>? copyOverwrite = null,
            Action<string>? delete = null,
            Func<string, bool>? exists = null,
            Func<string, string?>? readFileVersion = null,
            Action<string>? logWarn = null)
        {
            _move = move ?? ((src, dst) => File.Move(src, dst));
            _copyOverwrite = copyOverwrite ?? ((src, dst) => File.Copy(src, dst, overwrite: true));
            _delete = delete ?? File.Delete;
            _exists = exists ?? File.Exists;
            _readFileVersion = readFileVersion ?? DefaultReadFileVersion;
            _logWarn = logWarn ?? (_ => { });
        }

        private static string? DefaultReadFileVersion(string path)
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(path).FileVersion;
            }
            catch (UnauthorizedAccessException)
            {
                // Let Apply's outer catch classify this as access-denied instead
                // of reporting a misleading generic version mismatch.
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Applies the staged package under <paramref name="stagingDir"/> into
        /// <paramref name="installDir"/>. When <paramref name="expectedVersion"/> is
        /// non-null (release version like <c>0.7.0</c>), the staged DLL's FileVersion
        /// Major.Minor.Build must match before any live file is touched.
        /// </summary>
        public SwapResult Apply(string stagingDir, string installDir, string? expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(stagingDir))
                return SwapResult.Fail("Staging directory is required.");
            if (string.IsNullOrWhiteSpace(installDir))
                return SwapResult.Fail("Install directory is required.");

            string stagedDll = Path.Combine(stagingDir, UpdatePackage.DllName);
            string liveDll = Path.Combine(installDir, UpdatePackage.DllName);
            string oldDll = liveDll + OldSuffix;
            string newDll = liveDll + NewSuffix;

            try
            {
                // 1. Staged DLL must exist.
                if (!_exists(stagedDll))
                    return SwapResult.Fail("Staged " + UpdatePackage.DllName + " is missing.");

                // 2. Clear pre-existing .old / .new; fail closed if delete fails so we
                // never rename over an ambiguous leftover.
                if (_exists(oldDll))
                {
                    try { _delete(oldDll); }
                    catch (Exception ex)
                    {
                        return FailClosed("Could not remove pre-existing " + UpdatePackage.DllName + OldSuffix + ": " + ex.Message, ex);
                    }
                    if (_exists(oldDll))
                        return SwapResult.Fail("Could not remove pre-existing " + UpdatePackage.DllName + OldSuffix + ".");
                }
                if (_exists(newDll))
                {
                    try { _delete(newDll); }
                    catch (Exception ex)
                    {
                        return FailClosed("Could not remove pre-existing " + UpdatePackage.DllName + NewSuffix + ": " + ex.Message, ex);
                    }
                    if (_exists(newDll))
                        return SwapResult.Fail("Could not remove pre-existing " + UpdatePackage.DllName + NewSuffix + ".");
                }

                // 3. Full write of .new before touching the live file; version sanity first.
                _copyOverwrite(stagedDll, newDll);

                if (expectedVersion != null)
                {
                    string? fileVer = _readFileVersion(newDll);
                    if (!VersionsMatch(fileVer, expectedVersion))
                    {
                        TryDelete(newDll);
                        return SwapResult.Fail(
                            "Staged DLL FileVersion '" + (fileVer ?? "<unreadable>") +
                            "' does not match expected release version '" + expectedVersion + "'.");
                    }
                }

                // 4. Commit rename 1: live → .old (rename-while-loaded on NTFS).
                _move(liveDll, oldDll);

                // 5. Commit rename 2: .new → live; restore from .old on failure.
                try
                {
                    _move(newDll, liveDll);
                }
                catch (Exception commitEx)
                {
                    bool restored = false;
                    try
                    {
                        _move(oldDll, liveDll);
                        restored = true;
                    }
                    catch (Exception restoreEx)
                    {
                        _logWarn(
                            "CRITICAL: update commit failed and rollback failed. The plugin DLL is currently named '" +
                            UpdatePackage.DllName + OldSuffix + "' and must be renamed back to '" +
                            UpdatePackage.DllName + "' manually. Commit error: " + commitEx.Message +
                            "; restore error: " + restoreEx.Message);
                        return SwapResult.Fail(
                            "Update commit failed and rollback failed. The plugin DLL is currently named '" +
                            UpdatePackage.DllName + OldSuffix + "' and must be renamed back to '" +
                            UpdatePackage.DllName + "' manually. " + commitEx.Message,
                            rolledBack: false,
                            accessDenied: IsAccessDenied(commitEx) || IsAccessDenied(restoreEx));
                    }

                    return SwapResult.Fail(
                        "Update commit failed after renaming the live DLL; install restored from " +
                        UpdatePackage.DllName + OldSuffix + ". " + commitEx.Message,
                        rolledBack: restored,
                        accessDenied: IsAccessDenied(commitEx));
                }

                // 6. Logos are cosmetic — per-file failure is warn-only, not fatal.
                CopyLogosBestEffort(stagingDir, installDir);

                // 7. Only extractor-staged files were touched; installDir\FanaBridge\ is never used.
                return SwapResult.Ok();
            }
            catch (Exception ex)
            {
                // Don't leave a half-written .new behind (a retry would have to
                // delete it anyway, and next launch would sweep it regardless).
                TryDelete(newDll);
                return FailClosed(ex.Message, ex);
            }
        }

        /// <summary>
        /// Best-effort deletion of <c>FanaBridge.dll.old</c> / <c>FanaBridge.dll.new</c> in
        /// <paramref name="installDir"/> and stale <c>FanaBridge-update-*</c> dirs under
        /// <paramref name="tempRoot"/> (skipped when tempRoot is null). Never throws.
        /// </summary>
        public static void CleanupStaleArtifacts(string installDir, string? tempRoot, Action<string>? logWarn)
        {
            Action<string> warn = logWarn ?? (_ => { });
            try
            {
                if (!string.IsNullOrWhiteSpace(installDir))
                {
                    TryDeleteQuiet(Path.Combine(installDir, UpdatePackage.DllName + OldSuffix), warn);
                    TryDeleteQuiet(Path.Combine(installDir, UpdatePackage.DllName + NewSuffix), warn);
                }

                if (string.IsNullOrWhiteSpace(tempRoot) || !Directory.Exists(tempRoot))
                    return;

                foreach (string dir in Directory.GetDirectories(tempRoot, "FanaBridge-update-*"))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        warn("Could not remove stale update staging dir '" + dir + "': " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                warn("CleanupStaleArtifacts: " + ex.Message);
            }
        }

        // Runs strictly AFTER the DLL commit, so nothing in here may surface as a
        // swap failure: a Failed state on a live new DLL would let a retry delete
        // the .old rollback copy. Fully non-throwing, including the warn delegate.
        private void CopyLogosBestEffort(string stagingDir, string installDir)
        {
            try
            {
                string stagedLogos = Path.Combine(stagingDir, UpdatePackage.LogosDirName);
                if (!Directory.Exists(stagedLogos))
                    return;

                string destLogos = Path.Combine(installDir, UpdatePackage.LogosDirName);
                try
                {
                    Directory.CreateDirectory(destLogos);
                }
                catch (Exception ex)
                {
                    WarnQuiet("Could not create DevicesLogos directory: " + ex.Message);
                    return;
                }

                foreach (string src in Directory.GetFiles(stagedLogos, "*.png"))
                {
                    string dest = Path.Combine(destLogos, Path.GetFileName(src));
                    try
                    {
                        _copyOverwrite(src, dest);
                    }
                    catch (Exception ex)
                    {
                        WarnQuiet("Could not copy logo '" + Path.GetFileName(src) + "': " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                WarnQuiet("Logo copy skipped: " + ex.Message);
            }
        }

        private void WarnQuiet(string message)
        {
            try { _logWarn(message); } catch { /* cosmetic step must never throw */ }
        }

        private SwapResult FailClosed(string message, Exception ex)
            => SwapResult.Fail(message, rolledBack: false, accessDenied: IsAccessDenied(ex));

        private void TryDelete(string path)
        {
            try
            {
                if (_exists(path))
                    _delete(path);
            }
            catch (Exception ex)
            {
                _logWarn("Best-effort delete failed for '" + path + "': " + ex.Message);
            }
        }

        private static void TryDeleteQuiet(string path, Action<string> warn)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                warn("Could not delete '" + path + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Compares FileVersion (often four-part) to a release version string by
        /// Major.Minor.Build, treating missing Build as 0.
        /// </summary>
        private static bool VersionsMatch(string? fileVersion, string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(fileVersion))
                return false;
            if (!Version.TryParse(fileVersion, out Version? fileVer) || fileVer == null)
                return false;
            if (!Version.TryParse(expectedVersion, out Version? expected) || expected == null)
                return false;

            int fileBuild = fileVer.Build < 0 ? 0 : fileVer.Build;
            int expBuild = expected.Build < 0 ? 0 : expected.Build;
            return fileVer.Major == expected.Major
                && fileVer.Minor == expected.Minor
                && fileBuild == expBuild;
        }

        private static bool IsAccessDenied(Exception ex)
        {
            if (ex is UnauthorizedAccessException)
                return true;
            if (ex is IOException io && io.HResult == HResultAccessDenied)
                return true;
            return false;
        }
    }
}
