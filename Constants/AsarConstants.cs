namespace Asar.Constants;

/// <summary>
/// Compile-time constants that describe the ASAR binary format.
/// Centralising these values here makes future format changes trivial:
/// update one file and every consumer picks up the change automatically.
/// </summary>
public static class AsarConstants
{
    // ──────────────────────────────────────────────────────────────────────
    // Chromium Pickle header layout
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The first four bytes of every ASAR file are a little-endian uint32
    /// whose value is always <c>4</c>.  It encodes the byte-width of the
    /// second uint32 that immediately follows (i.e. it is self-describing).
    /// </summary>
    public const int PickleHeaderSize = 4;

    /// <summary>
    /// Byte offset of the "header data size" uint32 inside the file.
    /// </summary>
    public const int HeaderDataSizeOffset = 4;

    /// <summary>
    /// Byte offset of the "header string pickle payload size" uint32.
    /// The value stored here equals <c>HeaderStringSize + 4</c>.
    /// </summary>
    public const int HeaderStringPickleOffset = 8;

    /// <summary>
    /// Byte offset of the actual JSON header length uint32.
    /// </summary>
    public const int HeaderStringSizeOffset = 12;

    /// <summary>
    /// Byte offset at which the raw JSON header string begins.
    /// </summary>
    public const int HeaderJsonStartOffset = 16;

    /// <summary>
    /// Alignment boundary (in bytes) used by the Chromium Pickle serialiser.
    /// All sections must start at a multiple of this value.
    /// </summary>
    public const int PickleAlignment = 4;

    // ──────────────────────────────────────────────────────────────────────
    // JSON field names (used by both the serialiser and the deserialiser)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Key whose value is a child-file map inside a directory node.</summary>
    public const string FilesKey = "files";

    /// <summary>Key that stores the byte size of a file entry.</summary>
    public const string SizeKey = "size";

    /// <summary>
    /// Key that stores the byte offset of a file entry <em>relative to the
    /// content base</em> (i.e. the first byte after the header section).
    /// ASAR encodes this as a JSON string, not a number, to avoid JS
    /// integer-precision loss for large archives.
    /// </summary>
    public const string OffsetKey = "offset";

    /// <summary>Key indicating the entry is stored outside the archive.</summary>
    public const string UnpackedKey = "unpacked";

    /// <summary>Key indicating the file has the executable bit set.</summary>
    public const string ExecutableKey = "executable";

    /// <summary>Key for an optional integrity block (hash information).</summary>
    public const string IntegrityKey = "integrity";

    // ──────────────────────────────────────────────────────────────────────
    // Unpacked-file conventions
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The directory suffix used for files that are flagged "unpacked".
    /// For an archive at <c>app.asar</c> the companion directory is
    /// <c>app.asar.unpacked</c>.
    /// </summary>
    public const string UnpackedDirectorySuffix = ".unpacked";

    // ──────────────────────────────────────────────────────────────────────
    // Safety limits – raise these if you work with very large archives
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>Maximum JSON header size (256 MiB) accepted when reading.</summary>
    public const long MaxHeaderSize = 256L * 1024 * 1024;

    /// <summary>Maximum single-file size (4 GiB) extractable via the stream API.</summary>
    public const long MaxFileSize = 4L * 1024 * 1024 * 1024;

    /// <summary>Default buffer size used for stream copy operations (64 KiB).</summary>
    public const int DefaultBufferSize = 64 * 1024;
}
