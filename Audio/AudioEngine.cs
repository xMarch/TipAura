using Vortice.Multimedia;
using Vortice.XAudio2;

// XAudio2 output with a cap on simultaneous sounds: starting one more stops the oldest.
// Thread-safe; the scheduler plays alerts while the UI changes volume or previews sounds.
internal sealed class AudioEngine : IDisposable
{
    internal const int DefaultMaxVoices = 10, MaxVoiceLimit = 32;
    internal const float DefaultMaxPlaySeconds = 10f, MaxPlaySecondsLimit = 120f;

    private sealed record Playing(IXAudio2SourceVoice Voice, AudioClip Clip);

    private readonly object _gate = new();
    private readonly List<Playing> _playing = [];
    private IXAudio2? _engine;
    private IXAudio2MasteringVoice? _master;
    private volatile bool _deviceLost;
    private float _volume = 1f;
    private int _maxVoices = DefaultMaxVoices;
    private float _maxPlaySeconds = DefaultMaxPlaySeconds;
    private bool _disposed;

    internal string? Error { get; private set; }

    internal int ActiveCount { get { lock (_gate) { Reap(); return _playing.Count; } } }

    internal float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                _master?.SetVolume(_volume);
            }
        }
    }

    internal int MaxVoices
    {
        get { lock (_gate) return _maxVoices; }
        set
        {
            lock (_gate)
            {
                _maxVoices = Math.Clamp(value, 1, MaxVoiceLimit);
                Reap();
                while (_playing.Count > _maxVoices) StopOldest();
            }
        }
    }

    // Each clip stops after this many seconds (XAudio2 PlayLength); applies from the next Play.
    internal float MaxPlaySeconds
    {
        get { lock (_gate) return _maxPlaySeconds; }
        set { lock (_gate) _maxPlaySeconds = Math.Clamp(value, 0.05f, MaxPlaySecondsLimit); }
    }

    internal void Play(AudioClip clip)
    {
        lock (_gate)
        {
            if (_disposed || !EnsureEngine()) return;
            Reap();
            while (_playing.Count >= _maxVoices) StopOldest();
            try
            {
                var voice = _engine!.CreateSourceVoice(WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, clip.Channels), false);
                long frames = clip.Samples.Length / clip.Channels;
                long limit = (long)(_maxPlaySeconds * clip.SampleRate);
                unsafe
                {
                    fixed (float* samples = clip.Samples)
                    {
                        var buffer = new AudioBuffer((nint)samples, (uint)(clip.Samples.Length * sizeof(float)), BufferFlags.EndOfStream);
                        // 0 plays the whole buffer.
                        if (limit < frames) buffer.PlayLength = (uint)Math.Max(1, limit);
                        voice.SubmitSourceBuffer(buffer, null).CheckError();
                    }
                }
                voice.Start().CheckError();
                _playing.Add(new(voice, clip));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                AppLog.Error("Audio", "Could not play sound", ex);
            }
        }
    }

    internal void StopAll()
    {
        lock (_gate)
            while (_playing.Count > 0) StopOldest();
    }

    // The device was removed or changed: rebuild on the next Play. Called on the XAudio2 thread, which
    // must not destroy voices itself.
    private void OnCriticalError(object? sender, Vortice.XAudio2.ErrorEventArgs e)
    {
        _deviceLost = true;
        AppLog.Warn("Audio", $"XAudio2 critical error {e.ErrorCode}; the engine is rebuilt on the next sound.");
    }

    private bool EnsureEngine()
    {
        if (_deviceLost) ReleaseEngine();
        if (_engine is not null) return true;
        try
        {
            _engine = XAudio2.XAudio2Create(ProcessorSpecifier.UseDefaultProcessor, registerCallback: true);
            _engine.CriticalError += OnCriticalError;
            _master = _engine.CreateMasteringVoice(0, 0, AudioStreamCategory.GameEffects);
            _master.SetVolume(_volume);
            Error = null;
            AppLog.Info("Audio", "XAudio2 engine created.");
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            AppLog.Error("Audio", "Could not create the XAudio2 engine", ex);
            ReleaseEngine();
            return false;
        }
    }

    // Drops finished voices. Must hold _gate.
    private void Reap()
    {
        for (int i = _playing.Count - 1; i >= 0; i--)
            if (_playing[i].Voice.StateNoSamplesPlayed.BuffersQueued == 0)
            {
                _playing[i].Voice.DestroyVoice();
                _playing[i].Voice.Dispose();
                _playing.RemoveAt(i);
            }
    }

    private void StopOldest()
    {
        var oldest = _playing[0];
        _playing.RemoveAt(0);
        // DestroyVoice waits for the audio thread, so the clip's samples are no longer read afterwards.
        oldest.Voice.Stop();
        oldest.Voice.DestroyVoice();
        oldest.Voice.Dispose();
    }

    private void ReleaseEngine()
    {
        while (_playing.Count > 0) StopOldest();
        _master?.DestroyVoice();
        _master?.Dispose();
        _master = null;
        if (_engine is not null) _engine.CriticalError -= OnCriticalError;
        _engine?.Dispose();
        _engine = null;
        _deviceLost = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ReleaseEngine();
        }
    }
}
