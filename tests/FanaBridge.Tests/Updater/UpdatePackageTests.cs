using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FanaBridge.Updater;
using Xunit;

namespace FanaBridge.Tests.Updater
{
    public class UpdatePackageTests
    {
        [Fact]
        public void VerifySha256_Match_CaseInsensitive()
        {
            byte[] data = Encoding.UTF8.GetBytes("hello");
            string hex;
            using (var sha = SHA256.Create())
                hex = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");

            Assert.True(UpdatePackage.VerifySha256(data, hex.ToLowerInvariant()));
            Assert.True(UpdatePackage.VerifySha256(data, hex.ToUpperInvariant()));
            Assert.False(UpdatePackage.VerifySha256(data, new string('0', 64)));
        }

        [Fact]
        public void ExtractToStaging_WhitelistOnly_IgnoresTraversalAndExtras()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            string outsideProbe = Path.Combine(Path.GetTempPath(), "FanaBridge-evil-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "FanaBridge.dll", new byte[] { 1, 2, 3 });
                    WriteEntry(entries, "DevicesLogos/a.png", new byte[] { 4, 5 });
                    WriteEntry(entries, "DevicesLogos/sub/b.png", new byte[] { 6 });
                    WriteEntry(entries, "other.txt", Encoding.UTF8.GetBytes("nope"));
                    WriteEntry(entries, "../evil.dll", new byte[] { 9 });
                    WriteEntry(entries, "..\\evil.dll", new byte[] { 9 });
                    WriteEntry(entries, "DevicesLogos/../../evil.png", new byte[] { 9 });
                    // Bare directory marker — ignored.
                    var dir = entries.CreateEntry("DevicesLogos/");
                    _ = dir;
                });

                IReadOnlyList<string> written = UpdatePackage.ExtractToStaging(zip, staging);

                Assert.Equal(2, written.Count);
                Assert.Contains("FanaBridge.dll", written);
                Assert.Contains("DevicesLogos\\a.png", written);

                // Exact staging contents: only the two allowed files (plus DevicesLogos dir).
                string stagingRoot = Path.GetFullPath(staging)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string[] files = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
                    .Select(p =>
                    {
                        string full = Path.GetFullPath(p);
                        Assert.StartsWith(stagingRoot, full, StringComparison.OrdinalIgnoreCase);
                        return full.Substring(stagingRoot.Length).Replace('/', '\\');
                    })
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                Assert.Equal(new[] { "DevicesLogos\\a.png", "FanaBridge.dll" }, files);

                // Nothing outside staging from traversal names.
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(staging)!, "evil.dll")));
                Assert.False(Directory.Exists(outsideProbe));
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ExtractToStaging_MissingRootDll_Throws()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "DevicesLogos/a.png", new byte[] { 1 });
                });
                var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractToStaging(zip, staging));
                Assert.Contains("FanaBridge.dll", ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ExtractToStaging_DuplicateLogoViaSeparators_Throws()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                // "DevicesLogos/a.png" and "DevicesLogos\a.png" map to the same
                // relative path after separator normalization.
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "FanaBridge.dll", new byte[] { 1 });
                    WriteEntry(entries, "DevicesLogos/a.png", new byte[] { 2 });
                    WriteEntry(entries, "DevicesLogos\\a.png", new byte[] { 3 });
                });

                var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractToStaging(zip, staging));
                Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ExtractToStaging_DuplicateDll_Throws()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                // ZipArchive.CreateEntry does NOT reject duplicate names — a
                // hand-crafted archive can carry two FanaBridge.dll entries.
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "FanaBridge.dll", new byte[] { 1 });
                    WriteEntry(entries, "FanaBridge.dll", new byte[] { 2 });
                });

                var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractToStaging(zip, staging));
                Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ExtractToStaging_TotalSizeCap_Throws()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Three whitelisted entries of exactly 20 MB each (per-entry cap
                // boundary): the cumulative 60 MB crosses the 50 MB total cap
                // during the third entry's streaming copy.
                byte[] twentyMb = new byte[20 * 1024 * 1024];
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "FanaBridge.dll", twentyMb);
                    WriteEntry(entries, "DevicesLogos/a.png", twentyMb);
                    WriteEntry(entries, "DevicesLogos/b.png", twentyMb);
                });

                var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractToStaging(zip, staging));
                Assert.Contains("total", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ExtractToStaging_EntryCountCap_Throws()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "FanaBridge.dll", new byte[] { 1 });
                    for (int i = 0; i < 512; i++)
                        WriteEntry(entries, "pad" + i + ".bin", new byte[] { 0 });
                });
                // 1 + 512 = 513 entries > 512
                var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractToStaging(zip, staging));
                Assert.Contains("too many entries", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        [Trait("Category", "Slow")]
        public void ExtractToStaging_PerEntrySizeCap_Throws()
        {
            string staging = Path.Combine(Path.GetTempPath(), "FanaBridge-pkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                // >20 MB of zeros compresses well in the zip but expands past the cap while streaming.
                var huge = new byte[20 * 1024 * 1024 + 1];
                byte[] zip = BuildZip(entries =>
                {
                    WriteEntry(entries, "FanaBridge.dll", huge);
                });

                var ex = Assert.Throws<InvalidDataException>(() => UpdatePackage.ExtractToStaging(zip, staging));
                Assert.Contains("byte limit", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }
        }

        private static byte[] BuildZip(Action<ZipArchive> populate)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                populate(zip);
            return ms.ToArray();
        }

        private static void WriteEntry(ZipArchive zip, string name, byte[] content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream s = e.Open();
            s.Write(content, 0, content.Length);
        }
    }
}
