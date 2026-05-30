using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LacertMCP.Server.Session
{
    // Partial of SimulationStore that owns the SQL parser + ProcessQuery
    // implementation. Per agent 06 §3 + agent 03 §4, we DO NOT parse
    // arbitrary SQL — we pattern-match the small set of documented
    // templates and reject anything else. Both pre-TY21 and TY21+ forms
    // read from the same canonical Maps; only the projection differs.
    public sealed partial class SimulationStore
    {
        // ---------------------------------------------------------------
        // Templates (from agent 03 §4):
        //
        //   Pre-TY21 form (legacy global tables, WHERE on the client/state):
        //     select * from [DATA1-Client Info]
        //     select * from [DATA1-Client Info] where C1_0 = '{client}'
        //     select * from [DATA2-Subclient Info] where C2_0 = '{client}'
        //     select * from [DATA3-State Client Info] where C3_0 = '{client}'
        //     select * from [DATA3-State Client Info] where C3_0 = '{client}' and C3_G1 = '{state}'
        //     select * from [DATA4-State Subclient Info] where C4_0 = '{client}' and C4_G1 = '{state}'
        //     select * from [DATA5-Status Info] where C5_0 = '{client}'
        //     select * from [DATA6-Display Info] where C6_0 = '{client}'
        //     select * from [DATA7-Form Info] where C7_0 = '{client}'
        //
        //   TY21+ form (DATA2/3/4 became per-client tables prefixed by client number):
        //     select * from [{client}-SubClientInfo]
        //     select * from [{client}-StateClientInfo]
        //     select * from [{client}-StateSubClientInfo]
        //   (DATA1/5/6/7 unchanged in the spec — keep legacy form for those.)
        //
        // We compile two regex sets and try the new form first when
        // year >= 2021 AND table ∈ {2, 3, 4}.
        // ---------------------------------------------------------------

        private static readonly RegexOptions _rxOpts =
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // Pre-TY21 templates
        private static readonly Regex _rxData1All = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA1-Client Info\]\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData1One = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA1-Client Info\]\s+where\s+C1_0\s*=\s*'(?<client>[^']+)'\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData2Old = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA2-Subclient Info\]\s+where\s+C2_0\s*=\s*'(?<client>[^']+)'\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData3Old = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA3-State Client Info\]\s+where\s+C3_0\s*=\s*'(?<client>[^']+)'(?:\s+and\s+C3_G1\s*=\s*'(?<state>[^']+)')?\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData4Old = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA4-State Subclient Info\]\s+where\s+C4_0\s*=\s*'(?<client>[^']+)'(?:\s+and\s+C4_G1\s*=\s*'(?<state>[^']+)')?\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData5 = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA5-Status Info\]\s+where\s+C5_0\s*=\s*'(?<client>[^']+)'\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData6 = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA6-Display Info\]\s+where\s+C6_0\s*=\s*'(?<client>[^']+)'\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData7 = new Regex(
            @"^\s*select\s+\*\s+from\s+\[DATA7-Form Info\]\s+where\s+C7_0\s*=\s*'(?<client>[^']+)'\s*;?\s*$", _rxOpts);

        // TY21+ per-client templates
        private static readonly Regex _rxData2New = new Regex(
            @"^\s*select\s+\*\s+from\s+\[(?<client>[^\]\-]+)-SubClientInfo\]\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData3New = new Regex(
            @"^\s*select\s+\*\s+from\s+\[(?<client>[^\]\-]+)-StateClientInfo\]\s*;?\s*$", _rxOpts);

        private static readonly Regex _rxData4New = new Regex(
            @"^\s*select\s+\*\s+from\s+\[(?<client>[^\]\-]+)-StateSubClientInfo\]\s*;?\s*$", _rxOpts);

        /// <summary>
        /// Pattern-match a documented SQL template and dispatch to the
        /// canonical store. Both pre-TY21 and TY21+ forms produce the same
        /// <see cref="DbfRow"/> shape — only the SELECT surface changed
        /// in TY21, not the underlying DBF (per agent 06 §3). Returns empty
        /// list when the table-cell isn't seeded; throws
        /// <see cref="ArgumentException"/> if the SQL doesn't match any
        /// documented template (sim refuses to parse arbitrary SQL).
        /// </summary>
        public IReadOnlyList<DbfRow> ProcessQuery(string sql, SimulationContext ctx)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            var store = TryGetModule(ctx.DataPath, ctx.Year, ctx.Module);
            // No cell → no rows. Live ODBC would surface an empty result-set
            // here too (the table file exists but has no rows for this client).
            if (store == null) return Array.Empty<DbfRow>();

            // DATA1 — global; same template both forms.
            Match m;
            if ((m = _rxData1All.Match(sql)).Success)
            {
                var rows = new List<DbfRow>(store.Clients.Count);
                foreach (var kv in store.Clients) rows.Add(kv.Value);
                return rows;
            }
            if ((m = _rxData1One.Match(sql)).Success)
            {
                var client = m.Groups["client"].Value;
                return store.Clients.TryGetValue(client, out var row)
                    ? new List<DbfRow> { row }
                    : (IReadOnlyList<DbfRow>)Array.Empty<DbfRow>();
            }

            // DATA2 — TY21+ first when applicable.
            if (ctx.Year >= 2021 && (m = _rxData2New.Match(sql)).Success)
            {
                return SubClientsFor(store, m.Groups["client"].Value);
            }
            if ((m = _rxData2Old.Match(sql)).Success)
            {
                return SubClientsFor(store, m.Groups["client"].Value);
            }

            // DATA3
            if (ctx.Year >= 2021 && (m = _rxData3New.Match(sql)).Success)
            {
                return StatesFor(store, m.Groups["client"].Value, stateFilter: null);
            }
            if ((m = _rxData3Old.Match(sql)).Success)
            {
                var client = m.Groups["client"].Value;
                var state = m.Groups["state"].Success ? m.Groups["state"].Value : null;
                return StatesFor(store, client, state);
            }

            // DATA4
            if (ctx.Year >= 2021 && (m = _rxData4New.Match(sql)).Success)
            {
                return StateSubsFor(store, m.Groups["client"].Value, stateFilter: null);
            }
            if ((m = _rxData4Old.Match(sql)).Success)
            {
                var client = m.Groups["client"].Value;
                var state = m.Groups["state"].Success ? m.Groups["state"].Value : null;
                return StateSubsFor(store, client, state);
            }

            // DATA5/6/7 — unchanged in TY21+.
            if ((m = _rxData5.Match(sql)).Success)
            {
                var client = m.Groups["client"].Value;
                return store.Statuses.TryGetValue(client, out var row)
                    ? new List<DbfRow> { row }
                    : (IReadOnlyList<DbfRow>)Array.Empty<DbfRow>();
            }
            if ((m = _rxData6.Match(sql)).Success)
            {
                var client = m.Groups["client"].Value;
                return store.Displays.TryGetValue(client, out var row)
                    ? new List<DbfRow> { row }
                    : (IReadOnlyList<DbfRow>)Array.Empty<DbfRow>();
            }
            if ((m = _rxData7.Match(sql)).Success)
            {
                var client = m.Groups["client"].Value;
                return store.Forms.TryGetValue(client, out var rows)
                    ? (IReadOnlyList<DbfRow>)rows
                    : Array.Empty<DbfRow>();
            }

            throw new ArgumentException(
                $"SimulationStore.ProcessQuery: SQL does not match any documented template (agent 03 §4). " +
                $"Got: {sql}",
                nameof(sql));
        }

        // ---- table-specific projection helpers ----

        private static IReadOnlyList<DbfRow> SubClientsFor(ModuleStore store, string client)
        {
            var rows = new List<DbfRow>();
            foreach (var kv in store.SubClients)
            {
                if (string.Equals(kv.Key.Client, client, StringComparison.OrdinalIgnoreCase))
                    rows.Add(kv.Value);
            }
            return rows;
        }

        private static IReadOnlyList<DbfRow> StatesFor(ModuleStore store, string client, string? stateFilter)
        {
            var rows = new List<DbfRow>();
            foreach (var kv in store.States)
            {
                if (!string.Equals(kv.Key.Client, client, StringComparison.OrdinalIgnoreCase)) continue;
                if (stateFilter != null && !string.Equals(kv.Key.State, stateFilter, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(kv.Value);
            }
            return rows;
        }

        private static IReadOnlyList<DbfRow> StateSubsFor(ModuleStore store, string client, string? stateFilter)
        {
            var rows = new List<DbfRow>();
            foreach (var kv in store.StateSubs)
            {
                if (!string.Equals(kv.Key.Client, client, StringComparison.OrdinalIgnoreCase)) continue;
                if (stateFilter != null && !string.Equals(kv.Key.State, stateFilter, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(kv.Value);
            }
            return rows;
        }
    }
}
