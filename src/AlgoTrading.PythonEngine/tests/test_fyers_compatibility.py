"""Offline transport contract smoke checks for the reviewed FYERS wheel.

These exercise the installed SDK, requests/aiohttp and its logging/resource imports;
no broker account, network connection or order is used.
"""
import asyncio
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from aiohttp import web
from fyers_apiv3 import fyersModel
from fyers_apiv3.FyersWebsocket import data_ws, order_ws
import requests


class FyersCompatibilityTests(unittest.TestCase):
    def test_sync_profile_uses_supported_requests_and_logger(self):
        with tempfile.TemporaryDirectory() as folder:
            client = fyersModel.FyersModel(client_id='fixture', token='fixture', log_path=folder)
            response = requests.Response()
            response.status_code = 200
            response._content = b'{"s":"ok","code":200,"data":{"name":"fixture"}}'
            with patch.object(client.service.session, 'get', return_value=response) as get:
                self.assertEqual('fixture', client.get_profile()['data']['name'])
                self.assertTrue(get.call_args.kwargs['url'].endswith('/profile'))
            client.service.session.close()

    def test_async_profile_over_a_real_local_http_connection(self):
        async def scenario(folder):
            async def profile(request):
                self.assertEqual('fixture:fixture', request.headers['Authorization'])
                return web.json_response({'s': 'ok', 'code': 200})
            app = web.Application()
            app.router.add_get('/profile', profile)
            runner = web.AppRunner(app)
            await runner.setup()
            site = web.TCPSite(runner, '127.0.0.1', 0)
            await site.start()
            port = site._server.sockets[0].getsockname()[1]
            client = fyersModel.FyersModel(is_async=True, client_id='fixture', token='fixture', log_path=folder)
            try:
                with patch.object(fyersModel.Config, 'API', f'http://127.0.0.1:{port}'):
                    self.assertEqual({'s': 'ok', 'code': 200}, await client.get_profile())
            finally:
                if client.service.session is not None:
                    await client.service.session.close()
                await runner.cleanup()
        with tempfile.TemporaryDirectory() as folder:
            asyncio.run(scenario(folder))

    def test_websocket_mapping_is_loaded_without_setuptools(self):
        with tempfile.TemporaryDirectory() as folder:
            socket = data_ws.FyersDataSocket(access_token='fixture', log_path=folder)
            self.assertTrue(socket.data_val)
            self.assertTrue(json.loads(Path(data_ws.resource_filename('fyers_apiv3.FyersWebsocket', 'map.json')).read_text()))
            self.assertTrue(hasattr(order_ws, 'FyersOrderSocket'))


if __name__ == '__main__':
    unittest.main()
