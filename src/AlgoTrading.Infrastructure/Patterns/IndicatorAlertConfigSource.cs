using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>The config file as last read: where it is, when it changed, and what it says.</summary>
/// <param name="File">The file read; null when none was found.</param>
/// <param name="Searched">Every place looked, for the page to name when there is no file.</param>
/// <param name="Config">Null when there is no file or it could not be read.</param>
/// <param name="Error">Why an existing file could not be read.</param>
public sealed record IndicatorConfigRead(
    string? File,
    IReadOnlyList<string> Searched,
    DateTime? ModifiedUtc,
    IndicatorAlertConfig? Config,
    string? Error);

/// <summary>
/// Finds and reads <c>config/indicator-alerts.txt</c>, re-reading it only when
/// it changed, so an edit applies at the next scan without a restart.
/// </summary>
/// <remarks>
/// Looked for the way the Desk looks for the morning plan: the
/// <c>IndicatorAlerts:ConfigFile</c> setting when there is one (relative to the
/// content root), otherwise <c>config/indicator-alerts.txt</c> in the content
/// root or up to four directories above it, then the same from the working
/// directory. The content root is src/AlgoTrading.Api under <c>dotnet run</c>
/// and may be the repository root in a published layout, so neither is assumed.
/// </remarks>
public sealed class IndicatorAlertConfigSource
{
    public static readonly string RelativePath = Path.Combine("config", "indicator-alerts.txt");
    private const int SearchDepth = 4;

    private readonly string _contentRoot;
    private readonly string? _configured;
    private readonly object _gate = new();
    private (string File, DateTime Modified, long Length, IndicatorConfigRead Read)? _last;

    public IndicatorAlertConfigSource(IHostEnvironment environment, IConfiguration configuration)
        : this(environment.ContentRootPath, configuration["IndicatorAlerts:ConfigFile"])
    {
    }

    public IndicatorAlertConfigSource(string contentRoot, string? configuredFile)
    {
        _contentRoot = contentRoot;
        _configured = string.IsNullOrWhiteSpace(configuredFile) ? null : configuredFile;
    }

    public IndicatorConfigRead Read()
    {
        var (file, searched) = Locate();
        if (file is null) return new IndicatorConfigRead(null, searched, null, null, null);

        try
        {
            var info = new FileInfo(file);
            lock (_gate)
            {
                if (_last is { } last && last.File == file && last.Modified == info.LastWriteTimeUtc && last.Length == info.Length)
                    return last.Read;
            }

            var config = IndicatorAlertConfig.Parse(File.ReadAllText(file));
            var read = new IndicatorConfigRead(file, searched, info.LastWriteTimeUtc, config, null);
            lock (_gate) _last = (file, info.LastWriteTimeUtc, info.Length, read);
            return read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-save by an editor, or permissions: say so and try again next scan.
            return new IndicatorConfigRead(file, searched, null, null, $"The file could not be read: {ex.Message}");
        }
    }

    private (string? File, IReadOnlyList<string> Searched) Locate()
    {
        if (_configured is not null)
        {
            var path = Path.GetFullPath(Path.Combine(_contentRoot, _configured));
            return (File.Exists(path) ? path : null, [path]);
        }

        var searched = new List<string>();
        foreach (var start in new[] { _contentRoot, Directory.GetCurrentDirectory() })
        {
            var directory = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
            for (int depth = 0; depth <= SearchDepth && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, RelativePath);
                if (!searched.Contains(candidate)) searched.Add(candidate);
                if (File.Exists(candidate)) return (candidate, searched);
                directory = directory.Parent;
            }
        }

        return (null, searched);
    }
}
