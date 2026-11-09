"""API and Prometheus share one private, persistent scrape credential."""
import importlib.util
import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("metrics_settings_generator", REPO / "scripts" / "_gen_local_settings.py")
settings = importlib.util.module_from_spec(spec)
spec.loader.exec_module(settings)


class MetricsConfigurationTests(unittest.TestCase):
    def test_generation_preserves_other_settings_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            path.write_text('# existing settings\nPOSTGRES_HOST=localhost\nMETRICS_SCRAPE_TOKEN=\nOTHER="a=b"\n')
            env = settings.read_env(path)
            settings.ensure_metrics_token(path, env)
            token = env["METRICS_SCRAPE_TOKEN"]
            self.assertGreaterEqual(len(token), 32)
            self.assertTrue(token.startswith("metrics_"))
            self.assertIn('OTHER="a=b"', path.read_text())
            self.assertEqual(token, settings.build_api_settings(env)["Metrics"]["ScrapeToken"])
            before = path.read_bytes()
            again = settings.read_env(path)
            settings.ensure_metrics_token(path, again)
            self.assertEqual(before, path.read_bytes())
            self.assertEqual(token, again["METRICS_SCRAPE_TOKEN"])
            if os.name != "nt":
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))

    def test_missing_key_is_added_without_joining_the_previous_line(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            path.write_text("OTHER=unchanged")
            env = settings.read_env(path)
            settings.ensure_metrics_token(path, env)
            self.assertTrue(path.read_text().startswith("OTHER=unchanged\nMETRICS_SCRAPE_TOKEN="))

    def test_operator_supplied_token_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            token = "metrics_" + "t" * 43
            path.write_text(f'METRICS_SCRAPE_TOKEN="{token}"\n')
            before = path.read_bytes()
            env = settings.read_env(path)
            settings.ensure_metrics_token(path, env)
            self.assertEqual(before, path.read_bytes())
            self.assertEqual(token, settings.build_api_settings(env)["Metrics"]["ScrapeToken"])

    def test_invalid_explicit_token_is_refused_without_modifying_env(self):
        for token in ("short", "x" * 32 + " ", "x" * 16 + " " + "x" * 16):
            with self.subTest(token=token), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / ".env"
                path.write_text(f'METRICS_SCRAPE_TOKEN="{token}"\n')
                before = path.read_bytes()
                with self.assertRaises(ValueError):
                    settings.ensure_metrics_token(path, settings.read_env(path))
                self.assertEqual(before, path.read_bytes())

    def test_missing_setting_does_not_override_environment_configuration_with_a_blank(self):
        self.assertEqual({}, settings.build_api_settings({})["Metrics"])

    @unittest.skipIf(os.name == "nt", "container entrypoint is a POSIX shell script")
    def test_prometheus_receives_a_private_token_file_without_a_secret_in_arguments_or_environment(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            binary = root / "prometheus"
            binary.write_text('#!/bin/sh\nprintf "%s\\n" "$@"\ntest -z "${METRICS_SCRAPE_TOKEN:-}"\n')
            binary.chmod(0o700)
            token_file = root / "scrape-token"
            token_file.write_text("old")
            token_file.chmod(0o644)
            token = "metrics_" + "s" * 43
            result = subprocess.run(
                ["sh", str(REPO / "docker" / "start-prometheus.sh"), "--config.file=/etc/prometheus/prometheus.yml"],
                env={**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                     "METRICS_SCRAPE_TOKEN": token, "METRICS_TOKEN_FILE": str(token_file)},
                capture_output=True, text=True, check=False)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(token, token_file.read_text())
            self.assertEqual(0o600, stat.S_IMODE(token_file.stat().st_mode))
            self.assertNotIn(token, result.stdout + result.stderr)
            self.assertIn("--config.file=/etc/prometheus/prometheus.yml", result.stdout)

    @unittest.skipIf(os.name == "nt", "container entrypoint is a POSIX shell script")
    def test_prometheus_does_not_start_without_a_token(self):
        result = subprocess.run(["sh", str(REPO / "docker" / "start-prometheus.sh")],
                                env={**os.environ, "METRICS_SCRAPE_TOKEN": ""},
                                capture_output=True, text=True, check=False)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("METRICS_SCRAPE_TOKEN is missing", result.stderr)
