#if TIPAURA_AGENT_SELF_TEST
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class KeybindSmoke
{
    private const int Alt = 0xA4, Ctrl = 0xA2, Shift = 0xA0, RightCtrl = 0xA3, RightAlt = 0xA5, F13 = 0x7C;

    // The pure state machine: every fixed binding, both NumLock states and the ignored combinations.
    internal static void Processor()
    {
        var p = new KeybindProcessor();
        for (int slot = 1; slot <= 9; slot++)
        {
            Expect(p.Process(0x60 + slot, false, true), new(HotkeyKind.Start, slot), $"Numpad{slot}");
            p.Process(0x60 + slot, false, false);
        }
        Expect(p.Process(0x60, false, true), null, "Numpad0 alone");
        p.Process(0x60, false, false);

        Expect(p.Process(0x61, false, true), new(HotkeyKind.Start, 1), "press");
        Expect(p.Process(0x61, false, true), null, "auto-repeat while held");
        p.Process(0x61, false, false);

        p.Process(Alt, false, true);
        Expect(p.Process(0x63, false, true), new(HotkeyKind.ResetSlot, 3), "Alt+Numpad3");
        p.Process(0x63, false, false);
        Expect(p.Process(0x60, false, true), new(HotkeyKind.ResetAll, 0), "Alt+Numpad0");
        p.Process(0x60, false, false);
        p.Process(Alt, false, false);
        // Right Alt arrives as an extended key.
        p.Process(0xA5, true, true);
        Expect(p.Process(0x69, false, true), new(HotkeyKind.ResetSlot, 9), "RightAlt+Numpad9");
        p.Process(0x69, false, false);
        p.Process(0xA5, true, false);

        foreach (int modifier in new[] { Ctrl, Shift, 0x5B })
        {
            p.Process(modifier, false, true);
            Expect(p.Process(0x61, false, true), null, $"modifier 0x{modifier:X2}+Numpad1");
            p.Process(0x61, false, false);
            p.Process(modifier, false, false);
        }

        // NumLock off: the keypad sends navigation keys without the extended flag.
        int[] numLockOff = [0x2D, 0x23, 0x28, 0x22, 0x25, 0x0C, 0x27, 0x24, 0x26, 0x21];
        for (int slot = 1; slot <= 9; slot++)
        {
            Expect(p.Process(numLockOff[slot], false, true), new(HotkeyKind.Start, slot), $"NumLock-off keypad {slot}");
            p.Process(numLockOff[slot], false, false);
            // The dedicated navigation cluster sets the extended flag and is not a hotkey.
            Expect(p.Process(numLockOff[slot], true, true), null, $"navigation key 0x{numLockOff[slot]:X2}");
            p.Process(numLockOff[slot], true, false);
        }
        p.Process(Alt, false, true);
        Expect(p.Process(0x2D, false, true), new(HotkeyKind.ResetAll, 0), "Alt+keypad Insert (NumLock off)");
        p.Process(0x2D, false, false);
        p.Process(Alt, false, false);

        // A key held when the hook starts must be released before it can fire.
        var seeded = new KeybindProcessor();
        seeded.SeedKey(0x62);
        Expect(seeded.Process(0x62, false, true), null, "seeded held key");
        seeded.Process(0x62, false, false);
        Expect(seeded.Process(0x62, false, true), new(HotkeyKind.Start, 2), "after release");
        Configured();
        Bindings();
        Console.WriteLine("Hotkey processor: default and configured keys, modifiers, sides, NumLock off, held keys and validation passed.");
    }

    // Custom keys and modifiers with exact, side-aware matching.
    private static void Configured()
    {
        var p = new KeybindProcessor
        {
            Bindings = new HotkeyBindings().WithKey(1, F13) with
            {
                StartModifiers = new() { Ctrl = ModifierSide.Left },
                ResetModifiers = new() { Alt = ModifierSide.Right }
            }
        };
        HotkeyEvent? Chord(int[] modifiers, int key)
        {
            foreach (int m in modifiers) p.Process(m, m is RightCtrl or RightAlt, true);
            var result = p.Process(key, false, true);
            p.Process(key, false, false);
            foreach (int m in modifiers) p.Process(m, m is RightCtrl or RightAlt, false);
            return result;
        }
        Expect(Chord([Ctrl], F13), new(HotkeyKind.Start, 1), "LCtrl+F13");
        Expect(Chord([], F13), null, "F13 without the start modifier");
        Expect(Chord([RightCtrl], F13), null, "RCtrl+F13 (left required)");
        Expect(Chord([Ctrl, Shift], F13), null, "LCtrl+Shift+F13 (extra modifier)");
        Expect(Chord([Ctrl], 0x61), null, "LCtrl+Numpad1 after rebinding key 1");
        Expect(Chord([Ctrl], 0x0C), new(HotkeyKind.Start, 5), "LCtrl+keypad 5 with NumLock off");
        Expect(Chord([RightAlt], F13), new(HotkeyKind.ResetSlot, 1), "RAlt+F13");
        Expect(Chord([Alt], F13), null, "LAlt+F13 (right required)");
        Expect(Chord([RightAlt], 0x60), new(HotkeyKind.ResetAll, 0), "RAlt+Numpad0");
        Expect(Chord([], 0x60), null, "Numpad0 alone with a reset modifier");

        // Either side.
        p.Bindings = new HotkeyBindings { ResetModifiers = new() { Shift = ModifierSide.Either } };
        Expect(Chord([0xA1], 0x63), new(HotkeyKind.ResetSlot, 3), "RShift+Numpad3");
        Expect(Chord([Shift, 0xA1], 0x63), new(HotkeyKind.ResetSlot, 3), "both Shifts+Numpad3");
        Expect(Chord([], 0x63), new(HotkeyKind.Start, 3), "Numpad3");
        Expect(Chord([Shift], 0x60), new(HotkeyKind.ResetAll, 0), "Shift+Numpad0");
        p.Bindings = p.Bindings with { StartModifiers = new() { Ctrl = ModifierSide.Either } };
        Expect(Chord([RightCtrl], 0x63), new(HotkeyKind.Start, 3), "RCtrl+Numpad3 (either side)");
        // An empty reset modifier works like an empty start modifier: the bare keys reset.
        p.Bindings = p.Bindings with { ResetModifiers = new() };
        Expect(Chord([], 0x63), new(HotkeyKind.ResetSlot, 3), "Numpad3 with an empty reset modifier");
        Expect(Chord([], 0x60), new(HotkeyKind.ResetAll, 0), "Numpad0 with an empty reset modifier");
        Expect(Chord([Ctrl], 0x60), null, "Ctrl+Numpad0 with an empty reset modifier");
    }

    private static void Bindings()
    {
        ModifierCombo either = new() { Ctrl = ModifierSide.Either }, left = new() { Ctrl = ModifierSide.Left },
            right = new() { Ctrl = ModifierSide.Right }, none = new(), alt = new() { Alt = ModifierSide.Either };
        SelfTest.Check(either.Overlaps(left) && either.Overlaps(right) && !left.Overlaps(right) && !none.Overlaps(either) &&
            none.Overlaps(new ModifierCombo()) && !alt.Overlaps(either) && alt.Overlaps(alt), "Modifier overlap is wrong.");
        SelfTest.Check(new ModifierCombo { Ctrl = ModifierSide.Left, Alt = ModifierSide.Either, Shift = ModifierSide.Right }.Label == "LCtrl+Alt+RShift",
            "Modifier label is wrong.");
        var defaults = new HotkeyBindings();
        SelfTest.Check(HotkeyBindings.Validate(null) == defaults && HotkeyBindings.Validate(defaults) == defaults, "Default bindings should validate.");
        SelfTest.Check(HotkeyBindings.Validate(defaults.WithKey(2, 0x61)).Keys.SequenceEqual(defaults.Keys), "Duplicate keys should fall back to the defaults.");
        SelfTest.Check(HotkeyBindings.Validate(defaults.WithKey(2, 0xA2)).Keys.SequenceEqual(defaults.Keys), "A modifier key should fall back to the defaults.");
        SelfTest.Check(HotkeyBindings.Validate(defaults.WithKey(2, 0x0C)).Keys.SequenceEqual(defaults.Keys), "An unnormalized keypad key should fall back.");
        SelfTest.Check(HotkeyBindings.Validate(defaults with { Keys = [0x60] }).Keys.SequenceEqual(defaults.Keys), "A wrong key count should fall back.");
        SelfTest.Check(HotkeyBindings.Validate(defaults.WithKey(2, F13)).Keys[2] == F13, "A valid custom key should be kept.");
        var overlap = HotkeyBindings.Validate(defaults with { StartModifiers = new() { Alt = ModifierSide.Left } });
        SelfTest.Check(overlap.StartModifiers == defaults.StartModifiers && overlap.ResetModifiers == defaults.ResetModifiers,
            "Overlapping modifiers should fall back to the defaults.");
        SelfTest.Check(HotkeyBindings.Validate(defaults with { ResetModifiers = new() { Ctrl = (ModifierSide)9 } }).ResetModifiers == defaults.ResetModifiers,
            "An unknown modifier side should fall back.");
        SelfTest.Check(defaults.WithKey(1, F13) != defaults && defaults.WithKey(1, 0x61) == defaults, "Bindings should compare by content.");

        // Settings JSON: missing Hotkeys keeps the defaults; custom bindings round trip.
        var empty = System.Text.Json.JsonSerializer.Deserialize("{}", AppSettingsJsonContext.Default.AppSettings);
        SelfTest.Check(AppSettings.Validate(empty).Hotkeys == defaults, "Settings without Hotkeys should use the default bindings.");
        var custom = new AppSettings
        {
            Hotkeys = defaults.WithKey(4, F13) with { StartModifiers = new() { Shift = ModifierSide.Right } }
        };
        string text = System.Text.Json.JsonSerializer.Serialize(custom, AppSettingsJsonContext.Default.AppSettings);
        var back = AppSettings.Validate(System.Text.Json.JsonSerializer.Deserialize(text, AppSettingsJsonContext.Default.AppSettings));
        SelfTest.Check(back.Hotkeys == custom.Hotkeys && text.Contains("\"Right\"", StringComparison.Ordinal), "Hotkey settings did not round trip:\n" + text);
    }

    // The real low-level hook. Uses the keypad 5 key with NumLock off (VK_CLEAR, non-extended), which does
    // not type into the focused window, instead of Numpad digits.
    internal static void Run()
    {
        Processor();
        using var listener = new GlobalKeybinds();
        if (listener.Error is not null) throw new InvalidOperationException($"Global hook installation failed: {listener.Error}");
        Press(0x0C);
        Expect(listener, new(HotkeyKind.Start, 5));
        try
        {
            Native.keybd_event(Alt, 0, 0, 0);
            Press(0x0C);
        }
        finally { Native.keybd_event(Alt, 0, 2, 0); }
        Expect(listener, new(HotkeyKind.ResetSlot, 5));

        // Capture swallows the key (so nothing reaches the focused window) and reports the Numpad form.
        listener.BeginCapture();
        Press(0x0C);
        long start = Stopwatch.GetTimestamp();
        int captured = -1;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 3 && !listener.TryTakeCaptured(out captured)) Thread.Sleep(5);
        if (captured != 0x65) throw new InvalidOperationException($"Capture should report Numpad5 (0x65), got 0x{captured:X}.");
        Thread.Sleep(50);
        if (listener.TryDequeue(out var leaked)) throw new InvalidOperationException($"A captured key should not fire a hotkey: {leaked}");
        Console.WriteLine("Global keyboard hook: Start, Alt reset and key capture delivered through the low-level hook.");
    }

    private static void Expect(HotkeyEvent? actual, HotkeyEvent? expected, string what)
    {
        if (actual != expected) throw new InvalidOperationException($"{what}: expected {expected}, got {actual}.");
    }

    private static void Press(byte key)
    {
        Native.keybd_event(key, 0, 0, 0);
        Native.keybd_event(key, 0, 2, 0);
    }

    private static void Expect(GlobalKeybinds listener, HotkeyEvent expected)
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 3)
        {
            if (listener.TryDequeue(out var value))
            {
                if (value != expected) throw new InvalidOperationException($"Unexpected hotkey event: {value}");
                return;
            }
            Thread.Sleep(5);
        }
        throw new InvalidOperationException($"No hotkey event arrived for {expected}.");
    }
}

internal static partial class Native
{
    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
#endif
