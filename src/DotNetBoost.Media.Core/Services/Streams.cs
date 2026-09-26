using DotNetBoost.Media.Core.Interfaces;
using System.Security.Cryptography;

namespace DotNetBoost.Media.Core.Services;

/// <summary>
/// A read-only view over an upload that counts the bytes, hashes them and enforces the size limit
/// <b>as the backend reads them</b>.
/// <para>
/// This is what keeps an upload single-pass. The obvious alternative — buffer the file, measure
/// it, then hand it to the backend — turns a 2 GB upload into 2 GB of memory or a temporary file;
/// hashing it separately reads it twice. Here the backend's own read is the only read, and the
/// size, the hash and the limit all fall out of it.
/// </para>
/// <para>
/// The limit is enforced at the boundary: the read that would take the total past it throws, so
/// nothing beyond the limit is ever passed on — though bytes already handed to the backend have
/// of course been written, which is why the engine removes them again when an upload fails.
/// </para>
/// </summary>
internal sealed class MeasuringStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly IncrementalHash? _hash;
    private string? _checksum;

    public MeasuringStream(Stream inner, long limit, bool computeChecksum)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _limit = limit;
        _hash  = computeChecksum ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
    }

    /// <summary>How many bytes have been read so far.</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// The hash of everything read, lowercase hex, or <c>null</c> when hashing was off. Computed
    /// once, on first access — after that the instance can be disposed.
    /// </summary>
    public string? Checksum
    {
        get
        {
            if (_hash is null)
                return null;

            return _checksum ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Account(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private void Account(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
            return;

        BytesRead += chunk.Length;

        if (BytesRead > _limit)
            throw new MediaTooLargeException(BytesRead, _limit);

        _hash?.AppendData(chunk);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The inner stream belongs to the caller — it is usually the request body, which the
        // server is still responsible for. Only the hash is ours to release.
        if (disposing)
            _hash?.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>
/// A stream that serves a buffered prefix and then the rest of another stream, as one.
/// <para>
/// Sniffing an upload's real format means looking at its first few bytes, and a request body
/// cannot be rewound. So the engine reads that prefix into a small array, decides what the file
/// is, and then hands the backend one of these — which replays the prefix and continues straight
/// into the untouched remainder. The backend sees an ordinary stream and the file is still read
/// exactly once.
/// </para>
/// </summary>
internal sealed class PrefixStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _rest;
    private int _prefixPosition;

    public PrefixStream(ReadOnlyMemory<byte> prefix, Stream rest)
    {
        _prefix = prefix;
        _rest   = rest ?? throw new ArgumentNullException(nameof(rest));
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var fromPrefix = CopyPrefix(buffer);
        // Prefix bytes only. Returning a short read rather than topping it up from the inner
        // stream is allowed, and it keeps this class from having to reason about two sources in
        // one call — the caller's next read continues into the remainder.
        return fromPrefix > 0 ? fromPrefix : _rest.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var fromPrefix = CopyPrefix(buffer.Span);
        return fromPrefix > 0 ? fromPrefix : await _rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private int CopyPrefix(Span<byte> buffer)
    {
        var remaining = _prefix.Length - _prefixPosition;
        if (remaining <= 0 || buffer.IsEmpty)
            return 0;

        var take = Math.Min(remaining, buffer.Length);
        _prefix.Span.Slice(_prefixPosition, take).CopyTo(buffer);
        _prefixPosition += take;
        return take;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>SHA-256, lowercase hex. The hash every checksum in this library is.</summary>
internal sealed class Sha256ChecksumCalculator : IChecksumCalculator, IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

    public string Finish() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public void Dispose() => _hash.Dispose();
}
