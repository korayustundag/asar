using Asar.Constants;
using Asar.Models;
using Asar.Serialization;

namespace Asar.Core;

// ══════════════════════════════════════════════════════════════════════════════
// AsarWriter
//
// Builds a new ASAR archive from scratch by collecting (name → content)
// pairs and then flushing everything to a stream or file in a single pass.
//
// Usage pattern
// ─────────────
//   var writer = new AsarWriter();
//   writer.AddFile("main.js",    File.OpenRead("main.js"));
//   writer.AddFile("index.html", Encoding.UTF8.GetBytes("<html>…</html>"));
//   writer.Save("app.asar");
//
// Design
// ──────
//   1. Entries are staged in an in-memory AsarDirectoryEntry tree together
//      with their content (as streams or byte arrays).
//   2. On Save/SaveAsync the content streams are iterated in insertion order
//      to compute the final offset of each file.
//   3. The header JSON is serialised and written first (binary Pickle format).
//   4. The content bytes for each file are then written sequentially.
//      This two-pass approach guarantees a valid archive without seek-back.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Builds a new ASAR archive by staging files and then flushing to disk or to
/// any writable <see cref="Stream"/>.
/// </summary>
public sealed class AsarWriter : IDisposable
{
    // ── Staged entries: virtual path → content source ──────────────────────

    // We keep a parallel list to preserve insertion order when writing content.
    private readonly List<StagedEntry>      _stagedEntries  = new();
    private readonly AsarDirectoryEntry     _root           = new(string.Empty, string.Empty);
    private          bool                   _disposed;

    // ══════════════════════════════════════════════════════════════════════
    // AddFile – bytes
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stages a file with its complete content given as a byte array.
    /// </summary>
    /// <param name="virtualPath">
    /// Forward-slash-delimited path relative to the archive root.
    /// Intermediate directories are created automatically.
    /// </param>
    /// <param name="content">The file's byte content.</param>
    /// <param name="executable">
    /// When <see langword="true"/> the executable bit is set in the header.
    /// </param>
    public void AddFile(string virtualPath, byte[] content, bool executable = false)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Wrap the byte array in a MemoryStream so the same write path can
        // be used regardless of the content source.
        AddFile(virtualPath, new MemoryStream(content, writable: false), executable);
    }

    // ══════════════════════════════════════════════════════════════════════
    // AddFile – stream
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stages a file whose content will be read from <paramref name="content"/>
    /// when <see cref="Save(string)"/> or <see cref="SaveAsync"/> is called.
    /// </summary>
    /// <param name="virtualPath">
    /// Forward-slash-delimited path relative to the archive root.
    /// </param>
    /// <param name="content">
    /// A readable stream.  It does NOT need to be seekable: the writer reads
    /// it sequentially during the save pass.
    /// <para>
    /// <b>Ownership:</b> the stream is disposed by the writer after the
    /// content has been written.
    /// </para>
    /// </param>
    /// <param name="executable">
    /// When <see langword="true"/> the executable bit is set in the header.
    /// </param>
    public void AddFile(string virtualPath, Stream content, bool executable = false)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);

        virtualPath = NormalisePath(virtualPath);

        if (!content.CanRead)
            throw new ArgumentException("The content stream must be readable.", nameof(content));

        // Determine the file size.  If the stream is seekable, read Length;
        // otherwise drain to a MemoryStream so we know the size up-front
        // (required for the header JSON).
        long size;
        Stream contentStream;

        if (content.CanSeek)
        {
            size          = content.Length - content.Position;
            contentStream = content;
        }
        else
        {
            // Buffer non-seekable stream fully (e.g., NetworkStream).
            var ms = new MemoryStream();
            content.CopyTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            size          = ms.Length;
            contentStream = ms;
        }

        // ── Register the entry in the directory tree ───────────────────────

        AsarFileEntry fileEntry = EnsureFileEntry(virtualPath, size, executable);

        // ── Stage the content source for the write pass ────────────────────

        _stagedEntries.Add(new StagedEntry(fileEntry, contentStream));
    }

    // ══════════════════════════════════════════════════════════════════════
    // AddFile – text
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stages a text file encoded as UTF-8.
    /// </summary>
    public void AddFile(string virtualPath, string text, bool executable = false)
        => AddFile(virtualPath, System.Text.Encoding.UTF8.GetBytes(text), executable);

    // ══════════════════════════════════════════════════════════════════════
    // AddDirectory
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Explicitly creates an empty directory node at <paramref name="virtualPath"/>.
    /// Intermediate directories are created automatically – this method is
    /// only needed when you want an empty directory to appear in the archive.
    /// </summary>
    public void AddDirectory(string virtualPath)
    {
        ThrowIfDisposed();
        virtualPath = NormalisePath(virtualPath);
        EnsureDirectoryEntry(virtualPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Remove
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Removes a previously staged entry (file or directory) from the
    /// archive.  Has no effect if the path does not exist.
    /// </summary>
    public void Remove(string virtualPath)
    {
        ThrowIfDisposed();
        virtualPath = NormalisePath(virtualPath);

        string[]           segments = virtualPath.Split('/');
        AsarDirectoryEntry dir      = _root;

        // Walk to the parent directory of the target.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (!dir.Children.TryGetValue(segments[i], out AsarEntry? child)
                || child is not AsarDirectoryEntry subDir)
                return; // path does not exist

            dir = subDir;
        }

        string lastName = segments[^1];

        // Also remove the corresponding staged entry content stream.
        if (dir.Children.TryGetValue(lastName, out AsarEntry? target)
            && target is AsarFileEntry fileTarget)
        {
            int idx = _stagedEntries.FindIndex(s => s.Entry == fileTarget);
            if (idx >= 0)
            {
                _stagedEntries[idx].Content.Dispose();
                _stagedEntries.RemoveAt(idx);
            }
        }

        dir.RemoveChild(lastName);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Save (synchronous)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the staged archive to <paramref name="outputPath"/>, creating
    /// or overwriting the file as necessary.
    /// </summary>
    public void Save(string outputPath)
    {
        ThrowIfDisposed();

        using var fs = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            AsarConstants.DefaultBufferSize,
            useAsync: false);

        Save(fs);
    }

    /// <summary>
    /// Writes the staged archive to <paramref name="destination"/>.
    /// The stream is left open and positioned after the last written byte.
    /// </summary>
    public void Save(Stream destination)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));

        // ── Pass 1: compute content offsets ───────────────────────────────
        //
        // Walk staged entries in insertion order and assign each file's
        // Offset before serialising the header JSON.

        long currentOffset = 0;
        foreach (StagedEntry staged in _stagedEntries)
        {
            staged.Entry.Offset = currentOffset;
            currentOffset      += staged.Entry.Size;
        }

        // ── Pass 2: serialise and write the binary header ─────────────────

        var header = new AsarHeader(_root, contentBaseOffset: 0, jsonHeaderSize: 0);
        AsarHeaderSerializer.Serialize(header, destination);

        // ── Pass 3: write each file's content in order ────────────────────

        byte[] copyBuffer = new byte[AsarConstants.DefaultBufferSize];
        foreach (StagedEntry staged in _stagedEntries)
        {
            // Rewind seekable streams (in case the caller passed an
            // already-read stream before saving).
            if (staged.Content.CanSeek)
                staged.Content.Seek(0, SeekOrigin.Begin);

            CopyExact(staged.Content, destination, staged.Entry.Size, copyBuffer);
        }

        destination.Flush();
    }

    // ══════════════════════════════════════════════════════════════════════
    // SaveAsync (asynchronous)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Asynchronously writes the staged archive to <paramref name="outputPath"/>.
    /// </summary>
    public async Task SaveAsync(
        string            outputPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var fs = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            AsarConstants.DefaultBufferSize,
            useAsync: true);

        await SaveAsync(fs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously writes the staged archive to <paramref name="destination"/>.
    /// </summary>
    public async Task SaveAsync(
        Stream            destination,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);

        // Assign offsets.
        long currentOffset = 0;
        foreach (StagedEntry staged in _stagedEntries)
        {
            staged.Entry.Offset = currentOffset;
            currentOffset      += staged.Entry.Size;
        }

        // Write the header.
        var header = new AsarHeader(_root, contentBaseOffset: 0, jsonHeaderSize: 0);
        AsarHeaderSerializer.Serialize(header, destination);

        // Write file content sequentially.
        byte[] copyBuffer = new byte[AsarConstants.DefaultBufferSize];
        foreach (StagedEntry staged in _stagedEntries)
        {
            if (staged.Content.CanSeek)
                staged.Content.Seek(0, SeekOrigin.Begin);

            await CopyExactAsync(
                staged.Content,
                destination,
                staged.Entry.Size,
                copyBuffer,
                cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internal helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns or creates the <see cref="AsarFileEntry"/> node for
    /// <paramref name="virtualPath"/>, creating intermediate directories
    /// as needed.
    /// </summary>
    private AsarFileEntry EnsureFileEntry(string virtualPath, long size, bool executable)
    {
        string[]           parts  = virtualPath.Split('/');
        AsarDirectoryEntry dir    = _root;

        // Walk / create intermediate directory nodes.
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string seg = parts[i];
            if (!dir.Children.TryGetValue(seg, out AsarEntry? existing))
            {
                string dirPath = string.Join('/', parts[..( i + 1)]);
                var    subDir  = new AsarDirectoryEntry(seg, dirPath);
                dir.AddChild(subDir);
                dir = subDir;
            }
            else
            {
                dir = (AsarDirectoryEntry)existing;
            }
        }

        string   fileName  = parts[^1];
        string   fullPath  = virtualPath;

        // Replace an existing entry if the same path is staged twice.
        if (dir.Children.TryGetValue(fileName, out AsarEntry? old)
            && old is AsarFileEntry oldFile)
        {
            int idx = _stagedEntries.FindIndex(s => s.Entry == oldFile);
            if (idx >= 0)
            {
                _stagedEntries[idx].Content.Dispose();
                _stagedEntries.RemoveAt(idx);
            }
            dir.RemoveChild(fileName);
        }

        var fileEntry = new AsarFileEntry(fileName, fullPath, offset: 0, size)
        {
            IsExecutable = executable
        };
        dir.AddChild(fileEntry);
        return fileEntry;
    }

    /// <summary>
    /// Returns or creates the <see cref="AsarDirectoryEntry"/> node for
    /// the given path, creating intermediate nodes as needed.
    /// </summary>
    private AsarDirectoryEntry EnsureDirectoryEntry(string virtualPath)
    {
        if (string.IsNullOrEmpty(virtualPath)) return _root;

        string[]           parts = virtualPath.Split('/');
        AsarDirectoryEntry dir   = _root;

        for (int i = 0; i < parts.Length; i++)
        {
            string seg = parts[i];
            if (!dir.Children.TryGetValue(seg, out AsarEntry? existing))
            {
                string dirPath = string.Join('/', parts[..(i + 1)]);
                var    subDir  = new AsarDirectoryEntry(seg, dirPath);
                dir.AddChild(subDir);
                dir = subDir;
            }
            else
            {
                dir = (AsarDirectoryEntry)existing;
            }
        }

        return dir;
    }

    /// <summary>
    /// Copies exactly <paramref name="byteCount"/> bytes from
    /// <paramref name="source"/> to <paramref name="destination"/>.
    /// </summary>
    private static void CopyExact(Stream source, Stream destination, long byteCount, byte[] buffer)
    {
        long remaining = byteCount;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read   = source.Read(buffer, 0, toRead);
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// Asynchronously copies exactly <paramref name="byteCount"/> bytes.
    /// </summary>
    private static async Task CopyExactAsync(
        Stream            source,
        Stream            destination,
        long              byteCount,
        byte[]            buffer,
        CancellationToken cancellationToken)
    {
        long remaining = byteCount;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read   = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken)
                                     .ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                             .ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static string NormalisePath(string path)
        => path.Replace('\\', '/').Trim('/');

    // ══════════════════════════════════════════════════════════════════════
    // Disposal
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release all staged content streams.
        foreach (StagedEntry staged in _stagedEntries)
            staged.Content.Dispose();

        _stagedEntries.Clear();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, nameof(AsarWriter));

    // ── Internal staging record ───────────────────────────────────────────

    private sealed record StagedEntry(AsarFileEntry Entry, Stream Content);
}