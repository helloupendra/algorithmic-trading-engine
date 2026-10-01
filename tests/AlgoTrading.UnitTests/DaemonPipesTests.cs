using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Which lines of a launched daemon's pipes reach api.log, decided by the
/// marker core/safe_output.py writes as its tee begins, not by whether the
/// log file existed when a line happened to be read.
/// </summary>
/// <remarks>
/// CI on macOS lost "before safe_output": printed before the
/// tee, read only after the file appeared, and dropped as a duplicate the
/// file never held. Here the file and the clock are played, so every order
/// the reader can see the lines in is tried on purpose.
/// </remarks>
public class DaemonPipesTests
{
    private const string Marker = DaemonPipes.TeeMarker;

    private bool _file;
    private DateTime _now = new(2026, 10, 1, 3, 45, 0, DateTimeKind.Utc);

    private DaemonPipes Pipes() => new(() => _file, () => _now);

    [Fact]
    public void Lines_above_the_marker_are_logged_the_marker_never_and_lines_below_it_are_only_drained()
    {
        var pipes = Pipes();

        Assert.Equal(new[] { "ModuleNotFoundError: No module named 'redis'" },
            pipes.Stderr.Read("ModuleNotFoundError: No module named 'redis'"));
        Assert.Empty(pipes.Stderr.Read(Marker));
        _file = true;
        Assert.Empty(pipes.Stderr.Read("[dhan] no ticks for 60s"));
        Assert.Empty(pipes.Stderr.Read(null));
    }

    [Fact]
    public void A_line_from_before_the_tee_read_only_after_the_file_appeared_is_logged_when_the_marker_arrives()
    {
        var pipes = Pipes();
        // The reader fell behind: the file is there before it reads a thing,
        // and has been for a minute. How long does not matter; the order does.
        _file = true;
        _now += TimeSpan.FromMinutes(1);

        Assert.Empty(pipes.Stderr.Read("before safe_output"));
        Assert.Empty(pipes.Stderr.Read("a second early line"));
        Assert.Equal(new[] { "before safe_output", "a second early line" }, pipes.Stderr.Read(Marker));
        Assert.Empty(pipes.Stderr.Read("[test] no ticks for 60s"));
    }

    [Fact]
    public void A_line_left_unfinished_before_the_tee_is_logged_from_the_marker_line()
    {
        var pipes = Pipes();
        _file = true;

        Assert.Equal(new[] { "loading instruments..." }, pipes.Stdout.Read("loading instruments..." + Marker));
        Assert.Empty(pipes.Stdout.Read("[dhan] 212 instruments"));
    }

    [Fact]
    public void Below_the_marker_the_pipe_is_still_logged_while_no_file_exists()
    {
        // The tee could not open its file, or the file is not where the API
        // looks (a Windows virtualenv launcher runs python under another pid):
        // the pipe is then the only copy, as it always was.
        var pipes = Pipes();

        Assert.Empty(pipes.Stdout.Read(Marker));
        Assert.Equal(new[] { "[dhan] STARTING LIVE FEED" }, pipes.Stdout.Read("[dhan] STARTING LIVE FEED"));
        _file = true;
        Assert.Empty(pipes.Stdout.Read("[dhan] heartbeat"));
    }

    [Fact]
    public void A_daemon_that_never_sends_the_marker_falls_back_to_the_file_rule_and_logs_nothing_twice()
    {
        // Started by Python from before the marker: once the file exists every
        // pipe line is in it, and the file is what api.log is fed from.
        var pipes = Pipes();
        var logged = new List<string>();

        logged.AddRange(pipes.Stdout.Read("[fyers] importing"));
        _file = true;
        logged.AddRange(pipes.Stdout.Read("[fyers] connected"));
        _now += TimeSpan.FromSeconds(2);
        logged.AddRange(pipes.Stdout.Read("[fyers] subscribed"));
        _now += DaemonPipes.LegacyGrace;
        logged.AddRange(pipes.Stdout.Read("[fyers] heartbeat"));
        _now += TimeSpan.FromMinutes(5);
        logged.AddRange(pipes.Stdout.Read("[fyers] heartbeat"));
        logged.AddRange(pipes.Stdout.Read(null));

        Assert.Equal(new[] { "[fyers] importing" }, logged);
    }

    [Fact]
    public void A_daemon_that_sent_the_marker_on_one_pipe_is_waited_for_on_the_other_however_long()
    {
        var pipes = Pipes();
        _file = true;

        Assert.Empty(pipes.Stderr.Read("DeprecationWarning: early"));
        Assert.Empty(pipes.Stdout.Read(Marker));
        _now += TimeSpan.FromMinutes(1);
        Assert.Empty(pipes.Stderr.Read("another early warning"));

        Assert.Equal(new[] { "DeprecationWarning: early", "another early warning" }, pipes.Stderr.Read(Marker));
    }

    [Fact]
    public void At_the_end_of_the_pipe_a_held_line_is_logged_only_when_no_file_can_hold_it()
    {
        // A daemon without the marker: what was held is in the file.
        var old = Pipes();
        _file = true;
        Assert.Empty(old.Stdout.Read("[fyers] connected"));
        Assert.Empty(old.Stdout.Read(null));

        // One that sent it on stdout but whose stderr ended before its own:
        // the held stderr line came before any tee of that pipe.
        var marked = Pipes();
        Assert.Empty(marked.Stdout.Read(Marker));
        Assert.Empty(marked.Stderr.Read("Traceback (most recent call last):"));
        Assert.Equal(new[] { "Traceback (most recent call last):" }, marked.Stderr.Read(null));
    }
}
