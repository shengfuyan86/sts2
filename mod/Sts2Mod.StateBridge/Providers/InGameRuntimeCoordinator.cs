using Sts2Mod.StateBridge.Configuration;
using Sts2Mod.StateBridge.Contracts;
using Sts2Mod.StateBridge.Core;
using Sts2Mod.StateBridge.Extraction;
using Sts2Mod.StateBridge.Logging;
using Sts2Mod.StateBridge.Server;

namespace Sts2Mod.StateBridge.Providers;

internal static class InGameRuntimeCoordinator
{
    private static readonly object Gate = new();
    private static readonly Queue<PendingAction> PendingActions = new();
    private static Sts2RuntimeReflectionReader? _reader;
    private static BridgeSessionState? _sessionState;
    private static Dictionary<string, IWindowExtractor>? _extractors;
    private static IBridgeLogger? _logger;
    private static ExportedWindow? _currentWindow;
    private static ExportedWindow? _previousWindow;
    private static string? _lastTickError;
    private static int _tickCount;
    private static DateTimeOffset? _lastTickAt;
    private static bool _initialized;

    public static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _initialized;
            }
        }
    }

    public static void Initialize(Sts2RuntimeReflectionReader reader, BridgeOptions options, IBridgeLogger logger)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _reader = reader;
            _logger = logger;
            _sessionState = new BridgeSessionState(options);
            _extractors = new IWindowExtractor[]
            {
                new CombatWindowExtractor(),
                new RewardWindowExtractor(),
                new MapWindowExtractor(),
                new EventWindowExtractor(),
                new ShopWindowExtractor(),
                new MenuWindowExtractor(),
                new TerminalWindowExtractor(),
            }.ToDictionary(extractor => extractor.Phase, StringComparer.OrdinalIgnoreCase);
            _initialized = true;
            _lastTickError = null;
            _currentWindow = null;
            _previousWindow = null;
            _tickCount = 0;
            _lastTickAt = null;
            logger.Info("Initialized in-game runtime coordinator");
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            while (PendingActions.Count > 0)
            {
                var pending = PendingActions.Dequeue();
                pending.Completion.TrySetResult(CreateFailedResponse(
                    pending.Request,
                    pending.Request.ActionId,
                    "bridge_shutdown",
                    "In-game runtime coordinator is shutting down."));
            }

            _currentWindow = null;
            _previousWindow = null;
            _lastTickError = null;
            _tickCount = 0;
            _lastTickAt = null;
            _extractors = null;
            _reader = null;
            _sessionState = null;
            _initialized = false;
        }
    }

    public static void Tick(string source)
    {
        Sts2RuntimeReflectionReader? reader;
        BridgeSessionState? sessionState;
        Dictionary<string, IWindowExtractor>? extractors;
        IBridgeLogger? logger;
        int tickCount;
        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            _tickCount++;
            _lastTickAt = DateTimeOffset.UtcNow;
            reader = _reader;
            sessionState = _sessionState;
            extractors = _extractors;
            logger = _logger;
            tickCount = _tickCount;
        }

        if (reader is null || sessionState is null || extractors is null)
        {
            return;
        }

        try
        {
            var context = reader.CaptureWindow();
            var window = extractors[context.Phase].Export(context, sessionState);
            ExportedWindow? previous;
            lock (Gate)
            {
                previous = _currentWindow;
                _previousWindow = previous;
                _currentWindow = window;
                _lastTickError = null;
            }

            if (previous is not null
                && window.Snapshot.StateVersion != previous.Snapshot.StateVersion)
            {
                var inferred = DetectAction(previous, window);
                if (inferred is not null)
                {
                    ActionLogStore.Add(inferred);
                }
            }
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                _lastTickError = ex.Message;
            }

            logger?.Warn($"In-game tick could not capture runtime window: {ex.Message}");
        }

        ProcessPendingActions(reader, logger, tickCount, source);
    }

    public static bool TryGetCurrentWindow(out ExportedWindow window, out string? error)
    {
        lock (Gate)
        {
            if (_currentWindow is not null)
            {
                window = _currentWindow;
                error = null;
                return true;
            }

            window = default!;
            error = _lastTickError ?? "In-game runtime window is not ready yet.";
            return false;
        }
    }

    private static InferredAction? DetectAction(ExportedWindow old, ExportedWindow @new)
    {
        var oldSnap = old.Snapshot;
        var newSnap = @new.Snapshot;
        var sv = newSnap.StateVersion;
        var ts = DateTimeOffset.UtcNow.ToString("O");

        // Phase change — not a player action
        if (!string.Equals(oldSnap.Phase, newSnap.Phase, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var phase = newSnap.Phase;
        return phase switch
        {
            DecisionPhase.Combat => DetectCombatAction(oldSnap, newSnap, old.Actions, sv, ts),
            DecisionPhase.Map => DetectMapAction(oldSnap, newSnap, old.Actions, sv, ts),
            DecisionPhase.Event => DetectEventAction(oldSnap, newSnap, old.Actions, sv, ts),
            DecisionPhase.Reward => DetectRewardAction(oldSnap, newSnap, old.Actions, sv, ts),
            DecisionPhase.Shop => DetectShopAction(oldSnap, newSnap, old.Actions, sv, ts),
            _ => null,
        };
    }

    private static InferredAction? DetectCombatAction(
        DecisionSnapshot oldSnap, DecisionSnapshot newSnap,
        IReadOnlyList<LegalAction> oldActions, int sv, string ts)
    {
        var oldPlayer = oldSnap.Player;
        var newPlayer = newSnap.Player;
        if (oldPlayer is null || newPlayer is null)
        {
            return null;
        }

        var oldHandIds = oldPlayer.Hand.Select(c => c.CardId).ToList();
        var newHandIds = newPlayer.Hand.Select(c => c.CardId).ToList();
        var handChanged = oldHandIds.Count != newHandIds.Count
            || !oldHandIds.SequenceEqual(newHandIds);
        var energyChanged = oldPlayer.Energy != newPlayer.Energy;

        // Card played: hand shrank or card removed, and energy changed
        if (handChanged && energyChanged && newHandIds.Count < oldHandIds.Count)
        {
            var removedIds = oldHandIds.Except(newHandIds).ToList();
            foreach (var removed in removedIds)
            {
                var matched = oldActions.FirstOrDefault(a =>
                    string.Equals(a.Type, "play_card", StringComparison.OrdinalIgnoreCase)
                    && a.Params.TryGetValue("card_id", out var cid)
                    && string.Equals(cid?.ToString(), removed, StringComparison.Ordinal));
                if (matched is not null)
                {
                    return new InferredAction(
                        Type: "play_card",
                        Label: matched.Label,
                        Params: matched.Params,
                        StateVersion: sv,
                        Phase: DecisionPhase.Combat,
                        Timestamp: ts);
                }
            }

            // Fallback: card removed but no action match
            var card = oldPlayer.Hand.FirstOrDefault(c => c.CardId == removedIds.FirstOrDefault());
            if (card is not null)
            {
                return new InferredAction(
                    Type: "play_card",
                    Label: $"Play {card.Name}",
                    Params: new Dictionary<string, object?> { ["card_id"] = card.CardId, ["card_name"] = card.Name },
                    StateVersion: sv,
                    Phase: DecisionPhase.Combat,
                    Timestamp: ts);
            }
        }

        // Potion used: potions list shrank
        if (newPlayer.Potions.Count < oldPlayer.Potions.Count)
        {
            var matched = oldActions.FirstOrDefault(a =>
                string.Equals(a.Type, "use_potion", StringComparison.OrdinalIgnoreCase));
            return new InferredAction(
                Type: "use_potion",
                Label: matched?.Label ?? "Use Potion",
                Params: matched?.Params,
                StateVersion: sv,
                Phase: DecisionPhase.Combat,
                Timestamp: ts);
        }

        // Card played via star consumption (stars in metadata changed, energy unchanged)
        var oldStars = GetMetaInt(oldSnap.Metadata, "stars");
        var newStars = GetMetaInt(newSnap.Metadata, "stars");
        if (!energyChanged && oldStars.HasValue && newStars.HasValue && newStars.Value < oldStars.Value && handChanged)
        {
            var removedIds = oldHandIds.Except(newHandIds).ToList();
            foreach (var removed in removedIds)
            {
                var matched = oldActions.FirstOrDefault(a =>
                    string.Equals(a.Type, "play_card", StringComparison.OrdinalIgnoreCase)
                    && a.Params.TryGetValue("card_id", out var cid)
                    && string.Equals(cid?.ToString(), removed, StringComparison.Ordinal));
                if (matched is not null)
                {
                    return new InferredAction(
                        Type: "play_card",
                        Label: matched.Label,
                        Params: matched.Params,
                        StateVersion: sv,
                        Phase: DecisionPhase.Combat,
                        Timestamp: ts);
                }
            }
        }

        // End turn: energy restored + hand redrawn
        if (oldPlayer.Energy == 0 && newPlayer.Energy > 0 && handChanged)
        {
            return new InferredAction(
                Type: "end_turn",
                Label: "End Turn",
                StateVersion: sv,
                Phase: DecisionPhase.Combat,
                Timestamp: ts);
        }

        // Enemy turn: only when window_kind is "enemy_turn"
        var newWindowKind = GetWindowKind(newSnap);
        if (string.Equals(newWindowKind, "enemy_turn", StringComparison.OrdinalIgnoreCase))
        {
            return new InferredAction(
                Type: "enemy_turn",
                Label: "Enemy Turn",
                StateVersion: sv,
                Phase: DecisionPhase.Combat,
                Timestamp: ts);
        }

        // Player turn with effect trigger (enemy/player HP changed but no card played)
        if (string.Equals(newWindowKind, "player_turn", StringComparison.OrdinalIgnoreCase)
            && !handChanged && !energyChanged)
        {
            var playerHpChanged = oldPlayer.Hp != newPlayer.Hp || oldPlayer.Block != newPlayer.Block;
            var enemyStateChanged = false;
            if (oldSnap.Enemies.Count == newSnap.Enemies.Count)
            {
                for (int i = 0; i < oldSnap.Enemies.Count; i++)
                {
                    if (oldSnap.Enemies[i].Hp != newSnap.Enemies[i].Hp
                        || oldSnap.Enemies[i].Block != newSnap.Enemies[i].Block)
                    {
                        enemyStateChanged = true;
                        break;
                    }
                }
            }

            if (playerHpChanged || enemyStateChanged)
            {
                return new InferredAction(
                    Type: "effect_trigger",
                    Label: "Effect Trigger",
                    StateVersion: sv,
                    Phase: DecisionPhase.Combat,
                    Timestamp: ts);
            }
        }

        return null;
    }

    private static InferredAction? DetectMapAction(
        DecisionSnapshot oldSnap, DecisionSnapshot newSnap,
        IReadOnlyList<LegalAction> oldActions, int sv, string ts)
    {
        var oldCoord = oldSnap.RunState?.Map?.CurrentCoord;
        var newCoord = newSnap.RunState?.Map?.CurrentCoord;
        if (string.IsNullOrEmpty(oldCoord) || string.IsNullOrEmpty(newCoord)
            || string.Equals(oldCoord, newCoord, StringComparison.Ordinal))
        {
            return null;
        }

        // Match against legal actions
        var matched = oldActions.FirstOrDefault(a =>
            string.Equals(a.Type, "choose_map_node", StringComparison.OrdinalIgnoreCase)
            && a.Params.TryGetValue("node", out var node)
            && node?.ToString()?.Contains(newCoord) == true);
        if (matched is not null)
        {
            return new InferredAction(
                Type: "choose_map_node",
                Label: matched.Label,
                Params: matched.Params,
                StateVersion: sv,
                Phase: DecisionPhase.Map,
                Timestamp: ts);
        }

        return new InferredAction(
            Type: "choose_map_node",
            Label: $"Choose {newCoord}",
            Params: new Dictionary<string, object?> { ["node"] = newCoord },
            StateVersion: sv,
            Phase: DecisionPhase.Map,
            Timestamp: ts);
    }

    private static InferredAction? DetectEventAction(
        DecisionSnapshot oldSnap, DecisionSnapshot newSnap,
        IReadOnlyList<LegalAction> oldActions, int sv, string ts)
    {
        var oldMeta = oldSnap.Metadata;
        var newMeta = newSnap.Metadata;
        var oldTitle = GetMetaStr(oldMeta, "event_title");
        var newTitle = GetMetaStr(newMeta, "event_title");
        var oldKind = GetMetaStr(oldMeta, "window_kind");
        var newKind = GetMetaStr(newMeta, "window_kind");

        // Event option chosen (title changed)
        if (!string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
        {
            var matched = oldActions.FirstOrDefault(a =>
                string.Equals(a.Type, "choose_event_option", StringComparison.OrdinalIgnoreCase));
            return new InferredAction(
                Type: "choose_event_option",
                Label: matched?.Label ?? newTitle ?? "Event Choice",
                Params: matched?.Params,
                StateVersion: sv,
                Phase: DecisionPhase.Event,
                Timestamp: ts);
        }

        // Continue event (window_kind changed)
        if (!string.Equals(oldKind, newKind, StringComparison.Ordinal))
        {
            var matched = oldActions.FirstOrDefault(a =>
                string.Equals(a.Type, "continue_event", StringComparison.OrdinalIgnoreCase));
            return new InferredAction(
                Type: "continue_event",
                Label: matched?.Label ?? "Continue",
                Params: matched?.Params,
                StateVersion: sv,
                Phase: DecisionPhase.Event,
                Timestamp: ts);
        }

        return null;
    }

    private static InferredAction? DetectRewardAction(
        DecisionSnapshot oldSnap, DecisionSnapshot newSnap,
        IReadOnlyList<LegalAction> oldActions, int sv, string ts)
    {
        var oldRewards = oldSnap.Rewards;
        var newRewards = newSnap.Rewards;

        if (oldRewards.Count == newRewards.Count)
        {
            return null;
        }

        // Reward taken
        var removed = oldRewards.Except(newRewards).FirstOrDefault();
        if (removed is not null)
        {
            var matched = oldActions.FirstOrDefault(a =>
                string.Equals(a.Type, "choose_reward", StringComparison.OrdinalIgnoreCase)
                && a.Params.TryGetValue("reward", out var r)
                && string.Equals(r?.ToString(), removed, StringComparison.Ordinal));
            return new InferredAction(
                Type: "choose_reward",
                Label: matched?.Label ?? $"Choose {removed}",
                Params: matched?.Params ?? new Dictionary<string, object?> { ["reward"] = removed },
                StateVersion: sv,
                Phase: DecisionPhase.Reward,
                Timestamp: ts);
        }

        // All rewards consumed or advanced
        if (newRewards.Count == 0)
        {
            var advance = oldActions.FirstOrDefault(a =>
                string.Equals(a.Type, "advance_reward", StringComparison.OrdinalIgnoreCase));
            var skip = oldActions.FirstOrDefault(a =>
                string.Equals(a.Type, "skip_reward", StringComparison.OrdinalIgnoreCase));
            var matched = advance ?? skip;
            return new InferredAction(
                Type: matched?.Type ?? "advance_reward",
                Label: matched?.Label ?? "Advance",
                Params: matched?.Params,
                StateVersion: sv,
                Phase: DecisionPhase.Reward,
                Timestamp: ts);
        }

        return null;
    }

    private static InferredAction? DetectShopAction(
        DecisionSnapshot oldSnap, DecisionSnapshot newSnap,
        IReadOnlyList<LegalAction> oldActions, int sv, string ts)
    {
        var goldChanged = (oldSnap.Player?.Gold ?? 0) != (newSnap.Player?.Gold ?? 0);
        if (!goldChanged)
        {
            return null;
        }

        // Try to match against specific shop actions
        foreach (var action in oldActions)
        {
            if (action.Type.StartsWith("buy_shop_", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action.Type, "purge_shop_card", StringComparison.OrdinalIgnoreCase))
            {
                // Check if the item is no longer available (purchased)
                // This is a best-effort match
            }
        }

        return new InferredAction(
            Type: "shop_purchase",
            Label: "Shop Purchase",
            StateVersion: sv,
            Phase: DecisionPhase.Shop,
            Timestamp: ts);
    }

    private static string? GetWindowKind(DecisionSnapshot snap)
    {
        return snap.Metadata.TryGetValue("window_kind", out var val) ? val as string : null;
    }

    private static string? GetMetaStr(IReadOnlyDictionary<string, object?> meta, string key)
    {
        return meta.TryGetValue(key, out var val) ? val?.ToString() : null;
    }

    private static int? GetMetaInt(IReadOnlyDictionary<string, object?> meta, string key)
    {
        if (!meta.TryGetValue(key, out var val) || val is null)
        {
            return null;
        }

        if (val is int i)
        {
            return i;
        }

        if (int.TryParse(val.ToString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static ActionResponse ApplyAction(ActionRequest request, bool readOnly)
    {
        request = EnsureRequestId(request);
        if (readOnly)
        {
            return CreateRejectedResponse(request, request.ActionId, "read_only", "Bridge is running in read-only mode.");
        }

        PendingAction pending;
        int tickCount;
        int queueDepth;
        lock (Gate)
        {
            if (!_initialized)
            {
                return CreateRejectedResponse(request, request.ActionId, "not_in_game_runtime", "Bridge is not running inside the STS2 process.");
            }

            pending = new PendingAction(request);
            PendingActions.Enqueue(pending);
            tickCount = _tickCount;
            queueDepth = PendingActions.Count;
        }
        pending.Trace.MarkEnqueued(tickCount, Environment.CurrentManagedThreadId);
        _logger?.Info($"Enqueued in-game action request_id={request.RequestId} action_id={request.ActionId} decision_id={request.DecisionId} queue_depth={queueDepth}");

        if (!pending.Completion.Task.Wait(TimeSpan.FromSeconds(3)))
        {
            var metadata = CreateTraceMetadata(pending);
            _logger?.Warn($"Timed out waiting for in-game action request_id={request.RequestId} action_id={request.ActionId} stage={metadata["queue_stage"]}");
            return CreateFailedResponse(
                request,
                request.ActionId,
                "action_timeout",
                "Timed out waiting for the game thread to process the action.",
                metadata);
        }

        return pending.Completion.Task.GetAwaiter().GetResult();
    }

    private static void ProcessPendingActions(Sts2RuntimeReflectionReader reader, IBridgeLogger? logger, int tickCount, string source)
    {
        while (true)
        {
            PendingAction? pending;
            ExportedWindow? window;
            int queueDepth;
            lock (Gate)
            {
                if (PendingActions.Count == 0)
                {
                    return;
                }

                pending = PendingActions.Dequeue();
                window = _currentWindow;
                queueDepth = PendingActions.Count;
            }

            try
            {
                pending.Trace.MarkDequeued(tickCount, Environment.CurrentManagedThreadId);
                logger?.Info($"Dequeued in-game action request_id={pending.Request.RequestId} source={source} queue_depth={queueDepth}");
                pending.Trace.MarkExecuting(tickCount, Environment.CurrentManagedThreadId);
                var response = ExecutePendingAction(reader, pending.Request, window, tickCount);
                response = MergeTraceMetadata(response, pending, tickCount, response.Status);
                pending.Completion.TrySetResult(response);
            }
            catch (Exception ex)
            {
                pending.Trace.MarkFailed(tickCount, "action_execution_failed", ex.Message);
                logger?.Error("Failed to process queued in-game action", ex);
                pending.Completion.TrySetResult(CreateFailedResponse(
                    pending.Request,
                    pending.Request.ActionId,
                    "action_execution_failed",
                    ex.Message,
                    CreateTraceMetadata(pending)));
            }
        }
    }

    private static ActionResponse ExecutePendingAction(
        Sts2RuntimeReflectionReader reader,
        ActionRequest request,
        ExportedWindow? currentWindow,
        int tickCount)
    {
        currentWindow = RefreshCurrentWindow(reader) ?? currentWindow;
        if (currentWindow is null)
        {
            return CreateRejectedResponse(request, request.ActionId, "runtime_not_ready", "No live decision window is available yet.");
        }

        if (!string.Equals(request.DecisionId, currentWindow.Snapshot.DecisionId, StringComparison.Ordinal))
        {
            return CreateRejectedResponse(request, request.ActionId, "stale_decision", "Requested decision_id is no longer current.");
        }

        var action = ResolveAction(currentWindow.Actions, request);
        if (action is null)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["phase"] = currentWindow.Snapshot.Phase,
                ["state_version"] = currentWindow.Snapshot.StateVersion,
                ["tick_count"] = tickCount,
            };
            foreach (var pair in currentWindow.Snapshot.Metadata)
            {
                if (pair.Key is "window_kind" or "current_side" or "selection_kind" or "transition_kind")
                {
                    metadata[pair.Key] = pair.Value;
                }
            }

            return CreateRejectedResponse(
                request,
                request.ActionId,
                InferIllegalActionErrorCode(currentWindow, request),
                "Requested action is not part of the current legal action set.",
                metadata);
        }

        var result = reader.ExecuteAction(request, action);
        var responseMetadata = new Dictionary<string, object?>(result.Metadata)
        {
            ["phase"] = currentWindow.Snapshot.Phase,
            ["state_version"] = currentWindow.Snapshot.StateVersion,
            ["tick_count"] = tickCount,
        };
        if (!result.Accepted)
        {
            if (string.Equals(result.ErrorCode, "runtime_not_applied", StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailedResponse(request, action.ActionId, result.ErrorCode ?? "runtime_not_applied", result.Message, responseMetadata);
            }

            return CreateRejectedResponse(request, action.ActionId, result.ErrorCode ?? "action_rejected", result.Message, responseMetadata);
        }

        return CreateAcceptedResponse(request, action.ActionId, result.Message, responseMetadata);
    }

    private static ExportedWindow? RefreshCurrentWindow(Sts2RuntimeReflectionReader reader)
    {
        Dictionary<string, IWindowExtractor>? extractors;
        BridgeSessionState? sessionState;
        try
        {
            lock (Gate)
            {
                extractors = _extractors;
                sessionState = _sessionState;
            }

            if (extractors is null || sessionState is null)
            {
                return null;
            }

            var context = reader.CaptureWindow();
            var refreshed = extractors[context.Phase].Export(context, sessionState);
            lock (Gate)
            {
                _currentWindow = refreshed;
            }

            return refreshed;
        }
        catch
        {
            return null;
        }
    }

    private static string InferIllegalActionErrorCode(ExportedWindow currentWindow, ActionRequest request)
    {
        var actionType = ResolveRequestedActionType(currentWindow.Actions, request);
        var windowKind = currentWindow.Snapshot.Metadata.TryGetValue("window_kind", out var rawWindowKind)
            ? rawWindowKind as string
            : null;
        if ((string.Equals(actionType, "play_card", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "end_turn", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(windowKind, "enemy_turn", StringComparison.OrdinalIgnoreCase))
        {
            return "not_player_turn";
        }

        if ((string.Equals(actionType, "choose_combat_card", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "cancel_combat_selection", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(windowKind, "combat_card_selection", StringComparison.OrdinalIgnoreCase))
        {
            return "selection_window_changed";
        }

        if ((string.Equals(actionType, "choose_event_option", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "continue_event", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(currentWindow.Snapshot.Phase, DecisionPhase.Event, StringComparison.OrdinalIgnoreCase))
        {
            return "selection_window_changed";
        }

        if ((string.Equals(actionType, "buy_shop_card", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "buy_shop_relic", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "buy_shop_potion", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "purge_shop_card", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(actionType, "leave_shop", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(currentWindow.Snapshot.Phase, DecisionPhase.Shop, StringComparison.OrdinalIgnoreCase))
        {
            return "selection_window_changed";
        }

        return "illegal_action";
    }

    private static string? ResolveRequestedActionType(IEnumerable<LegalAction> actions, ActionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ActionType))
        {
            return request.ActionType;
        }

        if (!string.IsNullOrWhiteSpace(request.ActionId))
        {
            return actions.FirstOrDefault(action => string.Equals(action.ActionId, request.ActionId, StringComparison.Ordinal))?.Type;
        }

        return null;
    }

    private static LegalAction? ResolveAction(IEnumerable<LegalAction> actions, ActionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ActionId))
        {
            return actions.FirstOrDefault(action => string.Equals(action.ActionId, request.ActionId, StringComparison.Ordinal));
        }

        return actions.FirstOrDefault(action =>
            string.Equals(action.Type, request.ActionType, StringComparison.OrdinalIgnoreCase) &&
            request.Params.All(pair => action.Params.TryGetValue(pair.Key, out var value) && Equals(value, pair.Value)));
    }

    private static ActionResponse CreateAcceptedResponse(
        ActionRequest request,
        string? actionId,
        string message,
        IReadOnlyDictionary<string, object?> metadata)
    {
        return new ActionResponse(
            RequestId: request.RequestId ?? Guid.NewGuid().ToString("N"),
            DecisionId: request.DecisionId,
            ActionId: actionId,
            Status: "accepted",
            ErrorCode: null,
            Message: message,
            Metadata: metadata);
    }

    private static ActionResponse CreateRejectedResponse(
        ActionRequest request,
        string? actionId,
        string errorCode,
        string message,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new ActionResponse(
            RequestId: request.RequestId ?? Guid.NewGuid().ToString("N"),
            DecisionId: request.DecisionId,
            ActionId: actionId,
            Status: "rejected",
            ErrorCode: errorCode,
            Message: message,
            Metadata: metadata ?? new Dictionary<string, object?>());
    }

    private static ActionResponse CreateFailedResponse(
        ActionRequest request,
        string? actionId,
        string errorCode,
        string message,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new ActionResponse(
            RequestId: request.RequestId ?? Guid.NewGuid().ToString("N"),
            DecisionId: request.DecisionId,
            ActionId: actionId,
            Status: "failed",
            ErrorCode: errorCode,
            Message: message,
            Metadata: metadata ?? new Dictionary<string, object?>());
    }

    private static ActionRequest EnsureRequestId(ActionRequest request)
    {
        return string.IsNullOrWhiteSpace(request.RequestId)
            ? request with { RequestId = Guid.NewGuid().ToString("N") }
            : request;
    }

    private static IReadOnlyDictionary<string, object?> CreateTraceMetadata(PendingAction pending)
    {
        int lastTickCount;
        DateTimeOffset? lastTickAt;
        int pendingQueueCount;
        bool currentWindowReady;
        lock (Gate)
        {
            lastTickCount = _tickCount;
            lastTickAt = _lastTickAt;
            pendingQueueCount = PendingActions.Count;
            currentWindowReady = _currentWindow is not null;
        }

        return pending.Trace.ToMetadata(lastTickCount, lastTickAt, pendingQueueCount, currentWindowReady);
    }

    private static ActionResponse MergeTraceMetadata(ActionResponse response, PendingAction pending, int tickCount, string status)
    {
        if (string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            pending.Trace.MarkCompleted(tickCount, "completed", response.Message);
        }
        else if (string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
        {
            pending.Trace.MarkCompleted(tickCount, "rejected", response.ErrorCode ?? response.Message);
        }

        var metadata = new Dictionary<string, object?>(response.Metadata);
        foreach (var pair in CreateTraceMetadata(pending))
        {
            metadata[pair.Key] = pair.Value;
        }

        return response with { Metadata = metadata };
    }

    private sealed class PendingAction
    {
        public PendingAction(ActionRequest request)
        {
            Request = request;
            Trace = new InGameActionTrace(request.RequestId ?? Guid.NewGuid().ToString("N"));
            Completion = new TaskCompletionSource<ActionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ActionRequest Request { get; }

        public InGameActionTrace Trace { get; }

        public TaskCompletionSource<ActionResponse> Completion { get; }
    }
}
