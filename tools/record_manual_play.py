"""Record manual gameplay by polling the STS2 bridge API.

Saves snapshots and actions to manual_traces/ as JSONL.
Detects SL (Save & Load) events when session_id changes or state_version rolls back.

Usage:
    python tools/record_manual_play.py --bridge-base-url http://127.0.0.1:8081
"""

from __future__ import annotations

import argparse
import hashlib
import json
import signal
import sys
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.error import URLError
from urllib.request import Request, urlopen


@dataclass(slots=True)
class RecorderConfig:
    bridge_base_url: str = "http://127.0.0.1:17654"
    poll_interval: float = 0.5
    output_dir: str = "manual_traces"
    timeout: float = 5.0


@dataclass(slots=True)
class RunRecord:
    run_id: str
    session_id: str
    started_at: str
    entries: list[dict[str, Any]] = field(default_factory=list)
    slim_entries: list[dict[str, Any]] = field(default_factory=list)
    sl_events: list[dict[str, Any]] = field(default_factory=list)


def fetch_json(url: str, timeout: float) -> Any:
    with urlopen(url, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def snapshot_fingerprint(snapshot: dict[str, Any]) -> str:
    key = json.dumps(
        {
            "session_id": snapshot.get("session_id"),
            "decision_id": snapshot.get("decision_id"),
            "state_version": snapshot.get("state_version"),
            "phase": snapshot.get("phase"),
            "terminal": snapshot.get("terminal"),
        },
        sort_keys=True,
    )
    return hashlib.md5(key.encode()).hexdigest()


class ManualPlayRecorder:
    def __init__(self, config: RecorderConfig) -> None:
        self.config = config
        self.base_url = config.bridge_base_url.rstrip("/")
        self.output_dir = Path(config.output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self._running = True
        self._current_run: RunRecord | None = None
        self._last_fingerprint: str | None = None
        self._last_state_version: int | None = None
        self._last_session_id: str | None = None
        self._step_index: int = 0
        self._last_map_topo_hash: str | None = None

    def stop(self) -> None:
        self._running = False

    def run(self) -> None:
        self._setup_signal_handlers()
        print(f"Recording manual play from {self.base_url}")
        print(f"Output: {self.output_dir.resolve()}")
        print("Press Ctrl+C to stop.\n")

        while self._running:
            try:
                health = fetch_json(f"{self.base_url}/health", self.config.timeout)
                print(f"Bridge connected: {health.get('provider_mode', 'unknown')}")
                break
            except (URLError, OSError) as exc:
                print(f"Waiting for bridge... ({exc})")
                time.sleep(2)

        while self._running:
            try:
                self._poll_once()
            except (URLError, OSError) as exc:
                if self._running:
                    print(f"Connection lost: {exc}")
                    time.sleep(2)
            except Exception as exc:
                if self._running:
                    print(f"Error: {exc}")
                    time.sleep(1)

            time.sleep(self.config.poll_interval)

        self._finalize_run()

    def _poll_once(self) -> None:
        snapshot = fetch_json(f"{self.base_url}/snapshot", self.config.timeout)
        if not isinstance(snapshot, dict):
            return
        raw_actions = fetch_json(f"{self.base_url}/actions", self.config.timeout)
        actions = raw_actions if isinstance(raw_actions, list) else []
        fp = snapshot_fingerprint(snapshot)

        if fp == self._last_fingerprint:
            return

        session_id = str(snapshot.get("session_id", ""))
        state_version = int(snapshot.get("state_version", 0))
        phase = snapshot.get("phase", "unknown")
        terminal = bool(snapshot.get("terminal"))

        sl_event = self._detect_sl(session_id, state_version, snapshot)
        if sl_event:
            self._handle_sl_event(sl_event)

        if self._current_run is None:
            self._start_new_run(session_id, state_version)
        elif session_id != self._current_run.session_id:
            self._finalize_run()
            self._start_new_run(session_id, state_version)

        entry = {
            "step_index": self._step_index,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "session_id": session_id,
            "decision_id": snapshot.get("decision_id"),
            "state_version": state_version,
            "phase": phase,
            "terminal": terminal,
            "snapshot": snapshot,
            "actions": actions,
        }
        if sl_event:
            entry["sl_event"] = sl_event

        self._current_run.entries.append(entry)

        slim = self._build_slim_entry(entry, snapshot, actions, sl_event)
        self._current_run.slim_entries.append(slim)

        self._step_index += 1
        self._last_fingerprint = fp
        self._last_state_version = state_version
        self._last_session_id = session_id

        self._print_state(snapshot, actions, sl_event)

    def _build_slim_entry(
        self,
        entry: dict[str, Any],
        snapshot: dict[str, Any],
        actions: list[dict[str, Any]],
        sl_event: dict[str, Any] | None,
    ) -> dict[str, Any]:
        slim: dict[str, Any] = {
            "i": entry["step_index"],
            "sv": entry["state_version"],
            "p": entry["phase"],
            "t": entry["terminal"],
        }

        # Slim snapshot
        snap = self._slim_snapshot(snapshot)
        slim["snap"] = snap

        # Slim actions
        if actions:
            slim["act"] = [self._slim_action(a) for a in actions]

        # Map topology: only include when changed
        run_state = snapshot.get("run_state") or {}
        map_data = run_state.get("map")
        if map_data:
            all_nodes = map_data.get("all_nodes")
            all_edges = map_data.get("all_edges")
            if all_nodes or all_edges:
                topo = {"n": all_nodes, "e": all_edges}
                topo_hash = hashlib.md5(
                    json.dumps(topo, sort_keys=True, ensure_ascii=False).encode()
                ).hexdigest()
                if topo_hash != self._last_map_topo_hash:
                    slim["map"] = topo
                    self._last_map_topo_hash = topo_hash

        if sl_event:
            slim["sl"] = sl_event

        return slim

    @staticmethod
    def _slim_snapshot(snap: dict[str, Any]) -> dict[str, Any]:
        out: dict[str, Any] = {}

        # Player — keep all fields except pile card lists
        player = snap.get("player")
        if player is not None:
            p = dict(player)
            p.pop("draw_pile_cards", None)
            p.pop("discard_pile_cards", None)
            p.pop("exhaust_pile_cards", None)
            out["player"] = p

        # Enemies
        enemies = snap.get("enemies")
        if enemies:
            out["enemies"] = enemies

        # Rewards
        rewards = snap.get("rewards")
        if rewards:
            out["rewards"] = rewards

        # Run state — strip map topo, keep the rest
        run_state = snap.get("run_state")
        if run_state is not None:
            rs = dict(run_state)
            rs.pop("map", None)
            out["rs"] = rs

        # Metadata — keep only useful fields
        meta = snap.get("metadata")
        if meta:
            slim_meta: dict[str, Any] = {}
            for key in ("window_kind", "event_title", "event_options",
                        "event_continue_available", "event_continue_label",
                        "current_map_coord", "current_map_point_type",
                        "act_floor", "ascension_level"):
                if key in meta:
                    slim_meta[key] = meta[key]
            if slim_meta:
                out["meta"] = slim_meta

        return out

    @staticmethod
    def _slim_action(action: dict[str, Any]) -> dict[str, Any]:
        slim: dict[str, Any] = {
            "id": action.get("action_id"),
            "type": action.get("type"),
            "label": action.get("label"),
        }
        params = action.get("params")
        if params:
            slim["params"] = params
        return slim

    def _detect_sl(
        self, session_id: str, state_version: int, snapshot: dict[str, Any]
    ) -> dict[str, Any] | None:
        if self._last_session_id is None:
            return None

        # SL detected: session_id changed
        if session_id != self._last_session_id:
            return {
                "type": "session_changed",
                "old_session_id": self._last_session_id,
                "new_session_id": session_id,
                "old_state_version": self._last_state_version,
                "new_state_version": state_version,
                "reason": "Session ID changed, likely Save & Load to a different run",
            }

        # SL detected: state_version rolled back
        if self._last_state_version is not None and state_version < self._last_state_version:
            return {
                "type": "state_rollback",
                "old_state_version": self._last_state_version,
                "new_state_version": state_version,
                "session_id": session_id,
                "reason": "State version rolled back, likely Save & Load within same run",
            }

        # SL detected: large state_version jump (loaded a much earlier save)
        if self._last_state_version is not None and state_version > self._last_state_version + 10:
            return {
                "type": "large_version_jump",
                "old_state_version": self._last_state_version,
                "new_state_version": state_version,
                "session_id": session_id,
                "reason": "Large state version jump, possible load from earlier save",
            }

        return None

    def _handle_sl_event(self, event: dict[str, Any]) -> None:
        print(f"\n{'='*60}")
        print(f"SL EVENT DETECTED: {event['type']}")
        print(f"  {event['reason']}")
        print(f"{'='*60}\n")

        if self._current_run:
            self._current_run.sl_events.append(
                {
                    **event,
                    "step_index": self._step_index,
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                }
            )

        # If session changed, finalize current run
        if event["type"] == "session_changed":
            self._finalize_run()
            self._start_new_run(event["new_session_id"], event["new_state_version"])

    def _start_new_run(self, session_id: str, state_version: int) -> None:
        run_id = f"{datetime.now().strftime('%Y%m%d_%H%M%S')}_{session_id[:8]}"
        self._current_run = RunRecord(
            run_id=run_id,
            session_id=session_id,
            started_at=datetime.now(timezone.utc).isoformat(),
        )
        self._step_index = 0
        self._last_map_topo_hash = None
        print(f"Started recording run: {run_id}")

    def _finalize_run(self) -> None:
        if self._current_run is None:
            return
        if not self._current_run.entries:
            self._current_run = None
            return

        run = self._current_run
        output_file = self.output_dir / f"{run.run_id}.jsonl"
        with output_file.open("w", encoding="utf-8") as f:
            for entry in run.entries:
                f.write(json.dumps(entry, ensure_ascii=False) + "\n")

        # Write slim trace
        slim_file = self.output_dir / f"{run.run_id}.slim.jsonl"
        with slim_file.open("w", encoding="utf-8") as f:
            for slim in run.slim_entries:
                f.write(json.dumps(slim, ensure_ascii=False) + "\n")

        # Save run summary
        summary = {
            "run_id": run.run_id,
            "session_id": run.session_id,
            "started_at": run.started_at,
            "ended_at": datetime.now(timezone.utc).isoformat(),
            "total_steps": len(run.entries),
            "sl_events": run.sl_events,
            "phases_visited": list(
                {e.get("phase", "unknown") for e in run.entries}
            ),
        }
        summary_file = self.output_dir / f"{run.run_id}_summary.json"
        with summary_file.open("w", encoding="utf-8") as f:
            json.dump(summary, f, ensure_ascii=False, indent=2)

        print(f"\nSaved run: {output_file}")
        print(f"Slim:    {slim_file}")
        print(f"Summary: {summary_file}")
        print(f"  Steps: {len(run.entries)}, SL events: {len(run.sl_events)}")
        self._current_run = None

    def _print_state(
        self,
        snapshot: dict[str, Any],
        actions: list[dict[str, Any]],
        sl_event: dict[str, Any] | None,
    ) -> None:
        phase = snapshot.get("phase") or "?"
        sv = snapshot.get("state_version") or 0
        player = snapshot.get("player") or {}
        hp = player.get("hp") or "?"
        max_hp = player.get("max_hp") or "?"
        energy = player.get("energy") or "?"
        gold = player.get("gold") or "?"

        run_state = snapshot.get("run_state") or {}
        floor = run_state.get("floor") or "?"
        act = run_state.get("act") or "?"
        room = run_state.get("current_room_type") or "?"

        print(
            f"[{self._step_index:4d}] "
            f"sv={sv:>5}  phase={phase:<12}  "
            f"floor={floor}  act={act}  room={room:<10}  "
            f"HP={hp}/{max_hp}  energy={energy}  gold={gold}  "
            f"actions={len(actions)}"
        )

        if phase == "combat":
            enemies = snapshot.get("enemies") or []
            for e in enemies:
                print(f"         enemy: {e.get('name', '?')} HP={e.get('hp')}/{e.get('max_hp')} intent={e.get('intent', '?')}")

        if phase == "combat" and player.get("hand"):
            hand_names = [c.get("name", "?") for c in player.get("hand", [])]
            print(f"         hand: {', '.join(hand_names)}")

        if actions:
            action_labels = [a.get("label", a.get("type", "?"))[:30] for a in actions[:8]]
            print(f"         legal: {', '.join(action_labels)}")

        if phase == "map":
            map_state = (run_state.get("map") or {})
            all_nodes = map_state.get("all_nodes") or []
            all_edges = map_state.get("all_edges") or []
            visited_path = map_state.get("visited_path") or []
            current_coord = map_state.get("current_coord") or "?"
            if all_nodes:
                node_types = {}
                for n in all_nodes:
                    nt = n.get("node_type", "?")
                    node_types[nt] = node_types.get(nt, 0) + 1
                type_summary = ", ".join(f"{k}:{v}" for k, v in sorted(node_types.items()))
                print(f"         map: current={current_coord}  nodes={len(all_nodes)}  edges={len(all_edges)}  visited={len(visited_path)}")
                print(f"         node_types: {type_summary}")
            else:
                reachable = map_state.get("reachable_nodes") or []
                print(f"         map: current={current_coord}  reachable={len(reachable)}  (no full topology)")

    def _setup_signal_handlers(self) -> None:
        def handler(signum, frame):
            print("\nStopping recorder...")
            self.stop()

        signal.signal(signal.SIGINT, handler)
        signal.signal(signal.SIGTERM, handler)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Record manual STS2 gameplay via bridge polling"
    )
    parser.add_argument(
        "--bridge-base-url",
        default="http://127.0.0.1:17654",
        help="Bridge base URL (default: http://127.0.0.1:17654)",
    )
    parser.add_argument(
        "--poll-interval",
        type=float,
        default=0.5,
        help="Polling interval in seconds (default: 0.5)",
    )
    parser.add_argument(
        "--output-dir",
        default="manual_traces",
        help="Output directory (default: manual_traces)",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=5.0,
        help="HTTP timeout in seconds (default: 5.0)",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    config = RecorderConfig(
        bridge_base_url=args.bridge_base_url,
        poll_interval=args.poll_interval,
        output_dir=args.output_dir,
        timeout=args.timeout,
    )
    recorder = ManualPlayRecorder(config)
    recorder.run()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
