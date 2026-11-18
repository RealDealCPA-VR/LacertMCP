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

        private IReadOnlyList<DetailRow> LiveReadDetail(SessionContext ctx, string clientNumber, int? seriesFilter)
        {
            var key = LiveClientKey(clientNumber);
            var sql = seriesFilter.HasValue
                ? SqlTemplates.DetailBySeries(key, seriesFilter.Value)
                : SqlTemplates.DetailAll(key);
            return LiveQuery(ctx, sql, table: 0).Select(r => ToDetailRow(r)).ToList();
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
