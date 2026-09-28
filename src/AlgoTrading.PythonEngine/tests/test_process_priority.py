"""
The strategy runners give way on the CPU to the live feed (core/process_priority.py).

On Linux a nice value belongs to a thread and only threads started afterwards
inherit it, so the runner must lower its priority before anything starts a
thread; the last test reads the runner's source to hold it there.
"""

import ast
import os
import unittest
from pathlib import Path
from unittest import mock

import _bootstrap  # noqa: F401

import core.process_priority as process_priority
from core.process_priority import lower_priority_from_env

RUNNER = Path(__file__).resolve().parents[1] / "strategies" / "execution_runner.py"


class LowerPriorityTests(unittest.TestCase):
    def _run(self, env_value=None, file_value=None, has_nice=True):
        env = {} if env_value is None else {"RUNNER_NICE": env_value}
        logged = []
        nice = mock.MagicMock(return_value=10)
        with mock.patch.dict(os.environ, env, clear=False), \
             mock.patch.object(process_priority, "_configured",
                               side_effect=lambda var: env_value if env_value is not None else file_value), \
             mock.patch.object(process_priority.os, "nice", nice, create=True):
            if not has_nice:
                del process_priority.os.nice
            applied = lower_priority_from_env(log=logged.append)
        return applied, nice, logged

    def test_the_default_is_nice_10(self):
        applied, nice, logged = self._run()
        nice.assert_called_once_with(10)
        self.assertEqual((10, []), (applied, logged))

    def test_the_value_from_env_or_dotenv_is_used(self):
        applied, nice, _ = self._run(env_value="5")
        nice.assert_called_once_with(5)
        applied, nice, _ = self._run(file_value="15")
        nice.assert_called_once_with(15)

    def test_zero_leaves_the_priority_alone(self):
        applied, nice, _ = self._run(env_value="0")
        nice.assert_not_called()
        self.assertEqual(0, applied)

    def test_no_nice_on_the_platform_is_a_no_op(self):
        applied, nice, logged = self._run(has_nice=False)
        nice.assert_not_called()
        self.assertEqual((0, []), (applied, logged))

    def test_an_invalid_value_is_said_once_and_changes_nothing(self):
        applied, nice, logged = self._run(env_value="low")
        nice.assert_not_called()
        self.assertEqual(0, applied)
        self.assertEqual(1, len(logged))
        self.assertIn("RUNNER_NICE", logged[0])

    def test_the_environment_beats_the_file(self):
        with mock.patch.dict(os.environ, {"RUNNER_NICE": "3"}, clear=False), \
             mock.patch("dotenv.dotenv_values", return_value={"RUNNER_NICE": "12"}) as from_file:
            self.assertEqual("3", process_priority._configured("RUNNER_NICE"))
            from_file.assert_not_called()
        with mock.patch.dict(os.environ, {}, clear=False), \
             mock.patch("dotenv.dotenv_values", return_value={"RUNNER_NICE": "12"}) as from_file:
            os.environ.pop("RUNNER_NICE", None)
            self.assertEqual("12", process_priority._configured("RUNNER_NICE"))
            self.assertEqual(process_priority.REPO_ROOT / ".env", from_file.call_args.args[0])

    def test_the_module_imports_nothing_that_starts_threads_or_loads_settings(self):
        tree = ast.parse(Path(process_priority.__file__).read_text())
        imported = {alias.name for node in ast.walk(tree) if isinstance(node, ast.Import) for alias in node.names}
        imported |= {node.module for node in ast.walk(tree) if isinstance(node, ast.ImportFrom)}
        self.assertEqual({"os", "pathlib", "dotenv"}, imported)


class RunnerOrderTests(unittest.TestCase):
    """The runner lowers its priority before it imports anything that can start a thread."""

    THREAD_STARTERS = ("threading", "redis", "requests", "numpy", "pandas")

    def test_the_priority_is_lowered_before_any_thread_can_exist(self):
        tree = ast.parse(RUNNER.read_text())
        call_line = None
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and getattr(node.func, "id", None) == "lower_priority_from_env":
                call_line = node.lineno
        self.assertIsNotNone(call_line, "execution_runner.py no longer lowers its priority")

        first = {}
        for node in ast.walk(tree):
            names = []
            if isinstance(node, ast.Import):
                names = [alias.name for alias in node.names]
            elif isinstance(node, ast.ImportFrom) and node.module:
                names = [node.module]
            for name in names:
                root = name.split(".")[0]
                if root in self.THREAD_STARTERS:
                    first[root] = min(first.get(root, node.lineno), node.lineno)
            if isinstance(node, ast.Call) and getattr(node.func, "id", None) == "install_safe_stdio":
                first["install_safe_stdio"] = min(first.get("install_safe_stdio", node.lineno), node.lineno)

        self.assertEqual(set(self.THREAD_STARTERS[:3]) | {"install_safe_stdio"},
                         set(first) & (set(self.THREAD_STARTERS[:3]) | {"install_safe_stdio"}),
                         "the runner still imports these; the check must see them")
        for name, line in first.items():
            self.assertLess(call_line, line, f"{name} (line {line}) comes before lower_priority_from_env "
                                             f"(line {call_line})")


if __name__ == "__main__":
    unittest.main()
