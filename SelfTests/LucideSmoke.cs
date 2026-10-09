#if TIPAURA_AGENT_SELF_TEST
// The generated Lucide catalog, `lucide:` icon values in YAML and packs, and the glyph rasterizer
// (ImGui's font baker only; no device).
internal static class LucideSmoke
{
    internal static void Run()
    {
        Localization.Configure("en");
        try { Checks(); }
        finally { Localization.Configure(null); }
        Console.WriteLine("Lucide checks passed.");
    }

    private static void Checks()
    {
        var icons = Lucide.Icons;
        SelfTest.Check(icons.Length > 500, $"The Lucide catalog has only {icons.Length} icons.");
        for (int i = 1; i < icons.Length; i++)
            SelfTest.Check(string.CompareOrdinal(icons[i - 1].Name, icons[i].Name) < 0, $"Lucide.Icons is not sorted at {icons[i].Name}.");
        SelfTest.Check(icons.All(icon => icon.Categories != 0 && icon.Glyph is >= (char)0xE000 and <= (char)0xF8FF),
            "Every catalog icon needs a category and a Private Use Area glyph.");
        SelfTest.Check(icons.Select(icon => icon.Glyph).Distinct().Count() == icons.Length, "Two catalog icons share a glyph.");
        foreach (var (category, title) in Lucide.Categories)
            SelfTest.Check(icons.Any(icon => (icon.Categories & category) != 0), $"No icon in {title}.");

        SelfTest.Check(Lucide.Find("lucide:flame") is { Name: "flame" } && Lucide.Find(" LUCIDE:Flame ".Trim()) is { Name: "flame" } &&
            Lucide.Find("lucide:no-such-icon") is null && Lucide.Find("icons/fire.png") is null && Lucide.Find(null) is null,
            "Lucide.Find should match names case-insensitively and nothing else.");
        SelfTest.Check(Lucide.Find("lucide:accessibility") is { } first && first == icons[0] &&
            Lucide.Find("lucide:" + icons[^1].Name) == icons[^1], "Lucide.Find should reach both ends of the catalog.");

        var parsed = EncounterYaml.Parse("timers:\n  - {id: a, key: 1, duration: 5, icon: ' Lucide:Snowflake'}", "");
        SelfTest.Check(parsed.Issues.Count == 0 && parsed.Encounter!.Timers[0].Icon == "lucide:snowflake",
            "A Lucide icon should load without issues and be saved in lower case: " + string.Join("; ", parsed.Issues));
        SelfTest.Check(EncounterYaml.Serialize(parsed.Encounter!).Contains("icon: lucide:snowflake\n", StringComparison.Ordinal),
            "Serialize should write the Lucide icon as it is.");
        // Untagged, a Lucide icon means format 4 even with a root id; a format 3 file reads it too but needs an upgrade.
        SelfTest.Check(parsed is { Version: 4, Tagged: false } &&
            EncounterYaml.Parse("id: x\ntimers:\n  - {id: a, key: 1, duration: 5, icon: lucide:flame}", "").Version == 4 &&
            EncounterYaml.Parse("id: x\ntimers:\n  - {id: a, key: 1, duration: 5, icon: fire.png}", "").Version == 3,
            "An untagged file with a Lucide icon should be detected as format 4.");
        var v3 = EncounterYaml.Parse("version: 3\ntimers:\n  - {id: a, key: 1, duration: 5, icon: lucide:flame}", "");
        SelfTest.Check(v3 is { Version: 3, NeedsUpgrade: true } && v3.Issues.Count == 0 && v3.Encounter!.Timers[0].Icon == "lucide:flame",
            "A format 3 file should read lucide: as the icon and offer an upgrade: " + string.Join("; ", v3.Issues));
        var unknown = EncounterYaml.Parse("timers:\n  - {id: a, key: 1, duration: 5, icon: lucide:no-such-icon}", "").Encounter!;
        SelfTest.Check(unknown.Timers[0].Icon == "lucide:no-such-icon", "An unknown Lucide icon should be kept for the user to fix.");

        // A pack keeps the value and stores no entry for it; the loaded pack resolves it unchanged.
        string folder = Path.Combine(Path.GetTempPath(), $"tipaura-lucide-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var loose = parsed.Encounter! with { Id = "glyphs", BaseDirectory = folder };
            string zip = Path.Combine(folder, "glyphs.zip");
            SelfTest.Check(EncounterYaml.WritePack(loose, zip).Assets == 0, "A Lucide icon should not become a pack entry.");
            var pack = EncounterYaml.Load(zip);
            SelfTest.Check(pack.Issues.Count == 0 && pack.Encounter is { Pack: not null } packed &&
                packed.Timers[0].Icon == "lucide:snowflake" && packed.Resolve(packed.Timers[0].Icon) == "lucide:snowflake",
                "A pack should load its Lucide icon without issues: " + string.Join("; ", pack.Issues));
            var extraction = EncounterYaml.PlanExtraction(pack.Encounter!, Path.Combine(folder, "out"));
            SelfTest.Check(extraction.Files.Count == 1 && EncounterYaml.ApplyExtraction(extraction, new HashSet<string>()) == (1, true) &&
                EncounterYaml.Load(extraction.YamlPath).Encounter?.Timers[0].Icon == "lucide:snowflake", "Extracting a pack should leave the Lucide icon alone.");
        }
        finally { Directory.Delete(folder, true); }

        // Every glyph rasterizes; square (a rect from 3 to 21 on Lucide's 24-unit grid) lands centered.
        foreach (var icon in icons)
            SelfTest.Check(IconCache.RasterizeGlyph(icon.Glyph) is { } pixels && pixels.Any(p => p > 128), $"lucide:{icon.Name} rasterized empty.");
        var square = IconCache.RasterizeGlyph(Lucide.Find("lucide:square")!.Value.Glyph)!;
        const int Side = 128;
        int left = Side, right = -1, top = Side, bottom = -1;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
                if (square[y * Side + x] > 128)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        SelfTest.Check(Math.Abs(left - (Side - 1 - right)) <= 2 && Math.Abs(top - (Side - 1 - bottom)) <= 2 && right - left > Side * 2 / 3,
            $"lucide:square should be centered in its texture: x {left}..{right}, y {top}..{bottom}.");
        Console.WriteLine($"Lucide: {icons.Length} icons; square at x {left}..{right}, y {top}..{bottom}.");
    }
}
#endif
