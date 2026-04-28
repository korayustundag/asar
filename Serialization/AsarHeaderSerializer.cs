using System.Text;
using System.Text.Json;
using Asar.Constants;
using Asar.Exceptions;
using Asar.Models;

namespace Asar.Serialization;

// ══════════════════════════════════════════════════════════════════════════════
// AsarHeaderSerializer
//
// Converts between the binary Chromium-Pickle ASAR header and the in-memory
// AsarHeader model.
//
// Binary layout recap:
//   [uint32 LE = 4]                  ← pickle header size field
//   [uint32 LE = headerPickleSize]   ← size of the inner pickle payload
//   [uint32 LE = strPickleSize]      ← size of the string pickle (jsonLen+4)
//   [uint32 LE = jsonLen]            ← byte length of JSON string
//   [jsonLen bytes]                  ← UTF-8 JSON header
//   [0-3 padding bytes]              ← pad to next 4-byte boundary
//   ── content starts here ──
//
// All integers are little-endian.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Handles reading and writing of the ASAR binary header section.
/// </summary>
public static class AsarHeaderSerializer
{
    // ── JSON options shared across all operations ─────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Compact output to keep header size small.
        WriteIndented        = false,
        // Allow trailing commas / comments when reading (permissive).
        AllowTrailingCommas  = true,
        ReadCommentHandling  = JsonCommentHandling.Skip,
    };

    // ══════════════════════════════════════════════════════════════════════
    // Deserialise – binary → AsarHeader
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the binary ASAR header from <paramref name="stream"/> (which
    /// must be positioned at offset 0) and returns a fully-populated
    /// <see cref="AsarHeader"/> ready for random-access lookups.
    /// </summary>
    /// <param name="stream">
    /// A readable, seekable stream positioned at the very beginning of the
    /// ASAR file.
    /// </param>
    /// <exception cref="AsarHeaderException">
    /// Thrown if the header is malformed or exceeds safety limits.
    /// </exception>
    public static AsarHeader Deserialize(Stream stream)
    {
        // ── Step 1: Read the four leading uint32 values ───────────────────

        Span<byte> leadingBytes = stackalloc byte[AsarConstants.HeaderJsonStartOffset]; // 16 bytes
        ReadExactly(stream, leadingBytes);

        // Field 0 (offset 0): must equal 4.
        uint field0 = ReadUInt32LE(leadingBytes, 0);
        if (field0 != AsarConstants.PickleHeaderSize)
        {
            throw new AsarHeaderException(
                $"Invalid ASAR header: expected field0 = 4, got {field0}. " +
                "This file may not be a valid ASAR archive.");
        }

        // Field 1 (offset 4): headerPickleSize (= jsonLen + 8).
        uint headerPickleSize = ReadUInt32LE(leadingBytes, 4);

        // Field 2 (offset 8): string pickle payload size (= jsonLen + 4) – read but not stored.
        // uint strPickleSize = ReadUInt32LE(leadingBytes, 8);

        // Field 3 (offset 12): actual JSON byte length.
        uint jsonLen = ReadUInt32LE(leadingBytes, 12);

        // ── Step 2: Safety check ──────────────────────────────────────────

        if (jsonLen > AsarConstants.MaxHeaderSize)
        {
            throw new AsarLimitExceededException(
                $"ASAR header JSON size ({jsonLen:N0} bytes) exceeds the " +
                $"safety limit of {AsarConstants.MaxHeaderSize:N0} bytes.");
        }

        // ── Step 3: Read the JSON bytes ───────────────────────────────────

        byte[] jsonBytes = new byte[jsonLen];
        ReadExactly(stream, jsonBytes);

        string json = Encoding.UTF8.GetString(jsonBytes);

        // ── Step 4: Calculate the content base offset ─────────────────────
        //
        // Content starts directly after the first 8 bytes (two leading uint32s)
        // plus the headerPickleSize rounded up to the next 4-byte boundary.
        //
        // contentBase = 8 + Align4(headerPickleSize)
        //             = 8 + Align4(jsonLen + 8)

        long contentBase = 8L + Align4(headerPickleSize);

        // ── Step 5: Parse the JSON into the entry tree ────────────────────

        AsarDirectoryEntry root = ParseJsonHeader(json);

        return new AsarHeader(root, contentBase, (int)jsonLen);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Serialise – AsarHeader → binary
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the binary ASAR header section for the given
    /// <paramref name="header"/> into <paramref name="destination"/>.
    /// After this call the stream is positioned at the content base
    /// (ready to write file content).
    /// </summary>
    /// <returns>
    /// The number of bytes written (= the content base offset).
    /// </returns>
    public static long Serialize(AsarHeader header, Stream destination)
    {
        // ── Step 1: Serialise the entry tree to JSON ──────────────────────

        string json        = SerializeJsonHeader(header.Root);
        byte[] jsonBytes   = Encoding.UTF8.GetBytes(json);
        int    jsonLen     = jsonBytes.Length;

        // ── Step 2: Compute pickle sizes and padding ───────────────────────

        // headerPickleSize = jsonLen + 8  (two uint32s: strPickleSize + jsonLen)
        uint headerPickleSize = (uint)(jsonLen + 8);

        // strPickleSize = jsonLen + 4  (one uint32: the length field)
        uint strPickleSize    = (uint)(jsonLen + 4);

        // Total bytes in the header section = 8 + Align4(headerPickleSize)
        long contentBase = 8L + Align4(headerPickleSize);
        int  paddingLen  = (int)(contentBase - (8 + headerPickleSize));

        // ── Step 3: Write the four uint32 fields ──────────────────────────

        Span<byte> leadingBytes = stackalloc byte[AsarConstants.HeaderJsonStartOffset];

        WriteUInt32LE(leadingBytes, 0, AsarConstants.PickleHeaderSize); // field0 = 4
        WriteUInt32LE(leadingBytes, 4, headerPickleSize);               // field1
        WriteUInt32LE(leadingBytes, 8, strPickleSize);                  // field2
        WriteUInt32LE(leadingBytes, 12, (uint)jsonLen);                 // field3

        destination.Write(leadingBytes);

        // ── Step 4: Write JSON and optional padding ───────────────────────

        destination.Write(jsonBytes);

        // Pad with zero bytes to reach the next 4-byte boundary.
        if (paddingLen > 0)
        {
            Span<byte> padding = stackalloc byte[paddingLen];
            padding.Clear();
            destination.Write(padding);
        }

        return contentBase;
    }

    // ══════════════════════════════════════════════════════════════════════
    // JSON <-> Tree  helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts the raw JSON header string into an <see cref="AsarDirectoryEntry"/>
    /// tree.  This is the core deserialization step.
    /// </summary>
    private static AsarDirectoryEntry ParseJsonHeader(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling     = JsonCommentHandling.Skip,
        });

        JsonElement root = doc.RootElement;

        // The top-level object must have a "files" key.
        if (!root.TryGetProperty(AsarConstants.FilesKey, out JsonElement filesElement))
        {
            throw new AsarHeaderException(
                "The ASAR header JSON is missing the root \"files\" key.");
        }

        // Recursively parse the "files" object into a directory entry.
        AsarDirectoryEntry rootDir = new(string.Empty, string.Empty);
        ParseDirectory(filesElement, rootDir, parentPath: string.Empty);

        return rootDir;
    }

    /// <summary>
    /// Recursively processes a JSON object that represents the children of
    /// <paramref name="directory"/>, populating its
    /// <see cref="AsarDirectoryEntry.Children"/> collection.
    /// </summary>
    private static void ParseDirectory(
        JsonElement filesObject,
        AsarDirectoryEntry directory,
        string parentPath)
    {
        foreach (JsonProperty prop in filesObject.EnumerateObject())
        {
            string name        = prop.Name;
            JsonElement value  = prop.Value;

            // Build the full virtual path for this child.
            string virtualPath = parentPath.Length == 0
                ? name
                : $"{parentPath}/{name}";

            // ── Determine whether this is a file or a directory ───────────
            //
            // A directory node has a "files" property.
            // A file node has at least a "size" property.

            if (value.TryGetProperty(AsarConstants.FilesKey, out JsonElement childFiles))
            {
                // This node is a directory.
                AsarDirectoryEntry subDir = new(name, virtualPath)
                {
                    IsUnpacked = GetBoolOrDefault(value, AsarConstants.UnpackedKey, false)
                };

                directory.AddChild(subDir);

                // Recurse into the sub-directory.
                ParseDirectory(childFiles, subDir, virtualPath);
            }
            else
            {
                // This node is a file.
                AsarFileEntry file = new(name, virtualPath)
                {
                    IsUnpacked   = GetBoolOrDefault(value, AsarConstants.UnpackedKey, false),
                    IsExecutable = GetBoolOrDefault(value, AsarConstants.ExecutableKey, false),
                };

                // "size" is an integer (number of bytes).
                if (value.TryGetProperty(AsarConstants.SizeKey, out JsonElement sizeEl))
                    file.Size = sizeEl.GetInt64();

                // "offset" is stored as a string in the JSON to avoid JS precision loss.
                if (value.TryGetProperty(AsarConstants.OffsetKey, out JsonElement offsetEl))
                {
                    string? offsetStr = offsetEl.ValueKind == JsonValueKind.String
                        ? offsetEl.GetString()
                        : offsetEl.GetRawText();

                    if (long.TryParse(offsetStr, out long offset))
                        file.Offset = offset;
                }

                // Optional integrity block.
                if (value.TryGetProperty(AsarConstants.IntegrityKey, out JsonElement integrityEl))
                    file.Integrity = ParseIntegrity(integrityEl);

                directory.AddChild(file);
            }
        }
    }

    /// <summary>
    /// Parses an optional integrity JSON object into an
    /// <see cref="AsarIntegrity"/> record.
    /// </summary>
    private static AsarIntegrity ParseIntegrity(JsonElement el)
    {
        var integrity = new AsarIntegrity();

        if (el.TryGetProperty("algorithm", out JsonElement alg))
            integrity.Algorithm = alg.GetString() ?? string.Empty;

        if (el.TryGetProperty("hash", out JsonElement hash))
            integrity.Hash = hash.GetString() ?? string.Empty;

        if (el.TryGetProperty("blockSize", out JsonElement blockSize))
            integrity.BlockSize = blockSize.GetInt32();

        if (el.TryGetProperty("blocks", out JsonElement blocks)
            && blocks.ValueKind == JsonValueKind.Array)
        {
            integrity.Blocks = new List<string>();
            foreach (JsonElement block in blocks.EnumerateArray())
            {
                string? b = block.GetString();
                if (b is not null)
                    integrity.Blocks.Add(b);
            }
        }

        return integrity;
    }

    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts the in-memory entry tree back into the compact JSON string
    /// that lives inside the ASAR binary header.
    /// </summary>
    private static string SerializeJsonHeader(AsarDirectoryEntry root)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false
        });

        writer.WriteStartObject();
        writer.WritePropertyName(AsarConstants.FilesKey);
        WriteDirectory(writer, root);
        writer.WriteEndObject();

        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes all children of <paramref name="dir"/> as JSON object members.
    /// Recurses into sub-directories.
    /// </summary>
    private static void WriteDirectory(Utf8JsonWriter writer, AsarDirectoryEntry dir)
    {
        writer.WriteStartObject();

        foreach ((string name, AsarEntry entry) in dir.Children)
        {
            writer.WritePropertyName(name);

            if (entry is AsarDirectoryEntry subDir)
            {
                // Directory node: write a "files" wrapper then recurse.
                writer.WriteStartObject();
                WriteCommonFlags(writer, entry);
                writer.WritePropertyName(AsarConstants.FilesKey);
                WriteDirectory(writer, subDir);
                writer.WriteEndObject();
            }
            else if (entry is AsarFileEntry file)
            {
                // File node: write size, offset, and optional flags.
                writer.WriteStartObject();
                writer.WriteNumber(AsarConstants.SizeKey, file.Size);

                if (!file.IsUnpacked)
                {
                    // Offset is written as a JSON string to match the official format.
                    writer.WriteString(AsarConstants.OffsetKey, file.Offset.ToString());
                }

                WriteCommonFlags(writer, entry);

                if (file.IsExecutable)
                    writer.WriteBoolean(AsarConstants.ExecutableKey, true);

                if (file.Integrity is not null)
                    WriteIntegrity(writer, file.Integrity);

                writer.WriteEndObject();
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes the "unpacked" flag when it is set to true.</summary>
    private static void WriteCommonFlags(Utf8JsonWriter writer, AsarEntry entry)
    {
        if (entry.IsUnpacked)
            writer.WriteBoolean(AsarConstants.UnpackedKey, true);
    }

    /// <summary>Writes the optional integrity block.</summary>
    private static void WriteIntegrity(Utf8JsonWriter writer, AsarIntegrity integrity)
    {
        writer.WritePropertyName(AsarConstants.IntegrityKey);
        writer.WriteStartObject();
        writer.WriteString("algorithm", integrity.Algorithm);
        writer.WriteString("hash", integrity.Hash);

        if (integrity.BlockSize.HasValue)
            writer.WriteNumber("blockSize", integrity.BlockSize.Value);

        if (integrity.Blocks is { Count: > 0 })
        {
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();
            foreach (string block in integrity.Blocks)
                writer.WriteStringValue(block);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Low-level binary helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream,
    /// throwing if the stream ends early.
    /// </summary>
    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                throw new AsarHeaderException(
                    $"Unexpected end of stream while reading ASAR header " +
                    $"(read {totalRead} of {buffer.Length} bytes).");
            }
            totalRead += read;
        }
    }

    /// <summary>
    /// Reads a little-endian uint32 from <paramref name="span"/> at
    /// <paramref name="offset"/>.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint ReadUInt32LE(ReadOnlySpan<byte> span, int offset)
        => (uint)(span[offset]
                | (span[offset + 1] << 8)
                | (span[offset + 2] << 16)
                | (span[offset + 3] << 24));

    /// <summary>
    /// Writes a little-endian uint32 into <paramref name="span"/> at
    /// <paramref name="offset"/>.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt32LE(Span<byte> span, int offset, uint value)
    {
        span[offset]     = (byte) value;
        span[offset + 1] = (byte)(value >> 8);
        span[offset + 2] = (byte)(value >> 16);
        span[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to the next multiple of
    /// <see cref="AsarConstants.PickleAlignment"/> (4).
    /// </summary>
    private static long Align4(long value)
    {
        int align = AsarConstants.PickleAlignment;
        return (value + align - 1) & ~(long)(align - 1);
    }

    // Helper: read a boolean field from a JsonElement with a default fallback.
    private static bool GetBoolOrDefault(JsonElement el, string key, bool defaultValue)
        => el.TryGetProperty(key, out JsonElement val) && val.ValueKind == JsonValueKind.True
           ? true
           : defaultValue;
}