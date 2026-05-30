using System;
using System.Collections.Generic;
using System.Linq;

namespace LacertMCP.Server.Pagination
{
    /// <summary>
    /// Generic pagination helper used by tools that page through large client
    /// rosters or table rows. Per Todo.md task #77 — added in Phase 11 so future
    /// paginated tools have a single source of truth for the row shape.
    ///
    /// <para>
    /// Page shape (mirrors REST list endpoints):
    /// <code>{ "rows": [...], "total": N, "limit": L, "offset": O,
    ///         "hasMore": bool, "nextOffset": int? }</code>
    /// </para>
    ///
    /// <para>
    /// Both <c>limit</c> and <c>offset</c> are nullable so tools can omit them
    /// and get unbounded reads (legacy contract). When <c>limit</c> is null OR
    /// non-positive the page is the full source.
    /// </para>
    /// </summary>
    public static class Pagination
    {
        /// <summary>Default max page size — used when caller passes nothing.</summary>
        public const int DefaultLimit = 100;

        /// <summary>Hard ceiling — anything larger gets clamped to this.</summary>
        public const int MaxLimit = 1000;

        /// <summary>
        /// Page a source enumerable. Materializes <paramref name="source"/> via
        /// <see cref="Enumerable.ToList{T}"/> so <c>total</c> is exact;
        /// pagination is in-memory (tools that need streaming should build a
        /// per-tool iterator).
        /// </summary>
        public static Page<T> Page<T>(IEnumerable<T> source, int? limit = null, int? offset = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // Materialize so total + slice are coherent.
            var all = source as IList<T> ?? source.ToList();
            var total = all.Count;

            var effectiveOffset = offset.GetValueOrDefault();
            if (effectiveOffset < 0) effectiveOffset = 0;
            if (effectiveOffset > total) effectiveOffset = total;

            int effectiveLimit;
            if (!limit.HasValue || limit.Value <= 0)
            {
                effectiveLimit = total - effectiveOffset;
            }
            else
            {
                effectiveLimit = Math.Min(limit.Value, MaxLimit);
            }

            var sliceCount = Math.Min(effectiveLimit, Math.Max(0, total - effectiveOffset));
            var rows = new List<T>(sliceCount);
            for (int i = 0; i < sliceCount; i++)
            {
                rows.Add(all[effectiveOffset + i]);
            }

            var hasMore = (effectiveOffset + sliceCount) < total;
            int? nextOffset = hasMore ? (int?)(effectiveOffset + sliceCount) : null;

            return new Page<T>(rows, total, effectiveLimit, effectiveOffset, hasMore, nextOffset);
        }
    }

    /// <summary>
    /// Result shape from <see cref="Pagination.Page{T}"/>. Tools serialize this
    /// directly to JSON so the wire shape matches across calls.
    /// </summary>
    public sealed class Page<T>
    {
        public IReadOnlyList<T> Rows { get; }
        public int Total { get; }
        public int Limit { get; }
        public int Offset { get; }
        public bool HasMore { get; }
        public int? NextOffset { get; }

        public Page(IReadOnlyList<T> rows, int total, int limit, int offset, bool hasMore, int? nextOffset)
        {
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
            Total = total;
            Limit = limit;
            Offset = offset;
            HasMore = hasMore;
            NextOffset = nextOffset;
        }
    }
}
