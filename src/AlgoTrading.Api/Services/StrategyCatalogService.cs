// src/AlgoTrading.Api/Services/StrategyCatalogService.cs
using AlgoTrading.Contracts.Strategies;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services;

/// <summary>
/// One strategy as reported by tools/list_strategies.py (or the regex fallback).
/// </summary>
public sealed class StrategyCatalogEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string SourceFile { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> SupportedUnderlyings { get; set; } = new();
    public string InstrumentKind { get; set; } = "options";
    public string LegsSummary { get; set; } = string.Empty;
    public int DefaultLots { get; set; } = 1;
    public string DefaultParametersJson { get; set; } = "{}";
    public List<StrategyDataRequirement> DataRequirements { get; set; } = new();
    public DateTime CreatedUtc { get; set; }

    /// <summary>Set when the Python side could not load this strategy.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// The strategy catalog: runs <c>tools/list_strategies.py</c> in the engine
/// directory and caches the result until any strategies/**/*.py changes. When
/// Python fails or times out, falls back to a regex scan of the source files.
/// </summary>
public sealed class StrategyCatalogService
{
    private const int PythonTimeoutMs = 20_000;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private static readonly string[] DefaultUnderlyings = { "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX" };

    private static readonly HashSet<string> ScanExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "__init__.py", "base_strategy.py", "execution_runner.py", "logic_engine.py",
        "contract_selector.py", "price_resolver.py", "list_strategies.py", "registry.py",
        "private_strategies.py"
    };

    private readonly PythonEngineLocator _locator;
    private readonly ILogger<StrategyCatalogService> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyList<StrategyCatalogEntry>? _cached;
    private DateTime _cachedSourceStamp;
    private DateTime _cachedAtUtc;

    /// <summary>
    /// True when <see cref="_cached"/> came from the regex fallback. A fallback
    /// is retried on every TTL expiry (not only when a source file changes), so a
    /// transient Python failure at boot does not pin the fallback descriptions
    /// for the lifetime of the process.
    /// </summary>
    private bool _cachedIsFallback;

    public StrategyCatalogService(PythonEngineLocator locator, ILogger<StrategyCatalogService> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>
    /// Deterministic 31-bit positive id for a strategy name (FNV-1a 32-bit,
    /// masked to 31 bits, 0 mapped to 1). Stable across processes and machines,
    /// unlike string.GetHashCode().
    /// </summary>
    public static int StableId(string name)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        uint hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(name ?? string.Empty))
        {
            hash ^= b;
            hash *= prime;
        }

        int id = (int)(hash & 0x7fffffff);
        return id == 0 ? 1 : id;
    }

    public async Task<IReadOnlyList<StrategyCatalogEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        if (_cached is not null && now - _cachedAtUtc < CacheTtl)
        {
            return _cached;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTime.UtcNow;
            if (_cached is not null && now - _cachedAtUtc < CacheTtl)
            {
                return _cached;
            }

            var stamp = ComputeSourceStamp();
            if (_cached is not null && !_cachedIsFallback && stamp == _cachedSourceStamp)
            {
                _cachedAtUtc = now;
                return _cached;
            }

            var (entries, isFallback) = await LoadAsync(cancellationToken);
            if (isFallback && _cached is not null && !_cachedIsFallback)
            {
                // A previously good catalog beats a fresh fallback: keep the
                // descriptions the user already saw and retry on the next TTL.
                _logger.LogWarning("Strategy catalog refresh fell back to the regex scan; keeping the last good catalog.");
                _cachedIsFallback = true;
                _cachedAtUtc = DateTime.UtcNow;
                return _cached;
            }

            _cached = entries;
            _cachedIsFallback = isFallback;
            _cachedSourceStamp = stamp;
            _cachedAtUtc = DateTime.UtcNow;
            return entries;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<StrategyCatalogEntry?> FindAsync(int id, CancellationToken cancellationToken = default)
        => (await GetAllAsync(cancellationToken)).FirstOrDefault(x => x.Id == id);

    public async Task<StrategyCatalogEntry?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
        => (await GetAllAsync(cancellationToken)).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    /// <summary>
    /// Max LastWriteTimeUtc of strategies/**/*.py and the lister script; a change
    /// invalidates the cache. Cheap: a directory walk, no file reads.
    /// </summary>
    private DateTime ComputeSourceStamp()
    {
        var max = DateTime.MinValue;
        try
        {
            var strategiesDir = _locator.ScriptPath("strategies");
            if (Directory.Exists(strategiesDir))
            {
                foreach (var file in Directory.EnumerateFiles(strategiesDir, "*.py", SearchOption.AllDirectories))
                {
                    var t = File.GetLastWriteTimeUtc(file);
                    if (t > max) max = t;
                }
            }

            var lister = _locator.ScriptPath("tools", "list_strategies.py");
            if (File.Exists(lister))
            {
                var t = File.GetLastWriteTimeUtc(lister);
                if (t > max) max = t;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stat strategy sources; catalog cache will refresh on TTL only.");
        }
        return max;
    }

    /// <summary>
    /// Runs the Python lister. The flag says whether the result is the regex
    /// fallback, so the cache knows to retry it.
    /// </summary>
    private async Task<(IReadOnlyList<StrategyCatalogEntry> Entries, bool IsFallback)> LoadAsync(CancellationToken cancellationToken)
    {
        var engineDir = _locator.EngineDirectory;
        var lister = _locator.ScriptPath("tools", "list_strategies.py");

        if (!File.Exists(lister))
        {
            _logger.LogWarning("Strategy lister not found at {Path}; using regex scan fallback.", lister);
            return (ScanFallback(engineDir), true);
        }

        try
        {
            var (exitCode, stdout, stderr, timedOut) = await RunPythonAsync(lister, engineDir, cancellationToken);

            if (timedOut)
            {
                _logger.LogWarning("Strategy lister timed out after {Timeout}s; using regex scan fallback. stderr: {Stderr}",
                    PythonTimeoutMs / 1000, stderr);
                return (ScanFallback(engineDir), true);
            }

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning("Strategy lister exited with code {Code}; using regex scan fallback. stderr: {Stderr}",
                    exitCode, stderr);
                return (ScanFallback(engineDir), true);
            }

            var parsed = ParseCatalogJson(stdout);
            if (parsed.Count == 0)
            {
                _logger.LogWarning("Strategy lister returned no strategies; using regex scan fallback. stderr: {Stderr}", stderr);
                return (ScanFallback(engineDir), true);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogInformation("Strategy lister diagnostics: {Stderr}", stderr.Trim());
            }

            return (parsed, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Strategy lister failed; using regex scan fallback.");
            return (ScanFallback(engineDir), true);
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunPythonAsync(
        string script, string engineDir, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _locator.PythonExecutable,
            WorkingDirectory = engineDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(script);
        psi.Environment["PYTHONPATH"] = engineDir;
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        // UTF-8 on the pipe regardless of the host locale (Windows defaults to cp1252).
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the python process.");
        }

        // Read both pipes concurrently so neither can fill and block the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PythonTimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            string partialErr = string.Empty;
            try { partialErr = await stderrTask; } catch { /* pipe closed */ }
            return (-1, string.Empty, partialErr, true);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr, false);
    }

    // ------------------------------------------------------------------
    // JSON parsing (schema: spec §3.1)
    // ------------------------------------------------------------------

    private List<StrategyCatalogEntry> ParseCatalogJson(string json)
    {
        var result = new List<StrategyCatalogEntry>();

        // Tolerate stray output before the array (a print left in by accident).
        int start = json.IndexOf('[');
        if (start < 0) return result;

        using var doc = JsonDocument.Parse(json[start..]);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = GetString(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var entry = new StrategyCatalogEntry
            {
                Id = StableId(name),
                Name = name,
                ClassName = GetString(item, "className") ?? string.Empty,
                SourceFile = (GetString(item, "sourceFile") ?? string.Empty).Replace('\\', '/'),
                Description = GetString(item, "description") ?? string.Empty,
                Category = GetString(item, "category") ?? string.Empty,
                InstrumentKind = GetString(item, "instrumentKind") ?? "options",
                LegsSummary = GetString(item, "legsSummary") ?? string.Empty,
                DefaultLots = Math.Max(1, GetInt(item, "defaultLots") ?? 1),
                Error = GetString(item, "error")
            };

            if (item.TryGetProperty("supportedUnderlyings", out var su) && su.ValueKind == JsonValueKind.Array)
            {
                entry.SupportedUnderlyings = su.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim().ToUpperInvariant())
                    .Where(x => x.Length > 0)
                    .Distinct()
                    .ToList();
            }

            if (item.TryGetProperty("defaultParameters", out var dp) && dp.ValueKind == JsonValueKind.Object)
            {
                entry.DefaultParametersJson = dp.GetRawText();
            }
            else if (dp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dp.GetString()))
            {
                entry.DefaultParametersJson = dp.GetString()!;
            }

            if (item.TryGetProperty("dataRequirements", out var dr) && dr.ValueKind == JsonValueKind.Array)
            {
                foreach (var req in dr.EnumerateArray())
                {
                    if (req.ValueKind != JsonValueKind.Object) continue;
                    entry.DataRequirements.Add(new StrategyDataRequirement
                    {
                        SymbolType = GetString(req, "symbolType") ?? string.Empty,
                        Resolution = GetString(req, "resolution") ?? string.Empty
                    });
                }
            }

            var created = GetString(item, "createdUtc");
            entry.CreatedUtc = DateTime.TryParse(created, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                ? dt
                : DateTime.UtcNow;

            if (entry.SupportedUnderlyings.Count == 0)
            {
                entry.SupportedUnderlyings = DefaultUnderlyings.ToList();
            }

            if (!string.IsNullOrWhiteSpace(entry.Error) && string.IsNullOrWhiteSpace(entry.Description))
            {
                entry.Description = $"Failed to load: {entry.Error}";
            }

            result.Add(entry);
        }

        return result
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? GetInt(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    // ------------------------------------------------------------------
    // Regex fallback (the pre-catalog behaviour, kept for when Python fails)
    // ------------------------------------------------------------------

    /// <summary>
    /// Scans strategies/**/*.py for BaseStrategy subclasses and their <c>name</c>
    /// attribute, plus the factory names in private_strategies.py, so the list
    /// still matches what execution_runner can launch.
    /// </summary>
    private IReadOnlyList<StrategyCatalogEntry> ScanFallback(string engineDir)
    {
        var strategiesPath = Path.Combine(engineDir, "strategies");
        var list = new List<StrategyCatalogEntry>();

        if (!Directory.Exists(strategiesPath)) return list;

        var pyFiles = Directory.GetFiles(strategiesPath, "*.py", SearchOption.AllDirectories)
            .Where(f => !ScanExcludedFiles.Contains(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in pyFiles)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallback scan could not read {File}.", file);
                continue;
            }

            string? strategyName = null;
            string? className = null;

            foreach (var line in content.Split('\n'))
            {
                var trimmed = line.Trim();
                if (line.StartsWith("class ") && line.Contains("(BaseStrategy)"))
                {
                    var parts = line.Split(' ', '(');
                    if (parts.Length > 1) className = parts[1].Trim();
                }
                if (trimmed.StartsWith("name = \"") || trimmed.StartsWith("name = '"))
                {
                    var quote = trimmed.Contains('"') ? '"' : '\'';
                    var parts = trimmed.Split(quote);
                    if (parts.Length >= 3) strategyName = parts[1];
                }
            }

            if (string.IsNullOrEmpty(className)) continue;

            if (string.IsNullOrEmpty(strategyName))
            {
                strategyName = className;
            }

            var relative = Path.GetRelativePath(strategiesPath, file).Replace('\\', '/');
            list.Add(FallbackEntry(strategyName, className, relative, File.GetLastWriteTimeUtc(file)));
        }

        // Private factories (Titli variants) are registered under their own names.
        var privateFile = Path.Combine(strategiesPath, "private_strategies.py");
        if (File.Exists(privateFile))
        {
            try
            {
                var text = File.ReadAllText(privateFile);
                var stamp = File.GetLastWriteTimeUtc(privateFile);
                foreach (Match m in Regex.Matches(text, "\"(?<name>[A-Za-z0-9_]+)\"\\s*:\\s*_make\\(\\s*(?<cls>[A-Za-z0-9_]+)"))
                {
                    var name = m.Groups["name"].Value;
                    if (list.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(FallbackEntry(name, m.Groups["cls"].Value, "private_strategies.py", stamp));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fallback scan could not read private_strategies.py.");
            }
        }

        return list
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static StrategyCatalogEntry FallbackEntry(string name, string className, string sourceFile, DateTime createdUtc)
        => new()
        {
            Id = StableId(name),
            Name = name,
            ClassName = className,
            SourceFile = sourceFile,
            Description = $"Discovered from {sourceFile}. Description unavailable: the Python catalog could not be read — check the API log.",
            Category = string.Empty,
            SupportedUnderlyings = DefaultUnderlyings.ToList(),
            InstrumentKind = "options",
            LegsSummary = string.Empty,
            DefaultLots = 1,
            DefaultParametersJson = "{}",
            CreatedUtc = createdUtc
        };
}
