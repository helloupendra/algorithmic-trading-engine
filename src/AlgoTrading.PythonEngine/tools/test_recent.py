import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))
from core.api_client import PlatformApiClient
from core.config import API_BASE_URL

api = PlatformApiClient(API_BASE_URL, verify_ssl=False)
raw_idx = api.get_recent_bars("NSE:NIFTYBANK-INDEX", "5m", 5)
for r in raw_idx:
    print(r.get("barStartUtc"))
