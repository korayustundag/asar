using Asar.Exceptions;
using Asar.Models;

namespace Asar.IO;

// ══════════════════════════════════════════════════════════════════════════════
// AsarEntryStream
//
// A read-only, seekable Stream that exposes the content of a single file entry
// stored inside an ASAR archive.
//
// Key design decisions:
//   • The underlying archive stream is NOT owned by this stream and will NOT
//     be closed when this stream is disposed – the archive manages its own
//     lifetime.  A mutex (SemaphoreSlim) serialises concurrent reads so the
//     same archive stream can serve multiple AsarEntryStream instances.
//   • Seeking is O(1): we simply adjust an internal logical position offset
//     without touching the underlying stream until an actual Read is issued.
//   • Read operations seek the underlying stream to the correct absolute
//     position before copying bytes – this is the "random-access" guarantee.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A read-only, seekable <see cref="Stream"/> that provides random access to
/// the content of a single file stored inside an ASAR archive.
/// </summary>
/// <remarks>
/// <para>
/// This stream does <em>not</em> own the underlying archive stream.  Disposing
/// this instance only releases internal state; the archive stream remains open.
/// </para>
/// <para>
/// Thread-safety: multiple <see cref="AsarEntryStream"/> instances that share
/// the same underlying archive stream are protected by a shared
/// <see cref="SemaphoreSlim"/>.  Each read acquires the semaphore, seeks to
/// the correct position, copies bytes, and then releases the semaphore.
/// </para>
/// </remarks>
public sealed class AsarEntryStream : Stream
{
    // ── Archive state shared with the owning AsarArchive ──────────────────

    private readonly Stream         _archiveStream;
    private readonly SemaphoreSlim  _lock;          // shared across all entry-streams for this archive

    // ── Entry layout information ──────────────────────────────────────────

    /// <summary>
    /// Absolute byte offset inside <see cref="_archiveStream"/> at which
    /// this entry's content begins.
    /// </summary>
    private readonly long _absoluteStart;

    /// <summary>Total byte count of this entry's content.</summary>
    private readonly long _length;

    // ── Logical read position (relative to entry start) ───────────────────

    private long _position;

    // ── Disposal guard ────────────────────────────────────────────────────

    private bool _disposed;

    // ── Public metadata ───────────────────────────────────────────────────

    /// <summary>The file entry that this stream reads from.</summary>
    public AsarFileEntry Entry { get; }

    // ══════════════════════════════════════════════════════════════════════
    // Construction
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new <see cref="AsarEntryStream"/> for the specified
    /// <paramref name="fileEntry"/>.
    /// </summary>
    /// <param name="fileEntry">The file entry to expose as a stream.</param>
    /// <param name="archiveStream">The underlying archive file stream.</param>
    /// <param name="contentBase">
    /// The absolute byte offset in <paramref name="archiveStream"/> at which
    /// file content begins (= <see cref="Models.AsarHeader.ContentBaseOffset"/>).
    /// </param>
    /// <param name="sharedLock">
    /// A semaphore shared by all entry streams for this archive to prevent
    /// concurrent seeks/reads on the same underlying stream.
    /// </param>
    internal AsarEntryStream(
        AsarFileEntry  fileEntry,
        Stream         archiveStream,
        long           contentBase,
        SemaphoreSlim  sharedLock)
    {
        if (fileEntry.IsUnpacked)
        {
            throw new AsarUnpackedException(fileEntry.VirtualPath);
        }

        Entry           = fileEntry;
        _archiveStream  = archiveStream;
        _lock           = sharedLock;
        _absoluteStart  = contentBase + fileEntry.Offset;
        _length         = fileEntry.Size;
        _position       = 0;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Stream capability flags
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public override bool CanRead  => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek  => !_disposed;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    // ══════════════════════════════════════════════════════════════════════
    // Length / Position
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public override long Length
    {
        get { ThrowIfDisposed(); return _length; }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set
        {
            ThrowIfDisposed();
            if (value < 0 || value > _length)
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"Position {value} is out of range [0, {_length}].");
            _position = value;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Seek
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    /// <remarks>
    /// Seeking is O(1): only the internal logical position is updated.
    /// The underlying stream is not touched until a <see cref="Read"/> call.
    /// </remarks>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();

        // Translate the seek origin into an absolute logical position.
        long newPosition = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End     => _length + offset,
            _                  => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (newPosition < 0 || newPosition > _length)
        {
            throw new IOException(
                $"Seek would place the position ({newPosition}) outside " +
                $"the entry boundary [0, {_length}].");
        }

        _position = newPosition;
        return _position;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Read (synchronous)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    /// <remarks>
    /// Acquires the shared semaphore, seeks the underlying stream to the
    /// correct absolute position, reads bytes into <paramref name="buffer"/>,
    /// and releases the semaphore.  This ensures correct behaviour even when
    /// multiple <see cref="AsarEntryStream"/> instances share the same
    /// archive file stream.
    /// </remarks>
    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();

        // How many bytes are still available from the current position?
        long remaining = _length - _position;
        if (remaining <= 0) return 0;

        // Clamp the request to what is available.
        int toRead = (int)Math.Min(buffer.Length, remaining);

        // ── Acquire the shared lock ───────────────────────────────────────
        //
        // Using a synchronous Wait here is intentional: the async variant
        // is ReadAsync which will call WaitAsync instead.

        _lock.Wait();
        try
        {
            // Seek the underlying stream to the absolute position of the
            // bytes we want to read.  This is the "random access" guarantee.
            long absolutePosition = _absoluteStart + _position;
            _archiveStream.Seek(absolutePosition, SeekOrigin.Begin);

            // Read the bytes into the caller's buffer.
            int bytesRead = 0;
            while (bytesRead < toRead)
            {
                int n = _archiveStream.Read(buffer[bytesRead..toRead]);
                if (n == 0) break; // underlying stream ended prematurely
                bytesRead += n;
            }

            // Advance the logical position.
            _position += bytesRead;
            return bytesRead;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // ReadAsync (asynchronous)
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(
        byte[]            buffer,
        int               offset,
        int               count,
        CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                 .ConfigureAwait(false);

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte>      buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        long remaining = _length - _position;
        if (remaining <= 0) return 0;

        int toRead = (int)Math.Min(buffer.Length, remaining);

        // ── Acquire the shared async lock ─────────────────────────────────

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long absolutePosition = _absoluteStart + _position;
            _archiveStream.Seek(absolutePosition, SeekOrigin.Begin);

            int bytesRead = 0;
            while (bytesRead < toRead)
            {
                int n = await _archiveStream
                    .ReadAsync(buffer[bytesRead..toRead], cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0) break;
                bytesRead += n;
            }

            _position += bytesRead;
            return bytesRead;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Convenience helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads all remaining bytes of this entry into a new byte array.
    /// The logical position is advanced to the end of the entry.
    /// </summary>
    public byte[] ReadToEnd()
    {
        ThrowIfDisposed();

        long remaining = _length - _position;
        if (remaining <= 0) return Array.Empty<byte>();

        byte[] result = new byte[remaining];
        int    total  = 0;

        while (total < result.Length)
        {
            int n = Read(result, total, result.Length - total);
            if (n == 0) break;
            total += n;
        }

        return result[..total];
    }

    /// <summary>
    /// Asynchronously reads all remaining bytes of this entry.
    /// </summary>
    public async Task<byte[]> ReadToEndAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        long remaining = _length - _position;
        if (remaining <= 0) return Array.Empty<byte>();

        byte[] result = new byte[remaining];
        int    total  = 0;

        while (total < result.Length)
        {
            int n = await ReadAsync(result, total, result.Length - total, cancellationToken)
                          .ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }

        return result[..total];
    }

    // ══════════════════════════════════════════════════════════════════════
    // Unsupported write operations
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("AsarEntryStream is read-only.");

    /// <inheritdoc/>
    public override void SetLength(long value)
        => throw new NotSupportedException("AsarEntryStream is read-only.");

    /// <inheritdoc/>
    public override void Flush() { /* nothing to flush on a read-only stream */ }

    // ══════════════════════════════════════════════════════════════════════
    // Disposal
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Do NOT close or dispose the underlying archive stream.
        // The archive manages its own lifetime.
        _disposed = true;
        base.Dispose(disposing);
    }

    // ── Guard ─────────────────────────────────────────────────────────────

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(AsarEntryStream));
    }
}