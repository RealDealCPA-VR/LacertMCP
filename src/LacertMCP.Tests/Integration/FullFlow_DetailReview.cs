using System.Linq;
using System.Threading.Tasks;
using LacertMCP.Server.Session;
using LacertMCP.Server.Session.Fixtures;
using LacertMCP.Tests.Parity;
using Xunit;

namespace LacertMCP.Tests.Integration
{
    /// <summary>
    /// P15-039 — integration coverage for REQUIREMENTS §1 named operator
    /// flow #5: "pull tax-detail context into review chats". Calls
    /// <see cref="LacerteSessionManager.ReadDetail"/>, asserts the row
    /// envelope is coherent (Series/Code/Amount/Description populated),
    /// and drives the client-side filter the
    /// <c>lacerte_review_detail</c> prompt scripts (find W-2 rows by
    /// Series=11). Locks down P14-055 (Series numeric-string invariant)
    /// at the cross-tool layer.
    /// </summary>
    public class FullFlow_DetailReview
    {
        [Theory, BothModesData]
        public async Task DetailReview_ReadDetail_FilterBySeries_FindsW2Rows(SessionMode mode)
        {
            using var mgr = ParitySessionFactory.For(mode);
            await mgr.Register();
            mgr.SetContext(SeedData.DataPathIData2024, 2024, 'I');

            // BROW (ClientNumber 1001) in TY24 IData seeds 3 detail rows:
            // 2 Wages (Series=11) + 1 Dependent (Series=14).
            var rows = await mgr.ReadDetail("1001");
            Assert.NotNull(rows);
            Assert.True(rows.Count >= 3,
                $"TY24 IData BROW seed must include >= 3 detail rows; got {rows.Count}");

            // Envelope coherence — every row carries the documented columns.
            foreach (var r in rows)
            {
                Assert.False(string.IsNullOrEmpty(r.Series), "DetailRow.Series must be non-empty");
                Assert.False(string.IsNullOrEmpty(r.Code),   "DetailRow.Code must be non-empty");
            }

            // P14-055 numeric-string invariant: every Series value MUST parse
            // to an int so SQL templates that emit `where Series = 11` match
            // in-memory rows correctly when the live ODBC path lights up.
            foreach (var r in rows)
            {
                Assert.True(int.TryParse(r.Series, out _),
                    $"DetailRow.Series must parse to int (P14-055); got '{r.Series}'");
                Assert.True(int.TryParse(r.Code, out _),
                    $"DetailRow.Code must parse to int (P14-055); got '{r.Code}'");
            }

            // Client-side filter: find W-2 rows by Series. The prompt
            // documents this pattern verbatim — the LLM is supposed to be
            // able to discriminate W-2 rows from dependent / interest rows.
            var w2Rows = rows.Where(r => r.Series == SeedData.SeriesIds.Wages).ToList();
            Assert.True(w2Rows.Count >= 2,
                $"BROW seed has 2 Wages rows; got {w2Rows.Count}");

            // Each W-2 row's payload is coherent — Amount and Description
            // both populated. The prompt summarizes these to the operator.
            foreach (var w2 in w2Rows)
            {
                Assert.True(w2.Amount.HasValue, "W-2 row Amount must be populated");
                Assert.True(w2.Amount!.Value > 0, $"W-2 Amount must be positive; got {w2.Amount}");
                Assert.False(string.IsNullOrEmpty(w2.Description),
                    "W-2 row Description must be populated for the review summary");
            }

            // Sanity: dependent row (Series=14) is present and distinct
            // from the W-2 bucket — proves the filter discriminates.
            var depRows = rows.Where(r => r.Series == SeedData.SeriesIds.Dep).ToList();
            Assert.True(depRows.Count >= 1,
                $"BROW seed has a Dependent row; got {depRows.Count}");
            Assert.DoesNotContain(depRows, d => w2Rows.Contains(d));
        }
    }
}
