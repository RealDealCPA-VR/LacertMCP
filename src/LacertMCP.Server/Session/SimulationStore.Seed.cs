using System.Collections.Generic;
using LacertMCP.Server.Session.Fixtures;

namespace LacertMCP.Server.Session
{
    // Partial of SimulationStore that owns the seed wiring. Kept separate so
    // SimulationStore.cs stays readable and the fixture data lives in
    // Session/Fixtures/SeedData.cs where it can grow without polluting the
    // store implementation.
    public sealed partial class SimulationStore
    {
        /// <summary>
        /// Populates the in-memory store with the 8-client fixture from
        /// <c>docs/research/agents/06-simulation-mode.md</c> §2 — SMIT, JONE,
        /// REJE on TY20 IData; BROW, PEND on TY24 IData; ACME, WIDG on TY24
        /// CData; PART on TY24 PData. Zero file I/O.
        /// </summary>
        private void Seed()
        {
            SeedIndividuals2020();
            SeedIndividuals2024();
            SeedCorporates2024();
            SeedPartnership2024();
        }

        // ----- TY20 individual -----------------------------------------
        private void SeedIndividuals2020()
        {
            var store = GetOrAddModule(SeedData.DataPathIData2020, 2020, 'I');
            store.NextClientNumber = 1004; // next after the seeded 1001-1003

            SeedIndividualClient(store, SeedData.SMIT, SeedData.SmithDetails());
            SeedIndividualClient(store, SeedData.JONE, SeedData.JonesDetails());
            SeedIndividualClient(store, SeedData.REJE, SeedData.RejectedDetails());
        }

        // ----- TY24 individual -----------------------------------------
        private void SeedIndividuals2024()
        {
            var store = GetOrAddModule(SeedData.DataPathIData2024, 2024, 'I');
            store.NextClientNumber = 1003; // next after the seeded 1001-1002

            SeedIndividualClient(store, SeedData.BROW, SeedData.BrownDetails());
            SeedIndividualClient(store, SeedData.PEND, SeedData.PendDetails());
        }

        // ----- TY24 corporate ------------------------------------------
        private void SeedCorporates2024()
        {
            var store = GetOrAddModule(SeedData.DataPathCData2024, 2024, 'C');
            store.NextClientNumber = 1003; // next after the seeded 1001-1002

            SeedCorpClient(store, SeedData.ACME, SeedData.AcmeDetails());
            SeedCorpClient(store, SeedData.WIDG, SeedData.WidgDetails());
        }

        // ----- TY24 partnership ----------------------------------------
        private void SeedPartnership2024()
        {
            var store = GetOrAddModule(SeedData.DataPathPData2024, 2024, 'P');
            store.NextClientNumber = 1002; // next after the seeded 1001

            SeedPartnershipClient(store, SeedData.PART, SeedData.PartDetails());
        }

        // ---------------------------------------------------------------
        // Per-shape helpers — populate DATA1, DATA5, DATA7, EF tables, and
        // the detail file in the shape the live ODBC driver would surface.
        // Field positions follow the spec's C{table}_{n} convention.
        // ---------------------------------------------------------------

        private void SeedIndividualClient(ModuleStore store, IndividualClient c, List<DetailRow> details)
        {
            // DATA1 — Client Info.
            var data1 = new DbfRow(c.ClientNumber)
            {
                Fields =
                {
                    ["C1_0"] = c.ClientNumber,
                    ["C1_1"] = c.FirstName,
                    ["C1_2"] = c.LastName,
                    ["C1_3"] = c.Email,
                    ["C1_4"] = c.FilingStatus,
                    ["C1_5"] = c.Dependents,
                    ["ClientID"] = c.ClientID,
                },
            };
            store.Clients[c.ClientNumber] = data1;

            // DATA5 — F4 Status (1 row per client).
            store.Statuses[c.ClientNumber] = new DbfRow(c.ClientNumber)
            {
                Fields =
                {
                    ["C5_0"] = c.ClientNumber,
                    ["C5_1"] = c.EFStatus == "Rejected" ? "Rejected" : "InProgress",
                },
            };

            // DATA7 — Forms (one stub form row per client).
            store.Forms[c.ClientNumber] = new List<DbfRow>
            {
                new DbfRow(c.ClientNumber)
                {
                    Fields =
                    {
                        ["C7_0"] = c.ClientNumber,
                        ["C7_1"] = "1040",
                    },
                },
            };

            store.Details[c.ClientNumber] = details;

            // EF tables — TY19+ only; both seeded years (2020, 2024) qualify.
            //
            // P15-056 — three fixture shapes are required so the LEFT-OUTER-JOIN
            // parity contract (sim ↔ live ReadEFStatus) can be enforced:
            //   (a) EFStatus == "<non-null status string>" → EFiling + EFStatus rows
            //   (b) EFStatus == "<EFiling-no-status>" sentinel → EFiling only,
            //       no EFStatus row (mimics the live edge case where a return
            //       was transmitted but the status hasn't ticked back yet)
            //   (c) EFStatus == null → neither EFiling nor EFStatus row (client
            //       hasn't been transmitted; the sim's old INNER JOIN dropped
            //       these silently)
            if (c.EFStatus != null)
            {
                var efilingId = store.NextEFilingId++;
                store.EFilings.Add(new EFiling(
                    Id: efilingId,
                    ClientId: c.ClientNumber,
                    EFId: $"EF-{c.ClientID}-{efilingId:D4}",
                    InstanceId: $"INST-{efilingId:D6}"));
                if (c.EFStatus != "<EFiling-no-status>")
                {
                    store.EFStatuses.Add(new EFStatus(
                        EFilingId: efilingId,
                        Status: c.EFStatus,
                        StatusDate: "2024-04-15"));
                }
            }
        }

        private void SeedCorpClient(ModuleStore store, CorpClient c, List<DetailRow> details)
        {
            store.Clients[c.ClientNumber] = new DbfRow(c.ClientNumber)
            {
                Fields =
                {
                    ["C1_0"] = c.ClientNumber,
                    ["C1_1"] = c.CorpName,
                    ["C1_3"] = c.Email,
                    ["ClientID"] = c.ClientID,
                },
            };

            store.Statuses[c.ClientNumber] = new DbfRow(c.ClientNumber)
            {
                Fields = { ["C5_0"] = c.ClientNumber, ["C5_1"] = "InProgress" },
            };

            store.Forms[c.ClientNumber] = new List<DbfRow>
            {
                new DbfRow(c.ClientNumber)
                {
                    Fields = { ["C7_0"] = c.ClientNumber, ["C7_1"] = "1120" },
                },
            };

            // DATA3 — one State Info row per state the corp files in.
            foreach (var state in c.States)
            {
                store.States[(c.ClientNumber, state)] = new DbfRow(
                    ClientNumber: c.ClientNumber, StateAbbrev: state)
                {
                    Fields =
                    {
                        ["C3_0"] = c.ClientNumber,
                        ["C3_1"] = state,
                    },
                };
            }

            store.Details[c.ClientNumber] = details;
        }

        private void SeedPartnershipClient(ModuleStore store, PartnershipClient c, List<DetailRow> details)
        {
            store.Clients[c.ClientNumber] = new DbfRow(c.ClientNumber)
            {
                Fields =
                {
                    ["C1_0"] = c.ClientNumber,
                    ["C1_1"] = c.PartnershipName,
                    ["C1_3"] = c.Email,
                    ["ClientID"] = c.ClientID,
                },
            };

            store.Statuses[c.ClientNumber] = new DbfRow(c.ClientNumber)
            {
                Fields = { ["C5_0"] = c.ClientNumber, ["C5_1"] = "InProgress" },
            };

            store.Forms[c.ClientNumber] = new List<DbfRow>
            {
                new DbfRow(c.ClientNumber)
                {
                    Fields = { ["C7_0"] = c.ClientNumber, ["C7_1"] = "1065" },
                },
            };

            // DATA2 — one SubClient (partner) row per partner.
            foreach (var p in c.Partners)
            {
                store.SubClients[(c.ClientNumber, p.SubId)] = new DbfRow(
                    ClientNumber: c.ClientNumber, SubClientID: p.SubId)
                {
                    Fields =
                    {
                        ["C2_0"] = c.ClientNumber,
                        ["C2_1"] = p.SubId,
                        ["C2_2"] = p.Name,
                        ["C2_3"] = p.SharePct,
                    },
                };
            }

            store.Details[c.ClientNumber] = details;
        }
    }
}
