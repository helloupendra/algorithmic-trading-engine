import argparse
import time
import json
import psycopg2
from psycopg2.extras import RealDictCursor
from datetime import datetime, timezone
import os
from pathlib import Path
from dotenv import load_dotenv
import sys

# Append parent dir for redis_publisher import
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from messaging.redis_publisher import build_publisher_from_env, normalize_tick

# Resolve the repo-root .env from this file's location so the replayer works
# regardless of the directory it is launched from.
load_dotenv(dotenv_path=Path(__file__).resolve().parents[3] / ".env", override=False)

def run_replay(start_time: str, end_time: str, speed: float):
    print(f"Connecting to TimescaleDB to fetch data between {start_time} and {end_time}...")
    
    password = os.getenv("POSTGRES_PASSWORD") or os.getenv("DB_PASSWORD")
    if not password:
        raise RuntimeError(
            "POSTGRES_PASSWORD is not set. Copy .env.example to .env at the repo "
            "root and fill it in (see the PostgreSQL section)."
        )

    conn = psycopg2.connect(
        host=os.getenv("POSTGRES_HOST", os.getenv("DB_HOST", "localhost")),
        port=os.getenv("POSTGRES_PORT", os.getenv("DB_PORT", "5432")),
        dbname=os.getenv("POSTGRES_DB", os.getenv("DB_NAME", "algotrading")),
        user=os.getenv("POSTGRES_USER", os.getenv("DB_USER", "postgres")),
        password=password,
    )
    
    publisher = build_publisher_from_env()
    publisher.ensure_connection()

    cursor = conn.cursor(cursor_factory=RealDictCursor)
    
    query = """
    SELECT received_utc, raw_payload
    FROM market_ticks
    WHERE received_utc >= %s AND received_utc <= %s
    ORDER BY received_utc ASC
    """
    
    start_dt = datetime.fromisoformat(start_time).astimezone(timezone.utc)
    end_dt = datetime.fromisoformat(end_time).astimezone(timezone.utc)
    
    cursor.execute(query, (start_dt, end_dt))
    
    print("Executing query... this may take a moment for large datasets.")
    rows = cursor.fetchall()
    print(f"Found {len(rows)} ticks to replay.")
    
    if len(rows) == 0:
        print("No ticks found. Exiting.")
        return

    last_tick_time = None
    
    for row in rows:
        current_tick_time = row['received_utc']
        
        # Parse payload
        try:
            payload = json.loads(row['raw_payload'])
        except Exception as e:
            print(f"Error parsing raw_payload: {e}")
            continue
            
        normalized = normalize_tick(payload)
        
        # VERY IMPORTANT: Add the isReplay flag to trick the C# Backend 
        # so it updates LiveQuotesLatest but does NOT update LiveQuotes table directly
        normalized['isReplay'] = True
        
        publisher.publish_tick(normalized)
        
        if last_tick_time is not None and speed > 0:
            diff = (current_tick_time - last_tick_time).total_seconds()
            if diff > 0:
                time.sleep(diff / speed)
                
        last_tick_time = current_tick_time

    print("Replay completed.")
    cursor.close()
    conn.close()

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--start', required=True, help="ISO start time")
    parser.add_argument('--end', required=True, help="ISO end time")
    parser.add_argument('--speed', type=float, default=1.0, help="Replay speed multiplier")
    args = parser.parse_args()
    
    run_replay(args.start, args.end, args.speed)