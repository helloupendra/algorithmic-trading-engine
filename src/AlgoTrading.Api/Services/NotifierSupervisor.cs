// src/AlgoTrading.Api/Services/NotifierSupervisor.cs

using System.Diagnostics;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the Telegram notifier process (scripts/telegram_notifier.py).
/// </summary>
/// <remarks>
/// The notifier began life as a sidecar started by hand, because it was added
/// while strategies were running and the backend could not be restarted. That
/// constraint is long gone, and being started by hand was its one real flaw:
/// it is the piece nobody notices is missing. Everything else stays green —
/// the API answers, the ingestor ticks, strategies trade — and the only symptom
/// is a silence that looks exactly like "nothing happened today".
///
/// So it is a supervised daemon now, started with the API by
/// <see cref="NotifierStartupService"/>. All of the behaviour is inherited;
/// this only names the process and its arguments.
/// </remarks>
public sealed class NotifierSupervisor : PythonDaemonSupervisor
{
    public NotifierSupervisor(
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<NotifierSupervisor> logger)
        : base(
            new DaemonDescriptor(
                Name: "notifier",
                // The notifier lives in scripts/, not in the engine, and
                // ScriptParts is engine-relative — hence the walk up to the
                // repository root. It resolves its own .env from __file__, so
                // running it from the engine directory is safe.
                ScriptParts: new[] { "..", "..", "scripts", "telegram_notifier.py" },
                ProcessMarker: ProcessProbe.NotifierMarker,
                PidSettingKey: SystemSettingKeys.NotifierPid,
                // --no-forward: this process sends to Telegram itself rather
                // than republishing events other publishers already handled.
                // The startup summary is deliberately NOT suppressed: one short
                // message per API start is the cheapest possible proof that the
                // alert path works, and its absence is the failure this class
                // exists to prevent. Add "--quiet-start" here to silence it.
                Args: new[] { "--no-forward", "--interval", "5" }),
            engine, scopeFactory, logger)
    {
    }
}

/// <summary>
/// Starts the notifier with the API, keeps it started, and moves it onto new
/// code when its script changes.
/// </summary>
/// <remarks>
/// A plain start-once would leave the same hole open: if the notifier dies at
/// 10am, alerts stop for the rest of the day and nothing says so. The periodic
/// check is safe to repeat because <see cref="PythonDaemonSupervisor.StartAsync"/>
/// refuses when an instance is already alive — managed or adopted from a
/// previous API process — so this never starts a second one.
/// <para>
/// Adoption had its own hole: an API restart adopts the running notifier, so
/// nothing ever started it again after a deploy. On 27 Sep the one running
/// dated from 17 Sep, and the account names added to its titles on 26 Sep had
/// never reached Telegram. It keeps nothing worth keeping (a new one reads the
/// runs as its baseline), and the desk deploys only outside the session, so a
/// script newer than the process means a restart.
/// </para>
/// </remarks>
public sealed class NotifierStartupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly NotifierSupervisor _supervisor;
    private readonly PythonEngineLocator _engine;
    private readonly ILogger<NotifierStartupService> _logger;

    public NotifierStartupService(NotifierSupervisor supervisor, PythonEngineLocator engine, ILogger<NotifierStartupService> logger)
    {
        _supervisor = supervisor;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>True when the script was written after the process started: it runs older code.</summary>
    public static bool RunsOlderCode(DateTime scriptWrittenUtc, DateTime processStartedUtc) =>
        scriptWrittenUtc > processStartedUtc;

    private bool IsStale(int pid)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(_engine.ScriptPath(_supervisor.Descriptor.ScriptParts));
            using var process = Process.GetProcessById(pid);
            return RunsOlderCode(written, process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Gone, or not ours to read: the next minute's check decides.
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = await _supervisor.GetStatusAsync(stoppingToken);
                if (status.IsRunning && status.ProcessId is int pid && IsStale(pid))
                {
                    _logger.LogInformation("Telegram notifier pid {Pid} runs older code than its script; restarting it.", pid);
                    await _supervisor.StopAsync("its script changed; restarting it on the new code", stoppingToken);
                    status = await _supervisor.GetStatusAsync(stoppingToken);
                }

                if (!status.IsRunning)
                {
                    var outcome = await _supervisor.StartAsync(stoppingToken);
                    if (outcome.Started)
                    {
                        _logger.LogInformation("Telegram notifier started (pid {Pid}).", outcome.ProcessId);
                    }
                    else
                    {
                        // Not an error on its own: a racing start, or an
                        // instance this API did not launch, both land here.
                        _logger.LogWarning("Telegram notifier not started: {Message}", outcome.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad minute end the supervision loop.
                _logger.LogError(ex, "Could not check or start the Telegram notifier.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
