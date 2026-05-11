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
import time
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable
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
    sl_events: list[dict[str, Any]] = field(default_factory=list)


# ---------------------------------------------------------------------------
# Events
# ---------------------------------------------------------------------------

@dataclass(slots=True)
class StateChangedEvent:
    """Fired when snapshot fingerprint differs from last seen."""
    snapshot: dict[str, Any]
    actions: list[dict[str, Any]]
    inferred_actions: list[dict[str, Any]] = field(default_factory=list)


@dataclass(slots=True)
class DuplicateStateEvent:
    """Fired when snapshot fingerprint matches last seen (no-op)."""


@dataclass(slots=True)
class SLDetectedEvent:
    """Fired when an SL anomaly is detected."""
    sl_type: str  # "session_changed", "state_rollback", "large_version_jump"
    details: dict[str, Any]
    snapshot: dict[str, Any]
    actions: list[dict[str, Any]]


@dataclass(slots=True)
class SessionChangedEvent:
    """Fired when session_id changes."""
    old_session_id: str
    new_session_id: str
    old_state_version: int
    new_state_version: int


# ---------------------------------------------------------------------------
# Event Bus
# ---------------------------------------------------------------------------

class EventBus:
    def __init__(self) -> None:
        self._handlers: dict[type, list[Callable[..., None]]] = defaultdict(list)

    def on(self, event_type: type, handler: Callable[..., None]) -> None:
        self._handlers[event_type].append(handler)

    def emit(self, event: Any) -> None:
        for handler in self._handlers.get(type(event), []):
            handler(event)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _count_deck(player: dict[str, Any]) -> int:
    """Count total cards in deck (hand + draw + discard + exhaust)."""
    return (
        len(player.get("hand") or [])
        + (player.get("draw_pile") or 0)
        + (player.get("discard_pile") or 0)
        + (player.get("exhaust_pile") or 0)
    )


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
        self._last_game_fingerprint: str | None = None
        self._last_state_version: int | None = None
        self._last_session_id: str | None = None
        self._step_index: int = 0
        self._last_map_topo_hash: str | None = None
        self._last_phase: str | None = None
        self._last_phase_recorded: str | None = None
        self._phase_before_menu: str | None = None
        self._last_rewards: list[str] = []
        self._last_legal: list[dict[str, Any]] = []
        self._last_snapshot: dict[str, Any] | None = None
        self._jsonl_file: Any = None
        self._slim_file: Any = None
        self._total_steps: int = 0
        self._phases_visited: set[str] = set()
        self._bus = EventBus()
        self._setup_events()

    @staticmethod
    def _game_fingerprint(snapshot: dict[str, Any], actions: list[dict[str, Any]]) -> str:
        player = snapshot.get("player") or {}
        enemies = snapshot.get("enemies") or []
        meta = snapshot.get("metadata") or {}
        run_state = snapshot.get("run_state") or {}
        action_types = sorted([a.get("type", "") for a in actions])
        key: dict[str, Any] = {
            "phase": snapshot.get("phase"),
            "energy": player.get("energy"),
            "stars": meta.get("stars"),
            "hp": player.get("hp"),
            "block": player.get("block"),
            "hand": [c.get("card_id") for c in (player.get("hand") or [])],
            "potions": len(player.get("potions") or []),
            "enemies": [
                {"id": e.get("enemy_id"), "hp": e.get("hp"), "block": e.get("block"), "intent": e.get("intent")}
                for e in enemies
            ],
            "actions": action_types,
            "wk": meta.get("window_kind"),
            "coord": (run_state.get("map") or {}).get("current_coord"),
            "event": meta.get("event_title"),
            "rewards": snapshot.get("rewards"),
            "gold": player.get("gold"),
        }
        return hashlib.md5(json.dumps(key, sort_keys=True).encode()).hexdigest()

    # ------------------------------------------------------------------
    # Event wiring
    # ------------------------------------------------------------------

    def _setup_events(self) -> None:
        self._bus.on(DuplicateStateEvent, self._on_duplicate_state)
        self._bus.on(StateChangedEvent, self._on_state_changed)
        self._bus.on(SLDetectedEvent, self._on_sl_detected)
        self._bus.on(SessionChangedEvent, self._on_session_changed)

    def _on_duplicate_state(self, _event: DuplicateStateEvent) -> None:
        pass

    def _on_state_changed(self, event: StateChangedEvent) -> None:
        snapshot = event.snapshot
        actions = event.actions

        session_id = str(snapshot.get("session_id", ""))

        # Dedup: skip if game state is semantically identical (but not on session change)
        game_fp = self._game_fingerprint(snapshot, actions)
        if game_fp == self._last_game_fingerprint and session_id == self._last_session_id:
            self._bus.emit(DuplicateStateEvent())
            return
        self._last_game_fingerprint = game_fp
        state_version = int(snapshot.get("state_version", 0))
        phase = snapshot.get("phase", "unknown")
        terminal = bool(snapshot.get("terminal"))

        # Detect SL anomalies — may emit SLDetectedEvent / SessionChangedEvent
        sl_event = self._detect_sl(session_id, state_version, phase)
        if sl_event:
            sl_type = sl_event["type"]
            self._bus.emit(SLDetectedEvent(
                sl_type=sl_type, details=sl_event,
                snapshot=snapshot, actions=actions,
            ))
            if sl_type == "session_changed":
                self._bus.emit(SessionChangedEvent(
                    old_session_id=self._last_session_id or "",
                    new_session_id=session_id,
                    old_state_version=self._last_state_version or 0,
                    new_state_version=state_version,
                ))

        # Ensure a run is active
        if self._current_run is None:
            self._start_new_run(session_id, state_version)
        elif session_id != self._current_run.session_id:
            self._finalize_run()
            self._start_new_run(session_id, state_version)

        # Determine current action
        has_real_action = False
        current_action = None
        phase_changed = phase != self._last_phase_recorded

        # Reward phase: detect by legal list diff
        # When an item disappears from the legal list, it was the player's choice
        if phase == "reward":
            current_labels = {a.get("label", "") for a in actions}
            prev_labels = {a.get("label", "") for a in self._last_legal}
            removed_labels = prev_labels - current_labels
            if removed_labels and self._last_legal:
                removed_label = removed_labels.pop()
                current_action = {"type": "choose_reward", "label": removed_label}
                has_real_action = True
            self._last_legal = list(actions)
            self._last_rewards = list(snapshot.get("rewards") or [])
        elif phase_changed:
            self._last_legal = []
            self._last_rewards = []

        # Event phase: trust bridge's inferred action
        if current_action is None and phase == "event" and event.inferred_actions:
            inferred = event.inferred_actions[0]
            if inferred.get("type") in ("choose_event_option", "continue_event"):
                current_action = inferred
                has_real_action = True

        # Combat/other phases: validate inferred action against legal actions
        if current_action is None and event.inferred_actions:
            inferred = event.inferred_actions[0]
            if self._action_matches_legal(inferred, actions, snapshot):
                current_action = inferred
                has_real_action = True
        if current_action is None:
            current_action = self._infer_action_from_state(snapshot, actions, self._last_snapshot)

        # Enrich action with state delta
        if self._last_snapshot:
            current_action["detail"] = self._compute_detail(self._last_snapshot, snapshot)
        self._last_snapshot = snapshot

        # For reward/event phases, only write when a real action is detected
        # or when entering a new phase (skip hover/reorder events)
        if phase in ("reward", "event") and not has_real_action and not phase_changed:
            self._last_game_fingerprint = game_fp
            return
        self._last_phase_recorded = phase

        # Record entry
        entry = {
            "step_index": self._step_index,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "session_id": session_id,
            "decision_id": snapshot.get("decision_id"),
            "state_version": state_version,
            "phase": phase,
            "terminal": terminal,
            "action": current_action,
            "snapshot": snapshot,
            "actions": actions,
        }
        if sl_event:
            entry["sl_event"] = sl_event

        self._write_entry(entry)
        self._total_steps += 1
        self._phases_visited.add(phase)

        slim = self._build_slim_entry(entry, snapshot, actions, sl_event)
        self._write_slim(slim)

        self._step_index += 1
        self._last_fingerprint = snapshot_fingerprint(snapshot)
        self._last_state_version = state_version
        self._last_session_id = session_id

        self._print_state(snapshot, actions, sl_event, event.inferred_actions, current_action)

    def _on_sl_detected(self, event: SLDetectedEvent) -> None:
        print(f"\n{'='*60}")
        print(f"SL EVENT DETECTED: {event.sl_type}")
        print(f"  {event.details.get('reason', '')}")
        print(f"{'='*60}\n")

        if self._current_run:
            self._current_run.sl_events.append({
                **event.details,
                "step_index": self._step_index,
                "timestamp": datetime.now(timezone.utc).isoformat(),
            })

    def _on_session_changed(self, event: SessionChangedEvent) -> None:
        self._finalize_run()
        self._start_new_run(event.new_session_id, event.new_state_version)

    # ------------------------------------------------------------------
    # Main loop
    # ------------------------------------------------------------------

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
            self._bus.emit(DuplicateStateEvent())
            return

        inferred = self._fetch_action_log()
        self._bus.emit(StateChangedEvent(snapshot=snapshot, actions=actions, inferred_actions=inferred))

    @staticmethod
    def _action_matches_legal(
        inferred: dict[str, Any], legal_actions: list[dict[str, Any]],
        snapshot: dict[str, Any] | None = None,
    ) -> bool:
        inferred_type = inferred.get("type", "")
        if not legal_actions:
            return False

        # Reject enemy_turn when window_kind is still player_turn
        if inferred_type == "enemy_turn" and snapshot:
            wk = (snapshot.get("metadata") or {}).get("window_kind", "")
            if wk == "player_turn":
                return False

        # Find matching legal actions by type
        matching = [a for a in legal_actions if a.get("type") == inferred_type]
        if not matching:
            return False

        # For actions with specific targets, verify the target matches
        inferred_params = inferred.get("params") or {}

        # play_card: match card_id
        if inferred_type == "play_card":
            card_id = inferred_params.get("card_id", "")
            return any(a.get("params", {}).get("card_id") == card_id for a in matching)

        # choose_reward: match reward name
        if inferred_type == "choose_reward":
            reward = inferred_params.get("reward", "")
            return any(a.get("params", {}).get("reward") == reward for a in matching)

        # choose_map_node: match node
        if inferred_type == "choose_map_node":
            node = inferred_params.get("node", "")
            return any(a.get("params", {}).get("node") == node for a in matching)

        # choose_event_option: match option_index
        if inferred_type == "choose_event_option":
            idx = inferred_params.get("option_index")
            if idx is not None:
                return any(a.get("params", {}).get("option_index") == idx for a in matching)

        # For generic actions (end_turn, continue_event, etc.), type match is enough
        return True

    @staticmethod
    def _compute_detail(prev: dict[str, Any], curr: dict[str, Any]) -> dict[str, Any]:
        """Compute state delta between two snapshots for action context."""
        pp = prev.get("player") or {}
        cp = curr.get("player") or {}
        detail: dict[str, Any] = {}

        # HP / block / energy / gold
        hp_prev, hp_cur = pp.get("hp"), cp.get("hp")
        if hp_prev is not None and hp_cur is not None and hp_prev != hp_cur:
            detail["hp"] = f"{hp_prev}→{hp_cur}"
        block_prev, block_cur = pp.get("block"), cp.get("block")
        if block_prev is not None and block_cur is not None and block_prev != block_cur:
            detail["block"] = f"{block_prev}→{block_cur}"
        energy_prev, energy_cur = pp.get("energy"), cp.get("energy")
        if energy_prev is not None and energy_cur is not None and energy_prev != energy_cur:
            detail["energy"] = f"{energy_prev}→{energy_cur}"
        gold_prev, gold_cur = pp.get("gold"), cp.get("gold")
        if gold_prev is not None and gold_cur is not None and gold_prev != gold_cur:
            detail["gold"] = f"{gold_prev}→{gold_cur}"

        # Deck size
        deck_prev = _count_deck(pp)
        deck_cur = _count_deck(cp)
        if deck_prev != deck_cur:
            detail["deck"] = f"{deck_prev}→{deck_cur}"

        # Relics
        relics_prev = len(pp.get("relics") or [])
        relics_cur = len(cp.get("relics") or [])
        if relics_prev != relics_cur:
            detail["relics"] = f"{relics_prev}→{relics_cur}"

        # Enemies
        pe = prev.get("enemies") or []
        ce = curr.get("enemies") or []
        if len(pe) == len(ce):
            enemy_deltas = []
            for i in range(len(pe)):
                php = pe[i].get("hp")
                chp = ce[i].get("hp")
                if php != chp:
                    enemy_deltas.append(f"{pe[i].get('name', '?')} {php}→{chp}")
            if enemy_deltas:
                detail["enemy_hp"] = enemy_deltas

        return detail if detail else {}

    def _fetch_action_log(self) -> list[dict[str, Any]]:
        try:
            raw = fetch_json(f"{self.base_url}/action-log", self.config.timeout)
            actions = raw if isinstance(raw, list) else []
            if actions:
                # Clear after reading to avoid accumulating stale entries
                try:
                    req = Request(f"{self.base_url}/action-log", method="DELETE")
                    urlopen(req, timeout=self.config.timeout)
                except Exception:
                    pass
            return actions
        except Exception:
            return []

    @staticmethod
    def _infer_action_from_state(
        snapshot: dict[str, Any], actions: list[dict[str, Any]],
        prev: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        phase = snapshot.get("phase", "unknown")
        player = snapshot.get("player") or {}
        meta = snapshot.get("metadata") or {}
        window_kind = meta.get("window_kind", "")
        run_state = snapshot.get("run_state") or {}
        room = run_state.get("current_room_type") or ""

        # ── 休息火堆 (RestSite) ──────────────────────────────────
        # 火堆处 phase 在 combat/event 之间切换：
        #   combat + actions=0 → 火堆初始状态或动画中
        #   event  → 选择界面（休息/升级/删牌/挖遗物/锻炼等，取决于遗物）
        #   combat → 火堆操作完成后的结果
        if room == "RestSiteRoom" and phase in ("combat", "event"):
            if phase == "event":
                # legal中有"选择 XXX"的卡牌选项 → 强化牌组界面
                has_card_selection = any(
                    a.get("label", "").startswith("选择 ")
                    for a in actions
                    if a.get("type") == "choose_event_option"
                )
                if has_card_selection:
                    return {"type": "upgrade", "label": "Upgrade Card"}
                if window_kind == "event_choice":
                    return {"type": "rest_site_choice", "label": "Rest Site Choice"}
                if window_kind in ("event_continue", "event_text"):
                    return {"type": "rest_site_continue", "label": "Rest Site Continue"}
                return {"type": "rest_site_event", "label": "Rest Site Event"}
            # combat phase at rest site → detect specific action from state delta
            if prev:
                prev_player = prev.get("player") or {}
                cur_player = snapshot.get("player") or {}
                prev_hp = prev_player.get("hp")
                # HP未知 → 刚进入火堆，不是操作结果
                if prev_hp is None:
                    return {"type": "enter_rest_site", "label": "Enter Rest Site"}
                hp_delta = (cur_player.get("hp") or 0) - prev_hp
                if hp_delta > 0:
                    return {"type": "rest", "label": f"Rest (+{hp_delta} HP)"}
                prev_deck = _count_deck(prev_player)
                cur_deck = _count_deck(cur_player)
                if cur_deck < prev_deck:
                    return {"type": "purge", "label": f"Purge Card (deck {prev_deck}→{cur_deck})"}
                prev_relics = len(prev_player.get("relics") or [])
                cur_relics = len(cur_player.get("relics") or [])
                if cur_relics > prev_relics:
                    return {"type": "dig_relic", "label": "Dig Relic"}
            return {"type": "rest_site_result", "label": "Rest Site Result"}

        # ── 战斗 (Combat) ────────────────────────────────────────
        if phase == "combat":
            if window_kind == "enemy_turn" or (not actions and window_kind != "player_turn"):
                return {"type": "enemy_turn", "label": "Enemy Turn"}
            if window_kind == "combat_transition":
                return {"type": "combat_start", "label": "Combat Start"}
            energy = player.get("energy")
            hand = player.get("hand") or []
            if energy is not None and energy > 0 and hand:
                return {"type": "player_turn", "label": "Player Turn"}
            if energy == 0 and hand:
                return {"type": "awaiting_end_turn", "label": "Awaiting End Turn"}
            return {"type": "combat_state", "label": "Combat State"}

        # ── 地图 (Map) ──────────────────────────────────────────
        if phase == "map":
            if window_kind == "map_ready":
                return {"type": "choose_map_node", "label": "Choose Map Node"}
            return {"type": "map_state", "label": "Map State"}

        # ── 事件 (Event) ────────────────────────────────────────
        if phase == "event":
            if window_kind == "event_choice":
                return {"type": "choose_event_option", "label": "Event Choice"}
            if window_kind in ("event_continue", "event_text"):
                return {"type": "continue_event", "label": "Continue Event"}
            return {"type": "event_state", "label": "Event State"}

        # ── 奖励 (Reward) ───────────────────────────────────────
        if phase == "reward":
            rewards = snapshot.get("rewards") or []
            if rewards:
                return {"type": "choose_reward", "label": "Choose Reward"}
            return {"type": "advance_reward", "label": "Advance Reward"}

        # ── 商店 (Shop) ─────────────────────────────────────────
        # 商店操作通过状态差分区分：
        #   gold减少 + deck增加   → 购买卡牌
        #   gold减少 + relics增加 → 购买遗物
        #   gold减少 + potions变化 → 购买药水
        #   gold减少 + deck减少   → 删除卡牌
        #   gold不变 + deck不变   → 浏览/触发遗物效果
        if phase == "shop":
            if prev:
                pp = prev.get("player") or {}
                cp = snapshot.get("player") or {}
                gold_prev = pp.get("gold") or 0
                gold_cur = cp.get("gold") or 0
                gold_spent = gold_prev - gold_cur
                deck_prev = _count_deck(pp)
                deck_cur = _count_deck(cp)
                relics_prev = len(pp.get("relics") or [])
                relics_cur = len(cp.get("relics") or [])
                potions_prev = len(pp.get("potions") or [])
                potions_cur = len(cp.get("potions") or [])
                if gold_spent > 0:
                    if deck_cur > deck_prev:
                        return {"type": "buy_card", "label": f"Buy Card (-{gold_spent}g, deck {deck_prev}→{deck_cur})"}
                    if relics_cur > relics_prev:
                        return {"type": "buy_relic", "label": f"Buy Relic (-{gold_spent}g)"}
                    if potions_cur != potions_prev:
                        return {"type": "buy_potion", "label": f"Buy Potion (-{gold_spent}g)"}
                    if deck_cur < deck_prev:
                        return {"type": "purge_card", "label": f"Purge Card (-{gold_spent}g, deck {deck_prev}→{deck_cur})"}
                    return {"type": "shop_purchase", "label": f"Shop Purchase (-{gold_spent}g)"}
            return {"type": "shop_state", "label": "Shop State"}

        # ── 菜单 (Menu) ─────────────────────────────────────────
        if phase == "menu":
            return {"type": "menu_state", "label": "Menu State"}

        # ── 结算 (Terminal) ─────────────────────────────────────
        if phase == "terminal":
            return {"type": "terminal", "label": "Terminal"}

        return {"type": "state_snapshot", "label": "State Snapshot"}

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
            "a": entry["action"],
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
        self, session_id: str, state_version: int, phase: str,
    ) -> dict[str, Any] | None:
        if self._last_session_id is None:
            self._last_phase = phase
            return None

        # SL detected: session_id changed
        if session_id != self._last_session_id:
            sl = {
                "type": "session_changed",
                "old_session_id": self._last_session_id,
                "new_session_id": session_id,
                "old_state_version": self._last_state_version,
                "new_state_version": state_version,
                "reason": "Session ID changed, likely Save & Load to a different run",
            }
            self._last_phase = phase
            self._phase_before_menu = None
            return sl

        # SL detected: phase pattern X → menu → X
        if phase == "menu":
            if self._last_phase and self._last_phase != "menu":
                self._phase_before_menu = self._last_phase
        elif self._phase_before_menu and phase == self._phase_before_menu:
            sl = {
                "type": "phase_return",
                "interrupted_phase": phase,
                "old_state_version": self._last_state_version,
                "new_state_version": state_version,
                "session_id": session_id,
                "reason": f"Returned to '{phase}' phase after menu interruption, likely Save & Load",
            }
            self._last_phase = phase
            self._phase_before_menu = None
            return sl
        else:
            self._phase_before_menu = None

        self._last_phase = phase
        return None

    def _write_entry(self, entry: dict[str, Any]) -> None:
        if self._jsonl_file:
            self._jsonl_file.write(json.dumps(entry, ensure_ascii=False) + "\n")
            self._jsonl_file.flush()

    def _write_slim(self, slim: dict[str, Any]) -> None:
        if self._slim_file:
            self._slim_file.write(json.dumps(slim, ensure_ascii=False) + "\n")
            self._slim_file.flush()

    def _start_new_run(self, session_id: str, state_version: int) -> None:
        run_id = f"{datetime.now().strftime('%Y%m%d_%H%M%S')}_{session_id[:8]}"
        self._current_run = RunRecord(
            run_id=run_id,
            session_id=session_id,
            started_at=datetime.now(timezone.utc).isoformat(),
        )
        self._step_index = 0
        self._total_steps = 0
        self._phases_visited = set()
        self._last_map_topo_hash = None

        jsonl_path = self.output_dir / f"{run_id}.jsonl"
        slim_path = self.output_dir / f"{run_id}.slim.jsonl"
        self._jsonl_file = jsonl_path.open("w", encoding="utf-8")
        self._slim_file = slim_path.open("w", encoding="utf-8")
        print(f"Started recording run: {run_id}")

    def _finalize_run(self) -> None:
        if self._current_run is None:
            return

        run = self._current_run

        if self._jsonl_file:
            self._jsonl_file.close()
            self._jsonl_file = None
        if self._slim_file:
            self._slim_file.close()
            self._slim_file = None

        if self._total_steps == 0:
            output_file = self.output_dir / f"{run.run_id}.jsonl"
            slim_file = self.output_dir / f"{run.run_id}.slim.jsonl"
            output_file.unlink(missing_ok=True)
            slim_file.unlink(missing_ok=True)
            self._current_run = None
            return

        output_file = self.output_dir / f"{run.run_id}.jsonl"
        slim_file = self.output_dir / f"{run.run_id}.slim.jsonl"

        summary = {
            "run_id": run.run_id,
            "session_id": run.session_id,
            "started_at": run.started_at,
            "ended_at": datetime.now(timezone.utc).isoformat(),
            "total_steps": self._total_steps,
            "sl_events": run.sl_events,
            "phases_visited": sorted(self._phases_visited),
        }
        summary_file = self.output_dir / f"{run.run_id}_summary.json"
        with summary_file.open("w", encoding="utf-8") as f:
            json.dump(summary, f, ensure_ascii=False, indent=2)

        print(f"\nSaved run: {output_file}")
        print(f"Slim:    {slim_file}")
        print(f"Summary: {summary_file}")
        print(f"  Steps: {self._total_steps}, SL events: {len(run.sl_events)}")
        self._current_run = None

    def _print_state(
        self,
        snapshot: dict[str, Any],
        actions: list[dict[str, Any]],
        sl_event: dict[str, Any] | None,
        inferred_actions: list[dict[str, Any]] | None = None,
        current_action: dict[str, Any] | None = None,
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

        action_label = (current_action or {}).get("label", "?")
        print(
            f"[{self._step_index:4d}] "
            f"sv={sv:>5}  phase={phase:<12}  "
            f"action={action_label:<20}  "
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

        if inferred_actions:
            for inf in inferred_actions:
                inf_label = inf.get("label", inf.get("type", "?"))
                print(f"         >> detected: {inf_label}")

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
