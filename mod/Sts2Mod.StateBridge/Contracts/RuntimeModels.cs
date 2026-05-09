namespace Sts2Mod.StateBridge.Contracts;

public sealed record RuntimePowerState(
    string PowerId,
    string Name,
    int? Amount = null,
    string? Description = null,
    string? CanonicalPowerId = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record RuntimePotionState(
    string Name,
    string? Description = null,
    string? CanonicalPotionId = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record RuntimeRelicState(
    string Name,
    string? Description = null,
    string? CanonicalRelicId = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record RuntimeMapNodeInfo(
    string Coord,
    string NodeType,
    int Col,
    int Row,
    bool Visited = false,
    bool IsCurrent = false);

public sealed record RuntimeMapEdge(
    string From,
    string To);

public sealed record RuntimeRunMapState(
    string? CurrentCoord = null,
    string? CurrentNodeType = null,
    IReadOnlyList<string>? ReachableNodes = null,
    string? Source = null,
    IReadOnlyList<RuntimeMapNodeInfo>? AllNodes = null,
    IReadOnlyList<RuntimeMapEdge>? AllEdges = null,
    IReadOnlyList<string>? VisitedPath = null);

public sealed record RuntimeRunState(
    int? Act = null,
    int? Floor = null,
    string? CurrentRoomType = null,
    string? CurrentLocationType = null,
    int? CurrentActIndex = null,
    int? AscensionLevel = null,
    RuntimeRunMapState? Map = null);

public sealed record RuntimeCard(
    string CardId,
    string Name,
    int Cost,
    bool Playable = true,
    string? InstanceCardId = null,
    string? CanonicalCardId = null,
    string? Description = null,
    int? CostForTurn = null,
    bool? Upgraded = null,
    string? TargetType = null,
    string? CardType = null,
    string? Rarity = null,
    IReadOnlyList<string>? Traits = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record RuntimePlayerState(
    int Hp,
    int MaxHp,
    int Block,
    int Energy,
    int Gold,
    IReadOnlyList<RuntimeCard> Hand,
    int DrawPile,
    int DiscardPile,
    int ExhaustPile,
    IReadOnlyList<RuntimeRelicState> Relics,
    IReadOnlyList<RuntimePotionState> Potions,
    int PotionCapacity,
    IReadOnlyList<RuntimePowerState>? Powers = null,
    IReadOnlyList<RuntimeCard>? DrawPileCards = null,
    IReadOnlyList<RuntimeCard>? DiscardPileCards = null,
    IReadOnlyList<RuntimeCard>? ExhaustPileCards = null);

public sealed record RuntimeEnemyState(
    string EnemyId,
    string Name,
    int Hp,
    int MaxHp,
    int Block,
    string Intent,
    bool IsAlive = true,
    string? InstanceEnemyId = null,
    string? CanonicalEnemyId = null,
    string? IntentRaw = null,
    string? IntentType = null,
    int? IntentDamage = null,
    int? IntentHits = null,
    int? IntentBlock = null,
    IReadOnlyList<string>? IntentEffects = null,
    IReadOnlyList<RuntimePowerState>? Powers = null,
    string? MoveName = null,
    string? MoveDescription = null,
    IReadOnlyList<GlossaryAnchor>? MoveGlossary = null,
    IReadOnlyList<string>? Traits = null,
    IReadOnlyList<string>? Keywords = null);

public sealed record RuntimeActionDefinition(
    string Type,
    string Label,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string>? TargetConstraints = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record RuntimeWindowContext(
    string Phase,
    RuntimePlayerState? Player,
    IReadOnlyList<RuntimeEnemyState> Enemies,
    IReadOnlyList<string> Rewards,
    IReadOnlyList<string> MapNodes,
    bool Terminal,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<RuntimeActionDefinition> Actions,
    RuntimeRunState? RunState = null);
