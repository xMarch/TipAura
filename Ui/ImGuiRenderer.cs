using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal sealed class ImGuiRenderer : IDisposable
{
    // Precompiled from assets/shaders/imgui.hlsl, so the runtime never invokes the HLSL compiler.
    internal const string VertexShaderName = "imgui_vs", PixelShaderName = "imgui_ps",
        AlphaPixelShaderName = "imgui_ps_alpha";

    // ImDrawVert is two floats for position, two for UV, and four packed color bytes.
    private const uint VertexSize = 20;

    private static readonly Lazy<(byte[] Vertex, byte[] Pixel, byte[] AlphaPixel)> s_shaders = new(() => (
        LoadShader(VertexShaderName), LoadShader(PixelShaderName), LoadShader(AlphaPixelShaderName)));

    private readonly nint _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader, _alphaPixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depth;
    private readonly ID3D11SamplerState _linear, _point;
    private readonly GpuTexture _fontTexture = new(Format.R8_UNorm, alphaOnly: true);
    private ID3D11Buffer? _vertices, _indices;
    private int _vertexCapacity, _indexCapacity;
    private bool _frameStarted;
    private int _glyphVersion;
    private readonly Func<nint>? _glyphRanges;
    public ImFontPtr PlotLabelFont { get; private set; }
    public ImFontPtr IconFont { get; private set; }
    public (int Width, int Height) AtlasSize { get; private set; }
    public bool HasIconFont { get { unsafe { return IconFont.NativePtr != null; } } }
    public bool HasAllIcons
    {
        get
        {
            if (!HasIconFont) return false;
            unsafe
            {
                foreach (char icon in Lucide.Atlas)
                    if (IconFont.FindGlyphNoFallback(icon).NativePtr == null) return false;
            }
            return true;
        }
    }

    // Draws a Lucide icon and leaves the cursor on the same line for the following item.
    public void Icon(string icon)
    {
        if (!HasIconFont) return;
        ImGui.PushFont(IconFont);
        ImGui.TextUnformatted(icon);
        ImGui.PopFont();
        ImGui.SameLine();
    }

    // glyphRanges replaces the shared CJK ranges, e.g. a large alert font that only needs the encounter text.
    public ImGuiRenderer(int fontSizePx = 13, string? fontPath = null, int plotLabelSize = 0, Func<nint>? glyphRanges = null)
    {
        _glyphRanges = glyphRanges;
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGui.StyleColorsDark();

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        var device = Gpu.Device;
        var (vertex, pixel, alphaPixel) = s_shaders.Value;
        _vertexShader = device.CreateVertexShader(vertex);
        _pixelShader = device.CreatePixelShader(pixel);
        _alphaPixelShader = device.CreatePixelShader(alphaPixel);
        _inputLayout = device.CreateInputLayout([
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0)], vertex);
        _constants = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write));
        // A transparent OS window needs correct destination alpha as well as RGB blending.
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blend = device.CreateBlendState(blend);
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)
        {
            ScissorEnable = true, DepthClipEnable = true
        });
        _depth = device.CreateDepthStencilState(DepthStencilDescription.None);
        _linear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _point = device.CreateSamplerState(new SamplerDescription(Filter.MinLinearMagMipPoint, TextureAddressMode.Clamp));
        UpdateFont(fontSizePx, fontPath, plotLabelSize);
    }

    internal static byte[] LoadShader(string name)
    {
        using var stream = typeof(ImGuiRenderer).Assembly.GetManifestResourceStream($"tipaura.shaders.{name}.cso")
            ?? throw new InvalidOperationException($"Shader {name} is not embedded.");
        var bytecode = new byte[stream.Length];
        stream.ReadExactly(bytecode);
        return bytecode;
    }

    public bool UpdateFont(int fontSizePx, string? fontPath = null, int plotLabelSize = 0)
    {
        if (_frameStarted) throw new InvalidOperationException("Update the font between ImGui frames.");
        ImGui.SetCurrentContext(_context);
        _glyphVersion = CjkGlyphs.Version;
        var fonts = ImGui.GetIO().Fonts;
        fonts.Clear();
        PlotLabelFont = default;
        IconFont = default;
        bool customLoaded = false;
        if (fontSizePx <= 0) fonts.AddFontDefault();
        else
        {
            AddUiFont(fonts, fontSizePx, fontPath, Glyphs(), out customLoaded);
        }
        if (plotLabelSize > 0)
        {
            PlotLabelFont = AddUiFont(fonts, plotLabelSize, fontPath, Glyphs(), out _);
        }
        // Lucide icons live in the Private Use Area. Keep their font separate so a custom face
        // with its own PUA glyphs cannot override them, and pin the ranges until the atlas is baked.
        var icons = new ushort[Lucide.Atlas.Length * 2 + 1];
        for (int i = 0; i < Lucide.Atlas.Length; i++) icons[2 * i] = icons[2 * i + 1] = Lucide.Atlas[i];
        var pinned = GCHandle.Alloc(icons, GCHandleType.Pinned);
        try
        {
            unsafe
            {
                var (data, length) = LucideFontData();
                if (data != 0)
                {
                    var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
                    try
                    {
                        float size = fontSizePx > 0 ? fontSizePx : 13;
                        // The buffer is shared by every atlas for the process lifetime.
                        config.FontDataOwnedByAtlas = false;
                        config.PixelSnapH = true;
                        config.GlyphMinAdvanceX = size;
                        IconFont = fonts.AddFontFromMemoryTTF(data, length, size, config, pinned.AddrOfPinnedObject());
                    }
                    finally { config.Destroy(); }
                }
            }
            // An alpha-only atlas is a quarter of the RGBA size; the alpha pixel shader expands it.
            fonts.GetTexDataAsAlpha8(out nint pixels, out int width, out int height, out _);
            _fontTexture.Upload(width, height, pixels, width);
            fonts.SetTexID(_fontTexture.Id);
            AtlasSize = (width, height);
            // Every rebuild re-adds its fonts after Clear(), so the TTF copies are not needed after baking.
            fonts.ClearTexData();
            fonts.ClearInputData();
            AppLog.Info("UI", $"Font atlas {width}x{height} ({fonts.Fonts.Size} fonts, {fontSizePx}px).");
        }
        finally { pinned.Free(); }
        return fontPath is null || customLoaded;
    }

    private static readonly Lazy<(nint Data, int Length)> s_lucideFont = new(LoadLucideFont);

    internal static (nint Data, int Length) LucideFontData() => s_lucideFont.Value;

    // Loaded once into native memory and never freed: every renderer's atlas reads from it.
    private static unsafe (nint Data, int Length) LoadLucideFont()
    {
        using var stream = typeof(ImGuiRenderer).Assembly.GetManifestResourceStream("tipaura.lucide.ttf");
        if (stream is null) return (0, 0);
        int length = checked((int)stream.Length);
        void* data = NativeMemory.Alloc((nuint)length);
        stream.ReadExactly(new Span<byte>(data, length));
        return ((nint)data, length);
    }

    // True once loaded encounter text needs glyphs the current atlas lacks; rebuild between frames.
    public bool GlyphsStale => _glyphVersion != CjkGlyphs.Version;

    private nint Glyphs() => _glyphRanges?.Invoke() ?? CjkGlyphs.Ranges;

    private static unsafe ImFontPtr AddUiFont(ImFontAtlasPtr fonts, int size, string? customPath, nint glyphs, out bool customLoaded)
    {
        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        customLoaded = false;
        try
        {
            config.SizePixels = size;
            ImFontPtr face = default;
            if (customPath is not null && FontCatalog.IsUsable(customPath))
            {
                face = fonts.AddFontFromFileTTF(customPath, size, config, glyphs);
                customLoaded = face.NativePtr != null;
            }
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string consolas = Path.Combine(folder, "consola.ttf");
            if (face.NativePtr == null && FontCatalog.IsUsable(consolas))
                face = fonts.AddFontFromFileTTF(consolas, size, config, fonts.GetGlyphRangesDefault());
            config.MergeMode = face.NativePtr != null;
            var fallback = fonts.AddFontDefault(config);
            if (face.NativePtr == null) face = fallback;
            config.MergeMode = true;
            config.FontNo = 0;
            foreach (string name in new[] { "msjhbd.ttc", "msjh.ttc" })
            {
                string chinese = Path.Combine(folder, name);
                if (!FontCatalog.IsUsable(chinese)) continue;
                fonts.AddFontFromFileTTF(chinese, size, config, glyphs);
                break;
            }
            return face;
        }
        finally { config.Destroy(); }
    }

    public void BeginFrame(AppWindow window, float deltaTime, bool updateInput = true)
    {
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        var client = window.ClientSize;
        var framebuffer = window.FramebufferSize;
        io.DisplaySize = new Vector2(client.X, client.Y);
        io.DisplayFramebufferScale = new Vector2(
            client.X > 0 ? (float)framebuffer.X / client.X : 1f,
            client.Y > 0 ? (float)framebuffer.Y / client.Y : 1f);
        io.DeltaTime = MathF.Max(deltaTime, 1f / 1000f);

        if (updateInput) UpdateInput(window);
        ImGui.NewFrame();
        _frameStarted = true;
    }

    public void UpdateInput(AppWindow window)
    {
        SetCurrentContext();
        var io = ImGui.GetIO();
        var position = window.MousePosition;
        var scroll = window.ScrollDelta;
        io.AddMousePosEvent(position.X, position.Y);
        io.AddMouseButtonEvent(0, window.IsButtonDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, window.IsButtonDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, window.IsButtonDown(MouseButton.Middle));
        if (scroll.X != 0 || scroll.Y != 0)
            io.AddMouseWheelEvent(scroll.X, scroll.Y);
    }

    public void SetCurrentContext() => ImGui.SetCurrentContext(_context);

    public void ApplyTheme(int theme)
    {
        SetCurrentContext();
        if (theme == 0) ImGui.StyleColorsDark();
        else ImGui.StyleColorsLight();
    }

    // Draws into the render target bound by AppWindow.BeginRender.
    public void EndFrame(int framebufferWidth, int framebufferHeight)
    {
        if (!_frameStarted) return;
        _frameStarted = false;
        ImGui.Render();
        var drawData = ImGui.GetDrawData();
        if (framebufferWidth <= 0 || framebufferHeight <= 0 || drawData.TotalVtxCount == 0) return;

        var context = Gpu.Context;
        EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
        var vertexMap = context.Map(_vertices!, 0, MapMode.WriteDiscard);
        var indexMap = context.Map(_indices!, 0, MapMode.WriteDiscard);
        unsafe
        {
            byte* vertexTarget = (byte*)vertexMap.DataPointer, indexTarget = (byte*)indexMap.DataPointer;
            for (int i = 0; i < drawData.CmdListsCount; i++)
            {
                var list = drawData.CmdLists[i];
                long vertexBytes = list.VtxBuffer.Size * (long)VertexSize;
                long indexBytes = list.IdxBuffer.Size * (long)sizeof(ushort);
                Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vertexTarget, vertexBytes, vertexBytes);
                Buffer.MemoryCopy((void*)list.IdxBuffer.Data, indexTarget, indexBytes, indexBytes);
                vertexTarget += vertexBytes;
                indexTarget += indexBytes;
            }
        }
        context.Unmap(_vertices!, 0);
        context.Unmap(_indices!, 0);
        var constants = context.Map(_constants, 0, MapMode.WriteDiscard);
        unsafe
        {
            *(Vector4*)constants.DataPointer = new Vector4(drawData.DisplayPos.X, drawData.DisplayPos.Y,
                drawData.DisplaySize.X, drawData.DisplaySize.Y);
        }
        context.Unmap(_constants, 0);

        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _vertices!, VertexSize);
        context.IASetIndexBuffer(_indices!, Format.R16_UInt, 0);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.VSSetConstantBuffer(0, _constants);
        context.OMSetBlendState(_blend);
        context.OMSetDepthStencilState(_depth);
        context.RSSetState(_rasterizer);

        int vertexOffset = 0, indexOffset = 0;
        nint boundTexture = 0;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var list = drawData.CmdLists[i];
            for (int j = 0; j < list.CmdBuffer.Size; j++)
            {
                var command = list.CmdBuffer[j];
                if (command.UserCallback != nint.Zero)
                    throw new NotSupportedException("ImGui user draw callbacks are not supported.");

                var clip = command.ClipRect;
                int left = Math.Clamp((int)((clip.X - drawData.DisplayPos.X) * drawData.FramebufferScale.X), 0, framebufferWidth);
                int top = Math.Clamp((int)((clip.Y - drawData.DisplayPos.Y) * drawData.FramebufferScale.Y), 0, framebufferHeight);
                int right = Math.Clamp((int)((clip.Z - drawData.DisplayPos.X) * drawData.FramebufferScale.X), 0, framebufferWidth);
                int bottom = Math.Clamp((int)((clip.W - drawData.DisplayPos.Y) * drawData.FramebufferScale.Y), 0, framebufferHeight);
                if (right <= left || bottom <= top) continue;
                if (GpuTexture.Find(command.TextureId) is not { View: { } view } texture) continue;

                if (command.TextureId != boundTexture)
                {
                    context.PSSetShader(texture.AlphaOnly ? _alphaPixelShader : _pixelShader);
                    context.PSSetSampler(0, texture.PointSampling ? _point : _linear);
                    context.PSSetShaderResource(0, view);
                    boundTexture = command.TextureId;
                }
                context.RSSetScissorRect(left, top, right - left, bottom - top);
                context.DrawIndexed(command.ElemCount, (uint)(indexOffset + (int)command.IdxOffset),
                    vertexOffset + (int)command.VtxOffset);
            }
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += list.IdxBuffer.Size;
        }
        context.PSSetShaderResource(0, null!);
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (_vertices is null || vertexCount > _vertexCapacity)
        {
            _vertices?.Dispose();
            _vertexCapacity = vertexCount + 5000;
            _vertices = Gpu.Device.CreateBuffer(new BufferDescription((uint)_vertexCapacity * VertexSize,
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        if (_indices is null || indexCount > _indexCapacity)
        {
            _indices?.Dispose();
            _indexCapacity = indexCount + 10000;
            _indices = Gpu.Device.CreateBuffer(new BufferDescription((uint)_indexCapacity * sizeof(ushort),
                BindFlags.IndexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
    }

    public void Dispose()
    {
        _vertices?.Dispose();
        _indices?.Dispose();
        _fontTexture.Dispose();
        _linear.Dispose();
        _point.Dispose();
        _depth.Dispose();
        _rasterizer.Dispose();
        _blend.Dispose();
        _constants.Dispose();
        _inputLayout.Dispose();
        _alphaPixelShader.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        ImGui.DestroyContext(_context);
    }
}
