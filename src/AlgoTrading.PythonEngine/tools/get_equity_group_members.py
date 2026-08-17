import requests
from core.api_client import api_get, api_post, api_delete, api_put
import argparse
import urllib3
import os

# Disable SSL warnings for localhost
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

import sys
import os
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
from core.config import API_BASE_URL

def get_group_members(group_name: str):
    print(f"Fetching members for Equity Group: {group_name}")
    url = f"{API_BASE_URL}/api/Equities/groups/{group_name}/members"
    
    try:
        response = api_get(url, verify=False)
        response.raise_for_status()
        
        members = response.json()
        print(f"Found {len(members)} stocks in '{group_name}':\n")
        
        # Display the results neatly
        for idx, member in enumerate(members, 1):
            symbol = member.get('symbol', 'Unknown Symbol')
            name = member.get('name', 'Unknown Name')
            weightage = member.get('weightage', 0.0)
            
            print(f"{idx:2d}. {symbol:<20} | {name:<40} | Weight: {weightage}%")
            
    except requests.exceptions.HTTPError as err:
        if response.status_code == 404:
             print(f"Error: Equity group '{group_name}' not found. Check the group name.")
        else:
             print(f"HTTP Error: {err}")
    except Exception as e:
        print(f"An error occurred: {e}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Get stocks for an equity group")
    parser.add_argument(
        "--group", 
        type=str, 
        default="BANKNIFTY_CONSTITUENTS", 
        help="The name of the equity group (e.g., 'BANKNIFTY_CONSTITUENTS', 'NIFTY50_CONSTITUENTS')"
    )
    args = parser.parse_args()
    
    get_group_members(args.group)
