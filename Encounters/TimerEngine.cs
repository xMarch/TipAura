// A cue is due: Sound = -1 shows the timer's alert; otherwise play Timer.Sounds[Sound].
internal readonly record struct TimerCue(TimerDefinition Timer, long InstanceId, int Sound = -1);

// EndTicks is absolute, so readers compute the remaining time for their own frame from one snapshot.
internal sealed record TimerView(long InstanceId, TimerDefinition Timer, long EndTicks, bool Alerted)
{
    internal double Remaining(long now, long ticksPerSecond) => Math.Max(0, (EndTicks - now) / (double)ticksPerSecond);
}

// Pure countdown state. Times are Stopwatch-style ticks passed in by the caller, so tests drive it
// with a fake clock and the scheduler thread with Stopwatch.GetTimestamp(). Not thread-safe.
internal sealed class TimerEngine(long ticksPerSecond)
{
    private sealed class Instance
    {
        internal long Id;
        internal required TimerDefinition Timer;
        internal long Start, End, AlertAt;
        internal bool Alerted;
        // Sound indices sorted by play time, their absolute times, and the next one to play.
        internal required int[] SoundOrder;
        internal long[] SoundAt = [];
        internal int NextSound;
    }

    private readonly List<Instance> _instances = [];
    private long _nextId = 1;
    // Timer ids disabled for this session; kept across Load so editing the encounter keeps them.
    private IReadOnlySet<string> _disabled = new HashSet<string>();

    internal Encounter? Encounter { get; private set; }

    internal void Load(Encounter? encounter)
    {
        _instances.Clear();
        Encounter = encounter;
    }

    // Starts every enabled timer bound to the slot and returns cues that are due immediately
    // (warn_before >= duration, or sounds clamped to the start).
    internal List<TimerCue> Start(int slot, long now)
    {
        var cues = new List<TimerCue>();
        if (Encounter is null) return cues;
        foreach (var timer in Encounter.Timers)
        {
            if (!timer.Keys.Contains(slot) || _disabled.Contains(timer.Id)) continue;
            var existing = _instances.Where(i => ReferenceEquals(i.Timer, timer)).OrderBy(i => i.Start).ThenBy(i => i.Id).ToList();
            if (existing.Count >= timer.MaxInstances)
            {
                switch (timer.OnLimit)
                {
                    case LimitAction.Ignore: continue;
                    case LimitAction.Reset:
                        _instances.RemoveAll(i => ReferenceEquals(i.Timer, timer));
                        break;
                    default:
                        foreach (var oldest in existing.Take(existing.Count - timer.MaxInstances + 1)) _instances.Remove(oldest);
                        break;
                }
            }
            var order = Enumerable.Range(0, timer.Sounds.Count).OrderBy(i => timer.SoundTime(timer.Sounds[i])).ToArray();
            var instance = new Instance { Id = _nextId++, Timer = timer, SoundOrder = order };
            Schedule(instance, now);
            _instances.Add(instance);
        }
        Collect(now, cues);
        return cues;
    }

    internal void SetDisabled(IReadOnlySet<string> ids)
    {
        _disabled = ids;
        _instances.RemoveAll(i => ids.Contains(i.Timer.Id));
    }

    internal void ResetSlot(int slot) => _instances.RemoveAll(i => i.Timer.Keys.Contains(slot));
    internal void ResetAll() => _instances.Clear();
    internal void ResetInstance(long id) => _instances.RemoveAll(i => i.Id == id);
    internal int Count => _instances.Count;

    internal List<TimerCue> Advance(long now)
    {
        var cues = new List<TimerCue>();
        Collect(now, cues);
        return cues;
    }

    // The earliest pending alert, sound or expiry, or null when nothing is running.
    internal long? NextDeadline()
    {
        long? next = null;
        foreach (var instance in _instances)
        {
            long due = instance.End;
            if (!instance.Alerted) due = Math.Min(due, instance.AlertAt);
            if (instance.NextSound < instance.SoundAt.Length) due = Math.Min(due, instance.SoundAt[instance.NextSound]);
            if (next is null || due < next) next = due;
        }
        return next;
    }

    internal TimerView[] Snapshot() => _instances
        .Select(i => new TimerView(i.Id, i.Timer, i.End, i.Alerted))
        .OrderBy(view => view.EndTicks).ThenBy(view => view.InstanceId).ToArray();

    private void Schedule(Instance instance, long start)
    {
        var timer = instance.Timer;
        instance.Start = start;
        instance.End = start + Ticks(timer.Duration);
        instance.AlertAt = instance.End - Ticks(timer.AlertLead);
        instance.Alerted = false;
        instance.SoundAt = [.. instance.SoundOrder.Select(i => start + Ticks(timer.SoundTime(timer.Sounds[i])))];
        instance.NextSound = 0;
    }

    private long Ticks(double seconds) => (long)Math.Round(seconds * ticksPerSecond);

    // Emits the alert and every sound of the current cycle that is due, each once.
    private static void Due(Instance instance, long now, List<TimerCue> cues)
    {
        if (!instance.Alerted && now >= instance.AlertAt)
        {
            instance.Alerted = true;
            cues.Add(new(instance.Timer, instance.Id));
        }
        while (instance.NextSound < instance.SoundAt.Length && now >= instance.SoundAt[instance.NextSound])
            cues.Add(new(instance.Timer, instance.Id, instance.SoundOrder[instance.NextSound++]));
    }

    private void Collect(long now, List<TimerCue> cues)
    {
        for (int index = 0; index < _instances.Count; index++)
        {
            var instance = _instances[index];
            Due(instance, now, cues);
            if (now < instance.End) continue;
            if (!instance.Timer.Repeat)
            {
                _instances.RemoveAt(index--);
                continue;
            }
            // Repeat from the scheduled end rather than now, so cycles do not drift. After a long stall,
            // skip whole missed cycles and give at most one alert and one play of each sound, for the cycle
            // that contains now.
            long period = instance.End - instance.Start;
            long missed = (now - instance.End) / period;
            Schedule(instance, instance.End + missed * period);
            Due(instance, now, cues);
        }
    }
}
