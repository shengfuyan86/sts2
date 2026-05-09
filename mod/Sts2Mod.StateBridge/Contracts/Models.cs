namespace Sts2Mod.StateBridge.Contracts;

public sealed record DescriptionVariable(
    string Key,
    int? Value = null,
    string? Source = null,
    string? Placeholder = null,
    string? SemanticKind = null);

public sealed record GlossaryAnchor(
    string GlossaryId,
    string DisplayText,
    string? Hint = null,
    string? Source = null);

public static class DecisionPhase
{
    public const string Combat = "combat";
    public const string Reward = "reward";
    public const string Map = "map";
    public const string Event = "event";
    public const string Shop = "shop";
    public const string Menu = "menu";
    public const string Terminal = "terminal";
}

public sealed record CompatibilityMetadata(
    string ProtocolVersion,
    string ModVersion,
    string GameVersion,
    string ProviderMode,
    bool ReadOnly,
    bool Ready,
    string? Notes = null);

public sealed record PowerView(
    string PowerId,
    string Name,
    int? Amount = null,
    string? Description = null,
    string? CanonicalPowerId = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record PotionView(
    string Name,
    string? Description = null,
    string? CanonicalPotionId = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record RelicView(
    string Name,
    string? Description = null,
    string? CanonicalRelicId = null,
    IReadOnlyList<GlossaryAnchor>? Glossary = null);

public sealed record MapNodeInfo(
    string Coord,
    string NodeType,
    int Col,
    int Row,
    bool Visited = false,
    bool IsCurrent = false);

public sealed record MapEdge(
    string From,
    string To);

public sealed record RunMapState(
    string? CurrentCoord = null,
    string? CurrentNodeType = null,
    IReadOnlyList<string>? ReachableNodes = null,
    string? Source = null,
    IReadOnlyList<MapNodeInfo>? AllNodes = null,
    IReadOnlyList<MapEdge>? AllEdges = null,
    IReadOnlyList<string>? VisitedPath = null);

public sealed record RunState(
    int? Act = null,
    int? Floor = null,
    string? CurrentRoomType = null,
    string? CurrentLocationType = null,
    int? CurrentActIndex = null,
    int? AscensionLevel = null,
    RunMapState? Map = null);

public sealed record CardView(
    string CardId,
    string Name,
    int Cost,
    bool Playable,
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

public sealed record PlayerState(
    int Hp,
    int MaxHp,
    int Block,
    int Energy,
    int Gold,
    IReadOnlyList<CardView> Hand,
    int DrawPile,
    int DiscardPile,
    int ExhaustPile,
    IReadOnlyList<RelicView> Relics,
    IReadOnlyList<PotionView> Potions,
    int PotionCapacity,
    IReadOnlyList<PowerView>? Powers = null,
    IReadOnlyList<CardView>? DrawPileCards = null,
    IReadOnlyList<CardView>? DiscardPileCards = null,
    IReadOnlyList<CardView>? ExhaustPileCards = null);

public sealed record EnemyState(
    string EnemyId,
    string Name,
    int Hp,
    int MaxHp,
    int Block,
    string Intent,
    bool IsAlive,
    string? InstanceEnemyId = null,
    string? CanonicalEnemyId = null,
    string? IntentRaw = null,
    string? IntentType = null,
    int? IntentDamage = null,
    int? IntentHits = null,
    int? IntentBlock = null,
    IReadOnlyList<string>? IntentEffects = null,
    IReadOnlyList<PowerView>? Powers = null,
    string? MoveName = null,
    string? MoveDescription = null,
    IReadOnlyList<GlossaryAnchor>? MoveGlossary = null,
    IReadOnlyList<string>? Traits = null,
    IReadOnlyList<string>? Keywords = null);

public sealed record LegalAction(
    string ActionId,
    string Type,
    string Label,
    IReadOnlyDictionary<string, object?> Params,
    IReadOnlyList<string> TargetConstraints,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record DecisionSnapshot(
    string SessionId,
    string DecisionId,
    int StateVersion,
    string Phase,
    PlayerState? Player,
    IReadOnlyList<EnemyState> Enemies,
    IReadOnlyList<string> Rewards,
    IReadOnlyList<string> MapNodes,
    bool Terminal,
    CompatibilityMetadata Compatibility,
    IReadOnlyDictionary<string, object?> Metadata,
    RunState? RunState = null);

public sealed record HealthResponse(
    bool Healthy,
    string ProtocolVersion,
    string ModVersion,
    string GameVersion,
    string ProviderMode,
    bool ReadOnly,
    string Status);

public sealed record ActionRequest(
    string DecisionId,
    string? ActionId,
    string? ActionType,
    IReadOnlyDictionary<string, object?> Params,
    string? RequestId = null);

public sealed record ActionResponse(
    string RequestId,
    string DecisionId,
    string? ActionId,
    string Status,
    string? ErrorCode,
    string Message,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record AgentStatusUpdateRequest(
    string? SessionId,
    string? Phase,
    string? Status,
    string? UpdatedAt,
    string? ActionId = null,
    string? ActionLabel = null,
    string? Reason = null,
    string? Detail = null,
    string? Confidence = null,
    int? Turn = null,
    int? Step = null);

public sealed record AgentStatusHistoryEntry(
    string Status,
    string? Phase = null,
    string? ActionLabel = null,
    string? Reason = null,
    string? Detail = null,
    string? Confidence = null,
    int? Turn = null,
    int? Step = null,
    string? UpdatedAt = null);

public sealed record AgentStatusResponse(
    bool Empty,
    bool Stale,
    string Status,
    string? SourceStatus = null,
    string? SessionId = null,
    string? Phase = null,
    string? ActionId = null,
    string? ActionLabel = null,
    string? Reason = null,
    string? Detail = null,
    string? Confidence = null,
    int? Turn = null,
    int? Step = null,
    string? UpdatedAt = null,
    IReadOnlyList<AgentStatusHistoryEntry>? History = null);

public sealed record ErrorResponse(string ErrorCode, string Message, string? TraceId = null);

public sealed record ExportedWindow(DecisionSnapshot Snapshot, IReadOnlyList<LegalAction> Actions);
