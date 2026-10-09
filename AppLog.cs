using System.Text;
using System.Threading.Channels;

// One bounded writer for the GUI process; the scheduler and hook threads never wait for disk I/O.
internal static class AppLog
{
    private static FileLog? _current;
    private static readonly object ThrottleGate = new();
    private static readonly Dictionary<string, (DateTimeOffset Last, int Suppressed)> Throttles = new();
    internal static string DirectoryPath => AppContext.BaseDirectory;
    internal static string? Warning => _current?.Warning;

    internal static FileLog Start(string? directory = null)
    {
        var log = new FileLog(directory ?? DirectoryPath);
        _current = log;
        Info("App", $"Started TipAura {BuildInfo.Version}.");
        return log;
    }

    internal static void Info(string subsystem, string message) => _current?.Write("INFO", subsystem, message);
    internal static void Warn(string subsystem, string message) => _current?.Write("WARN", subsystem, message);
    internal static void Error(string subsystem, string message, Exception? error = null) =>
        _current?.Write("ERROR", subsystem, error is null ? message : $"{message}: {error}");

    // Writes the entry and drains the queue before returning: an unhandled exception may
    // terminate the process without running finally blocks, so the using-dispose is not enough.
    internal static void Fatal(string subsystem, string message, Exception? error = null)
    {
        Error(subsystem, message, error);
        try { _current?.Close(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* The process is already failing; keep the original exception. */ }
    }

    internal static void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("App", e.IsTerminating ? "Unhandled exception; terminating" : "Unhandled exception",
                e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Error("App", "Unobserved task exception", e.Exception);
    }

    internal static void WarnThrottled(string subsystem, string key, string message)
    {
        if (_current is null) return;
        lock (ThrottleGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (Throttles.TryGetValue(key, out var previous) && now - previous.Last < TimeSpan.FromSeconds(30))
            {
                Throttles[key] = (previous.Last, previous.Suppressed + 1);
                return;
            }
            if (previous.Suppressed > 0)
                Warn(subsystem, $"{previous.Suppressed} repeated {key} event(s) suppressed.");
            Throttles[key] = (now, 0);
            Warn(subsystem, message);
        }
    }
}

internal sealed class FileLog : IDisposable
{
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task _worker;
    private readonly string _directory;
    private readonly int _maxBytes, _files;
    private string? _warning;
    private int _disposed;
    internal string? Warning => Volatile.Read(ref _warning);

    internal FileLog(string directory, int maxBytes = 2 * 1024 * 1024, int files = 5)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _files = files;
        _worker = Task.Run(ConsumeAsync);
    }

    internal void Write(string level, string subsystem, string message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Warning is not null) return;
        if (message.Length > 4096) message = message[..4096] + " [truncated]";
        string line = $"{DateTimeOffset.UtcNow:O} [{level}] [{subsystem}] "
            + message.Replace('\r', ' ').Replace('\n', ' ');
        if (!_lines.Writer.TryWrite(line))
            Interlocked.Increment(ref _dropped);
    }

    private int _dropped;

    private async Task ConsumeAsync()
    {
        StreamWriter? writer = null;
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "tipaura.log");
            if (File.Exists(path) && new FileInfo(path).Length >= _maxBytes) Rotate(path);
            writer = Open(path);
            await foreach (string line in _lines.Reader.ReadAllAsync())
            {
                int dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) WriteLine($"{DateTimeOffset.UtcNow:O} [WARN] [Log] {dropped} events dropped (queue full).");
                WriteLine(line);
            }
            int remaining = Interlocked.Exchange(ref _dropped, 0);
            if (remaining > 0) WriteLine($"{DateTimeOffset.UtcNow:O} [WARN] [Log] {remaining} events dropped (queue full).");

            void WriteLine(string text)
            {
                int bytes = Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;
                if (writer.BaseStream.Length + bytes > _maxBytes)
                {
                    writer.Dispose();
                    Rotate(path);
                    writer = Open(path);
                }
                writer.WriteLine(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException)
        {
            Volatile.Write(ref _warning, $"Could not write log: {ex.Message}");
            Console.Error.WriteLine(Warning);
        }
        finally
        {
            try { writer?.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (Warning is null)
                {
                    Volatile.Write(ref _warning, $"Could not close log: {ex.Message}");
                    Console.Error.WriteLine(Warning);
                }
            }
        }
    }

    private static StreamWriter Open(string path) => new(new FileStream(path, FileMode.Append,
        FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

    private void Rotate(string path)
    {
        for (int i = _files - 1; i >= 1; i--)
        {
            string from = i == 1 ? path : path + "." + (i - 1);
            if (File.Exists(from)) File.Move(from, path + "." + i, true);
        }
    }

    public void Dispose() => Close(Timeout.InfiniteTimeSpan);

    // Bounded so a crash handler cannot hang the dying process on slow disk I/O.
    internal void Close(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lines.Writer.TryComplete();
        if (((IAsyncResult)_worker).AsyncWaitHandle.WaitOne(timeout)) _worker.GetAwaiter().GetResult();
    }
}
