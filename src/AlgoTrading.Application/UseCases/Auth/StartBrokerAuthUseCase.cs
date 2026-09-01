using AlgoTrading.Application.Interfaces;

namespace AlgoTrading.Application.UseCases.Auth;

/// <summary>
/// Use case for initiating the broker authentication flow.
/// </summary>
public class StartBrokerAuthUseCase
{
    private readonly IBrokerAuthService _brokerAuthService;

    /// <summary>
    /// Initializes a new instance of <see cref="StartBrokerAuthUseCase"/>.
    /// </summary>
    public StartBrokerAuthUseCase(IBrokerAuthService brokerAuthService)
    {
        _brokerAuthService = brokerAuthService;
    }

    /// <summary>
    /// Triggers the start of the authentication process.
    /// </summary>
    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        return _brokerAuthService.StartAuthFlowAsync(cancellationToken);
    }
}