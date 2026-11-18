using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Odbc;
using LacertMCP.Server.Schema;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// FP-2 — result of a detail-row write.
    /// <see cref="Snapshot"/>: path of the backup of the client's detail rows
    /// taken before the first live write to that client in this process (null
    /// in simulation, or before any live write).
    /// <see cref="Verified"/>: true = read back from Lacerte after the write and
    /// exactly one new matching row is there; null = no read-back (dry run,
    /// simulation, idempotent replay, or the read-back couldn't run — see
    /// <see cref="VerificationNote"/>). A mismatch is never returned: it throws
    /// 9022 WriteNotVerified.
    /// </summary>
    public sealed record DetailWriteOutcome(int Status, string? Snapshot, bool? Verified, string? VerificationNote);

    /// <summary>
    /// Live-mode read path: every read the simulation store answers is answered
    /// here from the Lacerte ODBC driver (<c>LacerteDSIIDriver</c>) when
    /// <see cref="Mode"/> is <see cref="SessionMode.Live"/>.
    ///
    /// <para>
    /// Before this file existed every live read threw 9201 BridgeUnavailable
    /// (the old <c>ThrowIfLiveUnwired</c> gate) — the ODBC layer was built and
    /// unit-tested but never connected to the session manager.
    /// </para>
    ///
    /// <para>
    /// Shape contract (two-mode discipline): live rows are normalized to the
    /// same shape <c>CloneRow</c> produces for sim rows — DBF column codes
    /// (<c>C1_0</c>, <c>C3_G1</c>, …) verbatim, plus camelCase meta keys
    /// <c>clientNumber</c> / <c>stateAbbrev</c>. DBF CHAR columns are
    /// right-padded with spaces on disk; every string is right-trimmed so
    /// "SMITH     " reads back as "SMITH" (and compares equal to what was
    /// written). Filters that sim applies case-insensitively in memory are
    /// applied in memory here too — the FoxPro-style driver's LIKE is
    /// case-sensitive, so pushing them into SQL would silently diverge.
    /// </para>
    ///
    /// <para>
    /// Threading: one <see cref="LacerteDsnReader"/> per (dataPath, year,
    /// module) tuple, each guarded by its own lock — an
    /// <c>OdbcConnection</c> is not safe for concurrent commands.
    /// </para>
    /// </summary>
    public sealed partial class LacerteSessionManager
    {
        private sealed class LiveReaderSlot
        {
            public readonly object Gate = new object();
            public LacerteDsnReader? Reader;
        }

        private readonly ConcurrentDictionary<string, LiveReaderSlot> _liveReaders =
            new ConcurrentDictionary<string, LiveReaderSlot>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Test seam: builds the reader for a connection string. Production uses the real ODBC driver.</summary>
        internal Func<string, LacerteDsnReader> LiveReaderFactory { get; set; } = cs => new LacerteDsnReader(cs);

        /// <summary>Upper bound on rows a single live roster read materializes.</summary>
        private const int LiveRosterCap = 100_000;

        internal static string LiveConnectionString(SessionContext ctx)
        {
            var info = Modules.GetByChar(ctx.Module)
                ?? throw new LacerteSDKError(SyntheticStatusCodes.InvalidArgument,
                    new Dictionary<string, object?> { ["module"] = ctx.Module.ToString() });
            return ConnectionStringBuilder.Build(ctx.DataPath, info.ReturnType, ctx.Year);
        }

        /// <summary>
        /// Runs <paramref name="sql"/> against the context's ODBC reader and
        /// returns normalized rows. <paramref name="fresh"/> bypasses the cached
        /// connection (used by write read-back so a driver-side cache can never
        /// make a failed write look verified).
        /// </summary>
        internal IReadOnlyList<Dictionary<string, object?>> LiveQuery(
            SessionContext ctx, string sql, int table, bool fresh = false)
        {
            var cs = LiveConnectionString(ctx);
            IReadOnlyList<IReadOnlyDictionary<string, object?>> raw;
            if (fresh)
            {
                using (var r = LiveReaderFactory(cs))
                {
                    raw = r.Query(sql);
                }
            }
            else
            {
                var slot = _liveReaders.GetOrAdd(cs, _ => new LiveReaderSlot());
                lock (slot.Gate)
                {
                    slot.Reader ??= LiveReaderFactory(cs);
                    raw = slot.Reader.Query(sql);
                }
            }
            return raw.Select(r => NormalizeLiveRow(table, r)).ToList();
        }

        /// <summary>True when the ODBC driver answers a trivial roster query for the context.</summary>
        internal bool LivePing(SessionContext ctx)
        {
            try
            {
                LiveQuery(ctx, SqlTemplates.Data1Top(1), table: 1, fresh: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void DisposeLiveReaders()
        {
            foreach (var slot in _liveReaders.Values)
            {
                lock (slot.Gate)
                {
                    try { slot.Reader?.Dispose(); } catch { /* best effort */ }
                    slot.Reader = null;
                }
            }
            _liveReaders.Clear();
        }

        // -------------------------------------------------------------------
        // Row normalization (pure — unit tested directly)
        // -------------------------------------------------------------------

        /// <summary>
        /// Normalizes one raw ODBC row into the sim-identical wire shape.
        /// </summary>
        internal static Dictionary<string, object?> NormalizeLiveRow(
            int table, IReadOnlyDictionary<string, object?> raw)
        {
            var dict = new Dictionary<string, object?>(raw.Count + 2);
            string? clientNumber = null;
            string? state = null;
            object? subClient = null;
            var idCol = "C" + table + "_0";
            var stateCol = "C" + table + "_G1";
            foreach (var kv in raw)
            {
                var v = NormalizeLiveValue(kv.Value);
                if (string.Equals(kv.Key, idCol, StringComparison.OrdinalIgnoreCase))
                {
                    clientNumber = (v as string)?.Trim() ?? v?.ToString();
                }
                if ((table == 3 || table == 4) && string.Equals(kv.Key, stateCol, StringComparison.OrdinalIgnoreCase))
                {
                    state = (v as string)?.Trim();
                }
                // Subclient number: the SDK documents C4_1 for DATA4 ("C4_1 is
                // the subclient number"; sample "... and C4_1 = 1"); DATA2
                // follows the same C{t}_1 layout. Only a positive integer
                // counts, so an unexpected column shape never invents an id.
                if ((table == 2 || table == 4)
                    && (string.Equals(kv.Key, "C" + table + "_1", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kv.Key, "SubClientID", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kv.Key, "SubClientNumber", StringComparison.OrdinalIgnoreCase)))
                {
                    var sid = ToPositiveInt(v);
                    if (sid.HasValue) subClient = sid.Value;
                }
                dict[kv.Key] = v;
            }
            var result = new Dictionary<string, object?>(dict.Count + 2);
            if (clientNumber != null) result["clientNumber"] = clientNumber;
            if (subClient != null) result["subClientId"] = subClient;
            if (!string.IsNullOrEmpty(state)) result["stateAbbrev"] = state;
            foreach (var kv in dict) result[kv.Key] = kv.Value;
            return result;
        }

        private static int? ToPositiveInt(object? v)
        {
            switch (v)
            {
                case null: return null;
                case string s:
                    return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : (int?)null;
                case IConvertible c:
                    try
                    {
                        var d = c.ToDecimal(CultureInfo.InvariantCulture);
                        return d > 0 && d == decimal.Truncate(d) && d <= int.MaxValue ? (int)d : (int?)null;
                    }
                    catch (Exception) { return null; }
                default: return null;
            }
        }

        /// <summary>
        /// DBF CHAR → right-trimmed string; DATE → ISO yyyy-MM-dd (time kept
        /// only when non-midnight); everything else passes through.
        /// </summary>
        internal static object? NormalizeLiveValue(object? v)
        {
            switch (v)
            {
                case null: return null;
                case DBNull _: return null;
                case string s: return s.TrimEnd(' ', '\0');
                case DateTime dt:
                    return dt.TimeOfDay == TimeSpan.Zero
                        ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                case byte[] bytes: return Convert.ToBase64String(bytes);
                default: return v;
            }
        }

        internal static DetailRow ToDetailRow(IReadOnlyDictionary<string, object?> row)
        {
            string S(string key)
            {
                if (!TryGetCI(row, key, out var v) || v == null) return string.Empty;
                return v switch
                {
                    string s => s.Trim(),
                    decimal d => d.ToString("0.############", CultureInfo.InvariantCulture),
                    double db => db.ToString("0.############", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString() ?? string.Empty,
                };
            }
            double? amount = null;
            if (TryGetCI(row, "Amount", out var a) && a != null)
            {
                if (a is string sa)
                {
                    if (double.TryParse(sa.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pa)) amount = pa;
                }
                else
                {
                    amount = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                }
            }
            return new DetailRow(
                S("Series"), S("Prefix"), S("Code"), S("Suffix"), S("StateAbbr"), S("CityAbbr"),
                S("Source"), S("TSJ"), S("ImportSrc"), amount, S("Description"));
        }

        internal static bool StatusDateOnOrAfter(object? statusDate, DateTime sinceUtc)
        {
            switch (statusDate)
            {
                case null: return false;
                case DateTime dt:
                    var u = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                    return u >= sinceUtc;
                default:
                    var s = (statusDate as string ?? statusDate.ToString() ?? string.Empty).Trim();
                    return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p)
                           && p >= sinceUtc;
            }
        }

        private static bool TryGetCI(IReadOnlyDictionary<string, object?> row, string key, out object? value)
        {
            if (row.TryGetValue(key, out value)) return true;
            foreach (var kv in row)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        /// <summary>Upper-cases + trims a client number and validates it as a safe SQL identifier.</summary>
        internal static string LiveClientKey(string clientNumber)
        {
            var key = (clientNumber ?? string.Empty).Trim().ToUpperInvariant();
            SqlTemplates.ValidateClient(key);
            return key;
        }

        // -------------------------------------------------------------------
        // Live implementations of the read cores
        // -------------------------------------------------------------------

        private IReadOnlyList<IReadOnlyDictionary<string, object?>> LiveListClients(
            SessionContext ctx, string? lastNameFilter, int limit)
        {
            if (limit <= 0) return Array.Empty<IReadOnlyDictionary<string, object?>>();
            // The DBF returns rows in physical order; sim (R7-11) orders by
            // ClientNumber, so the whole roster is read and sorted here too —
            // otherwise paging and "first N" would differ between modes.
            IEnumerable<Dictionary<string, object?>> rows = SortByClientNumber(
                LiveQuery(ctx, SqlTemplates.Data1Top(LiveRosterCap), 1));
            if (!string.IsNullOrEmpty(lastNameFilter))
            {
                // Same semantics as sim: case-insensitive StartsWith on the
                // trimmed name column, applied in memory.
                var nameKey = ctx.Module == 'I' ? "C1_2" : "C1_1";
                var f = lastNameFilter!;
                rows = rows.Where(r => TryGetCI(r, nameKey, out var v) && v is string s
                                       && s.Trim().StartsWith(f, StringComparison.OrdinalIgnoreCase));
            }
            return rows.Take(limit).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        }

        private static IEnumerable<Dictionary<string, object?>> SortByClientNumber(IEnumerable<Dictionary<string, object?>> rows)
            => rows.OrderBy(r => r.TryGetValue("clientNumber", out var v) ? v as string ?? v?.ToString() ?? string.Empty : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        private IReadOnlyDictionary<string, object?>? LiveReadClient(SessionContext ctx, string clientNumber)
        {
            var key = LiveClientKey(clientNumber);
            var rows = LiveQuery(ctx, SqlTemplates.Data1Where(key), 1);
            // Exact key match: an xBase driver with SET EXACT OFF can treat
            // C1_0 = '10' as a prefix match and return client 1001 first.
            return rows.FirstOrDefault(r => r.TryGetValue("clientNumber", out var v)
                && string.Equals((v as string)?.Trim(), key, StringComparison.OrdinalIgnoreCase));
        }

        private IReadOnlyList<IReadOnlyDictionary<string, object?>> LiveReadTable(
            SessionContext ctx, int table, string? clientNumber, string? stateAbbrev, bool fresh = false)
        {
            var useNew = CanUseNewApis(ctx.Year, ctx.Module);
            string? key = string.IsNullOrWhiteSpace(clientNumber) ? null : LiveClientKey(clientNumber!);
            if (key == null && table >= 2 && table <= 7)
            {
                // Whole-firm read (lacerte_client_query, lacerte_table_read with
                // no clientNumber) — sim returns every client's rows, so live must too.
                return LiveReadWholeFirm(ctx, table, useNew, stateAbbrev, fresh);
            }
            string sql;
            switch (table)
            {
                case 1:
                    sql = key == null ? SqlTemplates.Data1Top(LiveRosterCap) : SqlTemplates.Data1Where(key);
                    break;
                case 2:
                    sql = useNew ? SqlTemplates.Data2NewForm(RequireClientKey(key, table)) : SqlTemplates.Data2OldForm(RequireClientKey(key, table));
                    break;
                case 3:
                    sql = useNew ? SqlTemplates.Data3NewForm(RequireClientKey(key, table)) : SqlTemplates.Data3OldForm(RequireClientKey(key, table));
                    break;
                case 4:
                    if (useNew)
                    {
                        sql = SqlTemplates.Data4NewForm(RequireClientKey(key, table));
                    }
                    else
                    {
                        // No state = every state (the tool contract and sim);
                        // the in-memory state filter below handles a given state.
                        sql = string.IsNullOrWhiteSpace(stateAbbrev)
                            ? SqlTemplates.Data4OldFormAllStates(RequireClientKey(key, table))
                            : SqlTemplates.Data4OldForm(RequireClientKey(key, table), stateAbbrev!.Trim().ToUpperInvariant());
                    }
                    break;
                case 5: sql = SqlTemplates.Data5(RequireClientKey(key, table)); break;
                case 6: sql = SqlTemplates.Data6(RequireClientKey(key, table)); break;
                case 7: sql = SqlTemplates.Data7(RequireClientKey(key, table)); break;
                default:
                    throw new LacerteSDKError(StatusCodes.DBNotSupported,
                        new Dictionary<string, object?> { ["table"] = table });
            }

            IEnumerable<Dictionary<string, object?>> rows = LiveQuery(ctx, sql, table, fresh);

            // Per-client TY21+ tables have no client column — stamp it so the
            // row shape matches sim (which always carries clientNumber).
            if (key != null)
            {
                rows = rows.Select(r =>
                {
                    if (!r.ContainsKey("clientNumber")) r["clientNumber"] = key;
                    return r;
                });
            }
            if (!string.IsNullOrWhiteSpace(stateAbbrev))
            {
                var st = stateAbbrev!.Trim();
                rows = rows.Where(r => r.TryGetValue("stateAbbrev", out var v)
                                       && string.Equals(v as string, st, StringComparison.OrdinalIgnoreCase));
            }
            return rows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
        }

        /// <summary>
        /// DATA2–7 for every client. Global DBFs (DATA2/3/4 before TY21,
        /// DATA5/6/7 always) are one query; TY21+ DATA2/3/4 are one table per
        /// client, so the roster is walked (a client with no such table simply
        /// contributes no rows). Rows are ordered by clientNumber like sim.
        /// </summary>
        private IReadOnlyList<IReadOnlyDictionary<string, object?>> LiveReadWholeFirm(
            SessionContext ctx, int table, bool useNew, string? stateAbbrev, bool fresh)
        {
            IEnumerable<Dictionary<string, object?>> rows;
            if (!useNew || table >= 5)
            {
                rows = LiveQuery(ctx, SqlTemplates.GlobalTable(table), table, fresh);
            }
            else
            {
                var all = new List<Dictionary<string, object?>>();
                var roster = SortByClientNumber(LiveQuery(ctx, SqlTemplates.Data1Top(LiveRosterCap), 1));
                foreach (var client in roster)
                {
                    var cn = client.TryGetValue("clientNumber", out var v) ? (v as string)?.Trim() : null;
                    if (string.IsNullOrEmpty(cn)) continue;
                    string ck;
                    try { ck = LiveClientKey(cn!); }
                    catch (LacerteSDKError) { continue; } // a roster id that isn't a safe table prefix
                    var sql = table == 2 ? SqlTemplates.Data2NewForm(ck)
                        : table == 3 ? SqlTemplates.Data3NewForm(ck)
                        : SqlTemplates.Data4NewForm(ck);
                    try
                    {
                        foreach (var r in LiveQuery(ctx, sql, table, fresh))
                        {
                            if (!r.ContainsKey("clientNumber")) r["clientNumber"] = ck;
                            all.Add(r);
                        }
                    }
                    catch (LacerteSDKError ex) when (ex.StatusCode == StatusCodes.DBNotSupported)
                    {
                        // No per-client table for this client (nothing entered).
                    }
                }
                rows = all;
            }
            if (!string.IsNullOrWhiteSpace(stateAbbrev))
            {
                var st = stateAbbrev!.Trim();
                rows = rows.Where(r => r.TryGetValue("stateAbbrev", out var sv)
                                       && string.Equals(sv as string, st, StringComparison.OrdinalIgnoreCase));
            }
            return SortByClientNumber(rows).Cast<IReadOnlyDictionary<string, object?>>().ToList();
        }

        private static string RequireClientKey(string? key, int table)
        {
            if (key == null)
            {
                throw new LacerteSDKError(StatusCodes.InValidClientName,
                    new Dictionary<string, object?>
                    {
                        ["table"] = table,
                        ["reason"] = "Live reads of DATA" + table + " need a clientNumber (only DATA1 can be read for the whole firm at once).",
                    });
            }
            return key;
        }

        private IReadOnlyList<DetailRow> LiveReadDetail(SessionContext ctx, string clientNumber, int? seriesFilter, bool fresh = false)
        {
            var key = LiveClientKey(clientNumber);
            var sql = seriesFilter.HasValue
                ? SqlTemplates.DetailBySeries(key, seriesFilter.Value)
                : SqlTemplates.DetailAll(key);
            return LiveQuery(ctx, sql, table: 0, fresh: fresh).Select(r => ToDetailRow(r)).ToList();
        }

        // -------------------------------------------------------------------
        // FP-2 — live detail-row writes over ODBC
        //
        //   gate (LACERTE_LIVE_DETAIL_WRITES=1)
        //   -> build + validate the INSERT (nothing touched yet)
        //   -> per-client lock
        //   -> fresh read of the client's detail rows (baseline)
        //   -> first write to this client in this process: save that baseline
        //      as a JSON backup; can't save -> refuse
        //   -> INSERT in a separate worker process (a driver crash can't take
        //      the server down); never retried (append-only: a retry after a
        //      commit would duplicate the row)
        //   -> fresh read-back: rows equal to the written row must have grown
        //      by exactly one, else 9022
        // -------------------------------------------------------------------

        /// <summary>Default worker timeout; the worker is killed after it.</summary>
        internal static readonly TimeSpan DefaultDetailWriteTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Env override for the detail-backup directory.</summary>
        public const string DetailSnapshotDirEnvVar = "LACERTE_DETAIL_SNAPSHOT_DIR";

        /// <summary>FP-2 — live detail writes are refused (9202) unless this is true.</summary>
        internal bool LiveDetailWritesEnabled { get; set; }

        /// <summary>Directory the pre-write backups go to; null = none available (writes are refused).</summary>
        internal string? DetailSnapshotDirectory { get; set; }

        /// <summary>Test seam: runs the INSERT out of process. Production spawns this exe.</summary>
        internal IDetailWriteRunner DetailWriteRunner { get; set; } = new ProcessDetailWriteRunner();

        /// <summary>Worker timeout (test seam).</summary>
        internal TimeSpan DetailWriteTimeout { get; set; } = DefaultDetailWriteTimeout;

        // dataPath|year|module|client -> backup file written before the first write.
        private readonly ConcurrentDictionary<string, string> _detailSnapshots =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void InitLiveDetailWriteSettings(IDictionary<string, string?> env)
        {
            LiveDetailWritesEnabled = string.Equals(
                TryGet(env, Bridge.Writer.LiveDetailWritesEnvVar), "1", StringComparison.Ordinal);
            DetailSnapshotDirectory = ResolveDetailSnapshotDirectory(env);
        }

        /// <summary>
        /// <c>LACERTE_DETAIL_SNAPSHOT_DIR</c> when it is an absolute path, else
        /// <c>%APPDATA%\lacerte-mcp\detail-snapshots</c>; null when neither is
        /// available (live detail writes are then refused).
        /// </summary>
        public static string? ResolveDetailSnapshotDirectory(IDictionary<string, string?> env)
        {
            var explicitDir = TryGet(env, DetailSnapshotDirEnvVar);
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                try
                {
                    if (IsFullyQualifiedPath(explicitDir!.Trim()))
                    {
                        return System.IO.Path.GetFullPath(explicitDir.Trim());
                    }
                }
                catch (ArgumentException)
                {
                    // malformed — fall through to the default
                }
            }
            var appData = TryGet(env, "APPDATA");
            if (string.IsNullOrWhiteSpace(appData)) return null;
            return System.IO.Path.Combine(appData!, "lacerte-mcp", "detail-snapshots");
        }

        /// <summary>
        /// X:\... or \\server\share\... — not drive-relative "C:backups" or
        /// root-relative "\backups", which Path.IsPathRooted also accepts and
        /// which would land under the host's working directory.
        /// </summary>
        internal static bool IsFullyQualifiedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return path.Length > 2;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
                   && (path[2] == '\\' || path[2] == '/');
        }

        private static string DetailSnapshotKey(SessionContext ctx, string clientKey)
            => ctx.DataPath + "|" + ctx.Year.ToString(CultureInfo.InvariantCulture) + "|" + ctx.Module + "|" + clientKey;

        /// <summary>The backup taken for this client in this process, if any.</summary>
        internal string? KnownDetailSnapshot(SessionContext ctx, string clientNumber)
        {
            string key;
            try { key = LiveClientKey(clientNumber); }
            catch (LacerteSDKError) { return null; }
            return _detailSnapshots.TryGetValue(DetailSnapshotKey(ctx, key), out var p) ? p : null;
        }

        /// <summary>
        /// Live detail rows may only target a Series/Code (and Prefix/Suffix,
        /// when the entry pins them) that the module's VERIFIED field dictionary
        /// lists for the tax year: the SDK doesn't validate detail rows, so the
        /// server — not just the guided prompt — refuses codes nobody has
        /// mapped on this install (they would land on the wrong input line).
        /// </summary>
        private static void RequireDictionaryDetail(SessionContext ctx, DetailRow row)
        {
            int.TryParse((row.Series ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var series);
            int.TryParse((row.Code ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code);
            int? prefix = int.TryParse((row.Prefix ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : (int?)null;
            int? suffix = int.TryParse((row.Suffix ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : (int?)null;

            IReadOnlyList<Schema.FieldDictionaryEntry> entries;
            try
            {
                entries = Schema.FieldDictionaryLoader.GetEntries(ctx.Module);
            }
            catch (Exception)
            {
                entries = Array.Empty<Schema.FieldDictionaryEntry>();
            }
            var match = entries.FirstOrDefault(e => !e.IsField
                && e.Series == series && e.Code == code
                && (!e.Prefix.HasValue || e.Prefix == prefix)
                && (!e.Suffix.HasValue || e.Suffix == suffix)
                && e.AppliesToYear(ctx.Year));
            if (match != null) return;

            throw new LacerteSDKError(SyntheticStatusCodes.UnknownFieldAccess, new Dictionary<string, object?>
            {
                ["op"] = "WriteDetail",
                ["module"] = ctx.Module.ToString(),
                ["taxYear"] = ctx.Year,
                ["series"] = row.Series,
                ["code"] = row.Code,
                ["reason"] = "This detail Series/Code isn't in the verified field dictionary for this module and year, so it isn't written to a real return. "
                    + "Check lacerte_field_dictionary for the mapped codes; an operator adds new ones on the Lacerte PC with `lacerte-mcp map-diff`. Enter this amount in Lacerte meanwhile.",
            });
        }

        /// <summary>
        /// Live dry run: validates the row and builds the INSERT exactly as the
        /// commit would — no read, no backup, no worker.
        /// </summary>
        private DetailWriteOutcome LivePreviewDetailRow(SessionContext ctx, string clientNumber, DetailRow row)
        {
            var key = LiveClientKey(clientNumber);
            DetailRowWriter.RequireLiveSuffix(row);
            RequireDictionaryDetail(ctx, row);
            DetailRowWriter.BuildInsertSql(key, row);
            return new DetailWriteOutcome(StatusCodes.Success, KnownDetailSnapshot(ctx, key), null,
                "Dry run: the row passed validation and the INSERT was built; nothing was written and no backup was taken.");
        }

        /// <summary>
        /// Live commit of one detail row (see the section comment above).
        /// Synchronous on purpose: it runs under the per-client write lock.
        /// </summary>
        private DetailWriteOutcome LiveCommitDetailRow(
            SessionContext ctx, string clientNumber, DetailRow row, System.Threading.CancellationToken cancellationToken,
            string? idempotencyKey = null)
        {
            var key = LiveClientKey(clientNumber);
            // Validate + build before touching anything.
            DetailRowWriter.RequireLiveSuffix(row);
            RequireDictionaryDetail(ctx, row);
            var sql = DetailRowWriter.BuildInsertSql(key, row);

            lock (GetClientWriteLock(key))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Baseline over a fresh connection.
                IReadOnlyList<DetailRow> before;
                try
                {
                    before = LiveReadDetail(ctx, key, seriesFilter: null, fresh: true);
                }
                catch (LacerteSDKError ex) when (ex.StatusCode == StatusCodes.DBNotSupported)
                {
                    // No [<client>-Detail] table yet (a new client, nothing
                    // entered): the baseline is empty, like the read path
                    // treats it; the INSERT decides whether it can create it.
                    before = Array.Empty<DetailRow>();
                }
                catch (LacerteSDKError ex)
                {
                    var c = CopyContext(ex.Context);
                    c["op"] = "WriteDetail";
                    c["clientNumber"] = key;
                    c["reason"] = "Refused before writing: the client's detail rows could not be read, so no backup or read-back baseline could be taken. Nothing was written. (" + ex.StatusName + ")";
                    throw new LacerteSDKError(ex.StatusCode, c, ex);
                }

                // 2. Backup before the first write to this client in this process.
                var snapshot = EnsureDetailSnapshot(ctx, key, before);

                cancellationToken.ThrowIfCancellationRequested();
                int matchingBefore = DetailRowWriter.CountMatching(before, row);

                // 3. INSERT out of process. Never retried.
                DetailWriteRunResult run;
                try
                {
                    run = DetailWriteRunner.Run(LiveConnectionString(ctx), sql, DetailWriteTimeout);
                }
                catch (Exception ex) when (!(ex is LacerteSDKError))
                {
                    run = new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = ex.GetType().Name + ": " + ex.Message };
                }
                if (run == null)
                {
                    run = new DetailWriteRunResult { Status = DetailWriteRunStatus.NotStarted, Error = "The worker runner returned no result." };
                }
                if (run.Status != DetailWriteRunStatus.Ok)
                {
                    throw DetailWorkerFailure(ctx, key, row, run, snapshot, matchingBefore, sql, idempotencyKey);
                }

                // 4. Read-back over a fresh connection.
                IReadOnlyList<DetailRow> after;
                try
                {
                    after = LiveReadDetail(ctx, key, seriesFilter: null, fresh: true);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    var why = ex is LacerteSDKError sdk ? sdk.StatusName : ex.GetType().Name;
                    var readBackNote = "Not verified: the INSERT reported success but reading the detail rows back failed (" + why
                        + "). Check with lacerte_detail_read before writing this row again.";
                    var jn = AppendDetailJournal(ctx, key, row, sql, snapshot, idempotencyKey,
                        DetailWriteJournal.OutcomeUnverified, "read-back failed (" + why + ")");
                    return new DetailWriteOutcome(StatusCodes.Success, snapshot, null,
                        jn == null ? readBackNote : readBackNote + " " + jn);
                }

                int matchingAfter = DetailRowWriter.CountMatching(after, row);
                if (matchingAfter - matchingBefore == 1)
                {
                    // 5. Journal (restore). A failure here never fails the write.
                    var jn = AppendDetailJournal(ctx, key, row, sql, snapshot, idempotencyKey,
                        DetailWriteJournal.OutcomeVerified, null);
                    return new DetailWriteOutcome(StatusCodes.Success, snapshot, true, jn);
                }

                // The worker reported the INSERT ran: journal it as unverified
                // so a restore can find it if it is in the file.
                var journalNote = AppendDetailJournal(ctx, key, row, sql, snapshot, idempotencyKey,
                    DetailWriteJournal.OutcomeUnverified,
                    "read-back: matching rows " + matchingBefore.ToString(CultureInfo.InvariantCulture)
                    + " -> " + matchingAfter.ToString(CultureInfo.InvariantCulture));
                var notVerified = new Dictionary<string, object?>
                    {
                        ["op"] = "WriteDetail",
                        ["clientNumber"] = key,
                        ["series"] = row.Series,
                        ["code"] = row.Code,
                        ["matchingRowsBefore"] = matchingBefore,
                        ["matchingRowsAfter"] = matchingAfter,
                        ["totalRowsBefore"] = before.Count,
                        ["totalRowsAfter"] = after.Count,
                        ["rowsAffected"] = run.RowsAffected,
                        ["snapshot"] = snapshot,
                        ["reason"] = matchingAfter - matchingBefore == 0
                            ? "The INSERT reported success, but no new row equal to the written row was read back (the driver may have changed or truncated a value, e.g. a Description longer than the field allows). The detail file may now hold a row that differs from what was requested."
                            : "The number of rows equal to the written row changed by " + (matchingAfter - matchingBefore).ToString(CultureInfo.InvariantCulture)
                              + " instead of 1 (another writer — Lacerte's UI or a second server — may have changed this client's detail rows at the same time).",
                    };
                if (journalNote != null) notVerified["journal"] = journalNote;
                throw new LacerteSDKError(SyntheticStatusCodes.WriteNotVerified, notVerified);
            }
        }

        /// <summary>
        /// Maps a failed worker run to a <see cref="LacerteSDKError"/>. For an
        /// outcome where the INSERT may or may not have committed (crash,
        /// timeout, driver error) the detail rows are re-read once to tell the
        /// operator whether the row is there, so nobody retries blindly.
        /// </summary>
        private LacerteSDKError DetailWorkerFailure(
            SessionContext ctx, string key, DetailRow row, DetailWriteRunResult run, string snapshot, int matchingBefore,
            string? sql = null, string? idempotencyKey = null)
        {
            string rowWritten;
            if (run.Status == DetailWriteRunStatus.NotStarted)
            {
                rowWritten = "no";
            }
            else
            {
                try
                {
                    var probe = LiveReadDetail(ctx, key, seriesFilter: null, fresh: true);
                    int delta = DetailRowWriter.CountMatching(probe, row) - matchingBefore;
                    rowWritten = delta == 0 ? "no" : delta == 1 ? "yes" : "unknown";
                    // A worker that timed out may not have died yet (I/O stuck
                    // on a file share) and can still commit after this read.
                    if (run.Status == DetailWriteRunStatus.TimedOut && rowWritten == "no") rowWritten = "unknown";
                }
                catch (Exception)
                {
                    rowWritten = "unknown";
                }
            }
            string landed = rowWritten == "yes"
                ? " A re-read shows the row IS in the detail file — do not write it again."
                : rowWritten == "no"
                    ? " A re-read shows the row is NOT in the detail file."
                    : " Whether the row reached the detail file is unknown — check with lacerte_detail_read before writing it again.";

            var c = new Dictionary<string, object?>
            {
                ["op"] = "WriteDetail",
                ["clientNumber"] = key,
                ["series"] = row.Series,
                ["code"] = row.Code,
                ["worker"] = run.Status.ToString(),
                ["rowWritten"] = rowWritten,
                ["snapshot"] = snapshot,
            };
            if (run.ExitCode.HasValue) c["workerExitCode"] = run.ExitCode.Value;
            if (rowWritten == "yes" && sql != null)
            {
                // The row is in the file despite the error: journal it so a
                // restore can remove it.
                var jn = AppendDetailJournal(ctx, key, row, sql, snapshot, idempotencyKey,
                    DetailWriteJournal.OutcomeLandedAfterError, "worker " + run.Status.ToString());
                if (jn != null) c["journal"] = jn;
            }

            switch (run.Status)
            {
                case DetailWriteRunStatus.Failed when !string.IsNullOrEmpty(run.SqlState):
                {
                    var mapped = OdbcErrorMapper.MapSqlState(run.SqlState!, run.Error ?? string.Empty);
                    c["sqlState"] = run.SqlState;
                    c["odbcMessage"] = run.Error;
                    var hint = OdbcErrorMapper.GuidanceFor(run.SqlState);
                    if (hint != null) c["hint"] = hint;
                    c["reason"] = "The Lacerte ODBC driver rejected the INSERT (SQLSTATE " + run.SqlState + "). The MCP server is unaffected." + landed;
                    return new LacerteSDKError(mapped.StatusCode, c);
                }
                case DetailWriteRunStatus.Failed:
                    c["errorType"] = run.ErrorType;
                    c["odbcMessage"] = run.Error;
                    c["reason"] = "The detail-write worker could not run the INSERT (" + (run.ErrorType ?? "error") + "). The MCP server is unaffected." + landed;
                    return new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable, c);
                case DetailWriteRunStatus.TimedOut:
                    c["reason"] = "The Lacerte ODBC driver did not finish the INSERT within "
                        + DetailWriteTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " s; the worker process was stopped. The MCP server is unaffected." + landed;
                    return new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable, c);
                case DetailWriteRunStatus.NotStarted:
                    c["reason"] = "The detail-write worker process could not be started (" + run.Error + "). Nothing was written; the MCP server is unaffected.";
                    return new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable, c);
                default:
                    c["reason"] = "The Lacerte ODBC driver failed or crashed during the INSERT (the worker process exited"
                        + (run.ExitCode.HasValue ? " with code " + run.ExitCode.Value.ToString(CultureInfo.InvariantCulture) : string.Empty)
                        + " without a result). It ran in a separate process, so the MCP server is unaffected." + landed;
                    return new LacerteSDKError(SyntheticStatusCodes.BridgeUnavailable, c);
            }
        }

        /// <summary>
        /// Saves <paramref name="rows"/> (the client's detail rows before any MCP
        /// write in this process) as a JSON backup the first time; returns the
        /// existing path afterwards. Throws when the backup can't be written, so
        /// the write is refused.
        /// </summary>
        private string EnsureDetailSnapshot(SessionContext ctx, string key, IReadOnlyList<DetailRow> rows)
        {
            var snapKey = DetailSnapshotKey(ctx, key);
            if (_detailSnapshots.TryGetValue(snapKey, out var existing)) return existing;

            var dir = DetailSnapshotDirectory;
            if (string.IsNullOrWhiteSpace(dir))
            {
                throw SnapshotRefusal(key, null,
                    "no backup directory is available (set " + DetailSnapshotDirEnvVar + " to an absolute path, or make sure APPDATA is set)");
            }

            var now = DateTimeOffset.Now;
            var baseName = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + ctx.Module
                + (ctx.Year % 100).ToString("00", CultureInfo.InvariantCulture) + "-" + key;
            byte[] PayloadFor(string fileName) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "lacerte-mcp detail-row backup",
                version = 1,
                takenAt = now.ToString("o", CultureInfo.InvariantCulture),
                dataPath = ctx.DataPath,
                taxYear = ctx.Year,
                module = ctx.Module.ToString(),
                clientNumber = key,
                table = "[" + key + "-Detail]",
                rowCount = rows.Count,
                note = "The client's detail rows as read just before the first lacerte_detail_write to this client by this server process. Rows written afterwards are not in this file.",
                // Added after version 1 shipped; readers that don't know it ignore it.
                journal = DetailWriteJournal.JournalFileNameFor(fileName),
                journalNote = "Every row this server process INSERTED for this client is appended to the journal next to this file; `lacerte-mcp detail-restore` uses both to undo those rows.",
                rows,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            string path;
            try
            {
                System.IO.Directory.CreateDirectory(dir!);
                path = WriteNewFile(dir!, baseName, PayloadFor);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException
                                       || ex is ArgumentException || ex is NotSupportedException
                                       || ex is System.Security.SecurityException)
            {
                throw SnapshotRefusal(key, dir, ex.GetType().Name + ": " + ex.Message);
            }
            _detailSnapshots[snapKey] = path;
            return path;
        }

        private static string WriteNewFile(string dir, string baseName, Func<string, byte[]> payloadFor)
        {
            for (int n = 1; ; n++)
            {
                var name = n == 1 ? baseName + ".json" : baseName + "-" + n.ToString(CultureInfo.InvariantCulture) + ".json";
                var path = System.IO.Path.Combine(dir, name);
                var payload = payloadFor(name);
                try
                {
                    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None))
                    {
                        fs.Write(payload, 0, payload.Length);
                        fs.Flush(true);
                    }
                    return path;
                }
                catch (System.IO.IOException) when (n < 20 && System.IO.File.Exists(path))
                {
                    // name taken (same second) — try the next suffix
                }
            }
        }

        private static LacerteSDKError SnapshotRefusal(string key, string? dir, string why)
            => new LacerteSDKError(SyntheticStatusCodes.InternalServerError,
                new Dictionary<string, object?>
                {
                    ["op"] = "WriteDetail",
                    ["clientNumber"] = key,
                    ["snapshotDirectory"] = dir,
                    ["reason"] = "Refused before writing: the backup of this client's detail rows could not be saved (" + why + "). Nothing was written. Fix the backup directory and retry.",
                });

        /// <summary>Test seam: appends one journal line (path, UTF-8 line incl. newline). Production: <see cref="DetailWriteJournal.AppendLine"/>.</summary>
        internal Action<string, byte[]> DetailJournalAppender { get; set; } = DetailWriteJournal.AppendLine;

        /// <summary>Test seam: where a journal failure is logged. Production: stderr (stdout is the MCP channel).</summary>
        internal Action<string> DetailJournalLog { get; set; } = msg =>
        {
            try { Console.Error.WriteLine(msg); } catch (Exception) { /* no stderr */ }
        };

        /// <summary>
        /// Appends one line to the restore journal next to <paramref name="snapshot"/>
        /// for a row the worker INSERTED (runs under the per-client lock). Never
        /// throws: a journal failure must not turn a written row into a failed
        /// write. Returns null on success, else a note for the caller to surface.
        /// </summary>
        private string? AppendDetailJournal(SessionContext ctx, string key, DetailRow row, string sql, string snapshot,
            string? idempotencyKey, string outcome, string? note)
        {
            string journalPath = snapshot;
            try
            {
                journalPath = DetailWriteJournal.JournalPathFor(snapshot);
                var entry = new DetailJournalEntry
                {
                    EntryId = Guid.NewGuid().ToString("N"),
                    At = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                    DataPath = ctx.DataPath,
                    TaxYear = ctx.Year,
                    Module = ctx.Module.ToString(),
                    ClientNumber = key,
                    Snapshot = System.IO.Path.GetFileName(snapshot),
                    Row = row,
                    InsertSql = sql,
                    IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
                    Outcome = outcome,
                    Note = note,
                };
                DetailJournalAppender(journalPath, DetailWriteJournal.SerializeLine(entry));
                return null;
            }
            catch (Exception ex)
            {
                var msg = "The row was written, but recording it in the restore journal (" + journalPath + ") failed ("
                    + ex.GetType().Name + ": " + ex.Message + "), so `lacerte-mcp detail-restore` will not know about it; "
                    + "if it ever needs undoing, remove it in Lacerte by hand.";
                try { DetailJournalLog("[lacerte-mcp] detail-write journal append failed for client " + key + ": " + ex); }
                catch (Exception) { /* logging must not fail the write */ }
                return msg;
            }
        }

        private static Dictionary<string, object?> CopyContext(IReadOnlyDictionary<string, object?>? source)
        {
            var d = new Dictionary<string, object?>();
            if (source != null)
            {
                foreach (var kv in source) d[kv.Key] = kv.Value;
            }
            return d;
        }

        private IReadOnlyList<IReadOnlyDictionary<string, object?>> LiveReadEFStatus(SessionContext ctx, DateTime? sinceUtc)
        {
            DateTime? since = sinceUtc.HasValue && sinceUtc.Value.Kind == DateTimeKind.Local
                ? sinceUtc.Value.ToUniversalTime()
                : sinceUtc;
            var sql = since.HasValue ? SqlTemplates.EfStatusJoinSince(since.Value) : SqlTemplates.EfStatusJoin;
            return LiveQuery(ctx, sql, table: 0)
                // The SQL push-down is day-granular; apply the same exact
                // watermark filter sim uses so a poll doesn't re-deliver the day.
                .Where(r => !since.HasValue
                            || (TryGetCI(r, "StatusDate", out var sd) && StatusDateOnOrAfter(sd, since.Value)))
                .Select(r =>
                {
                    // Same six keys, same casing as the sim projection.
                    var o = new Dictionary<string, object?>();
                    foreach (var k in new[] { "ClientId", "EFilingId", "EFId", "InstanceId", "Status", "StatusDate" })
                    {
                        TryGetCI(r, k, out var v);
                        o[k] = v is string s ? s.Trim() : v;
                    }
                    return (IReadOnlyDictionary<string, object?>)o;
                })
                .ToList();
        }
    }
}
