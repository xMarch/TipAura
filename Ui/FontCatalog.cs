internal static class FontCatalog
{
    internal static bool IsFontFile(string path) =>
        Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ttc", StringComparison.OrdinalIgnoreCase);

    // ImGui aborts on a file that is not a font, so check the sfnt/collection signature first.
    internal static bool IsUsable(string path)
    {
        if (!IsFontFile(path) || !File.Exists(path)) return false;
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) < 4) return false;
            uint tag = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            return tag is 0x00010000 or 0x4F54544F /* OTTO */ or 0x74746366 /* ttcf */ or 0x74727565 /* true */;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
