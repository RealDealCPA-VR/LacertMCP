using System.Linq;
using LacertMCP.Server.Pagination;
using Xunit;

namespace LacertMCP.Tests.Pagination
{
    /// <summary>
    /// Phase 11 task #77 — pagination helper tests. Covers limit/offset edge
    /// cases + nextOffset/hasMore coherence.
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
    }
}
