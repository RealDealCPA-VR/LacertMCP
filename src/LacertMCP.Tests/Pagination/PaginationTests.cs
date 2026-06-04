using System;
using System.Linq;
using LacertMCP.Server.Pagination;
using Xunit;

namespace LacertMCP.Tests.Pagination
{
    /// <summary>
    /// Phase 11 task #77 — pagination helper tests. Covers limit/offset edge
    /// cases + nextOffset/hasMore coherence.
    ///
    /// P16-037 / P16-074 additions lock the tri-state limit contract
    /// (null=unbounded / 0=count-probe / positive=clamped / negative=throw)
    /// and the "Limit field = requested window after clamping" semantic.
    /// </summary>
    public class PaginationTests
    {
        private static int[] OneToTen() => Enumerable.Range(1, 10).ToArray();

        [Fact]
        public void Page_NoLimitNoOffset_ReturnsEverything()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen());
            Assert.Equal(10, page.Total);
            Assert.Equal(10, page.Rows.Count);
            Assert.Equal(0, page.Offset);
            Assert.False(page.HasMore);
            Assert.Null(page.NextOffset);
        }

        [Fact]
        public void Page_LimitSmallerThanTotal_HasMoreTrue()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 3);
            Assert.Equal(10, page.Total);
            Assert.Equal(3, page.Rows.Count);
            Assert.True(page.HasMore);
            Assert.Equal(3, page.NextOffset);
            Assert.Equal(new[] { 1, 2, 3 }, page.Rows);
        }

        [Fact]
        public void Page_LimitAndOffset_SliceCorrectly()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 4, offset: 3);
            Assert.Equal(4, page.Rows.Count);
            Assert.Equal(new[] { 4, 5, 6, 7 }, page.Rows);
            Assert.True(page.HasMore);
            Assert.Equal(7, page.NextOffset);
        }

        [Fact]
        public void Page_OffsetBeyondEnd_EmptyResult()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 4, offset: 99);
            Assert.Empty(page.Rows);
            Assert.False(page.HasMore);
            Assert.Null(page.NextOffset);
        }

        [Fact]
        public void Page_NegativeOffset_TreatedAsZero()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 2, offset: -7);
            Assert.Equal(new[] { 1, 2 }, page.Rows);
            Assert.Equal(0, page.Offset);
        }

        [Fact]
        public void Page_LimitLargerThanMax_Clamped()
        {
            var src = Enumerable.Range(1, Server.Pagination.Pagination.MaxLimit + 500).ToArray();
            var page = Server.Pagination.Pagination.Page(src, limit: 10_000);
            Assert.Equal(Server.Pagination.Pagination.MaxLimit, page.Rows.Count);
            Assert.True(page.HasMore);
        }

        [Fact]
        public void Page_LastPage_HasMoreFalse()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 4, offset: 8);
            Assert.Equal(new[] { 9, 10 }, page.Rows);
            Assert.False(page.HasMore);
            Assert.Null(page.NextOffset);
        }

        // ----- P16-037 — limit=0 / negative limit contract -----

        /// <summary>
        /// P16-037 — `limit: 0` is the count-only probe idiom. Returns rows=[],
        /// total preserved, hasMore=true when source has rows past offset,
        /// nextOffset=offset so the caller can resume paging from this point.
        /// </summary>
        [Fact]
        public void Page_LimitZero_ReturnsCountProbe()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 0);
            Assert.Empty(page.Rows);
            Assert.Equal(0, page.ReturnedCount);
            Assert.Equal(10, page.Total);
            Assert.Equal(0, page.Limit);
            Assert.Equal(0, page.Offset);
            Assert.True(page.HasMore);
            Assert.Equal(0, page.NextOffset);
        }

        /// <summary>
        /// P16-037 — count-only probe preserves offset so a caller probing
        /// "anything past row 5?" sees hasMore=true + nextOffset=5.
        /// </summary>
        [Fact]
        public void Page_LimitZeroWithOffset_PreservesOffset()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 0, offset: 5);
            Assert.Empty(page.Rows);
            Assert.Equal(10, page.Total);
            Assert.Equal(5, page.Offset);
            Assert.True(page.HasMore);
            Assert.Equal(5, page.NextOffset);
        }

        /// <summary>
        /// P16-037 — count-only probe on an exhausted offset reports hasMore=false.
        /// </summary>
        [Fact]
        public void Page_LimitZeroAtEnd_HasMoreFalse()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 0, offset: 10);
            Assert.Empty(page.Rows);
            Assert.Equal(10, page.Total);
            Assert.Equal(10, page.Offset);
            Assert.False(page.HasMore);
            Assert.Null(page.NextOffset);
        }

        /// <summary>
        /// P16-037 — negative limit is rejected explicitly. Previously the
        /// `limit &lt;= 0` branch silently treated -1 as "unbounded" AND
        /// bypassed the MaxLimit clamp — the worst-possible footgun.
        /// </summary>
        [Fact]
        public void Page_NegativeLimit_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Server.Pagination.Pagination.Page(OneToTen(), limit: -1));
        }

        [Fact]
        public void Page_LargeNegativeLimit_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Server.Pagination.Pagination.Page(OneToTen(), limit: -10_000));
        }

        // ----- P16-074 — Limit field = REQUESTED window (not actual rows) -----

        /// <summary>
        /// P16-074 — on a short last page, page.Limit reflects the REQUESTED
        /// window (4), not the actual returned row count (2). End-of-set
        /// detection must use HasMore/NextOffset, never Limit.
        /// </summary>
        [Fact]
        public void Page_LastPage_LimitFieldEqualsRequestedNotActualRows()
        {
            var page = Server.Pagination.Pagination.Page(OneToTen(), limit: 4, offset: 8);
            Assert.Equal(2, page.Rows.Count);           // actual rows on this page
            Assert.Equal(2, page.ReturnedCount);        // mirror of Rows.Count
            Assert.Equal(4, page.Limit);                // REQUESTED window
            Assert.False(page.HasMore);                 // canonical end-of-set signal
            Assert.Null(page.NextOffset);
        }

        /// <summary>
        /// P16-074 — when clamped to MaxLimit, page.Limit reflects the CLAMPED
        /// request (MaxLimit), not the actual returned row count.
        /// </summary>
        [Fact]
        public void Page_LimitLargerThanMax_LimitFieldEqualsMaxLimit()
        {
            var src = Enumerable.Range(1, 50).ToArray(); // smaller than MaxLimit
            var page = Server.Pagination.Pagination.Page(src, limit: 10_000);
            Assert.Equal(50, page.Rows.Count);
            Assert.Equal(50, page.ReturnedCount);
            Assert.Equal(Server.Pagination.Pagination.MaxLimit, page.Limit); // requested cap (clamped)
            Assert.False(page.HasMore);
        }

        /// <summary>
        /// P16-074 — ReturnedCount property is the unambiguous "rows on this
        /// page" field; equals Rows.Count across all branches.
        /// </summary>
        [Fact]
        public void Page_ReturnedCount_AlwaysEqualsRowsCount()
        {
            var p1 = Server.Pagination.Pagination.Page(OneToTen());
            Assert.Equal(p1.Rows.Count, p1.ReturnedCount);

            var p2 = Server.Pagination.Pagination.Page(OneToTen(), limit: 3);
            Assert.Equal(p2.Rows.Count, p2.ReturnedCount);

            var p3 = Server.Pagination.Pagination.Page(OneToTen(), limit: 0);
            Assert.Equal(p3.Rows.Count, p3.ReturnedCount);

            var p4 = Server.Pagination.Pagination.Page(OneToTen(), limit: 4, offset: 8);
            Assert.Equal(p4.Rows.Count, p4.ReturnedCount);
        }
    }
}
