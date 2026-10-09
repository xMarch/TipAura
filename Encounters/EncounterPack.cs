using System.Collections.Concurrent;
using System.IO.Compression;

// A zip in data/pack holding index.yaml and the sounds and icons it uses. Entries are Zstandard (ZIP
// method 93), Deflate or stored. .NET 11 reads and writes the zip structure itself and (de)compresses
// with System.IO.Compression.ZstandardStream, because its ZipArchive has no method 93; .NET 10 uses
// SharpCompress. Written packs are always Zstandard. The rules are specified in TECHNICAL.md
// ("Encounter packs").
// Immutable and thread-safe: every read opens the zip again.
internal sealed class EncounterPack
{
    internal const string IndexName = "index.yaml";
    internal const string DirectoryName = "pack";
    internal const string Extension = ".zip";
    // Assets live directly in these folders: sounds in sfx/, images in icons/.
    internal const string SoundFolder = "sfx", IconFolder = "icons";
    internal const long MaxEntryBytes = 64L << 20;
    internal const int MaxEntries = 4096;
    private const int Stored = 0, Deflate = 8, Zstandard = 93;

    private readonly Dictionary<string, Entry> _entries;

    private EncounterPack(string path, Dictionary<string, Entry> entries)
    {
        FullPath = path;
        _entries = entries;
    }

    internal string FullPath { get; }
    // The pack id is its file name; index.yaml must declare the same id.
    internal string FileId => System.IO.Path.GetFileNameWithoutExtension(FullPath);
    internal IEnumerable<string> Names => _entries.Keys;

    private readonly record struct Entry(string Name, int Method, long CompressedSize, long Size, long HeaderOffset);

    // Opened packs by full path, so asset keys ("<zip>|<entry>") can be read on any thread. Opening a
    // pack again replaces it, which picks up a zip that changed on disk.
    private static readonly ConcurrentDictionary<string, EncounterPack> s_open = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsPackPath(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    // Throws InvalidDataException or IOException with a message for the Encounter tab.
    internal static EncounterPack Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, method, encrypted, compressed, size, offset) in ReadDirectory(path))
        {
            if (rawName.EndsWith('/') && size == 0) continue;
            if (EntryName(rawName) is not { } name || name != rawName)
                throw new InvalidDataException(Localization.F("Pack entry '{0}' has an invalid name.", rawName));
            if (!name.Equals(IndexName, StringComparison.OrdinalIgnoreCase) && AssetFolder(name) is null)
                throw new InvalidDataException(Localization.F("Pack entry '{0}' is not allowed; a pack holds only {1}, sounds in sfx/ and images in icons/.",
                    name, IndexName));
            if (encrypted) throw new InvalidDataException(Localization.F("Pack entry '{0}' is encrypted.", name));
            if (method is not (Stored or Deflate or Zstandard))
                throw new InvalidDataException(Localization.F("Pack entry '{0}' uses compression method {1}; use Zstandard, Deflate or store.", name, method));
            if (size > MaxEntryBytes || compressed > MaxEntryBytes + (1 << 20))
                throw new InvalidDataException(Localization.F("Pack entry '{0}' is larger than {1} MB.", name, MaxEntryBytes >> 20));
            if (!entries.TryAdd(name, new Entry(name, method, compressed, size, offset)))
                throw new InvalidDataException(Localization.F("Pack entry '{0}' appears twice.", name));
            if (entries.Count > MaxEntries) throw new InvalidDataException(Localization.F("The pack has more than {0} entries.", MaxEntries));
        }
        var pack = new EncounterPack(path, entries);
        s_open[path] = pack;
        return pack;
    }

    // A relative asset path as an entry name ("sfx/a.ogg"), or null when it is absolute or leaves the pack.
    internal static string? EntryName(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative[0] is '/' or '\\') return null;
        var parts = new List<string>();
        foreach (string part in relative.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    // The folder an entry belongs to: SoundFolder for "sfx/<file>" with a sound extension, IconFolder for
    // "icons/<file>" with an image extension, otherwise null. Subfolders are not allowed.
    internal static string? AssetFolder(string entry)
    {
        int slash = entry.IndexOf('/');
        if (slash <= 0 || slash == entry.Length - 1 || entry.IndexOf('/', slash + 1) >= 0) return null;
        string folder = entry[..slash], extension = System.IO.Path.GetExtension(entry).ToLowerInvariant();
        if (folder.Equals(SoundFolder, StringComparison.OrdinalIgnoreCase) && EncounterYaml.SoundExtensions.Contains(extension)) return SoundFolder;
        if (folder.Equals(IconFolder, StringComparison.OrdinalIgnoreCase) && EncounterYaml.ImageExtensions.Contains(extension)) return IconFolder;
        return null;
    }

    internal bool Contains(string entry) => _entries.ContainsKey(entry);

    // The entry as stored in the zip, so keys of the same file match whatever case the YAML used.
    internal string? Find(string relative) =>
        EntryName(relative) is { } name && _entries.TryGetValue(name, out var entry) ? entry.Name : null;

    internal byte[] Read(string entry)
    {
        if (!_entries.TryGetValue(entry, out var info))
            throw new FileNotFoundException(Localization.F("{0} is not in the pack.", entry));
        try { return ReadEntry(info); }
        catch (Exception ex) when (ex is not (InvalidDataException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException(Localization.F("Could not read pack entry '{0}': {1}", entry, ex.Message), ex);
        }
    }

    // ---- Asset keys ----

    // '|' cannot occur in a Windows path, so it separates the zip path from the entry name.
    internal const char KeySeparator = '|';

    internal string Key(string entry) => FullPath + KeySeparator + entry;

    internal static bool IsKey(string key) => key.Contains(KeySeparator);

    // Reads a loose file path or a pack key.
    internal static byte[] ReadAsset(string key)
    {
        int split = key.IndexOf(KeySeparator);
        if (split < 0) return File.ReadAllBytes(key);
        string zip = key[..split];
        var pack = s_open.TryGetValue(zip, out var open) ? open : Open(zip);
        return pack.Read(key[(split + 1)..]);
    }

    // ---- Zip writing ----

    // Writes a new pack through a temporary file: every entry Zstandard-compressed, with UTF-8 names.
    // Throws IOException; the caller opens the result again, which replaces an older one in the registry.
    internal static void Write(string path, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        path = System.IO.Path.GetFullPath(path);
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                WriteZip(output, entries);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        s_open.TryRemove(path, out _);
    }

    // ---- Zip reading ----

    // Reads exactly size bytes and requires the stream to end there.
    private static byte[] ReadExactly(Stream source, long size, string name)
    {
        var data = new byte[size];
        int total = 0;
        while (total < data.Length)
        {
            int read = source.Read(data, total, data.Length - total);
            if (read == 0) throw new InvalidDataException(Localization.F("Pack entry '{0}' is truncated.", name));
            total += read;
        }
        if (source.ReadByte() >= 0) throw new InvalidDataException(Localization.F("Pack entry '{0}' is longer than its declared size.", name));
        return data;
    }

#if NET11_0_OR_GREATER
    private const uint EndSignature = 0x06054b50, Zip64EndSignature = 0x06064b50, Zip64LocatorSignature = 0x07064b50;
    private const uint CentralSignature = 0x02014b50, LocalSignature = 0x04034b50;

    private static List<(string Name, int Method, bool Encrypted, long Compressed, long Size, long Offset)> ReadDirectory(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // The end record is 22 bytes plus a comment of up to 64 KB.
        int tailLength = (int)Math.Min(file.Length, 22 + 0xFFFF);
        var tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        file.ReadExactly(tail);
        int end = -1;
        for (int i = tailLength - 22; i >= 0; i--)
            if (U32(tail, i) == EndSignature && i + 22 + U16(tail, i + 20) <= tailLength) { end = i; break; }
        if (end < 0) throw new InvalidDataException(Localization.T("Not a zip file."));
        long endPosition = file.Length - tailLength + end;
        if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0) throw new InvalidDataException(Localization.T("Split zip files are not supported."));
        long count = U16(tail, end + 10), directorySize = U32(tail, end + 12), directoryOffset = U32(tail, end + 16);
        if (count == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
        {
            var locator = new byte[20];
            if (endPosition < 20) throw new InvalidDataException(Localization.T("Not a zip file."));
            file.Position = endPosition - 20;
            file.ReadExactly(locator);
            if (U32(locator, 0) != Zip64LocatorSignature) throw new InvalidDataException(Localization.T("Not a zip file."));
            var record = new byte[56];
            file.Position = (long)U64(locator, 8);
            file.ReadExactly(record);
            if (U32(record, 0) != Zip64EndSignature) throw new InvalidDataException(Localization.T("Not a zip file."));
            count = (long)U64(record, 32);
            directorySize = (long)U64(record, 40);
            directoryOffset = (long)U64(record, 48);
        }
        if (count > MaxEntries) throw new InvalidDataException(Localization.F("The pack has more than {0} entries.", MaxEntries));
        if (directorySize > 16 << 20 || directoryOffset + directorySize > file.Length) throw new InvalidDataException(Localization.T("Not a zip file."));
        var directory = new byte[directorySize];
        file.Position = directoryOffset;
        file.ReadExactly(directory);

        var entries = new List<(string, int, bool, long, long, long)>();
        int at = 0;
        for (long i = 0; i < count; i++)
        {
            if (at + 46 > directory.Length || U32(directory, at) != CentralSignature) throw new InvalidDataException(Localization.T("Not a zip file."));
            int flags = U16(directory, at + 8), method = U16(directory, at + 10);
            long compressed = U32(directory, at + 20), size = U32(directory, at + 24);
            int nameLength = U16(directory, at + 28), extraLength = U16(directory, at + 30), commentLength = U16(directory, at + 32);
            long offset = U32(directory, at + 42);
            int next = at + 46 + nameLength + extraLength + commentLength;
            if (next > directory.Length) throw new InvalidDataException(Localization.T("Not a zip file."));
            string name;
            // Names are UTF-8 when flag bit 11 is set; tools write ASCII names without it, and other code
            // pages are rejected rather than guessed.
            try { name = new System.Text.UTF8Encoding(false, true).GetString(directory, at + 46, nameLength); }
            catch (System.Text.DecoderFallbackException) { throw new InvalidDataException(Localization.T("A pack entry name is not valid UTF-8.")); }
            if ((flags & 0x800) == 0 && !System.Text.Ascii.IsValid(name))
                throw new InvalidDataException(Localization.F("Pack entry '{0}' has an invalid name.", name));
            // Zip64 extra field: the 64-bit values follow in this order for each field that is 0xFFFFFFFF.
            for (int extra = at + 46 + nameLength, extraEnd = extra + extraLength; extra + 4 <= extraEnd;)
            {
                int id = U16(directory, extra), length = U16(directory, extra + 2);
                if (id == 0x0001)
                {
                    int value = extra + 4;
                    if (size == 0xFFFFFFFF && value + 8 <= extraEnd) { size = (long)U64(directory, value); value += 8; }
                    if (compressed == 0xFFFFFFFF && value + 8 <= extraEnd) { compressed = (long)U64(directory, value); value += 8; }
                    if (offset == 0xFFFFFFFF && value + 8 <= extraEnd) offset = (long)U64(directory, value);
                }
                extra += 4 + length;
            }
            entries.Add((name, method, (flags & 1) != 0, compressed, size, offset));
            at = next;
        }
        return entries;
    }

    private byte[] ReadEntry(Entry entry)
    {
        using var file = new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var local = new byte[30];
        file.Position = entry.HeaderOffset;
        file.ReadExactly(local);
        if (U32(local, 0) != LocalSignature) throw new InvalidDataException(Localization.F("Pack entry '{0}' is damaged.", entry.Name));
        file.Position = entry.HeaderOffset + 30 + U16(local, 26) + U16(local, 28);
        if (file.Position + entry.CompressedSize > file.Length) throw new InvalidDataException(Localization.F("Pack entry '{0}' is truncated.", entry.Name));
        var compressed = new byte[entry.CompressedSize];
        file.ReadExactly(compressed);
        using var source = new MemoryStream(compressed, writable: false);
        using Stream data = entry.Method switch
        {
            Zstandard => new ZstandardStream(source, CompressionMode.Decompress),
            Deflate => new DeflateStream(source, CompressionMode.Decompress),
            _ => source
        };
        return ReadExactly(data, entry.Size, entry.Name);
    }

    // ZipArchive cannot write method 93 either, so the zip is written here: local header and data per
    // entry, then the central directory and the end record. Entries are capped well below the Zip64 limits,
    // but the whole file is checked.
    private static void WriteZip(Stream output, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        if (entries.Count > MaxEntries) throw new IOException(Localization.F("The pack has more than {0} entries.", MaxEntries));
        var (time, date) = DosTime(DateTime.Now);
        using var central = new MemoryStream();
        using var directory = new BinaryWriter(central);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        foreach (var (name, data) in entries)
        {
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            using var compressed = new MemoryStream();
            using (var zstd = new ZstandardStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zstd.Write(data);
            uint crc = Crc32(data);
            writer.Flush();
            long offset = output.Position;
            if (offset + 30 + nameBytes.Length + compressed.Length > uint.MaxValue)
                throw new IOException(Localization.T("The pack would be larger than 4 GB."));
            writer.Write(LocalSignature);
            Common(writer);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Flush();
            compressed.WriteTo(output);

            directory.Write(CentralSignature);
            directory.Write(VersionNeeded);
            Common(directory);
            // Extra, comment, disk, internal and external attributes.
            directory.Write((ushort)0);
            directory.Write((ushort)0);
            directory.Write((ushort)0);
            directory.Write((ushort)0);
            directory.Write(0u);
            directory.Write((uint)offset);
            directory.Write(nameBytes);

            // The fields shared by both headers, from "version needed" to the name length; UTF-8 names (bit 11).
            void Common(BinaryWriter header)
            {
                header.Write(VersionNeeded);
                header.Write((ushort)0x800);
                header.Write((ushort)Zstandard);
                header.Write(time);
                header.Write(date);
                header.Write(crc);
                header.Write((uint)compressed.Length);
                header.Write((uint)data.Length);
                header.Write((ushort)nameBytes.Length);
            }
        }
        directory.Flush();
        writer.Flush();
        long directoryOffset = output.Position;
        if (directoryOffset + central.Length > uint.MaxValue) throw new IOException(Localization.T("The pack would be larger than 4 GB."));
        central.WriteTo(output);
        writer.Write(EndSignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)entries.Count);
        writer.Write((ushort)entries.Count);
        writer.Write((uint)central.Length);
        writer.Write((uint)directoryOffset);
        writer.Write((ushort)0);
    }

    // APPNOTE 6.3 is the version that defines method 93.
    private const ushort VersionNeeded = 63;

    private static (ushort Time, ushort Date) DosTime(DateTime t) =>
        ((ushort)(t.Hour << 11 | t.Minute << 5 | t.Second / 2), (ushort)(Math.Max(t.Year - 1980, 0) << 9 | t.Month << 5 | t.Day));

    private static readonly uint[] s_crcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ c >> 1 : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = s_crcTable[(crc ^ b) & 0xFF] ^ crc >> 8;
        return ~crc;
    }

    private static int U16(byte[] data, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
    private static uint U32(byte[] data, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    private static ulong U64(byte[] data, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at));
#else
    // SharpCompress reads the central directory lazily, so its own exceptions can come from the enumeration.
    private static List<(string Name, int Method, bool Encrypted, long Compressed, long Size, long Offset)> ReadDirectory(string path)
    {
        try
        {
            using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(path, new SharpCompress.Readers.ReaderOptions());
            var entries = new List<(string, int, bool, long, long, long)>();
            foreach (var entry in archive.Entries)
            {
                if (entries.Count >= MaxEntries) throw new InvalidDataException(Localization.F("The pack has more than {0} entries.", MaxEntries));
                entries.Add((entry.Key ?? "", Method(entry.CompressionType), entry.IsEncrypted, entry.CompressedSize, entry.Size, 0));
            }
            return entries;
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or InvalidDataException))
        {
            throw new InvalidDataException(Localization.T("Not a zip file."), ex);
        }
    }

    private byte[] ReadEntry(Entry entry)
    {
        using var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(FullPath, new SharpCompress.Readers.ReaderOptions());
        var match = archive.Entries.FirstOrDefault(e => e.Key == entry.Name) ??
            throw new FileNotFoundException(Localization.F("{0} is not in the pack.", entry.Name));
        using var data = match.OpenEntryStream();
        return ReadExactly(data, entry.Size, entry.Name);
    }

    private static void WriteZip(Stream output, IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        if (entries.Count > MaxEntries) throw new IOException(Localization.F("The pack has more than {0} entries.", MaxEntries));
        var options = new SharpCompress.Writers.Zip.ZipWriterOptions(SharpCompress.Common.CompressionType.ZStandard)
        {
            LeaveStreamOpen = true,
            // Without this the names are written as UTF-8 but not flagged as such (bit 11).
            ArchiveEncoding = new SharpCompress.Common.ArchiveEncoding { Default = System.Text.Encoding.UTF8 }
        };
        using var writer = new SharpCompress.Writers.Zip.ZipWriter(output, options);
        var now = DateTime.Now;
        foreach (var (name, data) in entries)
        {
            using var source = new MemoryStream(data, writable: false);
            writer.Write(name, source, now);
        }
    }

    // The ZIP method numbers, so both builds report unsupported methods alike.
    private static int Method(SharpCompress.Common.CompressionType type) => type switch
    {
        SharpCompress.Common.CompressionType.None => Stored,
        SharpCompress.Common.CompressionType.Deflate => Deflate,
        SharpCompress.Common.CompressionType.ZStandard => Zstandard,
        SharpCompress.Common.CompressionType.Deflate64 => 9,
        SharpCompress.Common.CompressionType.BZip2 => 12,
        SharpCompress.Common.CompressionType.LZMA => 14,
        SharpCompress.Common.CompressionType.Xz => 95,
        SharpCompress.Common.CompressionType.PPMd => 98,
        _ => -1
    };
#endif
}
