using AlgoTrading.Application.Providers;

namespace AlgoTrading.Application.UseCases.Auth
{
    /// <summary>
    /// Use case for exchanging an authorization code for a broker session.
    /// </summary>
    public class GenerateAccessTokenUseCase
    {
        private readonly IProviderRouter _router;

        public GenerateAccessTokenUseCase(IProviderRouter router)
        {
            _router = router;
        }

        /// <summary>
        /// Exchanges the callback's auth code with the broker bound to an account
        /// — the shared platform account when <paramref name="brokerAccountId"/>
        /// is null.
        /// </summary>
        public async Task<BrokerTokenResult> ExecuteAsync(
            string authCode,
            long? brokerAccountId = null,
            CancellationToken cancellationToken = default)
        {
            var broker = await _router.ResolveBrokerAsync(brokerAccountId, cancellationToken);

            return await broker.ExchangeAuthCodeAsync(authCode, cancellationToken);
        }
    }
}
