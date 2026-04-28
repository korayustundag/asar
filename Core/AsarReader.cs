using Asar.Constants;
using Asar.Exceptions;
using Asar.IO;
using Asar.Models;
using Asar.Serialization;

namespace Asar.Core;

// ══════════════════════════════════════════════════════════════════════════════
// AsarReader
//
// Provides all read operations for an open ASAR archive.
//
// Architecture
// ─────────────
// • One FileStream for the archive is held open for the lifetime of this
//   reader.  A SemaphoreSlim serialises concurrent read calls on that stream.
// • AsarEntryStream instances borrow the underlying FileStream (and the
//   semaphore) so they can perform random-access reads without reopening the
//   file on every call.
// • The parsed AsarHeader gives O(1) path lookups: no scanning required.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Reads files and metadata from an open ASAR archive with random-access
/// support.
/// </summary>
/// <remarks>
/// <para>
/// All path arguments use forward slashes as separators and are treated as
/// relative to the archive root (a leading slash is accepted and ignored).
/// </para>
/// <para>
/// This class is thread-safe: multiple threads may call any read method
/// concurrently.  A shared <see cref="SemaphoreSlim"/> serialises the
/// physical I/O on the underlying <see cref="FileStream"/>.
/// </para>
/// </remarks>
public sealed class AsarReader : IDisposable, IAsyncDisposable
{
    // ── Core state ────────────────────────────────────────────────────────

    private readonly Stream         _archiveStream;
    private readonly SemaphoreSlim  _streamLock = new(1, 1); // one-at-a-time I/O
    private readonly AsarHeader     _header;
    private readonly string         _archivePath;
    private          bool           _disposed;

    // ── Public surface ────────────────────────────────────────────────────

    /// <summary>The parsed header containing the full virtual-filesystem tree.</summary>
    public AsarHeader Header => _header;

    /// <summary>Path of the underlying archive file on disk.</summary>
    public string ArchivePath => _archivePath;

    // ══════════════════════════════════════════════════════════════════════
    // Construction / Open
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens an ASAR archive at <paramref name="archivePath"/> and parses
    /// its header.  The underlying file remains open until the reader is
    /// disposed.
    /// </summary>
    /// <param name="archivePath">Full path to the <c>.asar</c> file.</param>
    /// <exception cref="AsarHeaderException">
    /// Thrown if the file header is invalid or corrupted.
    /// </exception>
    public AsarReader(string archivePath)
    {
        _archivePath   = archivePath ?? throw new ArgumentNullException(nameof(archivePath));

        // Open with FileShare.Read so other processes can still read the archive.
        _archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: AsarConstants.DefaultBufferSize,
            useAsync: true);

        // Parse the binary header into the domain model.
        // This reads only the header section; file content is accessed lazily.
        _header = AsarHeaderSerializer.Deserialize(_archiveStream);
    }

    /// <summary>
    /// Creates an <see cref="AsarReader"/> from any readable and seekable
    /// <see cref="Stream"/>.  The stream must be positioned at offset 0.
    /// </summary>
    /// <param name="stream">
    /// A readable, seekable stream positioned at the start of the archive.
    /// <para><b>Ownership:</b> the caller retains ownership; the reader will
    /// NOT dispose the stream.</para>
    /// </param>
    public AsarReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "The stream must be readable and seekable.", nameof(stream));
        }

        _archivePath   = "<stream>";
        _archiveStream = stream;
        _header        = AsarHeaderSerializer.Deserialize(stream);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Entry lookup
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the <see cref="AsarEntry"/> at the given virtual path,
    /// or <see langword="null"/> if it does not exist.
    /// </summary>
    public AsarEntry? GetEntry(string virtualPath)
    {
        ThrowIfDisposed();
        return _header.Resolve(virtualPath);
    }

    /// <summary>
    /// Returns the <see cref="AsarFileEntry"/> at the given virtual path.
    /// </summary>
    /// <exception cref="AsarEntryNotFoundException"/>
    /// <exception cref="AsarEntryTypeMismatchException"/>
    public AsarFileEntry GetFileEntry(string virtualPath)
    {
        ThrowIfDisposed();
        return _header.ResolveFile(virtualPath);
    }

    /// <summary>
    /// Returns the <see cref="AsarDirectoryEntry"/> at the given virtual path.
    /// </summary>
    /// <exception cref="AsarEntryNotFoundException"/>
    /// <exception cref="AsarEntryTypeMismatchException"/>
    public AsarDirectoryEntry GetDirectoryEntry(string virtualPath)
    {
        ThrowIfDisposed();
        return _header.ResolveDirectory(virtualPath);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the given virtual path exists in
    /// the archive, regardless of whether it is a file or a directory.
    /// </summary>
    public bool Exists(string virtualPath)
    {
        ThrowIfDisposed();
        return _header.Resolve(virtualPath) is not null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given path resolves to a
    /// <see cref="AsarFileEntry"/>.
    /// </summary>
    public bool FileExists(string virtualPath)
    {
        ThrowIfDisposed();
        return _header.Resolve(virtualPath) is AsarFileEntry;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the given path resolves to a
    /// <see cref="AsarDirectoryEntry"/>.
    /// </summary>
    public bool DirectoryExists(string virtualPath)
    {
        ThrowIfDisposed();
        return _header.Resolve(virtualPath) is AsarDirectoryEntry;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Random-access stream API  ← THE KEY FEATURE
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens a seekable, read-only <see cref="Stream"/> over the content of
    /// the file at <paramref name="virtualPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned stream supports random-access reads: the caller may
    /// <see cref="Stream.Seek"/> to any position and begin reading from
    /// that offset without scanning the file from the beginning.  Each read
    /// internally translates the logical position into an absolute stream
    /// offset and performs a single seeked read – exactly like memory-mapped
    /// file access.
    /// </para>
    /// <para>
    /// The caller is responsible for disposing the returned stream.  The
    /// underlying archive stream is NOT closed when the entry stream is
    /// disposed.
    /// </para>
    /// </remarks>
    /// <param name="virtualPath">Virtual path inside the archive.</param>
    /// <returns>A seekable, read-only stream for the file's content.</returns>
    /// <exception cref="AsarEntryNotFoundException"/>
    /// <exception cref="AsarEntryTypeMismatchException"/>
    /// <exception cref="AsarUnpackedException">
    /// Thrown when the entry is flagged "unpacked".  Use
    /// <see cref="OpenUnpackedFile"/> instead for those entries.
    /// </exception>
    public Stream OpenEntryStream(string virtualPath)
    {
        ThrowIfDisposed();

        AsarFileEntry entry = _header.ResolveFile(virtualPath);

        // AsarEntryStream performs the random-access translation internally.
        return new AsarEntryStream(
            entry,
            _archiveStream,
            _header.ContentBaseOffset,
            _streamLock);
    }

    /// <summary>
    /// Opens the companion on-disk file for an entry that is flagged
    /// "unpacked".
    /// </summary>
    /// <param name="virtualPath">Virtual path inside the archive.</param>
    /// <returns>
    /// A <see cref="FileStream"/> pointing to the unpacked file on disk.
    /// </returns>
    /// <exception cref="AsarEntryNotFoundException"/>
    /// <exception cref="AsarEntryTypeMismatchException"/>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the entry is not unpacked.
    /// </exception>
    public FileStream OpenUnpackedFile(string virtualPath)
    {
        ThrowIfDisposed();

        AsarFileEntry entry = _header.ResolveFile(virtualPath);

        if (!entry.IsUnpacked)
        {
            throw new InvalidOperationException(
                $"Entry '{virtualPath}' is packed inside the archive. " +
                "Use OpenEntryStream() instead.");
        }

        // Build the path inside the companion .asar.unpacked directory.
        string unpackedDir  = _archivePath + AsarConstants.UnpackedDirectorySuffix;
        string unpackedPath = Path.Combine(unpackedDir, virtualPath.Replace('/', Path.DirectorySeparatorChar));

        return new FileStream(unpackedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Convenience read methods
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the entire content of the file at <paramref name="virtualPath"/>
    /// into a byte array.  This is a one-shot convenience method; prefer
    /// <see cref="OpenEntryStream"/> for large files.
    /// </summary>
    public byte[] ReadAllBytes(string virtualPath)
    {
        ThrowIfDisposed();

        using Stream stream = OpenEntryStream(virtualPath);

        // For known-size streams we can pre-allocate the buffer.
        if (stream.Length > 0)
        {
            byte[] buffer = new byte[stream.Length];
            int    total  = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer, total, buffer.Length - total);
                if (n == 0) break;
                total += n;
            }
            return buffer[..total];
        }

        return Array.Empty<byte>();
    }

    /// <summary>
    /// Asynchronously reads the entire content of the file at
    /// <paramref name="virtualPath"/> into a byte array.
    /// </summary>
    public async Task<byte[]> ReadAllBytesAsync(
        string            virtualPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Open an entry stream and drain it.
        await using Stream stream = OpenEntryStream(virtualPath);
        using var ms = new MemoryStream((int)Math.Min(stream.Length, int.MaxValue));
        await stream.CopyToAsync(ms, AsarConstants.DefaultBufferSize, cancellationToken)
                    .ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// Reads the content of the file at <paramref name="virtualPath"/> as a
    /// UTF-8 string.
    /// </summary>
    public string ReadAllText(string virtualPath)
        => System.Text.Encoding.UTF8.GetString(ReadAllBytes(virtualPath));

    /// <summary>
    /// Asynchronously reads the content of the file at
    /// <paramref name="virtualPath"/> as a UTF-8 string.
    /// </summary>
    public async Task<string> ReadAllTextAsync(
        string            virtualPath,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadAllBytesAsync(virtualPath, cancellationToken)
                              .ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads a slice of the file at <paramref name="virtualPath"/> starting
    /// at <paramref name="offset"/> for <paramref name="count"/> bytes.
    /// This is the most direct expression of the random-access capability.
    /// </summary>
    /// <param name="virtualPath">Virtual path inside the archive.</param>
    /// <param name="offset">Byte offset within the entry's content.</param>
    /// <param name="count">Number of bytes to read.</param>
    /// <returns>A byte array of length ≤ <paramref name="count"/>.</returns>
    public byte[] ReadSlice(string virtualPath, long offset, int count)
    {
        ThrowIfDisposed();

        using Stream stream = OpenEntryStream(virtualPath);

        // Seek to the desired offset within the entry.
        stream.Seek(offset, SeekOrigin.Begin);

        // Clamp count to the remaining bytes.
        int available = (int)Math.Min(count, stream.Length - offset);
        if (available <= 0) return Array.Empty<byte>();

        byte[] buffer = new byte[available];
        int    total  = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return buffer[..total];
    }

    /// <summary>
    /// Asynchronously reads a slice of the file at
    /// <paramref name="virtualPath"/>.
    /// </summary>
    public async Task<byte[]> ReadSliceAsync(
        string            virtualPath,
        long              offset,
        int               count,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using Stream stream = OpenEntryStream(virtualPath);
        stream.Seek(offset, SeekOrigin.Begin);

        int available = (int)Math.Min(count, stream.Length - offset);
        if (available <= 0) return Array.Empty<byte>();

        byte[] buffer = new byte[available];
        int    total  = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken)
                                .ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return buffer[..total];
    }

    // ══════════════════════════════════════════════════════════════════════
    // Directory listing
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the direct children (files and sub-directories) of the
    /// directory at <paramref name="virtualPath"/>.
    /// </summary>
    public IReadOnlyCollection<AsarEntry> ListDirectory(string virtualPath = "")
    {
        ThrowIfDisposed();

        AsarDirectoryEntry dir = _header.ResolveDirectory(virtualPath);
        return dir.Children.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Returns a flat list of every file in the archive (depth-first).
    /// </summary>
    public IEnumerable<AsarFileEntry> GetAllFiles() => _header.GetAllFiles();

    /// <summary>
    /// Returns a flat list of every entry (files and directories) in the
    /// archive (depth-first).
    /// </summary>
    public IEnumerable<AsarEntry> GetAllEntries() => _header.GetAllEntries();

    // ══════════════════════════════════════════════════════════════════════
    // Disposal
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archiveStream.Dispose();
        _streamLock.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _archiveStream.DisposeAsync().ConfigureAwait(false);
        _streamLock.Dispose();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, nameof(AsarReader));
}