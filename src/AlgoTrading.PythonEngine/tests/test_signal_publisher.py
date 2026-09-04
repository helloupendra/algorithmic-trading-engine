"""
UI signal feed routing: the runner posts to the run-scoped route, falls back to
the strategy-scoped route only on an unrouted 404 (older API), and never
re-routes a 404 that the new controller answered with a message.
"""

import unittest
from typing import Any, Dict, List, Optional, Tuple

import _bootstrap  # noqa: F401

from strategies.signal_utils import UiSignalPublisher


class FakeResponse:
    def __init__(self, status_code: int, body: Optional[Dict[str, Any]] = None, text: str = "") -> None:
        self.status_code = status_code
        self._body = body
        self.text = text

    def json(self) -> Any:
        if self._body is None:
            raise ValueError("no JSON body")
        return self._body


class FakeHttp:
    """Answers each URL from a scripted table and records every post."""

    def __init__(self, answers: Dict[str, List[FakeResponse]]) -> None:
        self._answers = {k: list(v) for k, v in answers.items()}
        self.posts: List[Tuple[str, Dict[str, Any]]] = []

    def post(self, url: str, json: Dict[str, Any], timeout: float) -> FakeResponse:
        self.posts.append((url, json))
        queue = self._answers.get(url)
        if not queue:
            raise AssertionError(f"unexpected POST {url}")
        return queue.pop(0) if len(queue) > 1 else queue[0]


BASE = "https://localhost:5025/"
RUN_URL = "https://localhost:5025/api/Strategy/runs/77/signals"
LEGACY_URL = "https://localhost:5025/api/Strategy/3/signals"
PAYLOAD = {"signal_type": "OPEN_GROUP", "reason": "test", "legs": [], "metadata": {}}


class UiSignalPublisherTests(unittest.TestCase):
    def test_posts_to_run_route_when_it_exists(self) -> None:
        http = FakeHttp({RUN_URL: [FakeResponse(200)]})
        pub = UiSignalPublisher(http, BASE, 77, 3)

        self.assertTrue(pub.publish(PAYLOAD))
        self.assertTrue(pub.publish(PAYLOAD))
        self.assertEqual([u for u, _ in http.posts], [RUN_URL, RUN_URL])
        self.assertFalse(pub.use_legacy_route)
        self.assertEqual(http.posts[0][1], PAYLOAD)

    def test_unrouted_404_falls_back_to_legacy_and_pins_it(self) -> None:
        http = FakeHttp({RUN_URL: [FakeResponse(404)], LEGACY_URL: [FakeResponse(200)]})
        pub = UiSignalPublisher(http, BASE, 77, 3)

        self.assertTrue(pub.publish(PAYLOAD))
        self.assertEqual([u for u, _ in http.posts], [RUN_URL, LEGACY_URL])
        self.assertTrue(pub.use_legacy_route)

        # Once pinned, the missing route is not probed again.
        self.assertTrue(pub.publish(PAYLOAD))
        self.assertEqual([u for u, _ in http.posts][-1], LEGACY_URL)
        self.assertEqual(len(http.posts), 3)

    def test_404_with_message_is_not_rerouted(self) -> None:
        not_active = FakeResponse(404, {"message": "Run 77 is not currently active."}, text='{"message":"..."}')
        http = FakeHttp({RUN_URL: [not_active]})
        pub = UiSignalPublisher(http, BASE, 77, 3)

        self.assertFalse(pub.publish(PAYLOAD))
        self.assertEqual([u for u, _ in http.posts], [RUN_URL])
        self.assertFalse(pub.use_legacy_route)

    def test_non_404_failure_reports_without_fallback(self) -> None:
        http = FakeHttp({RUN_URL: [FakeResponse(500, text="boom")]})
        pub = UiSignalPublisher(http, BASE, 77, 3)

        self.assertFalse(pub.publish(PAYLOAD))
        self.assertEqual([u for u, _ in http.posts], [RUN_URL])
        self.assertFalse(pub.use_legacy_route)

    def test_no_run_id_uses_legacy_route_directly(self) -> None:
        http = FakeHttp({LEGACY_URL: [FakeResponse(200)]})
        pub = UiSignalPublisher(http, BASE, None, 3)

        self.assertTrue(pub.publish(PAYLOAD))
        self.assertEqual([u for u, _ in http.posts], [LEGACY_URL])

    def test_legacy_failure_is_reported(self) -> None:
        http = FakeHttp({RUN_URL: [FakeResponse(404)], LEGACY_URL: [FakeResponse(404, {"message": "inactive"})]})
        pub = UiSignalPublisher(http, BASE, 77, 3)

        self.assertFalse(pub.publish(PAYLOAD))
        self.assertTrue(pub.use_legacy_route)


if __name__ == "__main__":
    unittest.main()
