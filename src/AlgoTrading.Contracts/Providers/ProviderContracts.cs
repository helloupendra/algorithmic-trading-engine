namespace AlgoTrading.Contracts.Providers;

/// <summary>What a connector can do, as the console renders it.</summary>
public class ProviderCapabilitiesResponse
{
    public bool History { get; set; }
    public bool LiveTicks { get; set; }
    public bool Quotes { get; set; }
    public bool OptionChain { get; set; }
    public bool Orders { get; set; }
    public bool Depth { get; set; }
    public bool OpenInterest { get; set; }
    public bool Greeks { get; set; }
    public int? MaxStreamSymbols { get; set; }
    public int? HistoryMaxDaysPerCall { get; set; }
    public int? RequestsPerMinute { get; set; }
    public IReadOnlyList<string> Resolutions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Segments { get; set; } = Array.Empty<string>();
}

/// <summary>Where a connector's credentials come from, and whether they are complete.</summary>
public class ProviderCredentialsResponse
{
    /// <summary>"database", "config" or "none". The secret itself is never returned.</summary>
    public string Source { get; set; } = "none";
    public string ClientId { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public bool HasSecret { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// <summary>The connector's live session, for connectors that need one.</summary>
public class ProviderSessionResponse
{
    public bool IsConnected { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public int? AgeSeconds { get; set; }

    /// <summary>
    /// True for a connector whose token expires daily and whose session was last
    /// saved before today's session started — a reconnect is due.
    /// </summary>
    public bool NeedsReconnect { get; set; }
}

/// <summary>One connector as the Connectors module shows it.</summary>
public class ProviderResponse
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"Data", "Execution" or "Both".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>"None", "ApiKey" or "OAuthDaily".</summary>
    public string Auth { get; set; } = string.Empty;

    public bool IsDataProvider { get; set; }
    public bool IsBroker { get; set; }

    /// <summary>
    /// False for a vendor on the roadmap that has no adapter in this build: it is
    /// listed so the directory is honest about what exists, but it cannot be
    /// configured or connected.
    /// </summary>
    public bool IsInstalled { get; set; } = true;

    /// <summary>Why an uninstalled connector is on the list. Empty for installed ones.</summary>
    public string PlannedNote { get; set; } = string.Empty;

    /// <summary>
    /// True once credentials have been saved for this connector — what an operator
    /// means by "I added this broker".
    /// </summary>
    public bool IsConfigured { get; set; }

    public ProviderCapabilitiesResponse Capabilities { get; set; } = new();
    public ProviderCredentialsResponse Credentials { get; set; } = new();
    public ProviderSessionResponse Session { get; set; } = new();

    /// <summary>The callback URL to register with this vendor's app.</summary>
    public string SuggestedRedirectUri { get; set; } = string.Empty;

    /// <summary>Capabilities this connector is currently serving, e.g. ["History"].</summary>
    public IReadOnlyList<string> ServingCapabilities { get; set; } = Array.Empty<string>();
}

public class SaveProviderCredentialsRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
}

/// <summary>One capability and the connectors serving it, best first.</summary>
public class ProviderBindingResponse
{
    public string Capability { get; set; } = string.Empty;

    /// <summary>Provider keys in priority order.</summary>
    public IReadOnlyList<string> ProviderKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// True when no rows are configured and the platform is falling back to
    /// whichever connectors claim the capability.
    /// </summary>
    public bool IsFallback { get; set; }
}

public class SaveProviderBindingRequest
{
    public string Capability { get; set; } = string.Empty;

    /// <summary>Provider keys in priority order; an empty list clears the binding.</summary>
    public IReadOnlyList<string> ProviderKeys { get; set; } = Array.Empty<string>();
}

/// <summary>Outcome of a live "test connection" against one connector.</summary>
public class ProviderTestResponse
{
    public string ProviderKey { get; set; } = string.Empty;
    public bool Ok { get; set; }

    /// <summary>What was attempted, e.g. "history NSE:NIFTYBANK-INDEX 15m".</summary>
    public string Probe { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
    public int? BarsReturned { get; set; }
    public int ElapsedMs { get; set; }
}

/// <summary>A file-based data vendor an operator added from the console.</summary>
public class DataVendorResponse
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Currently always "CsvFiles".</summary>
    public string Kind { get; set; } = string.Empty;

    public string Directory { get; set; } = string.Empty;

    /// <summary>The folder as the server actually resolves it — not the raw setting.</summary>
    public string ResolvedDirectory { get; set; } = string.Empty;

    /// <summary>False when the folder does not exist on the API host.</summary>
    public bool DirectoryExists { get; set; }

    /// <summary>How many *.csv files are sitting in it.</summary>
    public int FileCount { get; set; }

    public bool IsEnabled { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class SaveDataVendorRequest
{
    /// <summary>Lowercase, letters/digits/dashes. Immutable once rows carry it.</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}
