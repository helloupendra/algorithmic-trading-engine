using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Instruments;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;

namespace AlgoTrading.Infrastructure.Services
{
    /// <summary>
    /// Implementation of <see cref="IInstrumentImportService"/> that reads a master instrument CSV file downloaded from the broker.
    /// Parses symbols, derivatives metadata, and populates the local Instruments table.
    /// </summary>
    public class LocalCsvInstrumentImportService : IInstrumentImportService
    {
        private readonly TradingDbContext _dbContext;
        private readonly ILogger<LocalCsvInstrumentImportService> _logger;

        public LocalCsvInstrumentImportService(
            TradingDbContext dbContext,
            ILogger<LocalCsvInstrumentImportService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<ImportInstrumentsResponse> ImportFromLocalCsvAsync(
            string filepath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filepath))
                throw new ArgumentException("File path is required.", nameof(filepath));

            filepath = filepath.Trim(' ', '"', '\'');

            if (!File.Exists(filepath))
                throw new FileNotFoundException($"CSV file not found. Path received: '{filepath}'", filepath);

            int totalRowsRead = 0;
            int inserted = 0;
            int updated = 0;
            int skipped = 0;

            string fileName = Path.GetFileName(filepath).ToUpperInvariant();
            string segmentFromFile = GetSegmentFromFile(fileName);

            using var parser = new TextFieldParser(filepath);
            parser.TextFieldType = FieldType.Delimited;
            parser.SetDelimiters(",");

            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string[]? fields;

                try
                {
                    fields = parser.ReadFields();
                }
                catch (MalformedLineException ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed CSV line.");
                    skipped++;
                    continue;
                }

                if (fields is null || fields.Length < 14)
                {
                    skipped++;
                    continue;
                }

                totalRowsRead++;

                string description = GetField(fields, 1);
                string tickSizeRaw = GetField(fields, 4);
                string isin = GetField(fields, 5);
                string fullSymbol = GetField(fields, 9);
                string shortSymbol = GetField(fields, 13);

                if (string.IsNullOrWhiteSpace(fullSymbol))
                {
                    skipped++;
                    continue;
                }

                string exchange = GetExchangeFromSymbol(fullSymbol);
                if (string.IsNullOrWhiteSpace(exchange))
                {
                    exchange = GetExchangeFromFileName(fileName);
                }

                string instrumentType = ExtractInstrumentType(fullSymbol);

                decimal? tickSize = null;
                if (decimal.TryParse(tickSizeRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedTick))
                {
                    tickSize = parsedTick;
                }

                // Default values for non-derivative files
                string underlying = string.Empty;
                decimal? strikePrice = null;
                string optionType = string.Empty;
                DateOnly? expiryDate = null;

                // Extra parsing for derivatives
                if (segmentFromFile == "FO" || segmentFromFile == "COM")
                {
                    var parsed = ParseDerivativeFields(fullSymbol, description);
                    underlying = parsed.underlying;
                    strikePrice = parsed.strike;
                    optionType = parsed.optionType;

                    // ✅ Correct expiry extraction for FO rows:
                    // Example description: "BANKNIFTY 30 Jun 26 28000 CE"
                    // -> ExpiryDate = 2026-06-30
                    expiryDate = TryExtractExpiryFromDescription(description);
                }

                var existing = await _dbContext.Instruments
                    .FirstOrDefaultAsync(x => x.Symbol == fullSymbol, cancellationToken);

                if (existing is null)
                {
                    var entity = new Instrument
                    {
                        Symbol = fullSymbol,
                        Exchange = exchange,
                        Segment = segmentFromFile,
                        Description = description,
                        InstrumentType = instrumentType,
                        Isin = isin,
                        TickSize = tickSize,
                        ExpiryDate = expiryDate,

                        Underlying = underlying,
                        StrikePrice = strikePrice,
                        OptionType = optionType,

                        IsEnabled = true,
                        Priority = 0,
                        CreatedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow
                    };

                    await _dbContext.Instruments.AddAsync(entity, cancellationToken);
                    inserted++;
                }
                else
                {
                    bool changed = false;

                    if (existing.Exchange != exchange)
                    {
                        existing.Exchange = exchange;
                        changed = true;
                    }

                    if (existing.Segment != segmentFromFile)
                    {
                        existing.Segment = segmentFromFile;
                        changed = true;
                    }

                    if (existing.Description != description)
                    {
                        existing.Description = description;
                        changed = true;
                    }

                    if (existing.InstrumentType != instrumentType)
                    {
                        existing.InstrumentType = instrumentType;
                        changed = true;
                    }

                    if (existing.Isin != isin)
                    {
                        existing.Isin = isin;
                        changed = true;
                    }

                    if (existing.TickSize != tickSize)
                    {
                        existing.TickSize = tickSize;
                        changed = true;
                    }

                    if (existing.ExpiryDate != expiryDate)
                    {
                        existing.ExpiryDate = expiryDate;
                        changed = true;
                    }

                    if (existing.Underlying != underlying)
                    {
                        existing.Underlying = underlying;
                        changed = true;
                    }

                    if (existing.StrikePrice != strikePrice)
                    {
                        existing.StrikePrice = strikePrice;
                        changed = true;
                    }

                    if (existing.OptionType != optionType)
                    {
                        existing.OptionType = optionType;
                        changed = true;
                    }

                    if (changed)
                    {
                        existing.UpdatedUtc = DateTime.UtcNow;
                        updated++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            return new ImportInstrumentsResponse
            {
                TotalRowsRead = totalRowsRead,
                Inserted = inserted,
                Updated = updated,
                Skipped = skipped,
                Message = "Instrument import completed successfully."
            };
        }

        private static string GetField(string[] fields, int index)
        {
            if (fields is null || index < 0 || index >= fields.Length)
                return string.Empty;

            return fields[index]?.Trim() ?? string.Empty;
        }

        private static string GetSegmentFromFile(string fileName)
        {
            if (fileName.Contains("_FO"))
                return "FO";

            if (fileName.Contains("_CM"))
                return "CM";

            if (fileName.Contains("_COM"))
                return "COM";

            return string.Empty;
        }

        private static string GetExchangeFromFileName(string fileName)
        {
            if (fileName.StartsWith("NSE_"))
                return "NSE";

            if (fileName.StartsWith("BSE_"))
                return "BSE";

            if (fileName.StartsWith("MCX_"))
                return "MCX";

            return string.Empty;
        }

        private static string GetExchangeFromSymbol(string fullSymbol)
        {
            if (string.IsNullOrWhiteSpace(fullSymbol))
                return string.Empty;

            var split = fullSymbol.Split(':', 2);
            return split.Length == 2 ? split[0].Trim().ToUpperInvariant() : string.Empty;
        }

        private static string ExtractInstrumentType(string fullSymbol)
        {
            if (string.IsNullOrWhiteSpace(fullSymbol))
                return string.Empty;

            var exchangeSplit = fullSymbol.Split(':', 2);
            if (exchangeSplit.Length != 2)
                return string.Empty;

            var symbolPart = exchangeSplit[1];

            // CM example: NSE:SBIN-EQ -> EQ
            var typeSplit = symbolPart.Split('-', 2);
            if (typeSplit.Length == 2)
            {
                return typeSplit[1].Trim().ToUpperInvariant();
            }

            // FO options/futures encoded in symbol
            if (symbolPart.EndsWith("CE", StringComparison.OrdinalIgnoreCase))
                return "CE";

            if (symbolPart.EndsWith("PE", StringComparison.OrdinalIgnoreCase))
                return "PE";

            if (symbolPart.Contains("FUT", StringComparison.OrdinalIgnoreCase))
                return "FUT";

            return string.Empty;
        }

        private static (string underlying, decimal? strike, string optionType) ParseDerivativeFields(
            string fullSymbol,
            string description)
        {
            if (string.IsNullOrWhiteSpace(fullSymbol))
                return (string.Empty, null, string.Empty);

            string symbolPart = fullSymbol;
            var split = fullSymbol.Split(':', 2);
            if (split.Length == 2)
                symbolPart = split[1];

            string optionType = string.Empty;
            if (symbolPart.EndsWith("CE", StringComparison.OrdinalIgnoreCase))
                optionType = "CE";
            else if (symbolPart.EndsWith("PE", StringComparison.OrdinalIgnoreCase))
                optionType = "PE";

            decimal? strike = null;

            if (optionType == "CE" || optionType == "PE")
            {
                var descParts = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (descParts.Length >= 2)
                {
                    var possibleStrike = descParts[descParts.Length - 2];
                    if (decimal.TryParse(possibleStrike, NumberStyles.Any, CultureInfo.InvariantCulture, out var descStrike))
                    {
                        strike = descStrike;
                    }
                }
            }

            if (strike == null)
            {
                var strikeMatch = Regex.Match(symbolPart, @"(\d{4,6})(CE|PE)$", RegexOptions.IgnoreCase);
                if (strikeMatch.Success &&
                    decimal.TryParse(strikeMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedStrike))
                {
                    strike = parsedStrike;
                }
                else
                {
                    // Fallback: try any numeric token in the symbol
                    var parts = Regex.Split(symbolPart, @"[^0-9]+");
                    foreach (var part in parts)
                    {
                        if (decimal.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var fallbackStrike) &&
                            fallbackStrike >= 1000)
                        {
                            strike = fallbackStrike;
                        }
                    }
                }
            }

            string underlying = string.Empty;

            if (symbolPart.Contains("BANKNIFTY", StringComparison.OrdinalIgnoreCase))
                underlying = "BANKNIFTY";
            else if (symbolPart.Contains("FINNIFTY", StringComparison.OrdinalIgnoreCase))
                underlying = "FINNIFTY";
            else if (symbolPart.Contains("MIDCPNIFTY", StringComparison.OrdinalIgnoreCase))
                underlying = "MIDCPNIFTY";
            else if (symbolPart.Contains("NIFTY", StringComparison.OrdinalIgnoreCase))
                underlying = "NIFTY";

            // Fallback: description
            if (string.IsNullOrWhiteSpace(underlying) && !string.IsNullOrWhiteSpace(description))
            {
                if (description.Contains("BANKNIFTY", StringComparison.OrdinalIgnoreCase))
                    underlying = "BANKNIFTY";
                else if (description.Contains("FINNIFTY", StringComparison.OrdinalIgnoreCase))
                    underlying = "FINNIFTY";
                else if (description.Contains("MIDCPNIFTY", StringComparison.OrdinalIgnoreCase))
                    underlying = "MIDCPNIFTY";
                else if (description.Contains("NIFTY", StringComparison.OrdinalIgnoreCase))
                    underlying = "NIFTY";
            }

            return (underlying, strike, optionType);
        }

        /// <summary>
        /// Extracts real expiry date from description such as:
        /// "BANKNIFTY 30 Jun 26 28000 CE"
        /// "SENSEX 27 Mar 27 60000 PE"
        /// </summary>
        private static DateOnly? TryExtractExpiryFromDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return null;

            // Match patterns like:
            // 30 Jun 26
            // 9 Jan 27
            // 29 Dec 2026
            var match = Regex.Match(
                description,
                @"\b(\d{1,2}\s+[A-Za-z]{3}\s+\d{2,4})\b",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return null;

            var expiryText = Regex.Replace(match.Groups[1].Value.Trim(), @"\s+", " ");

            string[] formats =
            {
                "d MMM yy",
                "dd MMM yy",
                "d MMM yyyy",
                "dd MMM yyyy"
            };

            if (DateTime.TryParseExact(
                expiryText,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
            {
                return DateOnly.FromDateTime(parsedDate);
            }

            return null;
        }
    }
}
