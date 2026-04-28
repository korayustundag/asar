using Asar.Constants;
using Asar.Exceptions;
using Asar.Models;
using Asar.Serialization;

namespace Asar.Core;

// ══════════════════════════════════════════════════════════════════════════════
// AsarArchive – the single-entry-point façade
//
// AsarArchive combines reading, in-place modification, and re-packing into one
// coherent API.  It is the class most callers will use.
//
// Supported workflows
// ────────────────────
//   (A) Read-only:
//       using var archive = AsarArchive.Open("app.asar");
//       byte[] bytes = archive.ReadAllBytes("main.js");
//       Stream s     = archive.OpenEntryStream("assets/logo.png");
//
//   (B) Modify and save:
//       using var archive = AsarArchive.Open("app.asar");
//       archive.AddOrReplaceFile("main.js", newContent);
//       archive.RemoveFile("unused.js");
//       archive.Save("app-patched.asar");
//
//   (C) Create from scratch:
//       using var archive = AsarArchive.Create();
//       archive.AddFile("index.js", "console.log('hello');");
//       archive.Save("new.asar");
//
// Implementation notes
// ─────────────────────
// Modifications are staged in a "modification layer" that sits on top of the
// original archive content:
//   • Removed entries are tracked in a HashSet<string>.
//   • Added/replaced entries are tracked in a Dictionary<string, Stream>.
// On Save the merger combines the original and modified views to produce the
// final archive in a single pass.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// High-level façade for reading, modifying, and writing ASAR archives.
/// </summary>
/// <remarks>
/// <para>
/// Dispose this instance when finished to release the underlying file handle.
/// </para>
/// </remarks>
public sealed class AsarArchive : IDisposable, IAsyncDisposable
{
    // ── Underlying reader (null when creating from scratch) ────────────────

    private AsarReader?  _reader;

    // ── Modification layer ────────────────────────────────────────────────

    // Paths that have been removed from the original archive.
    private readonly HashSet<string>            _removedPaths  = new(StringComparer.Ordinal);

    // Paths that have been added or replaced, together with their content.
    private readonly Dictionary<string, ModifiedEntry> _modifiedEntries
        = new(StringComparer.Ordinal);

    // ── State ─────────────────────────────────────────────────────────────

    private          bool   _isReadOnly;
    private          bool   _disposed;

    // ══════════════════════════════════════════════════════════════════════
    // Factory methods
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens an existing ASAR archive for reading and optional modification.
    /// </summary>
    /// <param name="archivePath">Full path to the <c>.asar</c> file.</param>
    /// <param name="readOnly">
    /// When <see langword="true"/> modification methods throw
    /// <see cref="AsarReadOnlyException"/>.
    /// </param>
    public static AsarArchive Open(string archivePath, bool readOnly = false)
    {
        var archive = new AsarArchive
        {
            _reader     = new AsarReader(archivePath),
            _isReadOnly = readOnly
        };
        return archive;
    }

    /// <summary>
    /// Opens an ASAR archive from a stream (must be positioned at offset 0).
    /// </summary>
    /// <param name="stream">
    /// Readable and seekable stream.  Ownership is NOT transferred – the
    /// caller must dispose it after disposing the archive.
    /// </param>
    public static AsarArchive Open(Stream stream)
    {
        return new AsarArchive
        {
            _reader = new AsarReader(stream)
        };
    }

    /// <summary>
    /// Creates a new, empty archive.  Use <see cref="AddFile(string,byte[],bool)"/>
    /// to stage files, then <see cref="Save(string)"/> to write to disk.
    /// </summary>
    public static AsarArchive Create() => new();

    // Private constructor – use factory methods.
    private AsarArchive() { }

    // ══════════════════════════════════════════════════════════════════════
    // Header / metadata access
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The parsed ASAR header, or <see langword="null"/> for archives
    /// created via <see cref="Create()"/> that have not yet been saved.
    /// </summary>
    public AsarHeader? Header => _reader?.Header;

    /// <summary>
    /// Returns <see langword="true"/> when the archive was opened from a
    /// file (as opposed to created from scratch).
    /// </summary>
    public bool IsOpen => _reader is not null;

    // ══════════════════════════════════════════════════════════════════════
    // Read API (delegates to AsarReader)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens a seekable, read-only <see cref="Stream"/> for the file at
    /// <paramref name="virtualPath"/>.  Supports random-access seeks.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the archive was created via <see cref="Create()"/> and
    /// has no underlying reader.
    /// </exception>
    public Stream OpenEntryStream(string virtualPath)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.OpenEntryStream(virtualPath);
    }

    /// <summary>
    /// Reads the complete content of a file as a byte array.
    /// </summary>
    public byte[] ReadAllBytes(string virtualPath)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ReadAllBytes(virtualPath);
    }

    /// <summary>
    /// Asynchronously reads the complete content of a file as a byte array.
    /// </summary>
    public Task<byte[]> ReadAllBytesAsync(
        string            virtualPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ReadAllBytesAsync(virtualPath, cancellationToken);
    }

    /// <summary>
    /// Reads the complete content of a file as a UTF-8 string.
    /// </summary>
    public string ReadAllText(string virtualPath)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ReadAllText(virtualPath);
    }

    /// <summary>
    /// Asynchronously reads the complete content of a file as a UTF-8 string.
    /// </summary>
    public Task<string> ReadAllTextAsync(
        string            virtualPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ReadAllTextAsync(virtualPath, cancellationToken);
    }

    /// <summary>
    /// Reads a byte slice of the file at <paramref name="virtualPath"/>
    /// starting at <paramref name="offset"/> for <paramref name="count"/>
    /// bytes.  This is the purest expression of random-access.
    /// </summary>
    public byte[] ReadSlice(string virtualPath, long offset, int count)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ReadSlice(virtualPath, offset, count);
    }

    /// <summary>
    /// Asynchronously reads a byte slice.
    /// </summary>
    public Task<byte[]> ReadSliceAsync(
        string            virtualPath,
        long              offset,
        int               count,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ReadSliceAsync(virtualPath, offset, count, cancellationToken);
    }

    // ── Entry queries ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the entry at <paramref name="virtualPath"/>, or
    /// <see langword="null"/> if it does not exist.
    /// </summary>
    public AsarEntry? GetEntry(string virtualPath) => _reader?.GetEntry(virtualPath);

    /// <summary>Returns <see langword="true"/> when the path exists.</summary>
    public bool Exists(string virtualPath) => _reader?.Exists(virtualPath) ?? false;

    /// <summary>Returns <see langword="true"/> when the path resolves to a file.</summary>
    public bool FileExists(string virtualPath) => _reader?.FileExists(virtualPath) ?? false;

    /// <summary>Returns <see langword="true"/> when the path resolves to a directory.</summary>
    public bool DirectoryExists(string virtualPath) => _reader?.DirectoryExists(virtualPath) ?? false;

    /// <summary>Lists the direct children of the directory at <paramref name="virtualPath"/>.</summary>
    public IReadOnlyCollection<AsarEntry> ListDirectory(string virtualPath = "")
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.ListDirectory(virtualPath);
    }

    /// <summary>Returns a depth-first enumeration of every file entry.</summary>
    public IEnumerable<AsarFileEntry> GetAllFiles()
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.GetAllFiles();
    }

    /// <summary>Returns a depth-first enumeration of every entry.</summary>
    public IEnumerable<AsarEntry> GetAllEntries()
    {
        ThrowIfDisposed();
        EnsureReader();
        return _reader!.GetAllEntries();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Modification API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stages a new file (or replaces an existing one) with byte-array
    /// content.
    /// </summary>
    /// <param name="virtualPath">Target virtual path inside the archive.</param>
    /// <param name="content">File content.</param>
    /// <param name="executable">Sets the executable bit in the header.</param>
    public void AddFile(string virtualPath, byte[] content, bool executable = false)
    {
        ThrowIfReadOnly();
        virtualPath = NormalisePath(virtualPath);

        // Discard a previous modification for the same path.
        if (_modifiedEntries.TryGetValue(virtualPath, out ModifiedEntry? old))
        {
            old.Content.Dispose();
            _modifiedEntries.Remove(virtualPath);
        }

        _removedPaths.Remove(virtualPath);
        _modifiedEntries[virtualPath] = new ModifiedEntry(
            new MemoryStream(content, writable: false), executable, content.LongLength);
    }

    /// <summary>
    /// Stages a new file with string content encoded as UTF-8.
    /// </summary>
    public void AddFile(string virtualPath, string text, bool executable = false)
        => AddFile(virtualPath, System.Text.Encoding.UTF8.GetBytes(text), executable);

    /// <summary>
    /// Stages a new file from a stream.  The stream is read on
    /// <see cref="Save(string)"/>.
    /// </summary>
    /// <param name="virtualPath">Target virtual path.</param>
    /// <param name="content">
    /// A readable stream.  Must be seekable (or will be buffered internally).
    /// Ownership is transferred to this archive instance.
    /// </param>
    /// <param name="executable">Sets the executable bit.</param>
    public void AddFile(string virtualPath, Stream content, bool executable = false)
    {
        ThrowIfReadOnly();
        ArgumentNullException.ThrowIfNull(content);

        virtualPath = NormalisePath(virtualPath);

        // Buffer non-seekable streams so we can determine the size.
        Stream finalStream;
        long   size;

        if (content.CanSeek)
        {
            finalStream = content;
            size        = content.Length - content.Position;
        }
        else
        {
            var ms = new MemoryStream();
            content.CopyTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            finalStream = ms;
            size        = ms.Length;
        }

        if (_modifiedEntries.TryGetValue(virtualPath, out ModifiedEntry? old))
            old.Content.Dispose();

        _removedPaths.Remove(virtualPath);
        _modifiedEntries[virtualPath] = new ModifiedEntry(finalStream, executable, size);
    }

    /// <summary>
    /// Marks a file or directory for removal.  Removed entries are excluded
    /// from the output archive on the next <see cref="Save(string)"/> call.
    /// </summary>
    public void RemoveFile(string virtualPath)
    {
        ThrowIfReadOnly();
        virtualPath = NormalisePath(virtualPath);

        // If it was staged in this session, discard its content stream.
        if (_modifiedEntries.TryGetValue(virtualPath, out ModifiedEntry? mod))
        {
            mod.Content.Dispose();
            _modifiedEntries.Remove(virtualPath);
        }

        _removedPaths.Add(virtualPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Save (build the final archive)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds and writes the final ASAR archive to <paramref name="outputPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original archive (if any) is read sequentially in header order.
    /// Removed files are skipped.  Modified or newly added files replace or
    /// augment the original content.
    /// </para>
    /// <para>
    /// It is safe to set <paramref name="outputPath"/> equal to the path of
    /// the archive that was opened – the library writes to a temp file first
    /// and atomically renames it on success.
    /// </para>
    /// </remarks>
    public void Save(string outputPath)
    {
        ThrowIfDisposed();

        // Use a temporary path to avoid corruption when overwriting the source.
        string tempPath = outputPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using var writer = BuildWriter();
            writer.Save(tempPath);

            // Atomic replace: rename the temp file over the target.
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            // Clean up the temp file on failure.
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes the archive to the provided <paramref name="destination"/> stream.
    /// </summary>
    public void Save(Stream destination)
    {
        ThrowIfDisposed();

        using var writer = BuildWriter();
        writer.Save(destination);
    }

    /// <summary>
    /// Asynchronously builds and writes the archive to
    /// <paramref name="outputPath"/>.
    /// </summary>
    public async Task SaveAsync(
        string            outputPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        string tempPath = outputPath + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using var writer = BuildWriter();
            await writer.SaveAsync(tempPath, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Asynchronously writes the archive to the provided
    /// <paramref name="destination"/> stream.
    /// </summary>
    public async Task SaveAsync(
        Stream            destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        using var writer = BuildWriter();
        await writer.SaveAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Merge helper – builds an AsarWriter combining original + modifications
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Constructs an <see cref="AsarWriter"/> that merges original content
    /// from the reader (if open) with all staged modifications.
    /// </summary>
    private AsarWriter BuildWriter()
    {
        var writer = new AsarWriter();

        // ── Phase 1: Copy surviving entries from the original archive ─────────

        if (_reader is not null)
        {
            foreach (AsarFileEntry original in _reader.GetAllFiles())
            {
                string vp = original.VirtualPath;

                // Skip entries that have been removed.
                if (_removedPaths.Contains(vp)) continue;

                // Skip entries that will be replaced by a staged modification.
                if (_modifiedEntries.ContainsKey(vp)) continue;

                // Stream the original content from the archive using random-access.
                Stream entryStream = _reader.OpenEntryStream(vp);
                writer.AddFile(vp, entryStream, original.IsExecutable);
            }
        }

        // ── Phase 2: Add / overwrite with staged modifications ────────────────

        foreach ((string vp, ModifiedEntry mod) in _modifiedEntries)
        {
            // Reset seekable streams to position 0 before handing them to the writer.
            if (mod.Content.CanSeek)
                mod.Content.Seek(0, SeekOrigin.Begin);

            // We pass the stream without transferring ownership; the writer
            // wraps it.  Both this archive and the writer share the same
            // underlying MemoryStream but Seek+Read is safe here.
            writer.AddFile(vp, mod.Content, mod.IsExecutable);
        }

        return writer;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Utilities
    // ══════════════════════════════════════════════════════════════════════

    private static string NormalisePath(string path)
        => path.Replace('\\', '/').Trim('/');

    private void EnsureReader()
    {
        if (_reader is null)
        {
            throw new InvalidOperationException(
                "This archive was created via AsarArchive.Create() and has no " +
                "underlying archive to read from.  Call Save() first.");
        }
    }

    private void ThrowIfReadOnly()
    {
        ThrowIfDisposed();
        if (_isReadOnly) throw new AsarReadOnlyException();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, nameof(AsarArchive));

    // ══════════════════════════════════════════════════════════════════════
    // Disposal
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reader?.Dispose();

        foreach (ModifiedEntry mod in _modifiedEntries.Values)
            mod.Content.Dispose();

        _modifiedEntries.Clear();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_reader is not null)
            await _reader.DisposeAsync().ConfigureAwait(false);

        foreach (ModifiedEntry mod in _modifiedEntries.Values)
            mod.Content.Dispose();

        _modifiedEntries.Clear();
    }

    // ── Internal record for staged modifications ──────────────────────────

    private sealed record ModifiedEntry(Stream Content, bool IsExecutable, long Size);
}