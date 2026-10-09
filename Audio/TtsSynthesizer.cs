using Windows.Media.SpeechSynthesis;

internal sealed record TtsVoice(string Id, string Name, string Language);

// Windows OneCore speech through the WinRT projection. Synthesis happens ahead of time on worker
// threads, so the scheduler only ever plays cached PCM.
internal static class TtsSynthesizer
{
    internal static IReadOnlyList<TtsVoice> Voices()
    {
        var voices = new List<TtsVoice>();
        foreach (var voice in SpeechSynthesizer.AllVoices)
            voices.Add(new TtsVoice(voice.Id, voice.DisplayName, voice.Language));
        return voices;
    }

    // Returns a RIFF/WAVE file. An unknown or missing voice id falls back to the system default voice.
    internal static byte[] SynthesizeWav(string text, string? voiceId = null, double rate = 1.0)
    {
        using var synthesizer = new SpeechSynthesizer();
        if (voiceId is not null)
            foreach (var voice in SpeechSynthesizer.AllVoices)
                if (voice.Id == voiceId) { synthesizer.Voice = voice; break; }
        synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 6.0);
        using var stream = synthesizer.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
        using var input = stream.AsStreamForRead();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
