// src/AlgoTrading.Contracts/Options/BackfillOptionHistoryResponse.cs
namespace AlgoTrading.Contracts.Options;

public class BackfillOptionHistoryResponse
{
    public string Exchange { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;

    public DateOnly ExpiryDate { get; set; }
    public decimal AtmStrike { get; set; }

    public string Resolution { get; set; } = string.Empty;

    public int TotalContractsResolved { get; set; }
    public int TotalContractsFetched { get; set; }

    public int TotalCandlesInserted { get; set; }
    public int TotalCandlesUpdated { get; set; }
    public int TotalCandlesSkipped { get; set; }

    public List<string> Symbols { get; set; } = new();

    public string Message { get; set; } = string.Empty;
}