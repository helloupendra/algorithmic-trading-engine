using System.Net;
using System.Text;
using AlgoTrading.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Stand-ins for the brokers and the platform the portfolio code talks to: no test here reaches a network.
/// </summary>
/// <remarks>
/// The fixtures under Fixtures/Portfolio are the brokers' own answer shapes (FYERS API v3, DhanHQ v2, Angel One
/// SmartAPI) with every value made up: no real account, client id or token is in them.
/// </remarks>
internal static class PortfolioTestKit
{
    public static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "Portfolio")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "Portfolio", name));
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "AlgoTrading.Infrastructure")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One request as the fake broker saw it, with the headers it carried.</summary>
    public sealed record Seen(string Method, string Url, string? Body, IReadOnlyDictionary<string, string> Headers)
    {
        public string Path => new Uri(Url).AbsolutePath;
    }

    /// <summary>A broker that answers by method and path, and records every request.</summary>
    public sealed class FakeBroker : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode, string)>> _answers = new(StringComparer.Ordinal);

        public List<Seen> Requests { get; } = [];

        /// <summary>Answers <paramref name="path"/> with these, one per call, the last one repeated.</summary>
        public FakeBroker On(string method, string path, HttpStatusCode status, string body)
        {
            string key = $"{method} {path}";
            if (!_answers.TryGetValue(key, out var queue)) _answers[key] = queue = new Queue<(HttpStatusCode, string)>();
            queue.Enqueue((status, body));
            return this;
        }

        public FakeBroker Get(string path, string body) => On("GET", path, HttpStatusCode.OK, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), body, headers));

            string key = $"{request.Method.Method} {request.RequestUri.AbsolutePath}";
            if (!_answers.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"no such call\"}") };
            }

            var (status, json) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>The Connectors page's saved app credentials, as the platform's FYERS app would have them.</summary>
    public sealed class Credentials(BrokerCredentials? fyers = null) : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(providerKey == "fyers" && fyers is not null
                ? fyers
                : new BrokerCredentials(string.Empty, string.Empty, string.Empty, null, "none", null, null));

        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy,
            string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The portfolio never saves app credentials.");
    }

    /// <summary>A JWT-shaped token whose payload says when it ends; the signature is not a real one, as nothing checks it.</summary>
    public static string Jwt(DateTime expiresUtc)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        long exp = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $"{B64("{\"alg\":\"HS256\"}")}.{B64($"{{\"sub\":\"test\",\"exp\":{exp}}}")}.c2lnbmF0dXJl";
    }
}
