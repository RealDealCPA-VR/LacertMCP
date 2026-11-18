using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LacertMCP.Cli.Commands;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Schema;
using Xunit;

namespace LacertMCP.Tests.Cli
{
    /// <summary>FP-3 — <c>lacerte-mcp map-diff snapshot | compare | record</c>.</summary>
    [Collection(CliCollection.Name)]
    public sealed class MapDiffCommandTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dictDir;

        public MapDiffCommandTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "lacerte-mapdiff-" + Guid.NewGuid().ToString("N"));
            _dictDir = Path.Combine(_dir, "FieldDictionary");
            Directory.CreateDirectory(_dictDir);
            foreach (var m in Modules.All)
            {
                File.WriteAllText(Path.Combine(_dictDir, m.FileBasename + ".json"), FieldDictionaryLoader.Serialize(m.Char, Array.Empty<FieldDictionaryEntry>()));
            }
        }

        public void Dispose()
        {
            FieldDictionaryLoader.ReloadCaches();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ---- fixtures ---------------------------------------------------------------------

        /// <summary>Hand-written "before" snapshot of corporate test client TEST1 (TY2024).</summary>
        private const string BeforeJson = @"{
  ""format"": ""lacerte-mcp/map-diff-snapshot/1"",
  ""module"": ""C"",
  ""taxYear"": 2024,
  ""client"": ""TEST1"",
  ""takenAtUtc"": ""2026-11-18T10:00:00Z"",
  ""machine"": ""TEST-PC"",
  ""dataPath"": ""C:\\Lacerte\\24tax\\CData"",
  ""tableForm"": ""ty21"",
  ""rows"": [
    { ""table"": 1, ""rowKey"": ""client"", ""cells"": { ""C1_0"": ""TEST1"", ""C1_1"": ""Test Corp"", ""C1_37"": ""0"", ""C1_40"": null, ""C1_G7"": ""2026-11-01"" } },
    { ""table"": 3, ""rowKey"": ""state=CA"", ""state"": ""CA"", ""cells"": { ""C3_0"": ""TEST1"", ""C3_G1"": ""CA"", ""C3_5"": ""0"" } }
  ],
  ""detailRows"": [
    { ""series"": ""11"", ""prefix"": ""1"", ""code"": ""1"", ""suffix"": ""1000"", ""stateAbbr"": """", ""cityAbbr"": """", ""source"": """", ""tsj"": ""Taxpayer"", ""importSrc"": """", ""amount"": ""100"", ""description"": """" }
  ],
  ""readErrors"": {}
}";

        private ClientSnapshot Before() => ClientSnapshotReader.FromJson(BeforeJson);

        private string Save(string name, ClientSnapshot s)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllText(p, ClientSnapshotReader.ToJson(s));
            return p;
        }

        private static SnapshotTableRow Row(ClientSnapshot s, int table, string key) => s.Rows.Single(r => r.Table == table && r.RowKey == key);

        private (string before, string after) Pair(Action<ClientSnapshot> mutateAfter, Action<ClientSnapshot>? mutateBefore = null)
        {
            var b = Before();
            mutateBefore?.Invoke(b);
            var a = Before();
            a.TakenAtUtc = "2026-11-18T10:05:00Z";
            mutateAfter(a);
            return (Save("before-" + Guid.NewGuid().ToString("N") + ".json", b), Save("after-" + Guid.NewGuid().ToString("N") + ".json", a));
        }

        private MapDiffCommand.RecordOptions Opts(string before, string after, string id = "1120.gross_receipts")
            => new MapDiffCommand.RecordOptions
            {
                BeforePath = before,
                AfterPath = after,
                Id = id,
                Concept = "Gross receipts or sales",
                Form = "1120",
                Line = "1a",
                SchemaDir = _dictDir,
                MachineName = "TEST-PC",
                Today = new DateTime(2026, 11, 20),
                ExeDir = _dir,
            };

        private IReadOnlyList<FieldDictionaryEntry> Corporate()
        {
            var parsed = FieldDictionaryLoader.ValidateFile(Path.Combine(_dictDir, "corporate.json"), 'C', out var err);
            Assert.True(parsed != null, err);
            return parsed!.Entries;
        }

        private static (int exit, string stdout, string stderr) Run(Func<TextWriter, TextWriter, int> f)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var exit = f(o, e);
            return (exit, o.ToString(), e.ToString());
        }

        // ---- snapshot ---------------------------------------------------------------------

        private sealed class FakeSource : ISnapshotQuerySource
        {
            public readonly List<string> Queries = new List<string>();
            public readonly List<(string match, Func<IReadOnlyList<IReadOnlyDictionary<string, object?>>> rows)> Routes
                = new List<(string, Func<IReadOnlyList<IReadOnlyDictionary<string, object?>>>)>();
            public bool Disposed;

            public FakeSource On(string sqlFragment, params Dictionary<string, object?>[] rows)
            {
                Routes.Add((sqlFragment, () => rows));
                return this;
            }

            public FakeSource Fail(string sqlFragment)
            {
                Routes.Add((sqlFragment, () => throw new LacerteSDKError(StatusCodes.DBFNotAccessable)));
                return this;
            }

            public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql)
            {
                Queries.Add(sql);
                foreach (var (match, rows) in Routes)
                {
                    if (sql.IndexOf(match, StringComparison.Ordinal) >= 0) return rows();
                }
                return Array.Empty<IReadOnlyDictionary<string, object?>>();
            }

            public void Dispose() => Disposed = true;
        }

        private static Dictionary<string, object?> R(params (string k, object? v)[] cells)
            => cells.ToDictionary(c => c.k, c => c.v);

        [Fact]
        public void Snapshot_Ty21_ReadsPerClientTables_AndWritesCanonicalJson()
        {
            var fake = new FakeSource()
                .On("[DATA1-Client Info]", R(("C1_0", "TEST1   "), ("C1_1", "Test Corp  "), ("C1_37", 125000.00m), ("C1_G7", new DateTime(2026, 11, 1)), ("C1_40", DBNull.Value)))
                .On("[TEST1-SubClientInfo]", R(("C2_1", 2m), ("C2_3", "Officer")))
                .On("[TEST1-Detail-ClientStateInfo]", R(("C3_G1", "ca"), ("C3_5", 1.5)))
                .On("[TEST1-Detail]", R(("Series", 11), ("Prefix", 1), ("Code", 1), ("Suffix", 1000), ("StateAbbr", ""), ("CityAbbr", ""),
                    ("Source", ""), ("TSJ", "Taxpayer"), ("ImportSrc", ""), ("Amount", 100.25), ("Description", "W-2   ")));
            string? cs = null;
            var outPath = Path.Combine(_dir, "snap.json");
            var (exit, stdout, stderr) = Run((o, e) => MapDiffCommand.RunSnapshot("c", 2024, "test1", @"C:\Lacerte\24tax\CData", outPath,
                "auto", false, c => { cs = c; return fake; }, new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), "TEST-PC", o, e));

            Assert.True(exit == 0, stderr);
            Assert.True(fake.Disposed);
            Assert.Contains("ReturnType=Corporate", cs);
            Assert.Contains("TaxYear=2024", cs);
            Assert.Contains(fake.Queries, q => q.Contains("[TEST1-SubClientInfo]"));
            Assert.Contains(fake.Queries, q => q.Contains("[TEST1-Detail-SubClientStateInfo]"));
            Assert.DoesNotContain(fake.Queries, q => q.Contains("[DATA2-Subclient Info]"));
            Assert.Contains("contains the client's return data", stdout);

            var snap = ClientSnapshotReader.FromJson(File.ReadAllText(outPath));
            Assert.Equal("C", snap.Module);
            Assert.Equal("TEST1", snap.Client);
            Assert.Equal(ClientSnapshotReader.TableFormTy21, snap.TableForm);
            Assert.Equal("2026-11-18T10:00:00Z", snap.TakenAtUtc);
            var d1 = Row(snap, 1, "client");
            Assert.Equal("TEST1", d1.Cells["C1_0"]);
            Assert.Equal("Test Corp", d1.Cells["C1_1"]);
            Assert.Equal("125000", d1.Cells["C1_37"]);
            Assert.Equal("2026-11-01", d1.Cells["C1_G7"]);
            Assert.Null(d1.Cells["C1_40"]);
            Assert.Equal("2", Row(snap, 2, "sub=2").SubClient);
            Assert.Equal("1.5", Row(snap, 3, "state=CA").Cells["C3_5"]);
            var det = Assert.Single(snap.DetailRows);
            Assert.Equal("11", det.Series);
            Assert.Equal("100.25", det.Amount);
            Assert.Equal("W-2", det.Description);
            Assert.Empty(snap.ReadErrors);
        }

        [Fact]
        public void Snapshot_PreTy21_UsesGlobalTablesFilteredByClient()
        {
            var fake = new FakeSource()
                .On("[DATA1-Client Info]", R(("C1_0", "TEST1")))
                .On("[DATA4-State Subclient Info]", R(("C4_0", "TEST1"), ("C4_G1", "NY"), ("C4_1", 1), ("C4_2", "x")));
            var outPath = Path.Combine(_dir, "snap20.json");
            var (exit, _, stderr) = Run((o, e) => MapDiffCommand.RunSnapshot("P", 2020, "TEST1", @"C:\Lacerte\20tax\PData", outPath,
                "auto", false, c => fake, DateTime.UtcNow, "TEST-PC", o, e));
            Assert.True(exit == 0, stderr);
            Assert.Contains(fake.Queries, q => q.Contains("[DATA2-Subclient Info] where C2_0 = 'TEST1'"));
            Assert.Contains(fake.Queries, q => q.Contains("[DATA3-State Client Info] where C3_0 = 'TEST1'"));
            Assert.Contains(fake.Queries, q => q.Contains("[DATA4-State Subclient Info] where C4_0 = 'TEST1'"));
            var snap = ClientSnapshotReader.FromJson(File.ReadAllText(outPath));
            Assert.Equal(ClientSnapshotReader.TableFormPreTy21, snap.TableForm);
            Assert.Equal("NY", Row(snap, 4, "state=NY|sub=1").State);
        }

        [Fact]
        public void Snapshot_TableFormOverride_And_ReadErrorsAreRecorded()
        {
            var fake = new FakeSource()
                .On("[DATA1-Client Info]", R(("C1_0", "TEST1")))
                .Fail("[DATA2-Subclient Info]")
                .Fail("[TEST1-Detail]");
            var outPath = Path.Combine(_dir, "snap-err.json");
            var (exit, stdout, stderr) = Run((o, e) => MapDiffCommand.RunSnapshot("C", 2024, "TEST1", @"C:\x", outPath,
                "pre-ty21", false, c => fake, DateTime.UtcNow, "TEST-PC", o, e));
            Assert.True(exit == 0, stderr);
            Assert.Contains("DATA2: NOT READ", stdout);
            Assert.Contains("detail: NOT READ", stdout);
            var snap = ClientSnapshotReader.FromJson(File.ReadAllText(outPath));
            Assert.Equal(ClientSnapshotReader.TableFormPreTy21, snap.TableForm);
            Assert.Equal(new[] { "DATA2", "detail" }, snap.ReadErrors.Keys.ToArray());
            Assert.Contains("DBFNotAccessable", snap.ReadErrors["DATA2"]);
        }

        [Fact]
        public void Snapshot_Refuses_MissingClient_BadClient_BadForm_AndOverwrite()
        {
            var outPath = Path.Combine(_dir, "s.json");
            var missing = Run((o, e) => MapDiffCommand.RunSnapshot("C", 2024, "NOPE", @"C:\x", outPath, "auto", false, c => new FakeSource(), DateTime.UtcNow, "PC", o, e));
            Assert.Equal(1, missing.exit);
            Assert.Contains("not found in DATA1", missing.stderr);
            Assert.False(File.Exists(outPath));

            var bad = Run((o, e) => MapDiffCommand.RunSnapshot("C", 2024, "bad client;", @"C:\x", outPath, "auto", false, c => new FakeSource(), DateTime.UtcNow, "PC", o, e));
            Assert.Equal(1, bad.exit);
            Assert.Contains("InValidClientName", bad.stderr);

            var form = Run((o, e) => MapDiffCommand.RunSnapshot("C", 2024, "TEST1", @"C:\x", outPath, "newest", false, c => new FakeSource(), DateTime.UtcNow, "PC", o, e));
            Assert.Equal(1, form.exit);

            var module = Run((o, e) => MapDiffCommand.RunSnapshot("Z", 2024, "TEST1", @"C:\x", outPath, "auto", false, c => new FakeSource(), DateTime.UtcNow, "PC", o, e));
            Assert.Equal(1, module.exit);

            File.WriteAllText(outPath, "precious");
            var ok = new FakeSource().On("[DATA1-Client Info]", R(("C1_0", "TEST1")));
            var overwrite = Run((o, e) => MapDiffCommand.RunSnapshot("C", 2024, "TEST1", @"C:\x", outPath, "auto", false, c => ok, DateTime.UtcNow, "PC", o, e));
            Assert.Equal(1, overwrite.exit);
            Assert.Contains("--overwrite", overwrite.stderr);
            Assert.Equal("precious", File.ReadAllText(outPath));
            Assert.Equal(0, Run((o, e) => MapDiffCommand.RunSnapshot("C", 2024, "TEST1", @"C:\x", outPath, "auto", true, c => ok, DateTime.UtcNow, "PC", o, e)).exit);
        }

        // ---- compare ----------------------------------------------------------------------

        [Fact]
        public void Compare_ListsChangedCells_RowsAndDetailRows()
        {
            var (b, a) = Pair(s =>
            {
                Row(s, 1, "client").Cells["C1_37"] = "123457";
                s.Rows.Add(new SnapshotTableRow { Table = 3, RowKey = "state=NY", State = "NY", Cells = { ["C3_0"] = "TEST1", ["C3_G1"] = "NY", ["C3_5"] = "7", ["C3_6"] = "0" } });
                s.DetailRows[0].Amount = "250";
                s.DetailRows.Add(new SnapshotDetailRow { Series = "40", Prefix = "1", Code = "4", Suffix = "1000", Amount = "5000" });
            });
            var text = Run((o, e) => MapDiffCommand.RunCompare(b, a, false, Array.Empty<string>(), o, e));
            Assert.Equal(0, text.exit);
            Assert.Contains("DATA1 [client] C1_37: 0 -> 123457", text.stdout);
            Assert.Contains("DATA3 [state=NY] row added", text.stdout);
            Assert.Contains("DATA3 [state=NY] C3_5: (null) -> 7", text.stdout);
            Assert.DoesNotContain("C3_6", text.stdout);   // zero default in a new row
            Assert.DoesNotContain("C3_G1", text.stdout);  // key column of a new row
            Assert.Contains("detail changed: Series 11 Prefix 1 Code 1 Suffix 1000 TSJ Taxpayer Amount 100", text.stdout);
            Assert.Contains("Amount 250", text.stdout);
            Assert.Contains("detail added:   Series 40 Prefix 1 Code 4 Suffix 1000 Amount 5000", text.stdout);
            Assert.Contains("Summary: 2 field cell change(s), 1 row(s) added, 0 row(s) removed, 1 detail row(s) added, 1 changed, 0 removed.", text.stdout);

            var json = Run((o, e) => MapDiffCommand.RunCompare(b, a, true, new[] { "c1_37" }, o, e));
            Assert.Equal(0, json.exit);
            var root = JsonDocument.Parse(json.stdout).RootElement;
            var cell = Assert.Single(root.GetProperty("cellChanges").EnumerateArray());
            Assert.Equal("C3_5", cell.GetProperty("field").GetString());
            Assert.Equal("NY", cell.GetProperty("state").GetString());
            Assert.True(cell.GetProperty("inAddedRow").GetBoolean());
            Assert.Equal("C1_37", Assert.Single(root.GetProperty("ignoredFields").EnumerateArray()).GetString());
            Assert.Single(root.GetProperty("detailAdded").EnumerateArray());
            Assert.Equal("250", Assert.Single(root.GetProperty("detailChanged").EnumerateArray()).GetProperty("after").GetProperty("amount").GetString());
        }

        [Fact]
        public void Compare_DetailRemoved_And_DuplicateRowsCompareAsMultiset()
        {
            var (b, a) = Pair(
                s => { s.DetailRows.Clear(); s.DetailRows.Add(Dup()); s.DetailRows.Add(Dup()); },
                s => { s.DetailRows.Add(Dup()); s.DetailRows.Add(Dup()); });
            var r = Run((o, e) => MapDiffCommand.RunCompare(b, a, false, Array.Empty<string>(), o, e));
            Assert.Contains("detail removed: Series 11 Prefix 1 Code 1", r.stdout);
            Assert.Contains("0 detail row(s) added, 0 changed, 1 removed", r.stdout);

            static SnapshotDetailRow Dup() => new SnapshotDetailRow { Series = "20", Prefix = "1", Code = "3", Suffix = "1000", Amount = "10" };
        }

        [Fact]
        public void Compare_MissingOrInvalidFile_Exit1()
        {
            Assert.Equal(1, Run((o, e) => MapDiffCommand.RunCompare(Path.Combine(_dir, "nope.json"), Path.Combine(_dir, "nope2.json"), false, Array.Empty<string>(), o, e)).exit);
            var junk = Path.Combine(_dir, "junk.json");
            File.WriteAllText(junk, "{\"format\":\"other\"}");
            var r = Run((o, e) => MapDiffCommand.RunCompare(junk, junk, false, Array.Empty<string>(), o, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("not a valid map-diff snapshot", r.stderr);
        }

        // ---- record -----------------------------------------------------------------------

        [Fact]
        public void Record_SingleFieldChange_WritesEntry()
        {
            var (b, a) = Pair(s => Row(s, 1, "client").Cells["C1_37"] = "123457");
            var r = Run((o, e) => MapDiffCommand.RunRecord(Opts(b, a), o, e));
            Assert.True(r.exit == 0, r.stderr);
            Assert.Contains("corporate.json", r.stdout);
            Assert.Contains("DATA1 [client] C1_37: 0 -> 123457", r.stdout);
            Assert.Contains("lacerte_reload_schema", r.stdout);

            var e0 = Assert.Single(Corporate());
            Assert.Equal("1120.gross_receipts", e0.Id);
            Assert.Equal("Gross receipts or sales", e0.Concept);
            Assert.Equal("1120", e0.Form);
            Assert.Equal("1a", e0.Line);
            Assert.Equal(FieldDictionaryLoader.KindField, e0.Kind);
            Assert.Equal(1, e0.Table);
            Assert.Equal("C1_37", e0.Field);
            Assert.False(e0.NeedsSubclient);
            Assert.False(e0.NeedsState);
            Assert.Equal("amount", e0.DataType);
            Assert.Equal(new[] { 2024 }, e0.TaxYears.ToArray());
            Assert.Equal("TY2024 on TEST-PC, 2026-11-20", e0.VerifiedOn);
            Assert.Equal(FieldDictionaryLoader.SourceMapDiff, e0.Source);
            // Stable file format.
            var text = File.ReadAllText(Path.Combine(_dictDir, "corporate.json"));
            Assert.Equal(FieldDictionaryLoader.Serialize('C', Corporate()), text);
        }

        [Fact]
        public void Record_StateRowAdded_WithOneValue_NeedsState()
        {
            var (b, a) = Pair(s => s.Rows.Add(new SnapshotTableRow { Table = 3, RowKey = "state=NY", State = "NY", Cells = { ["C3_0"] = "TEST1", ["C3_G1"] = "NY", ["C3_5"] = "123457" } }));
            var o = Opts(b, a, "1120.ny_thing");
            o.DataType = "integer";
            o.Expect = "123,457";
            var r = Run((w, e) => MapDiffCommand.RunRecord(o, w, e));
            Assert.True(r.exit == 0, r.stderr);
            var e0 = Assert.Single(Corporate());
            Assert.Equal(3, e0.Table);
            Assert.Equal("C3_5", e0.Field);
            Assert.True(e0.NeedsState);
            Assert.False(e0.NeedsSubclient);
            Assert.Equal("integer", e0.DataType);
        }

        [Fact]
        public void Record_DetailRowAdded_WritesDetailEntry()
        {
            var (b, a) = Pair(s => s.DetailRows.Add(new SnapshotDetailRow
            {
                Series = "40", Prefix = "1", Code = "4", Suffix = "1000", StateAbbr = "CA", Amount = "123457", Description = "",
            }));
            var o = Opts(b, a, "1120.k1.box4");
            o.Expect = "123457";
            o.Notes = "Prefix numbers the partner";
            var r = Run((w, e) => MapDiffCommand.RunRecord(o, w, e));
            Assert.True(r.exit == 0, r.stderr);
            Assert.Contains("detail row added", r.stdout);
            var e0 = Assert.Single(Corporate());
            Assert.Equal(FieldDictionaryLoader.KindDetail, e0.Kind);
            Assert.Equal(40, e0.Series);
            Assert.Equal(4, e0.Code);
            Assert.Equal(1, e0.Prefix);
            Assert.Equal(1000, e0.Suffix);
            Assert.True(e0.NeedsState);
            Assert.Equal("amount", e0.DataType);
            Assert.Equal("Prefix numbers the partner", e0.Notes);
            Assert.Null(e0.Table);
        }

        [Fact]
        public void Record_DetailRowChanged_IsAccepted()
        {
            var (b, a) = Pair(s => s.DetailRows[0].Amount = "999");
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a, "1120.wages"), w, e));
            Assert.True(r.exit == 0, r.stderr);
            Assert.Contains("detail row changed", r.stdout);
            Assert.Equal(11, Assert.Single(Corporate()).Series);
        }

        [Fact]
        public void Record_RefusesMoreThanOneChange_AndListsThem()
        {
            var (b, a) = Pair(s =>
            {
                Row(s, 1, "client").Cells["C1_37"] = "123457";
                Row(s, 1, "client").Cells["C1_G7"] = "2026-11-18";
            });
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("exactly ONE changed field cell", r.stderr);
            Assert.Contains("C1_37: 0 -> 123457", r.stderr);
            Assert.Contains("C1_G7: 2026-11-01 -> 2026-11-18", r.stderr);
            Assert.Empty(Corporate());

            // A bookkeeping column can be left out explicitly.
            var o = Opts(b, a);
            o.Ignore = new[] { "C1_G7" };
            Assert.Equal(0, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);
            Assert.Equal("C1_37", Assert.Single(Corporate()).Field);
        }

        [Fact]
        public void Record_RefusesFieldPlusDetail_RemovalsAndClearedValues()
        {
            var both = Pair(s => { Row(s, 1, "client").Cells["C1_37"] = "5"; s.DetailRows[0].Amount = "5"; });
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(Opts(both.before, both.after), w, e)).exit);

            var removed = Pair(s => s.DetailRows.Clear());
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(removed.before, removed.after), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("detail removed", r.stderr);

            var cleared = Pair(s => Row(s, 1, "client").Cells["C1_1"] = "");
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(Opts(cleared.before, cleared.after), w, e)).exit);

            var rowGone = Pair(s => s.Rows.RemoveAll(x => x.Table == 3));
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(Opts(rowGone.before, rowGone.after), w, e)).exit);
            Assert.Empty(Corporate());
        }

        [Fact]
        public void Record_RefusesNothingChanged()
        {
            var (b, a) = Pair(s => { });
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("nothing changed", r.stderr);
        }

        [Fact]
        public void Record_RefusesDifferentClientsOrForms()
        {
            var (b, a) = Pair(s => { s.Client = "TEST2"; Row(s, 1, "client").Cells["C1_37"] = "1"; });
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("not of the same client", r.stderr);

            var (b2, a2) = Pair(s => { s.TableForm = "pre-ty21"; Row(s, 1, "client").Cells["C1_37"] = "1"; });
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(Opts(b2, a2), w, e)).exit);
        }

        [Fact]
        public void Record_ReadErrors_RefuseUnlessAllowedWithExpect()
        {
            var (b, a) = Pair(
                s => { s.ReadErrors["DATA2"] = "9 DBFNotAccessable"; Row(s, 1, "client").Cells["C1_37"] = "123457"; },
                s => s.ReadErrors["DATA2"] = "9 DBFNotAccessable");
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("reads failed", r.stderr);
            Assert.Contains("DATA2", r.stderr);

            var o = Opts(b, a);
            o.AllowReadErrors = true;
            var noExpect = Run((w, e) => MapDiffCommand.RunRecord(o, w, e));
            Assert.Equal(1, noExpect.exit);
            Assert.Contains("requires --expect", noExpect.stderr);

            o.Expect = "123457.00";
            var ok = Run((w, e) => MapDiffCommand.RunRecord(o, w, e));
            Assert.True(ok.exit == 0, ok.stderr);
            Assert.Contains("Warning: recording despite unreadable", ok.stdout);
        }

        [Fact]
        public void Record_ExpectMismatch_Refuses()
        {
            var (b, a) = Pair(s => Row(s, 1, "client").Cells["C1_37"] = "123457");
            var o = Opts(b, a);
            o.Expect = "123458";
            var r = Run((w, e) => MapDiffCommand.RunRecord(o, w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("does not hold the expected value", r.stderr);
            Assert.Empty(Corporate());
        }

        [Fact]
        public void Record_SameMappingAnotherYear_WidensTaxYears()
        {
            var (b, a) = Pair(s => Row(s, 1, "client").Cells["C1_37"] = "123457");
            Assert.Equal(0, Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e)).exit);
            var (b25, a25) = Pair(s => { s.TaxYear = 2025; Row(s, 1, "client").Cells["C1_37"] = "123457"; }, s => s.TaxYear = 2025);
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b25, a25), w, e));
            Assert.True(r.exit == 0, r.stderr);
            var e0 = Assert.Single(Corporate());
            Assert.Equal(new[] { 2024, 2025 }, e0.TaxYears.ToArray());
            Assert.Equal("TY2025 on TEST-PC, 2026-11-20", e0.VerifiedOn);
        }

        [Fact]
        public void Record_SameIdDifferentTarget_RefusesUnlessReplace()
        {
            var (b, a) = Pair(s => Row(s, 1, "client").Cells["C1_37"] = "123457");
            Assert.Equal(0, Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e)).exit);

            var (b2, a2) = Pair(s => Row(s, 1, "client").Cells["C1_40"] = "123457");
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b2, a2), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("already maps to DATA1 C1_37", r.stderr);
            Assert.Equal("C1_37", Assert.Single(Corporate()).Field);

            var o = Opts(b2, a2);
            o.Replace = true;
            Assert.Equal(0, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);
            Assert.Equal("C1_40", Assert.Single(Corporate()).Field);
        }

        [Fact]
        public void Record_TargetAlreadyMappedUnderAnotherId_Refuses()
        {
            var (b, a) = Pair(s => Row(s, 1, "client").Cells["C1_37"] = "123457");
            Assert.Equal(0, Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e)).exit);
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a, "1120.sales"), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("already mapped as '1120.gross_receipts'", r.stderr);

            // --all-years on a different id would overlap too.
            var o = Opts(b, a, "1120.sales");
            o.AllYears = true;
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);
            Assert.Single(Corporate());
        }

        [Fact]
        public void Record_RefusesInvalidInputs_AndInvalidExistingFile()
        {
            var (b, a) = Pair(s => Row(s, 1, "client").Cells["C1_37"] = "123457");
            var o = Opts(b, a, "bad id");
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);
            o = Opts(b, a);
            o.DataType = "money";
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);
            o = Opts(b, a);
            o.Concept = " ";
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);

            File.WriteAllText(Path.Combine(_dictDir, "corporate.json"), "{ broken");
            var r = Run((w, e) => MapDiffCommand.RunRecord(Opts(b, a), w, e));
            Assert.Equal(1, r.exit);
            Assert.Contains("is invalid", r.stderr);
            Assert.Equal("{ broken", File.ReadAllText(Path.Combine(_dictDir, "corporate.json")));

            o = Opts(b, a);
            o.SchemaDir = Path.Combine(_dir, "does-not-exist");
            Assert.Equal(1, Run((w, e) => MapDiffCommand.RunRecord(o, w, e)).exit);
        }

        [Fact]
        public void ResolveDictionaryDir_ExplicitAndReleaseLayouts()
        {
            // Explicit: an install folder containing Schema\FieldDictionary.
            var install = Path.Combine(_dir, "install", "server");
            var nested = Path.Combine(install, "Schema", "FieldDictionary");
            Directory.CreateDirectory(nested);
            Assert.Equal(Path.GetFullPath(nested), MapDiffCommand.ResolveDictionaryDir(install, _dir).Dir);
            // Explicit: a Schema folder, and the folder itself.
            Assert.Equal(Path.GetFullPath(nested), MapDiffCommand.ResolveDictionaryDir(Path.Combine(install, "Schema"), _dir).Dir);
            Assert.Equal(Path.GetFullPath(nested), MapDiffCommand.ResolveDictionaryDir(nested, _dir).Dir);

            // Release layout: <root>\cli\lacerte-mcp.exe next to <root>\server\Schema\FieldAccessMap.
            var root = Path.Combine(_dir, "release");
            var cli = Path.Combine(root, "cli");
            Directory.CreateDirectory(cli);
            Directory.CreateDirectory(Path.Combine(root, "server", "Schema", "FieldAccessMap"));
            var resolved = MapDiffCommand.ResolveDictionaryDir(null, cli);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "server", "Schema", "FieldDictionary")), resolved.Dir);
            Assert.Contains("release layout", resolved.Describe);

            // Fallback: next to the exe.
            var lone = Path.Combine(_dir, "lone");
            Directory.CreateDirectory(lone);
            Assert.Equal(Path.GetFullPath(Path.Combine(lone, "Schema", "FieldDictionary")), MapDiffCommand.ResolveDictionaryDir(null, lone).Dir);
        }

        [Theory]
        [InlineData("125000", "125,000.00", true)]
        [InlineData("125000", "$125,000", true)]
        [InlineData("125000", "125001", false)]
        [InlineData("2024-12-31", "12/31/2024", true)]
        [InlineData("ACME Corp", "acme corp", true)]
        [InlineData(null, "1", false)]
        public void ValueMatches(string? stored, string expected, bool match)
        {
            Assert.Equal(match, MapDiff.ValueMatches(stored, expected));
        }

        [Theory]
        [InlineData("125000.5", "amount")]
        [InlineData("2024-12-31", "date")]
        [InlineData("true", "bool")]
        [InlineData("ACME", "text")]
        public void InferDataType(string value, string expected)
        {
            Assert.Equal(expected, MapDiff.InferDataType(value));
        }
    }
}
