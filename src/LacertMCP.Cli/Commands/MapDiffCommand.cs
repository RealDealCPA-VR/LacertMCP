using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Schema;

namespace LacertMCP.Cli.Commands
{
    /// <summary>
    /// <c>lacerte-mcp map-diff {snapshot | compare | record}</c> — FP-3 tooling for
    /// building the field dictionary (<c>Schema/FieldDictionary/&lt;module&gt;.json</c>)
    /// on the Lacerte PC.
    ///
    /// <para>
    /// Workflow: <c>snapshot</c> a TEST client over the Lacerte ODBC driver, enter ONE
    /// known value on ONE Lacerte input screen, <c>snapshot</c> again, then
    /// <c>compare</c> to see what changed and <c>record</c> to write the mapping. Record
    /// refuses unless the diff is exactly one changed field cell or one added/changed
    /// detail row, so a mapping is never ambiguous.
    /// </para>
    /// </summary>
    public static class MapDiffCommand
    {
        public static Command Build()
        {
            var cmd = new Command("map-diff",
                "Build the field dictionary on the Lacerte PC: snapshot a test client, enter ONE known value " +
                "on ONE Lacerte input screen, snapshot again, compare, record.");
            cmd.AddCommand(BuildSnapshotCommand());
            cmd.AddCommand(BuildCompareCommand());
            cmd.AddCommand(BuildRecordCommand());
            return cmd;
        }

        // ---- snapshot -----------------------------------------------------------------------

        private static Command BuildSnapshotCommand()
        {
            var c = new Command("snapshot",
                "Read one client's DATA1-DATA4 rows and detail rows over the Lacerte ODBC driver and write them to a JSON file. " +
                "The file holds the client's return data - use a test client and delete snapshots when done.");
            var dataPathOpt = new Option<string>("--data-path", "Lacerte data directory for the module and year (e.g. C:\\Lacerte\\24tax\\CData).") { IsRequired = true };
            var yearOpt = new Option<int>("--year", "Tax year (four digits, e.g. 2024).") { IsRequired = true };
            var moduleOpt = new Option<string>("--module", "Module character (I/C/P/S/F/R/T/N/B).") { IsRequired = true };
            var clientOpt = new Option<string>("--client", "Client number (1-8 letters/digits).") { IsRequired = true };
            var outOpt = new Option<string>("--out", "Snapshot file to write (.json).") { IsRequired = true };
            var formOpt = new Option<string>("--table-form", () => "auto",
                "DATA2-DATA4 table form: auto (TY2021+ per-client tables, else the global pre-TY21 tables), ty21, or pre-ty21.");
            var overwriteOpt = new Option<bool>("--overwrite", () => false, "Replace --out if it already exists.");
            foreach (var o in new Option[] { dataPathOpt, yearOpt, moduleOpt, clientOpt, outOpt, formOpt, overwriteOpt }) c.AddOption(o);

            c.SetHandler(context =>
            {
                var r = context.ParseResult;
                context.ExitCode = RunSnapshot(
                    r.GetValueForOption(moduleOpt) ?? string.Empty,
                    r.GetValueForOption(yearOpt),
                    r.GetValueForOption(clientOpt) ?? string.Empty,
                    r.GetValueForOption(dataPathOpt) ?? string.Empty,
                    r.GetValueForOption(outOpt) ?? string.Empty,
                    r.GetValueForOption(formOpt) ?? "auto",
                    r.GetValueForOption(overwriteOpt),
                    cs => new DsnSnapshotSource(cs),
                    DateTime.UtcNow,
                    Environment.MachineName,
                    Console.Out,
                    Console.Error);
            });
            return c;
        }

        /// <summary>
        /// Testable core of <c>snapshot</c>. <paramref name="sourceFactory"/> receives the
        /// ODBC connection string (production: <see cref="DsnSnapshotSource"/>).
        /// </summary>
        public static int RunSnapshot(string module, int year, string client, string dataPath, string outPath,
            string tableForm, bool overwrite, Func<string, ISnapshotQuerySource> sourceFactory,
            DateTime nowUtc, string machine, TextWriter stdout, TextWriter stderr)
        {
            if (!TryParseModule(module, stderr, out var moduleChar)) return 1;
            var info = Modules.GetByChar(moduleChar)!;
            if (string.IsNullOrWhiteSpace(outPath))
            {
                stderr.WriteLine("--out is required.");
                return 1;
            }
            string form;
            switch ((tableForm ?? "auto").Trim().ToLowerInvariant())
            {
                case "auto": form = ClientSnapshotReader.DefaultTableForm(year); break;
                case ClientSnapshotReader.TableFormTy21: form = ClientSnapshotReader.TableFormTy21; break;
                case ClientSnapshotReader.TableFormPreTy21: form = ClientSnapshotReader.TableFormPreTy21; break;
                default:
                    stderr.WriteLine($"--table-form must be auto, {ClientSnapshotReader.TableFormTy21} or {ClientSnapshotReader.TableFormPreTy21} (got '{tableForm}').");
                    return 1;
            }
            var fullOut = Path.GetFullPath(outPath);
            if (File.Exists(fullOut) && !overwrite)
            {
                stderr.WriteLine($"Refusing to overwrite {fullOut} (it would destroy an earlier snapshot). Pass --overwrite or choose another --out.");
                return 1;
            }

            ClientSnapshot snap;
            try
            {
                var cs = ConnectionStringBuilder.Build(dataPath, info.ReturnType, year);
                using (var source = sourceFactory(cs))
                {
                    snap = ClientSnapshotReader.Read(source, moduleChar, year, client, form, dataPath, machine, nowUtc);
                }
            }
            catch (LacerteSDKError ex)
            {
                stderr.WriteLine($"Snapshot failed: {ex.StatusCode} {ex.StatusName} - {ex.UserMessage}");
                if (!string.IsNullOrEmpty(ex.Remediation)) stderr.WriteLine("  " + ex.Remediation);
                return 1;
            }
            catch (ArgumentException ex)
            {
                stderr.WriteLine("Snapshot failed: " + FirstLine(ex.Message));
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                stderr.WriteLine("Snapshot failed: " + ex.Message);
                return 1;
            }

            var dir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullOut, ClientSnapshotReader.ToJson(snap), new UTF8Encoding(false));

            stdout.WriteLine($"# Wrote {fullOut}");
            stdout.WriteLine($"  module {snap.Module} ({info.Name}), TY{snap.TaxYear}, client {snap.Client}, tables {snap.TableForm}, taken {snap.TakenAtUtc}");
            foreach (var t in Enumerable.Range(1, 4))
            {
                var n = snap.Rows.Count(r => r.Table == t);
                stdout.WriteLine(snap.ReadErrors.TryGetValue("DATA" + t, out var err)
                    ? $"  DATA{t}: NOT READ ({err})"
                    : $"  DATA{t}: {n} row(s)");
            }
            stdout.WriteLine(snap.ReadErrors.TryGetValue("detail", out var derr)
                ? $"  detail: NOT READ ({derr})"
                : $"  detail: {snap.DetailRows.Count} row(s)");
            if (snap.ReadErrors.Count > 0)
            {
                stdout.WriteLine("  Warning: a table that could not be read is unknown, not empty - `map-diff record` refuses to map across it unless --allow-read-errors is passed.");
            }
            stdout.WriteLine("  This file contains the client's return data. Use a test client and delete snapshots when done.");
            return 0;
        }

        // ---- compare ------------------------------------------------------------------------

        private static Command BuildCompareCommand()
        {
            var c = new Command("compare", "Print every changed cell and detail row between two snapshots.");
            var beforeOpt = new Option<string>("--before", "Snapshot taken before entering the value.") { IsRequired = true };
            var afterOpt = new Option<string>("--after", "Snapshot taken after entering the value.") { IsRequired = true };
            var jsonOpt = new Option<bool>("--json", () => false, "Print the diff as JSON.");
            var ignoreOpt = BuildIgnoreOption();
            foreach (var o in new Option[] { beforeOpt, afterOpt, jsonOpt, ignoreOpt }) c.AddOption(o);
            c.SetHandler(context =>
            {
                var r = context.ParseResult;
                context.ExitCode = RunCompare(
                    r.GetValueForOption(beforeOpt) ?? string.Empty,
                    r.GetValueForOption(afterOpt) ?? string.Empty,
                    r.GetValueForOption(jsonOpt),
                    r.GetValueForOption(ignoreOpt) ?? Array.Empty<string>(),
                    Console.Out, Console.Error);
            });
            return c;
        }

        private static Option<string[]> BuildIgnoreOption()
            => new Option<string[]>("--ignore", () => Array.Empty<string>(),
                "Column to leave out of the field-cell diff (repeatable), e.g. a bookkeeping column Lacerte updates on every edit.");

        public static int RunCompare(string beforePath, string afterPath, bool json, IReadOnlyCollection<string> ignore,
            TextWriter stdout, TextWriter stderr)
        {
            if (!TryLoadPair(beforePath, afterPath, stderr, out var before, out var after)) return 1;
            var diff = MapDiff.Compute(before!, after!, ignore);
            if (json)
            {
                stdout.WriteLine(JsonSerializer.Serialize(diff, DiffJsonOptions));
            }
            else
            {
                WriteDiff(diff, stdout);
            }
            return 0;
        }

        // ---- record -------------------------------------------------------------------------

        private static Command BuildRecordCommand()
        {
            var c = new Command("record",
                "Record the single change between two snapshots as a field-dictionary entry. Refuses unless the diff is " +
                "exactly one changed field cell or one added/changed detail row.");
            var beforeOpt = new Option<string>("--before", "Snapshot taken before entering the value.") { IsRequired = true };
            var afterOpt = new Option<string>("--after", "Snapshot taken after entering the value.") { IsRequired = true };
            var idOpt = new Option<string>("--id", "Stable entry id, e.g. 1120.gross_receipts.") { IsRequired = true };
            var conceptOpt = new Option<string>("--concept", "Plain-English concept, e.g. \"Gross receipts or sales\".") { IsRequired = true };
            var formOpt = new Option<string>("--form", "Form, e.g. 1120.") { IsRequired = true };
            var lineOpt = new Option<string?>("--line", () => null, "Form line or box, e.g. 1a.");
            var dataTypeOpt = new Option<string?>("--data-type", () => null,
                "amount | text | date | bool | integer | percent. Default: inferred from the new value.");
            var notesOpt = new Option<string?>("--notes", () => null, "Optional notes.");
            var expectOpt = new Option<string?>("--expect", () => null,
                "REQUIRED: the value you entered in Lacerte. record checks that the one changed cell (or the detail row's changed amount/description) holds it, so a coincidental change can't be recorded.");
            var allYearsOpt = new Option<bool>("--all-years", () => false,
                "Record the mapping for every tax year (default: only the snapshots' year).");
            var replaceOpt = new Option<bool>("--replace", () => false,
                "Replace an existing entry with the same id even when it points at a different target.");
            var allowErrOpt = new Option<bool>("--allow-read-errors", () => false,
                "Record even though a table could not be read in a snapshot (the change might really be in that table). Requires --expect.");
            var ignoreOpt = BuildIgnoreOption();
            var schemaDirOpt = new Option<string?>("--schema-dir", () => null,
                "Field-dictionary folder to write (the folder holding <module>.json, a Schema folder containing FieldDictionary, " +
                "or an install folder containing Schema\\FieldDictionary). Default: the tree the MCP server reads - the release " +
                "layout's ..\\server\\Schema\\FieldDictionary, or in a dev checkout the server's build output.");
            foreach (var o in new Option[] { beforeOpt, afterOpt, idOpt, conceptOpt, formOpt, lineOpt, dataTypeOpt, notesOpt,
                         expectOpt, allYearsOpt, replaceOpt, allowErrOpt, ignoreOpt, schemaDirOpt })
            {
                c.AddOption(o);
            }
            c.SetHandler(context =>
            {
                var r = context.ParseResult;
                var opts = new RecordOptions
                {
                    BeforePath = r.GetValueForOption(beforeOpt) ?? string.Empty,
                    AfterPath = r.GetValueForOption(afterOpt) ?? string.Empty,
                    Id = r.GetValueForOption(idOpt) ?? string.Empty,
                    Concept = r.GetValueForOption(conceptOpt) ?? string.Empty,
                    Form = r.GetValueForOption(formOpt) ?? string.Empty,
                    Line = r.GetValueForOption(lineOpt),
                    DataType = r.GetValueForOption(dataTypeOpt),
                    Notes = r.GetValueForOption(notesOpt),
                    Expect = r.GetValueForOption(expectOpt),
                    AllYears = r.GetValueForOption(allYearsOpt),
                    Replace = r.GetValueForOption(replaceOpt),
                    AllowReadErrors = r.GetValueForOption(allowErrOpt),
                    Ignore = r.GetValueForOption(ignoreOpt) ?? Array.Empty<string>(),
                    SchemaDir = r.GetValueForOption(schemaDirOpt),
                    MachineName = Environment.MachineName,
                    Today = DateTime.Now,
                    ExeDir = SchemaDirResolver.CliExeDir(),
                };
                context.ExitCode = RunRecord(opts, Console.Out, Console.Error);
            });
            return c;
        }

        /// <summary>Inputs of <c>record</c> (also the test seam).</summary>
        public sealed class RecordOptions
        {
            public string BeforePath { get; set; } = string.Empty;
            public string AfterPath { get; set; } = string.Empty;
            public string Id { get; set; } = string.Empty;
            public string Concept { get; set; } = string.Empty;
            public string Form { get; set; } = string.Empty;
            public string? Line { get; set; }
            public string? DataType { get; set; }
            public string? Notes { get; set; }
            public string? Expect { get; set; }
            public bool AllYears { get; set; }
            public bool Replace { get; set; }
            public bool AllowReadErrors { get; set; }
            public IReadOnlyCollection<string> Ignore { get; set; } = Array.Empty<string>();
            public string? SchemaDir { get; set; }
            public string MachineName { get; set; } = Environment.MachineName;
            public DateTime Today { get; set; } = DateTime.Now;
            public string ExeDir { get; set; } = SchemaDirResolver.CliExeDir();
        }

        public static int RunRecord(RecordOptions o, TextWriter stdout, TextWriter stderr)
        {
            if (!TryLoadPair(o.BeforePath, o.AfterPath, stderr, out var before, out var after)) return 1;
            var b = before!;
            var a = after!;

            // Both snapshots must describe the same client the same way.
            if (b.Module != a.Module || b.TaxYear != a.TaxYear
                || !string.Equals(b.Client, a.Client, StringComparison.OrdinalIgnoreCase)
                || b.TableForm != a.TableForm)
            {
                stderr.WriteLine("Refusing: the snapshots are not of the same client/module/year/table form " +
                                 $"(before: {Describe(b)}; after: {Describe(a)}).");
                return 1;
            }
            var module = b.Module[0];

            // A recorded field entry grants write access (range rules), so the
            // change must be proven to be the value the operator typed.
            if (string.IsNullOrWhiteSpace(o.Expect))
            {
                stderr.WriteLine("--expect <the value you entered in Lacerte> is required, so a coincidental change can't be recorded as this mapping.");
                return 1;
            }

            // Validate the entry text up front so a typo never costs a re-snapshot.
            var id = (o.Id ?? string.Empty).Trim();
            var concept = (o.Concept ?? string.Empty).Trim();
            var form = (o.Form ?? string.Empty).Trim();
            if (id.Length == 0 || concept.Length == 0 || form.Length == 0)
            {
                stderr.WriteLine("--id, --concept and --form must not be blank.");
                return 1;
            }
            string? dataType = string.IsNullOrWhiteSpace(o.DataType) ? null : o.DataType!.Trim().ToLowerInvariant();
            if (dataType != null && !FieldDictionaryLoader.DataTypes.Contains(dataType))
            {
                stderr.WriteLine($"--data-type must be one of {string.Join(" | ", FieldDictionaryLoader.DataTypes)} (got '{o.DataType}').");
                return 1;
            }

            var readErrors = b.ReadErrors.Keys.Union(a.ReadErrors.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (readErrors.Count > 0)
            {
                if (!o.AllowReadErrors)
                {
                    stderr.WriteLine("Refusing: these reads failed in at least one snapshot, so a change there would be invisible " +
                                     "and a coincidental change elsewhere could be recorded instead: " + string.Join(", ", readErrors) + ".");
                    foreach (var k in readErrors)
                    {
                        if (b.ReadErrors.TryGetValue(k, out var e1)) stderr.WriteLine($"  before {k}: {e1}");
                        if (a.ReadErrors.TryGetValue(k, out var e2)) stderr.WriteLine($"  after  {k}: {e2}");
                    }
                    stderr.WriteLine("Make sure the test client has those tables (e.g. a state / subclient row), re-snapshot, or pass --allow-read-errors with --expect.");
                    return 1;
                }
                if (string.IsNullOrWhiteSpace(o.Expect))
                {
                    stderr.WriteLine("--allow-read-errors requires --expect <the value you entered>.");
                    return 1;
                }
                stdout.WriteLine("Warning: recording despite unreadable table(s): " + string.Join(", ", readErrors) + ".");
            }

            var diff = MapDiff.Compute(b, a, o.Ignore);
            var valueCells = diff.CellChanges.Where(c => !c.InRemovedRow).ToList();
            var single = diff.CellChanges.Count + diff.DetailAdded.Count + diff.DetailChanged.Count + diff.DetailRemoved.Count == 1
                         && diff.RowsRemoved.Count == 0
                         // An added DATA2-4 row is part of the change: allowed only
                         // when the one changed cell is inside that same new row.
                         && (diff.RowsAdded.Count == 0
                             || (diff.RowsAdded.Count == 1 && valueCells.Count == 1 && valueCells[0].InAddedRow
                                 && valueCells[0].Table == diff.RowsAdded[0].Table && valueCells[0].RowKey == diff.RowsAdded[0].RowKey));
            CellChange? cell = null;
            DetailChange? detail = null;
            if (single)
            {
                if (valueCells.Count == 1 && !MapDiff.IsBlank(valueCells[0].New)) cell = valueCells[0];
                else if (diff.DetailAdded.Count == 1) detail = diff.DetailAdded[0];
                else if (diff.DetailChanged.Count == 1) detail = diff.DetailChanged[0];
            }
            if (cell == null && detail == null)
            {
                stderr.WriteLine(diff.IsEmpty
                    ? "Refusing: nothing changed between the snapshots. Enter ONE known value on ONE Lacerte input screen, close the client so Lacerte saves it, and snapshot again."
                    : "Refusing: the diff must contain exactly ONE changed field cell (with a non-blank new value) or ONE added/changed detail row, so the mapping is unambiguous. It contains:");
                if (!diff.IsEmpty) WriteDiff(diff, stderr);
                if (!diff.IsEmpty)
                {
                    stderr.WriteLine("Re-take the snapshots with only one value entered, or leave a bookkeeping column out with --ignore <column>.");
                }
                return 1;
            }

            // Proof that the change IS the value the operator typed. For a detail
            // row the value must be in a column that actually changed (a row
            // whose only change is Source/TSJ must not pass on its old amount).
            {
                bool ok;
                if (cell != null)
                {
                    ok = MapDiff.ValueMatches(cell.New, o.Expect!);
                }
                else
                {
                    var afterRow = detail!.After!;
                    var beforeRow = detail.Before;
                    bool amountChanged = beforeRow == null || !Equals(beforeRow.Amount, afterRow.Amount);
                    bool descriptionChanged = beforeRow == null
                        || !string.Equals((beforeRow.Description ?? string.Empty).Trim(), (afterRow.Description ?? string.Empty).Trim(), StringComparison.Ordinal);
                    if (!amountChanged && !descriptionChanged)
                    {
                        stderr.WriteLine("Refusing: the changed detail row's amount and description are unchanged (only a column such as Source/TSJ changed), so it can't be the value you entered:");
                        WriteDiff(diff, stderr);
                        return 1;
                    }
                    ok = (amountChanged && MapDiff.ValueMatches(afterRow.Amount, o.Expect!))
                         || (descriptionChanged && MapDiff.ValueMatches(afterRow.Description, o.Expect!));
                }
                if (!ok)
                {
                    stderr.WriteLine($"Refusing: the only change does not hold the expected value '{o.Expect}':");
                    WriteDiff(diff, stderr);
                    return 1;
                }
            }

            FieldDictionaryEntry entry;
            string inferredNote = string.Empty;
            var years = o.AllYears ? Array.Empty<int>() : new[] { b.TaxYear };
            var verifiedOn = $"TY{b.TaxYear} on {o.MachineName}, {o.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            var line = string.IsNullOrWhiteSpace(o.Line) ? null : o.Line!.Trim();
            var notes = string.IsNullOrWhiteSpace(o.Notes) ? null : o.Notes!.Trim();
            if (cell != null)
            {
                var field = cell.Field.ToUpperInvariant();
                if (cell.Table < 1 || cell.Table > 4)
                {
                    stderr.WriteLine($"Refusing: DATA{cell.Table} is not an input table.");
                    return 1;
                }
                if (dataType == null)
                {
                    dataType = MapDiff.InferDataType(cell.New);
                    inferredNote = $" (inferred from the new value; pass --data-type to override)";
                }
                entry = new FieldDictionaryEntry(id, concept, form, line, FieldDictionaryLoader.KindField,
                    cell.Table, field, null, null, null, null,
                    needsSubclient: cell.Table == 2 || cell.Table == 4,
                    needsState: cell.Table == 3 || cell.Table == 4,
                    dataType, notes, years, verifiedOn, FieldDictionaryLoader.SourceMapDiff);
            }
            else
            {
                var row = detail!.After!;
                if (!TryInt(row.Series, out var series) || !TryInt(row.Code, out var code))
                {
                    stderr.WriteLine($"Refusing: the detail row's Series '{row.Series}' / Code '{row.Code}' are not integers.");
                    return 1;
                }
                int? prefix = TryInt(row.Prefix, out var p) ? p : (int?)null;
                int? suffix = TryInt(row.Suffix, out var s) ? s : (int?)null;
                if (dataType == null)
                {
                    dataType = row.Amount != null ? "amount" : "text";
                    inferredNote = " (inferred: the row has an amount → amount, else text; pass --data-type to override)";
                }
                entry = new FieldDictionaryEntry(id, concept, form, line, FieldDictionaryLoader.KindDetail,
                    null, null, series, code, prefix, suffix,
                    needsSubclient: false,
                    needsState: !string.IsNullOrEmpty(row.StateAbbr),
                    dataType, notes, years, verifiedOn, FieldDictionaryLoader.SourceMapDiff);
            }

            // Resolve the dictionary file and upsert.
            var (dir, describe) = ResolveDictionaryDir(o.SchemaDir, o.ExeDir);
            if (!Directory.Exists(dir))
            {
                stderr.WriteLine($"Refusing: field-dictionary folder {dir} does not exist. Pass --schema-dir (e.g. the install's server folder, or src\\LacertMCP.Server\\Schema\\FieldDictionary).");
                return 1;
            }
            var path = FieldDictionaryLoader.FilePathFor(module, dir)!;
            var existing = new List<FieldDictionaryEntry>();
            if (File.Exists(path))
            {
                var parsed = FieldDictionaryLoader.ValidateFile(path, module, out var error);
                if (parsed == null)
                {
                    stderr.WriteLine($"Refusing to write: {path} is invalid ({error}). Fix or restore it first (the server treats it as empty).");
                    return 1;
                }
                existing.AddRange(parsed.Entries);
            }

            var prior = existing.FirstOrDefault(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (prior != null)
            {
                if (prior.TargetKey == entry.TargetKey && !o.Replace)
                {
                    // Same mapping re-verified (e.g. for another year): widen the years.
                    var merged = prior.TaxYears.Count == 0 || entry.TaxYears.Count == 0
                        ? Array.Empty<int>()
                        : prior.TaxYears.Union(entry.TaxYears).OrderBy(y => y).ToArray();
                    entry = new FieldDictionaryEntry(entry.Id, entry.Concept, entry.Form, entry.Line, entry.Kind,
                        entry.Table, entry.Field, entry.Series, entry.Code, entry.Prefix, entry.Suffix,
                        entry.NeedsSubclient, entry.NeedsState, entry.DataType, entry.Notes ?? prior.Notes,
                        merged, entry.VerifiedOn, entry.Source);
                }
                else if (prior.TargetKey != entry.TargetKey && !o.Replace)
                {
                    stderr.WriteLine($"Refusing: id '{prior.Id}' already maps to {prior.DescribeTarget()}" +
                                     (prior.TaxYears.Count > 0 ? $" for TY{string.Join(",", prior.TaxYears)}" : " for every year") +
                                     $", but this diff points at {entry.DescribeTarget()}. If Lacerte moved it for this year, use a year-specific id " +
                                     $"(e.g. {prior.Id}.ty{b.TaxYear}); pass --replace only if the old mapping was wrong.");
                    return 1;
                }
                existing.Remove(prior);
            }

            var clash = existing.FirstOrDefault(e => e.TargetKey == entry.TargetKey && FieldDictionaryLoader.YearsOverlap(e, entry));
            if (clash != null)
            {
                stderr.WriteLine($"Refusing: {entry.DescribeTarget()} is already mapped as '{clash.Id}' ({clash.Concept}) for overlapping years. " +
                                 "One Lacerte cell cannot mean two concepts - check which mapping is right and edit the file.");
                return 1;
            }

            existing.Add(entry);
            var text = FieldDictionaryLoader.Serialize(module, existing);
            try
            {
                // Same parse path as the server: never write a file it would reject.
                FieldDictionaryLoader.Parse(text, module);
            }
            catch (JsonException ex)
            {
                stderr.WriteLine($"Refusing to write: the new entry is invalid ({ex.Message}).");
                return 1;
            }
            WriteAtomically(path, text);
            FieldDictionaryLoader.ReloadCaches();

            var change = cell != null
                ? $"DATA{cell.Table} [{cell.RowKey}] {cell.Field}: {Show(cell.Old)} -> {Show(cell.New)}"
                : $"detail {(detail!.Kind == "added" ? "row added" : "row changed")}: {DescribeDetail(detail.After!)}";
            stdout.WriteLine($"# Dictionary dir: {describe}");
            stdout.WriteLine($"# Wrote {path}");
            stdout.WriteLine($"  change: {change}");
            stdout.WriteLine($"  {(prior != null ? "updated" : "added")} '{entry.Id}' -> {entry.DescribeTarget()}, dataType {entry.DataType}{inferredNote}, " +
                             (entry.TaxYears.Count == 0 ? "every tax year" : "TY" + string.Join(",", entry.TaxYears)));
            stdout.WriteLine("  A running MCP server caches the dictionary - call the lacerte_reload_schema tool to pick this up without a restart.");
            stdout.WriteLine("  This is an install / build-output copy: copy the entry into src\\LacertMCP.Server\\Schema\\FieldDictionary\\" +
                             Path.GetFileName(path) + " and commit it to ship it in a release. On an install, `lacerte-mcp update` and installer re-runs keep it; a dev rebuild of the server output replaces it.");
            return 0;
        }

        /// <summary>
        /// Resolves the field-dictionary folder: explicit <c>--schema-dir</c> (the folder
        /// itself, a Schema folder, or an install folder), else the sibling of the
        /// FieldAccessMap root <see cref="SchemaDirResolver"/> picks (release layout's
        /// server tree, the dev server's build output, or next to lacerte-mcp.exe).
        /// </summary>
        public static (string Dir, string Describe) ResolveDictionaryDir(string? explicitDir, string exeDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                var full = Path.GetFullPath(explicitDir!.Trim());
                var nestedInstall = Path.Combine(full, "Schema", FieldDictionaryLoader.FolderName);
                var nestedSchema = Path.Combine(full, FieldDictionaryLoader.FolderName);
                if (Directory.Exists(nestedInstall)) full = nestedInstall;
                else if (Directory.Exists(nestedSchema)) full = nestedSchema;
                return (full, $"{full} (from --schema-dir)");
            }
            var map = SchemaDirResolver.Resolve(null, exeDir);
            var schemaDir = Path.GetDirectoryName(map.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? exeDir;
            var dir = Path.Combine(schemaDir, FieldDictionaryLoader.FolderName);
            string why;
            switch (map.Source)
            {
                case SchemaDirResolver.SchemaDirSource.ReleaseServerSibling: why = "release layout: the server's schema tree"; break;
                case SchemaDirResolver.SchemaDirSource.DevServerBuild: why = "dev checkout: the server's build output, which the dev server reads - rebuilds overwrite it"; break;
                default: why = "next to lacerte-mcp.exe"; break;
            }
            return (dir, $"{dir} ({why})");
        }

        // ---- shared helpers -----------------------------------------------------------------

        private static readonly JsonSerializerOptions DiffJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static bool TryLoadPair(string beforePath, string afterPath, TextWriter stderr,
            out ClientSnapshot? before, out ClientSnapshot? after)
        {
            before = TryLoad(beforePath, "--before", stderr);
            after = before == null ? null : TryLoad(afterPath, "--after", stderr);
            return before != null && after != null;
        }

        private static ClientSnapshot? TryLoad(string path, string option, TextWriter stderr)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                stderr.WriteLine($"{option}: snapshot file '{path}' not found.");
                return null;
            }
            try
            {
                return ClientSnapshotReader.FromJson(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                stderr.WriteLine($"{option}: {path} is not a valid map-diff snapshot ({ex.Message}).");
                return null;
            }
        }

        private static void WriteDiff(SnapshotDiff diff, TextWriter w)
        {
            w.WriteLine($"# map-diff: {diff.Before} -> {diff.After}");
            foreach (var warning in diff.Warnings) w.WriteLine("  Warning: " + warning);
            foreach (var r in diff.RowsAdded) w.WriteLine($"  DATA{r.Table} [{r.RowKey}] row added");
            foreach (var r in diff.RowsRemoved) w.WriteLine($"  DATA{r.Table} [{r.RowKey}] row removed");
            foreach (var c in diff.CellChanges)
            {
                w.WriteLine($"  DATA{c.Table} [{c.RowKey}] {c.Field}: {Show(c.Old)} -> {Show(c.New)}");
            }
            foreach (var d in diff.DetailAdded) w.WriteLine($"  detail added:   {DescribeDetail(d.After!)}");
            foreach (var d in diff.DetailRemoved) w.WriteLine($"  detail removed: {DescribeDetail(d.Before!)}");
            foreach (var d in diff.DetailChanged)
            {
                w.WriteLine($"  detail changed: {DescribeDetail(d.Before!)}");
                w.WriteLine($"              ->  {DescribeDetail(d.After!)}");
            }
            if (diff.IgnoredFields.Count > 0) w.WriteLine("  (ignored columns: " + string.Join(", ", diff.IgnoredFields) + ")");
            w.WriteLine($"Summary: {diff.CellChanges.Count} field cell change(s), {diff.RowsAdded.Count} row(s) added, {diff.RowsRemoved.Count} row(s) removed, " +
                        $"{diff.DetailAdded.Count} detail row(s) added, {diff.DetailChanged.Count} changed, {diff.DetailRemoved.Count} removed.");
        }

        private static string DescribeDetail(SnapshotDetailRow d)
        {
            var sb = new StringBuilder();
            sb.Append($"Series {Show(d.Series)} Prefix {Show(d.Prefix)} Code {Show(d.Code)} Suffix {Show(d.Suffix)}");
            if (d.StateAbbr.Length > 0) sb.Append($" State {d.StateAbbr}");
            if (d.CityAbbr.Length > 0) sb.Append($" City {d.CityAbbr}");
            if (d.Tsj.Length > 0) sb.Append($" TSJ {d.Tsj}");
            if (d.Source.Length > 0) sb.Append($" Source {d.Source}");
            sb.Append($" Amount {Show(d.Amount)}");
            sb.Append($" Description {(d.Description.Length == 0 ? "(blank)" : "\"" + d.Description + "\"")}");
            return sb.ToString();
        }

        private static string Show(string? v) => v == null ? "(null)" : v.Length == 0 ? "(blank)" : v;

        private static string Describe(ClientSnapshot s) => $"module {s.Module}, TY{s.TaxYear}, client {s.Client}, tables {s.TableForm}";

        /// <summary>Integer detail column (the driver may hand back "11" or "11.0").</summary>
        private static bool TryInt(string? s, out int value)
        {
            var t = (s ?? string.Empty).Trim();
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                && d == decimal.Truncate(d) && d >= int.MinValue && d <= int.MaxValue)
            {
                value = (int)d;
                return true;
            }
            value = 0;
            return false;
        }

        private static bool TryParseModule(string module, TextWriter stderr, out char moduleChar)
        {
            moduleChar = '\0';
            var m = (module ?? string.Empty).Trim();
            if (m.Length != 1 || Modules.GetByChar(char.ToUpperInvariant(m[0])) == null)
            {
                stderr.WriteLine($"--module must be one of {string.Join("/", Modules.All.Select(x => x.Char))} (got '{module}').");
                return false;
            }
            moduleChar = char.ToUpperInvariant(m[0]);
            return true;
        }

        private static string FirstLine(string s)
        {
            var i = s.IndexOfAny(new[] { '\r', '\n' });
            return i >= 0 ? s.Substring(0, i) : s;
        }

        private static void WriteAtomically(string path, string text)
        {
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }

    // ---- diff model + engine --------------------------------------------------------------------

    /// <summary>A changed DATA1–DATA4 cell.</summary>
    public sealed class CellChange
    {
        [JsonPropertyName("table")] public int Table { get; set; }
        [JsonPropertyName("rowKey")] public string RowKey { get; set; } = string.Empty;
        [JsonPropertyName("subClient")] public string? SubClient { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("field")] public string Field { get; set; } = string.Empty;
        [JsonPropertyName("old")] public string? Old { get; set; }
        [JsonPropertyName("new")] public string? New { get; set; }
        /// <summary>The cell belongs to a row that only exists in the after snapshot.</summary>
        [JsonPropertyName("inAddedRow")] public bool InAddedRow { get; set; }
        /// <summary>The cell belongs to a row that only exists in the before snapshot.</summary>
        [JsonPropertyName("inRemovedRow")] public bool InRemovedRow { get; set; }
    }

    /// <summary>A DATA2–DATA4 row that appeared or disappeared.</summary>
    public sealed class RowChange
    {
        [JsonPropertyName("table")] public int Table { get; set; }
        [JsonPropertyName("rowKey")] public string RowKey { get; set; } = string.Empty;
    }

    /// <summary>A detail row added, removed or changed (same Series/Prefix/Code/Suffix/State/City).</summary>
    public sealed class DetailChange
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("before")] public SnapshotDetailRow? Before { get; set; }
        [JsonPropertyName("after")] public SnapshotDetailRow? After { get; set; }
    }

    /// <summary>Everything that differs between two snapshots.</summary>
    public sealed class SnapshotDiff
    {
        [JsonPropertyName("before")] public string Before { get; set; } = string.Empty;
        [JsonPropertyName("after")] public string After { get; set; } = string.Empty;
        [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = new List<string>();
        [JsonPropertyName("rowsAdded")] public List<RowChange> RowsAdded { get; set; } = new List<RowChange>();
        [JsonPropertyName("rowsRemoved")] public List<RowChange> RowsRemoved { get; set; } = new List<RowChange>();
        [JsonPropertyName("cellChanges")] public List<CellChange> CellChanges { get; set; } = new List<CellChange>();
        [JsonPropertyName("detailAdded")] public List<DetailChange> DetailAdded { get; set; } = new List<DetailChange>();
        [JsonPropertyName("detailRemoved")] public List<DetailChange> DetailRemoved { get; set; } = new List<DetailChange>();
        [JsonPropertyName("detailChanged")] public List<DetailChange> DetailChanged { get; set; } = new List<DetailChange>();
        [JsonPropertyName("ignoredFields")] public List<string> IgnoredFields { get; set; } = new List<string>();

        [JsonIgnore]
        public bool IsEmpty => RowsAdded.Count == 0 && RowsRemoved.Count == 0 && CellChanges.Count == 0
                               && DetailAdded.Count == 0 && DetailRemoved.Count == 0 && DetailChanged.Count == 0;
    }

    /// <summary>Pure diff engine for <see cref="ClientSnapshot"/>s.</summary>
    public static class MapDiff
    {
        public static SnapshotDiff Compute(ClientSnapshot before, ClientSnapshot after, IEnumerable<string>? ignoreFields = null)
        {
            var ignore = new HashSet<string>((ignoreFields ?? Array.Empty<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim().ToUpperInvariant()), StringComparer.Ordinal);
            var diff = new SnapshotDiff
            {
                Before = Label(before),
                After = Label(after),
                IgnoredFields = ignore.OrderBy(f => f, StringComparer.Ordinal).ToList(),
            };
            if (before.Module != after.Module || before.TaxYear != after.TaxYear
                || !string.Equals(before.Client, after.Client, StringComparison.OrdinalIgnoreCase))
            {
                diff.Warnings.Add("the snapshots are of different clients / modules / years.");
            }
            if (before.TableForm != after.TableForm)
            {
                diff.Warnings.Add($"the snapshots use different table forms ({before.TableForm} vs {after.TableForm}).");
            }
            foreach (var k in before.ReadErrors.Keys.Union(after.ReadErrors.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                diff.Warnings.Add($"{k} could not be read in {(before.ReadErrors.ContainsKey(k) && after.ReadErrors.ContainsKey(k) ? "either snapshot" : before.ReadErrors.ContainsKey(k) ? "the before snapshot" : "the after snapshot")} - changes there are not shown.");
            }

            foreach (var table in Enumerable.Range(1, 4))
            {
                if (before.ReadErrors.ContainsKey("DATA" + table) || after.ReadErrors.ContainsKey("DATA" + table)) continue;
                var b = before.Rows.Where(r => r.Table == table).GroupBy(r => r.RowKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var a = after.Rows.Where(r => r.Table == table).GroupBy(r => r.RowKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                foreach (var key in b.Keys.Union(a.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    b.TryGetValue(key, out var br);
                    a.TryGetValue(key, out var ar);
                    var row = ar ?? br!;
                    if (br == null) diff.RowsAdded.Add(new RowChange { Table = table, RowKey = key });
                    if (ar == null) diff.RowsRemoved.Add(new RowChange { Table = table, RowKey = key });
                    var columns = (br?.Cells.Keys ?? Enumerable.Empty<string>()).Union(ar?.Cells.Keys ?? Enumerable.Empty<string>())
                        .OrderBy(c => c, StringComparer.Ordinal);
                    foreach (var col in columns)
                    {
                        if (ignore.Contains(col)) continue;
                        string? ov = null, nv = null;
                        br?.Cells.TryGetValue(col, out ov);
                        ar?.Cells.TryGetValue(col, out nv);
                        if (br != null && ar != null)
                        {
                            if (string.Equals(ov, nv, StringComparison.Ordinal)) continue;
                        }
                        else
                        {
                            // A row that appeared / disappeared: its key columns and
                            // blank / zero defaults are structure, not entered values.
                            if (IsKeyColumn(table, col) || IsBlank(br == null ? nv : ov)) continue;
                        }
                        diff.CellChanges.Add(new CellChange
                        {
                            Table = table,
                            RowKey = key,
                            SubClient = row.SubClient,
                            State = row.State,
                            Field = col,
                            Old = ov,
                            New = nv,
                            InAddedRow = br == null,
                            InRemovedRow = ar == null,
                        });
                    }
                }
            }

            if (!before.ReadErrors.ContainsKey("detail") && !after.ReadErrors.ContainsKey("detail"))
            {
                CompareDetails(before.DetailRows, after.DetailRows, diff);
            }
            return diff;
        }

        private static void CompareDetails(List<SnapshotDetailRow> before, List<SnapshotDetailRow> after, SnapshotDiff diff)
        {
            // Drop exact matches pairwise (a multiset compare), then pair the
            // rest by identity: one-for-one = changed, otherwise added / removed.
            var remainingAfter = after.ToList();
            var remainingBefore = new List<SnapshotDetailRow>();
            foreach (var row in before)
            {
                var idx = remainingAfter.FindIndex(x => x.ContentKey == row.ContentKey);
                if (idx >= 0) remainingAfter.RemoveAt(idx);
                else remainingBefore.Add(row);
            }
            var identities = remainingBefore.Select(r => r.IdentityKey).Union(remainingAfter.Select(r => r.IdentityKey))
                .OrderBy(k => k, StringComparer.Ordinal);
            foreach (var id in identities)
            {
                var bs = remainingBefore.Where(r => r.IdentityKey == id).ToList();
                var ats = remainingAfter.Where(r => r.IdentityKey == id).ToList();
                if (bs.Count == 1 && ats.Count == 1)
                {
                    diff.DetailChanged.Add(new DetailChange { Kind = "changed", Before = bs[0], After = ats[0] });
                    continue;
                }
                diff.DetailRemoved.AddRange(bs.Select(r => new DetailChange { Kind = "removed", Before = r }));
                diff.DetailAdded.AddRange(ats.Select(r => new DetailChange { Kind = "added", After = r }));
            }
        }

        /// <summary>Identity columns of a DATA row (client number, subclient number, state).</summary>
        public static bool IsKeyColumn(int table, string column)
        {
            var c = column.ToUpperInvariant();
            if (c == "C" + table + "_0" || c == "SUBCLIENTID" || c == "SUBCLIENTNUMBER") return true;
            switch (table)
            {
                case 2: return c == "C2_1";
                case 3: return c == "C3_G1";
                case 4: return c == "C4_1" || c == "C4_G1";
                default: return false;
            }
        }

        /// <summary>Null, whitespace, or a number equal to zero (DBF numeric default).</summary>
        public static bool IsBlank(string? v)
            => string.IsNullOrWhiteSpace(v)
               || (decimal.TryParse(v!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == 0m);

        /// <summary>Best-effort data type from a canonical value: date, bool, amount (numeric) or text.</summary>
        public static string InferDataType(string? v)
        {
            var s = (v ?? string.Empty).Trim();
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "date";
            if (s == "true" || s == "false") return "bool";
            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return "amount";
            return "text";
        }

        /// <summary>
        /// True when a stored value equals what the operator typed: numerically for
        /// numbers (so 125000 matches 125000.00 and 125,000), as a date for dates,
        /// else trimmed and case-insensitive.
        /// </summary>
        public static bool ValueMatches(string? stored, string expected)
        {
            if (stored == null) return false;
            var s = stored.Trim();
            var e = (expected ?? string.Empty).Trim();
            var numberStyles = NumberStyles.Number | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowExponent;
            var en = CultureInfo.GetCultureInfo("en-US");
            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)
                && decimal.TryParse(e, numberStyles, en, out var ed))
            {
                return sd == ed;
            }
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sdt)
                && DateTime.TryParse(e, en, DateTimeStyles.None, out var edt))
            {
                return sdt == edt;
            }
            return string.Equals(s, e, StringComparison.OrdinalIgnoreCase);
        }

        private static string Label(ClientSnapshot s) => $"{s.Module}/TY{s.TaxYear}/{s.Client} @ {s.TakenAtUtc}";
    }
}
