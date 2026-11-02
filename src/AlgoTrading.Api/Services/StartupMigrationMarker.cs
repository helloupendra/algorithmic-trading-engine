using System.Globalization;

namespace AlgoTrading.Api.Services;

/// <summary>
/// A file that says "this API process is applying database migrations", written for the desk
/// (scripts/lib/desk-common.sh reads it).
/// </summary>
/// <remarks>
/// <para>
/// Migrations run as the API starts, before it listens, so for as long as they run GET /health does not answer. The
/// desk used to take that for an API that was down and kill it after about three and a half minutes, which rolled the
/// migration back, and the restarted API began it again: a heavy migration looped all night and into the morning job
/// (2 Nov 2026 review, F4). With this file the desk can tell the two apart.
/// </para>
/// <para>
/// A file rather than an answer on the port, because /health means "the API is serving" to everything else that reads
/// it (the pager, Sentinel, the Cloudflare Worker), and a stand-in listener answering 503 would change that: the Worker
/// shows its offline page for 502 and the like, not for 503.
/// </para>
/// <para>
/// <see cref="Done"/> removes the file. A migration that throws leaves it on purpose: a file whose process has gone is
/// how the desk knows a migration failed, as distinct from an API that never got that far.
/// </para>
/// </remarks>
public sealed class StartupMigrationMarker
{
    /// <summary>The environment variable naming the file. Only the desk sets it; without it there is no file.</summary>
    public const string PathVariable = "API_MIGRATING_FILE";

    private readonly string _path;
    private readonly ILogger _logger;

    private StartupMigrationMarker(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>
    /// Says the pending migrations are about to run: in the log always, in the file when <paramref name="path"/> is
    /// given. A file that cannot be written must not stop a migration, so that is only a warning; the desk then treats
    /// the API as it did before this file existed.
    /// </summary>
    /// <returns>The marker to call <see cref="Done"/> on, or null when there is no file.</returns>
    public static StartupMigrationMarker? Begin(string? path, IReadOnlyCollection<string> pending, ILogger logger, DateTimeOffset now)
    {
        logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            string temporary = path + ".tmp";
            // Written whole and moved into place: the desk never reads half a file.
            File.WriteAllText(temporary, string.Create(CultureInfo.InvariantCulture,
                $"pid={Environment.ProcessId}\nstarted={now.ToUnixTimeSeconds()}\npending={pending.Count}\n"));
            File.Move(temporary, path, overwrite: true);
            return new StartupMigrationMarker(path, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the migration marker {Path}; the desk cannot tell this migration from an API that is down.", path);
            return null;
        }
    }

    /// <summary>The migrations finished: removes the file.</summary>
    public void Done()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The desk drops a leftover file once /health answers.
            _logger.LogWarning(ex, "Could not remove the migration marker {Path}.", _path);
        }
    }
}
