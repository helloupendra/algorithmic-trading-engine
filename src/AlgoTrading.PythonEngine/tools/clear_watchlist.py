import requests
import urllib3
import os

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL

def clear_watchlist():
    print("Fetching active watchlist...")
    url = f"{API_BASE_URL}/api/LiveData/watchlist"
    
    try:
        response = requests.get(url, verify=False)
        response.raise_for_status()
        
        items = response.json()
        print(f"Found {len(items)} items in the watchlist. Deleting them...")
        
        for item in items:
            item_id = item.get('id')
            if item_id:
                delete_url = f"{API_BASE_URL}/api/LiveData/watchlist/{item_id}"
                requests.delete(delete_url, verify=False)
                
        print("Watchlist cleared successfully! The Python terminal will automatically sync and unsubscribe in a few seconds.")
            
    except requests.exceptions.RequestException as err:
        print(f"Error: {err}")

if __name__ == "__main__":
    clear_watchlist()
