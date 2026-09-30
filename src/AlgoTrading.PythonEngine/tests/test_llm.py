import unittest

import requests

import _bootstrap  # noqa: F401

from core.llm import DEFAULT_CHAINS, LlmClient, LlmError, chain_for, parse_json

KEY = "nvapi-secret-test-key"


class FakeResponse:
    def __init__(self, status, body=None, bad_json=False):
        self.status_code = status
        self._body = body or {}
        self._bad_json = bad_json

    def json(self):
        if self._bad_json:
            raise ValueError("not json")
        return self._body


def ok(text, tokens=10):
    return FakeResponse(200, {"choices": [{"message": {"content": text}}], "usage": {"completion_tokens": tokens}})


class FakeSession:
    """Answers per model from a script; records which models were asked."""

    def __init__(self, script):
        self.script = script
        self.asked = []
        self.headers_seen = []

    def post(self, url, timeout, headers, json):
        self.asked.append(json["model"])
        self.headers_seen.append(headers)
        step = self.script[json["model"]]
        if isinstance(step, Exception):
            raise step
        return step


class ChainTests(unittest.TestCase):
    def test_nemotron_ultra_is_asked_first_for_judgement(self):
        self.assertEqual("nvidia/nemotron-3-ultra-550b-a55b", DEFAULT_CHAINS["judge"][0])

    def test_the_environment_can_replace_a_chain(self):
        self.assertEqual(("a/one", "b/two"), chain_for("judge", {"LLM_MODELS_JUDGE": "a/one, b/two"}))
        self.assertEqual(DEFAULT_CHAINS["extract"], chain_for("extract", {}))
        with self.assertRaises(ValueError):
            chain_for("oracle", {})


class AskTests(unittest.TestCase):
    def test_the_first_model_that_answers_wins(self):
        judge = DEFAULT_CHAINS["judge"]
        session = FakeSession({judge[0]: ok("REJECT: risk is 3,900 against a 1,000 limit")})
        answer = LlmClient(KEY, session=session, env={}).ask("plan", system="You are the validator.")
        self.assertEqual(judge[0], answer.model)
        self.assertIn("REJECT", answer.text)
        self.assertEqual([judge[0]], session.asked)
        self.assertEqual("Bearer " + KEY, session.headers_seen[0]["Authorization"])

    def test_a_timeout_a_busy_model_and_an_empty_answer_each_move_to_the_next(self):
        # 30 Sep: deepseek-v4.1-flash gave nothing in 90 s while glm-5.3-flash answered.
        env = {"LLM_MODELS_JUDGE": "m/slow,m/busy,m/thinker,m/answers"}
        session = FakeSession({
            "m/slow": requests.Timeout("read timed out"),
            "m/busy": FakeResponse(429),
            "m/thinker": ok("   "),       # the whole budget went on reasoning
            "m/answers": ok("fine"),
        })
        answer = LlmClient(KEY, session=session, env=env).ask("q")
        self.assertEqual("m/answers", answer.model)
        self.assertEqual(["timeout", "http 429", "empty", "ok"], [a.outcome for a in answer.attempts])

    def test_when_nothing_answers_the_error_names_each_try_and_never_the_key(self):
        env = {"LLM_MODELS_JUDGE": "m/a,m/b"}
        session = FakeSession({"m/a": requests.ConnectionError(f"https://x?key={KEY}"), "m/b": FakeResponse(503)})
        with self.assertRaises(LlmError) as caught:
            LlmClient(KEY, session=session, env=env).ask("q")
        message = str(caught.exception)
        self.assertIn("m/a: unreachable (ConnectionError)", message)
        self.assertIn("m/b: http 503", message)
        self.assertNotIn(KEY, message)

    def test_a_json_answer_is_parsed_and_prose_moves_on(self):
        env = {"LLM_MODELS_EXTRACT": "m/prose,m/json"}
        session = FakeSession({"m/prose": ok("The sentiment is positive."),
                               "m/json": ok('Here you go:\n```json\n{"sentiment": 0.7, "tickers": ["TCS"]}\n```')})
        answer = LlmClient(KEY, session=session, env=env).ask("score", tier="extract", want_json=True)
        self.assertEqual("m/json", answer.model)
        self.assertEqual({"sentiment": 0.7, "tickers": ["TCS"]}, answer.data)

    def test_a_missing_key_says_where_to_put_it(self):
        with self.assertRaises(LlmError) as caught:
            LlmClient("", session=FakeSession({}))
        self.assertIn(".env", str(caught.exception))


class ParseJsonTests(unittest.TestCase):
    def test_finds_the_first_object_or_array_in_prose_or_fences(self):
        self.assertEqual({"a": 1}, parse_json('verdict: {"a": 1} done'))
        self.assertEqual([1, 2], parse_json("```\n[1, 2]\n```"))
        self.assertEqual({"ok": True}, parse_json('noise {bad json} then {"ok": true}'))
        with self.assertRaises(ValueError):
            parse_json("no json here")


if __name__ == "__main__":
    unittest.main()
