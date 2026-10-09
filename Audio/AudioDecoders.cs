using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NVorbis;
using Vortice.MediaFoundation;

// Interleaved float PCM. Samples live on the pinned object heap so XAudio2 can read them in place
// without a per-play copy.
internal sealed class AudioClip
{
    internal const double MaxSeconds = 120;

    internal AudioClip(float[] samples, int sampleRate, int channels)
    {
        if (sampleRate is < 1000 or > 384_000 || channels is < 1 or > 8)
            throw new InvalidDataException($"Unsupported audio format: {sampleRate} Hz, {channels} channel(s).");
        Samples = GC.AllocateUninitializedArray<float>(samples.Length, pinned: true);
        samples.CopyTo(Samples, 0);
        SampleRate = sampleRate;
        Channels = channels;
    }

    internal float[] Samples { get; }
    internal int SampleRate { get; }
    internal int Channels { get; }
    internal double Seconds => Samples.Length / (double)Channels / SampleRate;
}

internal static class AudioDecoders
{
    private static readonly Lazy<bool> s_mediaFoundation = new(() =>
    {
        MediaFactory.MFStartup(useLightVersion: true).CheckError();
        return true;
    });

    // A file path or a pack asset key (Encounter.Resolve).
    internal static AudioClip DecodeFile(string path)
    {
        if (EncounterPack.IsKey(path)) return Decode(Path.GetExtension(path).ToLowerInvariant(), EncounterPack.ReadAsset(path));
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".ogg") return DecodeOgg(path);
        if (extension == ".wav" && TryDecodeWav(File.ReadAllBytes(path)) is { } wav) return wav;
        // WAV encodings the parser does not handle (ADPCM, A-law...) also go through Media Foundation.
        return DecodeMediaFoundation(path);
    }

    // The same decoders on a file already in memory (a pack entry); extension is lowercase with the dot.
    internal static AudioClip Decode(string extension, byte[] data)
    {
        if (extension == ".ogg")
        {
            using var reader = new VorbisReader(new MemoryStream(data, writable: false), closeOnDispose: true);
            return DecodeOgg(reader);
        }
        if (extension == ".wav" && TryDecodeWav(data) is { } wav) return wav;
        _ = s_mediaFoundation.Value;
        using var stream = new MFByteStream(data);
        using var source = MediaFactory.MFCreateSourceReaderFromByteStream(stream, null);
        return DecodeMediaFoundation(source);
    }

    // PCM 8/16/24/32-bit integer and 32-bit float WAVE, including WAVE_FORMAT_EXTENSIBLE.
    // Returns null for other encodings.
    internal static AudioClip? TryDecodeWav(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        int format = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> samples = default;
        bool haveData = false;
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = data.Slice(offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            int start = offset + 8;
            // Streaming writers leave the size as 0 or -1; take the rest of the file for data.
            if (size < 0 || start + size > data.Length) size = data.Length - start;
            var body = data.Slice(start, size);
            if (id.SequenceEqual("fmt "u8) && body.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if (format == 0xFFFE && body.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
            }
            else if (id.SequenceEqual("data"u8)) { samples = body; haveData = true; }
            offset = start + size + (size & 1);
        }
        if (!haveData || channels == 0) throw new InvalidDataException("The WAVE file has no fmt or data chunk.");
        bool supported = format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits == 32;
        if (!supported) return null;
        int bytes = bits / 8;
        int count = samples.Length / bytes / channels * channels;
        CheckLength(count, rate, channels);
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            var sample = samples.Slice(i * bytes, bytes);
            output[i] = (format, bits) switch
            {
                (3, _) => BinaryPrimitives.ReadSingleLittleEndian(sample),
                (_, 8) => (sample[0] - 128) / 128f,
                (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                (_, 24) => (sample[0] | sample[1] << 8 | (sbyte)sample[2] << 16) / 8388608f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f
            };
        }
        return new AudioClip(output, rate, channels);
    }

    // Vorbis only; Opus in an Ogg container is rejected by NVorbis.
    internal static AudioClip DecodeOgg(string path)
    {
        using var reader = new VorbisReader(path);
        return DecodeOgg(reader);
    }

    private static AudioClip DecodeOgg(VorbisReader reader)
    {
        long total = reader.TotalSamples * reader.Channels;
        CheckLength(total, reader.SampleRate, reader.Channels);
        var output = new List<float>((int)Math.Max(0, total));
        var buffer = new float[reader.SampleRate * reader.Channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            output.AddRange(buffer.AsSpan(0, read));
            CheckLength(output.Count, reader.SampleRate, reader.Channels);
        }
        return new AudioClip([.. output], reader.SampleRate, reader.Channels);
    }

    // MP3, AAC/M4A, WMA and unusual WAV encodings through the Windows codecs. Windows N editions
    // without the Media Feature Pack have no AAC/MP3 decoders and fail here.
    internal static AudioClip DecodeMediaFoundation(string path)
    {
        _ = s_mediaFoundation.Value;
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        return DecodeMediaFoundation(reader);
    }

    private static AudioClip DecodeMediaFoundation(IMFSourceReader reader)
    {
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
        using (var requested = MediaFactory.MFCreateMediaType())
        {
            requested.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            requested.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, requested);
        }
        int rate, channels;
        using (var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream))
        {
            rate = (int)current.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
            channels = (int)current.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
        }
        var output = new List<float>();
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
                out _, out var flags, out _);
            if (sample is not null)
            {
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out nint pointer, out _, out int length);
                try
                {
                    unsafe { output.AddRange(new ReadOnlySpan<float>((void*)pointer, length / sizeof(float))); }
                }
                finally { buffer.Unlock(); }
                CheckLength(output.Count, rate, channels);
            }
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
        }
        return new AudioClip([.. output], rate, channels);
    }

    private static void CheckLength(long samples, int rate, int channels)
    {
        if (rate > 0 && channels > 0 && samples / (double)channels / rate > AudioClip.MaxSeconds)
            throw new InvalidDataException($"Audio is longer than {AudioClip.MaxSeconds:0} seconds.");
    }
}
