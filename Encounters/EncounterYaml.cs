using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

// Version is the format the file was read as and Tagged whether it declared it; files that failed to parse
// report the current format so they are not offered an upgrade.
internal sealed record EncounterLoadResult(Encounter? Encounter, IReadOnlyList<EncounterIssue> Issues,
    int Version = EncounterYaml.FormatVersion, bool Tagged = true)
{
    internal bool HasErrors => Issues.Any(issue => issue.IsError);
    internal bool NeedsUpgrade => Encounter is not null && (!Tagged || Version < EncounterYaml.FormatVersion);
}

// Reads and writes encounter files through YamlDotNet's node tree rather than its reflection-based
// serializer: no runtime code generation under Native AOT, and every node keeps its source line.
internal static class EncounterYaml
{
    internal const string Extension = ".yaml";
    // Format versions are specified in TECHNICAL.md ("Format versions and upgrade").
    internal const int FormatVersion = 4;
    internal static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    private static readonly string[] RootKeys = ["version", "id", "name", "timers"];
    private static readonly string[] TimerKeys =
        ["id", "name", "key", "duration", "repeat", "max_instances", "on_limit", "warn_before", "sound", "icon", "message", "color",
            "provider", "note"];
    private static readonly string[] SoundKeys = ["tts", "sfx", "offset"];
    internal static readonly string[] SoundExtensions = [".wav", ".ogg", ".mp3", ".m4a", ".aac", ".wma"];
    internal static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    // Every .yaml/.yml below data/ (except data/pack/), then the zips directly in data/pack/. A fixed recursive
    // enumeration is enough for this; no globbing package is needed.
    internal static IReadOnlyList<EncounterSource> ListDataFiles(string? directory = null)
    {
        directory = Path.GetFullPath(directory ?? DataDirectory);
        if (!Directory.Exists(directory)) return [];
        string packs = Path.Combine(directory, EncounterPack.DirectoryName);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true };
        var files = Directory.EnumerateFiles(directory, "*", options)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml" &&
                !path.StartsWith(packs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(path => new EncounterSource(path, Label(path), false))
            .OrderBy(source => source.Label, StringComparer.CurrentCultureIgnoreCase);
        var zips = Directory.Exists(packs)
            ? Directory.EnumerateFiles(packs, "*" + EncounterPack.Extension, new EnumerationOptions { IgnoreInaccessible = true })
                .Select(path => new EncounterSource(path, Label(path), true))
                .OrderBy(source => source.Label, StringComparer.CurrentCultureIgnoreCase)
            : Enumerable.Empty<EncounterSource>();
        return [.. files, .. zips];

        string Label(string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    }

    internal static EncounterLoadResult Load(string path)
    {
        if (EncounterPack.IsPackPath(path)) return LoadPack(path);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(null, [new(true, 0, Localization.F("Could not read file: {0}", ex.Message))]);
        }
        return Parse(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? "",
            Path.GetFileNameWithoutExtension(path));
    }

    // A pack loads only when index.yaml exists and declares the zip's file name as its id; its assets
    // are read from the zip and nothing outside it.
    internal static EncounterLoadResult LoadPack(string path)
    {
        EncounterPack pack;
        string text;
        try
        {
            pack = EncounterPack.Open(path);
            if (!pack.Contains(EncounterPack.IndexName))
                return new(null, [new(true, 0, Localization.F("The pack has no {0}.", EncounterPack.IndexName))]);
            text = DecodeText(pack.Read(EncounterPack.IndexName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException)
        {
            return new(null, [new(true, 0, Localization.F("Could not read pack: {0}", ex.Message))]);
        }
        var result = Parse(text, Path.GetDirectoryName(pack.FullPath) ?? "", pack.FileId, pack);
        if (result.Encounter is { } encounter && !string.Equals(encounter.Id, pack.FileId, StringComparison.OrdinalIgnoreCase))
            return result with
            {
                Encounter = null,
                Issues = [new(true, 0, encounter.Id is null
                    ? Localization.F("{0} must declare id: {1}, the pack's file name.", EncounterPack.IndexName, pack.FileId)
                    : Localization.F("The pack's id '{0}' does not match its file name '{1}'.", encounter.Id, pack.FileId)), .. result.Issues]
            };
        return result;
    }

    // UTF-8 with or without BOM, like File.ReadAllText; UTF-16 files carry a BOM.
    private static string DecodeText(byte[] data)
    {
        using var reader = new StreamReader(new MemoryStream(data), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static EncounterLoadResult Parse(string text, string baseDirectory, string fallbackName = "", EncounterPack? pack = null)
    {
        var issues = new List<EncounterIssue>();
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlException ex)
        {
            return new(null, [new(true, (int)ex.Start.Line, Localization.F("YAML syntax error: {0}", ex.Message))]);
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return new(null, [new(true, 0, Localization.T("The file must be a mapping with name and timers."))]);

        var context = new Context(issues, baseDirectory, pack);
        context.UnknownKeys(root, RootKeys);
        int? declared = root.Children.TryGetValue(new YamlScalarNode("version"), out var versionNode) ? context.Version(versionNode) : null;
        int version = declared ?? DetectVersion(root);
        // Older formats are rewritten in the node tree step by step, so the parser below only knows the current one.
        for (int from = version; from < FormatVersion; from++) Migrations[from - 1](root, context);
        string? id = context.String(root, "id")?.Trim() is { Length: > 0 } declaredId ? declaredId : null;
        string name = context.String(root, "name") ?? "";
        if (name.Length == 0) name = fallbackName;
        var timers = new List<TimerDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!root.Children.TryGetValue(new YamlScalarNode("timers"), out var timersNode) || timersNode is YamlScalarNode { Value: null or "" })
            context.Warn(root, Localization.T("No timers are defined."));
        else if (timersNode is not YamlSequenceNode sequence)
            context.Error(timersNode, Localization.T("timers must be a list."));
        else
            foreach (var item in sequence)
            {
                if (item is not YamlMappingNode mapping)
                {
                    context.Error(item, Localization.T("Each timer must be a mapping."));
                    continue;
                }
                if (context.Timer(mapping) is not { } timer) continue;
                if (!ids.Add(timer.Id))
                {
                    context.Error(mapping, Localization.F("Duplicate timer id '{0}'; the timer is skipped.", timer.Id));
                    continue;
                }
                timers.Add(timer);
            }
        return new(new Encounter { Id = id, Name = name, Timers = timers, BaseDirectory = baseDirectory, Pack = pack }, issues, version,
            declared is not null);
    }

    // A file without version: a `lucide:` icon makes it 4, a root id 3, any syntax added in format 2 makes
    // it 2, otherwise it is 1. Files saved before version was written use format 2 but carry no tag, and differ from 1 only
    // in that syntax or in a sound with both tts and sfx, which is read as format 1 when nothing else
    // tells them apart.
    private static int DetectVersion(YamlMappingNode root)
    {
        var sequence = root.Children.TryGetValue(new YamlScalarNode("timers"), out var timers) ? timers as YamlSequenceNode : null;
        if (sequence is not null && sequence.OfType<YamlMappingNode>().Any(timer =>
            timer.Children.TryGetValue(new YamlScalarNode("icon"), out var icon) && icon is YamlScalarNode { Value: var value } &&
            Lucide.IsIcon(value?.Trim())))
            return 4;
        if (root.Children.ContainsKey(new YamlScalarNode("id"))) return 3;
        if (sequence is null) return 1;
        foreach (var timer in sequence.OfType<YamlMappingNode>())
        {
            if (timer.Children.ContainsKey(new YamlScalarNode("provider")) || timer.Children.ContainsKey(new YamlScalarNode("note")) ||
                timer.Children.TryGetValue(new YamlScalarNode("key"), out var key) && key is YamlSequenceNode)
                return 2;
            if (timer.Children.TryGetValue(new YamlScalarNode("sound"), out var sound) &&
                (sound is YamlSequenceNode || sound is YamlMappingNode mapping && mapping.Children.ContainsKey(new YamlScalarNode("offset"))))
                return 2;
        }
        return 1;
    }

    // Migrations[n - 1] rewrites a format n tree into format n + 1.
    private static readonly Action<YamlMappingNode, Context>[] Migrations = [V1ToV2, V2ToV3, V3ToV4];

    // Format 4 only adds `icon: lucide:<name>`. In format 3 such a value was a file path that cannot exist
    // on Windows, so it is read as the Lucide icon.
    private static void V3ToV4(YamlMappingNode root, Context context) { }

    // Format 3 only adds the optional root id.
    private static void V2ToV3(YamlMappingNode root, Context context) { }

    // Format 1 played only the sfx of a sound that set both tts and sfx; format 2 plays both.
    private static void V1ToV2(YamlMappingNode root, Context context)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("timers"), out var timers) || timers is not YamlSequenceNode sequence) return;
        foreach (var timer in sequence.OfType<YamlMappingNode>())
        {
            if (!timer.Children.TryGetValue(new YamlScalarNode("sound"), out var node) || node is not YamlMappingNode sound ||
                !HasText(sound, "tts") || !HasText(sound, "sfx"))
                continue;
            sound.Children.Remove(new YamlScalarNode("tts"));
            string id = timer.Children.TryGetValue(new YamlScalarNode("id"), out var idNode) ? (idNode as YamlScalarNode)?.Value?.Trim() ?? "" : "";
            context.Warn(sound, Localization.F("Timer '{0}': sound sets both tts and sfx; using sfx.", id));
        }

        static bool HasText(YamlMappingNode mapping, string key) =>
            mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode { Value: { } text } &&
            !string.IsNullOrWhiteSpace(text);
    }

    // Comments in the original file are not preserved; the main window says so next to Save.
    internal static string Serialize(Encounter encounter)
    {
        var root = new YamlMappingNode
        {
            { "version", Number(FormatVersion) }
        };
        if (!string.IsNullOrEmpty(encounter.Id)) root.Add("id", Text(encounter.Id));
        root.Add("name", Text(encounter.Name));
        var timers = new YamlSequenceNode();
        foreach (var timer in encounter.Timers)
        {
            var node = new YamlMappingNode
            {
                { "id", Text(timer.Id) },
                { "name", Text(timer.DisplayName) },
                { "key", Keys(timer.Keys) },
                { "duration", Number(timer.Duration) },
                { "repeat", timer.Repeat ? "true" : "false" },
                { "max_instances", Number(timer.MaxInstances) },
                { "on_limit", LimitName(timer.OnLimit) },
                { "warn_before", Number(timer.WarnBefore) }
            };
            // One sound at the alert point keeps the short mapping form; otherwise a list with offsets.
            if (timer.Sounds is [{ Offset: 0 } single]) node.Add("sound", Sound(single));
            else if (timer.Sounds.Count > 0)
            {
                var sounds = new YamlSequenceNode();
                foreach (var sound in timer.Sounds)
                {
                    var item = Sound(sound);
                    if (sound.Offset != 0) item.Add("offset", Number(sound.Offset));
                    sounds.Add(item);
                }
                node.Add("sound", sounds);
            }
            if (!string.IsNullOrEmpty(timer.Icon)) node.Add("icon", Text(timer.Icon));
            if (!string.IsNullOrEmpty(timer.Message)) node.Add("message", Text(timer.Message));
            if (!string.IsNullOrEmpty(timer.Color)) node.Add("color", Text(timer.Color));
            if (!string.IsNullOrEmpty(timer.Provider)) node.Add("provider", Text(timer.Provider));
            if (!string.IsNullOrEmpty(timer.Note)) node.Add("note", Text(timer.Note));
            timers.Add(node);
        }
        root.Add("timers", timers);
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        // YamlStream always closes the document with "...", which is noise in a hand-edited file.
        string yaml = writer.ToString().Replace("\r\n", "\n");
        if (yaml.EndsWith("...\n", StringComparison.Ordinal)) yaml = yaml[..^4];
        return yaml;

        // Multi-line text (notes) is written as a literal block; the emitter falls back to quoting when
        // the text cannot be one (for example trailing spaces).
        static YamlScalarNode Text(string value) => new(value)
        {
            Style = value.Contains('\n') ? ScalarStyle.Literal : NeedsQuotes(value) ? ScalarStyle.DoubleQuoted : ScalarStyle.Any
        };
        static YamlScalarNode Number(double value) => new(value.ToString("R", CultureInfo.InvariantCulture));
        static YamlNode Keys(KeySet keys) => keys.Count == 1 ? Number(keys.Lowest)
            : new YamlSequenceNode(keys.Slots.Select(slot => (YamlNode)Number(slot))) { Style = YamlDotNet.Core.Events.SequenceStyle.Flow };
        static YamlMappingNode Sound(TimerSound sound) => sound.Sfx is not null
            ? new YamlMappingNode { { "sfx", Text(sound.Sfx) } }
            : new YamlMappingNode { { "tts", Text(sound.Tts ?? "") } };
    }

    // Plain scalars that YAML would read back as another type, or that start with an indicator.
    private static bool NeedsQuotes(string value) => value.Length == 0 || value.Trim() != value ||
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
        value.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~" ||
        "#&*!|>'\"%@`-?:,[]{}".Contains(value[0]) || value.Contains(": ", StringComparison.Ordinal) ||
        value.Contains(" #", StringComparison.Ordinal);

    internal static void Save(Encounter encounter, string path)
    {
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temporary, Serialize(encounter), new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Keeps the file as it is in an unused <file>.v<version>.bak, then saves the encounter over it in the
    // current format. Returns the backup's path.
    internal static string Upgrade(Encounter encounter, string path, int fromVersion)
    {
        string backup = path + $".v{fromVersion}.bak";
        for (int n = 2; File.Exists(backup); n++) backup = path + $".v{fromVersion}-{n}.bak";
        File.Copy(path, backup, overwrite: false);
        Save(encounter, path);
        return backup;
    }

    // Every sfx and icon file the encounter refers to, with the pack folder it belongs in. `lucide:` icons
    // are not files.
    private static IEnumerable<(string Relative, string Folder)> AssetReferences(Encounter encounter) =>
        encounter.Timers.SelectMany(timer => timer.Sounds.Select(sound => (Relative: sound.Sfx, Folder: EncounterPack.SoundFolder))
            .Append((Relative: timer.Icon, Folder: EncounterPack.IconFolder)))
        .Where(asset => !string.IsNullOrWhiteSpace(asset.Relative) && !Lucide.IsIcon(asset.Relative))
        .Select(asset => (asset.Relative!, asset.Folder));

    private static string[] ExtensionsFor(string folder) => folder == EncounterPack.SoundFolder ? SoundExtensions : ImageExtensions;

    // The file name an asset keeps in a pack or when extracted: the entry's name for a pack, otherwise the
    // source file's.
    private static string AssetFileName(string relative, string key) =>
        Path.GetFileName(EncounterPack.EntryName(relative) ?? key.Replace(EncounterPack.KeySeparator, '/'));

    // Writes the encounter as a pack at path, which must be named <id>.zip: index.yaml plus every sound and
    // icon it uses, sounds as sfx/<file> and icons as icons/<file> (numbered when another file already has
    // that name), and index.yaml refers to the new paths. Files of the source pack that nothing uses are left
    // out. A missing asset or one of the wrong type stops the export before anything is written. The
    // encounter itself is not changed. Returns the number of asset entries and of unused pack files left out.
    internal static (int Assets, int Trimmed) WritePack(Encounter encounter, string path)
    {
        path = Path.GetFullPath(path);
        if (encounter.Id is not { Length: > 0 } id) throw new InvalidDataException(Localization.T("Set an id first; the pack is named <id>.zip."));
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.EndsWith('.') || id.EndsWith(' '))
            throw new InvalidDataException(Localization.F("The id '{0}' cannot be used as a file name.", id));
        if (!EncounterPack.IsPackPath(path) || !string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Localization.F("The pack must be named {0}.", id + EncounterPack.Extension));

        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var timers = encounter.Timers.Select(timer => timer with
        {
            Icon = Add(timer.Icon, EncounterPack.IconFolder),
            Sounds = new(timer.Sounds.Select(sound => sound.Sfx is null ? sound : sound with { Sfx = Add(sound.Sfx, EncounterPack.SoundFolder) }))
        }).ToList();
        if (missing.Count > 0)
            throw new FileNotFoundException(Localization.F("Missing files: {0}. Nothing was exported.", string.Join(", ", missing.Distinct())));
        byte[] index = new System.Text.UTF8Encoding(false).GetBytes(
            Serialize(encounter with { Timers = timers, Pack = null, PackFiles = Encounter.NoPackFiles }));
        // The unused files of the source pack, counted before the zip is replaced.
        int trimmed = encounter.Pack is { } pack
            ? pack.Names.Where(name => !name.Equals(EncounterPack.IndexName, StringComparison.OrdinalIgnoreCase) && !encounter.PackFiles.ContainsKey(name))
                .Select(pack.Key).Concat(encounter.PackFiles.Values).Count(key => !sources.ContainsKey(key))
            : 0;
        EncounterPack.Write(path, [(EncounterPack.IndexName, index), .. entries.Select(entry => (entry.Key, entry.Value))]);
        // Opening it checks the result and replaces a pack of the same path that was opened before.
        EncounterPack.Open(path);
        return (entries.Count, trimmed);

        // The entry name for an asset path, reading the asset once per source file.
        string? Add(string? relative, string folder)
        {
            if (string.IsNullOrWhiteSpace(relative) || Lucide.IsIcon(relative)) return relative;
            if (encounter.Resolve(relative) is not { } key)
            {
                missing.Add(relative);
                return relative;
            }
            if (sources.TryGetValue(key, out var known)) return known;
            string name = AssetFileName(relative, key);
            if (!ExtensionsFor(folder).Contains(Path.GetExtension(name).ToLowerInvariant()))
                throw new InvalidDataException(Localization.F("'{0}' has an unsupported file type; supported: {1}.", relative,
                    string.Join(" ", ExtensionsFor(folder))));
            byte[] data;
            try { data = EncounterPack.ReadAsset(key); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                missing.Add(relative);
                return relative;
            }
            if (data.Length > EncounterPack.MaxEntryBytes)
                throw new InvalidDataException(Localization.F("{0} is larger than {1} MB.", relative, EncounterPack.MaxEntryBytes >> 20));
            string entry = FreeName(folder, name, data, other => entries.GetValueOrDefault(other));
            entries[entry] = data;
            sources[key] = entry;
            return entry;
        }
    }

    // folder/name, or folder/name-2, -3... while existing(entry) holds other bytes under that name.
    private static string FreeName(string folder, string name, byte[] data, Func<string, byte[]?> existing)
    {
        string entry = $"{folder}/{name}";
        for (int n = 2; existing(entry) is { } other && !other.AsSpan().SequenceEqual(data); n++)
            entry = $"{folder}/{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}";
        return entry;
    }

    // Adds a file to a pack encounter as folder/<file name>, numbered when the pack holds other bytes under
    // that name; the same bytes reuse the entry. The zip is written when the pack is saved. Returns the
    // encounter with the file added and the entry name to refer to.
    internal static (Encounter Encounter, string Entry) AddPackFile(Encounter encounter, string folder, string path)
    {
        var pack = encounter.Pack ?? throw new InvalidOperationException("Not a pack.");
        path = Path.GetFullPath(path);
        string name = Path.GetFileName(path);
        if (!ExtensionsFor(folder).Contains(Path.GetExtension(name).ToLowerInvariant()))
            throw new InvalidDataException(Localization.F("'{0}' has an unsupported file type; supported: {1}.", name,
                string.Join(" ", ExtensionsFor(folder))));
        if (new FileInfo(path).Length > EncounterPack.MaxEntryBytes)
            throw new InvalidDataException(Localization.F("{0} is larger than {1} MB.", name, EncounterPack.MaxEntryBytes >> 20));
        byte[] data = File.ReadAllBytes(path);
        string entry = FreeName(folder, name, data, other =>
            encounter.PackFiles.TryGetValue(other, out var source) ? File.Exists(source) ? File.ReadAllBytes(source) : []
            : pack.Find(other) is { } stored ? pack.Read(stored) : null);
        if (encounter.PackFiles.ContainsKey(entry)) return (encounter, entry);
        if (pack.Find(entry) is { } existing) return (encounter, existing);
        return (encounter with { PackFiles = encounter.PackFiles.SetItem(entry, path) }, entry);
    }

    // ---- Extracting a pack into data/ ----

    // Target is the full path, Label the path relative to data/. Exists: a file is already there; Conflict:
    // with other contents.
    internal sealed record ExtractedFile(string Target, string Label, byte[] Data, bool Exists, bool Conflict);
    internal sealed record PackExtraction(string YamlPath, IReadOnlyList<ExtractedFile> Files);

    // Plans extracting a pack encounter into data/: every asset it uses to sfx/<pack>_<file> or
    // icons/<pack>_<file>, and the encounter, referring to them, to <pack>.yaml (pack being the zip's file
    // name). The YAML is the last file. An asset missing from the pack keeps its path and is not extracted.
    internal static PackExtraction PlanExtraction(Encounter encounter, string? dataDirectory = null)
    {
        var pack = encounter.Pack ?? throw new InvalidOperationException("Not a pack.");
        string data = Path.GetFullPath(dataDirectory ?? DataDirectory);
        string prefix = pack.FileId;
        var files = new List<ExtractedFile>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, folder) in AssetReferences(encounter))
        {
            if (paths.ContainsKey(relative) || encounter.Resolve(relative) is not { } key) continue;
            byte[] bytes;
            try { bytes = EncounterPack.ReadAsset(key); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            string label = $"{folder}/{prefix}_{AssetFileName(relative, key)}";
            paths[relative] = label;
            if (!files.Any(file => string.Equals(file.Label, label, StringComparison.OrdinalIgnoreCase))) files.Add(Extracted(label, bytes));
        }
        var loose = encounter with
        {
            Pack = null, PackFiles = Encounter.NoPackFiles, BaseDirectory = data,
            Timers = encounter.Timers.Select(timer => timer with
            {
                Icon = Move(timer.Icon),
                Sounds = new(timer.Sounds.Select(sound => sound.Sfx is null ? sound : sound with { Sfx = Move(sound.Sfx) }))
            }).ToList()
        };
        files.Add(Extracted(prefix + Extension, new System.Text.UTF8Encoding(false).GetBytes(Serialize(loose))));
        return new(files[^1].Target, files);

        string? Move(string? relative) => relative is not null && paths.TryGetValue(relative, out var moved) ? moved : relative;

        ExtractedFile Extracted(string label, byte[] bytes)
        {
            string target = Path.Combine(data, label.Replace('/', Path.DirectorySeparatorChar));
            bool exists = File.Exists(target);
            return new(target, label, bytes, exists, exists && !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes));
        }
    }

    // Writes every planned file that is not there yet, and each conflicting one whose label is in overwrite
    // (through a temporary file). Returns the number of files written and whether the YAML in data/ is now
    // the extracted encounter.
    internal static (int Written, bool YamlReady) ApplyExtraction(PackExtraction plan, IReadOnlySet<string> overwrite)
    {
        int written = 0;
        bool yamlReady = false;
        foreach (var file in plan.Files)
        {
            bool yaml = file.Target == plan.YamlPath;
            if (file.Exists && !(file.Conflict && overwrite.Contains(file.Label)))
            {
                if (yaml) yamlReady = !file.Conflict;
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            string temporary = file.Target + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, file.Data);
                File.Move(temporary, file.Target, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            written++;
            if (yaml) yamlReady = true;
        }
        return (written, yamlReady);
    }

    internal static string LimitName(LimitAction action) => action switch
    {
        LimitAction.Ignore => "ignore", LimitAction.Reset => "reset", _ => "replace_oldest"
    };

    internal static bool IsColor(string? value) => value is { Length: 7 or 9 } && value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private sealed class Context(List<EncounterIssue> issues, string baseDirectory, EncounterPack? pack)
    {
        internal void Error(YamlNode node, string message) => issues.Add(new(true, (int)node.Start.Line, message));
        internal void Warn(YamlNode node, string message) => issues.Add(new(false, (int)node.Start.Line, message));

        internal void UnknownKeys(YamlMappingNode mapping, string[] known)
        {
            foreach (var key in mapping.Children.Keys)
                if (key is not YamlScalarNode { Value: { } name } || !known.Contains(name))
                    Warn(key, Localization.F("Unknown field '{0}' is ignored.", (key as YamlScalarNode)?.Value ?? key.ToString()));
        }

        // version: an integer from 1. Anything else warns and returns null, so the format is detected instead.
        internal int? Version(YamlNode node)
        {
            if (node is YamlScalarNode { Value: { } text } && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int version) &&
                version >= 1)
            {
                if (version > FormatVersion)
                    Warn(node, Localization.F("version {0} is newer than this TipAura supports ({1}); unknown fields are ignored.",
                        version, FormatVersion));
                return version;
            }
            Warn(node, Localization.T("version must be an integer from 1; the format is detected from the content."));
            return null;
        }

        private static YamlNode? Get(YamlMappingNode mapping, string key) =>
            mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) &&
            value is not YamlScalarNode { Value: null or "~" or "null", Style: ScalarStyle.Plain or ScalarStyle.Any } ? value : null;

        internal string? String(YamlMappingNode mapping, string key)
        {
            var node = Get(mapping, key);
            if (node is null) return null;
            if (node is YamlScalarNode scalar) return scalar.Value ?? "";
            Warn(node, Localization.F("{0} must be text; the value is ignored.", key));
            return null;
        }

        // A required number that is not numeric is an error and returns NaN; an optional one warns
        // and returns null so the caller keeps its default.
        private double? Number(YamlMappingNode mapping, string key, bool optional = false)
        {
            var node = Get(mapping, key);
            if (node is null) return null;
            if (node is YamlScalarNode { Value: { } text } &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                return value;
            if (optional)
            {
                Warn(node, Localization.F("{0} must be a number; using the default.", key));
                return null;
            }
            Error(node, Localization.F("{0} must be a number.", key));
            return double.NaN;
        }

        private bool? Boolean(YamlMappingNode mapping, string key)
        {
            var node = Get(mapping, key);
            if (node is null) return null;
            if (node is YamlScalarNode { Value: { } text })
                switch (text.ToLowerInvariant())
                {
                    case "true" or "yes" or "on": return true;
                    case "false" or "no" or "off": return false;
                }
            Warn(node, Localization.F("{0} must be true or false; using false.", key));
            return false;
        }

        internal TimerDefinition? Timer(YamlMappingNode mapping)
        {
            UnknownKeys(mapping, TimerKeys);
            string id = String(mapping, "id")?.Trim() ?? "";
            if (id.Length == 0)
            {
                Error(mapping, Localization.T("A timer has no id; the timer is skipped."));
                return null;
            }
            KeySet? keys = Keys(mapping, id);
            bool valid = keys is not null;
            double? duration = Number(mapping, "duration");
            if (duration is not { } seconds || !(seconds > 0) || seconds > TimerDefinition.MaxDuration)
            {
                if (duration is null || !double.IsNaN(duration.Value))
                    Error(Get(mapping, "duration") ?? mapping,
                        Localization.F("Timer '{0}': duration must be greater than 0 and at most 86400 seconds.", id));
                valid = false;
            }
            if (!valid) return null;

            var timer = new TimerDefinition
            {
                Id = id, Name = String(mapping, "name")?.Trim() ?? "", Keys = keys!.Value, Duration = duration!.Value,
                Repeat = Boolean(mapping, "repeat") ?? false, Message = Empty(String(mapping, "message")),
                Provider = Empty(String(mapping, "provider"))
            };
            if (Number(mapping, "max_instances", optional: true) is { } max)
            {
                if (max != Math.Floor(max) || max < 1 || max > TimerDefinition.MaxInstanceLimit)
                    Warn(Get(mapping, "max_instances")!, Localization.F("Timer '{0}': max_instances must be an integer from 1 to 20; using 1.", id));
                else timer = timer with { MaxInstances = (int)max };
            }
            if (String(mapping, "on_limit") is { } limit)
            {
                LimitAction? action = limit.Trim().ToLowerInvariant() switch
                {
                    "replace_oldest" => LimitAction.ReplaceOldest, "ignore" => LimitAction.Ignore, "reset" => LimitAction.Reset,
                    _ => null
                };
                if (action is null)
                    Warn(Get(mapping, "on_limit")!, Localization.F("Timer '{0}': on_limit must be replace_oldest, ignore or reset; using replace_oldest.", id));
                else timer = timer with { OnLimit = action.Value };
            }
            if (Number(mapping, "warn_before", optional: true) is { } warn)
            {
                var node = Get(mapping, "warn_before")!;
                if (warn < 0) Warn(node, Localization.F("Timer '{0}': warn_before must be 0 or more seconds; using 0.", id));
                else
                {
                    if (warn > timer.Duration)
                        Warn(node, Localization.F("Timer '{0}': warn_before is longer than duration; the alert fires when the timer starts.", id));
                    timer = timer with { WarnBefore = warn };
                }
            }
            if (Get(mapping, "sound") is { } soundNode) timer = timer with { Sounds = Sounds(soundNode, timer) };
            if (String(mapping, "note") is { } note && note.Trim().Length > 0)
            {
                note = TimerDefinition.LimitNote(note.Trim(), out bool truncated);
                if (truncated)
                    Warn(Get(mapping, "note")!, Localization.F("Timer '{0}': note is longer than {1} characters; the rest is cut off.",
                        id, TimerDefinition.MaxNoteLength));
                timer = timer with { Note = note };
            }
            if (Empty(String(mapping, "icon")) is { } icon)
            {
                if (!Lucide.IsIcon(icon)) CheckFile(Get(mapping, "icon")!, id, icon, ImageExtensions, EncounterPack.IconFolder);
                else if (Lucide.Find(icon) is { } glyph) icon = glyph.Value;
                else Warn(Get(mapping, "icon")!, Localization.F("Timer '{0}': unknown Lucide icon '{1}'.", id, icon));
                timer = timer with { Icon = icon };
            }
            if (Empty(String(mapping, "color")) is { } color)
            {
                if (IsColor(color)) timer = timer with { Color = color };
                else Warn(Get(mapping, "color")!, Localization.F("Timer '{0}': color must be #RRGGBB or #RRGGBBAA; using the default.", id));
            }
            return timer;
        }

        // key: one slot or a list of slots. Returns null (with an error) when no valid slot is given.
        private KeySet? Keys(YamlMappingNode mapping, string id)
        {
            var node = Get(mapping, "key");
            List<YamlNode> items = node is YamlSequenceNode sequence ? [.. sequence.Children] : node is null ? [] : [node];
            var keys = new KeySet(0);
            foreach (var item in items)
            {
                if (item is not YamlScalarNode { Value: { } text } ||
                    !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double slot) ||
                    slot != Math.Floor(slot) || slot < TimerDefinition.MinKey || slot > TimerDefinition.MaxKey)
                {
                    Error(item, Localization.F("Timer '{0}': key must be an integer from 1 to 9, or a list of them.", id));
                    return null;
                }
                if (keys.Contains((int)slot)) Warn(item, Localization.F("Timer '{0}': key {1} is listed twice.", id, (int)slot));
                keys = keys.With((int)slot, true);
            }
            if (keys.IsEmpty)
            {
                Error(node ?? mapping, Localization.F("Timer '{0}': key must be an integer from 1 to 9, or a list of them.", id));
                return null;
            }
            return keys;
        }

        // sound: one mapping or a list of mappings. A mapping with both tts and sfx gives two sounds that
        // play together.
        private ValueList<TimerSound> Sounds(YamlNode node, TimerDefinition timer)
        {
            string id = timer.Id;
            List<YamlNode> items = node is YamlSequenceNode sequence ? [.. sequence.Children] : [node];
            var sounds = new List<TimerSound>();
            foreach (var item in items)
            {
                if (item is not YamlMappingNode mapping)
                {
                    Warn(item, Localization.F("Timer '{0}': each sound must be a mapping with tts or sfx; it is ignored.", id));
                    continue;
                }
                UnknownKeys(mapping, SoundKeys);
                string? tts = Empty(String(mapping, "tts")), sfx = Empty(String(mapping, "sfx"));
                if (tts is null && sfx is null)
                {
                    Warn(mapping, Localization.F("Timer '{0}': each sound must be a mapping with tts or sfx; it is ignored.", id));
                    continue;
                }
                double offset = 0;
                if (Number(mapping, "offset", optional: true) is { } value)
                {
                    if (Math.Abs(value) > TimerSound.MaxOffset)
                        Warn(Get(mapping, "offset")!, Localization.F("Timer '{0}': offset must be from -86400 to 86400 seconds; using 0.", id));
                    else offset = value;
                }
                if (sfx is not null) CheckFile(Get(mapping, "sfx")!, id, sfx, SoundExtensions, EncounterPack.SoundFolder);
                TimerSound?[] parsed = [sfx is null ? null : new TimerSound { Sfx = sfx, Offset = offset },
                    tts is null ? null : new TimerSound { Tts = tts, Offset = offset }];
                foreach (var sound in parsed)
                {
                    if (sound is null) continue;
                    if (sounds.Count >= TimerDefinition.MaxSounds)
                    {
                        Warn(mapping, Localization.F("Timer '{0}': more than {1} sounds; the rest are ignored.", id, TimerDefinition.MaxSounds));
                        return new(sounds);
                    }
                    if (timer.SoundClamped(sound))
                        Warn(mapping, Localization.F("Timer '{0}': a sound offset falls outside the countdown; it plays at the start or end.", id));
                    sounds.Add(sound);
                }
            }
            return new(sounds);
        }

        // A missing file is a warning, not an error: the timer still runs, without that sound or icon. In a
        // pack, sounds must be in sfx/ and icons in icons/ (EncounterPack.AssetFolder).
        private void CheckFile(YamlNode node, string id, string relative, string[] extensions, string packFolder)
        {
            if (pack is not null)
            {
                if (EncounterPack.EntryName(relative) is not { } entry)
                    Warn(node, Localization.F("Timer '{0}': '{1}' must be a path inside the pack.", id, relative));
                else if (!extensions.Contains(Path.GetExtension(entry).ToLowerInvariant()))
                    Warn(node, Localization.F("Timer '{0}': unsupported file type '{1}'; supported: {2}.", id, relative, string.Join(" ", extensions)));
                else if (EncounterPack.AssetFolder(entry) != packFolder)
                    Warn(node, Localization.F("Timer '{0}': '{1}' must be directly in the pack's {2}/ folder.", id, relative, packFolder));
                else if (pack.Find(relative) is null)
                    Warn(node, Localization.F("Timer '{0}': {1} is not in the pack.", id, relative));
                return;
            }
            string path;
            try { path = Path.GetFullPath(Path.Combine(baseDirectory, relative)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Warn(node, Localization.F("Timer '{0}': invalid path '{1}'.", id, relative));
                return;
            }
            if (!extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                Warn(node, Localization.F("Timer '{0}': unsupported file type '{1}'; supported: {2}.", id, relative, string.Join(" ", extensions)));
            else if (!File.Exists(path))
                Warn(node, Localization.F("Timer '{0}': file not found: {1}", id, relative));
        }

        private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
