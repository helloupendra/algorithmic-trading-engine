import _bootstrap  # noqa: F401

import json
import stat
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import sentinel.__main__ as entry
from sentinel import context as context_module
from _sentinel_fakes import make_context


class AllowlistTests(unittest.TestCase):
    """Only read-only uses of the allowed tools; an allowed tool told to run or write something is refused."""

    def setUp(self):
        self.ctx = make_context(Path(tempfile.mkdtemp()))

    def test_what_the_agents_run_is_allowed(self):
        for args in (
            ["git", "grep", "-z", "-n", "-I", "-E", "-e", "-----BEGIN ([A-Z0-9]+ )*PRIVATE KEY-----"],
            ["git", "grep", "-z", "-n", "-I", "-E", "-i", "-e", "password"],
            ["git", "log", "-1", "--format=%h %s"],
            ["docker", "ps", "--format", "{{.Names}}\t{{.Status}}"],
            ["npm", "audit", "--omit=dev", "--json"],
            ["dotnet", "list", "package", "--vulnerable", "--include-transitive", "--no-restore"],
            ["fail2ban-client", "status", "sshd"],
            ["ss", "-tlnH"],
        ):
            with self.subTest(args=args):
                self.assertIsNone(context_module._refusal(args))

    def test_an_allowed_tool_told_to_run_a_program_or_write_a_file_is_refused(self):
        for args in (
            ["git", "grep", "-Oless", "x"],
            ["git", "grep", "-nOvim", "x"],                 # bundled with another short option
            ["git", "grep", "--open-files-in-pager=vim", "x"],
            ["git", "grep", "--open", "x"],                 # git takes any unambiguous prefix
            ["git", "log", "--output=/tmp/evidence"],
            ["git", "diff", "--out=/tmp/evidence"],
            ["git", "diff", "--ext-diff"],
            ["git", "log", "-c"],
            ["git", "-c", "core.pager=sh", "log"],
            ["npm", "audit", "fix"],
            ["npm", "audit", "--fix"],
            ["npm", "install"],
            ["docker", "run", "alpine"],
            ["rm", "-rf", "/"],
        ):
            with self.subTest(args=args):
                with self.assertRaises(PermissionError):
                    self.ctx.run(args)

    def test_a_pattern_after_e_is_a_pattern_not_an_option(self):
        self.assertIsNone(context_module._refusal(["git", "grep", "-e", "--output"]))
        self.assertIsNone(context_module._refusal(["git", "grep", "-ne", "-Ox"]))


class RunTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.ctx = make_context(self.tmp)

    def tool(self, name, script):
        path = self.tmp / name
        path.write_text("#!/bin/sh\n" + script, encoding="utf-8")
        path.chmod(path.stat().st_mode | stat.S_IEXEC)
        return str(path)

    def test_output_that_is_not_utf8_is_read_not_raised(self):
        exe = self.tool("ps", "printf 'pid \\377\\376 comm\\n'\n")
        with mock.patch.object(context_module.shutil, "which", return_value=exe):
            code, out = self.ctx.run(["ps", "-eo", "pid=,comm="])
        self.assertEqual(0, code)
        self.assertEqual("pid �� comm\n", out)

    def test_a_tool_is_found_once_so_a_later_path_cannot_swap_it(self):
        first = self.tool("who", "echo first\n")
        later = self.tool("who2", "echo swapped\n")
        with mock.patch.object(context_module.shutil, "which", side_effect=[first, later]) as which:
            self.assertEqual((0, "first\n"), self.ctx.run(["who"]))
            self.assertEqual((0, "first\n"), self.ctx.run(["who"]))
        self.assertEqual(1, which.call_count)

    def test_a_tool_that_is_not_installed_is_127(self):
        with mock.patch.object(context_module.shutil, "which", return_value=None):
            self.assertEqual((127, ""), self.ctx.run(["uptime"]))


class DryRunStateTests(unittest.TestCase):
    """A dry run reads from where the service is, and never moves the service's own place."""

    def setUp(self):
        self.repo = Path(tempfile.mkdtemp())
        self.live = self.repo / "logs" / "sentinel"
        self.live.mkdir(parents=True)
        (self.live / "state-logs.json").write_text(json.dumps({"offsets": {"api.log": 1200}}), encoding="utf-8")
        (self.live / "calendar.json").write_text(json.dumps({"day": "2026-09-28"}), encoding="utf-8")
        (self.live / "unstored-incidents.jsonl").write_text("{}\n", encoding="utf-8")

    def test_the_copy_starts_where_the_service_is(self):
        copy = entry.dry_run_state(self.live)
        self.assertNotEqual(self.live, copy)
        self.assertEqual({"calendar.json", "state-logs.json"}, {p.name for p in copy.iterdir()})
        ctx = make_context(self.repo)
        ctx.state_root = copy
        state = ctx.state("logs")
        self.assertEqual({"api.log": 1200}, state.data["offsets"])
        state.data["offsets"]["api.log"] = 9999
        state.save()
        self.assertEqual(1200, json.loads((self.live / "state-logs.json").read_text())["offsets"]["api.log"])

    def test_build_gives_a_dry_run_its_own_state_and_the_service_the_real_one(self):
        with mock.patch.object(entry, "REPO_ROOT", self.repo), self.assertLogs("sentinel", level="INFO") as logs:
            dry = entry.build(True, {"health"}, env={})
            live = entry.build(False, {"health"}, env={})
        self.assertTrue(any("dry run: agents keep their state in" in line for line in logs.output))
        self.assertNotEqual(self.live, dry._ctx.state_dir)
        self.assertTrue((dry._ctx.state_dir / "state-logs.json").is_file())
        self.assertEqual(self.live, live._ctx.state_dir)

    def test_without_a_state_root_agents_keep_their_state_under_logs(self):
        self.assertEqual(self.repo / "logs" / "sentinel", make_context(self.repo).state_dir)


if __name__ == "__main__":
    unittest.main()
