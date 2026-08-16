using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ExecutionEngine.Services
{
    public class FyersWebSocketClient
    {
        private readonly ILogger<FyersWebSocketClient> _logger;
        private readonly TickRepository _tickRepository;
        private readonly ISubscriber _redisPublisher;
        private ClientWebSocket _webSocket;
        
        // The FYERS Data WebSocket Endpoint
        private const string FyersDataWebSocketUrl = "wss://api.fyers.in/socket/v3/dataV3";

        public FyersWebSocketClient(
            ILogger<FyersWebSocketClient> logger, 
            TickRepository tickRepository,
            IConnectionMultiplexer redis)
        {
            _logger = logger;
            _tickRepository = tickRepository;
            _redisPublisher = redis.GetSubscriber();
            _webSocket = new ClientWebSocket();
        }

        public async Task ConnectAsync(string appId, string accessToken, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Attempting to connect to FYERS WebSocket...");

                // Constructing the connection URI with authentication tokens
                var connectionUri = new Uri($"{FyersDataWebSocketUrl}?access_token={appId}:{accessToken}");
                
                await _webSocket.ConnectAsync(connectionUri, cancellationToken);
                
                if (_webSocket.State == WebSocketState.Open)
                {
                    _logger.LogInformation("Successfully connected to FYERS Market Data Stream.");
                    
                    // Start the background listening loop without blocking the main thread
                    _ = ReceiveLoopAsync(cancellationToken); 
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to FYERS WebSocket.");
                throw;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            // 4KB buffer for incoming tick data
            var buffer = new byte[1024 * 4]; 

            try
            {
                while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("WebSocket closed by the broker.");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", cancellationToken);
                        break;
                    }

                    // Decode the raw byte stream into a JSON string
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    
                    try 
                    {
                        // 1. Parse the incoming JSON tick
                        var json = JObject.Parse(message);
                        string symbol = json["symbol"]?.ToString() ?? "UNKNOWN";
                        double ltp = json["ltp"]?.Value<double>() ?? 0;
                        long volume = json["vol"]?.Value<long>() ?? 0;

                        // 2. Distribute Live: Publish to Redis for Python strategy engine
                        await _redisPublisher.PublishAsync("live_market_data", message);

                        // 3. Store Historical: Save to TimescaleDB for backtesting
                        await _tickRepository.InsertTickAsync(symbol, ltp, volume);
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogWarning("Failed to parse tick data: {Error}", parseEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving data from WebSocket.");
            }
        }
        
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "System shutdown", cancellationToken);
                _webSocket.Dispose();
                _logger.LogInformation("FYERS WebSocket disconnected cleanly.");
            }
        }
    }
}