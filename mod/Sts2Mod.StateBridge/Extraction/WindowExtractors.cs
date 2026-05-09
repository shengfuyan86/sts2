using Sts2Mod.StateBridge.Contracts;
using Sts2Mod.StateBridge.Core;
using System.Text.Json;

namespace Sts2Mod.StateBridge.Extraction;

public abstract class WindowExtractorBase : IWindowExtractor
{
    public abstract string Phase { get; }

    public ExportedWindow Export(RuntimeWindowContext context, BridgeSessionState sessionState)
    {
        sessionState.AdvanceIfNeeded(context.Phase, CreateFingerprint(context));
        var snapshot = BuildSnapshot(context, sessionState);
        var actions = context.Actions.Select(action => new LegalAction(
            sessionState.CreateActionId(action.Type, action.Parameters),
            action.Type,
            action.Label,
            action.Parameters,
            action.TargetConstraints ?? Array.Empty<string>(),
            action.Metadata ?? new Dictionary<string, object?>())).ToArray();
        return new ExportedWindow(snapshot, actions);
    }

    private static string CreateFingerprint(RuntimeWindowContext context)
    {
        var canonical = new
        {
            context.Phase,
            context.Terminal,
            Player = context.Player is null
                ? null
                : new
                {
                    context.Player.Hp,
                    context.Player.MaxHp,
                    context.Player.Block,
                    context.Player.Energy,
                    context.Player.Gold,
                    Hand = context.Player.Hand.Select(card => new
                    {
                        card.CardId,
                        card.Name,
                        card.Cost,
                        card.Playable,
                        card.InstanceCardId,
                        card.CanonicalCardId,
                        card.Description,
                        card.CostForTurn,
                        card.Upgraded,
                        card.TargetType,
                        card.CardType,
                        card.Rarity,
                        Traits = card.Traits?.ToArray() ?? Array.Empty<string>(),
                        Keywords = card.Keywords?.ToArray() ?? Array.Empty<string>(),
                        Glossary = card.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray(),
                    context.Player.DrawPile,
                    context.Player.DiscardPile,
                    context.Player.ExhaustPile,
                    DrawPileCards = context.Player.DrawPileCards?.Select(card => new
                    {
                        card.CardId,
                        card.Name,
                        card.Cost,
                        card.Playable,
                        card.InstanceCardId,
                        card.CanonicalCardId,
                        card.Description,
                        card.CostForTurn,
                        card.Upgraded,
                        card.TargetType,
                        card.CardType,
                        card.Rarity,
                        Traits = card.Traits?.ToArray() ?? Array.Empty<string>(),
                        Keywords = card.Keywords?.ToArray() ?? Array.Empty<string>(),
                        Glossary = card.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray() ?? Array.Empty<object>(),
                    DiscardPileCards = context.Player.DiscardPileCards?.Select(card => new
                    {
                        card.CardId,
                        card.Name,
                        card.Cost,
                        card.Playable,
                        card.InstanceCardId,
                        card.CanonicalCardId,
                        card.Description,
                        card.CostForTurn,
                        card.Upgraded,
                        card.TargetType,
                        card.CardType,
                        card.Rarity,
                        Traits = card.Traits?.ToArray() ?? Array.Empty<string>(),
                        Keywords = card.Keywords?.ToArray() ?? Array.Empty<string>(),
                        Glossary = card.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray() ?? Array.Empty<object>(),
                    ExhaustPileCards = context.Player.ExhaustPileCards?.Select(card => new
                    {
                        card.CardId,
                        card.Name,
                        card.Cost,
                        card.Playable,
                        card.InstanceCardId,
                        card.CanonicalCardId,
                        card.Description,
                        card.CostForTurn,
                        card.Upgraded,
                        card.TargetType,
                        card.CardType,
                        card.Rarity,
                        Traits = card.Traits?.ToArray() ?? Array.Empty<string>(),
                        Keywords = card.Keywords?.ToArray() ?? Array.Empty<string>(),
                        Glossary = card.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray() ?? Array.Empty<object>(),
                    Relics = context.Player.Relics.Select(relic => new
                    {
                        relic.Name,
                        relic.Description,
                        relic.CanonicalRelicId,
                        Glossary = relic.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray(),
                    Potions = context.Player.Potions.Select(potion => new
                    {
                        potion.Name,
                        potion.Description,
                        potion.CanonicalPotionId,
                        Glossary = potion.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray(),
                    context.Player.PotionCapacity,
                    Powers = context.Player.Powers?.Select(power => new
                    {
                        power.PowerId,
                        power.Name,
                        power.Amount,
                        power.Description,
                        power.CanonicalPowerId,
                        Glossary = power.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                    }).ToArray() ?? Array.Empty<object>(),
                },
            Enemies = context.Enemies.Select(enemy => new
            {
                enemy.EnemyId,
                enemy.Name,
                enemy.Hp,
                enemy.MaxHp,
                enemy.Block,
                enemy.Intent,
                enemy.IsAlive,
                enemy.InstanceEnemyId,
                enemy.CanonicalEnemyId,
                enemy.IntentRaw,
                enemy.IntentType,
                enemy.IntentDamage,
                enemy.IntentHits,
                enemy.IntentBlock,
                IntentEffects = enemy.IntentEffects?.ToArray() ?? Array.Empty<string>(),
                enemy.MoveName,
                enemy.MoveDescription,
                MoveGlossary = enemy.MoveGlossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                Traits = enemy.Traits?.ToArray() ?? Array.Empty<string>(),
                Keywords = enemy.Keywords?.ToArray() ?? Array.Empty<string>(),
                Powers = enemy.Powers?.Select(power => new
                {
                    power.PowerId,
                    power.Name,
                    power.Amount,
                    power.Description,
                    power.CanonicalPowerId,
                    Glossary = power.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
                }).ToArray() ?? Array.Empty<object>(),
            }).ToArray(),
            Rewards = context.Rewards.ToArray(),
            MapNodes = context.MapNodes.ToArray(),
            RunState = context.RunState is null
                ? null
                : new
                {
                    context.RunState.Act,
                    context.RunState.Floor,
                    context.RunState.CurrentRoomType,
                    context.RunState.CurrentLocationType,
                    context.RunState.CurrentActIndex,
                    context.RunState.AscensionLevel,
                    Map = context.RunState.Map is null
                        ? null
                        : new
                        {
                            context.RunState.Map.CurrentCoord,
                            context.RunState.Map.CurrentNodeType,
                            ReachableNodes = context.RunState.Map.ReachableNodes?.ToArray() ?? Array.Empty<string>(),
                            context.RunState.Map.Source,
                        },
                },
            Metadata = FilterStableMetadata(context.Metadata),
            Actions = context.Actions.Select(action => new
            {
                action.Type,
                action.Label,
                Parameters = action.Parameters.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value),
                TargetConstraints = action.TargetConstraints?.ToArray() ?? Array.Empty<string>(),
                Metadata = FilterStableMetadata(action.Metadata ?? new Dictionary<string, object?>()),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(canonical);
    }

    private static IReadOnlyDictionary<string, object?> FilterStableMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        return metadata
            .Where(pair => !IsDiagnosticsKey(pair.Key))
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static bool IsDiagnosticsKey(string key)
    {
        return string.Equals(key, "text_diagnostics", StringComparison.Ordinal) ||
               string.Equals(key, "diagnostics", StringComparison.Ordinal);
    }

    protected DecisionSnapshot BuildSnapshot(RuntimeWindowContext context, BridgeSessionState sessionState)
    {
        return new DecisionSnapshot(
            sessionState.SessionId,
            sessionState.DecisionId,
            sessionState.StateVersion,
            context.Phase,
            context.Player is null ? null : Convert(context.Player),
            context.Enemies.Select(Convert).ToArray(),
            context.Rewards.ToArray(),
            context.MapNodes.ToArray(),
            context.Terminal,
            sessionState.Compatibility,
            BuildMetadata(context),
            context.RunState is null ? null : Convert(context.RunState));
    }

    protected virtual IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        return new Dictionary<string, object?>(context.Metadata);
    }

    protected static PlayerState Convert(RuntimePlayerState player)
    {
        return new PlayerState(
            player.Hp,
            player.MaxHp,
            player.Block,
            player.Energy,
            player.Gold,
            player.Hand.Select(Convert).ToArray(),
            player.DrawPile,
            player.DiscardPile,
            player.ExhaustPile,
            player.Relics.Select(Convert).ToArray(),
            player.Potions.Select(Convert).ToArray(),
            player.PotionCapacity,
            Convert(player.Powers),
            player.DrawPileCards?.Select(Convert).ToArray() ?? Array.Empty<CardView>(),
            player.DiscardPileCards?.Select(Convert).ToArray() ?? Array.Empty<CardView>(),
            player.ExhaustPileCards?.Select(Convert).ToArray() ?? Array.Empty<CardView>());
    }

    protected static EnemyState Convert(RuntimeEnemyState enemy)
    {
        return new EnemyState(
            enemy.EnemyId,
            enemy.Name,
            enemy.Hp,
            enemy.MaxHp,
            enemy.Block,
            enemy.Intent,
            enemy.IsAlive,
            enemy.InstanceEnemyId,
            enemy.CanonicalEnemyId,
            enemy.IntentRaw,
            enemy.IntentType,
            enemy.IntentDamage,
            enemy.IntentHits,
            enemy.IntentBlock,
            enemy.IntentEffects?.ToArray() ?? Array.Empty<string>(),
            Convert(enemy.Powers),
            enemy.MoveName,
            enemy.MoveDescription,
            enemy.MoveGlossary?.ToArray() ?? Array.Empty<GlossaryAnchor>(),
            enemy.Traits?.ToArray() ?? Array.Empty<string>(),
            enemy.Keywords?.ToArray() ?? Array.Empty<string>());
    }

    protected static CardView Convert(RuntimeCard card)
    {
        return new CardView(
            card.CardId,
            card.Name,
            card.Cost,
            card.Playable,
            card.InstanceCardId,
            card.CanonicalCardId,
            card.Description,
            card.CostForTurn,
            card.Upgraded,
            card.TargetType,
            card.CardType,
            card.Rarity,
            card.Traits?.ToArray() ?? Array.Empty<string>(),
            card.Keywords?.ToArray() ?? Array.Empty<string>(),
            card.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>());
    }

    protected static IReadOnlyList<PowerView> Convert(IReadOnlyList<RuntimePowerState>? powers)
    {
        return powers?.Select(Convert).ToArray() ?? Array.Empty<PowerView>();
    }

    protected static PowerView Convert(RuntimePowerState power)
    {
        return new PowerView(
            power.PowerId,
            power.Name,
            power.Amount,
            power.Description,
            power.CanonicalPowerId,
            power.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>());
    }

    protected static PotionView Convert(RuntimePotionState potion)
    {
        return new PotionView(
            potion.Name,
            potion.Description,
            potion.CanonicalPotionId,
            potion.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>());
    }

    protected static RelicView Convert(RuntimeRelicState relic)
    {
        return new RelicView(
            relic.Name,
            relic.Description,
            relic.CanonicalRelicId,
            relic.Glossary?.ToArray() ?? Array.Empty<GlossaryAnchor>());
    }

    protected static RunState Convert(RuntimeRunState runState)
    {
        return new RunState(
            runState.Act,
            runState.Floor,
            runState.CurrentRoomType,
            runState.CurrentLocationType,
            runState.CurrentActIndex,
            runState.AscensionLevel,
            runState.Map is null ? null : new RunMapState(
                runState.Map.CurrentCoord,
                runState.Map.CurrentNodeType,
                runState.Map.ReachableNodes?.ToArray() ?? Array.Empty<string>(),
                runState.Map.Source,
                runState.Map.AllNodes?.Select(n => new MapNodeInfo(n.Coord, n.NodeType, n.Col, n.Row, n.Visited, n.IsCurrent)).ToArray() ?? Array.Empty<MapNodeInfo>(),
                runState.Map.AllEdges?.Select(e => new MapEdge(e.From, e.To)).ToArray() ?? Array.Empty<MapEdge>(),
                runState.Map.VisitedPath?.ToArray() ?? Array.Empty<string>()));
    }
}

public sealed class CombatWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Combat;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata)
        {
            ["supports_targeting"] = true,
        };
        if (!metadata.ContainsKey("window_kind"))
        {
            metadata["window_kind"] = "player_turn";
        }
        return metadata;
    }
}

public sealed class RewardWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Reward;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata)
        {
            ["reward_count"] = context.Rewards.Count,
        };
        if (!metadata.ContainsKey("window_kind"))
        {
            metadata["window_kind"] = "reward_choice";
        }
        return metadata;
    }
}

public sealed class MapWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Map;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata)
        {
            ["node_count"] = context.MapNodes.Count,
        };
        if (!metadata.ContainsKey("window_kind"))
        {
            metadata["window_kind"] = "map_choice";
        }
        return metadata;
    }
}

public sealed class EventWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Event;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata)
        {
            ["event_continue_available"] = context.Metadata.TryGetValue("event_continue_available", out var continueAvailable)
                ? continueAvailable
                : context.Actions.Any(action => action.Type == "continue_event"),
            ["supports_targeting"] = false,
        };
        if (!metadata.ContainsKey("window_kind"))
        {
            metadata["window_kind"] = "event_transition";
        }
        return metadata;
    }
}

public sealed class ShopWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Shop;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata)
        {
            ["supports_targeting"] = false,
        };
        if (!metadata.ContainsKey("window_kind"))
        {
            metadata["window_kind"] = "shop_main";
        }
        if (!metadata.ContainsKey("shop_offer_count") &&
            metadata.TryGetValue("shop_offers", out var offers) &&
            offers is System.Collections.ICollection collection)
        {
            metadata["shop_offer_count"] = collection.Count;
        }
        return metadata;
    }
}

public sealed class MenuWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Menu;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata);
        if (!metadata.ContainsKey("window_kind"))
        {
            metadata["window_kind"] = "main_menu";
        }
        metadata["supports_targeting"] = false;
        return metadata;
    }
}

public sealed class TerminalWindowExtractor : WindowExtractorBase
{
    public override string Phase => DecisionPhase.Terminal;

    protected override IReadOnlyDictionary<string, object?> BuildMetadata(RuntimeWindowContext context)
    {
        var metadata = new Dictionary<string, object?>(context.Metadata)
        {
            ["window_kind"] = "terminal"
        };
        return metadata;
    }
}
