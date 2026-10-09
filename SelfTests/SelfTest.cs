#if TIPAURA_AGENT_SELF_TEST
using System.Diagnostics;

// Agent-only checks; see TECHNICAL.md "Validation" for when to run each entry point.
internal static class SelfTest
{
    // Pure logic with no devices, windows or global hooks: YAML, the countdown engine and hotkey mapping.
    internal static void Run()
    {
        YamlSmoke();
        PackSmoke.Run();
        LucideSmoke.Run();
        EngineSmoke();
        KeybindSmoke.Processor();
        HistorySmoke();
        HookSmoke.Run();
        ShaderTool.Verify();
        Console.WriteLine("Self-test passed.");
    }

    internal static string ProjectPath(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TipAura.csproj"))) return Path.Combine(directory.FullName, relative);
        throw new InvalidOperationException("Run the self-test from a build inside the TipAura repository.");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The format 1 example (commit a80e300) is the upgrade target: it holds the first six timers of today's example.
    private static void UpgradeSmoke(Encounter current)
    {
        string fixture = ProjectPath("SelfTests/fixtures/example-v1.yaml");
        string original = File.ReadAllText(fixture);
        var v1 = EncounterYaml.Parse(original, current.BaseDirectory);
        Check(v1 is { Version: 1, Tagged: false, NeedsUpgrade: true } && v1.Issues.Count == 0,
            $"The format 1 example should load as an untagged version 1 without issues: {v1.Version} {v1.Tagged} " + string.Join("; ", v1.Issues));
        Check(v1.Encounter!.Name == current.Name && v1.Encounter.Timers.SequenceEqual(current.Timers.Take(6)),
            "The format 1 example should read as the first six timers of the current example.");
        string upgraded = EncounterYaml.Serialize(v1.Encounter);
        Check(upgraded.StartsWith($"version: {EncounterYaml.FormatVersion}\n", StringComparison.Ordinal), "Serialize should write version first:\n" + upgraded);
        var back = EncounterYaml.Parse(upgraded, current.BaseDirectory);
        Check(back is { Version: EncounterYaml.FormatVersion, Tagged: true, NeedsUpgrade: false } && back.Issues.Count == 0 &&
            back.Encounter!.Timers.SequenceEqual(v1.Encounter.Timers.Select(t => t with { Name = t.DisplayName })),
            "The upgraded example did not load back unchanged:\n" + upgraded + string.Join("; ", back.Issues));

        // On disk: the original is kept byte for byte, and a second upgrade never overwrites the first backup.
        // The temporary folder has no sfx or icons, so only missing-file warnings are expected there.
        string folder = Path.Combine(Path.GetTempPath(), $"tipaura-upgrade-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "old.yaml");
            File.Copy(fixture, path);
            var loaded = EncounterYaml.Load(path);
            string backup = EncounterYaml.Upgrade(loaded.Encounter!, path, loaded.Version);
            Check(Path.GetFileName(backup) == "old.yaml.v1.bak" && File.ReadAllBytes(backup).AsSpan().SequenceEqual(File.ReadAllBytes(fixture)),
                "The backup should be an exact copy named old.yaml.v1.bak: " + backup);
            var after = EncounterYaml.Load(path);
            Check(after is { Version: EncounterYaml.FormatVersion, Tagged: true, NeedsUpgrade: false, HasErrors: false } &&
                after.Encounter!.Timers.Count == 6, "The upgraded file should load as the current format.");
            File.Copy(fixture, path, overwrite: true);
            string second = EncounterYaml.Upgrade(loaded.Encounter!, path, 1);
            Check(Path.GetFileName(second) == "old.yaml.v1-2.bak" && File.ReadAllText(backup) == original,
                "A second upgrade should pick a new backup name: " + second);
        }
        finally { Directory.Delete(folder, true); }

        // Detection and format 1 semantics: a sound with both tts and sfx plays only the sfx in format 1.
        const string both = "timers:\n  - {id: a, key: 1, duration: 5, sound: {tts: hi, sfx: sfx/chime.wav}}";
        var old = EncounterYaml.Parse(both, current.BaseDirectory);
        Check(old is { Version: 1, Encounter.Timers: [{ Sounds: [{ Sfx: "sfx/chime.wav", Tts: null }] }] } &&
            old.Issues.Single() is { IsError: false } issue && issue.Message.Contains("using sfx", StringComparison.Ordinal),
            "Format 1 tts + sfx should keep only the sfx with a warning: " + string.Join("; ", old.Issues));
        foreach (string newer in new[] { both.Replace("key: 1", "key: [1]"), both.Replace("}}", ", offset: 0}}"),
            both.Replace("duration: 5", "duration: 5, note: n"), "version: 2\n" + both })
        {
            var parsed = EncounterYaml.Parse(newer, current.BaseDirectory);
            Check(parsed is { Version: 2, Encounter.Timers: [{ Sounds.Count: 2 }] } && parsed.Issues.Count == 0,
                "Format 2 syntax or a version tag should keep both sounds: " + newer + " " + string.Join("; ", parsed.Issues));
        }
        Check(EncounterYaml.Parse("version: 2\n" + both, "").NeedsUpgrade && !EncounterYaml.Parse($"version: {EncounterYaml.FormatVersion}\n" + both, "").NeedsUpgrade &&
            EncounterYaml.Parse(both.Replace("key: 1", "key: [1]"), current.BaseDirectory).NeedsUpgrade,
            "Only an untagged or older file needs an upgrade.");
        var future = EncounterYaml.Parse($"version: {EncounterYaml.FormatVersion + 1}\n" + both, current.BaseDirectory);
        Check(future is { Version: EncounterYaml.FormatVersion + 1, NeedsUpgrade: false, Encounter.Timers: [{ Sounds.Count: 2 }] } &&
            future.Issues.Any(i => i.Message.Contains("newer", StringComparison.Ordinal)), "A newer version should warn and load as current.");
        var bad = EncounterYaml.Parse("version: x\n" + both, current.BaseDirectory);
        Check(bad is { Version: 1, Tagged: false, NeedsUpgrade: true } && bad.Issues.Any(i => i.Message.Contains("version must be", StringComparison.Ordinal)),
            "An invalid version should warn and fall back to detection.");
        Check(EncounterYaml.Parse("name: x\ntimers: [", "") is { NeedsUpgrade: false }, "A file that fails to parse needs no upgrade.");
    }

    private static void YamlSmoke()
    {
        Localization.Configure("en");
        var example = EncounterYaml.Load(ProjectPath("data/example.yaml"));
        Check(example.Encounter is { Timers.Count: 7 }, "example.yaml should load 7 timers.");
        Check(example.Issues.Count == 0, "example.yaml has issues: " + string.Join("; ", example.Issues));
        Check(example is { Version: EncounterYaml.FormatVersion, Tagged: true, NeedsUpgrade: false }, "example.yaml should declare the current format.");
        var encounter = example.Encounter!;
        var breath = encounter.Timers[0];
        Check(breath.Keys == KeySet.Of(1) && breath is { Id: "breath", Duration: 30, WarnBefore: 5, Repeat: false, MaxInstances: 1, Icon: "icons/fire.png" },
            "breath fields were not parsed.");
        Check(breath.ExpandedTts(breath.Sounds.Single()) == "龍息 5 秒", $"TTS template expansion: {breath.ExpandedTts(breath.Sounds[0])}");
        Check(encounter.Timers[1] is { Repeat: true, Sounds: [{ Sfx: "sfx/chime.wav", Offset: 0 }] }, "frost_orb repeat/sfx were not parsed.");
        var combo = encounter.Timers[6];
        Check(combo.Keys == KeySet.Of(6, 7), "A key list should bind several keys.");
        Check(combo.Icon == "lucide:tornado", "A Lucide icon should be kept as lucide:<name>.");
        Check(combo is { Provider: not null, Note: not null, Sounds: [{ Sfx: "sfx/chime.wav", Offset: 0 }, { Tts: not null, Offset: 1 }] },
            "combo provider/note/sounds were not parsed.");
        Check(combo.ExpandedTts(combo.Sounds[1]) == "2 秒後轉階段", $"{{sec}} should be the time left at that sound: {combo.ExpandedTts(combo.Sounds[1])}");
        Check(encounter.Timers[2] is { MaxInstances: 3, OnLimit: LimitAction.ReplaceOldest }, "poison limit was not parsed.");
        Check(encounter.Timers[5].OnLimit == LimitAction.Ignore, "on_limit ignore was not parsed.");
        Check(encounter.Resolve("sfx/chime.wav") is { } chime && File.Exists(chime), "Relative sfx paths should resolve next to the YAML.");

        // Serialize -> parse keeps every field.
        string yaml = EncounterYaml.Serialize(encounter);
        var again = EncounterYaml.Parse(yaml, encounter.BaseDirectory);
        Check(again.Issues.Count == 0, "Round trip produced issues: " + string.Join("; ", again.Issues));
        Check(again.Encounter!.Name == encounter.Name && again.Encounter.Timers.SequenceEqual(encounter.Timers.Select(t => t with { Name = t.DisplayName })),
            "Round trip changed the timers:\n" + yaml);
        Check(yaml.Contains("name: 範例首領") && !yaml.Contains("\\u"), "Serialized YAML should keep CJK text readable:\n" + yaml);
        // Strings that would read back as other types are quoted.
        var tricky = encounter with { Name = "true", Timers = [breath with { Id = "123", Message = "a: b #c", Name = "" }] };
        var trickyBack = EncounterYaml.Parse(EncounterYaml.Serialize(tricky), encounter.BaseDirectory).Encounter!;
        Check(trickyBack.Name == "true" && trickyBack.Timers[0] is { Id: "123", Message: "a: b #c", Name: "123" },
            "Quoted scalars did not round trip: " + EncounterYaml.Serialize(tricky));

        ExpectIssue("name: x\ntimers: [", true, "YAML syntax error");
        ExpectIssue("- a\n- b", true, "must be a mapping");
        ExpectIssue("name: x", false, "No timers");
        ExpectIssue("timers:\n  - name: a\n    key: 1\n    duration: 5", true, "no id");
        ExpectIssue("timers:\n  - id: a\n    key: 10\n    duration: 5", true, "key must be an integer from 1 to 9");
        ExpectIssue("timers:\n  - id: a\n    key: 1.5\n    duration: 5", true, "key must be an integer");
        ExpectIssue("timers:\n  - id: a\n    key: x\n    duration: 5", true, "key must be an integer from 1 to 9");
        ExpectIssue("timers:\n  - id: a\n    key: 1\n    duration: 0", true, "duration must be greater than 0");
        ExpectIssue("timers:\n  - id: a\n    key: 1", true, "duration must be greater than 0");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5}\n  - {id: a, key: 2, duration: 5}", true, "Duplicate timer id");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, colour: red}", false, "Unknown field 'colour'");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, warn_before: 9}", false, "warn_before is longer than duration");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, warn_before: x}", false, "warn_before must be a number");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, max_instances: 0}", false, "max_instances must be an integer from 1 to 20");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, on_limit: stack}", false, "on_limit must be");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, color: red}", false, "color must be");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, repeat: maybe}", false, "repeat must be true or false");
        ExpectIssue("timers:\n  - {id: a, key: [], duration: 5}", true, "or a list of them");
        ExpectIssue("timers:\n  - {id: a, key: [1, x], duration: 5}", true, "key must be an integer from 1 to 9");
        ExpectIssue("timers:\n  - {id: a, key: [1, 1], duration: 5}", false, "listed twice");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, sound: hi}", false, "each sound must be a mapping");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, sound: [{offset: 1}]}", false, "each sound must be a mapping");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, sound: {tts: hi, offset: 9}}", false, "outside the countdown");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, sound: {tts: hi, offset: 99999}}", false, "offset must be from");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, note: " + new string('x', 1025) + "}", false, "note is longer than 1024");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, sound: {sfx: sfx/missing.wav}}", false, "file not found");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, sound: {sfx: sfx/x.flac}}", false, "unsupported file type");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, icon: icons/missing.png}", false, "file not found");
        ExpectIssue("timers:\n  - {id: a, key: 1, duration: 5, icon: lucide:no-such-icon}", false, "unknown Lucide icon 'lucide:no-such-icon'");

        // A bad timer is skipped while the rest of the file still loads; the error carries its line.
        var partial = EncounterYaml.Parse("name: p\ntimers:\n  - {id: ok, key: 1, duration: 5}\n  - {id: bad, key: 1, duration: -1}\n",
            encounter.BaseDirectory);
        Check(partial.Encounter!.Timers.Count == 1 && partial.Issues.Single().Line == 4,
            "A bad timer should be skipped with an error on its own line: " + string.Join("; ", partial.Issues));
        var fallback = EncounterYaml.Parse("timers:\n  - {id: a, key: 1, duration: 5, max_instances: 0, warn_before: -2}", "", "fallback-name");
        Check(fallback.Encounter is { Name: "fallback-name" } && fallback.Encounter.Timers[0] is { MaxInstances: 1, WarnBefore: 0 },
            "Invalid optional fields should keep their defaults; a missing name uses the file name.");

        // Save writes a file that loads back to the same timers.
        string temp = Path.Combine(Path.GetTempPath(), $"tipaura-{Guid.NewGuid():N}.yaml");
        try
        {
            EncounterYaml.Save(encounter with { BaseDirectory = Path.GetDirectoryName(temp)! }, temp);
            var saved = EncounterYaml.Load(temp).Encounter!;
            Check(saved.Timers.Count == encounter.Timers.Count && !File.Exists(temp + ".tmp"), "Save did not write a loadable file.");
        }
        finally { File.Delete(temp); }
        Localization.Configure(null);
        // Composite sounds, key lists, provider and multi-line notes.
        var composite = EncounterYaml.Parse("timers:\n  - {id: a, key: [3, 1], duration: 10, warn_before: 4, sound: {tts: \"{sec}\", sfx: sfx/chime.wav}}",
            encounter.BaseDirectory);
        var a = composite.Encounter!.Timers.Single();
        Check(composite.Issues.Count == 0 && a.Keys == KeySet.Of(1, 3) && a.Sounds is [{ Sfx: "sfx/chime.wav" }, { Tts: "{sec}" }],
            "tts + sfx in one mapping should give two sounds: " + string.Join("; ", composite.Issues));
        string note = "第一行\n" + new string('字', 1100);
        string clipped = TimerDefinition.LimitNote(note, out bool cut);
        Check(cut && TimerDefinition.NoteLength(clipped) == 1024 && TimerDefinition.LimitNote("😀😀", out bool notCut) == "😀😀" && !notCut,
            "Notes should be limited to 1024 characters.");
        var rich = encounter with
        {
            Timers = [a with { Provider = "MT", Note = "line 1\nline 2: x #y", Sounds = new([a.Sounds[0], a.Sounds[1] with { Offset = -1.5 }]) },
                a with { Id = "b", Note = "trailing space \nnext", Sounds = new([new TimerSound { Tts = "only", Offset = 0.25 }]) }]
        };
        string richYaml = EncounterYaml.Serialize(rich);
        var richBack = EncounterYaml.Parse(richYaml, encounter.BaseDirectory);
        Check(richBack.Issues.Count == 0 && richBack.Encounter!.Timers.SequenceEqual(rich.Timers.Select(t => t with { Name = t.DisplayName })),
            "Key lists, sound lists, provider and notes did not round trip:\n" + richYaml + string.Join("; ", richBack.Issues));
        Check(richYaml.Contains("key: [1, 3]") && richYaml.Contains("note: |"), "Key lists should be flow lists and notes literal blocks:\n" + richYaml);
        // Timers tab sorting: key, time; ties keep the file order.
        TimerDefinition[] list = [a with { Provider = "b", Keys = KeySet.Of(5), Duration = 3 }, a with { Keys = KeySet.Of(2), Duration = 9 },
            a with { Provider = "A", Keys = KeySet.Of(2, 8), Duration = 3 }];
        Check(MainWindow.SortedTimers(list, 0).SequenceEqual([0, 1, 2]) && MainWindow.SortedTimers(list, 1).SequenceEqual([2, 1, 0]) &&
            MainWindow.SortedTimers(list, 2).SequenceEqual([2, 0, 1]), "Timer list sorting is wrong.");
        Check(MainWindow.TimerListLabel(list[0] with { Name = "n", Provider = null, Keys = KeySet.Of(1, 2, 3, 4, 5) }, Num) == "Num1  n\nNum2\nNum3  +2" &&
            MainWindow.TimerListLabel(list[2] with { Name = "n" }, Num) == "Num2  n  [A]\nNum8", "Timer list labels should put extra keys on up to two more lines.");
        Localization.Configure("en");
        UpgradeSmoke(encounter);
        Localization.Configure(null);
        Console.WriteLine("YAML: example, round trip, quoting, validation messages, save and upgrade passed.");

        static void ExpectIssue(string yaml, bool error, string fragment)
        {
            var result = EncounterYaml.Parse(yaml, ProjectPath("data"));
            Check(result.Issues.Any(issue => issue.IsError == error && issue.Message.Contains(fragment, StringComparison.Ordinal)),
                $"Expected {(error ? "error" : "warning")} containing '{fragment}' for:\n{yaml}\nGot: {string.Join("; ", result.Issues)}");
        }
    }

    private static void EngineSmoke()
    {
        const long Hz = 1000; // Fake clock in milliseconds.
        TimerDefinition Timer(string id, int key, double duration, double warn = 0, bool repeat = false,
            int max = 1, LimitAction limit = LimitAction.ReplaceOldest) => new()
            { Id = id, Name = id, Keys = KeySet.Of(key), Duration = duration, WarnBefore = warn, Repeat = repeat, MaxInstances = max, OnLimit = limit };
        var engine = new TimerEngine(Hz);

        // Alert at end - warn_before, removal at the end.
        var a = Timer("a", 1, 10, warn: 3);
        engine.Load(new Encounter { Timers = [a] });
        Check(engine.Start(1, 0).Count == 0, "No alert should fire at start.");
        Check(engine.NextDeadline() == 7000, "Next deadline should be the alert time.");
        Check(engine.Advance(6999).Count == 0, "Alert fired early.");
        Check(engine.Advance(7000).Single().Timer == a, "Alert did not fire at 7 s.");
        Check(engine.Advance(8000).Count == 0, "Alert fired twice.");
        Check(engine.NextDeadline() == 10000 && engine.Snapshot().Single().Alerted, "After the alert the deadline is the end.");
        Check(Math.Abs(engine.Snapshot()[0].Remaining(8500, Hz) - 1.5) < 1e-9, "Remaining time is wrong.");
        engine.Advance(10000);
        Check(engine.Count == 0 && engine.NextDeadline() is null, "Timer should be removed at its end.");

        // warn_before 0 alerts at the end; warn_before >= duration alerts on start.
        var end = Timer("end", 2, 5);
        var early = Timer("early", 3, 2, warn: 5);
        engine.Load(new Encounter { Timers = [end, early] });
        engine.Start(2, 0);
        Check(engine.Advance(4999).Count == 0 && engine.Advance(5000).Single().Timer == end, "warn_before 0 should alert at the end.");
        Check(engine.Start(3, 0).Single().Timer == early, "warn_before >= duration should alert when started.");

        // Repeat restarts from the scheduled end, and skips whole cycles after a stall with one alert.
        var r = Timer("r", 4, 10, warn: 2, repeat: true);
        engine.Load(new Encounter { Timers = [r] });
        engine.Start(4, 0);
        Check(engine.Advance(8000).Count == 1 && engine.Advance(10000).Count == 0, "First cycle alerts once.");
        Check(engine.Snapshot().Single().EndTicks == 20000, "Repeat should start the next cycle at the previous end.");
        Check(engine.Advance(18000).Count == 1, "Second cycle alert.");
        Check(engine.Advance(55000).Count == 0 && engine.Snapshot().Single().EndTicks == 60000,
            "A stall should skip to the cycle containing now without alerting for skipped cycles.");
        Check(engine.Advance(58000).Count == 1, "Alert in the caught-up cycle.");
        engine.ResetSlot(4);
        Check(engine.Count == 0, "ResetSlot should clear the repeat timer.");

        // Simultaneous limit: replace_oldest, ignore, reset.
        var replace = Timer("replace", 5, 10, max: 2);
        var ignore = Timer("ignore", 6, 10, max: 2, limit: LimitAction.Ignore);
        var reset = Timer("reset", 7, 10, max: 2, limit: LimitAction.Reset);
        engine.Load(new Encounter { Timers = [replace, ignore, reset] });
        foreach (int t in new[] { 0, 1000, 2000 }) { engine.Start(5, t); engine.Start(6, t); engine.Start(7, t); }
        long[] Ends(TimerDefinition timer) => [.. engine.Snapshot().Where(v => v.Timer == timer).Select(v => v.EndTicks)];
        Check(Ends(replace).SequenceEqual([11000L, 12000L]), "replace_oldest should drop the oldest copy: " + string.Join(",", Ends(replace)));
        Check(Ends(ignore).SequenceEqual([10000L, 11000L]), "ignore should keep the running copies: " + string.Join(",", Ends(ignore)));
        Check(Ends(reset).SequenceEqual([12000L]), "reset should clear the copies and start one: " + string.Join(",", Ends(reset)));

        // One key starts several timers; reset by slot, by instance and for the whole encounter.
        var x = Timer("x", 8, 10);
        var y = Timer("y", 8, 20);
        var z = Timer("z", 9, 30);
        engine.Load(new Encounter { Timers = [x, y, z] });
        engine.Start(8, 0);
        engine.Start(9, 0);
        Check(engine.Count == 3, "Numpad8 should start both of its timers.");
        engine.ResetInstance(engine.Snapshot().First(v => v.Timer == y).InstanceId);
        Check(engine.Count == 2 && engine.Snapshot().All(v => v.Timer != y), "ResetInstance should remove one copy.");
        engine.ResetSlot(8);
        Check(engine.Snapshot().Single().Timer == z, "ResetSlot should only clear its own key.");
        engine.ResetAll();
        Check(engine.Count == 0, "ResetAll should clear everything.");
        engine.Start(8, 0);
        engine.Load(new Encounter { Timers = [x] });
        Check(engine.Count == 0, "Loading an encounter should clear running timers.");
        Check(engine.Start(1, 0).Count == 0 && engine.Count == 0, "A key without timers should do nothing.");

        // One timer on several keys: either key starts it, Alt+either key resets it.
        var multi = Timer("multi", 1, 10, max: 3) with { Keys = KeySet.Of(1, 3) };
        engine.Load(new Encounter { Timers = [multi] });
        engine.Start(1, 0);
        engine.Start(3, 0);
        engine.Start(2, 0);
        Check(engine.Count == 2, "Both bound keys should start the timer, and only those.");
        engine.ResetSlot(3);
        Check(engine.Count == 0, "Resetting either bound key should clear the timer.");

        // Disabled timers: their keys skip them, disabling removes running copies, Load keeps the set.
        var kept = Timer("kept", 1, 10);
        var off = Timer("off", 1, 10);
        engine.Load(new Encounter { Timers = [kept, off] });
        engine.Start(1, 0);
        engine.SetDisabled(new HashSet<string> { "off" });
        Check(engine.Count == 1 && engine.Snapshot().Single().Timer == kept, "Disabling should remove the timer's running copies.");
        engine.Load(new Encounter { Timers = [kept, off] });
        engine.Start(1, 0);
        Check(engine.Count == 1 && engine.Snapshot().Single().Timer == kept, "A disabled timer should not start, also after Load.");
        engine.SetDisabled(new HashSet<string>());
        engine.Start(1, 0);
        Check(engine.Snapshot().Count(v => v.Timer == off) == 1, "An enabled timer should start again.");

        // Sounds play at the alert point plus their offset, clamped into the countdown; the alert is separate.
        var timeline = Timer("timeline", 1, 10, warn: 4) with
        {
            Sounds = new([new TimerSound { Tts = "late", Offset = 1 }, new TimerSound { Sfx = "x.wav" }, new TimerSound { Tts = "early", Offset = -2 },
                new TimerSound { Tts = "clamped", Offset = 99 }, new TimerSound { Tts = "start", Offset = -99 }])
        };
        engine.Load(new Encounter { Timers = [timeline] });
        var startCues = engine.Start(1, 0);
        Check(startCues.Count == 1 && startCues[0].Sound == 4, "A sound clamped to the start should play when the timer starts.");
        Check(engine.NextDeadline() == 4000, "The next deadline should be the early sound.");
        Check(engine.Advance(4000).Single().Sound == 2, "Negative offsets play before the alert.");
        var together = engine.Advance(6000);
        Check(together.Count == 2 && together.Any(c => c.Sound == -1) && together.Any(c => c.Sound == 1),
            "Offset 0 plays together with the alert.");
        Check(engine.Advance(7000).Single().Sound == 0, "Positive offsets play after the alert.");
        Check(engine.Advance(10000).Single().Sound == 3 && engine.Count == 0, "Sounds past the end play at the end.");
        var looping = timeline with { Repeat = true, Sounds = new([new TimerSound { Tts = "a", Offset = 1 }]) };
        engine.Load(new Encounter { Timers = [looping] });
        engine.Start(1, 0);
        Check(engine.Advance(7000).Count == 2 && engine.Advance(17000).Count(c => c.Sound == 0) == 1,
            "A repeating timer should play its sounds again every cycle.");
        Console.WriteLine("Engine: alert timing, repeat, limits, multi-timer keys, key sets, disabled timers, sound offsets and resets passed.");
    }

    private static string Num(int slot) => $"Num{slot}";

    private static void HistorySmoke()
    {
        var history = new EditHistory<int>(50);
        for (int i = 0; i < 60; i++) history.Push(i);
        Check(history.UndoCount == 50, "History should keep 50 steps.");
        int state = 60, undone = 0;
        while (history.TryUndo(state, out state)) undone++;
        Check(undone == 50 && state == 10, $"Undo should stop at the oldest kept step (10), reached {state}.");
        Check(history.TryRedo(state, out state) && state == 11 && history.TryUndo(state, out state) && state == 10,
            "Redo and undo should round-trip.");
        history.TryRedo(state, out state);
        history.Push(state);
        Check(!history.CanRedo && history.CanUndo, "A new edit should clear the redo steps.");
        history.Clear();
        Check(!history.CanUndo && !history.CanRedo && !history.TryUndo(5, out state) && state == 5, "Clear should empty both stacks.");

        Check(new AppSettings().PauseHotkeysWhileTyping, "Hotkeys should pause while typing by default.");
        Check(!new AppSettings().DiscordCapture, "Discord capture should be off by default.");
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { PauseHotkeysWhileTyping = false, DiscordCapture = true },
            AppSettingsJsonContext.Default.AppSettings);
        Check(System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) is { PauseHotkeysWhileTyping: false, DiscordCapture: true },
            "PauseHotkeysWhileTyping and DiscordCapture should round-trip:\n" + json);
        // Properties missing from the file keep their defaults, also inside the overlay placements.
        string file = Path.Combine(Path.GetTempPath(), $"tipaura-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, "{}");
            var empty = AppSettingsStore.Load(file);
            Check(empty == AppSettings.Validate(new AppSettings()) && empty is { Volume: 0.8f, FontSizePx: 16, PauseHotkeysWhileTyping: true, DiscordCapture: false },
                "An empty settings file should load the defaults.");
            File.WriteAllText(file, """{ "Volume": 0.5, "Timeline": { "Width": 400 } }""");
            var partial = AppSettingsStore.Load(file);
            Check(partial is { Volume: 0.5f, MaxSounds: AudioEngine.DefaultMaxVoices, TtsRate: 1f, AlertSeconds: 3f, MaxTimelineBars: 8 } &&
                partial.Timeline is { Width: 400, Height: 260, FontSize: 18, Visible: true, ClickThrough: true, BackgroundOpacity: 0.35f },
                "Missing settings properties should keep their defaults.");
            // Monochrome colors: one set per theme, invalid values fall back per field, a missing set to the theme default.
            File.WriteAllText(file, """{ "TimelineMonochrome": true, "MonochromeDark": { "Text": "#112233", "Fill": "red" }, "MaxSoundSeconds": 500 }""");
            var mono = AppSettingsStore.Load(file);
            Check(mono is { TimelineMonochrome: true, MaxSoundSeconds: AudioEngine.MaxPlaySecondsLimit } &&
                mono.MonochromeDark == TimelineColors.DefaultDark with { Text = "#112233" } && mono.MonochromeLight == TimelineColors.DefaultLight &&
                mono.MonochromeColors == mono.MonochromeDark && (mono with { Theme = 1 }).MonochromeColors == mono.MonochromeLight,
                "Monochrome colors should validate per field and follow the theme.");
            Check(empty is { TimelineMonochrome: false, MaxSoundSeconds: AudioEngine.DefaultMaxPlaySeconds } &&
                AppSettings.Validate(new AppSettings { MaxSoundSeconds = float.NaN, MonochromeLight = null! }) is { MaxSoundSeconds: AudioEngine.DefaultMaxPlaySeconds } valid &&
                valid.MonochromeLight == TimelineColors.DefaultLight && AppSettings.Validate(new AppSettings { MaxSoundSeconds = 0 }).MaxSoundSeconds == 1,
                "Max sound length and monochrome defaults.");
            var light = new AppSettings { MonochromeLight = TimelineColors.DefaultLight with { Flash = "#00FF0080" } };
            Check(System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(light, AppSettingsJsonContext.Default.AppSettings),
                AppSettingsJsonContext.Default.AppSettings)!.MonochromeLight == light.MonochromeLight, "Monochrome colors should round-trip.");
        }
        finally { File.Delete(file); }
        Console.WriteLine("Editor history and settings defaults passed.");
    }

    internal static void TtsSmoke()
    {
        var voices = TtsSynthesizer.Voices();
        foreach (var voice in voices) Console.WriteLine($"Voice: {voice.Language} {voice.Name}");
        Check(voices.Count > 0, "No Windows TTS voices are installed.");
        string? chinese = voices.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))?.Id;
        long start = Stopwatch.GetTimestamp();
        var clip = AudioDecoders.TryDecodeWav(TtsSynthesizer.SynthesizeWav("龍息 五秒", chinese, 1.5));
        Check(clip is { Seconds: > 0.3 and < 10 }, "TTS produced no usable audio.");
        Console.WriteLine($"TTS: {clip!.Seconds:F2} s, {clip.SampleRate} Hz x{clip.Channels} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms.");
    }

    internal static void DecodeSmoke()
    {
        var wav = AudioDecoders.DecodeFile(ProjectPath("data/sfx/chime.wav"));
        Check(wav is { SampleRate: 44100, Channels: 1 } && Math.Abs(wav.Seconds - 1.02) < 0.01, $"WAV: {wav.Seconds} s");
        Check(wav.Samples.Max() is > 0.3f and <= 1f, "WAV samples were not normalized.");
        var mediaFoundationWav = AudioDecoders.DecodeMediaFoundation(ProjectPath("data/sfx/chime.wav"));
        Check(Math.Abs(mediaFoundationWav.Seconds - wav.Seconds) < 0.01, "Media Foundation WAV length differs from the parser.");
        foreach (var (file, channels) in new[] { ("chime.ogg", 1), ("chime.mp3", 1), ("chime-stereo.m4a", 2) })
        {
            var clip = AudioDecoders.DecodeFile(ProjectPath("SelfTests/fixtures/" + file));
            // Lossy codecs add encoder padding, so allow a little extra length.
            Check(clip.Channels == channels && clip.Seconds is > 1.0 and < 1.15 && clip.Samples.Max() > 0.2f,
                $"{file}: {clip.Seconds:F3} s x{clip.Channels}");
            Console.WriteLine($"Decoded {file}: {clip.Seconds:F3} s, {clip.SampleRate} Hz x{clip.Channels}.");
            // The same file from memory, as a pack entry is decoded (Media Foundation through an IMFByteStream).
            var memory = AudioDecoders.Decode(Path.GetExtension(file), File.ReadAllBytes(ProjectPath("SelfTests/fixtures/" + file)));
            Check(memory.Channels == clip.Channels && memory.SampleRate == clip.SampleRate && Math.Abs(memory.Seconds - clip.Seconds) < 0.01,
                $"{file} from memory: {memory.Seconds:F3} s x{memory.Channels}");
        }
        var packed = AudioDecoders.DecodeFile(EncounterYaml.Load(ProjectPath("SelfTests/fixtures/demo.zip")).Encounter!.Resolve("sfx/chime.mp3")!);
        Check(packed.Seconds is > 1.0 and < 1.15, $"chime.mp3 from demo.zip: {packed.Seconds:F3} s");
        try
        {
            AudioDecoders.DecodeFile(ProjectPath("SelfTests/fixtures/chime-opus.ogg"));
            throw new InvalidOperationException("Opus in Ogg should be rejected.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException { Message: "Opus in Ogg should be rejected." })
        {
            Console.WriteLine($"Opus rejected: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine("Decode smoke passed.");
    }

    // Plays at zero volume, so nothing is audible.
    internal static void AudioSmoke()
    {
        using var audio = new AudioEngine { Volume = 0, MaxVoices = 2 };
        var clip = AudioDecoders.DecodeFile(ProjectPath("data/sfx/chime.wav"));
        for (int i = 0; i < 4; i++) audio.Play(clip);
        Check(audio.Error is null, "Audio error: " + audio.Error);
        Check(audio.ActiveCount == 2, $"The voice cap should keep 2 sounds, found {audio.ActiveCount}.");
        audio.MaxVoices = 1;
        Check(audio.ActiveCount == 1, "Lowering the cap should stop the oldest sound.");
        long start = Stopwatch.GetTimestamp();
        while (audio.ActiveCount > 0 && Stopwatch.GetElapsedTime(start).TotalSeconds < 3) Thread.Sleep(20);
        Check(audio.ActiveCount == 0, "Finished sounds should be released.");
        audio.Play(clip);
        audio.StopAll();
        Check(audio.ActiveCount == 0, "StopAll should stop every sound.");
        // The max playback length cuts a long clip (3 s of silence) short.
        audio.MaxPlaySeconds = 0.2f;
        var silence = new AudioClip(new float[44100 * 3], 44100, 1);
        start = Stopwatch.GetTimestamp();
        audio.Play(silence);
        while (audio.ActiveCount > 0 && Stopwatch.GetElapsedTime(start).TotalSeconds < 3) Thread.Sleep(10);
        double cut = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Check(audio.Error is null && audio.ActiveCount == 0 && cut < 1, $"A 3 s clip should stop after 0.2 s, took {cut:F2} s.");
        Console.WriteLine($"Audio smoke passed (voice cap, oldest-first stop, release after playback, cut-off after {cut:F2} s).");
    }

    // The real scheduler thread with real time: alert latency against the expected instant.
    internal static void SchedulerSmoke()
    {
        using var audio = new AudioEngine { Volume = 0 };
        var sounds = new SoundCache();
        using var scheduler = new TimerScheduler(audio, sounds, installHook: false);
        var timer = new TimerDefinition { Id = "t", Name = "t", Keys = KeySet.Of(1), Duration = 0.6, WarnBefore = 0.2 };
        var repeat = new TimerDefinition { Id = "r", Name = "r", Keys = KeySet.Of(2), Duration = 0.25, Repeat = true };
        scheduler.Load(new Encounter { Timers = [timer, repeat], BaseDirectory = ProjectPath("data") });
        var errors = new List<double>();
        for (int round = 0; round < 5; round++)
        {
            long started = Stopwatch.GetTimestamp();
            scheduler.Start(1);
            var alert = WaitAlert(scheduler, 2);
            double error = (alert.At - started) / (double)Stopwatch.Frequency - 0.4;
            errors.Add(error * 1000);
            Check(alert.Timer == timer, "Wrong timer alerted.");
            long waitEnd = Stopwatch.GetTimestamp();
            while (scheduler.Snapshot.Length > 0 && Stopwatch.GetElapsedTime(waitEnd).TotalSeconds < 2) Thread.Sleep(5);
            Check(scheduler.Snapshot.Length == 0, "Timer did not end.");
        }
        Console.WriteLine($"Scheduler alert error (ms): {string.Join(", ", errors.Select(e => e.ToString("F2")))}");
        Check(errors.All(e => e is >= -1 and < 20), "Alert timing is outside 0-20 ms.");

        scheduler.Start(2);
        long repeatStart = Stopwatch.GetTimestamp();
        for (int cycle = 1; cycle <= 3; cycle++)
        {
            var alert = WaitAlert(scheduler, 2);
            double drift = (alert.At - repeatStart) / (double)Stopwatch.Frequency - 0.25 * cycle;
            Check(drift is > -0.001 and < 0.02, $"Repeat cycle {cycle} drifted {drift * 1000:F1} ms.");
        }
        scheduler.ResetSlot(2);
        Thread.Sleep(50);
        Check(scheduler.Snapshot.Length == 0, "ResetSlot did not stop the repeating timer.");
        Console.WriteLine("Scheduler smoke passed (alert latency, repeat without drift, reset).");

        static AlertEvent WaitAlert(TimerScheduler scheduler, double seconds)
        {
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds)
            {
                if (scheduler.TryTakeAlert(out var alert)) return alert;
                Thread.Sleep(1);
            }
            throw new InvalidOperationException("No alert arrived.");
        }
    }
}
#endif
