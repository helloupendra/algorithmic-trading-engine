using ExecutionEngine;
using ExecutionEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        // 1. Register Redis globally as a Singleton
        var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
        var multiplexer = ConnectionMultiplexer.Connect(redisUrl);
        services.AddSingleton<IConnectionMultiplexer>(multiplexer);

        // 2. Register our database repository
        services.AddSingleton<TickRepository>();
        
        // 3. Register our FYERS WebSocket client 
        services.AddSingleton<FyersWebSocketClient>();
        
        // 4. Register our main background listener
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();