#if TIPAURA_AGENT_SELF_TEST
using Vortice.D3DCompiler;

// Regenerates and verifies the precompiled ImGui shader bytecode embedded by ImGuiRenderer.
internal static class ShaderTool
{
    private static readonly (string Name, string Entry, string Profile)[] Shaders =
    [
        (ImGuiRenderer.VertexShaderName, "VertexMain", "vs_4_0"),
        (ImGuiRenderer.PixelShaderName, "PixelMain", "ps_4_0"),
        (ImGuiRenderer.AlphaPixelShaderName, "AlphaPixelMain", "ps_4_0"),
    ];

    internal static void Dump(string directory)
    {
        string source = Source();
        Directory.CreateDirectory(directory);
        foreach (var shader in Shaders)
        {
            string path = Path.Combine(directory, shader.Name + ".cso");
            File.WriteAllBytes(path, Compile(source, shader.Entry, shader.Profile));
            Console.WriteLine($"Wrote {path}");
        }
    }

    internal static void Verify()
    {
        string source = Source();
        foreach (var shader in Shaders)
            if (!ImGuiRenderer.LoadShader(shader.Name).AsSpan().SequenceEqual(Compile(source, shader.Entry, shader.Profile)))
                throw new InvalidOperationException(
                    $"Embedded {shader.Name}.cso is stale; regenerate it with --dump-shaders assets/shaders.");
        Console.WriteLine("Embedded ImGui shader bytecode matches imgui.hlsl.");
    }

    private static byte[] Compile(string source, string entry, string profile) =>
        Compiler.Compile(source, entry, "imgui.hlsl", profile).ToArray();

    private static string Source()
    {
        using var stream = typeof(ShaderTool).Assembly.GetManifestResourceStream("tipaura.shaders.imgui.hlsl")
            ?? throw new InvalidOperationException("The ImGui shader source is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
#endif
