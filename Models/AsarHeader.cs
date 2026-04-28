namespace Asar.Models;

// ══════════════════════════════════════════════════════════════════════════════
// AsarHeader – top-level model that represents the decoded ASAR header.
//
// Responsibilities:
//   • Store the root directory of the virtual filesystem tree.
//   • Expose the content-base offset so callers can translate relative
//     file offsets into absolute stream positions for random access.
//   • Provide path-resolution helpers that traverse the tree.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Represents the fully-parsed ASAR header.
/// </summary>
/// <remarks>
/// After parsing, the content of every file in the archive can be accessed at:
/// <code>
///   absoluteOffset = header.ContentBaseOffset + fileEntry.Offset;
/// </code>
/// This enables true random-access reads without scanning the archive.
/// </remarks>
public sealed class AsarHeader
{
    // ── Root of the virtual filesystem ───────────────────────────────────

    /// <summary>
    /// The root directory that contains every file and sub-directory in
    /// the archive's virtual filesystem.
    /// </summary>
    public AsarDirectoryEntry Root { get; set; }

    // ── Archive layout ────────────────────────────────────────────────────

    /// <summary>
    /// Absolute byte offset (from the start of the archive stream) at which
    /// file content begins.  All <see cref="AsarFileEntry.Offset"/> values
    /// are relative to this position.
    /// </summary>
    public long ContentBaseOffset { get; set; }

    /// <summary>
    /// The raw size in bytes of the JSON header string as it appears in the
    /// binary file (before any padding).
    /// </summary>
    public int JsonHeaderSize { get; set; }

    // ── Construction ─────────────────────────────────────────────────────

    /// <summary>Creates an empty header with a fresh root directory.</summary>
    public AsarHeader()
    {
        Root = new AsarDirectoryEntry(string.Empty, string.Empty);
    }

    /// <summary>Creates a header with a pre-built root directory.</summary>
    public AsarHeader(AsarDirectoryEntry root, long contentBaseOffset, int jsonHeaderSize)
    {
        Root              = root;
        ContentBaseOffset = contentBaseOffset;
        JsonHeaderSize    = jsonHeaderSize;
    }

    // ── Path resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Resolves a virtual path (e.g. <c>"src/main.js"</c>) and returns the
    /// matching <see cref="AsarEntry"/>, or <see langword="null"/> when the
    /// path does not exist in the archive.
    /// </summary>
    /// <param name="virtualPath">
    /// Forward-slash-delimited path relative to the archive root.
    /// A leading slash is accepted and stripped automatically.
    /// </param>
    public AsarEntry? Resolve(string virtualPath)
    {
        // Normalise the path: strip leading/trailing slashes, unify separators.
        virtualPath = NormalisePath(virtualPath);

        if (string.IsNullOrEmpty(virtualPath))
            return Root;

        // Walk segment by segment through the directory tree.
        string[] segments = virtualPath.Split('/');
        AsarEntry current = Root;

        foreach (string segment in segments)
        {
            if (segment.Length == 0)
                continue; // ignore consecutive slashes

            if (current is not AsarDirectoryEntry dir)
                return null; // tried to descend into a file

            if (!dir.TryGetChild(segment, out AsarEntry? child) || child is null)
                return null; // segment not found

            current = child;
        }

        return current;
    }

    /// <summary>
    /// Resolves a virtual path and returns the entry, throwing an exception
    /// if not found.
    /// </summary>
    /// <exception cref="Exceptions.AsarEntryNotFoundException">
    /// Thrown when the path does not exist.
    /// </exception>
    public AsarEntry ResolveRequired(string virtualPath)
        => Resolve(virtualPath)
           ?? throw new Exceptions.AsarEntryNotFoundException(virtualPath);

    /// <summary>
    /// Resolves a path that must be a file entry.
    /// </summary>
    /// <exception cref="Exceptions.AsarEntryNotFoundException"/>
    /// <exception cref="Exceptions.AsarEntryTypeMismatchException"/>
    public AsarFileEntry ResolveFile(string virtualPath)
    {
        AsarEntry entry = ResolveRequired(virtualPath);

        return entry as AsarFileEntry
               ?? throw new Exceptions.AsarEntryTypeMismatchException(virtualPath, "file");
    }

    /// <summary>
    /// Resolves a path that must be a directory entry.
    /// </summary>
    /// <exception cref="Exceptions.AsarEntryNotFoundException"/>
    /// <exception cref="Exceptions.AsarEntryTypeMismatchException"/>
    public AsarDirectoryEntry ResolveDirectory(string virtualPath)
    {
        AsarEntry entry = ResolveRequired(virtualPath);

        return entry as AsarDirectoryEntry
               ?? throw new Exceptions.AsarEntryTypeMismatchException(virtualPath, "directory");
    }

    // ── Enumeration ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a flat enumeration of every <see cref="AsarFileEntry"/> in
    /// the archive, traversing the tree in depth-first order.
    /// </summary>
    public IEnumerable<AsarFileEntry> GetAllFiles()
        => EnumerateFiles(Root);

    /// <summary>
    /// Returns a flat enumeration of every <see cref="AsarEntry"/> (files
    /// and directories) in depth-first order.
    /// </summary>
    public IEnumerable<AsarEntry> GetAllEntries()
        => EnumerateEntries(Root);

    // ── Internal helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Recursively walks the directory tree and yields file entries.
    /// </summary>
    private static IEnumerable<AsarFileEntry> EnumerateFiles(AsarDirectoryEntry dir)
    {
        foreach (AsarEntry child in dir.Children.Values)
        {
            if (child is AsarFileEntry file)
            {
                yield return file;
            }
            else if (child is AsarDirectoryEntry subDir)
            {
                // Recurse into sub-directories.
                foreach (AsarFileEntry nested in EnumerateFiles(subDir))
                    yield return nested;
            }
        }
    }

    /// <summary>
    /// Recursively walks the directory tree and yields every entry including
    /// directory nodes themselves.
    /// </summary>
    private static IEnumerable<AsarEntry> EnumerateEntries(AsarDirectoryEntry dir)
    {
        foreach (AsarEntry child in dir.Children.Values)
        {
            yield return child;

            if (child is AsarDirectoryEntry subDir)
            {
                foreach (AsarEntry nested in EnumerateEntries(subDir))
                    yield return nested;
            }
        }
    }

    /// <summary>
    /// Strips leading and trailing slashes and replaces back-slashes with
    /// forward slashes so paths from Windows callers are handled correctly.
    /// </summary>
    private static string NormalisePath(string path)
        => path.Replace('\\', '/').Trim('/');
}