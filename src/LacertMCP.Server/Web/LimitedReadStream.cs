using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LacertMCP.Server.Web
{
    /// <summary>A request body grew past its cap (answered with 413).</summary>
    public sealed class RequestTooLargeException : IOException
    {
        public RequestTooLargeException(long limit)
            : base("The request body is larger than " + limit + " bytes.")
        {
        }
    }

    /// <summary>
    /// Read-only wrapper that counts the bytes read from a request body and
    /// throws <see cref="RequestTooLargeException"/> once more than the cap has
    /// arrived. Checking Content-Length alone is not enough: a chunked request
    /// has none (-1), and the JSON readers would otherwise buffer it without limit.
    /// </summary>
    internal sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public LimitedReadStream(Stream inner, long limit)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _limit = limit;
        }

        public long BytesRead => _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Count(_inner.Read(buffer, offset, Clamp(count)));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Count(await _inner.ReadAsync(buffer, offset, Clamp(count), cancellationToken).ConfigureAwait(false));

        // Never ask for more than one byte past the cap, so an oversized body is
        // detected without pulling the rest of it into memory.
        private int Clamp(int count)
        {
            var room = _limit - _read + 1;
            return room < count ? (int)Math.Max(1, room) : count;
        }

        private int Count(int n)
        {
            _read += n;
            if (_read > _limit) throw new RequestTooLargeException(_limit);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // Dispose leaves the inner stream alone: it belongs to HttpListener, which
        // closes it with the response.
    }
}
