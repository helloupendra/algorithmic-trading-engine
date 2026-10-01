// src/AlgoTrading.Api/Services/DaemonPipes.cs

namespace AlgoTrading.Api.Services;

/// <summary>
/// Decides which lines of a launched daemon's stdout and stderr pipes go to
/// api.log, when the daemon also keeps a log file of its own that the API
/// follows (<see cref="PythonDaemonSupervisor.DaemonDescriptor.LogName"/>).
/// </summary>
/// <remarks>
/// The file and the pipes carry the same lines once core/safe_output.py has
/// installed its tee, and only the pipes carry what came before: an import
/// error, a line printed before the tee. The supervisor used to decide at the
/// moment it read a pipe line, by whether the file existed yet. That is a race:
/// on a slow machine a line printed before the tee can be read only after the
/// file has appeared, and it was dropped although the file never held it — CI
/// on macOS lost "before safe_output" exactly that way.
/// <para>
/// So it is decided by order instead. When the tee is installed, safe_output
/// first writes <see cref="TeeMarker"/> on each original pipe. Everything
/// above the marker is the pipe's alone and is logged; the marker itself is
/// not; everything below it is in the file and the pipe is only drained.
/// </para>
/// </remarks>
public sealed class DaemonPipes
{
    /// <summary>
    /// The line core/safe_output.py writes on each pipe as its tee begins
    /// (<c>PIPE_MARKER</c> there; the two must stay identical). The record
    /// separator in front keeps it from ever being taken for real output.
    /// </summary>
    public const string TeeMarker = "\u001e[safe_output] this pipe continues in the log file";

    /// <summary>
    /// How long a pipe line that may already be in the file is held, waiting
    /// for the marker, before the daemon is taken for one that never sends it.
    /// </summary>
    public static readonly TimeSpan LegacyGrace = TimeSpan.FromSeconds(5);

    private readonly Func<bool> _fileExists;
    private readonly Func<DateTime> _clock;

    // Set once either pipe has carried the marker: this daemon sends it, so a
    // pipe still without one has it on its way and is never given up on.
    private volatile bool _sendsMarker;

    /// <param name="fileExists">Whether the daemon's log file exists yet.</param>
    /// <param name="clock">UTC now; a test plays the time.</param>
    public DaemonPipes(Func<bool> fileExists, Func<DateTime>? clock = null)
    {
        _fileExists = fileExists;
        _clock = clock ?? (() => DateTime.UtcNow);
        Stdout = new Pipe(this);
        Stderr = new Pipe(this);
    }

    public Pipe Stdout { get; }
    public Pipe Stderr { get; }

    /// <summary>One of the two pipes. Fed every line read from it, in order.</summary>
    public sealed class Pipe
    {
        private readonly DaemonPipes _daemon;
        private readonly object _gate = new();
        private readonly List<string> _held = new();
        private DateTime? _heldSinceUtc;
        private bool _marked;
        private bool _legacy;

        internal Pipe(DaemonPipes daemon) => _daemon = daemon;

        /// <summary>
        /// One line read from the pipe, or null at its end. Returns the lines
        /// to log now, in pipe order: none, this one, or lines held until now.
        /// </summary>
        public IReadOnlyList<string> Read(string? line)
        {
            lock (_gate)
            {
                if (line is null)
                {
                    // The end. A held line from a daemon that sends the marker
                    // came before this pipe's marker, which never arrived: no
                    // file has it. From one that does not, the file has it.
                    return _daemon._sendsMarker ? Release() : Drop();
                }

                if (line.EndsWith(TeeMarker, StringComparison.Ordinal))
                {
                    _marked = true;
                    _daemon._sendsMarker = true;
                    // A line left unfinished before the tee shares the marker's line.
                    var before = line[..^TeeMarker.Length];
                    if (before.Length > 0) _held.Add(before);
                    return Release();
                }

                if (_marked)
                {
                    // Below the marker the file has every line, unless it could
                    // not be written at all (or is somewhere the API does not
                    // look): then the pipe is still the only copy.
                    return _daemon._fileExists() ? Array.Empty<string>() : new[] { line };
                }

                if (_legacy) return Array.Empty<string>();

                if (_held.Count == 0 && !_daemon._fileExists())
                {
                    // No file yet, so no line has reached it through the tee:
                    // the pipe is the only copy of this one.
                    return new[] { line };
                }

                // The file exists and this pipe has not shown its marker. From
                // a daemon that sends it, this line came before the tee — and
                // the marker, written before the file's first line, is already
                // in the pipe right behind it. So the line is held, not
                // dropped, until the marker releases it.
                //
                // A daemon started by Python from before the marker never
                // sends it. Its pipe lines are all in the file, and holding
                // them forever would only cost memory, so after LegacyGrace
                // with nothing marked the old rule applies: once the file
                // exists the pipe is only drained. The grace counts from the
                // first held line, not from the file's appearance: a reader
                // that falls behind (what CI on macOS did) must not turn a new
                // daemon's early line into a dropped one.
                var now = _daemon._clock();
                _heldSinceUtc ??= now;
                if (!_daemon._sendsMarker && now - _heldSinceUtc.Value >= LegacyGrace)
                {
                    _legacy = true;
                    return Drop();
                }

                _held.Add(line);
                return Array.Empty<string>();
            }
        }

        private IReadOnlyList<string> Release()
        {
            if (_held.Count == 0) return Array.Empty<string>();
            var lines = _held.ToArray();
            _held.Clear();
            _heldSinceUtc = null;
            return lines;
        }

        private IReadOnlyList<string> Drop()
        {
            _held.Clear();
            _heldSinceUtc = null;
            return Array.Empty<string>();
        }
    }
}
