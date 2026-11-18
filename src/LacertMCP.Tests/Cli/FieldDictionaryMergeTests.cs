using System;
using System.IO;
using System.Linq;
using LacertMCP.Cli.Commands;
using LacertMCP.Cli.Update;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>
    /// Field-dictionary entries recorded on the Lacerte PC (map-diff record) must
    /// survive `lacerte-mcp update` and an install-http.ps1 re-run.
    /// </summary>
    public sealed class FieldDictionaryMergeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _installed;
        private readonly string _release;

        public FieldDictionaryMergeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "lacerte-mcp-dictmerge-" + Guid.NewGuid().ToString("N"));
            _installed = Directory.CreateDirectory(Path.Combine(_root, "installed")).FullName;
            _release = Directory.CreateDirectory(Path.Combine(_root, "release")).FullName;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        private static FieldDictionaryEntry Field(string id, int table, string field, string verified = "TY2024 on LACERTE-PC, 2026-11-20")
            => new FieldDictionaryEntry(id, "concept " + id, "1120", null, FieldDictionaryLoader.KindField,
                table, field, null, null, null, null, false, false, "amount", null, new[] { 2024 }, verified, FieldDictionaryLoader.SourceMapDiff);

        private void Write(string dir, char module, params FieldDictionaryEntry[] entries)
            => File.WriteAllText(Path.Combine(dir, Modules.FileBasenameFor(module) + ".json"), FieldDictionaryLoader.Serialize(module, entries));

        private FieldDictionaryFile Load(string dir, char module)
        {
            var f = FieldDictionaryLoader.ValidateFile(Path.Combine(dir, Modules.FileBasenameFor(module) + ".json"), module, out var error);
            Assert.True(f != null, error);
            return f!;
        }

        [Fact]
        public void RecordedEntries_AreKept_IntoAnEmptyReleaseDictionary()
        {
            Write(_installed, 'C', Field("1120.gross_receipts", 1, "C1_40"), Field("1120.cogs", 1, "C1_41"));
            Write(_release, 'C');

            var result = FieldDictionaryMerge.Merge(_installed, _release);

            Assert.Equal(2, result.Kept);
            Assert.Equal(new[] { "1120.cogs", "1120.gross_receipts" }, Load(_release, 'C').Entries.Select(e => e.Id).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void SameId_ReleaseWins_AndConflictingCell_IsDroppedAndReported()
        {
            Write(_installed, 'C',
                Field("1120.gross_receipts", 1, "C1_40", "old"),
                Field("1120.local_name", 1, "C1_50"),     // same cell as a release entry, other id
                Field("1120.extra", 1, "C1_60"));
            Write(_release, 'C',
                Field("1120.gross_receipts", 1, "C1_40", "reviewed"),
                Field("1120.officer_comp", 1, "C1_50"));

            var result = FieldDictionaryMerge.Merge(_installed, _release);

            var merged = Load(_release, 'C').Entries.ToDictionary(e => e.Id);
            Assert.Equal("reviewed", merged["1120.gross_receipts"].VerifiedOn);
            Assert.True(merged.ContainsKey("1120.officer_comp"));
            Assert.True(merged.ContainsKey("1120.extra"));
            Assert.False(merged.ContainsKey("1120.local_name"));
            Assert.Equal(1, result.Kept);
            Assert.Equal(1, result.Superseded);
            Assert.Contains(result.Describe(), l => l.Contains("1120.local_name"));
        }

        [Fact]
        public void InvalidInstalledFile_TakesTheReleaseCopy()
        {
            File.WriteAllText(Path.Combine(_installed, "corporate.json"), "{ not json");
            Write(_release, 'C', Field("1120.gross_receipts", 1, "C1_40"));

            var result = FieldDictionaryMerge.Merge(_installed, _release);

            Assert.Single(result.Unreadable);
            Assert.Single(Load(_release, 'C').Entries);
        }

        [Fact]
        public void MergeDictionaryVerb_ReportsAndExitsZero()
        {
            Write(_installed, 'F', new FieldDictionaryEntry("1041.fiduciary_fee", "Fiduciary fees", "1041", "12", FieldDictionaryLoader.KindField,
                1, "C1_30", null, null, null, null, false, false, "amount", null, null, "TY2024 on LACERTE-PC", FieldDictionaryLoader.SourceMapDiff));
            Write(_release, 'F');
            var outW = new StringWriter();
            var errW = new StringWriter();

            var code = FieldAccessCommand.RunMergeDictionary(_installed, _release, outW, errW);

            Assert.True(code == 0, errW.ToString());
            Assert.Contains("Kept 1 field-dictionary entry", outW.ToString());
        }
    }
}
