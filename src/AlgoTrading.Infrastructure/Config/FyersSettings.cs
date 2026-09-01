using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Infrastructure.Config
{
    /// <summary>
    /// Strongly typed configuration class representing the Fyers API credentials and URLs.
    /// Bound directly from the appsettings.json file.
    /// </summary>
    public class FyersSettings
    {
        public string ClientId { get; set; } = string.Empty;
        public string SecretKey { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = string.Empty;
        public string DataApiBaseUrl { get; set; } = "https://api-t1.fyers.in";
    }
}
