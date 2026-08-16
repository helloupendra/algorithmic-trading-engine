using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Instruments;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.UseCases.Instruments;

/// <summary>
/// Use case for bulk importing instrument master data from a local CSV file.
/// </summary>
public class ImportInstrumentsFromFileUseCase
{
    private readonly IInstrumentImportService _instrumentImportService;

    /// <summary>
    /// Initializes a new instance of <see cref="ImportInstrumentsFromFileUseCase"/>.
    /// </summary>
    public ImportInstrumentsFromFileUseCase(IInstrumentImportService instrumentImportService)
    {
        _instrumentImportService = instrumentImportService;
    }

    /// <summary>
    /// Executes the import process based on the provided request parameters.
    /// </summary>
    public Task<ImportInstrumentsResponse> ExecuteAsync(
        ImportInstrumentsRequest request,
        CancellationToken cancellationToken = default)
    {
        return _instrumentImportService.ImportFromLocalCsvAsync(request.FilePath, cancellationToken);
    }
}

