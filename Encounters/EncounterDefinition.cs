using System.Collections;
using System.Collections.Immutable;

// One encounter file: a list of countdowns started by the key slots 1~9 (bound to keys in the local settings).
// The schema and validation rules are specified in TECHNICAL.md ("Encounter YAML").
internal enum LimitAction { ReplaceOldest, Ignore, Reset }

// The key slots that start a timer, as a bit set so records holding it keep value equality.
internal readonly record struct KeySet(ushort Mask)
{
    internal static KeySet Of(params int[] slots)
    {
        ushort mask = 0;
        foreach (int slot in slots)
            if (slot is >= TimerDefinition.MinKey and <= TimerDefinition.MaxKey) mask |= (ushort)(1 << slot);
        return new(mask);
    }

    internal bool Contains(int slot) => slot is >= 0 and < 16 && (Mask & (1 << slot)) != 0;
    internal KeySet With(int slot, bool on) => new(on ? (ushort)(Mask | 1 << slot) : (ushort)(Mask & ~(1 << slot)));
    internal bool IsEmpty => Mask == 0;
    internal int Count => System.Numerics.BitOperations.PopCount(Mask);
    internal IEnumerable<int> Slots => Enumerable.Range(TimerDefinition.MinKey, TimerDefinition.MaxKey).Where(Contains);
    // Sorting key; 10 for an empty set so it sorts last.
    internal int Lowest => IsEmpty ? 10 : System.Numerics.BitOperations.TrailingZeroCount(Mask);
}

// An immutable list with sequence equality, so records holding one compare by content.
internal sealed class ValueList<T>(IEnumerable<T> items) : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items = [.. items];
    internal static ValueList<T> Empty { get; } = new([]);

    public T this[int index] => _items[index];
    public int Count => _items.Length;
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    public bool Equals(ValueList<T>? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }

    internal ValueList<T> SetItem(int index, T item) => new(_items.Select((value, i) => i == index ? item : value));
    internal ValueList<T> RemoveAt(int index) => new(_items.Where((_, i) => i != index));
    internal ValueList<T> Add(T item) => new([.. _items, item]);
}

internal sealed record TimerSound
{
    internal const double MaxOffset = TimerDefinition.MaxDuration;

    // Exactly one of Tts and Sfx is set. Sfx is stored as written (relative to the YAML folder).
    public string? Tts { get; init; }
    public string? Sfx { get; init; }
    // Seconds relative to the timer's alert point (end - warn_before); negative plays earlier.
    public double Offset { get; init; }
}

internal sealed record TimerDefinition
{
    internal const int MinKey = 1, MaxKey = 9, MaxInstanceLimit = 20, MaxSounds = 16, MaxNoteLength = 1024;
    internal const double MaxDuration = 24 * 60 * 60;

    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public KeySet Keys { get; init; } = KeySet.Of(1);
    public double Duration { get; init; } = 10;
    public bool Repeat { get; init; }
    public int MaxInstances { get; init; } = 1;
    public LimitAction OnLimit { get; init; } = LimitAction.ReplaceOldest;
    public double WarnBefore { get; init; }
    public ValueList<TimerSound> Sounds { get; init; } = ValueList<TimerSound>.Empty;
    public string? Icon { get; init; }
    public string? Message { get; init; }
    public string? Color { get; init; }
    public string? Provider { get; init; }
    public string? Note { get; init; }

    internal string DisplayName => Name.Length > 0 ? Name : Id;
    internal string AlertText => string.IsNullOrEmpty(Message) ? DisplayName : Message;
    // Seconds before expiry at which the alert fires; a warning longer than the timer fires at start.
    internal double AlertLead => Math.Min(WarnBefore, Duration);

    // Seconds after start at which a sound plays: the alert point shifted by the sound's offset,
    // kept inside the countdown.
    internal double SoundTime(TimerSound sound) => Math.Clamp(Duration - AlertLead + sound.Offset, 0, Duration);
    internal bool SoundClamped(TimerSound sound) => Duration - AlertLead + sound.Offset is var t && (t < 0 || t > Duration);

    // {name} and {sec} are expanded once at load, so every TTS phrase can be synthesized ahead of time.
    // {sec} is the time left when this sound plays (the warn-before seconds for offset 0).
    internal string? ExpandedTts(TimerSound sound) => sound.Tts?.Replace("{name}", DisplayName, StringComparison.Ordinal)
        .Replace("{sec}", FormatSeconds(Duration - SoundTime(sound)), StringComparison.Ordinal);

    internal static string FormatSeconds(double seconds) =>
        seconds.ToString(seconds == Math.Floor(seconds) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

    // Notes are limited in characters (Unicode scalar values), not UTF-16 units or bytes.
    internal static string LimitNote(string note, out bool truncated)
    {
        int count = 0, length = 0;
        foreach (var rune in note.EnumerateRunes())
        {
            if (++count > MaxNoteLength) { truncated = true; return note[..length]; }
            length += rune.Utf16SequenceLength;
        }
        truncated = false;
        return note;
    }

    internal static int NoteLength(string note) => note.EnumerateRunes().Count();
}

internal sealed record Encounter
{
    // Optional for loose files; a pack's index.yaml must declare the zip's file name.
    public string? Id { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<TimerDefinition> Timers { get; init; } = [];
    // Folder that relative sfx and icon paths resolve against (for a pack, the folder holding the zip); not serialized.
    public string BaseDirectory { get; init; } = "";
    // Set when the encounter was loaded from a pack: its assets come only from that zip.
    public EncounterPack? Pack { get; init; }
    // Files added to the pack in the editor and not yet saved: entry name ("sfx/a.ogg") to the full path of
    // the source file. Saving the pack writes them into the zip.
    public ImmutableDictionary<string, string> PackFiles { get; init; } = NoPackFiles;
    internal static readonly ImmutableDictionary<string, string> NoPackFiles =
        ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

    // A full path, or for a pack the asset key "<zip>|<entry>" (EncounterPack.ReadAsset). A `lucide:` icon
    // is returned as it is (IconCache draws it). Null when the path is empty or leaves the pack.
    internal string? Resolve(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (Lucide.IsIcon(relative)) return relative.Trim();
        if (Pack is { } pack)
        {
            if (EncounterPack.EntryName(relative) is not { } entry) return null;
            return PackFiles.TryGetValue(entry, out var added) ? added : pack.Key(pack.Find(relative) ?? entry);
        }
        return Path.GetFullPath(Path.Combine(BaseDirectory, relative));
    }

    // The pack's assets in one folder (EncounterPack.SoundFolder or IconFolder), added files included, by name.
    internal IReadOnlyList<string> PackAssets(string folder)
    {
        if (Pack is not { } pack) return [];
        return [.. pack.Names.Where(name => !PackFiles.ContainsKey(name)).Concat(PackFiles.Keys)
            .Where(name => EncounterPack.AssetFolder(name) == folder).Order(StringComparer.OrdinalIgnoreCase)];
    }
}

// An encounter file found below data/: a loose YAML file or a pack zip. Label is the path relative to data/.
internal sealed record EncounterSource(string Path, string Label, bool IsPack);

internal readonly record struct EncounterIssue(bool IsError, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"L{Line}: {Message}" : Message;
}
