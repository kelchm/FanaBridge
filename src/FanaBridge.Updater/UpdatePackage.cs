#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FanaBridge.Updater
{
    /// <summary>
    /// Integrity check and whitelist extraction for a FanaBridge release zip.
    /// Only the merged plugin DLL and top-level DevicesLogos PNGs are ever written;
    /// everything else is ignored, which also defeats zip-slip path traversal.
    /// </summary>
    public static class UpdatePackage
    {
        /// <summary>Root plugin assembly name inside the release zip and install dir.</summary>
        public const string DllName = "FanaBridge.dll";

        /// <summary>Cosmetic device-logo directory name (sibling of the plugin DLL).</summary>
        public const string LogosDirName = "DevicesLogos";

        // Caps are hard-coded: release zips are tiny (one DLL + a handful of PNGs).
        // Sizes are enforced while streaming so zip headers cannot under-report.
        private const int MaxArchiveEntries = 512;
        private const long MaxBytesPerEntry = 20L * 1024 * 1024;
        private const long MaxTotalExtractedBytes = 50L * 1024 * 1024;

        /// <summary>
        /// Returns true when <paramref name="data"/>'s SHA-256 matches
        /// <paramref name="expectedHex"/> (case-insensitive hex, no prefix).
        /// </summary>
        public static bool VerifySha256(byte[] data, string expectedHex)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrEmpty(expectedHex) || expectedHex.Length != 64)
                return false;

            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(data);

            string actual = ToHex(hash);
            return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts ONLY whitelisted entries into <paramref name="stagingDir"/> (created if
        /// needed; caller passes a fresh private dir). Returns relative paths written
        /// (e.g. <c>FanaBridge.dll</c>, <c>DevicesLogos\x.png</c>).
        /// Throws <see cref="InvalidDataException"/> (message = reason) on: no root
        /// FanaBridge.dll entry, duplicate whitelisted entries, caps exceeded, unreadable zip.
        /// </summary>
        public static IReadOnlyList<string> ExtractToStaging(byte[] zipBytes, string stagingDir)
        {
            if (zipBytes == null)
                throw new ArgumentNullException(nameof(zipBytes));
            if (string.IsNullOrWhiteSpace(stagingDir))
                throw new ArgumentException("Staging directory is required.", nameof(stagingDir));

            Directory.CreateDirectory(stagingDir);

            var written = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sawDll = false;
            long totalBytes = 0;

            try
            {
                using var ms = new MemoryStream(zipBytes, writable: false);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

                if (zip.Entries.Count > MaxArchiveEntries)
                    throw new InvalidDataException(
                        "Update package has too many entries (max " + MaxArchiveEntries + ").");

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if (!TryMapWhitelist(entry.FullName, out string? relativePath) || relativePath == null)
                        continue;

                    if (!seen.Add(relativePath))
                        throw new InvalidDataException(
                            "Update package contains duplicate entry '" + relativePath + "'.");

                    string destPath = Path.Combine(stagingDir, relativePath);
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);

                    long entryBytes = 0;
                    using (Stream src = entry.Open())
                    using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            entryBytes += read;
                            totalBytes += read;
                            if (entryBytes > MaxBytesPerEntry)
                                throw new InvalidDataException(
                                    "Update package entry exceeds the " + MaxBytesPerEntry + " byte limit.");
                            if (totalBytes > MaxTotalExtractedBytes)
                                throw new InvalidDataException(
                                    "Update package exceeds the " + MaxTotalExtractedBytes + " byte extracted total.");
                            dst.Write(buffer, 0, read);
                        }
                    }

                    written.Add(relativePath);
                    if (string.Equals(relativePath, DllName, StringComparison.OrdinalIgnoreCase))
                        sawDll = true;
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Update package could not be read: " + ex.Message, ex);
            }

            if (!sawDll)
                throw new InvalidDataException(
                    "Update package is missing root entry '" + DllName + "'.");

            return written;
        }

        /// <summary>
        /// Maps a zip entry full name to a relative install path, or returns false to
        /// ignore the entry. Only exact <c>FanaBridge.dll</c> and single-level
        /// <c>DevicesLogos/&lt;file&gt;.png</c> are accepted.
        /// </summary>
        private static bool TryMapWhitelist(string fullName, out string? relativePath)
        {
            relativePath = null;
            if (string.IsNullOrEmpty(fullName))
                return false;

            // Normalize separators for matching; zip tools on Windows may use '\'.
            string name = fullName.Replace('\\', '/');

            // Skip bare directory markers.
            if (name.EndsWith("/", StringComparison.Ordinal))
                return false;

            if (string.Equals(name, DllName, StringComparison.Ordinal))
            {
                relativePath = DllName;
                return true;
            }

            string prefix = LogosDirName + "/";
            if (name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                string file = name.Substring(prefix.Length);
                // Single level only — reject empty, nested, or traversal segments.
                if (file.Length == 0 || file.IndexOf('/') >= 0 || file.IndexOf('\\') >= 0)
                    return false;
                if (file == "." || file == "..")
                    return false;
                // Names that aren't valid Windows file names (':' in particular
                // has drive-qualifier semantics in legacy path handling) are not
                // logos — ignore rather than rely on FileStream rejecting them.
                if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return false;

                relativePath = LogosDirName + "\\" + file;
                return true;
            }

            return false;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
