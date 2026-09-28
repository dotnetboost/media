using Amazon.S3;
using Amazon.S3.Model;
using DotNetBoost.Media.Core;
using System.Net;

namespace DotNetBoost.Media.AmazonS3;

/// <summary>
/// A seekable, read-only stream over one S3 object.
/// <para>
/// S3's own response stream is forward-only, and that is not merely inconvenient: ASP.NET Core
/// serves a <c>Range</c> request only when the stream it is given can report a length, so a
/// forward-only stream silently turns every range request into a <c>200</c> carrying the whole
/// object. A client seeking through a video would pull the entire file through the application
/// on every seek, and the endpoint's advertised <c>Range</c> support would be a fiction on this
/// backend alone.
/// </para>
/// <para>
/// So this fills the gap the way the Azure SDK's own read stream does: the object's length is
/// known up front, seeking is free because it only moves a counter, and the bytes for wherever
/// the reader lands are fetched with a ranged <c>GET</c> when they are actually asked for.
/// </para>
/// <para>
/// The common cases cost nothing extra. A straight download reads the response that was already
/// opened, start to finish, in one request. A <c>bytes=0-</c> range — what a media element asks
/// for first — lands on that same response. Only a seek somewhere else issues a second request,
/// and a short forward seek is served by discarding bytes rather than paying for one.
/// </para>
/// </summary>
internal sealed class S3ReadStream : Stream
{
    /// <summary>
    /// How far forward a seek may go before a new ranged request beats reading and discarding.
    /// A round trip to S3 costs far more than 64 KiB off a connection that is already open, and
    /// far less than skipping a megabyte down one.
    /// </summary>
    private const int ForwardSkipLimit = 64 * 1024;

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _key;
    private readonly string _providerName;
    private readonly string _path;

    /// <summary>The open response, or <c>null</c> when none is in flight.</summary>
    private Stream? _body;

    /// <summary>The object offset <see cref="_body"/> will next yield.</summary>
    private long _bodyPosition;

    private long _position;
    private bool _disposed;

    /// <param name="s3">The client the object is read through.</param>
    /// <param name="bucket">The bucket holding it.</param>
    /// <param name="key">Its full key, prefix included.</param>
    /// <param name="providerName">The backend's name, for the exceptions raised here.</param>
    /// <param name="path">The storage path, for the exceptions raised here.</param>
    /// <param name="length">The object's length, from the response that opened it.</param>
    /// <param name="body">That response's stream, positioned at the start of the object.</param>
    public S3ReadStream(
        IAmazonS3 s3,
        string bucket,
        string key,
        string providerName,
        string path,
        long length,
        Stream body)
    {
        _s3           = s3 ?? throw new ArgumentNullException(nameof(s3));
        _bucket       = bucket;
        _key          = key;
        _providerName = providerName;
        _path         = path;
        _body         = body ?? throw new ArgumentNullException(nameof(body));

        Length = length;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;

    /// <summary>The object's length, as the opening response reported it.</summary>
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => Length + offset,
            _                  => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0)
            throw new IOException("Cannot seek before the start of the object.");

        // Only the counter moves. Seeking past the end is legal on a stream and simply reads as
        // end-of-stream, and seeking to a position that is never read must not cost a request —
        // which is exactly what ASP.NET Core does when it measures the object before serving one.
        _position = target;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        // The fetch is async underneath because the SDK offers nothing else. ASP.NET Core does not
        // call this — synchronous IO is disabled by default, and every copy it performs is
        // ReadAsync — so this path exists for a caller that reaches for Stream.CopyTo, and blocking
        // is the honest cost of asking a network stream a synchronous question.
        var body = EnsureBodyAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (body is null)
            return 0;

        var read = body.Read(buffer);
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var body = await EnsureBodyAsync(cancellationToken).ConfigureAwait(false);

        if (body is null)
            return 0;

        var read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Advance(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// The response to read the byte at <see cref="_position"/> from, or <c>null</c> at the end of
    /// the object. Reuses the one already open where it can, skips forward down it where that is
    /// cheaper than a round trip, and otherwise opens a ranged one.
    /// </summary>
    private async ValueTask<Stream?> EnsureBodyAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_position >= Length)
            return null;

        if (_body is { } open)
        {
            if (_bodyPosition == _position)
                return open;

            if (_position > _bodyPosition && _position - _bodyPosition <= ForwardSkipLimit)
            {
                await SkipAsync(open, _position - _bodyPosition, ct).ConfigureAwait(false);
                return open;
            }

            // Backwards, or far enough forward that a new request is cheaper. Disposing is what
            // tells the SDK to abandon the rest of the transfer rather than drain it.
            await open.DisposeAsync().ConfigureAwait(false);
            _body = null;
        }

        return _body = await OpenRangeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens the object from <see cref="_position"/> to its end.</summary>
    private async Task<Stream> OpenRangeAsync(CancellationToken ct)
    {
        try
        {
            var response = await _s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key        = _key,
                    // Inclusive at both ends, which is what the HTTP header means too.
                    ByteRange  = new ByteRange(_position, Length - 1)
                },
                ct).ConfigureAwait(false);

            _bodyPosition = _position;
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound
                                           || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            // Deleted while it was being read. The row still points at it, which is precisely what
            // MediaContentMissingException reports.
            throw new MediaContentMissingException(_providerName, _path);
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException(
                $"Could not read object '{_key}' from bucket '{_bucket}' at offset {_position}: {ex.Message}",
                ex,
                _providerName);
        }
    }

    /// <summary>Reads <paramref name="count"/> bytes off an open response and throws them away.</summary>
    private async ValueTask SkipAsync(Stream body, long count, CancellationToken ct)
    {
        var scratch = new byte[(int)Math.Min(count, ForwardSkipLimit)];

        while (count > 0)
        {
            var wanted = (int)Math.Min(count, scratch.Length);
            var read   = await body.ReadAsync(scratch.AsMemory(0, wanted), ct).ConfigureAwait(false);

            if (read == 0)
            {
                // The object is shorter than its reported length — it was replaced underneath us.
                // Leave the counter where the bytes actually ran out so the caller reads an end.
                _position = _bodyPosition;
                return;
            }

            _bodyPosition += read;
            count         -= read;
        }
    }

    private void Advance(int read)
    {
        _position     += read;
        _bodyPosition += read;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _body?.Dispose();
            _body = null;
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed && _body is { } body)
        {
            await body.DisposeAsync().ConfigureAwait(false);
            _body = null;
        }

        _disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
