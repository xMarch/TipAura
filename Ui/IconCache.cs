using ImGuiNET;
using Vortice.WIC;

// Timer icons decoded with WIC, scaled to at most MaxSide pixels and uploaded once. Every ImGui context
// shares the one D3D11 device, so a texture can be drawn by the main window and the overlays alike.
// A `lucide:<name>` icon is its glyph from the embedded Lucide font instead: an alpha-only, mipmapped
// MaxSide square that draws in the tint color (GpuTexture.AlphaOnly). UI thread only.
internal sealed class IconCache : IDisposable
{
    private const int MaxSide = 128;
    private readonly Dictionary<string, GpuTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    // Glyphs never change, so Clear keeps them.
    private readonly Dictionary<char, GpuTexture?> _glyphs = [];
    private IWICImagingFactory? _factory;

    // Null when the path (or pack asset key) is empty, missing or not a decodable image, or names no
    // catalog icon; failures are cached until Clear.
    internal GpuTexture? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Lucide.IsIcon(path)) return Lucide.Find(path) is { } icon ? Glyph(icon.Glyph) : null;
        if (_textures.TryGetValue(path, out var cached)) return cached;
        GpuTexture? texture = null;
        try
        {
            if (EncounterPack.IsKey(path) || File.Exists(path)) texture = Load(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn("UI", $"Could not load icon {Path.GetFileName(path)}: {ex.Message}");
        }
        _textures[path] = texture;
        return texture;
    }

    // Forgets every icon, so edited image files are read again.
    internal void Clear()
    {
        foreach (var texture in _textures.Values) texture?.Dispose();
        _textures.Clear();
    }

    private GpuTexture? Glyph(char glyph)
    {
        if (_glyphs.TryGetValue(glyph, out var cached)) return cached;
        GpuTexture? texture = null;
        try
        {
            if (RasterizeGlyph(glyph) is { } pixels)
            {
                texture = new GpuTexture(Vortice.DXGI.Format.R8_UNorm, alphaOnly: true);
                texture.UploadMipmapped(MaxSide, pixels);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("UI", $"Could not draw Lucide glyph U+{(int)glyph:X4}: {ex.Message}");
        }
        _glyphs[glyph] = texture;
        return texture;
    }

    // Bakes the glyph alone into a throwaway ImGui atlas at MaxSide px and copies its coverage into a
    // MaxSide square: the font's line box vertically, centered on its advance horizontally, so every icon
    // keeps Lucide's 24-unit grid. Null when the font or the glyph is missing.
    internal static unsafe byte[]? RasterizeGlyph(char glyph)
    {
        var (data, length) = ImGuiRenderer.LucideFontData();
        if (data == 0) return null;
        var atlas = new ImFontAtlasPtr(ImGuiNative.ImFontAtlas_ImFontAtlas());
        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        ushort* ranges = stackalloc ushort[] { glyph, glyph, 0 };
        try
        {
            atlas.Flags |= ImFontAtlasFlags.NoMouseCursors;
            config.FontDataOwnedByAtlas = false;
            // One atlas pixel per texture pixel (ImGui oversamples horizontally by default).
            config.OversampleH = config.OversampleV = 1;
            var font = atlas.AddFontFromMemoryTTF(data, length, MaxSide, config, (nint)ranges);
            atlas.GetTexDataAsAlpha8(out byte* atlasPixels, out int atlasWidth, out _);
            var found = font.FindGlyphNoFallback(glyph);
            if (found.NativePtr == null) return null;
            // ImGui.NET's ImFontGlyph wrapper predates the Colored/Visible/Codepoint bit fields sharing one
            // uint, so its float offsets are wrong for this cimgui; read the native struct directly:
            // uint bits; float AdvanceX, X0, Y0, X1, Y1, U0, V0, U1, V1.
            float* metrics = (float*)found.NativePtr;
            float advance = metrics[1], x0 = metrics[2], y0 = metrics[3], x1 = metrics[4], y1 = metrics[5];
            int u0 = (int)MathF.Round(metrics[6] * atlasWidth), v0 = (int)MathF.Round(metrics[7] * atlas.TexHeight);
            int width = (int)MathF.Round(x1 - x0), height = (int)MathF.Round(y1 - y0);
            int left = (int)MathF.Round(x0 + (MaxSide - advance) / 2), top = (int)MathF.Round(y0);
            var pixels = new byte[MaxSide * MaxSide];
            for (int y = 0; y < height; y++)
            {
                int row = top + y;
                if (row is < 0 or >= MaxSide) continue;
                for (int x = 0; x < width; x++)
                {
                    int column = left + x;
                    if (column is >= 0 and < MaxSide) pixels[row * MaxSide + column] = atlasPixels[(v0 + y) * atlasWidth + u0 + x];
                }
            }
            return pixels;
        }
        finally
        {
            config.Destroy();
            atlas.Destroy();
        }
    }

    private GpuTexture Load(string path)
    {
        _factory ??= new IWICImagingFactory();
        using var stream = EncounterPack.IsKey(path) ? _factory.CreateStream(EncounterPack.ReadAsset(path)) : null;
        using var decoder = stream is not null ? _factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad)
            : _factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        float scale = Math.Min(1f, MaxSide / (float)Math.Max(size.Width, size.Height));
        int width = Math.Max(1, (int)(size.Width * scale)), height = Math.Max(1, (int)(size.Height * scale));
        using var scaler = _factory.CreateBitmapScaler();
        scaler.Initialize(frame, (uint)width, (uint)height, BitmapInterpolationMode.HighQualityCubic);
        using var converter = _factory.CreateFormatConverter();
        // Straight alpha: the ImGui shader multiplies by vertex color and blends with SourceAlpha.
        converter.Initialize(scaler, PixelFormat.Format32bppRGBA).CheckError();
        var pixels = new byte[width * height * 4];
        converter.CopyPixels((uint)width * 4, pixels);
        var texture = new GpuTexture(Vortice.DXGI.Format.R8G8B8A8_UNorm);
        texture.Upload(width, height, pixels);
        return texture;
    }

#if TIPAURA_AGENT_SELF_TEST
    internal int GlyphCountForSmoke => _glyphs.Count;
#endif

    public void Dispose()
    {
        Clear();
        foreach (var texture in _glyphs.Values) texture?.Dispose();
        _glyphs.Clear();
        _factory?.Dispose();
    }
}
