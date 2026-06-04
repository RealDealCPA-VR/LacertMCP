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
    ///         "returnedCount": R, "hasMore": bool, "nextOffset": int? }</code>
    /// </para>
    ///
    /// <para>
    /// <b>limit semantics (audit P16-037 + P16-075):</b>
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>null</c> — unbounded read; emits every row from <c>offset</c> to the end of <c>source</c>. Legacy contract; tools should normalize null to <see cref="DefaultLimit"/> at the wire layer before calling this helper if they want LLM-safe defaults. Direct callers (tests, internal probes) may still pass null.</description></item>
    ///   <item><description><c>0</c> — count-only probe. Returns <c>rows = []</c> with <c>total</c> populated; <c>hasMore</c> is true when the source has rows beyond <c>offset</c>, with <c>nextOffset = offset</c> so the caller can resume paging if desired. This is the cheap "how many rows would I get?" idiom — added in P16-037 to give LLMs a deliberate count-probe path instead of accidentally tripping the surprising "unbounded" branch.</description></item>
    ///   <item><description><c>positive int</c> — bounded page, clamped to <see cref="MaxLimit"/>.</description></item>
    ///   <item><description><c>negative int</c> — rejected with <see cref="ArgumentOutOfRangeException"/>. Eliminates the silent "negative = unbounded" footgun (audit P16-037).</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Limit field semantics (audit P16-074):</b> <see cref="Page{T}.Limit"/> reflects the
    /// REQUESTED page window (after <see cref="MaxLimit"/> clamping), NOT the actual number of
    /// rows returned. On the short last page this may exceed <see cref="Page{T}.Rows"/>.Count.
    /// Use <see cref="Page{T}.ReturnedCount"/> (or <c>Rows.Count</c>) for "rows on this page"
    /// and <see cref="Page{T}.HasMore"/>/<see cref="Page{T}.NextOffset"/> to decide whether to keep paging —
    /// NEVER use <c>Limit</c> for end-of-set detection.
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
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="limit"/> is negative (audit P16-037 — eliminates the silent "negative = unbounded" footgun).</exception>
        public static Page<T> Page<T>(IEnumerable<T> source, int? limit = null, int? offset = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // P16-037 — reject negative limit explicitly. Previously the
            // `limit <= 0` branch silently treated -1, -100, etc. as "unbounded"
            // AND bypassed the MaxLimit clamp — the worst-possible behavior for
            // an LLM that hallucinated a negative value (or for a defensive
            // probe like `limit: -1`). Throwing here surfaces the intent error
            // immediately instead of letting it materialize as a transport-size
            // failure further downstream.
            if (limit.HasValue && limit.Value < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(limit),
                    limit.Value,
                    "limit must be null (unbounded read), 0 (count-only probe), or a positive int (clamped to Pagination.MaxLimit).");
            }

            // Materialize so total + slice are coherent.
            var all = source as IList<T> ?? source.ToList();
            var total = all.Count;

            var effectiveOffset = offset.GetValueOrDefault();
            if (effectiveOffset < 0) effectiveOffset = 0;
            if (effectiveOffset > total) effectiveOffset = total;

            // P16-037 + P16-074 — tri-state limit contract with a stable
            // "Limit = requested window after clamping" semantic across all
            // branches. See class-level remarks for the full contract.
            int effectiveLimit;
            bool isCountProbe;
            if (limit.HasValue && limit.Value == 0)
            {
                // Explicit count-only probe — returns rows=[], total=N.
                effectiveLimit = 0;
                isCountProbe = true;
            }
            else if (!limit.HasValue)
            {
                // Unbounded (legacy contract). Limit field equals the requested
                // cap, which conceptually is "everything from here onward" —
                // surface that as `total - effectiveOffset` so the field's
                // numeric value matches what was actually computable as a cap.
                // Direct callers (tests / internal probes) rely on this;
                // tool-layer callers should normalize null -> DefaultLimit
                // before invoking Pagination.Page.
                effectiveLimit = total - effectiveOffset;
                isCountProbe = false;
            }
            else
            {
                // Bounded page — clamp to MaxLimit.
                effectiveLimit = Math.Min(limit.Value, MaxLimit);
                isCountProbe = false;
            }

            var sliceCount = isCountProbe
                ? 0
                : Math.Min(effectiveLimit, Math.Max(0, total - effectiveOffset));
            var rows = new List<T>(sliceCount);
            for (int i = 0; i < sliceCount; i++)
            {
                rows.Add(all[effectiveOffset + i]);
            }

            bool hasMore;
            int? nextOffset;
            if (isCountProbe)
            {
                // Count-only probe — hasMore signals "the source HAS rows past
                // offset" so the caller can decide to keep paging from this
                // point. nextOffset equals offset (the caller's slice is empty
                // but didn't advance). P16-037 refined recommendation.
                hasMore = total > effectiveOffset;
                nextOffset = hasMore ? (int?)effectiveOffset : null;
            }
            else
            {
                hasMore = (effectiveOffset + sliceCount) < total;
                nextOffset = hasMore ? (int?)(effectiveOffset + sliceCount) : null;
            }

            return new Page<T>(rows, total, effectiveLimit, effectiveOffset, hasMore, nextOffset);
        }
    }

    /// <summary>
    /// Result shape from <see cref="Pagination.Page{T}"/>. Tools serialize this
    /// directly to JSON so the wire shape matches across calls.
    ///
    /// <para>
    /// <b>Field semantics — IMPORTANT (audit P16-074):</b>
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="Rows"/> — the actual page slice. <c>Rows.Count</c> is the canonical "rows on this page" — equals <see cref="ReturnedCount"/>.</description></item>
    ///   <item><description><see cref="Total"/> — exact total across the full source.</description></item>
    ///   <item><description><see cref="Limit"/> — the REQUESTED page window (after <see cref="Pagination.MaxLimit"/> clamping). On the short last page this may exceed <see cref="Rows"/>.Count. <b>Do NOT use Limit for end-of-set detection</b> — use <see cref="HasMore"/>.</description></item>
    ///   <item><description><see cref="ReturnedCount"/> — convenience mirror of <c>Rows.Count</c>; added in P16-074 to make the rows-on-this-page field unambiguous in the serialized envelope.</description></item>
    ///   <item><description><see cref="Offset"/> — the offset used (after clamping into [0, Total]).</description></item>
    ///   <item><description><see cref="HasMore"/> / <see cref="NextOffset"/> — end-of-set signal. Drive paging loops from these, NEVER from <c>Limit</c>.</description></item>
    /// </list>
    /// </summary>
    public sealed class Page<T>
    {
        public IReadOnlyList<T> Rows { get; }
        public int Total { get; }

        /// <summary>
        /// The REQUESTED page window (after <see cref="Pagination.MaxLimit"/> clamping).
        /// On the short last page this may exceed <see cref="Rows"/>.Count —
        /// use <see cref="ReturnedCount"/> for actual rows returned and
        /// <see cref="HasMore"/> for end-of-set detection.
        /// </summary>
        public int Limit { get; }

        public int Offset { get; }
        public bool HasMore { get; }
        public int? NextOffset { get; }

        /// <summary>
        /// Convenience mirror of <c>Rows.Count</c>. Added in P16-074 so the
        /// serialized envelope carries an unambiguous "rows on this page"
        /// field that callers can read without conflating it with the
        /// requested <see cref="Limit"/> window.
        /// </summary>
        public int ReturnedCount => Rows.Count;

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
