using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Models;

namespace OpenApiContractValidation.Middleware;

/// <summary>
/// A replacement <see cref="IHttpResponseBodyFeature"/> that holds back (buffers) the
/// entire HTTP response body in memory, so the OpenAPI contract validation middleware can
/// inspect it <em>before</em> a single byte reaches the client. While buffering, start and
/// complete operations are suppressed so <c>HttpContext.Response.HasStarted</c> stays
/// <see langword="false"/> and the response can still be rewritten or replaced. Once the
/// middleware has validated the body, <see cref="CommitAsync"/> replays the buffer to the
/// wrapped inner feature.
/// </summary>
public sealed class HoldBackResponseBodyFeature : IHttpResponseBodyFeature, IAsyncDisposable
{
    private readonly IHttpResponseBodyFeature _inner;
    private readonly long _maxBufferSizeBytes;
    private readonly bool _throwOnCapExceeded;
    private readonly MemoryStream _buffer;
    private CapStream? _capStream;
    private PipeWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="HoldBackResponseBodyFeature"/> class.
    /// </summary>
    /// <param name="inner">The original response body feature provided by the host.</param>
    /// <param name="maxBufferSizeBytes">
    /// The maximum number of bytes that will be buffered. A write that would exceed this limit is a
    /// contract failure: see <paramref name="throwOnCapExceeded"/>.
    /// </param>
    /// <param name="throwOnCapExceeded">
    /// When <see langword="true"/> (throw policy), exceeding the cap throws
    /// <see cref="OpenApiContractValidationException"/> mid-write so the oversized, unvalidated response
    /// is suppressed. When <see langword="false"/> (log policy), the feature switches to pass-through
    /// (flushing what was buffered and streaming the rest) and sets <see cref="CapExceeded"/> so the
    /// middleware can log and skip validation without failing the request.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxBufferSizeBytes"/> is zero or negative.
    /// </exception>
    public HoldBackResponseBodyFeature(
        IHttpResponseBodyFeature inner,
        long maxBufferSizeBytes,
        bool throwOnCapExceeded = true
    )
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferSizeBytes);

        _inner = inner;
        _maxBufferSizeBytes = maxBufferSizeBytes;
        _throwOnCapExceeded = throwOnCapExceeded;
        _buffer = new MemoryStream();
    }

    /// <summary>
    /// <see langword="true"/> once <see cref="DisableBuffering"/> has been called and the
    /// feature switched to pass-through mode.
    /// </summary>
    public bool BufferingDisabled { get; private set; }

    /// <summary>
    /// <see langword="true"/> once the response exceeded <c>maxBufferSizeBytes</c> under the log policy
    /// and the feature switched to pass-through. The middleware uses this to log and skip validation.
    /// </summary>
    public bool CapExceeded { get; private set; }

    /// <summary>
    /// <see langword="true"/> once the buffered bytes have been replayed to the inner
    /// feature through <see cref="CommitAsync"/>.
    /// </summary>
    public bool Committed { get; private set; }

    /// <summary>The number of bytes currently held in the in-memory buffer.</summary>
    public long BufferedLength => _buffer.Length;

    /// <summary>
    /// The stream application code writes to. While buffering, writes land in the in-memory
    /// buffer (subject to the configured size cap) and never reach the inner stream; after
    /// <see cref="DisableBuffering"/> writes pass straight through to the inner feature.
    /// </summary>
    public Stream Stream => BufferingDisabled ? _inner.Stream : CapStreamInstance;

    /// <summary>
    /// The <see cref="PipeWriter"/> application code writes to. It is backed by the same
    /// buffer as <see cref="Stream"/> so that JSON and asynchronous streaming responses are
    /// captured identically and subject to the same size cap. After
    /// <see cref="DisableBuffering"/> this returns the inner writer.
    /// </summary>
    public PipeWriter Writer
    {
        get
        {
            if (BufferingDisabled)
            {
                return _inner.Writer;
            }

            _writer ??= PipeWriter.Create(
                CapStreamInstance,
                new StreamPipeWriterOptions(leaveOpen: true)
            );
            return _writer;
        }
    }

    private CapStream CapStreamInstance => _capStream ??= new CapStream(this);

    /// <summary>
    /// While buffering this is a no-op, so that
    /// <c>HttpContext.Response.HasStarted</c> stays <see langword="false"/> and the
    /// middleware can still rewrite the response. After <see cref="DisableBuffering"/> it
    /// delegates to the inner feature.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation.</param>
    /// <returns>A completed task while buffering, otherwise the inner feature's task.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        BufferingDisabled ? _inner.StartAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// While buffering this is a no-op for the same reason as
    /// <see cref="StartAsync"/>. After <see cref="DisableBuffering"/> it delegates to the
    /// inner feature.
    /// </summary>
    /// <returns>A completed task while buffering, otherwise the inner feature's task.</returns>
    public Task CompleteAsync() => BufferingDisabled ? _inner.CompleteAsync() : Task.CompletedTask;

    /// <summary>
    /// Reads the requested portion of <paramref name="path"/> into the buffer, instead of
    /// letting the host stream the file directly to the client (which would bypass
    /// validation). After <see cref="DisableBuffering"/> this delegates to the inner
    /// feature.
    /// </summary>
    /// <param name="path">The absolute path of the file to send.</param>
    /// <param name="offset">The byte offset within the file at which to start reading.</param>
    /// <param name="count">
    /// The number of bytes to copy, or <see langword="null"/> to copy to the end of the file.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation.</param>
    /// <returns>A task that represents the asynchronous copy operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public async Task SendFileAsync(
        string path,
        long offset,
        long? count,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(path);

        if (BufferingDisabled)
        {
            await _inner
                .SendFileAsync(path, offset, count, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        const int BufferSize = 81920;
        byte[] chunk = new byte[BufferSize];

        using var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            useAsync: true
        );

        if (offset > 0)
        {
            fileStream.Seek(offset, SeekOrigin.Begin);
        }

        long remaining = count ?? -1;
        while (remaining != 0)
        {
            // remaining is never 0 here (loop guard), so toRead is always >= 1.
            int toRead = remaining < 0 ? BufferSize : (int)Math.Min(remaining, BufferSize);

            int read = await fileStream
                .ReadAsync(chunk.AsMemory(0, toRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            EnforceCapAndWrite(chunk.AsSpan(0, read));

            if (remaining > 0)
            {
                remaining -= read;
            }
        }
    }

    /// <summary>
    /// Switches the feature into pass-through mode: subsequent writes, and the start,
    /// complete and send-file operations are forwarded directly to the inner feature and
    /// nothing is buffered any more. Any bytes already buffered are copied to the inner
    /// stream first so that pass-through mode never loses data.
    /// </summary>
    public void DisableBuffering()
    {
        if (BufferingDisabled)
        {
            return;
        }

        BufferingDisabled = true;

        if (_buffer.Length > 0)
        {
            _buffer.Position = 0;
            _buffer.CopyTo(_inner.Stream);
            _inner.Stream.Flush();
        }

        _inner.DisableBuffering();
    }

    /// <summary>
    /// Returns the bytes captured so far, for the middleware to validate. Any bytes still
    /// pending inside the <see cref="Writer"/> are flushed into the buffer first.
    /// </summary>
    /// <returns>A copy of the buffered response body.</returns>
    public byte[] GetBufferedBytes() => GetBufferedSpan().ToArray();

    /// <summary>
    /// Returns the bytes captured so far as a span over the internal buffer, without copying,
    /// for the middleware's validation path. Any bytes still pending inside the
    /// <see cref="Writer"/> are flushed into the buffer first.
    /// </summary>
    internal ReadOnlySpan<byte> GetBufferedSpan()
    {
        if (_writer is not null)
        {
            // The underlying cap stream is in-memory, so this flush completes synchronously
            // and drains every PipeWriter-buffered byte into the backing buffer.
            _writer.FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        return _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
    }

    /// <summary>
    /// Replays the buffered bytes to the inner response stream and completes the inner
    /// feature. Call this once validation has passed.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation.</param>
    /// <returns>A task that represents the asynchronous replay operation.</returns>
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (Committed)
        {
            return;
        }

        if (BufferingDisabled)
        {
            await _inner.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _inner.CompleteAsync().ConfigureAwait(false);
            Committed = true;
            return;
        }

        // Drain any bytes still held inside the PipeWriter into the backing buffer before
        // copying to the inner stream.
        if (_writer is not null)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _buffer.Position = 0;
        await _buffer.CopyToAsync(_inner.Stream, cancellationToken).ConfigureAwait(false);
        await _inner.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _inner.CompleteAsync().ConfigureAwait(false);

        Committed = true;
    }

    /// <summary>
    /// Completes the underlying <see cref="PipeWriter"/> and disposes the buffer.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_writer is not null)
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
        }

        await _buffer.DisposeAsync().ConfigureAwait(false);
    }

    private void EnforceCapAndWrite(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return;
        }

        // Once the cap has been exceeded under the log policy we are in pass-through: write straight
        // to the inner stream so the rest of the (unvalidated) response still reaches the client.
        if (CapExceeded)
        {
            _inner.Stream.Write(source);
            return;
        }

        if (_buffer.Length + source.Length > _maxBufferSizeBytes)
        {
            if (_throwOnCapExceeded)
            {
                throw CapExceededException();
            }

            // Log policy: stop buffering, flush what we have, and stream this and subsequent writes
            // straight through. DisableBuffering() performs the flush + pass-through switch.
            CapExceeded = true;
            DisableBuffering();
            _inner.Stream.Write(source);
            return;
        }

        _buffer.Write(source);
    }

    private OpenApiContractValidationException CapExceededException()
    {
        var limit = _maxBufferSizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        return new(
            ContractPhase.Response,
            new[]
            {
                new ContractViolation(
                    Location: "responseBody",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: limit,
                    Actual: null,
                    Message: $"the response exceeded the validation buffer capacity (limit {limit}) and cannot be validated."
                ),
            }
        );
    }

    /// <summary>
    /// A <see cref="Stream"/> that funnels every write into the in-memory buffer while
    /// enforcing the configured size cap, so that both <see cref="Stream"/> and
    /// <see cref="Writer"/> writes are bounded identically.
    /// </summary>
    private sealed class CapStream : Stream
    {
        private readonly HoldBackResponseBodyFeature _owner;

        internal CapStream(HoldBackResponseBodyFeature owner)
        {
            _owner = owner;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => true;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => _owner._buffer.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _owner._buffer.Position;
            set => _owner._buffer.Position = value;
        }

        /// <inheritdoc />
        public override void Flush()
        {
            // No-op: writes are already reflected in the backing buffer.
        }

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            _owner._buffer.Read(buffer, offset, count);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) =>
            _owner._buffer.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => _owner._buffer.SetLength(value);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            _owner.EnforceCapAndWrite(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer) => _owner.EnforceCapAndWrite(buffer);

        /// <inheritdoc />
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            _owner.EnforceCapAndWrite(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
