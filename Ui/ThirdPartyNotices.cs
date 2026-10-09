// Components distributed with TipAura binaries (or adapted into its source) and their embedded license texts.
// Resource names map to assets/licenses/*.txt, LICENSE and assets/fonts/LICENSE-lucide.txt (see TipAura.csproj).
// When a package is added or upgraded, update this table and the matching license file.
internal sealed class ThirdPartyNotice(string name, string version, string license, string url, string[] resources)
{
    private string? _text;
    internal string Name => name;
    internal string Version => version;
    internal string License => license;
    internal string Url => url;
    internal string[] Resources => resources;

    internal static ThirdPartyNotice[] All { get; } =
    [
        new("Dear ImGui (via cimgui)", "1.91.6", "MIT", "https://github.com/ocornut/imgui", ["dear-imgui", "cimgui"]),
        new("ImGui.NET", "1.91.6.1", "MIT", "https://github.com/ImGuiNET/ImGui.NET", ["imgui-net"]),
        new("Silk.NET (Core, GLFW, Input, Windowing, Maths)", "2.23.0", "MIT", "https://github.com/dotnet/Silk.NET", ["silk-net"]),
        new("GLFW", "3.4", "Zlib", "https://www.glfw.org", ["glfw"]),
        new("Vortice.Windows (Direct3D11, DXGI, DirectComposition, Direct2D1/WIC, MediaFoundation, XAudio2)", "3.8.3", "MIT",
            "https://github.com/amerkoleci/Vortice.Windows", ["vortice"]),
        new("Vortice.Mathematics", "2.1.0", "MIT", "https://github.com/amerkoleci/Vortice.Mathematics", ["vortice-math"]),
        new("SharpGen.Runtime", "2.4.2-beta", "MIT", "https://github.com/SharpGenTools/SharpGenTools", ["sharpgen"]),
        new("YamlDotNet", "18.1.0", "MIT", "https://github.com/aaubry/YamlDotNet", ["yamldotnet"]),
        new("NVorbis", "0.10.5", "MIT", "https://github.com/NVorbis/NVorbis", ["nvorbis"]),
#if NET11_0_OR_GREATER
        new(".NET Runtime and Libraries", "11.0", "MIT", "https://github.com/dotnet/runtime",
            ["dotnet-runtime", "dotnet-runtime-notices"]),
        new("Zstandard (zstd, part of the .NET 11 runtime)", "11.0", "BSD-3-Clause", "https://github.com/facebook/zstd", ["zstd"]),
#else
        new("SharpCompress", "0.50.4", "MIT", "https://github.com/adamhathcock/sharpcompress", ["sharpcompress"]),
        new("ZstdSharp (Zstandard port embedded in SharpCompress)", "0.50.4", "MIT; zstd: BSD-3-Clause", "https://github.com/oleg-st/ZstdSharp",
            ["zstdsharp", "zstd"]),
        new(".NET Runtime and Libraries", "10.0", "MIT", "https://github.com/dotnet/runtime",
            ["dotnet-runtime", "dotnet-runtime-notices"]),
#endif
        new("Lucide icons", "1.51.0", "ISC (some icons MIT)", "https://lucide.dev", ["lucide"]),
        new("yaaft (UI framework, global keyboard hook and logging adapted into TipAura)", "1.0.0", "MIT",
            "https://github.com/xMarch/yaaft", ["tipaura"]),
    ];

    internal static ThirdPartyNotice TipAura { get; } = new("TipAura", BuildInfo.Version, "MIT", BuildInfo.RepositoryUrl, ["tipaura"]);

    internal string Text => _text ??= string.Join("\n\n" + new string('-', 72) + "\n\n", Resources.Select(Load));

    private static string Load(string resource)
    {
        using var stream = typeof(ThirdPartyNotice).Assembly.GetManifestResourceStream($"tipaura.licenses.{resource}.txt")
            ?? throw new InvalidOperationException($"License {resource} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd();
    }
}
