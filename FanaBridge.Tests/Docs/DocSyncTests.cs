using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanaBridge.Protocol.Schema;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Phase 3 — proves the docs can't silently drift from the schema. This is the
    /// drift-test half of the strategy: it parses a hand-written markdown table and
    /// asserts its STRUCTURAL columns (offset/field/type/range) match the
    /// ReportField definition. The Description column stays prose and is not checked,
    /// so cross-reference links and narrative remain hand-editable.
    ///
    /// (Full generation — rewriting the table region from <see cref="TuningPayload.RenderDocTable"/>
    /// — is the natural next step once this guard is green in CI.)
    /// </summary>
    public class DocSyncTests
    {
        // Walk up from the test output dir to find docs/reference/<name>.
        private static string FindDoc(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "docs", "reference", name);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        // Extract the first markdown table whose header row starts with the marker,
        // returning each body row's trimmed cells (header + separator skipped).
        private static List<string[]> ExtractTable(string path, string headerStartsWith)
        {
            var lines = File.ReadAllLines(path);
            int start = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimStart().StartsWith(headerStartsWith)) { start = i; break; }
            if (start < 0) return null;

            var rows = new List<string[]>();
            for (int i = start + 2; i < lines.Length; i++) // skip header + separator
            {
                var l = lines[i].Trim();
                if (!l.StartsWith("|")) break;
                rows.Add(l.Trim('|').Split('|').Select(c => c.Trim()).ToArray());
            }
            return rows;
        }

        [Fact]
        public void TuningPayloadDocTable_StructuralColumnsMatchSchema()
        {
            var path = FindDoc("protocol.md");
            if (path == null) return; // docs not present in this run context — skip

            var rows = ExtractTable(path, "| Offset | Field | Type");
            Assert.NotNull(rows);
            Assert.Equal(TuningPayload.Fields.Count, rows.Count);

            for (int i = 0; i < TuningPayload.Fields.Count; i++)
            {
                var f = TuningPayload.Fields[i];
                var r = rows[i];
                Assert.Equal(f.Offset.ToString(), r[0]);              // Offset
                Assert.Equal(f.Name, r[1]);                            // Field
                Assert.Equal(f.Signed ? "**sbyte**" : "byte", r[2]);   // Type
                Assert.Equal(f.Range, r[3]);                           // Range
            }
        }
    }
}
