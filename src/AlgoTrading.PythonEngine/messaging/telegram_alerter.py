import os
import requests
import threading
import queue
from typing import Optional

class TelegramAlerter:
    """
    Thread-safe Telegram Alert Module for sending trading signals.
    Uses a background worker thread and queue.Queue to prevent blocking
    or crashing the main execution runner loop.
    """
    
    def __init__(self, bot_token: Optional[str] = None, chat_id: Optional[str] = None):
        self.bot_token = bot_token or os.getenv("TELEGRAM_BOT_TOKEN")
        self.chat_id = chat_id or os.getenv("TELEGRAM_CHAT_ID")
        self.base_url = f"https://api.telegram.org/bot{self.bot_token}"
        
        # Setup background worker for async dispatching
        self._queue = queue.Queue()
        self._worker_thread = threading.Thread(target=self._worker, daemon=True)
        self._worker_thread.start()

        if not self.bot_token or not self.chat_id:
            print("WARNING: Telegram Bot Token or Chat ID not configured. Alerts will be printed only.")

    def _worker(self):
        """Background thread that processes and sends alerts sequentially."""
        while True:
            message = self._queue.get()
            if message is None:
                break # Poison pill for graceful shutdown
            self._send_sync(message)
            self._queue.task_done()

    def format_alert(
        self, 
        signal_type: str, 
        logic_reason: str, 
        action: str, 
        premium: float, 
        support: float, 
        resistance: float
    ) -> str:
        """
        Formats the alert to exactly match the requested style.
        """
        return (
            f"🚨 ALERT: {signal_type}!\n"
            f"📉 Logic: {logic_reason}\n"
            f"🎯 Action: {action}\n"
            f"💰 Current Premium: ₹{premium}\n"
            f"🚧 Support: {support} | Resistance: {resistance}"
        )

    def _send_sync(self, message: str):
        """Sends the Telegram alert synchronously using requests (executed by worker thread)."""
        if not self.bot_token or not self.chat_id:
            print(f"[DRY-RUN TELEGRAM ALERT]\n{message}\n")
            return
            
        url = f"{self.base_url}/sendMessage"
        payload = {
            "chat_id": self.chat_id,
            "text": message,
            "parse_mode": "HTML"
        }
        
        try:
            response = requests.post(url, json=payload, timeout=10)
            response.raise_for_status()
        except Exception as e:
            print(f"Failed to send Telegram alert: {e}")

    def send_alert_async(self, message: str):
        """
        Dispatches the Telegram alert asynchronously via thread-safe queue.
        Call this directly from on_bar() without asyncio.run().
        """
        self._queue.put(message)
