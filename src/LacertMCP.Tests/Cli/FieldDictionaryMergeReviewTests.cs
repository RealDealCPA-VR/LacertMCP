using System;
using System.IO;
using System.Linq;
using LacertMCP.Cli.Update;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>R8-review — dictionary merge: case-insensitive ids, conflicts kept aside.</summary>
    public sealed class FieldDictionaryMergeReviewTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lacerte-mcp-dictmerge2-" + Guid.NewGuid().ToString("N"));
        private string Installed => Path.Combine(_root, "installed");
        private string Release => Path.Combine(_root, "release");

        public FieldDictionaryMergeReviewTests()
        {
            Directory.CreateDirectory(Installed);
            Directory.CreateDirectory(Release);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }

        private static FieldDictionaryEntry Field(string id, string field, params int[] years)
            => new FieldDictionaryEntry(id, "concept", "1120", null, FieldDictionaryLoader.KindField,
                1, field, null, null, null, null, false, false, "amount", null, years, "TY2024 on PC", FieldDictionaryLoader.SourceMapDiff);

        private void Write(string dir, params FieldDictionaryEntry[] e)
            => File.WriteAllText(Path.Combine(dir, "corporate.json"), FieldDictionaryLoader.Serialize('C', e));

        [Fact]
        public void IdDifferingOnlyInCase_IsNotADuplicate_UpdateStillMerges()
        {
            Write(Installed, Field("1120.Gross_Receipts", "C1_40", 2025));
            Write(Release, Field("1120.gross_receipts", "C1_40", 2024));

            var result = FieldDictionaryMerge.Merge(Installed, Release);   // must not throw

            var merged = FieldDictionaryLoader.ValidateFile(Path.Combine(Release, "corporate.json"), 'C', out var err);
            Assert.True(merged != null, err);
            Assert.Single(merged!.Entries);
            Assert.Single(result.Conflicts);           // different years: reported, not silently dropped
            Assert.Single(result.SideFiles);
        }

        [Fact]
        public void SameIdDifferentCell_KeepsThisPcCopyAside_ReleaseIsUsed()
        {
            Write(Installed, Field("1120.gross_receipts", "C1_41", 2024));   // corrected here
            Write(Release, Field("1120.gross_receipts", "C1_40", 2024));

            var result = FieldDictionaryMerge.Merge(Installed, Release);

            var merged = FieldDictionaryLoader.ValidateFile(Path.Combine(Release, "corporate.json"), 'C', out _)!;
            Assert.Equal("C1_40", Assert.Single(merged.Entries).Field);
            var side = Assert.Single(result.SideFiles);
            Assert.Contains("C1_41", File.ReadAllText(side));
            Assert.Contains(result.Describe(), l => l.Contains("kept for review"));
        }

        [Fact]
        public void SameIdSameMapping_IsSuperseded_NoConflict()
        {
            Write(Installed, Field("1120.gross_receipts", "C1_40", 2024));
            Write(Release, Field("1120.gross_receipts", "C1_40", 2024));
            var result = FieldDictionaryMerge.Merge(Installed, Release);
            Assert.Equal(1, result.Superseded);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.SideFiles);
        }

        [Fact]
        public void LeadingZeroField_IsRejectedByTheLoader()
        {
            var json = FieldDictionaryLoader.Serialize('C', new[] { Field("1120.x", "C1_37", 2024) }).Replace("C1_37", "C1_037");
            Assert.ThrowsAny<Exception>(() => FieldDictionaryLoader.Parse(json, 'C'));
        }
    }
}
