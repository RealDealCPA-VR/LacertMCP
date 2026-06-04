using System.Collections.Generic;

namespace LacertMCP.Server.Session
{
    /// <summary>
    /// Resolves a sim call to a specific <c>(datapath, year, module)</c> cell.
    /// Carries the same triple the live path resolver uses so the same record
    /// can be handed to either backend. <c>ReadOnly</c> is reserved for the
    /// Phase 3 session-manager read-only gate; the sim methods that accept a
    /// context ignore it today (the gate fires in the session manager before
    /// the sim is reached).
    /// </summary>
    public sealed record SimulationContext(
        string DataPath,
        int Year,
        char Module,
        bool ReadOnly = false);

    /// <summary>
    /// Arguments bag for <see cref="SimulationStore.SimulateCreateClient"/>.
    /// Mirrors the shape of <c>IClientList.SetDataPath -> IClient(...) -> CreateClient</c>
    /// in the live COM bridge so the wire-shape stays observationally identical
    /// across modes.
    /// <para>
    /// P16-054/P16-080 — ClientID/EmailID renamed to ClientId/EmailId so the
    /// internal C# casing matches the canonical wire form (camelCase clientId/
    /// emailId). One canonical casing across the whole codebase.
    /// </para>
    /// </summary>
    public sealed record SimulateCreateClientArgs(
        string DataPath,
        int Year,
        char Module,
        string ClientId,
        string TaxType,
        string? FirstName = null,
        string? LastName = null,
        string? EmailId = null);

    /// <summary>
    /// Deep-clone snapshot of the entire simulation store. Returned by
    /// <see cref="SimulationStore.Snapshot"/>; consumed by
    /// <see cref="SimulationStore.Restore"/>. Carries enough state that a
    /// snapshot → mutate → restore round-trip is observationally a no-op:
    /// every <see cref="ModuleStore"/> is cloned in full, plus the monotonic
    /// counters and the auth latches.
    /// </summary>
    public sealed class SimulationSnapshot
    {
        /// <summary>
        /// datapath → year → module → (cloned) <see cref="ModuleStore"/>.
        /// Plain <see cref="Dictionary{TKey,TValue}"/> on purpose — the
        /// snapshot is immutable from the caller's view once handed back.
        /// </summary>
        public Dictionary<string, Dictionary<int, Dictionary<char, ModuleStore>>> Root { get; set; }
            = new Dictionary<string, Dictionary<int, Dictionary<char, ModuleStore>>>();

        /// <summary>Cloned value of the registered latch at snapshot time.</summary>
        public bool Registered { get; set; }

        /// <summary>Cloned value of the sessionExpired latch at snapshot time.</summary>
        public bool SessionExpired { get; set; }
    }
}
