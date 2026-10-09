using System.Collections.Concurrent;
using System.Globalization;

// Decoded clips keyed by what produced them, so identical phrases or files are prepared once and
// alerts never wait for synthesis or decoding. Thread-safe.
internal sealed class SoundCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<AudioClip>>> _clips = new(StringComparer.Ordinal);
    private volatile string? _voiceId;
    private double _rate = 1.0;

    // Changing the voice or rate changes the TTS keys, so old phrases are simply no longer used.
    internal void ConfigureTts(string? voiceId, double rate)
    {
        _voiceId = voiceId;
        Volatile.Write(ref _rate, rate);
    }

    // Forgets every clip, so edited sound files are read again.
    internal void Clear() => _clips.Clear();

    internal void Warm(Encounter encounter)
    {
        foreach (var timer in encounter.Timers)
            foreach (var sound in timer.Sounds) _ = Request(encounter, timer, sound);
    }

    internal Task<AudioClip>? Request(Encounter encounter, TimerDefinition timer, TimerSound sound)
    {
        if (Key(encounter, timer, sound) is not { } key) return null;
        return _clips.GetOrAdd(key, k => new Lazy<Task<AudioClip>>(() => Task.Run(() => Produce(k)))).Value;
    }

    // Null while the clip is still being prepared or when it failed.
    internal AudioClip? TryGetReady(Encounter encounter, TimerDefinition timer, TimerSound sound) =>
        Request(encounter, timer, sound) is { IsCompletedSuccessfully: true } task ? task.Result : null;

    internal string? ErrorFor(Encounter encounter, TimerDefinition timer, TimerSound sound) =>
        Request(encounter, timer, sound) is { IsFaulted: true } task ? task.Exception!.GetBaseException().Message : null;

    internal (int Ready, int Pending, int Failed) Status(Encounter encounter)
    {
        int ready = 0, pending = 0, failed = 0;
        foreach (var timer in encounter.Timers)
            foreach (var sound in timer.Sounds)
                switch (Request(encounter, timer, sound))
                {
                    case null: break;
                    case { IsCompletedSuccessfully: true }: ready++; break;
                    case { IsCompleted: true }: failed++; break;
                    default: pending++; break;
                }
        return (ready, pending, failed);
    }

    // The TTS key uses the expanded text, so timers sharing a phrase share its clip.
    private string? Key(Encounter encounter, TimerDefinition timer, TimerSound sound)
    {
        if (sound.Sfx is not null)
        {
            try { return encounter.Resolve(sound.Sfx) is { } path ? "sfx\n" + path : null; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        if (timer.ExpandedTts(sound) is { Length: > 0 } text)
            return string.Create(CultureInfo.InvariantCulture, $"tts\n{_voiceId}\n{Volatile.Read(ref _rate):0.##}\n{text}");
        return null;
    }

    private static AudioClip Produce(string key)
    {
        string[] parts = key.Split('\n', 4);
        try
        {
            if (parts[0] == "sfx") return AudioDecoders.DecodeFile(parts[1]);
            byte[] wav = TtsSynthesizer.SynthesizeWav(parts[3], parts[1].Length > 0 ? parts[1] : null,
                double.Parse(parts[2], CultureInfo.InvariantCulture));
            return AudioDecoders.TryDecodeWav(wav) ?? throw new InvalidDataException("Unexpected TTS audio format.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Audio", $"Could not prepare {(parts[0] == "sfx" ? Path.GetFileName(parts[1].Replace(EncounterPack.KeySeparator, '/')) : "TTS phrase")}: {ex.Message}");
            throw;
        }
    }
}
