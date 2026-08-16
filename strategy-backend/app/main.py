from fastapi import FastAPI
import redis.asyncio as redis
import asyncio
import json
import os
import logging

# Set up basic logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("StrategyEngine")

app = FastAPI(
    title="Polyglot Strategy Engine API",
    description="Consumes live market data, executes quantitative logic, and issues trade signals.",
    version="1.0.0"
)

REDIS_URL = os.getenv("REDIS_URL", "redis://localhost:6379/0")
redis_client = None
background_tasks = set()

async def market_data_listener(r_client: redis.Redis):
    """Background task that listens for live ticks from the C# execution engine."""
    pubsub = r_client.pubsub()
    await pubsub.subscribe("live_market_data")
    
    logger.info("Strategy Engine is now listening for live market data...")
    
    try:
        async for message in pubsub.listen():
            if message["type"] == "message":
                data = message["data"]
                
                # 1. Parse the incoming tick from C#
                tick = json.loads(data)
                symbol = tick.get("symbol")
                ltp = tick.get("ltp")
                
                logger.info(f"Analyzing {symbol} at ₹{ltp}")
                
                # 2. TODO: Run your specific BankNifty/Sensex trading strategy here!
                
                # 3. Example of firing a signal back to C#
                # if strategy_condition_met:
                #     await fire_trade_signal(r_client, symbol, "BUY", 15)
                
    except asyncio.CancelledError:
        logger.info("Market data listener stopped.")
    finally:
        await pubsub.unsubscribe("live_market_data")

async def fire_trade_signal(r_client: redis.Redis, symbol: str, action: str, quantity: int):
    """Publishes a trade execution command back to the C# engine."""
    signal = {
        "symbol": symbol,
        "action": action,      # "BUY" or "SELL"
        "quantity": quantity,
        "order_type": "MARKET"
    }
    
    logger.info(f"FIRING SIGNAL: {action} {quantity}x {symbol}")
    await r_client.publish("trade_signals", json.dumps(signal))

@app.on_event("startup")
async def startup_event():
    global redis_client
    # Connect to Redis
    redis_client = redis.from_url(REDIS_URL, encoding="utf-8", decode_responses=True)
    await redis_client.ping()
    
    # Start the listener as a background asyncio task
    task = asyncio.create_task(market_data_listener(redis_client))
    background_tasks.add(task)
    task.add_done_callback(background_tasks.discard)

@app.on_event("shutdown")
async def shutdown_event():
    # Clean up background tasks and connections
    for task in background_tasks:
        task.cancel()
    if redis_client:
        await redis_client.close()

@app.get("/")
async def root():
    return {"status": "online", "service": "strategy-backend"}