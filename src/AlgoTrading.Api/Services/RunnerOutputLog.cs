// src/AlgoTrading.Api/Services/RunnerOutputLog.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The log a strategy runner keeps of its own output from its first line
/// (core/safe_output.py): <c>logs/engine/runner-&lt;run&gt;-&lt;pid&gt;.log</c>,
/// every line stamped with a UTC time and the console's stream mark —
/// <c>2026-09-28T03:45:01.123Z | text</c> for stdout, <c>… ! text</c> for
/// stderr — and the last one <c>EXIT code=… reason=…</c>.
/// </summary>
/// <remarks>
/// The API reads every run's console from this file rather than from the
/// runner's pipes. A runner adopted after an API restart has no pipes, and on
/// 24 Sep, with all 13 live runs adopted, the console showed only the lines
/// the API wrote itself. One source means a run reads the same before and
/// after a restart.
/// </remarks>
public static partial class RunnerOutputLog
{
    /// <summary>One line of the file, split into its parts.</summary>
    /// <param name="AtUtc">When the runner wrote it; null for a line with no stamp.</param>
    public sealed record Line(DateTime? AtUtc, bool IsStderr, string Text);

    /// <summary>How the runner said it ended, from its EXIT line.</summary>
    public sealed record Exit(int Code, string Reason);

    public static string PathFor(string engineLogDirectory, long runId, int processId)
        => Path.Combine(
            engineLogDirectory,
            $"runner-{runId.ToString(CultureInfo.InvariantCulture)}-{processId.ToString(CultureInfo.InvariantCulture)}.log");

    /// <summary>
    /// A line of the file. One without a stamp — a runner from before 28 Sep,
    /// which wrote this file only after its pipe had died — is stdout text
    /// with no time of its own.
    /// </summary>
    public static Line Parse(string raw)
    {
        var match = StampedLine().Match(raw);
        if (!match.Success) return new Line(null, false, raw);

        DateTime? at = DateTime.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
        return new Line(at, match.Groups["stream"].Value == "!", match.Groups["text"].Value);
    }

    /// <summary>
    /// The console's form of a line, "HH:mm:ss | text" or "HH:mm:ss ! text",
    /// on the runner's clock when it stamped the line.
    /// </summary>
    public static string ToConsole(Line line, DateTime nowUtc)
        => $"{(line.AtUtc ?? nowUtc).ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {(line.IsStderr ? '!' : '|')} {line.Text}";

    /// <summary>The runner's EXIT line, or null for any other text.</summary>
    public static Exit? ParseExit(string text)
    {
        var match = ExitLine().Match(text);
        return match.Success && int.TryParse(match.Groups["code"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            ? new Exit(code, match.Groups["reason"].Value.Trim())
            : null;
    }

    [GeneratedRegex(@"^(?<at>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z) (?<stream>[|!]) (?<text>.*)$")]
    private static partial Regex StampedLine();

    [GeneratedRegex(@"^EXIT code=(?<code>-?\d+) reason=(?<reason>.*)$")]
    private static partial Regex ExitLine();
}

/// <summary>
/// Follows one runner's log file into its console: the file's last lines
/// first, then whatever is appended, read every second from where the last
/// read stopped. Never throws; a file that does not exist yet is simply
/// looked for again.
/// </summary>
public sealed class RunnerLogTail : IDisposable
{
    /// <summary>At most this much of the file's end is read to find the lines it starts with.</summary>
    private const int SeedBytes = 512 * 1024;

    /// <summary>At most this much is read in one poll; the rest waits for the next.</summary>
    private const int MaxReadBytes = 1024 * 1024;

    /// <summary>A console line is cut here: a runaway print must not fill the ring by itself.</summary>
    private const int MaxLineChars = 4000;

    private readonly Action<RunnerOutputLog.Line, bool> _onLine;
    private readonly int _seedLines;
    private readonly object _gate = new();
    private long _offset;
    private bool _seeded;
    private CancellationTokenSource? _stop;

    /// <param name="path">The runner's log file.</param>
    /// <param name="onLine">Receives every complete line, in file order.</param>
    /// <param name="seedLines">How many of the file's existing lines the first read hands over.</param>
    public RunnerLogTail(string path, Action<RunnerOutputLog.Line> onLine, int seedLines = RunningStrategy.LogCapacity)
        : this(path, (line, _) => onLine(line), seedLines)
    {
    }

    /// <param name="path">The process's log file.</param>
    /// <param name="onLine">
    /// Receives every complete line, in file order, and whether it came from
    /// the first read (true: the file already held it) or was appended since.
    /// A daemon's lines go into the API's own log, and one the file held when
    /// the API adopted it may have been logged by the API before the restart.
    /// </param>
    /// <param name="seedLines">How many of the file's existing lines the first read hands over.</param>
    public RunnerLogTail(string path, Action<RunnerOutputLog.Line, bool> onLine, int seedLines = RunningStrategy.LogCapacity)
    {
        Path = path;
        _onLine = onLine;
        _seedLines = seedLines;
    }

    public string Path { get; }

    /// <summary>
    /// Hands over what the file holds that has not been read: on the first
    /// read the last <c>seedLines</c> lines of it, on every later one the lines
    /// appended since. A line still being written waits for its newline.
    /// Returns how many lines were handed over.
    /// </summary>
    public int Poll()
    {
        lock (_gate)
        {
            try
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return _seeded ? ReadAppended(stream) : Seed(stream);
            }
            catch (FileNotFoundException)
            {
                return 0; // the runner has not written its first line yet
            }
            catch (DirectoryNotFoundException)
            {
                return 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>Reads the file every <paramref name="interval"/> until <see cref="Stop"/>.</summary>
    public void Start(TimeSpan interval)
    {
        lock (_gate)
        {
            if (_stop is not null) return;
            _stop = new CancellationTokenSource();
        }

        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                do
                {
                    Poll();
                }
                while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        });
    }

    /// <summary>Stops the polling after one last read, so a runner's final lines are not left behind.</summary>
    public void Stop()
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            stop = _stop;
            _stop = null;
        }

        stop?.Cancel();
        stop?.Dispose();
        Poll();
    }

    public void Dispose() => Stop();

    private int Seed(FileStream stream)
    {
        long length = stream.Length;
        long start = Math.Max(0, length - SeedBytes);
        var bytes = ReadRange(stream, start, (int)(length - start));

        // Reading from the middle of the file lands inside a line: skip to the next one.
        int from = 0;
        if (start > 0)
        {
            int newline = Array.IndexOf(bytes, (byte)'\n');
            from = newline < 0 ? bytes.Length : newline + 1;
        }

        var lines = CompleteLines(bytes, from, out int consumed);
        _offset = start + consumed;
        _seeded = true;

        int skip = Math.Max(0, lines.Count - _seedLines);
        for (int i = skip; i < lines.Count; i++) Deliver(lines[i], seeded: true);
        return lines.Count - skip;
    }

    private int ReadAppended(FileStream stream)
    {
        long length = stream.Length;
        if (length < _offset) _offset = 0; // truncated or replaced: read it again from the start
        if (length == _offset) return 0;

        int count = (int)Math.Min(length - _offset, MaxReadBytes);
        var bytes = ReadRange(stream, _offset, count);
        var lines = CompleteLines(bytes, 0, out int consumed);

        if (consumed == 0 && count == MaxReadBytes)
        {
            // A megabyte without a newline will not end in one: pass it on as it is.
            lines.Add(Encoding.UTF8.GetString(bytes));
            consumed = count;
        }

        _offset += consumed;
        foreach (var line in lines) Deliver(line, seeded: false);
        return lines.Count;
    }

    private void Deliver(string raw, bool seeded)
    {
        var text = raw.Length > MaxLineChars ? raw[..MaxLineChars] + "…" : raw;
        try
        {
            _onLine(RunnerOutputLog.Parse(text), seeded);
        }
        catch
        {
            // A consumer's failure must not stop the reading.
        }
    }

    private static byte[] ReadRange(FileStream stream, long start, int count)
    {
        var buffer = new byte[count];
        stream.Seek(start, SeekOrigin.Begin);
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) break;
            read += n;
        }
        return read == count ? buffer : buffer[..read];
    }

    /// <summary>The newline-terminated lines of <paramref name="bytes"/> from <paramref name="from"/>, decoded as UTF-8.</summary>
    private static List<string> CompleteLines(byte[] bytes, int from, out int consumed)
    {
        var lines = new List<string>();
        int start = from;
        for (int i = from; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            int end = i > start && bytes[i - 1] == (byte)'\r' ? i - 1 : i;
            lines.Add(Encoding.UTF8.GetString(bytes, start, end - start));
            start = i + 1;
        }
        consumed = start;
        return lines;
    }
}
