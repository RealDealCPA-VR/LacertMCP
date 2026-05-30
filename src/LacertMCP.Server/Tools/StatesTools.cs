using System.ComponentModel;
using System.Threading.Tasks;
using LacertMCP.Server.Errors;
using LacertMCP.Server.Session;
using ModelContextProtocol.Server;

namespace LacertMCP.Server.Tools
{
    /// <summary>
    /// DATA3 (State Client Info) and DATA4 (State SubClient Info) read tools.
    /// Per <c>Todo.md</c> Phase 5 task #45.
    /// </summary>
    [McpServerToolType]
    public sealed class StatesTools
    {
        private readonly LacerteSessionManager _session;

        public StatesTools(LacerteSessionManager session) => _session = session;

        [McpServerTool]
        [Description("Read DATA3 state rows for one client. Each row carries state-level overrides (apportionment, addbacks, residency) keyed by (ClientNumber, StateAbbrev).")]
        public Task<string> LacerteStateRead(
            [Description("The ClientNumber whose state rows to read.")] string clientNumber,
            [Description("Optional 2-letter state abbreviation filter (e.g. \"CA\"). If omitted, returns all state rows for the client.")] string? stateAbbrev = null)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadTable(3, clientNumber, stateAbbrev).ConfigureAwait(false);
                return new
                {
                    clientNumber,
                    stateAbbrev,
                    count = rows.Count,
                    states = rows,
                };
            });

        [McpServerTool]
        [Description("Read DATA4 state-subclient rows for one client. Each row is a per-state override on a subclient (e.g. partner-level state apportionment) keyed by (ClientNumber, SubClientID, StateAbbrev).")]
        public Task<string> LacerteStateSubclientRead(
            [Description("The ClientNumber whose state-subclient rows to read.")] string clientNumber,
            [Description("Optional 2-letter state abbreviation filter. If omitted, returns all states.")] string? stateAbbrev = null)
            => WrapToolHandler.Run(async () =>
            {
                var rows = await _session.ReadTable(4, clientNumber, stateAbbrev).ConfigureAwait(false);
                return new
                {
                    clientNumber,
                    stateAbbrev,
                    count = rows.Count,
                    stateSubclients = rows,
                };
            });
    }
}
