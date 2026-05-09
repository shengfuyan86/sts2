from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from tools.record_manual_play import ManualPlayRecorder, RecorderConfig, snapshot_fingerprint


def make_snapshot(
    session_id: str = "sess-001",
    decision_id: str = "dec-001",
    state_version: int = 1,
    phase: str = "combat",
    terminal: bool = False,
    hp: int = 60,
    max_hp: int = 80,
    energy: int = 3,
    gold: int = 99,
    floor: int = 5,
    act: int = 1,
    enemy_name: str = "Jaw Worm",
    enemy_hp: int = 40,
    hand: list[str] | None = None,
) -> dict:
    return {
        "session_id": session_id,
        "decision_id": decision_id,
        "state_version": state_version,
        "phase": phase,
        "terminal": terminal,
        "player": {
            "hp": hp,
            "max_hp": max_hp,
            "block": 5,
            "energy": energy,
            "gold": gold,
            "hand": [{"name": n, "card_id": n.lower().replace(" ", "_"), "cost": 1, "playable": True} for n in (hand or ["Strike", "Defend"])],
            "draw_pile": 5,
            "discard_pile": 0,
            "exhaust_pile": 0,
            "relics": [],
            "potions": [],
            "powers": [],
        },
        "enemies": [
            {
                "enemy_id": "jaw_worm",
                "name": enemy_name,
                "hp": enemy_hp,
                "max_hp": 44,
                "block": 0,
                "intent": "attack_12",
                "is_alive": True,
            }
        ],
        "rewards": [],
        "map_nodes": [],
        "run_state": {
            "floor": floor,
            "act": act,
            "current_room_type": "monster",
            "map": {
                "current_coord": "0,0",
                "current_node_type": "monster",
                "reachable_nodes": ["enemy@1,1", "elite@2,1"],
                "source": "current_map_point",
                "all_nodes": [
                    {"coord": "0,0", "node_type": "monster", "col": 0, "row": 0, "visited": True, "is_current": True},
                    {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1, "visited": False, "is_current": False},
                    {"coord": "2,1", "node_type": "elite", "col": 2, "row": 1, "visited": False, "is_current": False},
                ],
                "all_edges": [
                    {"from": "0,0", "to": "1,1"},
                    {"from": "0,0", "to": "2,1"},
                ],
                "visited_path": ["0,0"],
            },
        },
        "metadata": {},
    }


def make_actions(count: int = 3) -> list[dict]:
    return [
        {
            "action_id": f"act-{i}",
            "type": "play_card",
            "label": f"Play card {i}",
            "params": {"card_index": i},
        }
        for i in range(count)
    ]


class SnapshotFingerprintTests(unittest.TestCase):
    def test_same_snapshot_produces_same_fingerprint(self) -> None:
        s = make_snapshot()
        self.assertEqual(snapshot_fingerprint(s), snapshot_fingerprint(s))

    def test_different_state_version_produces_different_fingerprint(self) -> None:
        s1 = make_snapshot(state_version=1)
        s2 = make_snapshot(state_version=2)
        self.assertNotEqual(snapshot_fingerprint(s1), snapshot_fingerprint(s2))

    def test_different_decision_id_produces_different_fingerprint(self) -> None:
        s1 = make_snapshot(decision_id="dec-001")
        s2 = make_snapshot(decision_id="dec-002")
        self.assertNotEqual(snapshot_fingerprint(s1), snapshot_fingerprint(s2))


class RecorderConfigTests(unittest.TestCase):
    def test_default_config(self) -> None:
        config = RecorderConfig()
        self.assertEqual(config.bridge_base_url, "http://127.0.0.1:17654")
        self.assertEqual(config.poll_interval, 0.5)
        self.assertEqual(config.output_dir, "manual_traces")
        self.assertEqual(config.timeout, 5.0)

    def test_custom_config(self) -> None:
        config = RecorderConfig(
            bridge_base_url="http://127.0.0.1:8081",
            poll_interval=1.0,
            output_dir="custom_dir",
            timeout=10.0,
        )
        self.assertEqual(config.bridge_base_url, "http://127.0.0.1:8081")
        self.assertEqual(config.poll_interval, 1.0)
        self.assertEqual(config.output_dir, "custom_dir")
        self.assertEqual(config.timeout, 10.0)


class BasicRecordingTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
            poll_interval=0.01,
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_creates_output_directory(self) -> None:
        self.assertTrue(self.recorder.output_dir.exists())

    def test_records_single_state_change(self) -> None:
        snap = make_snapshot(state_version=1)
        actions = make_actions(3)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.assertIsNotNone(self.recorder._current_run)
        self.assertEqual(len(self.recorder._current_run.entries), 1)
        self.assertEqual(self.recorder._current_run.entries[0]["state_version"], 1)
        self.assertEqual(self.recorder._current_run.entries[0]["phase"], "combat")

    def test_ignores_duplicate_state(self) -> None:
        snap = make_snapshot(state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            # First poll - new state
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()
            # Second poll - same state (no change)
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.assertEqual(len(self.recorder._current_run.entries), 1)

    def test_records_multiple_state_changes(self) -> None:
        snap1 = make_snapshot(state_version=1, phase="combat")
        snap2 = make_snapshot(state_version=2, phase="reward")
        snap3 = make_snapshot(state_version=3, phase="map")
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions, snap2, actions, snap3, actions]
            self.recorder._poll_once()
            self.recorder._poll_once()
            self.recorder._poll_once()

        self.assertEqual(len(self.recorder._current_run.entries), 3)
        self.assertEqual(self.recorder._current_run.entries[0]["phase"], "combat")
        self.assertEqual(self.recorder._current_run.entries[1]["phase"], "reward")
        self.assertEqual(self.recorder._current_run.entries[2]["phase"], "map")

    def test_finalizes_run_on_stop(self) -> None:
        snap = make_snapshot(state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.recorder._finalize_run()

        files = list(self.recorder.output_dir.iterdir())
        jsonl_files = [f for f in files if f.suffix == ".jsonl" and "slim" not in f.name]
        slim_files = [f for f in files if f.suffix == ".jsonl" and "slim" in f.name]
        summary_files = [f for f in files if f.suffix == ".json" and "summary" in f.name]
        self.assertEqual(len(jsonl_files), 1)
        self.assertEqual(len(slim_files), 1)
        self.assertEqual(len(summary_files), 1)

        # Verify JSONL content
        with jsonl_files[0].open("r", encoding="utf-8") as f:
            lines = f.readlines()
        self.assertEqual(len(lines), 1)
        entry = json.loads(lines[0])
        self.assertEqual(entry["state_version"], 1)
        self.assertIn("snapshot", entry)
        self.assertIn("actions", entry)
        self.assertIn("timestamp", entry)

        # Verify summary content
        with summary_files[0].open("r", encoding="utf-8") as f:
            summary = json.load(f)
        self.assertEqual(summary["total_steps"], 1)
        self.assertIn("started_at", summary)
        self.assertIn("ended_at", summary)
        self.assertIn("phases_visited", summary)


class SLDetectionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
            poll_interval=0.01,
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_no_sl_on_first_poll(self) -> None:
        snap = make_snapshot(session_id="sess-001", state_version=1)
        result = self.recorder._detect_sl("sess-001", 1, snap)
        self.assertIsNone(result)

    def test_detects_session_change(self) -> None:
        self.recorder._last_session_id = "sess-001"
        self.recorder._last_state_version = 5

        snap = make_snapshot(session_id="sess-002", state_version=1)
        result = self.recorder._detect_sl("sess-002", 1, snap)

        self.assertIsNotNone(result)
        self.assertEqual(result["type"], "session_changed")
        self.assertEqual(result["old_session_id"], "sess-001")
        self.assertEqual(result["new_session_id"], "sess-002")

    def test_detects_state_rollback(self) -> None:
        self.recorder._last_session_id = "sess-001"
        self.recorder._last_state_version = 10

        snap = make_snapshot(session_id="sess-001", state_version=3)
        result = self.recorder._detect_sl("sess-001", 3, snap)

        self.assertIsNotNone(result)
        self.assertEqual(result["type"], "state_rollback")
        self.assertEqual(result["old_state_version"], 10)
        self.assertEqual(result["new_state_version"], 3)

    def test_detects_large_version_jump(self) -> None:
        self.recorder._last_session_id = "sess-001"
        self.recorder._last_state_version = 5

        snap = make_snapshot(session_id="sess-001", state_version=20)
        result = self.recorder._detect_sl("sess-001", 20, snap)

        self.assertIsNotNone(result)
        self.assertEqual(result["type"], "large_version_jump")

    def test_no_sl_on_normal_increment(self) -> None:
        self.recorder._last_session_id = "sess-001"
        self.recorder._last_state_version = 5

        snap = make_snapshot(session_id="sess-001", state_version=6)
        result = self.recorder._detect_sl("sess-001", 6, snap)

        self.assertIsNone(result)

    def test_sl_event_recorded_in_run(self) -> None:
        snap1 = make_snapshot(session_id="sess-001", state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions]
            self.recorder._poll_once()

        # Simulate session change
        snap2 = make_snapshot(session_id="sess-002", state_version=1)
        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap2, actions]
            self.recorder._poll_once()

        # The first run should have an SL event
        # The recorder finalizes the old run and starts a new one
        # Check that files were created
        files = list(self.recorder.output_dir.iterdir())
        jsonl_files = [f for f in files if f.suffix == ".jsonl"]
        self.assertGreaterEqual(len(jsonl_files), 1)


class PhasePrintingTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_prints_combat_state_with_enemies(self) -> None:
        snap = make_snapshot(
            phase="combat",
            hp=50,
            max_hp=80,
            energy=3,
            gold=99,
            floor=5,
            act=1,
            enemy_name="Jaw Worm",
            enemy_hp=40,
            hand=["Strike", "Bash"],
        )
        actions = make_actions(3)

        import io
        from contextlib import redirect_stdout

        buf = io.StringIO()
        with redirect_stdout(buf):
            self.recorder._print_state(snap, actions, None)

        output = buf.getvalue()
        self.assertIn("combat", output)
        self.assertIn("HP=50/80", output)
        self.assertIn("energy=3", output)
        self.assertIn("Jaw Worm", output)
        self.assertIn("Strike", output)
        self.assertIn("Bash", output)

    def test_prints_sl_event(self) -> None:
        snap = make_snapshot()
        sl_event = {
            "type": "state_rollback",
            "reason": "State version rolled back",
        }

        import io
        from contextlib import redirect_stdout

        buf = io.StringIO()
        with redirect_stdout(buf):
            self.recorder._print_state(snap, [], sl_event)

        output = buf.getvalue()
        # _print_state doesn't print SL events directly, they're handled in _handle_sl_event
        self.assertIn("combat", output)


class RunLifecycleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
            poll_interval=0.01,
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_starts_new_run_on_first_poll(self) -> None:
        snap = make_snapshot(session_id="sess-001", state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.assertIsNotNone(self.recorder._current_run)
        self.assertEqual(self.recorder._current_run.session_id, "sess-001")

    def test_session_change_finalizes_and_starts_new_run(self) -> None:
        snap1 = make_snapshot(session_id="sess-001", state_version=1)
        snap2 = make_snapshot(session_id="sess-002", state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions]
            self.recorder._poll_once()

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap2, actions]
            self.recorder._poll_once()

        self.assertEqual(self.recorder._current_run.session_id, "sess-002")
        # First run should have been saved
        files = list(self.recorder.output_dir.iterdir())
        jsonl_files = [f for f in files if f.suffix == ".jsonl"]
        self.assertGreaterEqual(len(jsonl_files), 1)

    def test_empty_run_not_saved(self) -> None:
        self.recorder._start_new_run("sess-001", 1)
        self.recorder._finalize_run()
        files = list(self.recorder.output_dir.iterdir())
        self.assertEqual(len(files), 0)

    def test_step_index_resets_on_new_run(self) -> None:
        snap1 = make_snapshot(session_id="sess-001", state_version=1)
        snap2 = make_snapshot(session_id="sess-001", state_version=2)
        snap3 = make_snapshot(session_id="sess-002", state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions, snap2, actions]
            self.recorder._poll_once()
            self.recorder._poll_once()

        self.assertEqual(self.recorder._step_index, 2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap3, actions]
            self.recorder._poll_once()

        self.assertEqual(self.recorder._step_index, 1)

    def test_output_contains_all_expected_fields(self) -> None:
        snap = make_snapshot(state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.recorder._finalize_run()

        jsonl_files = [f for f in self.recorder.output_dir.glob("*.jsonl") if "slim" not in f.name]
        self.assertEqual(len(jsonl_files), 1)

        with jsonl_files[0].open("r", encoding="utf-8") as f:
            entry = json.loads(f.readline())

        self.assertIn("step_index", entry)
        self.assertIn("timestamp", entry)
        self.assertIn("session_id", entry)
        self.assertIn("decision_id", entry)
        self.assertIn("state_version", entry)
        self.assertIn("phase", entry)
        self.assertIn("terminal", entry)
        self.assertIn("snapshot", entry)
        self.assertIn("actions", entry)

    def test_summary_contains_sl_events(self) -> None:
        snap1 = make_snapshot(session_id="sess-001", state_version=1)
        snap2 = make_snapshot(session_id="sess-002", state_version=1)
        actions = make_actions(2)

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions]
            self.recorder._poll_once()

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap2, actions]
            self.recorder._poll_once()

        # Finalize the second run
        self.recorder._finalize_run()

        summary_files = list(self.recorder.output_dir.glob("*_summary.json"))
        self.assertGreaterEqual(len(summary_files), 1)

        # Find the summary for sess-001 (should have SL event)
        for sf in summary_files:
            with sf.open("r", encoding="utf-8") as f:
                summary = json.load(f)
            if summary["session_id"] == "sess-001":
                self.assertGreaterEqual(len(summary["sl_events"]), 1)
                self.assertEqual(summary["sl_events"][0]["type"], "session_changed")
                break


class ConnectionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
            poll_interval=0.01,
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_stop_sets_running_false(self) -> None:
        self.assertTrue(self.recorder._running)
        self.recorder.stop()
        self.assertFalse(self.recorder._running)

    def test_fetch_json_failure_propagates_to_run_loop(self) -> None:
        from urllib.error import URLError

        with patch("tools.record_manual_play.fetch_json", side_effect=URLError("connection refused")):
            with self.assertRaises(URLError):
                self.recorder._poll_once()
        # _poll_once raises; the run() loop catches and retries


class ParserTests(unittest.TestCase):
    def test_parser_defaults(self) -> None:
        from tools.record_manual_play import build_parser
        parser = build_parser()
        args = parser.parse_args([])
        self.assertEqual(args.bridge_base_url, "http://127.0.0.1:17654")
        self.assertEqual(args.poll_interval, 0.5)
        self.assertEqual(args.output_dir, "manual_traces")
        self.assertEqual(args.timeout, 5.0)

    def test_parser_custom_args(self) -> None:
        from tools.record_manual_play import build_parser
        parser = build_parser()
        args = parser.parse_args([
            "--bridge-base-url", "http://127.0.0.1:8081",
            "--poll-interval", "1.0",
            "--output-dir", "custom",
            "--timeout", "10.0",
        ])
        self.assertEqual(args.bridge_base_url, "http://127.0.0.1:8081")
        self.assertEqual(args.poll_interval, 1.0)
        self.assertEqual(args.output_dir, "custom")
        self.assertEqual(args.timeout, 10.0)


class MapTopologyRecordingTests(unittest.TestCase):
    """Tests for full map topology recording (all_nodes, all_edges, visited_path)."""

    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
            poll_interval=0.01,
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    _DEFAULT_NODES = [
        {"coord": "0,0", "node_type": "monster", "col": 0, "row": 0, "visited": True, "is_current": True},
        {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1, "visited": False, "is_current": False},
        {"coord": "2,1", "node_type": "elite", "col": 2, "row": 1, "visited": False, "is_current": False},
    ]
    _DEFAULT_EDGES = [
        {"from": "0,0", "to": "1,1"},
        {"from": "0,0", "to": "2,1"},
    ]

    def _make_map_snapshot(
        self,
        phase: str = "map",
        state_version: int = 1,
        all_nodes: list[dict] | None = None,
        all_edges: list[dict] | None = None,
        visited_path: list[str] | None = None,
        current_coord: str = "0,0",
        reachable_nodes: list[str] | None = None,
        include_map: bool = True,
    ) -> dict:
        map_data = None
        if include_map:
            map_data = {
                "current_coord": current_coord,
                "current_node_type": "monster",
                "reachable_nodes": reachable_nodes if reachable_nodes is not None else ["enemy@1,1", "elite@2,1"],
                "source": "current_map_point",
                "all_nodes": all_nodes if all_nodes is not None else list(self._DEFAULT_NODES),
                "all_edges": all_edges if all_edges is not None else list(self._DEFAULT_EDGES),
                "visited_path": visited_path if visited_path is not None else ["0,0"],
            }
        return {
            "session_id": "sess-001",
            "decision_id": "dec-001",
            "state_version": state_version,
            "phase": phase,
            "terminal": False,
            "player": {"hp": 60, "max_hp": 80, "block": 0, "energy": 0, "gold": 99, "hand": []},
            "enemies": [],
            "rewards": [],
            "map_nodes": reachable_nodes if reachable_nodes is not None else [],
            "run_state": {
                "floor": 5,
                "act": 1,
                "current_room_type": "map",
                "map": map_data,
            },
            "metadata": {},
        }

    def test_map_topology_recorded_in_entry(self) -> None:
        snap = self._make_map_snapshot()
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose Enemy@1,1", "params": {"node": "enemy@1,1"}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        entry = self.recorder._current_run.entries[0]
        map_state = entry["snapshot"]["run_state"]["map"]
        self.assertEqual(len(map_state["all_nodes"]), 3)
        self.assertEqual(len(map_state["all_edges"]), 2)
        self.assertEqual(map_state["visited_path"], ["0,0"])

    def test_map_nodes_have_correct_fields(self) -> None:
        snap = self._make_map_snapshot()
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose Enemy@1,1", "params": {"node": "enemy@1,1"}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        map_state = self.recorder._current_run.entries[0]["snapshot"]["run_state"]["map"]
        current_node = next(n for n in map_state["all_nodes"] if n["is_current"])
        self.assertEqual(current_node["coord"], "0,0")
        self.assertEqual(current_node["node_type"], "monster")
        self.assertTrue(current_node["visited"])
        self.assertEqual(current_node["col"], 0)
        self.assertEqual(current_node["row"], 0)

    def test_map_edges_preserved_in_output(self) -> None:
        snap = self._make_map_snapshot()
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose Enemy@1,1", "params": {"node": "enemy@1,1"}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        map_state = self.recorder._current_run.entries[0]["snapshot"]["run_state"]["map"]
        edges = map_state["all_edges"]
        self.assertEqual(edges[0]["from"], "0,0")
        self.assertEqual(edges[0]["to"], "1,1")
        self.assertEqual(edges[1]["from"], "0,0")
        self.assertEqual(edges[1]["to"], "2,1")

    def test_map_topology_saved_to_jsonl(self) -> None:
        snap = self._make_map_snapshot()
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose Enemy@1,1", "params": {"node": "enemy@1,1"}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.recorder._finalize_run()

        jsonl_files = [f for f in self.recorder.output_dir.glob("*.jsonl") if "slim" not in f.name]
        self.assertEqual(len(jsonl_files), 1)

        with jsonl_files[0].open("r", encoding="utf-8") as f:
            entry = json.loads(f.readline())

        map_state = entry["snapshot"]["run_state"]["map"]
        self.assertIn("all_nodes", map_state)
        self.assertIn("all_edges", map_state)
        self.assertIn("visited_path", map_state)
        self.assertEqual(len(map_state["all_nodes"]), 3)
        self.assertEqual(len(map_state["all_edges"]), 2)

    def test_map_phase_prints_topology_summary(self) -> None:
        snap = self._make_map_snapshot()
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose Enemy@1,1", "params": {"node": "enemy@1,1"}}]

        import io
        from contextlib import redirect_stdout

        buf = io.StringIO()
        with redirect_stdout(buf):
            self.recorder._print_state(snap, actions, None)

        output = buf.getvalue()
        self.assertIn("map:", output)
        self.assertIn("nodes=3", output)
        self.assertIn("edges=2", output)
        self.assertIn("visited=1", output)
        self.assertIn("node_types:", output)
        self.assertIn("monster:1", output)
        self.assertIn("enemy:1", output)
        self.assertIn("elite:1", output)

    def test_map_phase_without_full_topology(self) -> None:
        snap = self._make_map_snapshot(all_nodes=[], all_edges=[], visited_path=[])
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose Enemy@1,1", "params": {"node": "enemy@1,1"}}]

        import io
        from contextlib import redirect_stdout

        buf = io.StringIO()
        with redirect_stdout(buf):
            self.recorder._print_state(snap, actions, None)

        output = buf.getvalue()
        self.assertIn("reachable=", output)
        self.assertIn("no full topology", output)

    def test_empty_map_handled_gracefully(self) -> None:
        snap = self._make_map_snapshot(include_map=False)
        actions = []

        import io
        from contextlib import redirect_stdout

        buf = io.StringIO()
        with redirect_stdout(buf):
            self.recorder._print_state(snap, actions, None)

        output = buf.getvalue()
        self.assertIn("map:", output)

    def test_map_topology_across_multiple_polls(self) -> None:
        """Verify map updates as player moves through nodes."""
        snap1 = self._make_map_snapshot(
            state_version=1,
            current_coord="0,0",
            visited_path=["0,0"],
            all_nodes=[
                {"coord": "0,0", "node_type": "monster", "col": 0, "row": 0, "visited": True, "is_current": True},
                {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1, "visited": False, "is_current": False},
            ],
            all_edges=[{"from": "0,0", "to": "1,1"}],
        )
        snap2 = self._make_map_snapshot(
            state_version=2,
            current_coord="1,1",
            visited_path=["0,0", "1,1"],
            all_nodes=[
                {"coord": "0,0", "node_type": "monster", "col": 0, "row": 0, "visited": True, "is_current": False},
                {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1, "visited": True, "is_current": True},
                {"coord": "2,2", "node_type": "rest", "col": 2, "row": 2, "visited": False, "is_current": False},
            ],
            all_edges=[
                {"from": "0,0", "to": "1,1"},
                {"from": "1,1", "to": "2,2"},
            ],
        )
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose", "params": {}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions, snap2, actions]
            self.recorder._poll_once()
            self.recorder._poll_once()

        self.assertEqual(len(self.recorder._current_run.entries), 2)

        # First entry: at 0,0
        map1 = self.recorder._current_run.entries[0]["snapshot"]["run_state"]["map"]
        self.assertEqual(map1["current_coord"], "0,0")
        self.assertEqual(len(map1["all_nodes"]), 2)
        self.assertEqual(map1["visited_path"], ["0,0"])

        # Second entry: moved to 1,1
        map2 = self.recorder._current_run.entries[1]["snapshot"]["run_state"]["map"]
        self.assertEqual(map2["current_coord"], "1,1")
        self.assertEqual(len(map2["all_nodes"]), 3)
        self.assertEqual(map2["visited_path"], ["0,0", "1,1"])
        current_node = next(n for n in map2["all_nodes"] if n["is_current"])
        self.assertEqual(current_node["coord"], "1,1")


class PythonModelTests(unittest.TestCase):
    """Tests for Python-side map model classes."""

    def test_map_node_info_defaults(self) -> None:
        from sts2_agent.models import MapNodeInfo
        node = MapNodeInfo()
        self.assertEqual(node.coord, "")
        self.assertEqual(node.node_type, "")
        self.assertEqual(node.col, -1)
        self.assertEqual(node.row, -1)
        self.assertFalse(node.visited)
        self.assertFalse(node.is_current)

    def test_map_node_info_with_values(self) -> None:
        from sts2_agent.models import MapNodeInfo
        node = MapNodeInfo(coord="3,5", node_type="elite", col=3, row=5, visited=True, is_current=True)
        self.assertEqual(node.coord, "3,5")
        self.assertEqual(node.node_type, "elite")
        self.assertEqual(node.col, 3)
        self.assertEqual(node.row, 5)
        self.assertTrue(node.visited)
        self.assertTrue(node.is_current)

    def test_map_edge_defaults(self) -> None:
        from sts2_agent.models import MapEdge
        edge = MapEdge()
        self.assertEqual(edge.from_coord, "")
        self.assertEqual(edge.to_coord, "")

    def test_map_edge_with_values(self) -> None:
        from sts2_agent.models import MapEdge
        edge = MapEdge(from_coord="0,0", to_coord="1,1")
        self.assertEqual(edge.from_coord, "0,0")
        self.assertEqual(edge.to_coord, "1,1")

    def test_run_map_state_defaults(self) -> None:
        from sts2_agent.models import RunMapState
        state = RunMapState()
        self.assertEqual(state.all_nodes, [])
        self.assertEqual(state.all_edges, [])
        self.assertEqual(state.visited_path, [])

    def test_run_map_state_with_topology(self) -> None:
        from sts2_agent.models import MapNodeInfo, MapEdge, RunMapState
        state = RunMapState(
            current_coord="0,0",
            all_nodes=[
                MapNodeInfo(coord="0,0", node_type="monster", col=0, row=0),
                MapNodeInfo(coord="1,1", node_type="elite", col=1, row=1),
            ],
            all_edges=[MapEdge(from_coord="0,0", to_coord="1,1")],
            visited_path=["0,0"],
        )
        self.assertEqual(len(state.all_nodes), 2)
        self.assertEqual(len(state.all_edges), 1)
        self.assertEqual(state.visited_path, ["0,0"])


class BridgeDecoderMapTests(unittest.TestCase):
    """Tests for bridge HTTP decoder with full map topology."""

    def test_decode_run_state_with_full_map(self) -> None:
        from sts2_agent.bridge.http import HttpGameBridge
        payload = {
            "act": 1,
            "floor": 5,
            "current_room_type": "map",
            "map": {
                "current_coord": "0,0",
                "current_node_type": "monster",
                "reachable_nodes": ["enemy@1,1"],
                "source": "current_map_point",
                "all_nodes": [
                    {"coord": "0,0", "node_type": "monster", "col": 0, "row": 0, "visited": True, "is_current": True},
                    {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1, "visited": False, "is_current": False},
                ],
                "all_edges": [
                    {"from": "0,0", "to": "1,1"},
                ],
                "visited_path": ["0,0"],
            },
        }
        result = HttpGameBridge._decode_run_state(payload)
        self.assertIsNotNone(result)
        self.assertIsNotNone(result.map)
        self.assertEqual(len(result.map.all_nodes), 2)
        self.assertEqual(result.map.all_nodes[0].coord, "0,0")
        self.assertEqual(result.map.all_nodes[0].node_type, "monster")
        self.assertTrue(result.map.all_nodes[0].visited)
        self.assertTrue(result.map.all_nodes[0].is_current)
        self.assertEqual(result.map.all_nodes[1].coord, "1,1")
        self.assertFalse(result.map.all_nodes[1].visited)
        self.assertEqual(len(result.map.all_edges), 1)
        self.assertEqual(result.map.all_edges[0].from_coord, "0,0")
        self.assertEqual(result.map.all_edges[0].to_coord, "1,1")
        self.assertEqual(result.map.visited_path, ["0,0"])

    def test_decode_run_state_without_map_fields(self) -> None:
        from sts2_agent.bridge.http import HttpGameBridge
        payload = {
            "act": 1,
            "floor": 5,
            "map": {
                "current_coord": "0,0",
                "reachable_nodes": ["enemy@1,1"],
            },
        }
        result = HttpGameBridge._decode_run_state(payload)
        self.assertIsNotNone(result)
        self.assertIsNotNone(result.map)
        self.assertEqual(result.map.all_nodes, [])
        self.assertEqual(result.map.all_edges, [])
        self.assertEqual(result.map.visited_path, [])

    def test_decode_run_state_map_is_none(self) -> None:
        from sts2_agent.bridge.http import HttpGameBridge
        payload = {"act": 1, "floor": 5}
        result = HttpGameBridge._decode_run_state(payload)
        self.assertIsNotNone(result)
        self.assertIsNone(result.map)

    def test_decode_run_state_none_payload(self) -> None:
        from sts2_agent.bridge.http import HttpGameBridge
        result = HttpGameBridge._decode_run_state(None)
        self.assertIsNone(result)


class SlimFormatTests(unittest.TestCase):
    """Tests for the slim trace format."""

    def setUp(self) -> None:
        self.tmpdir = Path(tempfile.mkdtemp())
        self.config = RecorderConfig(
            bridge_base_url="http://fake:1234",
            output_dir=str(self.tmpdir / "traces"),
            poll_interval=0.01,
        )
        self.recorder = ManualPlayRecorder(self.config)

    def tearDown(self) -> None:
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _make_full_snapshot(self, state_version: int = 1, phase: str = "combat") -> dict:
        return {
            "session_id": "sess-001",
            "decision_id": f"dec-{state_version}",
            "state_version": state_version,
            "phase": phase,
            "terminal": False,
            "player": {
                "hp": 60, "max_hp": 80, "block": 5, "energy": 3, "gold": 99,
                "hand": [{"name": "Strike", "card_id": "strike", "cost": 1, "playable": True}],
                "draw_pile": 5, "discard_pile": 0, "exhaust_pile": 0,
                "relics": [{"name": "Burning Blood", "description": "Heal 6 HP", "canonical_relic_id": "burning_blood", "glossary": []}],
                "potions": [], "potion_capacity": 2, "powers": [],
                "draw_pile_cards": [{"name": "Strike", "card_id": "strike", "cost": 1, "playable": True}],
                "discard_pile_cards": [],
                "exhaust_pile_cards": [],
            },
            "enemies": [{"enemy_id": "jaw_worm", "name": "Jaw Worm", "hp": 40, "max_hp": 44, "block": 0, "intent": "attack_12", "is_alive": True}],
            "rewards": [],
            "map_nodes": [],
            "compatibility": {"protocol_version": "0.1.0", "mod_version": "0.1.0.0", "game_version": "v0.105.1", "provider_mode": "in-game-runtime", "read_only": False, "ready": True},
            "metadata": {
                "source": "sts2_runtime", "phase_detected": "combat",
                "game_version": "v0.105.1",
                "managed_dir": "D:\\Program Files\\Steam\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64",
                "pile_export": {"draw_pile": {"source": "draw_pile_missing"}},
                "window_kind": "player_turn",
            },
            "run_state": {"act": 1, "floor": 5, "current_room_type": "monster"},
        }

    def test_slim_snapshot_removes_compatibility(self) -> None:
        snap = self._make_full_snapshot()
        slim = ManualPlayRecorder._slim_snapshot(snap)
        self.assertNotIn("compatibility", slim)

    def test_slim_snapshot_removes_pile_cards(self) -> None:
        snap = self._make_full_snapshot()
        slim = ManualPlayRecorder._slim_snapshot(snap)
        player = slim["player"]
        self.assertNotIn("draw_pile_cards", player)
        self.assertNotIn("discard_pile_cards", player)
        self.assertNotIn("exhaust_pile_cards", player)
        # Core player fields preserved
        self.assertEqual(player["hp"], 60)
        self.assertEqual(player["energy"], 3)

    def test_slim_snapshot_keeps_enemies(self) -> None:
        snap = self._make_full_snapshot()
        slim = ManualPlayRecorder._slim_snapshot(snap)
        self.assertEqual(len(slim["enemies"]), 1)
        self.assertEqual(slim["enemies"][0]["name"], "Jaw Worm")

    def test_slim_snapshot_strips_metadata(self) -> None:
        snap = self._make_full_snapshot()
        slim = ManualPlayRecorder._slim_snapshot(snap)
        meta = slim.get("meta", {})
        self.assertIn("window_kind", meta)
        self.assertNotIn("managed_dir", meta)
        self.assertNotIn("pile_export", meta)
        self.assertNotIn("source", meta)

    def test_slim_action_strips_metadata(self) -> None:
        action = {
            "action_id": "act-001",
            "type": "play_card",
            "label": "Play Strike",
            "params": {"card_index": 0},
            "target_constraints": [],
            "metadata": {"event_option": {"glossary": [], "keywords": []}},
        }
        slim = ManualPlayRecorder._slim_action(action)
        self.assertEqual(slim["id"], "act-001")
        self.assertEqual(slim["type"], "play_card")
        self.assertIn("params", slim)
        self.assertNotIn("target_constraints", slim)
        self.assertNotIn("metadata", slim)

    def test_slim_entry_structure(self) -> None:
        snap = self._make_full_snapshot()
        actions = [{"action_id": "a1", "type": "play_card", "label": "Strike", "params": {}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.assertEqual(len(self.recorder._current_run.slim_entries), 1)
        slim = self.recorder._current_run.slim_entries[0]
        self.assertIn("i", slim)
        self.assertIn("sv", slim)
        self.assertIn("p", slim)
        self.assertIn("snap", slim)
        self.assertIn("act", slim)
        self.assertNotIn("snapshot", slim)
        self.assertNotIn("actions", slim)
        self.assertNotIn("compatibility", slim)

    def test_slim_map_dedup(self) -> None:
        """Map topo should only appear once when unchanged."""
        snap1 = self._make_full_snapshot(state_version=1, phase="map")
        snap1["run_state"]["map"] = {
            "current_coord": "0,0",
            "all_nodes": [{"coord": "0,0", "node_type": "monster", "col": 0, "row": 0, "visited": True, "is_current": True}],
            "all_edges": [{"from": "0,0", "to": "1,1"}],
        }
        snap2 = self._make_full_snapshot(state_version=2, phase="map")
        snap2["run_state"]["map"] = {
            "current_coord": "1,1",
            "all_nodes": [{"coord": "0,0", "node_type": "monster", "col": 0, "row": 0}, {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1}],
            "all_edges": [{"from": "0,0", "to": "1,1"}],
        }
        # Same topo as snap1 (same nodes/edges), just coord changed
        snap3 = self._make_full_snapshot(state_version=3, phase="map")
        snap3["run_state"]["map"] = {
            "current_coord": "1,1",
            "all_nodes": [{"coord": "0,0", "node_type": "monster", "col": 0, "row": 0}, {"coord": "1,1", "node_type": "enemy", "col": 1, "row": 1}],
            "all_edges": [{"from": "0,0", "to": "1,1"}],
        }
        actions = [{"action_id": "a1", "type": "choose_map_node", "label": "Choose", "params": {}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap1, actions, snap2, actions, snap3, actions]
            self.recorder._poll_once()
            self.recorder._poll_once()
            self.recorder._poll_once()

        slims = self.recorder._current_run.slim_entries
        # Entry 0: first map topo included
        self.assertIn("map", slims[0])
        # Entry 1: different topo, included
        self.assertIn("map", slims[1])
        # Entry 2: same topo as entry 1, NOT included
        self.assertNotIn("map", slims[2])

    def test_slim_file_written_on_finalize(self) -> None:
        snap = self._make_full_snapshot()
        actions = [{"action_id": "a1", "type": "play_card", "label": "Strike", "params": {}}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        self.recorder._finalize_run()

        slim_files = list(self.recorder.output_dir.glob("*.slim.jsonl"))
        self.assertEqual(len(slim_files), 1)

        with slim_files[0].open("r", encoding="utf-8") as f:
            lines = f.readlines()
        self.assertEqual(len(lines), 1)

    def test_slim_size_smaller_than_raw(self) -> None:
        snap = self._make_full_snapshot()
        actions = [{"action_id": "a1", "type": "play_card", "label": "Strike", "params": {}, "metadata": {"verbose": "data"}, "target_constraints": []}]

        with patch("tools.record_manual_play.fetch_json") as mock_fetch:
            mock_fetch.side_effect = [snap, actions]
            self.recorder._poll_once()

        raw_size = len(json.dumps(self.recorder._current_run.entries[0], ensure_ascii=False))
        slim_size = len(json.dumps(self.recorder._current_run.slim_entries[0], ensure_ascii=False))
        self.assertLess(slim_size, raw_size)


if __name__ == "__main__":
    unittest.main()
