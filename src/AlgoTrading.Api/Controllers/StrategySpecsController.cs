// src/AlgoTrading.Api/Controllers/StrategySpecsController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Serves a strategy's specification — docs/strategies/&lt;Name&gt;.md — to the
/// console's Library page. The file is the same one a reviewer reads on
/// GitHub; the API adds nothing but the parsed facts block.
/// </summary>
/// <remarks>
/// Lives beside <see cref="StrategyController"/> under the same route prefix
/// rather than inside it: that controller owns the run lifecycle and its
/// constructor already takes fifteen services; reading a Markdown file needs
/// two. Same module grant as the rest of the catalog — a spec is
/// documentation, so, like GET /api/Strategy/{id}, it is not filtered by the
/// trader's strategy package; the check that stops a run is on deploy.
/// </remarks>
[RequireModule(PlatformModules.Strategies)]
[ApiController]
[Route("api/Strategy")]
public class StrategySpecsController : ControllerBase
{
    /// <summary>docs/strategies at the repo root, two levels above the API's content root (the same convention as InstrumentMasterService.Directory).</summary>
    private const string SpecsFolder = "docs/strategies";

    private readonly StrategyCatalogService _catalog;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<StrategySpecsController> _logger;

    public StrategySpecsController(
        StrategyCatalogService catalog,
        IWebHostEnvironment env,
        ILogger<StrategySpecsController> logger)
    {
        _catalog = catalog;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// The spec of the strategy with this catalog id. 404 for an unknown id;
    /// 200 with <c>hasSpec=false</c> when the strategy exists but the file has
    /// not been written yet.
    /// </summary>
    [HttpGet("{id:int}/spec")]
    public async Task<ActionResult<StrategySpecResponse>> GetSpec(int id, CancellationToken cancellationToken)
    {
        var entry = await _catalog.FindAsync(id, cancellationToken);
        if (entry is null) return NotFound(new { message = $"Strategy {id} not found." });

        // The file name is the registry name. Registry names are identifiers
        // (class `name` attributes, factory keys); anything else cannot have a
        // spec, and refusing it here is what keeps a crafted name from naming
        // a path outside the folder.
        if (!SpecFileName.IsMatch(entry.Name))
        {
            _logger.LogWarning("Strategy name {Name} is not a valid spec file name; reporting no spec.", entry.Name);
            return Ok(Missing(entry.Name));
        }

        var folder = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "docs", "strategies"));
        var file = Path.GetFullPath(Path.Combine(folder, entry.Name + ".md"));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            // Unreachable given the regex above; kept so the guard does not
            // depend on the regex staying strict.
            return Ok(Missing(entry.Name));
        }

        if (!System.IO.File.Exists(file)) return Ok(Missing(entry.Name));

        string markdown;
        try
        {
            markdown = await System.IO.File.ReadAllTextAsync(file, cancellationToken);
        }
        catch (IOException ex)
        {
            // A spec being saved by its author at this instant: report it as
            // absent for this request rather than fail the page.
            _logger.LogWarning(ex, "Could not read strategy spec {File}.", file);
            return Ok(Missing(entry.Name));
        }

        return Ok(new StrategySpecResponse
        {
            Name = entry.Name,
            HasSpec = true,
            Markdown = markdown,
            Path = RelativePath(entry.Name),
            UpdatedUtc = System.IO.File.GetLastWriteTimeUtc(file),
            Facts = ParseFacts(markdown)
        });
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static readonly Regex SpecFileName = new("^[A-Za-z0-9]+$", RegexOptions.Compiled);

    /// <summary>A fenced yaml block: the last one in the file is the facts block.</summary>
    private static readonly Regex YamlFence = new(
        @"^```\s*ya?ml\s*\r?\n(?<body>.*?)\r?\n```\s*$",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RelativePath(string name) => $"{SpecsFolder}/{name}.md";

    private static StrategySpecResponse Missing(string name) => new()
    {
        Name = name,
        HasSpec = false,
        Markdown = null,
        Path = RelativePath(name),
        UpdatedUtc = null,
        Facts = null
    };

    /// <summary>
    /// The facts block as a flat map. Reads only top-level <c>key: value</c>
    /// lines — no nesting, no lists — which is all the template allows and all
    /// the Library page shows. Comments and surrounding quotes are dropped.
    /// Null when the document has no yaml block.
    /// </summary>
    internal static Dictionary<string, string>? ParseFacts(string markdown)
    {
        var matches = YamlFence.Matches(markdown);
        if (matches.Count == 0) return null;

        var body = matches[^1].Groups["body"].Value;
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0]) || trimmed.StartsWith("- ", StringComparison.Ordinal)) continue;

            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed[..colon].Trim();
            var value = trimmed[(colon + 1)..];
            int comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment];
            value = value.Trim();
            if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\''))
            {
                value = value[1..^1];
            }

            facts[key] = value;
        }

        return facts.Count == 0 ? null : facts;
    }
}
