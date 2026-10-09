#if TIPAURA_AGENT_SELF_TEST
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Encounter packs and recursive discovery. fixtures/demo.zip was written by SharpCompress with Zstandard
// entries, so the .NET 11 reader is checked against another implementation; the damaged packs are
// written by hand below.
internal static class PackSmoke
{
    private const string Index = "version: 3\nid: {0}\nname: p\ntimers:\n  - {{id: a, key: 1, duration: 5, sound: {{sfx: {1}}}, icon: {2}}}\n";

    internal static void Run()
    {
        // The checks below match English messages.
        Localization.Configure("en");
        try { Checks(); }
        finally { Localization.Configure(null); }
        Console.WriteLine("Pack checks passed.");
    }

    private static void Checks()
    {
        string fixture = SelfTest.ProjectPath("SelfTests/fixtures/demo.zip");
        var demo = EncounterYaml.Load(fixture);
        SelfTest.Check(demo is { Encounter: { Id: "demo", Pack: not null, Timers.Count: 2 }, Version: 3, NeedsUpgrade: true } && demo.Issues.Count == 0,
            "demo.zip should load without issues: " + string.Join("; ", demo.Issues));
        var encounter = demo.Encounter!;
        SelfTest.Check(BinaryPrimitives.ReadUInt16LittleEndian(File.ReadAllBytes(fixture).AsSpan(8)) == 93, "demo.zip should use Zstandard entries.");
        foreach (var (relative, original) in new[] { ("sfx/chime.wav", "data/sfx/chime.wav"), ("SFX/Chime.ogg", "SelfTests/fixtures/chime.ogg"),
            ("./icons/../icons/fire.png", "data/icons/fire.png") })
        {
            string? key = encounter.Resolve(relative);
            SelfTest.Check(key is not null && EncounterPack.IsKey(key) &&
                EncounterPack.ReadAsset(key).AsSpan().SequenceEqual(File.ReadAllBytes(SelfTest.ProjectPath(original))),
                $"{relative} should read back from the pack as {original}.");
        }
        SelfTest.Check(encounter.Resolve("../example.yaml") is null && encounter.Resolve("C:/x.wav") is null && encounter.Resolve("/x.wav") is null,
            "Paths leaving the pack should not resolve.");
        var wav = AudioDecoders.DecodeFile(encounter.Resolve("sfx/chime.wav")!);
        var ogg = AudioDecoders.DecodeFile(encounter.Resolve("sfx/chime.ogg")!);
        SelfTest.Check(wav.Seconds > 0.1 && ogg.Seconds > 0.1, "Pack sounds should decode.");
        SelfTest.Check(EncounterYaml.Parse("id: x\ntimers: []", "") is { Version: 3, Tagged: false, Encounter.Id: "x" }, "A root id means format 3.");
        SelfTest.Check(EncounterYaml.Serialize(encounter).StartsWith($"version: {EncounterYaml.FormatVersion}\nid: demo\n", StringComparison.Ordinal), "Serialize should write id after version.");

        string folder = Path.Combine(Path.GetTempPath(), $"tipaura-pack-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            Damaged(folder);
            Extraction(folder, encounter);
            Adding(folder, encounter);
            Packing(folder, encounter);
            Listing(folder);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void Damaged(string folder)
    {
        byte[] chime = File.ReadAllBytes(SelfTest.ProjectPath("data/sfx/chime.wav"));
        byte[] icon = File.ReadAllBytes(SelfTest.ProjectPath("data/icons/fire.png"));

        // Hand-written packs: every method this reader supports, then one fault each.
        var good = Write(folder, "good", [Text("index.yaml", string.Format(Index, "good", "sfx/chime.wav", "../outside.png"), 8),
            new("sfx/chime.wav", chime, Method: StoredOrZstd), new("icons/icon.png", icon), new("sfx/", [])]);
        var loaded = EncounterYaml.Load(good);
        SelfTest.Check(loaded is { Encounter.Id: "good" } && !loaded.HasErrors && loaded.Issues.Single().Message.Contains("inside the pack", StringComparison.Ordinal),
            "A hand-written pack should load and warn about an icon outside it: " + string.Join("; ", loaded.Issues));
        SelfTest.Check(EncounterPack.ReadAsset(loaded.Encounter!.Resolve("sfx/chime.wav")!).AsSpan().SequenceEqual(chime), "Entries should read back unchanged.");
        SelfTest.Check(string.Join(",", loaded.Encounter.PackAssets(EncounterPack.SoundFolder).Concat(loaded.Encounter.PackAssets(EncounterPack.IconFolder))) ==
            "sfx/chime.wav,icons/icon.png", "PackAssets should list each folder.");
        var wrong = EncounterYaml.Load(Write(folder, "wrong", [Text("index.yaml", string.Format(Index, "wrong", "icons/icon.png", "sfx/chime.png")),
            new("sfx/chime.wav", chime), new("icons/icon.png", icon)]));
        SelfTest.Check(wrong is { HasErrors: false } && wrong.Issues.Count == 2 && wrong.Issues.Any(i => i.Message.Contains("unsupported file type", StringComparison.Ordinal)) &&
            wrong.Issues.Any(i => i.Message.Contains("directly in the pack's icons/ folder", StringComparison.Ordinal)),
            "Assets in the wrong folder should warn: " + string.Join("; ", wrong.Issues));

        Expect(Write(folder, "mismatch", [Text("index.yaml", string.Format(Index, "other", "x.wav", "x.png"))]), "does not match");
        Expect(Write(folder, "noid", [Text("index.yaml", "version: 3\nname: n\ntimers: []\n")]), "must declare id");
        Expect(Write(folder, "noindex", [new("sfx/x.wav", [1])]), "has no index.yaml");
        foreach (string stray in new[] { "a/chime.wav", "chime.wav", "sfx/sub/chime.wav", "icons/chime.wav", "sfx/icon.png", "readme.txt" })
            Expect(Write(folder, "stray", [Text("index.yaml", string.Format(Index, "stray", "x.wav", "x.png")), new(stray, chime)]), "is not allowed");
        Expect(Write(folder, "escape", [Text("index.yaml", string.Format(Index, "escape", "x.wav", "x.png")), new("../x.wav", chime)]), "invalid name");
        Expect(Write(folder, "backslash", [Text("index.yaml", string.Format(Index, "backslash", "x.wav", "x.png")), new("sfx\\x.wav", chime)]), "invalid name");
        Expect(Write(folder, "twice", [Text("index.yaml", string.Format(Index, "twice", "x.wav", "x.png")), new("sfx/a.wav", chime), new("SFX/A.wav", chime)]), "appears twice");
        Expect(Write(folder, "locked", [Text("index.yaml", string.Format(Index, "locked", "x.wav", "x.png")), new("sfx/x.wav", chime, Flags: 1)]), "encrypted");
        Expect(Write(folder, "lzma", [Text("index.yaml", string.Format(Index, "lzma", "x.wav", "x.png")), new("sfx/x.wav", chime, Method: 14)]), "compression method 14");
        Expect(Write(folder, "short", [Text("index.yaml", string.Format(Index, "short", "x.wav", "x.png"), declaredExtra: 10)]), "Could not read pack");
        File.WriteAllText(Path.Combine(folder, "notzip.zip"), "not a zip");
        Expect(Path.Combine(folder, "notzip.zip"), "Could not read pack");

        static void Expect(string path, string message)
        {
            var result = EncounterYaml.Load(path);
            SelfTest.Check(result.Encounter is null && result.Issues.Any(i => i.IsError && i.Message.Contains(message, StringComparison.Ordinal)),
                $"{Path.GetFileName(path)} should fail with '{message}': " + string.Join("; ", result.Issues));
        }
    }

    // Extracting writes every used asset with the pack's name as prefix and <pack>.yaml into the data folder;
    // existing files with the same bytes are skipped, others are replaced only when chosen.
    private static void Extraction(string folder, Encounter pack)
    {
        string data = Path.Combine(folder, "extract");
        var plan = EncounterYaml.PlanExtraction(pack, data);
        string labels = string.Join(",", plan.Files.Select(f => f.Label).Order(StringComparer.Ordinal));
        SelfTest.Check(labels == "demo.yaml,icons/demo_fire.png,sfx/demo_chime.mp3,sfx/demo_chime.ogg,sfx/demo_chime.wav" &&
            plan.Files[^1].Target == plan.YamlPath && plan.YamlPath == Path.Combine(data, "demo.yaml") && !plan.Files.Any(f => f.Exists),
            "Unexpected extraction plan: " + labels);
        var empty = new HashSet<string>();
        SelfTest.Check(EncounterYaml.ApplyExtraction(plan, empty) == (5, true), "The first extraction should write every file.");
        var back = EncounterYaml.Load(plan.YamlPath);
        SelfTest.Check(back is { Encounter: { Id: "demo", Pack: null } loose, Issues.Count: 0 } &&
            loose.Timers.SelectMany(t => t.Sounds.Select(s => s.Sfx).Append(t.Icon)).OfType<string>().All(p => p.Contains("/demo_", StringComparison.Ordinal)) &&
            File.ReadAllBytes(Path.Combine(data, "icons", "demo_fire.png")).AsSpan().SequenceEqual(EncounterPack.ReadAsset(pack.Resolve("icons/fire.png")!)),
            "The extracted file should load with prefixed paths: " + string.Join("; ", back.Issues));

        var again = EncounterYaml.PlanExtraction(pack, data);
        SelfTest.Check(again.Files.All(f => f.Exists && !f.Conflict) && EncounterYaml.ApplyExtraction(again, empty) == (0, true),
            "Extracting again over identical files should write nothing.");
        string icon = Path.Combine(data, "icons", "demo_fire.png");
        File.WriteAllText(icon, "changed");
        File.AppendAllText(plan.YamlPath, "# edited\n");
        var conflicts = EncounterYaml.PlanExtraction(pack, data);
        SelfTest.Check(string.Join(",", conflicts.Files.Where(f => f.Conflict).Select(f => f.Label)) == "icons/demo_fire.png,demo.yaml",
            "Changed files should be conflicts.");
        SelfTest.Check(EncounterYaml.ApplyExtraction(conflicts, empty) == (0, false) && File.ReadAllText(icon) == "changed",
            "Keeping every conflict should write nothing and keep the existing YAML.");
        SelfTest.Check(EncounterYaml.ApplyExtraction(conflicts, new HashSet<string> { "icons/demo_fire.png", "demo.yaml" }) == (2, true) &&
            File.ReadAllBytes(icon).AsSpan().SequenceEqual(EncounterPack.ReadAsset(pack.Resolve("icons/fire.png")!)) &&
            !File.ReadAllText(plan.YamlPath).Contains("# edited", StringComparison.Ordinal), "Overwriting should replace the chosen files.");
    }

    // Files added in the editor: named after the source, numbered when the pack has other bytes under that
    // name, reused when it has the same; written into the zip by Export pack, which leaves unused files out.
    private static void Adding(string folder, Encounter demo)
    {
        string stereo = SelfTest.ProjectPath("SelfTests/fixtures/chime-stereo.m4a");
        var (added, entry) = EncounterYaml.AddPackFile(demo, EncounterPack.SoundFolder, stereo);
        SelfTest.Check(entry == "sfx/chime-stereo.m4a" && added.PackFiles.Count == 1 && added.Resolve(entry) == Path.GetFullPath(stereo) &&
            added.PackAssets(EncounterPack.SoundFolder).Contains(entry), "An added file should resolve to its source.");
        var (same, existing) = EncounterYaml.AddPackFile(added, EncounterPack.SoundFolder, SelfTest.ProjectPath("data/sfx/chime.wav"));
        SelfTest.Check(existing == "sfx/chime.wav" && same.PackFiles.Count == 1, $"The same bytes should reuse the pack's entry: {existing}");
        string other = Path.Combine(folder, "chime.wav");
        File.WriteAllBytes(other, File.ReadAllBytes(stereo));
        var (renamed, numbered) = EncounterYaml.AddPackFile(added, EncounterPack.SoundFolder, other);
        SelfTest.Check(numbered == "sfx/chime-2.wav" && renamed.PackFiles.Count == 2, $"Other bytes under a used name should be numbered: {numbered}");
        try
        {
            EncounterYaml.AddPackFile(demo, EncounterPack.IconFolder, stereo);
            SelfTest.Check(false, "A sound should not be added as an icon.");
        }
        catch (InvalidDataException ex) { SelfTest.Check(ex.Message.Contains("unsupported file type", StringComparison.Ordinal), ex.Message); }

        // Only the added sound and the first timer's icon stay in use; the other pack files are left out,
        // and so is the unused added chime-2.wav.
        var edited = renamed with
        {
            Timers = [renamed.Timers[0] with { Sounds = new([new TimerSound { Sfx = entry }]) },
                .. renamed.Timers.Skip(1).Select(t => t with { Sounds = ValueList<TimerSound>.Empty, Icon = null })]
        };
        string zip = Path.Combine(folder, "added", "demo.zip");
        var (assets, trimmed) = EncounterYaml.WritePack(edited, zip);
        int icons = edited.Timers[0].Icon is null ? 0 : 1;
        var packed = EncounterYaml.Load(zip);
        SelfTest.Check(packed is { HasErrors: false, Issues.Count: 0, Encounter.Pack: { } written } && assets == 1 + icons &&
            trimmed == 4 - icons + 1 && written.Names.Count() == assets + 1 &&
            EncounterPack.ReadAsset(packed.Encounter.Resolve("sfx/chime-stereo.m4a")!).AsSpan().SequenceEqual(File.ReadAllBytes(stereo)),
            $"Export pack wrote {assets} asset(s) and left out {trimmed}: " + string.Join("; ", packed.Issues));
    }

    // Export pack...: assets inside the YAML's folder keep their path, others move to sfx/ or icons/
    // (numbered when the name is taken by other bytes), and the pack loads back with the same bytes.
    private static void Packing(string folder, Encounter demo)
    {
        string source = Path.Combine(folder, "loose"), outside = Path.Combine(folder, "outside");
        Directory.CreateDirectory(Path.Combine(source, "音效"));
        Directory.CreateDirectory(Path.Combine(outside, "b"));
        byte[] wav = File.ReadAllBytes(SelfTest.ProjectPath("data/sfx/chime.wav"));
        byte[] ogg = File.ReadAllBytes(SelfTest.ProjectPath("SelfTests/fixtures/chime.ogg"));
        byte[] png = File.ReadAllBytes(SelfTest.ProjectPath("data/icons/fire.png"));
        File.WriteAllBytes(Path.Combine(source, "音效", "提示.wav"), wav);
        File.WriteAllBytes(Path.Combine(outside, "chime.ogg"), ogg);
        File.WriteAllBytes(Path.Combine(outside, "b", "chime.ogg"), wav);
        File.WriteAllBytes(Path.Combine(outside, "fire.png"), png);
        string yaml = Path.Combine(source, "loose.yaml");
        File.WriteAllText(yaml, "version: 3\nid: loose\nname: l\ntimers:\n" +
            $"  - {{id: a, key: 1, duration: 5, sound: [{{sfx: 音效/提示.wav}}, {{sfx: ../outside/chime.ogg}}], icon: '{Path.Combine(outside, "fire.png")}'}}\n" +
            "  - {id: b, key: 2, duration: 5, sound: {sfx: ../outside/b/chime.ogg}, icon: ../outside/fire.png}\n");
        var loaded = EncounterYaml.Load(yaml);
        SelfTest.Check(loaded is { HasErrors: false, Issues.Count: 0 }, "The loose source should load: " + string.Join("; ", loaded.Issues));
        var loose = loaded.Encounter!;

        string zip = Path.Combine(folder, "pack", "loose.zip");
        var (assets, trimmed) = EncounterYaml.WritePack(loose, zip);
        var packed = EncounterYaml.Load(zip);
        string paths = string.Join(",", packed.Encounter?.Timers.SelectMany(t => t.Sounds.Select(s => s.Sfx).Append(t.Icon)) ?? []);
        SelfTest.Check(assets == 4 && trimmed == 0 && packed is { Encounter.Id: "loose", HasErrors: false } && packed.Issues.Count == 0 &&
            paths == "sfx/提示.wav,sfx/chime.ogg,icons/fire.png,sfx/chime-2.ogg,icons/fire.png",
            $"Export pack wrote {assets} asset(s) as {paths}: " + string.Join("; ", packed.Issues));
        foreach (var (relative, data) in new[] { ("sfx/提示.wav", wav), ("sfx/chime.ogg", ogg), ("sfx/chime-2.ogg", wav), ("icons/fire.png", png) })
            SelfTest.Check(EncounterPack.ReadAsset(packed.Encounter!.Resolve(relative)!).AsSpan().SequenceEqual(data), $"{relative} should read back unchanged.");
        byte[] bytes = File.ReadAllBytes(zip);
        SelfTest.Check(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) == 93 && (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) & 0x800) != 0,
            "Written packs should use Zstandard and UTF-8 names.");
        SelfTest.Check(loose.Timers[0].Icon == Path.Combine(outside, "fire.png"), "Export pack should not change the open encounter.");

        // A pack exported again elsewhere keeps its entries.
        string copy = Path.Combine(folder, "copy", "demo.zip");
        SelfTest.Check(EncounterYaml.WritePack(demo, copy) == (4, 0) && EncounterYaml.Load(copy) is { HasErrors: false, Issues.Count: 0, Encounter: { } again } &&
            EncounterPack.ReadAsset(again.Resolve("sfx/chime.mp3")!).AsSpan().SequenceEqual(EncounterPack.ReadAsset(demo.Resolve("sfx/chime.mp3")!)),
            "A pack should export to another pack unchanged.");

        Fails(() => EncounterYaml.WritePack(loose with { Id = null }, zip), "Set an id first");
        Fails(() => EncounterYaml.WritePack(loose, Path.Combine(folder, "pack", "other.zip")), "must be named loose.zip");
        Fails(() => EncounterYaml.WritePack(loose with { Id = "a/b" }, zip), "cannot be used as a file name");
        var broken = loose with { Timers = [.. loose.Timers, loose.Timers[1] with { Id = "c", Icon = "nothere.png" }] };
        Fails(() => EncounterYaml.WritePack(broken, zip), "nothere.png");
        var mistyped = loose with { Timers = [loose.Timers[0] with { Icon = "音效/提示.wav" }] };
        Fails(() => EncounterYaml.WritePack(mistyped, zip), "unsupported file type");
        SelfTest.Check(File.ReadAllBytes(zip).AsSpan().SequenceEqual(bytes), "A failed pack export should leave the existing zip alone.");

        static void Fails(Action action, string message)
        {
            try
            {
                action();
                SelfTest.Check(false, $"Export pack should fail with '{message}'.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                SelfTest.Check(ex.Message.Contains(message, StringComparison.Ordinal), ex.Message);
            }
        }
    }

    // Loose YAML at any depth outside pack/, then zips directly in pack/.
    private static void Listing(string folder)
    {
        string data = Path.Combine(folder, "data");
        foreach (string file in new[] { "b.yaml", "a/deep/c.yml", "a/old.yaml.v1.bak", "pack/inner.yaml", "pack/x.zip", "pack/sub/y.zip", "notes.txt" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(data, file))!);
            File.WriteAllText(Path.Combine(data, file), "");
        }
        var listed = EncounterYaml.ListDataFiles(data);
        string labels = string.Join(",", listed.Select(f => f.Label + (f.IsPack ? "*" : "")));
        SelfTest.Check(labels == "a/deep/c.yml,b.yaml,pack/x.zip*", "Unexpected listing: " + labels);
    }

#if NET11_0_OR_GREATER
    private const int StoredOrZstd = 93;
#else
    private const int StoredOrZstd = 0;
#endif

    private sealed record Entry(string Name, byte[] Data, int Method = 0, int Flags = 0, int DeclaredExtra = 0);

    private static Entry Text(string name, string text, int method = 0, int declaredExtra = 0) =>
        new(name, Encoding.UTF8.GetBytes(text), method, 0, declaredExtra);

    // A minimal zip: local headers, central directory and end record. Method 8 and (on .NET 11) 93 are
    // compressed; any other method number stores the bytes as they are. DeclaredExtra overstates the size.
    private static string Write(string folder, string id, Entry[] entries)
    {
        string path = Path.Combine(folder, id + EncounterPack.Extension);
        using var zip = new MemoryStream();
        using var central = new MemoryStream();
        foreach (var entry in entries)
        {
            byte[] data = entry.Method switch
            {
                8 => Compress(s => new DeflateStream(s, CompressionLevel.Optimal), entry.Data),
#if NET11_0_OR_GREATER
                93 => Compress(s => new ZstandardStream(s, CompressionMode.Compress), entry.Data),
#endif
                _ => entry.Data
            };
            byte[] name = Encoding.UTF8.GetBytes(entry.Name);
            long offset = zip.Position;
            uint crc = Crc32(entry.Data);
            Header(zip, 0x04034b50, local: true);
            zip.Write(name);
            zip.Write(data);
            Header(central, 0x02014b50, local: false);
            central.Write(name);

            void Header(Stream s, uint signature, bool local)
            {
                var h = new byte[local ? 30 : 46];
                BinaryPrimitives.WriteUInt32LittleEndian(h, signature);
                int at = local ? 4 : 6;
                if (!local) BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), 63);
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(at), 63);
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(at + 2), (ushort)(entry.Flags | 0x800));
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(at + 4), (ushort)entry.Method);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 10), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 14), (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at + 18), (uint)(entry.Data.Length + entry.DeclaredExtra));
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(at + 22), (ushort)name.Length);
                if (!local) BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(42), (uint)offset);
                s.Write(h);
            }
        }
        long directoryOffset = zip.Position;
        central.Position = 0;
        central.CopyTo(zip);
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)directoryOffset);
        zip.Write(end);
        File.WriteAllBytes(path, zip.ToArray());
        return path;

        static byte[] Compress(Func<Stream, Stream> create, byte[] data)
        {
            using var output = new MemoryStream();
            using (var compressor = create(output)) compressor.Write(data);
            return output.ToArray();
        }
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? crc >> 1 ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }
}
#endif
