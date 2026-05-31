using System.Collections.Generic;
// SeedData lives in a child namespace; explicitly import the parent so we
// can construct DetailRow / DbfRow / ModuleStore without fully-qualifying.
using LacertMCP.Server.Session;

namespace LacertMCP.Server.Session.Fixtures
{
    /// <summary>
    /// Hand-authored seed fixture for <see cref="SimulationStore"/>. Eight
    /// clients spanning the TY20/TY24 boundary and three modules (I/C/P) so
    /// every read path (DATA1..7 + details + EF), every TY21+ branching
    /// codepath, and most error paths have a target in the in-memory store.
    ///
    /// Roster (from <c>docs/research/agents/06-simulation-mode.md</c> §2):
    ///
    /// | Datapath | Year | Module | Clients                                                       |
    /// |----------|------|--------|---------------------------------------------------------------|
    /// | IData    | 2020 | I      | SMIT (single), JONE (joint, 1 dep), REJE (EF rejected)        |
    /// | IData    | 2024 | I      | BROW (joint, 2 deps), PEND (EF Transmitted)                   |
    /// | CData    | 2024 | C      | ACME (1120, CA+NY), WIDG (1120, single state)                 |
    /// | PData    | 2024 | P      | PART (1065, 3 partners → DATA2 subclients)                    |
    ///
    /// Datapath strings follow the sim-canonical form
    /// <c>"C:\\Lacerte\\{YY}Tax\\{Module}Data"</c>.
    /// </summary>
    public static class SeedData
    {
        // Canonical datapath strings used by the seed and assertable from tests.
        public const string DataPathIData2020 = @"C:\Lacerte\20Tax\IData";
        public const string DataPathIData2024 = @"C:\Lacerte\24Tax\IData";
        public const string DataPathCData2024 = @"C:\Lacerte\24Tax\CData";
        public const string DataPathPData2024 = @"C:\Lacerte\24Tax\PData";

        // ---- TY20 Individual ----
        public static readonly IndividualClient SMIT = new IndividualClient(
            ClientNumber: "1001", ClientID: "SMIT",
            FirstName: "John", LastName: "Smith",
            FilingStatus: "Single", Dependents: 0,
            Email: "john.smith@example.com",
            EFStatus: null);

        public static readonly IndividualClient JONE = new IndividualClient(
            ClientNumber: "1002", ClientID: "JONE",
            FirstName: "Mary", LastName: "Jones",
            FilingStatus: "Joint", Dependents: 1,
            Email: "mary.jones@example.com",
            EFStatus: null);

        public static readonly IndividualClient REJE = new IndividualClient(
            ClientNumber: "1003", ClientID: "REJE",
            FirstName: "Robert", LastName: "Rejected",
            FilingStatus: "Single", Dependents: 1,
            Email: "robert.rejected@example.com",
            EFStatus: "Rejected");

        // ---- TY24 Individual ----
        public static readonly IndividualClient BROW = new IndividualClient(
            ClientNumber: "1001", ClientID: "BROW",
            FirstName: "Bill", LastName: "Brown",
            FilingStatus: "Joint", Dependents: 2,
            Email: "bill.brown@example.com",
            EFStatus: null);

        public static readonly IndividualClient PEND = new IndividualClient(
            ClientNumber: "1002", ClientID: "PEND",
            FirstName: "Patty", LastName: "Pending",
            FilingStatus: "Joint", Dependents: 0,
            Email: "patty.pending@example.com",
            EFStatus: "Transmitted");

        // ---- TY24 Corporate ----
        public static readonly CorpClient ACME = new CorpClient(
            ClientNumber: "1001", ClientID: "ACME",
            CorpName: "ACME Industries Inc",
            Email: "ein@acme.example.com",
            States: new[] { "CA", "NY" });

        public static readonly CorpClient WIDG = new CorpClient(
            ClientNumber: "1002", ClientID: "WIDG",
            CorpName: "Widget Holdings LLC",
            Email: "ein@widget.example.com",
            States: new[] { "TX" });

        // ---- TY24 Partnership ----
        public static readonly PartnershipClient PART = new PartnershipClient(
            ClientNumber: "1001", ClientID: "PART",
            PartnershipName: "Partner Ventures LP",
            Email: "info@partner.example.com",
            Partners: new[]
            {
                new Partner(SubId: 1, Name: "Alice Partner", SharePct: 50.0),
                new Partner(SubId: 2, Name: "Bob Partner",   SharePct: 30.0),
                new Partner(SubId: 3, Name: "Carol Partner", SharePct: 20.0),
            });

        // ----------------------------------------------------------------
        // Detail-row starter sets. 2-3 rows per client is enough to exercise
        // read/write paths; expand to 5-15 once the chokepoint methods land.
        //
        // Series IDs (P14-055): the detail-file Series column is a NUMERIC
        // Lacerte screen identifier per spec §2 and the live ODBC wire
        // shape (agent 03 §4 example: `Series = 11`). DetailRow.Series is
        // typed `string` to preserve the wire shape, but seed values use
        // numeric strings so the in-memory store is observationally
        // identical to live for any future SQL-driven detail-row read
        // path (e.g. SqlTemplates.DetailBySeries which emits
        // `where Series = 11`). The SeriesIds class below names the
        // documented IDs for readability.
        // ----------------------------------------------------------------

        /// <summary>
        /// Named constants for the documented Lacerte detail-file Series IDs
        /// (spec §2 / agent 03 §4). Stored as numeric strings — the wire
        /// shape of <see cref="DetailRow.Series"/> is <c>string</c>, but the
        /// values are guaranteed parseable integers so SQL templates that
        /// emit unquoted integer literals (e.g. <c>where Series = 11</c>)
        /// match the in-memory rows correctly. Source: audit task P14-055,
        /// Lacerte SDK Developer Instructions §2 detail-file column table.
        /// </summary>
        public static class SeriesIds
        {
            public const string Wages   = "11"; // W-2 income
            public const string Int     = "12"; // Interest income (1099-INT)
            public const string Div     = "13"; // Dividend income (1099-DIV)
            public const string Dep     = "14"; // Dependent listings
            public const string Inc     = "20"; // Corporate / partnership income
            public const string State   = "30"; // State apportionment
            public const string Partner = "40"; // Partnership partner rows
        }

        public static List<DetailRow> SmithDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Wages, "",  "1",  "", "", "", "W2",      "T", "", 75000.00, "ACME Industries W-2"),
            new DetailRow(SeriesIds.Int,   "",  "1",  "", "", "", "1099INT", "T", "",   320.50, "First Bank interest"),
        };

        public static List<DetailRow> JonesDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Wages, "", "1", "", "", "", "W2",      "J", "", 120000.00, "Joint household wages"),
            new DetailRow(SeriesIds.Div,   "", "1", "", "", "", "1099DIV", "J", "",   1850.75, "Brokerage dividends"),
            new DetailRow(SeriesIds.Dep,   "", "1", "", "", "", "",        "",  "",      0.00, "Dependent: Susan Jones, age 8"),
        };

        public static List<DetailRow> RejectedDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Wages, "", "1", "", "", "", "W2", "T", "", 45000.00, "Single wages — return rejected"),
        };

        public static List<DetailRow> BrownDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Wages, "", "1", "", "", "", "W2",      "T", "", 95000.00, "Bill primary wages"),
            new DetailRow(SeriesIds.Wages, "", "2", "", "", "", "W2",      "S", "", 82000.00, "Spouse wages"),
            new DetailRow(SeriesIds.Dep,   "", "1", "", "", "", "",        "",  "",     0.00, "Dependent: Tim Brown, age 12"),
        };

        public static List<DetailRow> PendDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Wages, "", "1", "", "", "", "W2",  "J", "", 210000.00, "Joint EF in-flight"),
            new DetailRow(SeriesIds.Int,   "", "1", "", "", "", "1099INT", "J", "", 1200.00, "EF in-flight interest"),
        };

        public static List<DetailRow> AcmeDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Inc,   "", "1", "", "",   "", "",  "", "", 5_200_000.00, "Gross receipts"),
            new DetailRow(SeriesIds.State, "", "1", "", "CA", "", "",  "", "", 3_100_000.00, "CA apportioned income"),
            new DetailRow(SeriesIds.State, "", "2", "", "NY", "", "",  "", "", 2_100_000.00, "NY apportioned income"),
        };

        public static List<DetailRow> WidgDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Inc,   "", "1", "", "",   "", "", "", "", 850_000.00, "Gross receipts"),
            new DetailRow(SeriesIds.State, "", "1", "", "TX", "", "", "", "", 850_000.00, "TX apportioned income"),
        };

        public static List<DetailRow> PartDetails() => new List<DetailRow>
        {
            new DetailRow(SeriesIds.Inc,     "", "1", "", "", "", "", "", "", 1_500_000.00, "Ordinary income"),
            new DetailRow(SeriesIds.Partner, "", "1", "", "", "", "", "", "",   750_000.00, "Alice share — 50%"),
            new DetailRow(SeriesIds.Partner, "", "2", "", "", "", "", "", "",   450_000.00, "Bob share — 30%"),
            new DetailRow(SeriesIds.Partner, "", "3", "", "", "", "", "", "",   300_000.00, "Carol share — 20%"),
        };
    }

    /// <summary>Hand-authored individual-module client spec for the seed.</summary>
    public sealed record IndividualClient(
        string ClientNumber,
        string ClientID,
        string FirstName,
        string LastName,
        string FilingStatus,
        int Dependents,
        string Email,
        string? EFStatus);

    /// <summary>Hand-authored corporate-module client spec for the seed.</summary>
    public sealed record CorpClient(
        string ClientNumber,
        string ClientID,
        string CorpName,
        string Email,
        string[] States);

    /// <summary>Hand-authored partnership-module client spec for the seed.</summary>
    public sealed record PartnershipClient(
        string ClientNumber,
        string ClientID,
        string PartnershipName,
        string Email,
        Partner[] Partners);

    /// <summary>One partner in a DATA2 subclient row for the partnership seed.</summary>
    public sealed record Partner(int SubId, string Name, double SharePct);
}
