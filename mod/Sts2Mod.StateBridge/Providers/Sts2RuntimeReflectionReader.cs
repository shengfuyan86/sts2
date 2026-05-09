using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Sts2Mod.StateBridge.Configuration;
using Sts2Mod.StateBridge.Contracts;
using Sts2Mod.StateBridge.Logging;

namespace Sts2Mod.StateBridge.Providers;

internal sealed class RuntimeStatusReport(bool healthy, string status)
{
    public bool Healthy { get; } = healthy;

    public string Status { get; } = status;
}

internal sealed class RuntimeActionResult(bool accepted, string message, string? errorCode = null, IReadOnlyDictionary<string, object?>? metadata = null)
{
    public bool Accepted { get; } = accepted;

    public string Message { get; } = message;

    public string? ErrorCode { get; } = errorCode;

    public IReadOnlyDictionary<string, object?> Metadata { get; } = metadata ?? new Dictionary<string, object?>();
}

internal sealed record PotionTargetProbe(
    bool RequiresTarget,
    string? TargetType,
    string? Usage,
    string? SelectionPrompt,
    string ProbeSource,
    string? ProbeMessage);

internal sealed record ResolvedPotionAction(
    object Slot,
    object PotionModel,
    RuntimePotionState PotionState,
    object? Holder,
    string HolderResolution);

internal sealed record EnemyMoveNameResolution(
    string? Value,
    string? SuppressedReason = null,
    string? SuppressedCandidate = null,
    string? Source = null);

internal sealed record PlayCardRuntimeSnapshot(
    int? Energy,
    int? PlayerHp,
    int? PlayerBlock,
    int HandCount,
    string HandSignature,
    string EnemySignature,
    string? SelectionScreenType,
    string? SelectionKind,
    string? SelectionPrompt,
    string? OverlayTopType);

internal sealed class Sts2RuntimeReflectionReader
{
    private const string Sts2AssemblyName = "sts2";
    private const string NGameTypeName = "MegaCrit.Sts2.Core.Nodes.NGame";
    private const string NRunTypeName = "MegaCrit.Sts2.Core.Nodes.NRun";
    private const string OverlayStackTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack";
    private const string RewardScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    private const string PhaseMenu = DecisionPhase.Menu;
    private static readonly string[] MenuContinueLabelHints = { "continue", "resume", "继续", "继续游戏", "继续旅程" };
    private static readonly string[] RewardAdvanceLabelHints = { "advance", "continue", "proceed", "next", "forward", "前进", "继续", "下一步" };
    private static readonly string[] EventContinueLabelHints = { "continue", "proceed", "leave", "confirm", "继续", "离开", "确认", "前进", "完成" };
    private static readonly string[] ShopLeaveLabelHints = { "leave", "continue", "proceed", "exit", "back", "离开", "继续", "前进", "返回" };
    private static readonly string[] ShopPurchaseMethodNames = { "OnTryPurchase", "TryPurchase", "Purchase", "Buy", "OnPressed", "Press", "Click", "Activate" };
    private static readonly string[] ShopLeaveMethodNames = { "Proceed", "Leave", "ExitRoom", "ExitShop", "Continue", "OnLeavePressed" };
    private static readonly string[] EventCardSelectionTypeHints = { "DeckEnchant", "EnchantSelect", "Enchant", "EventCardSelect", "EventCardSelection" };
    private static readonly string[] MenuNewRunLabelHints = { "new run", "new game", "start new", "开始新", "新游戏", "新旅程" };
    private static readonly string[] MenuConfirmLabelHints = { "start", "begin", "confirm", "ok", "开始", "确认", "确定" };
    private static readonly string[] MenuDangerLabelHints = { "exit", "quit", "abandon", "delete", "退出", "放弃", "删除" };
    private static readonly string[] MenuCharacterLabelHints =
    {
        // Keep this list conservative; only emit select_character when we are fairly confident.
        "ironclad", "silent", "defect", "watcher",
        "铁甲", "静默", "机器人", "观者",
    };
    private static readonly string[] MenuActivationMethodNames =
    {
        "Click",
        "Press",
        "Activate",
        "OnPressed",
        "OnPress",
        "Confirm",
        "Select",
    };
    private static readonly string[] CardRewardSelectionTypeHints =
    {
        "CardReward",
        "RewardCard",
        "CardSelection",
        "CardSelect",
        "CardGrid",
    };

    private static readonly string[] CardRewardChoiceCollectionMembers =
    {
        "_cardHolders",
        "CardHolders",
        "_holders",
        "Holders",
        "_activeHolders",
        "ActiveHolders",
        "_cards",
        "Cards",
        "_rewardCards",
        "RewardCards",
        "_cardChoices",
        "CardChoices",
        "_choices",
        "Choices",
        "Options",
        "_options",
        "_cardButtons",
        "CardButtons",
        "_buttons",
        "Buttons",
        "_cardNodes",
        "CardNodes",
        "_displayedCards",
        "DisplayedCards",
        "_generatedCards",
        "GeneratedCards",
    };

    private static readonly string[] CardRewardChoiceCardMembers =
    {
        "Card",
        "_card",
        "CardModel",
        "CardNode",
        "Reward",
        "_reward",
        "Value",
        "_value",
        "Data",
        "_data",
    };

    private static readonly string[] CardRewardChoiceSelectMethodNames =
    {
        "SelectCard",
        "SelectHolder",
        "ChooseCard",
        "OnCardClicked",
        "OnCardSelected",
        "OnCardChosen",
        "OnChoiceSelected",
        "CardSelectedFrom",
        "CardChosenFrom",
        "ConfirmSelection",
        "OnHolderPressed",
        "SelectCardInSimpleMode",
        "SelectCardInUpgradeMode",
    };

    private static readonly string[] CardRewardChoiceSkipMethodNames =
    {
        "CancelFree",
        "Skip",
        "OnSkip",
        "OnSkipped",
        "SkipReward",
        "Cancel",
        "OnCancel",
        "Close",
        "Dismiss",
    };
    private static readonly string[] CombatCardSelectionTypeHints =
    {
        "CardSelection",
        "CardSelect",
        "ChooseCard",
        "HandSelect",
        "HandSelection",
        "SelectCard",
        "Exhaust",
        "Discard",
    };
    private static readonly string[] CombatCardChoiceCancelMethodNames =
    {
        "CancelFree",
        "Cancel",
        "CancelSelection",
        "Back",
        "Close",
        "Dismiss",
        "Skip",
        "OnCancel",
        "OnClose",
        "OnSkip",
        "CancelHandSelectionIfNecessary",
    };
    private static readonly string[] ShopCardMemberNames = { "Card", "_card", "CardModel", "CardState", "CardData", "Model", "Data" };
    private static readonly string[] ShopRelicMemberNames = { "Relic", "_relic", "RelicModel", "RelicState", "RelicData", "Model", "Data" };
    private static readonly string[] ShopPotionMemberNames = { "Potion", "_potion", "PotionModel", "PotionState", "PotionData", "Model", "Data" };
    private static readonly string[] ShopPriceMemberNames = { "Cost", "Price", "_cost", "_price", "GoldCost", "PurchasePrice", "Amount" };
    private static readonly string[] ShopInventoryEntryCollections = { "CharacterCardEntries", "ColorlessCardEntries", "RelicEntries", "PotionEntries" };
    private static readonly string[] CombatSelectionPromptMembers =
    {
        "Title",
        "Name",
        "Label",
        "Text",
        "Prompt",
        "Description",
        "PromptText",
        "HelpText",
        "HeaderText",
        "BodyText",
        "_selectionHeader",
        "SelectionHeader",
    };
    private static readonly string[] RewardAdvanceMethodNames =
    {
        "Advance",
        "Continue",
        "Proceed",
        "Next",
        "GoNext",
        "OnContinuePressed",
        "OnContinueButtonPressed",
        "OnProceedButtonPressed",
        "OnProceedPressed",
    };
    private static readonly Regex RichTextTagRegex = new(@"\[(?:/?)[^\]]+\]", RegexOptions.Compiled);
    private static readonly Regex RichTextPairRegex = new(@"\[(?<tag>[A-Za-z0-9_]+)\](?<content>.*?)\[/\k<tag>\]", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<expr>[^}]+))?\}", RegexOptions.Compiled);
    private static readonly Regex EnergyIconPathRegex = new(@"res://images/packed/sprite_fonts/[A-Za-z0-9_]+_energy_icon\.png", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnergyIconRunRegex = new(@"(?:(?:res://images/packed/sprite_fonts/[A-Za-z0-9_]+_energy_icon\.png)\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnergyLabelRunRegex = new(@"(?:(?<count>\d+)能量\s*){2,}", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string> DescriptionSemanticDisplayText =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gold"] = "金币",
            ["energy"] = "能量",
        };
    private static readonly string[] DescriptionSearchNestedMembers =
    {
        "Data",
        "Definition",
        "RuntimeData",
        "RuntimeState",
        "DynamicData",
        "DynamicState",
        "CardData",
        "CardModel",
        "CardNode",
        "Model",
        "Card",
        "CardState",
        "CardDefinition",
        "Stats",
        "State",
        "Effect",
        "Effects",
        "Action",
        "Actions",
        "Preview",
        "PreviewData",
        "CombatData",
        "Computed",
        "Values",
        "DynamicVars",
        "CanonicalVars",
    };
    private static readonly string[] GlossaryHintCollectionMembers =
    {
        "HoverTips",
        "HoverTip",
        "DumbHoverTip",
        "ExtraHoverTips",
    };
    private static readonly string[] GlossaryHintDescriptionMembers =
    {
        "Description",
        "SmartDescription",
        "DynamicDescription",
        "RenderedDescription",
        "RenderedText",
        "DisplayDescription",
        "DescriptionRendered",
        "Text",
    };
    private static readonly string[] GlossaryIdentityMembers =
    {
        "PowerId",
        "KeywordId",
        "GlossaryId",
        "StatusId",
        "TermId",
        "Id",
    };
    private static readonly string[] GlossaryDisplayMembers =
    {
        "Title",
        "Name",
        "DisplayName",
        "Label",
        "Text",
    };
    private static readonly string[] GlossaryLocStringSuffixes =
    {
        "smartDescription",
        "description",
    };
    private static readonly string[] EntityDescriptionLocStringSuffixes =
    {
        "description",
        "smartDescription",
    };
    private static readonly IReadOnlyDictionary<string, string[]> DescriptionVariableMemberAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["damage"] = new[] { "Damage", "CurrentDamage", "DisplayedDamage", "PreviewDamage", "FinalDamage", "ModifiedDamage", "ComputedDamage", "BaseDamage", "AttackDamage", "DamageAmount" },
            ["block"] = new[] { "Block", "CurrentBlock", "DisplayedBlock", "PreviewBlock", "FinalBlock", "ModifiedBlock", "ComputedBlock", "BaseBlock", "BlockAmount" },
            ["draw"] = new[] { "Draw", "DrawAmount", "Cards", "CardsToDraw", "DrawCount", "CardDraw", "CardsDrawn" },
            ["strength"] = new[] { "Strength", "StrengthAmount" },
            ["magic"] = new[] { "MagicNumber", "Magic", "MagicValue", "ModifiedMagicNumber", "CurrentMagicNumber", "Value" },
            ["energy"] = new[] { "Energy", "EnergyGain", "EnergyAmount", "Cost", "CurrentEnergyCost" },
            ["gold"] = new[] { "Gold", "GoldAmount", "GoldCost", "Price", "Cost", "PurchasePrice", "PriceIncrease", "CostIncrease", "PriceDelta" },
            ["vulnerable"] = new[] { "Vulnerable", "VulnerableAmount" },
            ["weak"] = new[] { "Weak", "WeakAmount" },
            ["frail"] = new[] { "Frail", "FrailAmount" },
            ["dexterity"] = new[] { "Dexterity", "DexterityAmount" },
            ["amount"] = new[] { "Amount", "Stacks", "Value", "PriceIncrease", "CostIncrease", "PriceDelta", "CalcPriceIncrease" },
        };
    private static readonly IReadOnlyDictionary<string, GlossarySpec> KnownGlossarySpecs =
        new Dictionary<string, GlossarySpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["block"] = new("block", "格挡", new[] { "格挡", "block" }, "在下个回合前，阻挡伤害。"),
            ["strength"] = new("strength", "力量", new[] { "力量", "strength" }, "使攻击造成更多伤害。"),
            ["vulnerable"] = new("vulnerable", "易伤", new[] { "易伤", "vulnerable" }, "易伤的生物从攻击中受到的伤害增加50%。"),
            ["weak"] = new("weak", "虚弱", new[] { "虚弱", "weak" }, "使攻击造成的伤害降低。"),
            ["frail"] = new("frail", "脆弱", new[] { "脆弱", "frail" }, "使从牌获得的格挡减少。"),
            ["dexterity"] = new("dexterity", "敏捷", new[] { "敏捷", "dexterity" }, "使从牌获得的格挡增加。"),
            ["thorns"] = new("thorns", "荆棘", new[] { "荆棘", "thorns" }, "当受到攻击伤害时，对攻击者造成伤害。"),
            ["damage"] = new("damage", "伤害", new[] { "伤害", "damage" }, "会降低目标生命值。"),
            ["draw"] = new("draw", "抽牌", new[] { "抽", "draw" }, "从抽牌堆抽牌到手牌。"),
            ["energy"] = new("energy", "能量", new[] { "能量", "energy" }, "用于打出卡牌。"),
            ["exhaust"] = new("exhaust", "消耗", new[] { "消耗", "exhaust" }, "在战斗结束前移除。"),
            ["discard"] = new("discard", "弃牌", new[] { "弃", "discard" }, "将牌放入弃牌堆。"),
            ["status"] = new("status", "状态", new[] { "状态", "status" }, "状态牌通常会带来负面影响。"),
            ["debuff"] = new("debuff", "负面效果", new[] { "负面效果", "debuff" }, "会削弱目标。"),
            ["buff"] = new("buff", "增益", new[] { "增益", "buff" }, "会强化目标。"),
            ["upgrade"] = new("upgrade", "升级", new[] { "升级", "upgrade" }, "永久强化卡牌或其他对象的数值与效果。"),
            ["relic"] = new("relic", "遗物", new[] { "遗物", "relic" }, "提供被动效果的永久收藏品。"),
            ["metallicize"] = new("metallicize", "金属化", new[] { "金属化", "metallicize" }, "在回合结束时获得格挡。"),
            ["strike"] = new("strike", "打击", new[] { "打击", "strike" }, "攻击标签，常用于与打击牌相关的协同效果。"),
            ["defend"] = new("defend", "防御", new[] { "防御", "defend" }, "防御标签，常用于与防御牌相关的协同效果。"),
        };
    private readonly BridgeOptions _options;
    private readonly InstallationProbeResult _probe;
    private readonly IBridgeLogger? _logger;

    public Sts2RuntimeReflectionReader(BridgeOptions options, InstallationProbeResult probe, IBridgeLogger? logger = null)
    {
        _options = options;
        _probe = probe;
        _logger = logger;
    }

    public RuntimeStatusReport GetStatusReport()
    {
        var assembly = FindSts2Assembly();
        if (assembly is null)
        {
            return new RuntimeStatusReport(
                healthy: false,
                status: $"sts2 assembly is not loaded in the current process; launch the bridge inside the game. managed_dir={_probe.ManagedDir ?? "missing"}");
        }

        if (!TryGetRuntimeRoot(assembly, out var root, out var status))
        {
            return new RuntimeStatusReport(healthy: true, status: status);
        }

        var phase = root.RunNode is not null && root.RunState is not null
            ? DetectPhase(root.RunNode, root.RunState)
            : PhaseMenu;
        return new RuntimeStatusReport(
            healthy: true,
            status: $"live runtime attached; phase={phase}; game_version={_probe.GameVersion ?? _options.GameVersion}");
    }

    public bool IsAssemblyLoaded()
    {
        return FindSts2Assembly() is not null;
    }

    public RuntimeWindowContext CaptureWindow()
    {
        var assembly = FindSts2Assembly()
            ?? throw new InvalidOperationException("sts2 assembly is not loaded in the current process. Start the bridge from inside the game runtime.");

        if (!TryGetRuntimeRoot(assembly, out var root, out var status))
        {
            throw new InvalidOperationException(status);
        }

        if (root.RunNode is null || root.RunState is null)
        {
            return BuildMenuWindow(root.GameInstance);
        }

        var phase = DetectPhase(root.RunNode, root.RunState);
        return phase switch
        {
            DecisionPhase.Reward => BuildRewardWindow(root.RunNode, root.RunState),
            DecisionPhase.Map => BuildMapWindow(root.RunNode, root.RunState),
            DecisionPhase.Event => BuildEventWindow(root.RunNode, root.RunState),
            DecisionPhase.Shop => BuildShopWindow(root.RunNode, root.RunState),
            DecisionPhase.Terminal => BuildTerminalWindow(root.RunNode, root.RunState),
            _ => BuildCombatWindow(root.RunNode, root.RunState),
        };
    }

    public RuntimeActionResult ExecuteAction(ActionRequest request, LegalAction action)
    {
        var assembly = FindSts2Assembly();
        if (assembly is null)
        {
            return new RuntimeActionResult(false, "sts2 assembly is not loaded in the current process.", "runtime_not_ready");
        }

        if (!TryGetRuntimeRoot(assembly, out var root, out var status))
        {
            return new RuntimeActionResult(false, status, "runtime_not_ready");
        }

        if (root.RunNode is null || root.RunState is null)
        {
            return action.Type switch
            {
                "continue_run" => ExecuteMenuAction(root.GameInstance, request, action, "continue_run"),
                "start_new_run" => ExecuteMenuAction(root.GameInstance, request, action, "start_new_run"),
                "select_character" => ExecuteMenuAction(root.GameInstance, request, action, "select_character"),
                "confirm_start_run" => ExecuteMenuAction(root.GameInstance, request, action, "confirm_start_run"),
                _ => new RuntimeActionResult(false, "No active run is available yet.", "runtime_not_ready"),
            };
        }

        return action.Type switch
        {
            "play_card" => ExecutePlayCard(root.RunNode, root.RunState, request, action),
            "use_potion" => ExecuteUsePotion(root.RunNode, root.RunState, request, action),
            "end_turn" => ExecuteEndTurn(root.RunState, request),
            "choose_combat_card" => ExecuteChooseCombatCard(root.RunNode, root.RunState, request, action),
            "cancel_combat_selection" => ExecuteCancelCombatSelection(root.RunNode, root.RunState, request, action),
            "choose_reward" => ExecuteChooseReward(root.RunNode, request, action),
            "skip_reward" => ExecuteSkipReward(root.RunNode, request),
            "advance_reward" => ExecuteAdvanceReward(root.RunNode, request, action),
            "choose_map_node" => ExecuteChooseMapNode(request, action),
            "choose_event_option" => ExecuteChooseEventOption(root.RunNode, root.RunState, request, action),
            "continue_event" => ExecuteContinueEvent(root.RunNode, root.RunState, request, action),
            "buy_shop_card" => ExecuteBuyShopOffer(root.RunNode, root.RunState, request, action, "card"),
            "buy_shop_relic" => ExecuteBuyShopOffer(root.RunNode, root.RunState, request, action, "relic"),
            "buy_shop_potion" => ExecuteBuyShopOffer(root.RunNode, root.RunState, request, action, "potion"),
            "purge_shop_card" => ExecuteBuyShopOffer(root.RunNode, root.RunState, request, action, "service"),
            "leave_shop" => ExecuteLeaveShop(root.RunNode, root.RunState, request, action),
            _ => new RuntimeActionResult(false, $"Action type '{action.Type}' is not supported yet.", "unsupported_action"),
        };
    }

    private RuntimeWindowContext BuildMenuWindow(object gameInstance)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var metadata = CreateMenuMetadata(gameInstance);
        var buttons = DiscoverMenuButtons(gameInstance, textDiagnostics, metadata);
        LogTextDiagnostics("menu", textDiagnostics);

        var actions = new List<RuntimeActionDefinition>();
        foreach (var candidate in buttons)
        {
            var parameters = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(candidate.Label))
            {
                parameters["button_label"] = candidate.Label;
            }

            if (string.Equals(candidate.Kind, "select_character", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(candidate.CharacterId))
            {
                parameters["character_id"] = candidate.CharacterId;
                parameters["character_label"] = candidate.Label;
            }

            actions.Add(new RuntimeActionDefinition(
                candidate.Kind,
                candidate.Label,
                parameters,
                Metadata: candidate.Diagnostics));
        }

        if (actions.Count == 0)
        {
            metadata["menu_action_suppressed"] = true;
            metadata["menu_action_suppressed_reason"] = metadata.TryGetValue("menu_action_suppressed_reason", out var reason)
                ? reason
                : "no_safe_menu_actions_detected";
        }

        return new RuntimeWindowContext(
            DecisionPhase.Menu,
            Player: null,
            Enemies: Array.Empty<RuntimeEnemyState>(),
            Rewards: Array.Empty<string>(),
            MapNodes: Array.Empty<string>(),
            Terminal: false,
            Metadata: metadata,
            Actions: actions);
    }

    private sealed record MenuActionCandidate(
        string Kind,
        string Label,
        string? CharacterId,
        IReadOnlyDictionary<string, object?> Diagnostics);

    private List<MenuActionCandidate> DiscoverMenuButtons(object gameInstance, TextDiagnosticsCollector textDiagnostics, Dictionary<string, object?> metadata)
    {
        var roots = EnumerateMenuRoots(gameInstance).ToList();
        metadata["menu_root_count"] = roots.Count;

        var discovered = new List<(object Node, string Label, string Source)>();
        var seenNodes = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var suppressedNonInteractable = 0;
        var suppressedUnlabeled = 0;
        foreach (var root in roots)
        {
            var rootNode = root.Node;
            if (rootNode is null)
            {
                continue;
            }

            var rootType = GetTypeName(rootNode) ?? string.Empty;
            foreach (var node in EnumerateNodeDescendants(rootNode, maxDepth: 7).Prepend(rootNode))
            {
                if (!seenNodes.Add(node))
                {
                    continue;
                }

                if (!IsPotentialMenuButton(node))
                {
                    continue;
                }

                if (!IsMenuNodeInteractable(node))
                {
                    suppressedNonInteractable += 1;
                    continue;
                }

                var label = GetMenuNodeLabel(node, textDiagnostics);
                if (string.IsNullOrWhiteSpace(label))
                {
                    suppressedUnlabeled += 1;
                    continue;
                }

                if (IsDangerLabel(label))
                {
                    continue;
                }

                discovered.Add((node, label!, root.Source));
                if (discovered.Count >= 128)
                {
                    break;
                }
            }

            if (discovered.Count >= 128)
            {
                metadata["menu_scan_truncated"] = true;
                metadata["menu_scan_truncated_root_type"] = rootType;
                break;
            }
        }

        metadata["menu_button_candidate_count"] = discovered.Count;
        metadata["menu_suppressed_non_interactable_count"] = suppressedNonInteractable;
        metadata["menu_suppressed_unlabeled_count"] = suppressedUnlabeled;

        // Classify actions conservatively.
        var actions = new List<MenuActionCandidate>();
        var windowKind = "main_menu";

        var continueButton = discovered.FirstOrDefault(item => IsContinueLabel(item.Label));
        if (continueButton.Node is not null)
        {
            actions.Add(new MenuActionCandidate(
                Kind: "continue_run",
                Label: continueButton.Label,
                CharacterId: null,
                Diagnostics: new Dictionary<string, object?>
                {
                    ["menu_detection_source"] = continueButton.Source,
                    ["menu_target_type"] = GetTypeName(continueButton.Node),
                }));
        }

        var newRunButton = discovered.FirstOrDefault(item => IsNewRunLabel(item.Label));
        if (newRunButton.Node is not null)
        {
            actions.Add(new MenuActionCandidate(
                Kind: "start_new_run",
                Label: newRunButton.Label,
                CharacterId: null,
                Diagnostics: new Dictionary<string, object?>
                {
                    ["menu_detection_source"] = newRunButton.Source,
                    ["menu_target_type"] = GetTypeName(newRunButton.Node),
                }));
        }

        // Only attempt character/confirm actions when the UI looks like a new run setup flow.
        var characterButtons = discovered
            .Where(item => IsCharacterLabel(item.Label) || IsCharacterNode(item.Node))
            .Select(item => new
            {
                item.Node,
                item.Label,
                item.Source,
                CharacterId = NormalizeCharacterId(item.Node, item.Label),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.CharacterId))
            .Take(8)
            .ToList();

        if (characterButtons.Count > 0)
        {
            windowKind = "new_run_setup";
            foreach (var button in characterButtons)
            {
                actions.Add(new MenuActionCandidate(
                    Kind: "select_character",
                    Label: button.Label,
                    CharacterId: button.CharacterId,
                    Diagnostics: new Dictionary<string, object?>
                    {
                        ["menu_detection_source"] = button.Source,
                        ["menu_target_type"] = GetTypeName(button.Node),
                    }));
            }

            var confirm = discovered.FirstOrDefault(item => IsConfirmLabel(item.Label));
            if (confirm.Node is not null)
            {
                actions.Add(new MenuActionCandidate(
                    Kind: "confirm_start_run",
                    Label: confirm.Label,
                    CharacterId: null,
                    Diagnostics: new Dictionary<string, object?>
                    {
                        ["menu_detection_source"] = confirm.Source,
                        ["menu_target_type"] = GetTypeName(confirm.Node),
                    }));
            }
        }

        metadata["window_kind"] = windowKind;

        // If we found a lot of buttons but none classified as safe actions, explain why.
        if (discovered.Count > 0 && actions.Count == 0)
        {
            metadata["menu_action_suppressed_reason"] = "candidates_found_but_no_safe_classification";
        }

        return actions;
    }

    private IEnumerable<(object? Node, string Source)> EnumerateMenuRoots(object gameInstance)
    {
        // Prefer overlay stack (works even when runNode is not available).
        var overlayStack = GetMemberValue(GetOverlayStackType(), "Instance");
        var overlayTop = TryInvokeParameterlessMethod(overlayStack, "Peek");
        if (overlayTop is not null)
        {
            yield return (overlayTop, "overlay_stack.peek");
        }

        // Commonly useful roots on NGame.Instance.
        foreach (var member in new[] { "GlobalUi", "UI", "Ui", "MainMenu", "_mainMenu", "Menu", "Menus", "Screen", "Screens", "Root" })
        {
            var candidate = GetMemberValue(gameInstance, member);
            if (candidate is not null)
            {
                yield return (candidate, $"game_instance.{member}");
            }
        }

        yield return (gameInstance, "game_instance");
    }

    private static bool IsPotentialMenuButton(object node)
    {
        var typeName = GetTypeName(node) ?? string.Empty;
        if (typeName.Contains("Button", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var type = node.GetType();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var hasActivationMethod = methods.Any(method => MenuActivationMethodNames.Contains(method.Name, StringComparer.Ordinal));
        if (!hasActivationMethod)
        {
            return false;
        }

        return type.GetProperty("Text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null ||
               type.GetProperty("Label", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null ||
               type.GetProperty("Title", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null ||
               type.GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;
    }

    private static bool IsMenuNodeInteractable(object node)
    {
        if (GetMemberValue(node, "Visible") is bool visible && !visible)
        {
            return false;
        }

        if (TryInvokeParameterlessMethod(node, "IsVisibleInTree") is bool visibleInTree && !visibleInTree)
        {
            return false;
        }

        if (GetMemberValue(node, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            return false;
        }

        if (GetMemberValue(node, "_isEnabled") is bool privateEnabled && !privateEnabled)
        {
            return false;
        }

        if (GetMemberValue(node, "Disabled") is bool disabled && disabled)
        {
            return false;
        }

        if (GetMemberValue(node, "IsLocked") is bool isLocked && isLocked)
        {
            return false;
        }

        if (GetMemberValue(node, "_isLocked") is bool privateLocked && privateLocked)
        {
            return false;
        }

        return true;
    }

    private static string? GetMenuNodeLabel(object node, TextDiagnosticsCollector? textDiagnostics = null)
    {
        var label = ConvertToText(
            GetMemberValue(node, "Text") ?? GetMemberValue(node, "Label") ?? GetMemberValue(node, "Title"),
            "menu.button.label",
            textDiagnostics,
            "Text",
            "Label",
            "Title");
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        if (!CanUseMenuNodeNameFallback(node))
        {
            return null;
        }

        label = ConvertToText(
            GetMemberValue(node, "Name") ?? node,
            "menu.button.name",
            textDiagnostics,
            "Name");
        return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    private static bool CanUseMenuNodeNameFallback(object node)
    {
        var typeName = GetTypeName(node) ?? string.Empty;
        return typeName.Contains("Button", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("CharacterSelect", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Confirm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDangerLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return MenuDangerLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsContinueLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return MenuContinueLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase)) &&
               !IsNewRunLabel(label);
    }

    private static bool IsNewRunLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        if (MenuNewRunLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // "Start" is ambiguous; only treat it as new-run when paired with "new"/"run"/"game"/"新".
        if (normalized.Contains("start", StringComparison.OrdinalIgnoreCase) || normalized.Contains("开始", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Contains("new", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("run", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("game", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("新", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsConfirmLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return MenuConfirmLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase)) &&
               !IsDangerLabel(label);
    }

    private static bool IsCharacterLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return MenuCharacterLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCharacterNode(object node)
    {
        var typeName = GetTypeName(node) ?? string.Empty;
        return typeName.Contains("Character", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Hero", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("Class", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLabel(string label)
    {
        return label.Trim().ToLowerInvariant();
    }

    private static string? NormalizeCharacterId(object node, string label)
    {
        var candidate = ConvertToText(GetMemberValue(node, "CharacterId") ?? GetMemberValue(node, "Id") ?? GetMemberValue(node, "Character") ?? label);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var normalized = candidate.Trim().ToLowerInvariant();
        // Keep it stable and readable for action params.
        normalized = new string(normalized.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private RuntimeActionResult ExecuteMenuAction(object gameInstance, ActionRequest request, LegalAction action, string expectedKind)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var metadata = new Dictionary<string, object?>();
        var candidates = DiscoverMenuButtons(gameInstance, textDiagnostics, metadata);
        var label = ConvertToText(GetDictionaryValue(action.Params, "button_label"));
        var characterId = ConvertToText(GetDictionaryValue(action.Params, "character_id"));

        MenuActionCandidate? target = null;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Kind, expectedKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(characterId))
            {
                if (!string.Equals(candidate.CharacterId, characterId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            else if (!string.IsNullOrWhiteSpace(label))
            {
                if (!string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            target = candidate;
            break;
        }

        if (target is null)
        {
            return new RuntimeActionResult(false, "Menu target is no longer available.", "stale_action", metadata: new Dictionary<string, object?>
            {
                ["action_type"] = expectedKind,
                ["menu_label"] = label,
                ["menu_character_id"] = characterId,
                ["menu_candidate_count"] = candidates.Count,
                ["menu_diagnostics"] = metadata,
            });
        }

        // Re-scan roots and find a matching node to click based on label/id.
        if (!TryResolveMenuTargetNode(gameInstance, expectedKind, label, characterId, out var node, out var resolveMetadata))
        {
            return new RuntimeActionResult(false, "Menu target node could not be resolved.", "runtime_incompatible", resolveMetadata);
        }

        if (!TryExecuteMenuActionHighLevel(gameInstance, expectedKind, node, out var handler) &&
            !TryActivateMenuNode(node, out handler))
        {
            return new RuntimeActionResult(false, "Menu target node is not clickable.", "not_clickable", new Dictionary<string, object?>
            {
                ["runtime_handler"] = "menu_node.activate",
                ["target_type"] = GetTypeName(node),
                ["expected_kind"] = expectedKind,
            });
        }

        return new RuntimeActionResult(true, $"Executed menu action '{expectedKind}'.", metadata: new Dictionary<string, object?>
        {
            ["action_type"] = expectedKind,
            ["runtime_handler"] = handler,
            ["target_type"] = GetTypeName(node),
            ["resolve_metadata"] = resolveMetadata,
        });
    }

    private bool TryResolveMenuTargetNode(object gameInstance, string expectedKind, string? label, string? characterId, out object node, out IReadOnlyDictionary<string, object?> metadata)
    {
        foreach (var root in EnumerateMenuRoots(gameInstance))
        {
            if (root.Node is null)
            {
                continue;
            }

            foreach (var child in EnumerateNodeDescendants(root.Node, maxDepth: 7).Prepend(root.Node))
            {
                if (!IsPotentialMenuButton(child))
                {
                    continue;
                }

                if (!IsMenuNodeInteractable(child))
                {
                    continue;
                }

                var childLabel = GetMenuNodeLabel(child);
                if (string.IsNullOrWhiteSpace(childLabel))
                {
                    continue;
                }

                childLabel = childLabel.Trim();

                if (!string.IsNullOrWhiteSpace(characterId) && string.Equals(expectedKind, "select_character", StringComparison.Ordinal))
                {
                    var id = NormalizeCharacterId(child, childLabel);
                    if (!string.IsNullOrWhiteSpace(id) && string.Equals(id, characterId, StringComparison.OrdinalIgnoreCase))
                    {
                        node = child;
                        metadata = new Dictionary<string, object?>
                        {
                            ["menu_detection_source"] = root.Source,
                            ["menu_target_type"] = GetTypeName(child),
                            ["menu_character_id"] = id,
                        };
                        return true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(label) && string.Equals(childLabel, label, StringComparison.OrdinalIgnoreCase))
                {
                    node = child;
                    metadata = new Dictionary<string, object?>
                    {
                        ["menu_detection_source"] = root.Source,
                        ["menu_target_type"] = GetTypeName(child),
                        ["menu_label"] = childLabel,
                    };
                    return true;
                }
            }
        }

        node = default!;
        metadata = new Dictionary<string, object?>
        {
            ["expected_kind"] = expectedKind,
            ["menu_label"] = label,
            ["menu_character_id"] = characterId,
        };
        return false;
    }

    private bool TryExecuteMenuActionHighLevel(object gameInstance, string expectedKind, object node, out string handler)
    {
        return expectedKind switch
        {
            "continue_run" => TryExecuteContinueRunAction(gameInstance, node, out handler),
            "start_new_run" => TryExecuteStartNewRunAction(gameInstance, node, out handler),
            "select_character" => TryExecuteSelectCharacterAction(gameInstance, node, out handler),
            "confirm_start_run" => TryExecuteConfirmStartRunAction(gameInstance, node, out handler),
            _ => TryReturnUnhandled(out handler),
        };
    }

    private bool TryExecuteContinueRunAction(object gameInstance, object node, out string handler)
    {
        var mainMenu = GetMemberValue(node, "_mainMenu")
            ?? GetMemberValue(node, "MainMenu")
            ?? FindMainMenuOwner(gameInstance, node);
        if (mainMenu is not null)
        {
            if (TryInvokeFirstCompatibleMethod(mainMenu, new[] { "OnContinueButtonPressed" }, new[] { new object?[] { node } }, out var methodName))
            {
                handler = $"main_menu.{methodName}";
                return true;
            }

            if (TryInvokeFirstCompatibleMethod(mainMenu, new[] { "OnContinueButtonPressedAsync" }, new[] { Array.Empty<object?>() }, out methodName))
            {
                handler = $"main_menu.{methodName}";
                return true;
            }
        }

        handler = string.Empty;
        return false;
    }

    private bool TryExecuteStartNewRunAction(object gameInstance, object node, out string handler)
    {
        var submenu = FindSingleplayerSubmenu(gameInstance, node);
        if (submenu is not null &&
            TryInvokeFirstCompatibleMethod(submenu, new[] { "OpenCharacterSelect" }, new[] { new object?[] { node } }, out var submenuMethod))
        {
            handler = $"singleplayer_submenu.{submenuMethod}";
            return true;
        }

        var mainMenu = GetMemberValue(node, "_mainMenu")
            ?? GetMemberValue(node, "MainMenu")
            ?? FindMainMenuOwner(gameInstance, node);
        if (mainMenu is not null)
        {
            if (TryInvokeFirstCompatibleMethod(mainMenu, new[] { "OpenSingleplayerSubmenu" }, new[] { Array.Empty<object?>() }, out var methodName))
            {
                handler = $"main_menu.{methodName}";
                return true;
            }

            if (TryInvokeFirstCompatibleMethod(mainMenu, new[] { "SingleplayerButtonPressed" }, new[] { new object?[] { node } }, out methodName))
            {
                handler = $"main_menu.{methodName}";
                return true;
            }
        }

        handler = string.Empty;
        return false;
    }

    private bool TryExecuteSelectCharacterAction(object gameInstance, object node, out string handler)
    {
        var character = GetMemberValue(node, "Character") ?? GetMemberValue(node, "_character");
        var screen = FindCharacterSelectScreen(gameInstance, node);
        if (screen is not null && character is not null &&
            TryInvokeFirstCompatibleMethod(screen, new[] { "SelectCharacter" }, new[] { new object?[] { node, character } }, out var screenMethod))
        {
            handler = $"character_select_screen.{screenMethod}";
            return true;
        }

        var buttonDelegate = GetMemberValue(node, "_delegate") ?? GetMemberValue(node, "Delegate");
        if (buttonDelegate is not null && character is not null &&
            TryInvokeFirstCompatibleMethod(buttonDelegate, new[] { "SelectCharacter" }, new[] { new object?[] { node, character } }, out var delegateMethod))
        {
            handler = $"character_select_delegate.{delegateMethod}";
            return true;
        }

        if (TryInvokeFirstCompatibleMethod(node, new[] { "Select" }, new[] { Array.Empty<object?>() }, out var methodName))
        {
            handler = $"menu_node.{methodName}";
            return true;
        }

        handler = string.Empty;
        return false;
    }

    private bool TryExecuteConfirmStartRunAction(object gameInstance, object node, out string handler)
    {
        var screen = FindCharacterSelectScreen(gameInstance, node);
        if (screen is not null &&
            TryInvokeFirstCompatibleMethod(screen, new[] { "OnEmbarkPressed" }, new[] { new object?[] { node } }, out var methodName))
        {
            handler = $"character_select_screen.{methodName}";
            return true;
        }

        handler = string.Empty;
        return false;
    }

    private object? FindMainMenuOwner(object gameInstance, object node)
    {
        foreach (var root in EnumerateMenuRoots(gameInstance))
        {
            if (root.Node is null)
            {
                continue;
            }

            foreach (var candidate in EnumerateNodeDescendants(root.Node, maxDepth: 6).Prepend(root.Node))
            {
                var typeName = GetTypeName(candidate) ?? string.Empty;
                if (!typeName.Contains("NMainMenu", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReferenceEquals(GetMemberValue(candidate, "_continueButton"), node) ||
                    ContainsNodeReference(candidate, node, maxDepth: 4))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private object? FindSingleplayerSubmenu(object gameInstance, object node)
    {
        foreach (var root in EnumerateMenuRoots(gameInstance))
        {
            if (root.Node is null)
            {
                continue;
            }

            foreach (var candidate in EnumerateNodeDescendants(root.Node, maxDepth: 6).Prepend(root.Node))
            {
                var typeName = GetTypeName(candidate) ?? string.Empty;
                if (!typeName.Contains("NSingleplayerSubmenu", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReferenceEquals(GetMemberValue(candidate, "_standardButton"), node) ||
                    ContainsNodeReference(candidate, node, maxDepth: 4))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private object? FindCharacterSelectScreen(object gameInstance, object node)
    {
        var buttonDelegate = GetMemberValue(node, "_delegate") ?? GetMemberValue(node, "Delegate");
        if (buttonDelegate is not null)
        {
            var delegateTypeName = GetTypeName(buttonDelegate) ?? string.Empty;
            if (delegateTypeName.Contains("NCharacterSelectScreen", StringComparison.Ordinal))
            {
                return buttonDelegate;
            }
        }

        foreach (var root in EnumerateMenuRoots(gameInstance))
        {
            if (root.Node is null)
            {
                continue;
            }

            foreach (var candidate in EnumerateNodeDescendants(root.Node, maxDepth: 6).Prepend(root.Node))
            {
                var typeName = GetTypeName(candidate) ?? string.Empty;
                if (!typeName.Contains("NCharacterSelectScreen", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReferenceEquals(GetMemberValue(candidate, "_embarkButton"), node) ||
                    ReferenceEquals(GetMemberValue(candidate, "_selectedButton"), node) ||
                    ContainsNodeReference(candidate, node, maxDepth: 4))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private bool ContainsNodeReference(object root, object target, int maxDepth)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return EnumerateNodeDescendants(root, maxDepth)
            .Any(child => ReferenceEquals(child, target));
    }

    private static bool TryReturnUnhandled(out string handler)
    {
        handler = string.Empty;
        return false;
    }

    private static bool TryActivateMenuNode(object node, out string handler)
    {
        var argSets = new List<object?[]>
        {
            Array.Empty<object?>(),
            new object?[] { false },
            new object?[] { true },
            new object?[] { "pressed" },
        };

        if (TryInvokeFirstCompatibleMethod(node, MenuActivationMethodNames, argSets, out var methodName))
        {
            handler = $"menu_node.{methodName}";
            return true;
        }

        var emitSignalCandidates = node.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, "EmitSignal", StringComparison.Ordinal))
            .ToArray();
        foreach (var emitSignal in emitSignalCandidates)
        {
            var parameters = emitSignal.GetParameters();
            if (parameters.Length == 0)
            {
                continue;
            }

            // Prefer EmitSignal(StringName signal, Variant[] args)
            if (parameters.Length == 2 && parameters[1].ParameterType.IsArray)
            try
            {
                var signalArg = CreateSignalArgument(parameters[0].ParameterType, "pressed");
                if (signalArg is null)
                {
                    continue;
                }

                var elementType = parameters[1].ParameterType.GetElementType();
                if (elementType is null)
                {
                    continue;
                }

                var argsArray = Array.CreateInstance(elementType, 0);
                emitSignal.Invoke(node, new[] { signalArg, argsArray });
                handler = $"menu_node.{emitSignal.Name}(pressed)";
                return true;
            }
            catch
            {
                // ignore
            }

            // Fallback: EmitSignal(string signal) style overloads.
            if (parameters.Length == 1)
            {
                try
                {
                    var signalArg = CreateSignalArgument(parameters[0].ParameterType, "pressed");
                    if (signalArg is null)
                    {
                        continue;
                    }

                    emitSignal.Invoke(node, new[] { signalArg });
                    handler = $"menu_node.{emitSignal.Name}(pressed)";
                    return true;
                }
                catch
                {
                    // ignore
                }
            }
        }

        handler = "menu_node.unhandled";
        return false;
    }

    private static object? CreateSignalArgument(Type parameterType, string signal)
    {
        if (parameterType == typeof(string))
        {
            return signal;
        }

        // Godot's C# bindings usually use Godot.StringName for signal identifiers.
        if (string.Equals(parameterType.Name, "StringName", StringComparison.Ordinal) ||
            string.Equals(parameterType.FullName, "Godot.StringName", StringComparison.Ordinal))
        {
            try
            {
                return Activator.CreateInstance(parameterType, signal);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private Dictionary<string, object?> CreateMenuMetadata(object gameInstance)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["source"] = "sts2_runtime",
            ["phase_detected"] = DecisionPhase.Menu,
            ["game_version"] = _probe.GameVersion ?? _options.GameVersion,
            ["managed_dir"] = _probe.ManagedDir,
            ["game_instance_type"] = GetTypeName(gameInstance),
        };

        var overlayStack = GetMemberValue(GetOverlayStackType(), "Instance");
        var overlayTop = TryInvokeParameterlessMethod(overlayStack, "Peek");
        if (overlayTop is not null)
        {
            metadata["overlay_top_type"] = GetTypeName(overlayTop);
        }

        return metadata;
    }

    private RuntimeWindowContext BuildCombatWindow(object runNode, object runState)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var playerBuild = BuildPlayerState(runState, textDiagnostics);
        var player = playerBuild.Player;
        var enemyBuild = BuildEnemies(runState, textDiagnostics);
        var enemies = enemyBuild.Enemies;
        var metadata = CreateBaseMetadata(runNode, runState, DecisionPhase.Combat);
        foreach (var pair in playerBuild.Diagnostics)
        {
            metadata[pair.Key] = pair.Value;
        }
        metadata["enemy_export"] = new Dictionary<string, object?>
        {
            ["enemy_count"] = enemies.Count,
            ["degraded"] = enemyBuild.Diagnostics.Count > 0,
            ["entry_count"] = enemyBuild.Diagnostics.Count,
            ["entries"] = enemyBuild.Diagnostics.Take(12).ToArray(),
        };
        var runStateSnapshot = BuildRunState(runState, textDiagnostics);
        var rewardAnalysis = AnalyzeRewardPhase(runNode, runState);
        metadata["phase_detection"] = rewardAnalysis.ToMetadata();
        var actions = new List<RuntimeActionDefinition>();
        var isPlayerTurn = IsPlayerTurn(runState);
        metadata["window_kind"] = isPlayerTurn ? "player_turn" : "enemy_turn";
        var liveEnemyIds = enemies.Where(enemy => enemy.IsAlive).Select(enemy => enemy.EnemyId).ToArray();
        if (liveEnemyIds.Length == 0)
        {
            metadata["window_kind"] = "combat_transition";
            metadata["reward_pending"] = true;
            metadata["transition_kind"] = "combat_reward_transition";
            LogTextDiagnostics("combat_transition", textDiagnostics);
            return new RuntimeWindowContext(
                DecisionPhase.Combat,
                player,
                enemies,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Terminal: false,
                Metadata: metadata,
                Actions: Array.Empty<RuntimeActionDefinition>(),
                RunState: runStateSnapshot);
        }

        var combatSelection = TryBuildCombatSelectionContext(runNode, runState, textDiagnostics);
        if (combatSelection is not null)
        {
            metadata["window_kind"] = "combat_card_selection";
            metadata["selection_kind"] = combatSelection.Value.SelectionKind;
            metadata["selection_prompt"] = combatSelection.Value.SelectionPrompt;
            metadata["selection_source"] = combatSelection.Value.DetectionSource;
            metadata["selection_screen_type"] = combatSelection.Value.SelectionScreenType;
            metadata["selection_prompt_source"] = combatSelection.Value.PromptSource;
            metadata["selection_choice_count"] = combatSelection.Value.Actions.Count(action => string.Equals(action.Type, "choose_combat_card", StringComparison.Ordinal));
            metadata["selection_cancel_available"] = combatSelection.Value.CancelAvailable;
            if (!combatSelection.Value.CancelAvailable)
            {
                metadata["selection_cancel_reason"] = combatSelection.Value.CancelReason;
            }

            LogTextDiagnostics("combat_card_selection", textDiagnostics);
            return new RuntimeWindowContext(
                DecisionPhase.Combat,
                player,
                enemies,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Terminal: false,
                Metadata: metadata,
                Actions: combatSelection.Value.Actions,
                RunState: runStateSnapshot);
        }

        if (!isPlayerTurn)
        {
            metadata["actions_suppressed"] = true;
            metadata["actions_suppressed_reason"] = "non_player_turn";
            LogTextDiagnostics("combat_enemy_turn", textDiagnostics);
            return new RuntimeWindowContext(
                DecisionPhase.Combat,
                player,
                enemies,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Terminal: false,
                Metadata: metadata,
                Actions: Array.Empty<RuntimeActionDefinition>(),
                RunState: runStateSnapshot);
        }

        foreach (var card in GetHandCardDescriptors(runState, textDiagnostics).Where(card => card.Playable))
        {
            var parameters = new Dictionary<string, object?>
            {
                ["card_id"] = card.CardId,
                ["card_name"] = card.Name,
            };
            if (!string.IsNullOrWhiteSpace(card.TargetType))
            {
                parameters["target_type"] = card.TargetType;
            }

            var actionMetadata = new Dictionary<string, object?> { ["playable"] = true };
            actionMetadata["card_preview"] = BuildCardPreview(card);
            if (!string.Equals(card.NameResolution.Status, "resolved", StringComparison.Ordinal))
            {
                foreach (var pair in RuntimeTextResolver.CreateActionDiagnostics($"actions.play_card[{card.CardId}].label", card.NameResolution))
                {
                    actionMetadata[pair.Key] = pair.Value;
                }
            }

            actions.Add(new RuntimeActionDefinition(
                "play_card",
                $"Play {card.Name}",
                parameters,
                BuildTargetConstraints(card.TargetType, liveEnemyIds),
                actionMetadata));
        }

        var livePlayer = GetPlayers(runState).FirstOrDefault();
        var livePotionSlots = EnumerateObjects(ResolvePotionCollection(livePlayer)).ToList();
        if (player is not null)
        {
            for (var potionIndex = 0; potionIndex < player.Potions.Count; potionIndex += 1)
            {
                var potion = player.Potions[potionIndex];
                if (string.IsNullOrWhiteSpace(potion.Name))
                {
                    continue;
                }

                var potionPreview = BuildPotionPreview(potion);
                var actionMetadata = new Dictionary<string, object?>();
                if (potionPreview.Count > 0)
                {
                    actionMetadata["potion_preview"] = potionPreview;
                }
                if (potionIndex >= 0 && potionIndex < livePotionSlots.Count)
                {
                    var potionSource = ResolvePotionActionModel(livePotionSlots[potionIndex]);
                    if (potionSource is not null)
                    {
                        var probe = ProbePotionTargetRequirement(potionSource);
                        if (!string.IsNullOrWhiteSpace(probe.TargetType))
                        {
                            actionMetadata["target_type"] = probe.TargetType;
                        }
                        if (!string.IsNullOrWhiteSpace(probe.Usage))
                        {
                            actionMetadata["potion_usage"] = probe.Usage;
                        }
                        if (!string.IsNullOrWhiteSpace(probe.SelectionPrompt))
                        {
                            actionMetadata["selection_prompt"] = probe.SelectionPrompt;
                        }

                        actionMetadata["requires_target"] =
                            probe.RequiresTarget ||
                            (!string.IsNullOrWhiteSpace(probe.TargetType) && BuildTargetConstraints(probe.TargetType, liveEnemyIds).Count > 0);
                    }
                }

                var potionTargetType = ConvertToText(actionMetadata.TryGetValue("target_type", out var targetTypeValue) ? targetTypeValue : null);
                actions.Add(new RuntimeActionDefinition(
                    "use_potion",
                    $"Use {potion.Name}",
                    new Dictionary<string, object?>
                    {
                        ["potion"] = potion.Name,
                        ["potion_index"] = potionIndex,
                        ["canonical_potion_id"] = potion.CanonicalPotionId,
                    },
                    BuildTargetConstraints(potionTargetType, liveEnemyIds),
                    Metadata: actionMetadata));
            }
        }

        actions.Add(new RuntimeActionDefinition("end_turn", "End Turn", new Dictionary<string, object?>()));
        LogTextDiagnostics("combat", textDiagnostics);
        return new RuntimeWindowContext(
            DecisionPhase.Combat,
            player,
            enemies,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Terminal: false,
            Metadata: metadata,
            Actions: actions,
            RunState: runStateSnapshot);
    }

    private RuntimeWindowContext BuildRewardWindow(object runNode, object runState)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var rewardAnalysis = AnalyzeRewardPhase(runNode, runState);
        var rewards = ExtractRewards(runNode, textDiagnostics);
        var playerBuild = BuildPlayerState(runState, textDiagnostics);
        var player = playerBuild.Player;
        var metadata = CreateBaseMetadata(runNode, runState, DecisionPhase.Reward);
        foreach (var pair in playerBuild.Diagnostics)
        {
            metadata[pair.Key] = pair.Value;
        }
        var runStateSnapshot = BuildRunState(runState, textDiagnostics);
        metadata["phase_detection"] = rewardAnalysis.ToMetadata();
        metadata["reward_subphase"] = rewardAnalysis.RewardSubphase;
        metadata["detection_source"] = rewardAnalysis.DetectionSource;
        metadata["window_kind"] = rewardAnalysis.RewardSubphase switch
        {
            "card_reward_selection" => "reward_card_selection",
            "reward_advance" => "reward_advance",
            "reward_transition" => "reward_transition",
            _ => "reward_choice",
        };
        metadata["reward_count"] = rewards.Count;
        metadata["reward_screen_complete"] = rewardAnalysis.RewardScreenComplete;
        var actions = rewards
            .Select((reward, index) =>
            {
                var actionMetadata = new Dictionary<string, object?>();
                if (!string.Equals(reward.Resolution.Status, "resolved", StringComparison.Ordinal))
                {
                    foreach (var pair in RuntimeTextResolver.CreateActionDiagnostics($"actions.choose_reward[{index}].label", reward.Resolution))
                    {
                        actionMetadata[pair.Key] = pair.Value;
                    }
                }

                return new RuntimeActionDefinition(
                    "choose_reward",
                    $"Choose {reward.Label}",
                    new Dictionary<string, object?> { ["reward"] = reward.Label, ["reward_index"] = index },
                    Metadata: actionMetadata);
            })
            .ToList();

        if (rewards.Count > 0)
        {
            var skipAvailability = ResolveRewardSkipAvailability(runNode, rewardAnalysis);
            metadata["reward_skip_available"] = skipAvailability.Available;
            if (!skipAvailability.Available && !string.IsNullOrWhiteSpace(skipAvailability.Reason))
            {
                metadata["reward_skip_reason"] = skipAvailability.Reason;
            }

            if (skipAvailability.Available)
            {
                actions.Add(new RuntimeActionDefinition("skip_reward", "Skip Reward", new Dictionary<string, object?>()));
            }
        }

        if (rewardAnalysis.AdvanceButtonDetected &&
            TryBuildRewardAdvanceAction(runNode, textDiagnostics, out var advanceAction, out var advanceMetadata))
        {
            actions.Add(advanceAction);
            foreach (var pair in advanceMetadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        if (actions.Count == 0 && rewards.Count == 0 && !metadata.ContainsKey("reward_advance_available"))
        {
            metadata["reward_advance_available"] = false;
        }

        LogTextDiagnostics("reward", textDiagnostics);

        return new RuntimeWindowContext(
            DecisionPhase.Reward,
            player,
            Array.Empty<RuntimeEnemyState>(),
            rewards.Select(reward => reward.Label).ToArray(),
            Array.Empty<string>(),
            Terminal: false,
            Metadata: metadata,
            Actions: actions,
            RunState: runStateSnapshot);
    }

    private RuntimeWindowContext BuildMapWindow(object runNode, object runState)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var mapNodes = ExtractMapNodes(runState, textDiagnostics, out var mapNodeSource);
        var playerBuild = BuildPlayerState(runState, textDiagnostics);
        var player = playerBuild.Player;
        var metadata = CreateBaseMetadata(runNode, runState, DecisionPhase.Map);
        foreach (var pair in playerBuild.Diagnostics)
        {
            metadata[pair.Key] = pair.Value;
        }
        var runStateSnapshot = BuildRunState(runState, textDiagnostics, mapNodes, mapNodeSource);
        metadata["node_count"] = mapNodes.Count;
        metadata["window_kind"] = mapNodes.Count > 0 ? "map_ready" : "map_transition";
        metadata["map_ready"] = mapNodes.Count > 0;
        metadata["map_node_source"] = mapNodeSource;
        metadata["no_reachable_nodes"] = mapNodes.Count == 0;
        LogTextDiagnostics("map", textDiagnostics);
        var actions = mapNodes
            .Select(node => new RuntimeActionDefinition(
                "choose_map_node",
                $"Choose {node}",
                new Dictionary<string, object?> { ["node"] = node }))
            .ToList();

        return new RuntimeWindowContext(
            DecisionPhase.Map,
            player,
            Array.Empty<RuntimeEnemyState>(),
            Array.Empty<string>(),
            mapNodes,
            Terminal: false,
            Metadata: metadata,
            Actions: actions,
            RunState: runStateSnapshot);
    }

    private RuntimeWindowContext BuildEventWindow(object runNode, object runState)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var playerBuild = BuildPlayerState(runState, textDiagnostics);
        var player = playerBuild.Player;
        var metadata = CreateBaseMetadata(runNode, runState, DecisionPhase.Event);
        foreach (var pair in playerBuild.Diagnostics)
        {
            metadata[pair.Key] = pair.Value;
        }

        var runStateSnapshot = BuildRunState(runState, textDiagnostics);
        var analysis = AnalyzeEventPhase(runNode, runState, textDiagnostics);
        metadata["window_kind"] = analysis.WindowKind;
        metadata["event_title"] = analysis.Title;
        metadata["event_body"] = analysis.Body;
        metadata["event_continue_available"] = analysis.ContinueAvailable;
        metadata["event_detection_source"] = analysis.DetectionSource;
        metadata["event_phase_detected"] = analysis.TreatAsEvent;
        if (!string.IsNullOrWhiteSpace(analysis.EventSubphase))
        {
            metadata["event_subphase"] = analysis.EventSubphase;
        }
        if (!string.IsNullOrWhiteSpace(analysis.SelectionPrompt))
        {
            metadata["event_selection_prompt"] = analysis.SelectionPrompt;
        }
        metadata["event_options"] = analysis.Options
            .Select(BuildEventOptionPayload)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(analysis.ContinueLabel))
        {
            metadata["event_continue_label"] = analysis.ContinueLabel;
        }
        if (!string.IsNullOrWhiteSpace(analysis.Diagnostics))
        {
            metadata["event_diagnostics"] = analysis.Diagnostics;
        }

        var actions = new List<RuntimeActionDefinition>();
        foreach (var option in analysis.Options)
        {
            if (!option.Available)
            {
                continue;
            }

            if (option.IsContinue)
            {
                actions.Add(new RuntimeActionDefinition(
                    "continue_event",
                    option.Label,
                    new Dictionary<string, object?>
                    {
                        ["button_label"] = option.Label,
                        ["option_index"] = option.Index,
                    },
                    Metadata: new Dictionary<string, object?>
                    {
                        ["event_option_index"] = option.Index,
                        ["event_detection_source"] = option.DetectionSource,
                        ["event_option"] = BuildEventOptionActionMetadata(option),
                    }));
                continue;
            }

            actions.Add(new RuntimeActionDefinition(
                "choose_event_option",
                $"选择 {option.Label}",
                new Dictionary<string, object?>
                {
                    ["option_index"] = option.Index,
                    ["card_id"] = option.StableId,
                },
                Metadata: new Dictionary<string, object?>
                {
                    ["event_option_index"] = option.Index,
                    ["event_detection_source"] = option.DetectionSource,
                    ["event_option"] = BuildEventOptionActionMetadata(option),
                }));
        }

        if (analysis.ContinueAvailable && !actions.Any(action => string.Equals(action.Type, "continue_event", StringComparison.Ordinal)))
        {
            actions.Add(new RuntimeActionDefinition(
                "continue_event",
                analysis.ContinueLabel,
                new Dictionary<string, object?>
                {
                    ["button_label"] = analysis.ContinueLabel,
                },
                Metadata: new Dictionary<string, object?>
                {
                    ["event_detection_source"] = analysis.DetectionSource,
                }));
        }

        LogTextDiagnostics("event", textDiagnostics);
        return new RuntimeWindowContext(
            DecisionPhase.Event,
            player,
            Array.Empty<RuntimeEnemyState>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Terminal: false,
            Metadata: metadata,
            Actions: actions,
            RunState: runStateSnapshot);
    }

    private Dictionary<string, object?> BuildEventOptionPayload(EventOptionAnalysis option)
    {
        return new Dictionary<string, object?>
        {
            ["option_index"] = option.Index,
            ["label"] = option.Label,
            ["available"] = option.Available,
            ["disabled"] = !option.Available,
            ["is_continue"] = option.IsContinue,
            ["detection_source"] = option.DetectionSource,
            ["card_id"] = option.StableId,
            ["description"] = option.Description,
            ["keywords"] = option.Keywords,
            ["glossary"] = option.Glossary,
        };
    }

    private Dictionary<string, object?> BuildEventOptionActionMetadata(EventOptionAnalysis option)
    {
        return new Dictionary<string, object?>
        {
            ["description"] = option.Description,
            ["keywords"] = option.Keywords,
            ["glossary"] = option.Glossary,
        };
    }

    private RuntimeWindowContext BuildShopWindow(object runNode, object runState)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var playerBuild = BuildPlayerState(runState, textDiagnostics);
        var player = playerBuild.Player;
        var metadata = CreateBaseMetadata(runNode, runState, DecisionPhase.Shop);
        foreach (var pair in playerBuild.Diagnostics)
        {
            metadata[pair.Key] = pair.Value;
        }

        var runStateSnapshot = BuildRunState(runState, textDiagnostics);
        var analysis = AnalyzeShopPhase(runNode, runState, textDiagnostics);
        metadata["window_kind"] = "shop_main";
        metadata["shop_leave_available"] = analysis.LeaveButton is not null || analysis.FallbackLeaveSupported;
        metadata["shop_offer_count"] = analysis.Offers.Count;
        metadata["shop_offers"] = analysis.Offers.Select(BuildShopOfferPayload).ToArray();
        LogTextDiagnostics("shop", textDiagnostics);

        var actions = new List<RuntimeActionDefinition>();
        foreach (var offer in analysis.Offers)
        {
            if (!offer.Purchasable)
            {
                continue;
            }

            var parameters = new Dictionary<string, object?>
            {
                ["offer_id"] = offer.OfferId,
                ["offer_index"] = offer.Index,
                ["price"] = offer.Price,
                ["kind"] = offer.Kind,
            };
            if (!string.IsNullOrWhiteSpace(offer.CanonicalId))
            {
                parameters["canonical_id"] = offer.CanonicalId;
            }

            var label = offer.Kind switch
            {
                "service" => $"{offer.Name} ({offer.Price}g)",
                _ => $"Buy {offer.Name} ({offer.Price}g)",
            };
            var actionType = offer.Kind switch
            {
                "card" => "buy_shop_card",
                "relic" => "buy_shop_relic",
                "potion" => "buy_shop_potion",
                _ => "purge_shop_card",
            };
            actions.Add(new RuntimeActionDefinition(
                actionType,
                label,
                parameters,
                Metadata: BuildShopOfferActionMetadata(offer)));
        }

        if (analysis.LeaveButton is not null || analysis.FallbackLeaveSupported)
        {
            actions.Add(new RuntimeActionDefinition(
                "leave_shop",
                "Leave Shop",
                new Dictionary<string, object?>
                {
                    ["choice"] = "leave_shop",
                },
                Metadata: new Dictionary<string, object?>
                {
                    ["choice"] = "leave_shop",
                    ["display_label"] = analysis.LeaveLabel,
                }));
        }

        return new RuntimeWindowContext(
            DecisionPhase.Shop,
            player,
            Array.Empty<RuntimeEnemyState>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Terminal: false,
            Metadata: metadata,
            Actions: actions,
            RunState: runStateSnapshot);
    }

    private Dictionary<string, object?> BuildShopOfferPayload(ShopOfferAnalysis offer)
    {
        var payload = new Dictionary<string, object?>
        {
            ["offer_id"] = offer.OfferId,
            ["kind"] = offer.Kind,
            ["name"] = offer.Name,
            ["price"] = offer.Price,
            ["purchasable"] = offer.Purchasable,
            ["unavailable_reason"] = offer.UnavailableReason,
            ["description"] = offer.Description,
            ["glossary"] = offer.Glossary,
            ["canonical_id"] = offer.CanonicalId,
        };
        if (offer.Card is not null)
        {
            payload["card_id"] = offer.Card.CardId;
            payload["card_type"] = offer.Card.CardType;
            payload["rarity"] = offer.Card.Rarity;
            payload["cost"] = offer.Card.Cost;
            payload["target_type"] = offer.Card.TargetType;
            payload["keywords"] = offer.Card.Keywords;
        }
        else if (offer.Relic is not null)
        {
            payload["keywords"] = offer.Relic.Glossary?.Select(anchor => anchor.GlossaryId).Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }
        else if (offer.Potion is not null)
        {
            payload["keywords"] = offer.Potion.Glossary?.Select(anchor => anchor.GlossaryId).Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(offer.ServiceKind))
        {
            payload["service_kind"] = offer.ServiceKind;
        }

        return payload
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private Dictionary<string, object?> BuildShopOfferActionMetadata(ShopOfferAnalysis offer)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["offer_id"] = offer.OfferId,
            ["offer_index"] = offer.Index,
            ["offer_kind"] = offer.Kind,
            ["offer_name"] = offer.Name,
            ["price"] = offer.Price,
        };
        if (!string.IsNullOrWhiteSpace(offer.CanonicalId))
        {
            metadata["canonical_id"] = offer.CanonicalId;
        }
        if (!string.IsNullOrWhiteSpace(offer.ServiceKind))
        {
            metadata["service_kind"] = offer.ServiceKind;
        }

        return metadata;
    }

    private RuntimeWindowContext BuildTerminalWindow(object runNode, object runState)
    {
        var textDiagnostics = new TextDiagnosticsCollector();
        var playerBuild = BuildPlayerState(runState, textDiagnostics);
        var player = playerBuild.Player;
        var metadata = CreateBaseMetadata(runNode, runState, DecisionPhase.Terminal);
        foreach (var pair in playerBuild.Diagnostics)
        {
            metadata[pair.Key] = pair.Value;
        }
        var runStateSnapshot = BuildRunState(runState, textDiagnostics);
        metadata["result"] = GetBoolean(runState, "IsGameOver") ? "game_over" : "terminal";
        LogTextDiagnostics("terminal", textDiagnostics);
        return new RuntimeWindowContext(
            DecisionPhase.Terminal,
            player,
            Array.Empty<RuntimeEnemyState>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Terminal: true,
            Metadata: metadata,
            Actions: Array.Empty<RuntimeActionDefinition>(),
            RunState: runStateSnapshot);
    }

    private Dictionary<string, object?> CreateBaseMetadata(object runState, string phase)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["source"] = "sts2_runtime",
            ["phase_detected"] = phase,
            ["game_version"] = _probe.GameVersion ?? _options.GameVersion,
            ["managed_dir"] = _probe.ManagedDir,
            ["current_room_type"] = GetTypeName(GetMemberValue(runState, "CurrentRoom")),
            ["current_location_type"] = GetTypeName(GetMemberValue(runState, "CurrentLocation")),
            ["act_floor"] = GetNullableInt(runState, "ActFloor"),
            ["current_act_index"] = GetNullableInt(runState, "CurrentActIndex"),
            ["ascension_level"] = GetNullableInt(runState, "AscensionLevel"),
            ["is_game_over"] = GetBoolean(runState, "IsGameOver"),
        };

        var combatState = GetCombatState(runState);
        if (combatState is not null)
        {
            metadata["round_number"] = GetNullableInt(combatState, "RoundNumber");
            metadata["current_side"] = ConvertToText(GetMemberValue(combatState, "CurrentSide"));
        }

        var currentMapPoint = GetMemberValue(runState, "CurrentMapPoint");
        if (currentMapPoint is not null)
        {
            metadata["current_map_coord"] = DescribeMapCoord(GetMemberValue(currentMapPoint, "coord"));
            metadata["current_map_point_type"] = ConvertToText(GetMemberValue(currentMapPoint, "PointType"));
        }

        var player = GetPlayers(runState).FirstOrDefault();
        var playerCombatState = GetMemberValue(player, "PlayerCombatState");
        if (playerCombatState is not null)
        {
            metadata["stars"] = GetNullableInt(playerCombatState, "Stars");
            metadata["max_energy"] = GetNullableInt(playerCombatState, "MaxEnergy");
        }

        return metadata;
    }

    private bool IsPlayerTurn(object runState)
    {
        var combatState = GetCombatState(runState);
        var currentSide = ConvertToText(GetMemberValue(combatState, "CurrentSide"));
        if (string.IsNullOrWhiteSpace(currentSide))
        {
            return true;
        }

        return string.Equals(currentSide, "Player", StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<string, object?> CreateBaseMetadata(object runNode, object runState, string phase)
    {
        var metadata = CreateBaseMetadata(runState, phase);
        var overlayTop = GetOverlayTopScreen(runNode);
        if (overlayTop is not null)
        {
            metadata["overlay_top_type"] = GetTypeName(overlayTop);
        }
        return metadata;
    }

    private RuntimeRunState BuildRunState(
        object runState,
        TextDiagnosticsCollector textDiagnostics,
        IReadOnlyList<string>? reachableNodes = null,
        string? mapNodeSource = null)
    {
        var currentActIndex = GetNullableInt(runState, "CurrentActIndex");
        var act = GetNullableInt(runState, "Act")
                  ?? GetNullableInt(runState, "CurrentAct")
                  ?? (currentActIndex is null ? null : currentActIndex.Value + 1);
        var currentRoom = GetMemberValue(runState, "CurrentRoom");
        var currentLocation = GetMemberValue(runState, "CurrentLocation");
        var currentMapPoint = GetMemberValue(runState, "CurrentMapPoint");
        var mapNodes = reachableNodes ?? ExtractMapNodes(runState, textDiagnostics, out mapNodeSource);
        mapNodeSource ??= "unavailable";

        var (allNodes, allEdges, visitedPath) = ExtractFullMap(runState, textDiagnostics);

        return new RuntimeRunState(
            Act: act,
            Floor: GetNullableInt(runState, "ActFloor"),
            CurrentRoomType: NormalizeTypeName(currentRoom),
            CurrentLocationType: NormalizeTypeName(currentLocation),
            CurrentActIndex: currentActIndex,
            AscensionLevel: GetNullableInt(runState, "AscensionLevel"),
            Map: new RuntimeRunMapState(
                CurrentCoord: currentMapPoint is null ? null : DescribeMapCoord(GetMemberValue(currentMapPoint, "coord")),
                CurrentNodeType: currentMapPoint is null
                    ? null
                    : ConvertToText(GetMemberValue(currentMapPoint, "PointType"), "run_state.map.current_node_type", textDiagnostics),
                ReachableNodes: mapNodes,
                Source: mapNodeSource,
                AllNodes: allNodes,
                AllEdges: allEdges,
                VisitedPath: visitedPath));
    }

    private object? GetOverlayTopScreen(object runNode)
    {
        var overlayStack = GetOverlayStack(runNode);
        if (overlayStack is null)
        {
            return null;
        }

        return TryInvokeParameterlessMethod(overlayStack, "Peek");
    }

    private PlayerBuildResult BuildPlayerState(object runState, TextDiagnosticsCollector textDiagnostics)
    {
        var player = GetPlayers(runState).FirstOrDefault();
        if (player is null)
        {
            return new PlayerBuildResult(null, new Dictionary<string, object?>());
        }

        var creature = GetMemberValue(player, "Creature");
        var playerCombatState = GetMemberValue(player, "PlayerCombatState");
        var drawPile = GetMemberValue(playerCombatState, "DrawPile");
        var discardPile = GetMemberValue(playerCombatState, "DiscardPile");
        var exhaustPile = GetMemberValue(playerCombatState, "ExhaustPile");
        var handCards = ExtractCards(
            GetMemberValue(playerCombatState, "Hand"),
            "player.hand",
            textDiagnostics,
            CardDescriptionContext.Hand);
        var drawPileCount = CountCards(drawPile);
        var discardPileCount = CountCards(discardPile);
        var exhaustPileCount = CountCards(exhaustPile);
        var drawPileCards = ExtractCardsWithSource(
            drawPile,
            "player.draw_pile_cards",
            textDiagnostics,
            "draw_pile",
            drawPileCount,
            CardDescriptionContext.DrawPile);
        var discardPileCards = ExtractCardsWithSource(
            discardPile,
            "player.discard_pile_cards",
            textDiagnostics,
            "discard_pile",
            discardPileCount,
            CardDescriptionContext.DiscardPile);
        var exhaustPileCards = ExtractCardsWithSource(
            exhaustPile,
            "player.exhaust_pile_cards",
            textDiagnostics,
            "exhaust_pile",
            exhaustPileCount,
            CardDescriptionContext.ExhaustPile);
        var relics = ExtractRelics(GetMemberValue(player, "Relics"), "player.relics", textDiagnostics);
        var potionCollection = ResolvePotionCollection(player);
        var potions = ExtractPotions(potionCollection, "player.potions", textDiagnostics);
        var potionCapacity = GetNullableInt(player, "MaxPotionCount")
                             ?? CountObjects(potionCollection)
                             ?? potions.Count;
        var powers = ExtractPowers(creature ?? player, "player.powers", textDiagnostics);
        var diagnostics = new Dictionary<string, object?>
        {
            ["pile_export"] = new Dictionary<string, object?>
            {
                ["draw_pile"] = CreatePileDiagnostics(drawPileCards),
                ["discard_pile"] = CreatePileDiagnostics(discardPileCards),
                ["exhaust_pile"] = CreatePileDiagnostics(exhaustPileCards),
            },
            ["potion_export"] = new Dictionary<string, object?>
            {
                ["count"] = potions.Count,
                ["capacity"] = potionCapacity,
            },
        };

        return new PlayerBuildResult(new RuntimePlayerState(
            Hp: GetNullableInt(creature, "CurrentHp") ?? 0,
            MaxHp: GetNullableInt(creature, "MaxHp") ?? 0,
            Block: GetNullableInt(creature, "Block") ?? 0,
            Energy: GetNullableInt(playerCombatState, "Energy") ?? 0,
            Gold: GetNullableInt(player, "Gold") ?? 0,
            Hand: handCards,
            DrawPile: drawPileCount,
            DiscardPile: discardPileCount,
            ExhaustPile: exhaustPileCount,
            Relics: relics,
            Potions: potions,
            PotionCapacity: potionCapacity,
            Powers: powers,
            DrawPileCards: drawPileCards.Cards,
            DiscardPileCards: discardPileCards.Cards,
            ExhaustPileCards: exhaustPileCards.Cards), diagnostics);
    }

    private IReadOnlyDictionary<string, object?> CreatePileDiagnostics(PileCardExtraction extraction)
    {
        var diagnostics = new Dictionary<string, object?>
        {
            ["source"] = extraction.Source,
            ["expected_count"] = extraction.ExpectedCount,
            ["exported_count"] = extraction.Cards.Count,
            ["degraded"] = extraction.IsDegraded,
        };

        if (!string.IsNullOrWhiteSpace(extraction.FallbackReason))
        {
            diagnostics["fallback_reason"] = extraction.FallbackReason;
        }

        return diagnostics;
    }

    private PileCardExtraction ExtractCardsWithSource(
        object? pile,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        string pileName,
        int expectedCount,
        CardDescriptionContext descriptionContext)
    {
        if (pile is null)
        {
            return new PileCardExtraction(Array.Empty<RuntimeCard>(), $"{pileName}_missing", expectedCount, "pile_missing");
        }

        var cards = GetMemberValue(pile, "Cards");
        if (cards is null)
        {
            return new PileCardExtraction(Array.Empty<RuntimeCard>(), $"{pileName}_cards_missing", expectedCount, "cards_collection_missing");
        }

        try
        {
            return new PileCardExtraction(
                ExtractCards(pile, path, textDiagnostics, descriptionContext),
                $"{pileName}_cards",
                expectedCount);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Pile extraction degraded for {pileName}: {ex.GetBaseException().Message}");
            return new PileCardExtraction(Array.Empty<RuntimeCard>(), $"{pileName}_extract_failed", expectedCount, "pile_extract_failed");
        }
    }

    private EnemyBuildResult BuildEnemies(object runState, TextDiagnosticsCollector textDiagnostics)
    {
        var combatState = GetCombatState(runState);
        if (combatState is null)
        {
            return new EnemyBuildResult(Array.Empty<RuntimeEnemyState>(), Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var playerTargets = BuildCreatureTargetCollection(runState);
        var enemies = new List<RuntimeEnemyState>();
        var diagnostics = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var (enemy, index) in EnumerateObjects(GetMemberValue(combatState, "Enemies")).Select((enemy, index) => (enemy, index)))
        {
            try
            {
                enemies.Add(BuildEnemyState(enemy, index, playerTargets, textDiagnostics, diagnostics));
            }
            catch (Exception ex)
            {
                var enemyId = ResolveEnemyId(enemy, index);
                diagnostics.Add(CreateEnemyExportDiagnostic(
                    index,
                    enemyId,
                    "enemy",
                    "exception",
                    ex.GetBaseException().Message));
                _logger?.Warn($"Enemy extraction degraded for enemies[{index}] ({enemyId}): {ex.GetBaseException().Message}");
                enemies.Add(BuildFallbackEnemyState(enemy, index, playerTargets, textDiagnostics));
            }
        }

        return new EnemyBuildResult(enemies.ToArray(), diagnostics.ToArray());
    }

    private RuntimeEnemyState BuildEnemyState(
        object enemy,
        int index,
        object? playerTargets,
        TextDiagnosticsCollector textDiagnostics,
        List<IReadOnlyDictionary<string, object?>> diagnostics)
    {
        var path = $"enemies[{index}]";
        var enemyId = ResolveEnemyId(enemy, index);
        var monster = GetMemberValue(enemy, "Monster");
        var intent = ResolveEnemyIntent(enemy, playerTargets, $"{path}.intent", textDiagnostics);
        var powers = ExtractPowers(monster ?? enemy, $"{path}.powers", textDiagnostics, index, enemyId, diagnostics);
        var enrichment = DescribeEnemyEnrichment(enemy, enemyId, index, playerTargets, intent, powers, textDiagnostics, diagnostics);
        return new RuntimeEnemyState(
            EnemyId: enemyId,
            Name: ConvertToText(GetMemberValue(enemy, "Name"), $"{path}.name", textDiagnostics) ?? $"enemy_{index}",
            Hp: GetNullableInt(enemy, "CurrentHp") ?? 0,
            MaxHp: GetNullableInt(enemy, "MaxHp") ?? 0,
            Block: GetNullableInt(enemy, "Block") ?? 0,
            Intent: intent.Display,
            IsAlive: GetBoolean(enemy, "IsAlive", defaultValue: true),
            InstanceEnemyId: enemyId,
            CanonicalEnemyId: ResolveEnemyCanonicalId(enemy),
            IntentRaw: intent.Raw,
            IntentType: intent.Type,
            IntentDamage: intent.Damage,
            IntentHits: intent.Hits,
            IntentBlock: intent.Block,
            IntentEffects: intent.Effects,
            Powers: powers,
            MoveName: enrichment.MoveName,
            MoveDescription: enrichment.MoveDescription,
            MoveGlossary: enrichment.MoveGlossary,
            Traits: enrichment.Traits,
            Keywords: enrichment.Keywords);
    }

    private RuntimeEnemyState BuildFallbackEnemyState(object enemy, int index, object? playerTargets, TextDiagnosticsCollector textDiagnostics)
    {
        var path = $"enemies[{index}]";
        var enemyId = ResolveEnemyId(enemy, index);
        var intent = ResolveEnemyIntent(enemy, playerTargets, $"{path}.intent", textDiagnostics);
        return new RuntimeEnemyState(
            EnemyId: enemyId,
            Name: ConvertToText(GetMemberValue(enemy, "Name"), $"{path}.name", textDiagnostics) ?? $"enemy_{index}",
            Hp: GetNullableInt(enemy, "CurrentHp") ?? 0,
            MaxHp: GetNullableInt(enemy, "MaxHp") ?? 0,
            Block: GetNullableInt(enemy, "Block") ?? 0,
            Intent: intent.Display,
            IsAlive: GetBoolean(enemy, "IsAlive", defaultValue: true),
            InstanceEnemyId: enemyId,
            CanonicalEnemyId: ResolveEnemyCanonicalId(enemy),
            IntentRaw: intent.Raw,
            IntentType: intent.Type,
            IntentDamage: intent.Damage,
            IntentHits: intent.Hits,
            IntentBlock: intent.Block,
            IntentEffects: intent.Effects,
            Powers: ExtractPowers(GetMemberValue(enemy, "Monster") ?? enemy, $"{path}.powers", textDiagnostics, index, enemyId),
            MoveGlossary: Array.Empty<GlossaryAnchor>(),
            Traits: Array.Empty<string>(),
            Keywords: Array.Empty<string>());
    }

    private EnemyEnrichmentDescriptor DescribeEnemyEnrichment(
        object enemy,
        string enemyId,
        int index,
        object? playerTargets,
        EnemyIntentDescriptor intent,
        IReadOnlyList<RuntimePowerState> powers,
        TextDiagnosticsCollector textDiagnostics,
        List<IReadOnlyDictionary<string, object?>> diagnostics)
    {
        var path = $"enemies[{index}]";
        var monster = GetMemberValue(enemy, "Monster");
        var moveSource = ResolveEnemyMoveSource(enemy);

        T SafeRead<T>(string field, Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                diagnostics.Add(CreateEnemyExportDiagnostic(
                    index,
                    enemyId,
                    field,
                    "exception",
                    ex.GetBaseException().Message));
                _logger?.Warn($"Enemy enrich field degraded for {path}.{field}: {ex.GetBaseException().Message}");
                return fallback;
            }
        }

        var moveNameResolution = SafeRead(
            "move_name",
            () => ResolveEnemyMoveName(enemy, moveSource, playerTargets, intent, path, textDiagnostics),
            fallback: new EnemyMoveNameResolution(null));
        if (!string.IsNullOrWhiteSpace(moveNameResolution.SuppressedReason) &&
            !string.IsNullOrWhiteSpace(moveNameResolution.SuppressedCandidate))
        {
            RecordEnemyFieldFilter(
                index,
                enemyId,
                "move_name",
                moveNameResolution.SuppressedReason!,
                $"source={moveNameResolution.Source ?? "unknown"} candidate=\"{AbbreviateForLog(moveNameResolution.SuppressedCandidate)}\"",
                diagnostics);
        }

        var moveName = moveNameResolution.Value;
        var rawTraits = SafeRead(
            "traits",
            () => ExtractEnemyTraits(enemy, monster, moveSource, $"{path}.traits", textDiagnostics),
            fallback: Array.Empty<string>());
        var traits = FilterEnemyTerms(
            rawTraits,
            index,
            enemyId,
            "traits",
            $"{path}.traits",
            diagnostics);
        var placeholderKeywords = Array.Empty<string>();
        var moveDescription = SafeRead(
            "move_description",
            () => ResolveEnemyMoveDescription(enemy, moveSource, playerTargets, path, textDiagnostics, moveName, traits, placeholderKeywords),
            fallback: new DescriptionExtraction(null, null, null, null, null, Array.Empty<DescriptionVariable>(), Array.Empty<GlossaryAnchor>()));
        var moveGlossary = FilterEnemyMoveGlossary(
            moveDescription.Glossary,
            moveName,
            moveDescription.Description,
            index,
            enemyId,
            $"{path}.move_glossary",
            diagnostics);
        var rawKeywords = SafeRead(
            "keywords",
            () => ExtractEnemyKeywords(intent, moveGlossary, traits, powers, enemy, monster, moveSource, $"{path}.keywords", textDiagnostics),
            fallback: Array.Empty<string>());
        var keywords = FilterEnemyTerms(
            rawKeywords,
            index,
            enemyId,
            "keywords",
            $"{path}.keywords",
            diagnostics);

        if (string.IsNullOrWhiteSpace(moveName) &&
            string.IsNullOrWhiteSpace(moveNameResolution.SuppressedReason) &&
            !string.Equals(intent.Display, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(CreateEnemyExportDiagnostic(index, enemyId, "move_name", "not_found", "no readable move name found", "unresolved"));
        }

        if (string.IsNullOrWhiteSpace(moveDescription.Description) && !string.Equals(intent.Display, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(CreateEnemyExportDiagnostic(index, enemyId, "move_description", "not_found", "no readable move description found", "unresolved"));
        }

        return new EnemyEnrichmentDescriptor(
            moveName,
            moveDescription.Description,
            moveGlossary,
            traits,
            keywords);
    }

    private IReadOnlyList<HandCardDescriptor> GetHandCardDescriptors(object runState, TextDiagnosticsCollector textDiagnostics)
    {
        var player = GetPlayers(runState).FirstOrDefault();
        var playerCombatState = GetMemberValue(player, "PlayerCombatState");
        var hand = GetMemberValue(playerCombatState, "Hand");
        return EnumerateObjects(GetMemberValue(hand, "Cards"))
            .Select((card, index) =>
            {
                var nameResolution = RuntimeTextResolver.Resolve(
                    GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card,
                    $"player.hand[{index}].display_name",
                    textDiagnostics,
                    "Title",
                    "Name");
                var traits = ExtractTextList(
                    GetMemberValue(card, "Traits") ?? GetMemberValue(card, "Tags"),
                    $"player.hand[{index}].traits",
                    textDiagnostics);
                var keywords = ExtractTextList(
                    GetMemberValue(card, "Keywords") ?? GetMemberValue(card, "KeywordIds"),
                    $"player.hand[{index}].keywords",
                    textDiagnostics);
                var description = ResolveCardDescription(
                    card,
                    $"player.hand[{index}].description",
                    textDiagnostics,
                    CardDescriptionContext.Hand,
                    traits,
                    keywords);
                return new HandCardDescriptor(
                    RuntimeCardIdentity.CreateCardId(card, index),
                    nameResolution.Text ?? $"card_{index}",
                    nameResolution,
                    ConvertToText(GetMemberValue(card, "TargetType")),
                    GetBoolean(card, "IsPlayable", defaultValue: true),
                    ResolveCardCanonicalId(card),
                    description.Description,
                    description.Glossary,
                    ResolveCardUpgraded(card),
                    traits,
                    keywords);
            })
            .ToArray();
    }

    private IReadOnlyList<RuntimeCard> ExtractCards(
        object? pile,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        CardDescriptionContext descriptionContext = CardDescriptionContext.Unknown)
    {
        var results = new List<RuntimeCard>();
        foreach (var (card, index) in EnumerateObjects(GetMemberValue(pile, "Cards")).Select((card, index) => (card, index)))
        {
            try
            {
                results.Add(BuildRuntimeCard(card, index, path, textDiagnostics, descriptionContext));
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Card extraction degraded for {path}[{index}]: {ex.GetBaseException().Message}");
                results.Add(BuildFallbackRuntimeCard(card, index));
            }
        }

        return results;
    }

    private RuntimeCard BuildRuntimeCard(
        object card,
        int index,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        CardDescriptionContext descriptionContext = CardDescriptionContext.Unknown)
    {
        var traits = ExtractTextList(
            GetMemberValue(card, "Traits") ?? GetMemberValue(card, "Tags"),
            $"{path}[{index}].traits",
            textDiagnostics);
        var keywords = ExtractTextList(
            GetMemberValue(card, "Keywords") ?? GetMemberValue(card, "KeywordIds"),
            $"{path}[{index}].keywords",
            textDiagnostics);
        var description = ResolveCardDescription(
            card,
            $"{path}[{index}].description",
            textDiagnostics,
            descriptionContext,
            traits,
            keywords);
        var instanceCardId = RuntimeCardIdentity.CreateCardId(card, index);
        return new RuntimeCard(
            CardId: instanceCardId,
            Name: ConvertToText(GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card, $"{path}[{index}].name", textDiagnostics, "Title", "Name")
                  ?? $"card_{index}",
            Cost: ResolveCardCost(card),
            Playable: GetBoolean(card, "IsPlayable", defaultValue: true),
            InstanceCardId: instanceCardId,
            CanonicalCardId: ResolveCardCanonicalId(card),
            Description: description.Description,
            CostForTurn: ResolveCardCostForTurn(card),
            Upgraded: ResolveCardUpgraded(card),
            TargetType: ConvertToText(GetMemberValue(card, "TargetType"), $"{path}[{index}].target_type", textDiagnostics),
            CardType: ConvertToText(GetMemberValue(card, "CardType") ?? GetMemberValue(card, "Type"), $"{path}[{index}].card_type", textDiagnostics),
            Rarity: ConvertToText(GetMemberValue(card, "Rarity") ?? GetMemberValue(card, "CardRarity"), $"{path}[{index}].rarity", textDiagnostics),
            Traits: traits,
            Keywords: keywords,
            Glossary: description.Glossary);
    }

    private RuntimeCard BuildFallbackRuntimeCard(object? card, int index)
    {
        var instanceCardId = RuntimeCardIdentity.CreateCardId(card, index);
        return new RuntimeCard(
            CardId: instanceCardId,
            Name: ConvertToText(GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card) ?? $"card_{index}",
            Cost: ResolveCardCost(card),
            Playable: GetBoolean(card, "IsPlayable", defaultValue: true),
            InstanceCardId: instanceCardId,
            CanonicalCardId: ResolveCardCanonicalId(card),
            Description: null,
            CostForTurn: ResolveCardCostForTurn(card),
            Upgraded: ResolveCardUpgraded(card),
            TargetType: ConvertToText(GetMemberValue(card, "TargetType")),
            CardType: ConvertToText(GetMemberValue(card, "CardType") ?? GetMemberValue(card, "Type")),
            Rarity: ConvertToText(GetMemberValue(card, "Rarity") ?? GetMemberValue(card, "CardRarity")),
            Traits: Array.Empty<string>(),
            Keywords: Array.Empty<string>(),
            Glossary: Array.Empty<GlossaryAnchor>());
    }

    private int CountCards(object? pile)
    {
        return EnumerateObjects(GetMemberValue(pile, "Cards")).Count();
    }

    private static int? CountObjects(object? collection)
    {
        if (collection is null)
        {
            return null;
        }

        return EnumerateObjects(collection).Count();
    }

    private List<RewardOption> ExtractRewards(object runNode, TextDiagnosticsCollector textDiagnostics)
    {
        var rewardScreen = GetRewardScreen(runNode);
        var cardRewardScreen = GetCardRewardSelectionScreen(runNode, rewardScreen);
        if (cardRewardScreen is not null)
        {
            return ExtractCardRewardSelectionRewards(cardRewardScreen, textDiagnostics);
        }

        if (rewardScreen is null)
        {
            return new List<RewardOption>();
        }

        return GetRewardButtons(rewardScreen)
            .Select((button, index) => DescribeReward(GetMemberValue(button, "Reward"), $"rewards[{index}]", textDiagnostics))
            .OfType<RewardOption>()
            .Where(reward => !string.IsNullOrWhiteSpace(reward.Label))
            .ToList();
    }

    private List<RewardOption> ExtractCardRewardSelectionRewards(object cardRewardScreen, TextDiagnosticsCollector textDiagnostics)
    {
        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(cardRewardScreen));
        if (choices.Count == 0)
        {
            return new List<RewardOption>();
        }

        var options = new List<RewardOption>(choices.Count);
        for (var index = 0; index < choices.Count; index++)
        {
            var card = ResolveCardRewardChoiceCard(choices[index]);
            var display = GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card;
            var resolution = RuntimeTextResolver.Resolve(display, $"rewards[{index}].card", textDiagnostics, "Title", "Name");
            var label = resolution.Text ?? $"card_{index}";
            options.Add(new RewardOption(label, resolution));
        }

        return options
            .Where(option => !string.IsNullOrWhiteSpace(option.Label))
            .ToList();
    }

    private List<object> FilterCardRewardSelectionChoices(List<object> choices)
    {
        if (choices.Count == 0)
        {
            return choices;
        }

        // If we already discovered holder-like nodes, drop entries that do not actually carry a card model/node.
        if (choices.Any(choice => GetMemberValue(choice, "CardModel") is not null || GetMemberValue(choice, "CardNode") is not null))
        {
            return choices
                .Where(choice => GetMemberValue(choice, "CardModel") is not null || GetMemberValue(choice, "CardNode") is not null)
                .ToList();
        }

        return choices;
    }

    private List<string> ExtractMapNodes(object runState, TextDiagnosticsCollector textDiagnostics, out string source)
    {
        var currentMapPoint = GetMemberValue(runState, "CurrentMapPoint");
        var nodes = EnumerateObjects(GetMemberValue(currentMapPoint, "Children"))
            .Select((node, index) => DescribeMapNode(node, $"map_nodes[{index}]", textDiagnostics))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (nodes.Count > 0)
        {
            source = "current_map_point";
            return nodes;
        }

        var map = GetMemberValue(runState, "Map");
        var startingPoint = GetMemberValue(map, "StartingMapPoint");
        nodes = EnumerateObjects(GetMemberValue(startingPoint, "Children"))
            .Select((node, index) => DescribeMapNode(node, $"map_nodes[{index}]", textDiagnostics))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        source = nodes.Count > 0 ? "starting_map_point_fallback" : "no_reachable_nodes";
        return nodes;
    }

    private (List<RuntimeMapNodeInfo> Nodes, List<RuntimeMapEdge> Edges, List<string> VisitedPath) ExtractFullMap(
        object runState, TextDiagnosticsCollector textDiagnostics)
    {
        var nodes = new List<RuntimeMapNodeInfo>();
        var edges = new List<RuntimeMapEdge>();
        var seenCoords = new HashSet<string>(StringComparer.Ordinal);
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        var currentCoordStr = (string?)null;

        var currentMapPoint = GetMemberValue(runState, "CurrentMapPoint");
        if (currentMapPoint is not null)
        {
            currentCoordStr = DescribeMapCoord(GetMemberValue(currentMapPoint, "coord"));
        }

        var map = GetMemberValue(runState, "Map");
        var startingPoint = GetMemberValue(map, "StartingMapPoint");
        var rootPoint = startingPoint;

        if (rootPoint is null)
        {
            return (nodes, edges, new List<string>());
        }

        WalkMapTree(rootPoint, null, textDiagnostics, nodes, edges, seenCoords, seenEdges, currentCoordStr, 0);

        var visitedPath = string.IsNullOrEmpty(currentCoordStr)
            ? new List<string>()
            : new List<string> { currentCoordStr };

        return (nodes, edges, visitedPath);
    }

    private void WalkMapTree(
        object? mapPoint,
        string? parentCoord,
        TextDiagnosticsCollector textDiagnostics,
        List<RuntimeMapNodeInfo> nodes,
        List<RuntimeMapEdge> edges,
        HashSet<string> seenCoords,
        HashSet<string> seenEdges,
        string? currentCoordStr,
        int depth)
    {
        if (mapPoint is null || depth > 50)
        {
            return;
        }

        var coordObj = GetMemberValue(mapPoint, "coord");
        var coord = DescribeMapCoord(coordObj);
        if (string.IsNullOrWhiteSpace(coord) || coord == "-1,-1")
        {
            return;
        }

        // Add edge if not duplicate
        if (parentCoord is not null)
        {
            var edgeKey = $"{parentCoord}->{coord}";
            if (seenEdges.Add(edgeKey))
            {
                edges.Add(new RuntimeMapEdge(parentCoord, coord));
            }
        }

        // Skip if this node was already added via another path
        if (!seenCoords.Add(coord))
        {
            return;
        }

        var pointType = ConvertToText(GetMemberValue(mapPoint, "PointType"), "map_node.point_type", textDiagnostics) ?? "unknown";
        var col = GetNullableInt(coordObj, "col") ?? -1;
        var row = GetNullableInt(coordObj, "row") ?? -1;
        var isCurrent = string.Equals(coord, currentCoordStr, StringComparison.Ordinal);

        nodes.Add(new RuntimeMapNodeInfo(coord, pointType, col, row, isCurrent, isCurrent));

        var children = EnumerateObjects(GetMemberValue(mapPoint, "Children"));
        foreach (var child in children)
        {
            WalkMapTree(child, coord, textDiagnostics, nodes, edges, seenCoords, seenEdges, currentCoordStr, depth + 1);
        }
    }

    private bool TryGetRuntimeRoot(Assembly assembly, out RuntimeRoot root, out string status)
    {
        var gameType = assembly.GetType(NGameTypeName);
        if (gameType is null)
        {
            root = default;
            status = "MegaCrit.Sts2.Core.Nodes.NGame was not found in sts2.dll.";
            return false;
        }

        var gameInstance = GetMemberValue(gameType, "Instance");
        if (gameInstance is null)
        {
            root = default;
            status = "sts2 assembly loaded; waiting for NGame.Instance.";
            return false;
        }

        var runNode = GetMemberValue(gameInstance, "CurrentRunNode");
        if (runNode is null)
        {
            var runType = assembly.GetType(NRunTypeName);
            runNode = GetMemberValue(runType, "Instance");
        }

        if (runNode is null)
        {
            root = new RuntimeRoot(gameInstance, RunNode: null, RunState: null);
            status = "game runtime attached; no active run detected (exporting menu phase).";
            return true;
        }

        var runState = GetMemberValue(runNode, "_state");
        if (runState is null)
        {
            root = new RuntimeRoot(gameInstance, runNode, RunState: null);
            status = "run node found, but RunState is not available yet (exporting menu phase).";
            return true;
        }

        root = new RuntimeRoot(gameInstance, runNode, runState);
        status = "ok";
        return true;
    }

    private ShopPhaseAnalysis AnalyzeShopPhase(object runNode, object runState, TextDiagnosticsCollector? textDiagnostics)
    {
        var currentRoom = GetMemberValue(runState, "CurrentRoom");
        var currentRoomType = GetTypeName(currentRoom) ?? string.Empty;
        var currentMapPointType = ConvertToText(GetMemberValue(runState, "CurrentMapPointType") ?? GetMemberValue(GetMemberValue(runState, "CurrentMapPoint"), "NodeType")) ?? string.Empty;
        var looksLikeShop = currentRoomType.Contains("Merchant", StringComparison.OrdinalIgnoreCase) ||
                            currentRoomType.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
                            currentMapPointType.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
                            currentMapPointType.Contains("Merchant", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeShop)
        {
            return new ShopPhaseAnalysis(false, Array.Empty<ShopOfferAnalysis>(), null, "????", false, "none");
        }

        var diagnostics = textDiagnostics ?? new TextDiagnosticsCollector();
        var playerBuild = BuildPlayerState(runState, diagnostics);
        var player = playerBuild.Player;
        var merchantRoomNode = ResolveMerchantRoomNode(runNode);
        var inventoryNode = GetMemberValue(merchantRoomNode, "Inventory");
        var inventoryEntity = GetMemberValue(inventoryNode, "Inventory") ?? GetMemberValue(currentRoom, "Inventory");
        var offerNodes = new List<object>();
        var seenOfferIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? leaveButton = null;
        var leaveLabel = "????";
        var detectionSource = inventoryEntity is not null
            ? "merchant_inventory"
            : !string.IsNullOrWhiteSpace(currentRoomType)
                ? "current_room_type"
                : "current_map_point_type";

        var offers = new List<ShopOfferAnalysis>();
        if (inventoryEntity is not null)
        {
            var slotByEntry = BuildMerchantSlotIndex(inventoryNode);
            var entryCandidates = EnumerateMerchantEntries(inventoryEntity).ToList();
            foreach (var (entry, index) in entryCandidates.Select((entry, index) => (entry, index)))
            {
                slotByEntry.TryGetValue(entry, out var slotNode);
                if (!TryBuildShopOfferFromEntry(entry, slotNode, index, player, diagnostics, out var offer))
                {
                    continue;
                }

                if (!seenOfferIds.Add(offer.OfferId))
                {
                    continue;
                }

                offers.Add(offer);
            }
        }

        IEnumerable<object> EnumerateRoots()
        {
            if (merchantRoomNode is not null)
            {
                yield return merchantRoomNode;
            }

            if (inventoryNode is not null)
            {
                yield return inventoryNode;
            }

            if (currentRoom is not null)
            {
                yield return currentRoom;
                foreach (var memberName in new[] { "Inventory", "MerchantInventory", "InventoryUi", "UiRoot", "Root", "Screen" })
                {
                    var candidate = GetMemberValue(currentRoom, memberName);
                    if (candidate is not null)
                    {
                        yield return candidate;
                    }
                }
            }

            yield return runNode;
            var overlayTop = GetOverlayTopScreen(runNode);
            if (overlayTop is not null)
            {
                yield return overlayTop;
            }
        }

        var seenNodes = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var root in EnumerateRoots())
        {
            foreach (var node in EnumerateNodeDescendants(root, maxDepth: 8).Prepend(root))
            {
                if (!seenNodes.Add(node))
                {
                    continue;
                }

                var typeName = GetTypeName(node) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                if (!IsShopNodeVisible(node))
                {
                    continue;
                }

                if (typeName.Contains("MerchantCardRemoval", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("MerchantCard", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("MerchantRelic", StringComparison.OrdinalIgnoreCase) ||
                    typeName.Contains("MerchantPotion", StringComparison.OrdinalIgnoreCase))
                {
                    offerNodes.Add(node);
                    continue;
                }

                if (leaveButton is null && typeName.Contains("MerchantButton", StringComparison.OrdinalIgnoreCase))
                {
                    var label = GetMenuNodeLabel(node, diagnostics);
                    if (!string.IsNullOrWhiteSpace(label) && IsShopLeaveLabel(label))
                    {
                        leaveButton = node;
                        leaveLabel = label.Trim();
                    }
                }
            }
        }

        if (leaveButton is null)
        {
            var backButton = GetMemberValue(inventoryNode, "_backButton")
                ?? GetMemberValue(inventoryNode, "BackButton");
            var proceedButton = GetMemberValue(merchantRoomNode, "ProceedButton")
                ?? GetMemberValue(merchantRoomNode, "_proceedButton");
            leaveButton = backButton
                ?? proceedButton
                ?? GetMemberValue(merchantRoomNode, "MerchantButton");
            if (leaveButton is not null)
            {
                leaveLabel = "离开商店";
            }
        }

        if (leaveButton is null)
        {
            var buttonSummary = string.Join(
                " | ",
                seenNodes
                    .Where(node => (GetTypeName(node) ?? string.Empty).Contains("Button", StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .Select(node =>
                    {
                        var typeName = NormalizeTypeName(node) ?? "unknown";
                        var label = GetMenuNodeLabel(node, diagnostics) ?? "<none>";
                        return $"{typeName}:{AbbreviateForLog(label)}";
                    }));
            _logger?.Info($"Shop leave button unresolved room={currentRoomType} buttons={buttonSummary}");
        }

        if (offers.Count == 0)
        {
            foreach (var (node, index) in offerNodes.Select((node, index) => (node, index)))
            {
                if (!TryBuildShopOffer(node, index, player, diagnostics, out var offer))
                {
                    continue;
                }

                if (!seenOfferIds.Add(offer.OfferId))
                {
                    continue;
                }

                offers.Add(offer);
            }
        }

        if (string.IsNullOrWhiteSpace(leaveLabel) || leaveLabel == "????")
        {
            leaveLabel = "离开商店";
        }

        var fallbackLeaveSupported =
            (merchantRoomNode is not null &&
             (HasAnyCompatibleMethod(merchantRoomNode, new[] { "HideScreen" }, new object?[] { leaveButton }, Array.Empty<object?>()) ||
              HasAnyCompatibleMethod(merchantRoomNode, new[] { "OpenInventory" }, Array.Empty<object?>()))) ||
            (currentRoom is not null &&
             HasAnyCompatibleMethod(currentRoom, ShopLeaveMethodNames, Array.Empty<object?>(), new object?[] { leaveButton }));
        return new ShopPhaseAnalysis(true, offers, leaveButton, leaveLabel, fallbackLeaveSupported, detectionSource);
    }

    private object? ResolveMerchantRoomNode(object runNode)
    {
        var merchantRoomType = FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom");
        var instance = merchantRoomType is null ? null : GetMemberValue(merchantRoomType, "Instance");
        if (instance is not null)
        {
            return instance;
        }

        return EnumerateNodeDescendants(runNode, maxDepth: 8)
            .FirstOrDefault(node =>
            {
                var typeName = GetTypeName(node) ?? string.Empty;
                return typeName.Contains("NMerchantRoom", StringComparison.Ordinal);
            });
    }

    private Dictionary<object, object> BuildMerchantSlotIndex(object? inventoryNode)
    {
        var slotByEntry = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        if (inventoryNode is null)
        {
            return slotByEntry;
        }

        IEnumerable<object> slots = Array.Empty<object>();
        if (TryInvokeParameterlessMethod(inventoryNode, "GetAllSlots") is IEnumerable slotsResult)
        {
            slots = EnumerateObjects(slotsResult);
        }
        else
        {
            slots = EnumerateNodeDescendants(inventoryNode, maxDepth: 6)
                .Where(node =>
                {
                    var typeName = GetTypeName(node) ?? string.Empty;
                    return typeName.Contains("NMerchantSlot", StringComparison.Ordinal) ||
                           typeName.Contains("NMerchantCard", StringComparison.Ordinal) ||
                           typeName.Contains("NMerchantRelic", StringComparison.Ordinal) ||
                           typeName.Contains("NMerchantPotion", StringComparison.Ordinal) ||
                           typeName.Contains("NMerchantCardRemoval", StringComparison.Ordinal);
                });
        }

        foreach (var slot in slots)
        {
            var entry = GetMemberValue(slot, "Entry");
            if (entry is not null)
            {
                slotByEntry[entry] = slot;
            }
        }

        return slotByEntry;
    }

    private IEnumerable<object> EnumerateMerchantEntries(object inventoryEntity)
    {
        foreach (var memberName in ShopInventoryEntryCollections)
        {
            foreach (var entry in EnumerateObjects(GetMemberValue(inventoryEntity, memberName)))
            {
                yield return entry;
            }
        }

        var removalEntry = GetMemberValue(inventoryEntity, "CardRemovalEntry");
        if (removalEntry is not null)
        {
            yield return removalEntry;
        }
    }

    private bool TryBuildShopOfferFromEntry(
        object entry,
        object? slotNode,
        int index,
        RuntimePlayerState? player,
        TextDiagnosticsCollector textDiagnostics,
        out ShopOfferAnalysis offer)
    {
        var typeName = GetTypeName(entry) ?? string.Empty;
        var detectionSource = $"merchant_entry.{NormalizeTypeName(entry) ?? "unknown"}";
        var gold = player?.Gold ?? 0;
        var potionCount = player?.Potions.Count ?? 0;
        var potionCapacity = player?.PotionCapacity ?? 0;
        var price = ResolveShopPrice(entry) ?? (slotNode is null ? null : ResolveShopPrice(slotNode)) ?? 0;
        var stocked = GetMemberValue(entry, "IsStocked") is not bool stockedValue || stockedValue;
        var enoughGold = GetMemberValue(entry, "EnoughGold") is not bool enoughGoldValue || enoughGoldValue;
        var interactable = slotNode is null || IsMenuNodeInteractable(slotNode);

        if (typeName.Contains("MerchantCardRemovalEntry", StringComparison.OrdinalIgnoreCase))
        {
            var service = DescribeShopService(
                primarySource: slotNode,
                secondarySource: entry,
                path: $"shop.service[{index}]",
                textDiagnostics);
            var used = GetMemberValue(entry, "Used") is bool usedValue && usedValue;
            var purchasable = stocked && interactable && !used && enoughGold && gold >= price;
            var unavailableReason = purchasable ? null : used ? "service_unavailable" : (gold < price || !enoughGold) ? "not_affordable" : "service_unavailable";
            offer = new ShopOfferAnalysis(
                OfferId: "service:purge_card",
                Kind: "service",
                Index: index,
                Name: service.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: service.Description,
                Glossary: service.Glossary,
                CanonicalId: null,
                DetectionSource: detectionSource,
                OfferNode: entry,
                ActivationNode: slotNode ?? entry,
                Card: null,
                Relic: null,
                Potion: null,
                ServiceKind: "purge_card");
            return true;
        }

        if (typeName.Contains("MerchantCardEntry", StringComparison.OrdinalIgnoreCase))
        {
            var cardSource = GetMemberValue(GetMemberValue(entry, "CreationResult"), "Card")
                ?? GetMemberValue(GetMemberValue(slotNode, "_cardNode"), "CardModel")
                ?? GetMemberValue(GetMemberValue(slotNode, "_cardNode"), "Model")
                ?? ResolveShopPayloadNode(slotNode ?? entry, ShopCardMemberNames)
                ?? entry;
            RuntimeCard runtimeCard;
            try
            {
                runtimeCard = BuildRuntimeCard(cardSource, index, "shop.card", textDiagnostics, CardDescriptionContext.Preview);
            }
            catch
            {
                runtimeCard = BuildFallbackRuntimeCard(cardSource, index);
            }

            var purchasable = stocked && interactable && enoughGold && gold >= price;
            var unavailableReason = purchasable ? null : (gold < price || !enoughGold) ? "not_affordable" : "not_clickable";
            offer = new ShopOfferAnalysis(
                OfferId: $"card:{runtimeCard.CardId}",
                Kind: "card",
                Index: index,
                Name: runtimeCard.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: runtimeCard.Description,
                Glossary: runtimeCard.Glossary ?? Array.Empty<GlossaryAnchor>(),
                CanonicalId: runtimeCard.CanonicalCardId,
                DetectionSource: detectionSource,
                OfferNode: entry,
                ActivationNode: slotNode ?? entry,
                Card: runtimeCard,
                Relic: null,
                Potion: null,
                ServiceKind: null);
            return true;
        }

        if (typeName.Contains("MerchantRelicEntry", StringComparison.OrdinalIgnoreCase))
        {
            var relicSource = GetMemberValue(entry, "Model")
                ?? GetMemberValue(slotNode, "_relic")
                ?? ResolveShopPayloadNode(slotNode ?? entry, ShopRelicMemberNames)
                ?? entry;
            var relic = DescribeRelic(relicSource, $"shop.relic[{index}]", textDiagnostics);
            if (relic is null)
            {
                offer = default;
                return false;
            }

            var purchasable = stocked && interactable && enoughGold && gold >= price;
            var unavailableReason = purchasable ? null : (gold < price || !enoughGold) ? "not_affordable" : "not_clickable";
            offer = new ShopOfferAnalysis(
                OfferId: $"relic:{relic.CanonicalRelicId ?? NormalizeComparisonText(relic.Name) ?? index.ToString()}",
                Kind: "relic",
                Index: index,
                Name: relic.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: relic.Description,
                Glossary: relic.Glossary ?? Array.Empty<GlossaryAnchor>(),
                CanonicalId: relic.CanonicalRelicId,
                DetectionSource: detectionSource,
                OfferNode: entry,
                ActivationNode: slotNode ?? entry,
                Card: null,
                Relic: relic,
                Potion: null,
                ServiceKind: null);
            return true;
        }

        if (typeName.Contains("MerchantPotionEntry", StringComparison.OrdinalIgnoreCase))
        {
            var potionSource = GetMemberValue(entry, "Model")
                ?? GetMemberValue(slotNode, "_potion")
                ?? ResolveShopPayloadNode(slotNode ?? entry, ShopPotionMemberNames)
                ?? entry;
            var potion = DescribePotion(potionSource, $"shop.potion[{index}]", textDiagnostics);
            if (potion is null)
            {
                offer = default;
                return false;
            }

            var potionSlotsFull = potionCapacity > 0 && potionCount >= potionCapacity;
            var purchasable = stocked && interactable && enoughGold && gold >= price && !potionSlotsFull;
            var unavailableReason = purchasable ? null : potionSlotsFull ? "potion_slots_full" : (gold < price || !enoughGold) ? "not_affordable" : "not_clickable";
            offer = new ShopOfferAnalysis(
                OfferId: $"potion:{potion.CanonicalPotionId ?? NormalizeComparisonText(potion.Name) ?? index.ToString()}",
                Kind: "potion",
                Index: index,
                Name: potion.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: potion.Description,
                Glossary: potion.Glossary ?? Array.Empty<GlossaryAnchor>(),
                CanonicalId: potion.CanonicalPotionId,
                DetectionSource: detectionSource,
                OfferNode: entry,
                ActivationNode: slotNode ?? entry,
                Card: null,
                Relic: null,
                Potion: potion,
                ServiceKind: null);
            return true;
        }

        offer = default;
        return false;
    }

    private bool TryBuildShopOffer(
        object node,
        int index,
        RuntimePlayerState? player,
        TextDiagnosticsCollector textDiagnostics,
        out ShopOfferAnalysis offer)
    {
        var typeName = GetTypeName(node) ?? string.Empty;
        var detectionSource = $"shop_node.{NormalizeTypeName(node) ?? "unknown"}";
        var gold = player?.Gold ?? 0;
        var potionCount = player?.Potions.Count ?? 0;
        var potionCapacity = player?.PotionCapacity ?? 0;
        var price = ResolveShopPrice(node) ?? 0;
        var interactable = IsMenuNodeInteractable(node);

        if (typeName.Contains("MerchantCardRemoval", StringComparison.OrdinalIgnoreCase))
        {
            var serviceEntry =
                ResolveShopPayloadNode(node, new[] { "Entry", "_removalEntry", "RemovalEntry" })
                ?? GetFirstMemberValue(node, "Entry", "_removalEntry", "RemovalEntry");
            var service = DescribeShopService(
                primarySource: node,
                secondarySource: serviceEntry,
                path: $"shop.service[{index}]",
                textDiagnostics);
            var purchasable = interactable && gold >= price;
            var unavailableReason = purchasable ? null : gold < price ? "not_affordable" : "service_unavailable";
            offer = new ShopOfferAnalysis(
                OfferId: "service:purge_card",
                Kind: "service",
                Index: index,
                Name: service.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: service.Description,
                Glossary: service.Glossary,
                CanonicalId: null,
                DetectionSource: detectionSource,
                OfferNode: node,
                ActivationNode: node,
                Card: null,
                Relic: null,
                Potion: null,
                ServiceKind: "purge_card");
            if (price <= 0)
            {
                _logger?.Info($"Shop offer probe type={typeName} kind=service members={DescribeObjectMembersForLog(node)}");
            }
            return true;
        }

        if (typeName.Contains("MerchantCard", StringComparison.OrdinalIgnoreCase))
        {
            var cardSource = ResolveShopPayloadNode(node, ShopCardMemberNames) ?? node;
            RuntimeCard runtimeCard;
            try
            {
                runtimeCard = BuildRuntimeCard(cardSource, index, "shop.card", textDiagnostics, CardDescriptionContext.Preview);
            }
            catch
            {
                runtimeCard = BuildFallbackRuntimeCard(cardSource, index);
            }

            var purchasable = interactable && gold >= price;
            var unavailableReason = purchasable ? null : gold < price ? "not_affordable" : "not_clickable";
            offer = new ShopOfferAnalysis(
                OfferId: $"card:{runtimeCard.CardId}",
                Kind: "card",
                Index: index,
                Name: runtimeCard.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: runtimeCard.Description,
                Glossary: runtimeCard.Glossary ?? Array.Empty<GlossaryAnchor>(),
                CanonicalId: runtimeCard.CanonicalCardId,
                DetectionSource: detectionSource,
                OfferNode: node,
                ActivationNode: node,
                Card: runtimeCard,
                Relic: null,
                Potion: null,
                ServiceKind: null);
            if (price <= 0 || runtimeCard.Name.StartsWith("MerchantCardHolder", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Info(
                    $"Shop offer probe type={typeName} kind=card name={runtimeCard.Name} price={price} " +
                    $"node={DescribeObjectMembersForLog(node)} payload={DescribeObjectMembersForLog(cardSource)}");
            }
            return true;
        }

        if (typeName.Contains("MerchantRelic", StringComparison.OrdinalIgnoreCase))
        {
            var relicSource = ResolveShopPayloadNode(node, ShopRelicMemberNames) ?? node;
            var relic = DescribeRelic(relicSource, $"shop.relic[{index}]", textDiagnostics);
            if (relic is null)
            {
                offer = default;
                return false;
            }

            var purchasable = interactable && gold >= price;
            var unavailableReason = purchasable ? null : gold < price ? "not_affordable" : "not_clickable";
            offer = new ShopOfferAnalysis(
                OfferId: $"relic:{relic.CanonicalRelicId ?? NormalizeComparisonText(relic.Name) ?? index.ToString()}",
                Kind: "relic",
                Index: index,
                Name: relic.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: relic.Description,
                Glossary: relic.Glossary ?? Array.Empty<GlossaryAnchor>(),
                CanonicalId: relic.CanonicalRelicId,
                DetectionSource: detectionSource,
                OfferNode: node,
                ActivationNode: node,
                Card: null,
                Relic: relic,
                Potion: null,
                ServiceKind: null);
            if (price <= 0)
            {
                _logger?.Info(
                    $"Shop offer probe type={typeName} kind=relic name={relic.Name} price={price} " +
                    $"node={DescribeObjectMembersForLog(node)} payload={DescribeObjectMembersForLog(relicSource)}");
            }
            return true;
        }

        if (typeName.Contains("MerchantPotion", StringComparison.OrdinalIgnoreCase))
        {
            var potionSource = ResolveShopPayloadNode(node, ShopPotionMemberNames) ?? node;
            var potion = DescribePotion(potionSource, $"shop.potion[{index}]", textDiagnostics);
            if (potion is null)
            {
                offer = default;
                return false;
            }

            var potionSlotsFull = potionCapacity > 0 && potionCount >= potionCapacity;
            var purchasable = interactable && gold >= price && !potionSlotsFull;
            var unavailableReason = purchasable ? null : potionSlotsFull ? "potion_slots_full" : gold < price ? "not_affordable" : "not_clickable";
            offer = new ShopOfferAnalysis(
                OfferId: $"potion:{potion.CanonicalPotionId ?? NormalizeComparisonText(potion.Name) ?? index.ToString()}",
                Kind: "potion",
                Index: index,
                Name: potion.Name,
                Price: price,
                Purchasable: purchasable,
                UnavailableReason: unavailableReason,
                Description: potion.Description,
                Glossary: potion.Glossary ?? Array.Empty<GlossaryAnchor>(),
                CanonicalId: potion.CanonicalPotionId,
                DetectionSource: detectionSource,
                OfferNode: node,
                ActivationNode: node,
                Card: null,
                Relic: null,
                Potion: potion,
                ServiceKind: null);
            if (price <= 0)
            {
                _logger?.Info(
                    $"Shop offer probe type={typeName} kind=potion name={potion.Name} price={price} " +
                    $"node={DescribeObjectMembersForLog(node)} payload={DescribeObjectMembersForLog(potionSource)}");
            }
            return true;
        }

        offer = default;
        return false;
    }

    private (string Name, string Description, IReadOnlyList<GlossaryAnchor> Glossary) DescribeShopService(
        object? primarySource,
        object? secondarySource,
        string path,
        TextDiagnosticsCollector textDiagnostics)
    {
        var titleValue =
            GetFirstMemberValue(primarySource, "Title", "Label", "Name", "Text")
            ?? GetFirstMemberValue(secondarySource, "Title", "Label", "Name", "Text");
        var fallbackNode = primarySource ?? secondarySource;
        var fallbackLabel = fallbackNode is null ? null : GetMenuNodeLabel(fallbackNode, textDiagnostics);
        var name = NormalizeDescriptionText(ConvertDescriptionTemplateToText(
                titleValue,
                $"{path}.title",
                textDiagnostics,
                "Title",
                "Label",
                "Name",
                "Text"))
            ?? fallbackLabel
            ?? "卡牌移除服务";

        var descriptionValue =
            GetFirstMemberValue(primarySource, "Description", "DynamicDescription", "StaticDescription", "RulesText", "Text", "LocString")
            ?? GetFirstMemberValue(secondarySource, "Description", "DynamicDescription", "StaticDescription", "RulesText", "Text", "LocString");
        var boundDescriptionValue =
            TryBindLocStringWithDynamicVars(descriptionValue, primarySource)
            ?? TryBindLocStringWithDynamicVars(descriptionValue, secondarySource);
        var raw = ConvertDescriptionTemplateToText(
            descriptionValue,
            $"{path}.description",
            textDiagnostics,
            "Description",
            "DynamicDescription",
            "StaticDescription",
            "RulesText",
            "Text",
            "LocString");
        var rendered = NormalizeDescriptionText(ConvertRenderedDescriptionToText(
            GetFirstMemberValue(primarySource, "RenderedDescription", "RenderedText", "DisplayDescription")
            ?? GetFirstMemberValue(secondarySource, "RenderedDescription", "RenderedText", "DisplayDescription")
            ?? boundDescriptionValue,
            $"{path}.rendered",
            textDiagnostics,
            "RenderedDescription",
            "RenderedText",
            "DisplayDescription"));
        var seedVariables = ExtractDescriptionVariablesFromLocString(descriptionValue, "loc_string", secondarySource ?? primarySource)
            .Concat(ExtractDescriptionVariablesFromLocString(boundDescriptionValue, "bound_loc_string", secondarySource ?? primarySource))
            .ToArray();
        var variables = DeduplicateVariables(
            (primarySource is null
                ? Array.Empty<DescriptionVariable>()
                : ExtractDescriptionVariables(primarySource, raw, seedVariables))
            .Concat(secondarySource is null || ReferenceEquals(secondarySource, primarySource)
                ? Array.Empty<DescriptionVariable>()
                : ExtractDescriptionVariables(secondarySource, raw, seedVariables)));
        var renderOutcome = RenderDescription(
            raw,
            rendered,
            variables);
        var canonicalDescription = ChooseCanonicalDescription(renderOutcome.Text, raw);
        if (ContainsDescriptionPlaceholder(canonicalDescription) && !string.IsNullOrWhiteSpace(raw))
        {
            var strippedTemplate = NormalizeDescriptionText(PlaceholderRegex.Replace(raw, string.Empty));
            if (!string.IsNullOrWhiteSpace(strippedTemplate) && !ContainsDescriptionPlaceholder(strippedTemplate))
            {
                canonicalDescription = strippedTemplate;
            }
        }
        canonicalDescription ??= "移除牌组中的一张牌。";
        var glossary = ExtractGlossaryAnchors(
            canonicalId: null,
            displayName: name,
            texts: new[] { canonicalDescription, raw, name },
            keywords: null,
            traits: null,
            path: $"{path}.glossary",
            primarySource,
            secondarySource);

        LogDescriptionDiagnostics(
            kind: "shop_service",
            identifier: "purge_card",
            path: path,
            raw: raw,
            rendered: canonicalDescription,
            quality: renderOutcome.Quality,
            source: renderOutcome.Source,
            variables: variables,
            glossary: glossary);

        return (name.Trim(), canonicalDescription, glossary);
    }

    private object? ResolveShopPayloadNode(object node, IEnumerable<string> memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = GetMemberValue(node, memberName);
            if (value is not null)
            {
                return value;
            }
        }

        foreach (var memberName in new[] { "Model", "Data", "Item", "ItemModel" })
        {
            var container = GetMemberValue(node, memberName);
            if (container is null)
            {
                continue;
            }

            foreach (var nestedName in memberNames)
            {
                var nested = GetMemberValue(container, nestedName);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private int? ResolveShopPrice(object node)
    {
        foreach (var memberName in ShopPriceMemberNames)
        {
            var value = GetMemberValue(node, memberName);
            var price = ConvertToInt32(value);
            if (price is not null)
            {
                return price;
            }
        }

        foreach (var memberName in new[] { "Model", "Data", "Item", "ItemModel" })
        {
            var nested = GetMemberValue(node, memberName);
            if (nested is null)
            {
                continue;
            }

            foreach (var nestedPriceMember in ShopPriceMemberNames)
            {
                var price = ConvertToInt32(GetMemberValue(nested, nestedPriceMember));
                if (price is not null)
                {
                    return price;
                }
            }
        }

        return null;
    }

    private static bool IsShopNodeVisible(object node)
    {
        if (GetMemberValue(node, "Visible") is bool visible && !visible)
        {
            return false;
        }

        if (TryInvokeParameterlessMethod(node, "IsVisibleInTree") is bool visibleInTree && !visibleInTree)
        {
            return false;
        }

        return true;
    }

    private static bool IsShopLeaveLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return ShopLeaveLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeObjectMembersForLog(object? target)
    {
        if (target is null)
        {
            return "<null>";
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var parts = new List<string>();
        foreach (var member in target.GetType().GetMembers(flags)
                     .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
                     .OrderBy(member => member.Name)
                     .Take(24))
        {
            object? value;
            try
            {
                value = GetMemberValue(target, member.Name);
            }
            catch
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            string rendered;
            if (value is string text)
            {
                rendered = AbbreviateForLog(text);
            }
            else if (value is int or long or bool or float or double or decimal)
            {
                rendered = value.ToString() ?? string.Empty;
            }
            else
            {
                rendered = NormalizeTypeName(value) ?? GetTypeName(value) ?? value.ToString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(rendered))
            {
                continue;
            }

            parts.Add($"{member.Name}={rendered}");
        }

        return parts.Count == 0 ? "<no-readable-members>" : string.Join(", ", parts);
    }

    private static bool HasAnyCompatibleMethod(object target, IEnumerable<string> methodNames, params object?[][] argSets)
    {
        foreach (var methodName in methodNames)
        {
            foreach (var args in argSets)
            {
                if (target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(method => string.Equals(method.Name, methodName, StringComparison.Ordinal) && method.GetParameters().Length == args.Length))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string DetectPhase(object runNode, object runState)
    {
        if (GetBoolean(runState, "IsGameOver"))
        {
            return DecisionPhase.Terminal;
        }

        var screenTracker = GetMemberValue(runNode, "ScreenStateTracker");
        if (GetBoolean(screenTracker, "_mapScreenVisible"))
        {
            return DecisionPhase.Map;
        }

        var currentRoomType = GetTypeName(GetMemberValue(runState, "CurrentRoom"));
        if (currentRoomType is not null && currentRoomType.Contains("Map", StringComparison.OrdinalIgnoreCase))
        {
            return DecisionPhase.Map;
        }

        if (AnalyzeRewardPhase(runNode, runState).TreatAsReward)
        {
            return DecisionPhase.Reward;
        }

        if (AnalyzeEventPhase(runNode, runState, textDiagnostics: null).TreatAsEvent)
        {
            return DecisionPhase.Event;
        }

        if (AnalyzeShopPhase(runNode, runState, textDiagnostics: null).TreatAsShop)
        {
            return DecisionPhase.Shop;
        }

        return DecisionPhase.Combat;
    }

    private object? GetRewardScreen(object runNode)
    {
        var screenTracker = GetMemberValue(runNode, "ScreenStateTracker");
        var trackerRewardScreen = GetMemberValue(screenTracker, "_connectedRewardsScreen")
                                  ?? GetMemberValue(screenTracker, "ConnectedRewardsScreen")
                                  ?? GetMemberValue(screenTracker, "_rewardScreen")
                                  ?? GetMemberValue(screenTracker, "RewardScreen");
        if (trackerRewardScreen is not null)
        {
            return trackerRewardScreen;
        }

        var overlayRewardScreen = GetOverlayRewardScreen(runNode);
        if (overlayRewardScreen is not null)
        {
            return overlayRewardScreen;
        }

        return GetMemberValue(GetRewardScreenType(), "Instance");
    }

    private object? GetCardRewardSelectionScreen(object runNode, object? rewardScreen = null)
    {
        rewardScreen ??= GetRewardScreen(runNode);
        var overlayTop = GetOverlayTopScreen(runNode);
        if (overlayTop is null || IsRewardScreenObject(overlayTop))
        {
            return null;
        }

        var typeName = GetTypeName(overlayTop) ?? string.Empty;
        var nameHint = CardRewardSelectionTypeHints.Any(hint => typeName.Contains(hint, StringComparison.OrdinalIgnoreCase));
        var prompt = ResolveCombatSelectionPrompt(overlayTop, textDiagnostics: null);
        if (rewardScreen is null && !typeName.Contains("Reward", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var looksLikeCombatSelection = CombatCardSelectionTypeHints.Any(hint => typeName.Contains(hint, StringComparison.OrdinalIgnoreCase)) &&
                                       !typeName.Contains("Reward", StringComparison.OrdinalIgnoreCase) &&
                                       !string.IsNullOrWhiteSpace(prompt) &&
                                       (prompt.Contains("选择", StringComparison.OrdinalIgnoreCase) ||
                                        prompt.Contains("消耗", StringComparison.OrdinalIgnoreCase) ||
                                        prompt.Contains("弃", StringComparison.OrdinalIgnoreCase) ||
                                        prompt.Contains("select", StringComparison.OrdinalIgnoreCase) ||
                                        prompt.Contains("choose", StringComparison.OrdinalIgnoreCase) ||
                                        prompt.Contains("exhaust", StringComparison.OrdinalIgnoreCase) ||
                                        prompt.Contains("discard", StringComparison.OrdinalIgnoreCase));
        if (looksLikeCombatSelection && rewardScreen is null)
        {
            return null;
        }

        if (!nameHint && rewardScreen is null)
        {
            // Avoid accidentally treating unrelated card screens (deck view/shop/etc.) as reward.
            if (!typeName.Contains("Reward", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var choices = ExtractCardRewardChoiceItems(overlayTop);
        if (choices.Count == 0)
        {
            return null;
        }

        var hasSelectHook = HasAnyMethod(overlayTop, CardRewardChoiceSelectMethodNames) ||
                            choices.Any(choice => HasAnyMethod(choice, CardRewardChoiceSelectMethodNames));
        if (!nameHint && !hasSelectHook)
        {
            return null;
        }

        return overlayTop;
    }

    private CombatSelectionContext? TryBuildCombatSelectionContext(object runNode, object runState, TextDiagnosticsCollector textDiagnostics)
    {
        var selectionResolution = GetCombatCardSelectionResolution(runNode, runState, textDiagnostics);
        if (selectionResolution is null)
        {
            return null;
        }

        var selectionScreen = selectionResolution.Value.SelectionScreen;
        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(selectionScreen));
        if (choices.Count == 0)
        {
            _logger?.Warn(
                $"Combat selection detection rejected source={selectionResolution.Value.DetectionSource} " +
                $"screen={selectionResolution.Value.SelectionScreenType ?? "<unknown>"} reason=no_choices");
            return null;
        }

        var selectionPrompt = ResolveCombatSelectionPrompt(selectionScreen, textDiagnostics);
        if (IsGenericCombatSelectionPrompt(selectionPrompt) &&
            selectionResolution.Value.PromptScreen is not null &&
            !ReferenceEquals(selectionResolution.Value.PromptScreen, selectionScreen))
        {
            var promptFallback = ResolveCombatSelectionPrompt(selectionResolution.Value.PromptScreen, textDiagnostics);
            if (!string.IsNullOrWhiteSpace(promptFallback))
            {
                selectionPrompt = promptFallback;
            }
        }

        var selectionKind = ResolveCombatSelectionKind(selectionScreen, selectionPrompt);
        var actions = new List<RuntimeActionDefinition>(choices.Count + 1);
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            var runtimeCard = BuildCombatSelectionCard(choice, index, textDiagnostics);
            actions.Add(new RuntimeActionDefinition(
                "choose_combat_card",
                $"Choose {runtimeCard.Name}",
                new Dictionary<string, object?>
                {
                    ["selection_index"] = index,
                    ["card_id"] = runtimeCard.CardId,
                    ["card_name"] = runtimeCard.Name,
                },
                Metadata: new Dictionary<string, object?>
                {
                    ["selection_kind"] = selectionKind,
                    ["selection_source"] = selectionResolution.Value.DetectionSource,
                    ["card_preview"] = BuildCardPreview(runtimeCard),
                }));
        }

        var cancelAvailable = HasAnyMethod(selectionScreen, CombatCardChoiceCancelMethodNames);
        string? cancelReason = null;
        if (cancelAvailable)
        {
            actions.Add(new RuntimeActionDefinition(
                "cancel_combat_selection",
                "Cancel Selection",
                new Dictionary<string, object?>(),
                Metadata: new Dictionary<string, object?>
                {
                    ["selection_kind"] = selectionKind,
                    ["selection_source"] = selectionResolution.Value.DetectionSource,
                }));
        }
        else
        {
            cancelReason = "cancel_hook_not_found";
            _logger?.Warn(
                $"Combat selection cancel hook missing source={selectionResolution.Value.DetectionSource} " +
                $"screen={selectionResolution.Value.SelectionScreenType ?? "<unknown>"}");
        }

        return new CombatSelectionContext(
            SelectionScreen: selectionScreen,
            SelectionKind: selectionKind,
            SelectionPrompt: selectionPrompt,
            CancelAvailable: cancelAvailable,
            CancelReason: cancelReason,
            DetectionSource: selectionResolution.Value.DetectionSource,
            SelectionScreenType: selectionResolution.Value.SelectionScreenType,
            PromptSource: selectionResolution.Value.PromptSource,
            Actions: actions);
    }

    private object? GetCombatCardSelectionScreen(object runNode, object runState, TextDiagnosticsCollector? textDiagnostics = null)
    {
        return GetCombatCardSelectionResolution(runNode, runState, textDiagnostics)?.SelectionScreen;
    }

    private CombatSelectionScreenResolution? GetCombatCardSelectionResolution(
        object runNode,
        object runState,
        TextDiagnosticsCollector? textDiagnostics = null)
    {
        if (!BuildEnemies(runState, new TextDiagnosticsCollector()).Enemies.Any(enemy => enemy.IsAlive))
        {
            return null;
        }

        var overlayTop = GetOverlayTopScreen(runNode);
        var overlayTopType = GetTypeName(overlayTop);
        var currentPlayerHand = GetCurrentPlayerHand(runState);
        var staticPlayerHand = GetStaticPlayerHand();
        var activeCurrentPlayerHandSelection = IsActivePlayerHandSelectionScreen(currentPlayerHand, textDiagnostics)
            ? currentPlayerHand
            : null;
        var activeStaticPlayerHandSelection =
            !ReferenceEquals(staticPlayerHand, currentPlayerHand) && IsActivePlayerHandSelectionScreen(staticPlayerHand, textDiagnostics)
                ? staticPlayerHand
                : null;

        if (overlayTop is not null)
        {
            var overlayLooksReward = IsRewardScreenObject(overlayTop);
            var overlayHasSelectionHint = HasCombatCardSelectionTypeHint(overlayTop) ||
                                          HasCombatSelectionPromptHint(ResolveCombatSelectionPrompt(overlayTop, textDiagnostics));
            if ((!overlayLooksReward || overlayHasSelectionHint) &&
                LooksLikeCombatCardSelectionScreen(overlayTop, textDiagnostics))
            {
                return new CombatSelectionScreenResolution(
                    SelectionScreen: overlayTop,
                    PromptScreen: overlayTop,
                    DetectionSource: "overlay_direct",
                    SelectionScreenType: overlayTopType,
                    PromptSource: "selection_screen");
            }

            if (HasCombatCardSelectionTypeHint(overlayTop) &&
                LooksLikeOverlayBackedPlayerHandSelection(staticPlayerHand, textDiagnostics))
            {
                return new CombatSelectionScreenResolution(
                    SelectionScreen: staticPlayerHand!,
                    PromptScreen: overlayTop,
                    DetectionSource: "overlay_assisted_static_player_hand",
                    SelectionScreenType: GetTypeName(staticPlayerHand),
                    PromptSource: "overlay_top");
            }

            if (HasCombatCardSelectionTypeHint(overlayTop) &&
                LooksLikeOverlayBackedPlayerHandSelection(currentPlayerHand, textDiagnostics))
            {
                return new CombatSelectionScreenResolution(
                    SelectionScreen: currentPlayerHand!,
                    PromptScreen: overlayTop,
                    DetectionSource: "overlay_assisted_player_hand",
                    SelectionScreenType: GetTypeName(currentPlayerHand),
                    PromptSource: "overlay_top");
            }

            if (HasCombatCardSelectionTypeHint(overlayTop))
            {
                var playerHandChoiceCount = currentPlayerHand is null
                    ? 0
                    : FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(currentPlayerHand)).Count;
                var staticPlayerHandChoiceCount = staticPlayerHand is null
                    ? 0
                    : FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(staticPlayerHand)).Count;
                _logger?.Warn(
                    $"Combat selection overlay hint rejected overlay={overlayTopType ?? "<unknown>"} " +
                    $"reward_like={overlayLooksReward} " +
                    $"player_hand={GetTypeName(currentPlayerHand) ?? "<none>"} " +
                    $"player_hand_choices={playerHandChoiceCount} " +
                    $"static_player_hand={GetTypeName(staticPlayerHand) ?? "<none>"} " +
                    $"static_player_hand_choices={staticPlayerHandChoiceCount}");
            }
        }

        if (activeCurrentPlayerHandSelection is not null)
        {
            return new CombatSelectionScreenResolution(
                SelectionScreen: activeCurrentPlayerHandSelection,
                PromptScreen: activeCurrentPlayerHandSelection,
                DetectionSource: "player_hand_state",
                SelectionScreenType: GetTypeName(activeCurrentPlayerHandSelection),
                PromptSource: "selection_screen");
        }

        if (activeStaticPlayerHandSelection is not null)
        {
            return new CombatSelectionScreenResolution(
                SelectionScreen: activeStaticPlayerHandSelection,
                PromptScreen: activeStaticPlayerHandSelection,
                DetectionSource: "player_hand_instance",
                SelectionScreenType: GetTypeName(activeStaticPlayerHandSelection),
                PromptSource: "selection_screen");
        }

        return null;
    }

    private object? GetActivePlayerHandSelectionScreen(TextDiagnosticsCollector? textDiagnostics)
    {
        var playerHand = GetStaticPlayerHand();
        return IsActivePlayerHandSelectionScreen(playerHand, textDiagnostics)
            ? playerHand
            : null;
    }

    private object? GetCurrentPlayerHand(object runState)
    {
        var player = GetPlayers(runState).FirstOrDefault();
        var playerCombatState = GetMemberValue(player, "PlayerCombatState");
        return GetMemberValue(playerCombatState, "Hand");
    }

    private object? GetStaticPlayerHand()
    {
        var playerHandType = FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
        return GetMemberValue(playerHandType, "Instance");
    }

    private bool IsActivePlayerHandSelectionScreen(object? playerHand, TextDiagnosticsCollector? textDiagnostics)
    {
        if (playerHand is null ||
            (!GetBoolean(playerHand, "IsInCardSelection") && !GetBoolean(playerHand, "InSelectMode")))
        {
            return false;
        }

        var prompt = ResolveCombatSelectionPrompt(playerHand, textDiagnostics);
        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(playerHand));
        return choices.Count > 0 || !string.IsNullOrWhiteSpace(prompt);
    }

    private bool LooksLikeOverlayBackedPlayerHandSelection(object? playerHand, TextDiagnosticsCollector? textDiagnostics)
    {
        if (playerHand is null || !LooksLikePlayerHandSelectionScreen(playerHand))
        {
            return false;
        }

        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(playerHand));
        if (choices.Count == 0)
        {
            return false;
        }

        var prompt = ResolveCombatSelectionPrompt(playerHand, textDiagnostics);
        return HasCardSelectionHook(playerHand, choices) || !string.IsNullOrWhiteSpace(prompt);
    }

    private bool LooksLikeCombatCardSelectionScreen(object selectionScreen, TextDiagnosticsCollector? textDiagnostics)
    {
        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(selectionScreen));
        if (choices.Count == 0)
        {
            return false;
        }

        var hasSelectHook = HasCardSelectionHook(selectionScreen, choices);
        if (!hasSelectHook)
        {
            return false;
        }

        var typeName = GetTypeName(selectionScreen) ?? string.Empty;
        var prompt = ResolveCombatSelectionPrompt(selectionScreen, textDiagnostics);
        var hasTypeHint = HasCombatCardSelectionTypeHint(typeName);
        var hasPromptHint = HasCombatSelectionPromptHint(prompt);
        return hasTypeHint || hasPromptHint;
    }

    private static bool HasCardSelectionHook(object selectionScreen, IReadOnlyList<object> choices)
    {
        return HasAnyMethod(selectionScreen, CardRewardChoiceSelectMethodNames) ||
               choices.Any(choice => HasAnyMethod(choice, CardRewardChoiceSelectMethodNames));
    }

    private static bool HasCombatCardSelectionTypeHint(object? selectionScreen)
    {
        return HasCombatCardSelectionTypeHint(GetTypeName(selectionScreen));
    }

    private static bool HasCombatCardSelectionTypeHint(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        return CombatCardSelectionTypeHints.Any(hint => typeName.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasCombatSelectionPromptHint(string? prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt) &&
               (prompt.Contains("选择", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("消耗", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("弃", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("select", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("choose", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("exhaust", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("discard", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGenericCombatSelectionPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return true;
        }

        var normalized = NormalizeLabel(prompt);
        return string.Equals(normalized, "hand", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "cards", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "card", StringComparison.OrdinalIgnoreCase);
    }

    private RuntimeCard BuildCombatSelectionCard(object choice, int index, TextDiagnosticsCollector textDiagnostics)
    {
        var card = ResolveCardRewardChoiceCard(choice) ?? choice;
        try
        {
            return BuildRuntimeCard(
                card,
                index,
                "combat_selection_choices",
                textDiagnostics,
                CardDescriptionContext.Hand);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Combat selection card extraction degraded for combat_selection_choices[{index}]: {ex.GetBaseException().Message}");
            return BuildFallbackRuntimeCard(card, index);
        }
    }

    private string? ResolveCombatSelectionPrompt(object selectionScreen, TextDiagnosticsCollector? textDiagnostics)
    {
        var direct = ConvertToText(
            GetFirstMemberValue(selectionScreen, CombatSelectionPromptMembers),
            "combat_selection.prompt",
            textDiagnostics,
            CombatSelectionPromptMembers);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var header = GetMemberValue(selectionScreen, "_selectionHeader") ?? GetMemberValue(selectionScreen, "SelectionHeader");
        return ConvertToText(
            GetFirstMemberValue(header, "Text", "Label", "Title", "Name"),
            "combat_selection.prompt",
            textDiagnostics,
            "Text",
            "Label",
            "Title",
            "Name");
    }

    private static string ResolveCombatSelectionKind(object selectionScreen, string? prompt)
    {
        var text = $"{GetTypeName(selectionScreen)} {prompt}";
        if (text.Contains("消耗", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exhaust", StringComparison.OrdinalIgnoreCase))
        {
            return "exhaust_card";
        }

        if (text.Contains("弃", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("discard", StringComparison.OrdinalIgnoreCase))
        {
            return "discard_card";
        }

        return "choose_card";
    }

    private RewardPhaseAnalysis AnalyzeRewardPhase(object runNode, object runState)
    {
        var rewardScreen = GetRewardScreen(runNode);
        var rewardButtons = GetRewardButtons(rewardScreen).ToArray();
        var hasRewardScreen = rewardScreen is not null;
        var rewardScreenComplete = hasRewardScreen && GetBoolean(rewardScreen, "IsComplete");
        var rewardScreenVisible = hasRewardScreen && IsRewardScreenVisible(runNode, rewardScreen!);
        var hasLiveEnemies = BuildEnemies(runState, new TextDiagnosticsCollector()).Enemies.Any(enemy => enemy.IsAlive);
        var cardRewardSelectionDetected = GetCardRewardSelectionScreen(runNode, rewardScreen) is not null;
        var rewardAdvanceDetected = hasRewardScreen &&
                                    rewardButtons.Length == 0 &&
                                    rewardScreenVisible &&
                                    TryFindRewardAdvanceButton(runNode, rewardScreen, textDiagnostics: null, out _, out _);
        var treatAsReward = hasRewardScreen &&
                            (!rewardScreenComplete ||
                             rewardButtons.Length > 0 ||
                             rewardScreenVisible ||
                             !hasLiveEnemies);
        if (cardRewardSelectionDetected)
        {
            treatAsReward = true;
        }

        var rewardSubphase = "none";
        if (cardRewardSelectionDetected)
        {
            rewardSubphase = "card_reward_selection";
        }
        else if (rewardAdvanceDetected)
        {
            rewardSubphase = "reward_advance";
        }
        else if (hasRewardScreen && rewardButtons.Length == 0 && rewardScreenComplete && rewardScreenVisible)
        {
            rewardSubphase = "reward_transition";
        }
        else if (hasRewardScreen)
        {
            rewardSubphase = "reward_choice";
        }
        var detectionSource = cardRewardSelectionDetected
            ? "overlay_stack.card_reward_selection"
            : rewardAdvanceDetected ? "reward_screen.advance_button"
            : ResolveRewardScreenSource(runNode, rewardScreen);

        return new RewardPhaseAnalysis(
            TreatAsReward: treatAsReward,
            HasRewardScreen: hasRewardScreen,
            RewardScreenComplete: rewardScreenComplete,
            RewardScreenVisible: rewardScreenVisible,
            RewardButtonCount: rewardButtons.Length,
            HasLiveEnemies: hasLiveEnemies,
            RewardScreenSource: ResolveRewardScreenSource(runNode, rewardScreen),
            CardRewardSelectionDetected: cardRewardSelectionDetected,
            AdvanceButtonDetected: rewardAdvanceDetected,
            RewardSubphase: rewardSubphase,
            DetectionSource: detectionSource,
            OverlayTopType: GetTypeName(GetOverlayTopScreen(runNode)));
    }

    private IEnumerable<object> GetRewardButtons(object? rewardScreen)
    {
        return EnumerateObjects(
            GetMemberValue(rewardScreen, "_rewardButtons")
            ?? GetMemberValue(rewardScreen, "RewardButtons")
            ?? GetMemberValue(rewardScreen, "Buttons"));
    }

    private readonly record struct RewardSkipAvailability(bool Available, string? Reason);

    private RewardSkipAvailability ResolveRewardSkipAvailability(object runNode, RewardPhaseAnalysis rewardAnalysis)
    {
        if (string.Equals(rewardAnalysis.RewardSubphase, "card_reward_selection", StringComparison.Ordinal))
        {
            var cardRewardScreen = GetCardRewardSelectionScreen(runNode);
            if (cardRewardScreen is null)
            {
                return new RewardSkipAvailability(false, "card_reward_screen_missing");
            }

            return HasAnyMethod(cardRewardScreen, CardRewardChoiceSkipMethodNames)
                ? new RewardSkipAvailability(true, null)
                : new RewardSkipAvailability(false, "skip_hook_not_found");
        }

        return new RewardSkipAvailability(true, null);
    }

    private bool TryBuildRewardAdvanceAction(
        object runNode,
        TextDiagnosticsCollector textDiagnostics,
        out RuntimeActionDefinition action,
        out IReadOnlyDictionary<string, object?> metadata)
    {
        if (!TryFindRewardAdvanceButton(runNode, GetRewardScreen(runNode), textDiagnostics, out var buttonNode, out var buttonLabel))
        {
            action = default!;
            metadata = new Dictionary<string, object?>
            {
                ["reward_advance_available"] = false,
            };
            return false;
        }

        action = new RuntimeActionDefinition(
            "advance_reward",
            buttonLabel,
            new Dictionary<string, object?>
            {
                ["button_label"] = buttonLabel,
            });
        metadata = new Dictionary<string, object?>
        {
            ["reward_advance_available"] = true,
            ["reward_advance_label"] = buttonLabel,
            ["reward_advance_target_type"] = GetTypeName(buttonNode),
        };
        return true;
    }

    private bool TryFindRewardAdvanceButton(
        object runNode,
        object? rewardScreen,
        TextDiagnosticsCollector? textDiagnostics,
        out object buttonNode,
        out string buttonLabel)
    {
        if (rewardScreen is null)
        {
            buttonNode = default!;
            buttonLabel = string.Empty;
            return false;
        }

        foreach (var candidate in EnumerateNodeDescendants(rewardScreen, maxDepth: 7).Prepend(rewardScreen))
        {
            if (!IsPotentialMenuButton(candidate))
            {
                continue;
            }

            if (!IsMenuNodeInteractable(candidate))
            {
                continue;
            }

            var label = GetMenuNodeLabel(candidate, textDiagnostics);
            if (string.IsNullOrWhiteSpace(label) || !IsRewardAdvanceLabel(label))
            {
                continue;
            }

            buttonNode = candidate;
            buttonLabel = label.Trim();
            return true;
        }

        buttonNode = default!;
        buttonLabel = string.Empty;
        return false;
    }

    private static bool IsRewardAdvanceLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return RewardAdvanceLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private EventPhaseAnalysis AnalyzeEventPhase(object runNode, object runState, TextDiagnosticsCollector? textDiagnostics)
    {
        var currentRoomType = GetTypeName(GetMemberValue(runState, "CurrentRoom")) ?? string.Empty;
        var hasEventRoomType = currentRoomType.Contains("EventRoom", StringComparison.OrdinalIgnoreCase);
        var eventRoom = GetEventRoom(runNode, runState);
        var eventLayout = GetEventLayout(eventRoom);
        var eventModel = GetEventModel(eventRoom, eventLayout);
        var cardSelection = TryBuildEventCardSelectionContext(runNode, textDiagnostics);
        var options = cardSelection?.Options ?? ExtractEventOptions(eventRoom, eventLayout, eventModel, textDiagnostics);
        var title = ResolveEventTitle(eventLayout, eventModel, textDiagnostics);
        var body = ResolveEventBody(eventLayout, eventModel, textDiagnostics);
        if (cardSelection is not null)
        {
            title ??= cardSelection.Value.Title;
            body = JoinDistinctDescriptionText(cardSelection.Value.Body, body);
        }
        var hasLiveEnemies = BuildEnemies(runState, new TextDiagnosticsCollector()).Enemies.Any(enemy => enemy.IsAlive);
        var hasVisibleContent = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body) || options.Count > 0;
        var treatAsEvent = !hasLiveEnemies &&
                           (hasEventRoomType || eventRoom is not null || cardSelection is not null) &&
                           (hasVisibleContent || HasEventProceedHook(eventRoom));
        var hasChoiceOption = options.Any(option => option.Available && !option.IsContinue);
        var hasContinueOption = options.Any(option => option.Available && option.IsContinue);
        if (!hasContinueOption && HasEventProceedHook(eventRoom) && treatAsEvent)
        {
            hasContinueOption = true;
        }

        var windowKind = treatAsEvent
            ? hasChoiceOption ? "event_choice" : hasContinueOption ? "event_continue" : "event_transition"
            : "none";
        var continueLabel = options
            .Where(option => option.IsContinue)
            .Select(option => option.Label)
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
            ?? "继续";
        var detectionSource = cardSelection?.DetectionSource
            ?? (eventLayout is not null && options.Count > 0
                ? "event_layout.option_buttons"
                : eventModel is not null && options.Count > 0
                    ? "event_model.current_options"
                    : eventRoom is not null
                        ? "event_room.instance"
                        : hasEventRoomType ? "run_state.current_room" : "none");
        var diagnostics = !treatAsEvent
            ? hasLiveEnemies ? "live_enemies_present" : "event_ui_not_ready"
            : options.Count == 0 && !hasContinueOption ? "no_clickable_event_actions" : null;
        var eventSubphase = cardSelection is not null ? "card_selection" : null;

        return new EventPhaseAnalysis(
            TreatAsEvent: treatAsEvent,
            EventRoom: eventRoom,
            EventLayout: eventLayout,
            EventModel: eventModel,
            Title: title,
            Body: body,
            Options: options,
            ContinueAvailable: hasContinueOption,
            ContinueLabel: continueLabel,
            WindowKind: windowKind,
            DetectionSource: detectionSource,
            Diagnostics: diagnostics,
            EventSubphase: eventSubphase,
            SelectionPrompt: cardSelection?.Prompt);
    }

    private object? GetEventRoom(object runNode, object runState)
    {
        var currentRoomType = GetTypeName(GetMemberValue(runState, "CurrentRoom")) ?? string.Empty;
        var eventRoom = GetMemberValue(runNode, "EventRoom")
                        ?? GetMemberValue(GetEventRoomType(), "Instance");
        if (eventRoom is null)
        {
            return null;
        }

        if (currentRoomType.Contains("EventRoom", StringComparison.OrdinalIgnoreCase))
        {
            return eventRoom;
        }

        return IsEventRoomVisible(eventRoom) ? eventRoom : null;
    }

    private Type? GetEventRoomType()
    {
        return FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom");
    }

    private static object? GetEventLayout(object? eventRoom)
    {
        return GetMemberValue(eventRoom, "Layout")
               ?? GetMemberValue(eventRoom, "_layout")
               ?? GetMemberValue(eventRoom, "CustomEventNode");
    }

    private static object? GetEventModel(object? eventRoom, object? eventLayout)
    {
        return GetMemberValue(eventRoom, "_event")
               ?? GetMemberValue(eventRoom, "Event")
               ?? GetMemberValue(eventLayout, "_event")
               ?? GetMemberValue(eventLayout, "Event");
    }

    private EventCardSelectionContext? TryBuildEventCardSelectionContext(object runNode, TextDiagnosticsCollector? textDiagnostics)
    {
        var overlayTop = GetOverlayTopScreen(runNode);
        if (overlayTop is null || !LooksLikeEventCardSelectionScreen(overlayTop, textDiagnostics))
        {
            return null;
        }

        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(overlayTop));
        if (choices.Count == 0)
        {
            return null;
        }

        var options = choices
            .Select((choice, index) => BuildEventOptionFromCardSelectionChoice(overlayTop, choice, index, textDiagnostics))
            .ToArray();
        var title = ResolveEventCardSelectionTitle(overlayTop, textDiagnostics);
        var body = ResolveEventCardSelectionBody(overlayTop, textDiagnostics);
        return new EventCardSelectionContext(
            SelectionScreen: overlayTop,
            Title: title,
            Body: body,
            Prompt: body,
            DetectionSource: "overlay_top.event_card_selection",
            Options: options);
    }

    private List<EventOptionAnalysis> ExtractEventOptions(
        object? eventRoom,
        object? eventLayout,
        object? eventModel,
        TextDiagnosticsCollector? textDiagnostics)
    {
        var optionButtons = EnumerateObjects(GetMemberValue(eventLayout, "OptionButtons")).ToList();
        if (optionButtons.Count > 0)
        {
            return optionButtons
                .Select((button, index) => BuildEventOptionFromButton(button, index, textDiagnostics))
                .ToList();
        }

        var options = EnumerateObjects(
                GetMemberValue(eventModel, "CurrentOptions")
                ?? GetMemberValue(eventRoom, "_connectedOptions")
                ?? GetMemberValue(eventRoom, "ConnectedOptions"))
            .ToList();
        return options
            .Select((option, index) => BuildEventOptionFromModel(option, index, textDiagnostics))
            .ToList();
    }

    private EventOptionAnalysis BuildEventOptionFromCardSelectionChoice(
        object selectionScreen,
        object choice,
        int index,
        TextDiagnosticsCollector? textDiagnostics)
    {
        var collector = textDiagnostics ?? new TextDiagnosticsCollector();
        var card = ResolveCardRewardChoiceCard(choice) ?? choice;
        RuntimeCard runtimeCard;
        try
        {
            runtimeCard = BuildRuntimeCard(
                card,
                index,
                "event_selection_choices",
                collector,
                CardDescriptionContext.Unknown);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Event card selection extraction degraded for event_selection_choices[{index}]: {ex.GetBaseException().Message}");
            runtimeCard = BuildFallbackRuntimeCard(card, index);
        }

        var label = string.IsNullOrWhiteSpace(runtimeCard.Name) ? $"事件卡牌 {index + 1}" : runtimeCard.Name.Trim();
        return new EventOptionAnalysis(
            Index: index,
            Label: label,
            Available: true,
            IsContinue: false,
            OptionObject: choice,
            ButtonNode: null,
            DetectionSource: "overlay_top.event_card_selection",
            SelectionScreen: selectionScreen,
            SelectionValue: card,
            StableId: runtimeCard.CardId,
            PreviewText: runtimeCard.Description,
            Description: runtimeCard.Description,
            Keywords: runtimeCard.Keywords,
            Glossary: runtimeCard.Glossary);
    }

    private EventOptionAnalysis BuildEventOptionFromButton(object button, int index, TextDiagnosticsCollector? textDiagnostics)
    {
        var option = GetMemberValue(button, "Option");
        var label = DescribeEventOptionLabel(button, option, index, textDiagnostics);
        var description = ResolveEventOptionDescription(button, option, label, index, textDiagnostics);
        var glossary = FilterEventOptionGlossary(
            label,
            description,
            $"{BuildEventOptionPath(index)}.glossary",
            ExtractEventOptionGlossary(
                label,
                description,
                BuildEventOptionPath(index),
                button,
                option,
                GetMemberValue(button, "Option"),
                GetMemberValue(option, "Description"),
                GetMemberValue(button, "_label"),
                GetMemberValue(button, "Label"),
                GetMemberValue(button, "Text"),
                GetFirstMemberValue(option, "Description", "TooltipText", "HoverTips", "HoverTip", "ExtraHoverTips")));
        var keywords = glossary
            .Select(anchor => anchor.GlossaryId)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var isLocked = GetBoolean(option, "IsLocked") || !IsMenuNodeInteractable(button);
        var isContinue = GetBoolean(option, "IsProceed") || IsEventContinueLabel(label);
        return new EventOptionAnalysis(
            Index: GetNullableInt(button, "Index") ?? index,
            Label: label,
            Available: !isLocked,
            IsContinue: isContinue,
            OptionObject: option,
            ButtonNode: button,
            DetectionSource: "event_layout.option_buttons",
            PreviewText: description,
            Description: description,
            Keywords: keywords,
            Glossary: glossary);
    }

    private EventOptionAnalysis BuildEventOptionFromModel(object option, int index, TextDiagnosticsCollector? textDiagnostics)
    {
        var label = DescribeEventOptionLabel(button: null, option, index, textDiagnostics);
        var description = ResolveEventOptionDescription(button: null, option, label, index, textDiagnostics);
        var glossary = FilterEventOptionGlossary(
            label,
            description,
            $"{BuildEventOptionPath(index)}.glossary",
            ExtractEventOptionGlossary(
                label,
                description,
                BuildEventOptionPath(index),
                option,
                GetMemberValue(option, "Description"),
                GetMemberValue(option, "Title"),
                GetMemberValue(option, "TooltipText"),
                GetFirstMemberValue(option, "HoverTips", "HoverTip", "ExtraHoverTips")));
        var keywords = glossary
            .Select(anchor => anchor.GlossaryId)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var isLocked = GetBoolean(option, "IsLocked");
        var isContinue = GetBoolean(option, "IsProceed") || IsEventContinueLabel(label);
        return new EventOptionAnalysis(
            Index: index,
            Label: label,
            Available: !isLocked,
            IsContinue: isContinue,
            OptionObject: option,
            ButtonNode: null,
            DetectionSource: "event_model.current_options",
            PreviewText: description,
            Description: description,
            Keywords: keywords,
            Glossary: glossary);
    }

    private string? ResolveEventOptionDescription(
        object? button,
        object? option,
        string label,
        int index,
        TextDiagnosticsCollector? textDiagnostics)
    {
        var description = NormalizeDescriptionText(ConvertDescriptionTemplateToText(
            GetFirstMemberValue(
                option,
                "Description",
                "RulesText",
                "TooltipText",
                "PreviewText",
                "Body",
                "BodyText",
                "Detail",
                "Details",
                "Text")
            ?? GetFirstMemberValue(
                button,
                "Description",
                "TooltipText",
                "PreviewText",
                "Body",
                "BodyText",
                "Detail",
                "Details",
                "Text"),
            $"{BuildEventOptionPath(index)}.detail",
            textDiagnostics,
            "Description",
            "RulesText",
            "TooltipText",
            "PreviewText",
            "Body",
            "BodyText",
            "Detail",
            "Details",
            "Text",
            "ParsedText",
            "BbcodeText"));
        if (!string.IsNullOrWhiteSpace(description) && !ContainsDescriptionPlaceholder(description))
        {
            return description;
        }

        var derived = DeriveEventOptionDescriptionFromLabel(label);
        if (!string.IsNullOrWhiteSpace(derived))
        {
            return derived;
        }

        return description;
    }

    private static string? DeriveEventOptionDescriptionFromLabel(string? label)
    {
        var normalized = NormalizeDescriptionText(label);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length >= 2)
        {
            return string.Join('\n', lines.Skip(1));
        }

        return normalized;
    }

    private IReadOnlyList<GlossaryAnchor> ExtractEventOptionGlossary(
        string? label,
        string? description,
        string path,
        params object?[] hintSources)
    {
        var anchors = new List<GlossaryAnchor>(
            ExtractGlossaryAnchors(
                canonicalId: null,
                displayName: null,
                texts: new[] { label, description },
                keywords: null,
                traits: null,
                path,
                hintSources));
        var seen = new HashSet<string>(
            anchors.Select(anchor => anchor.GlossaryId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var hoverTip in EnumerateGlossaryHoverTips(hintSources))
        {
            var tipId = ConvertToText(GetMemberValue(hoverTip, "Id"), "Id");
            var tipTitle = NormalizeDescriptionText(ConvertToText(
                GetMemberValue(hoverTip, "Title")
                ?? GetMemberValue(hoverTip, "Name")
                ?? GetMemberValue(hoverTip, "Label"),
                path: "event.option.hover_tip_title",
                textDiagnostics: null,
                preferredMembers: new[] { "Title", "Name", "Label", "Text" }));
            var normalizedTipId = NormalizeGlossaryId(tipId);
            if (string.IsNullOrWhiteSpace(normalizedTipId) && !string.IsNullOrWhiteSpace(tipTitle))
            {
                normalizedTipId = NormalizeGlossaryId(Regex.Replace(tipTitle, @"\d+$", string.Empty));
            }
            else if (LooksLikeSyntheticGlossaryId(normalizedTipId) && !string.IsNullOrWhiteSpace(tipTitle))
            {
                normalizedTipId = NormalizeGlossaryId(Regex.Replace(tipTitle, @"\d+$", string.Empty));
            }

            if (string.IsNullOrWhiteSpace(normalizedTipId) || string.IsNullOrWhiteSpace(tipTitle) || !seen.Add(normalizedTipId))
            {
                continue;
            }

            anchors.Add(BuildGlossaryAnchor(
                new GlossaryCandidate(normalizedTipId, tipTitle, "runtime_hover_tip"),
                path,
                hintSources));
        }

        return anchors;
    }

    private IReadOnlyList<GlossaryAnchor> FilterEventOptionGlossary(
        string label,
        string? description,
        string path,
        IReadOnlyList<GlossaryAnchor> glossary)
    {
        if (glossary.Count == 0)
        {
            return glossary;
        }

        var filtered = new List<GlossaryAnchor>(glossary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in glossary)
        {
            var reason = ClassifyEventOptionGlossaryFilterReason(anchor);
            if (reason is not null)
            {
                LogEventOptionGlossaryFilter(label, path, anchor, reason, description);
                continue;
            }

            var dedupeKey = string.Join("|",
                NormalizeComparisonText(anchor.GlossaryId),
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!seen.Add(dedupeKey))
            {
                LogEventOptionGlossaryFilter(label, path, anchor, "duplicate_glossary", description);
                continue;
            }

            filtered.Add(anchor);
        }

        return filtered;
    }

    private static string? ClassifyEventOptionGlossaryFilterReason(GlossaryAnchor anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor.Hint))
        {
            return "empty_hint";
        }

        if (string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_hint";
        }

        if (string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback_builtin";
        }

        if (ContainsDescriptionPlaceholder(anchor.Hint))
        {
            return "template_hint";
        }

        return null;
    }

    private void LogEventOptionGlossaryFilter(string label, string path, GlossaryAnchor anchor, string reason, string? description)
    {
        var message =
            $"Event option glossary filtered label=\"{AbbreviateForLog(label)}\" path={path} glossary_id={anchor.GlossaryId} " +
            $"display_text={anchor.DisplayText} source={anchor.Source ?? "unknown"} reason={reason} " +
            $"description=\"{AbbreviateForLog(description)}\" hint=\"{AbbreviateForLog(anchor.Hint)}\"";
        _logger?.Warn(message);
    }

    private static string BuildEventOptionPath(int index)
    {
        return $"event.options[{index}]";
    }

    private static bool LooksLikeSyntheticGlossaryId(string? glossaryId)
    {
        if (string.IsNullOrWhiteSpace(glossaryId))
        {
            return false;
        }

        return glossaryId.StartsWith("locstring", StringComparison.OrdinalIgnoreCase) ||
               glossaryId.StartsWith("description", StringComparison.OrdinalIgnoreCase);
    }

    private string DescribeEventOptionLabel(object? button, object? option, int index, TextDiagnosticsCollector? textDiagnostics)
    {
        var label = NormalizeDescriptionText(ConvertToText(
            GetMemberValue(button, "_label") ?? GetMemberValue(button, "Label") ?? GetMemberValue(button, "Text"),
            $"event.options[{index}].button",
            textDiagnostics,
            "Text",
            "Label",
            "BbcodeText",
            "ParsedText",
            "AccessibilityName"));
        label ??= NormalizeDescriptionText(ConvertDescriptionTemplateToText(
            GetMemberValue(option, "Description"),
            $"event.options[{index}].description",
            textDiagnostics,
            "Description",
            "Text"));
        label ??= NormalizeDescriptionText(ConvertDescriptionTemplateToText(
            GetMemberValue(option, "Title"),
            $"event.options[{index}].title",
            textDiagnostics,
            "Title",
            "Text"));
        if (string.IsNullOrWhiteSpace(label) && GetBoolean(option, "IsProceed"))
        {
            label = "继续";
        }

        return string.IsNullOrWhiteSpace(label) ? $"事件选项 {index + 1}" : label.Trim();
    }

    private string? ResolveEventTitle(object? eventLayout, object? eventModel, TextDiagnosticsCollector? textDiagnostics)
    {
        return NormalizeDescriptionText(ConvertToText(
                   GetMemberValue(eventLayout, "_title") ?? GetMemberValue(eventLayout, "Title"),
                   "event.title",
                   textDiagnostics,
                   "Text",
                   "Label",
                   "Title"))
               ?? NormalizeDescriptionText(ConvertDescriptionTemplateToText(
                   GetMemberValue(eventModel, "Title"),
                   "event.title_model",
                   textDiagnostics,
                   "Title",
                   "Text"));
    }

    private string? ResolveEventBody(object? eventLayout, object? eventModel, TextDiagnosticsCollector? textDiagnostics)
    {
        return NormalizeDescriptionText(ConvertToText(
                   GetMemberValue(eventLayout, "_description") ?? GetMemberValue(eventLayout, "Description"),
                   "event.body",
                   textDiagnostics,
                   "Text",
                   "ParsedText",
                   "BbcodeText"))
               ?? NormalizeDescriptionText(ConvertDescriptionTemplateToText(
                   GetMemberValue(eventModel, "Description"),
                   "event.body_model",
                   textDiagnostics,
                   "Description",
                   "Text"));
    }

    private bool LooksLikeEventCardSelectionScreen(object selectionScreen, TextDiagnosticsCollector? textDiagnostics)
    {
        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(selectionScreen));
        if (choices.Count == 0)
        {
            return false;
        }

        var typeName = GetTypeName(selectionScreen) ?? string.Empty;
        if (!HasCardSelectionHook(selectionScreen, choices))
        {
            return false;
        }

        return EventCardSelectionTypeHints.Any(hint => typeName.Contains(hint, StringComparison.OrdinalIgnoreCase)) ||
               GetMemberValue(selectionScreen, "_enchantment") is not null ||
               GetMemberValue(selectionScreen, "Enchantment") is not null ||
               GetMemberValue(selectionScreen, "_enchantmentTitle") is not null ||
               GetMemberValue(selectionScreen, "EnchantmentTitle") is not null ||
               !string.IsNullOrWhiteSpace(ResolveEventCardSelectionBody(selectionScreen, textDiagnostics));
    }

    private string? ResolveEventCardSelectionTitle(object selectionScreen, TextDiagnosticsCollector? textDiagnostics)
    {
        return NormalizeDescriptionText(ConvertToText(
                   GetMemberValue(selectionScreen, "_enchantmentTitle")
                   ?? GetMemberValue(selectionScreen, "EnchantmentTitle")
                   ?? GetMemberValue(selectionScreen, "_title")
                   ?? GetMemberValue(selectionScreen, "Title"),
                   "event.card_selection.title",
                   textDiagnostics,
                   "_enchantmentTitle",
                   "EnchantmentTitle",
                   "_title",
                   "Title"));
    }

    private string? ResolveEventCardSelectionBody(object selectionScreen, TextDiagnosticsCollector? textDiagnostics)
    {
        var info = NormalizeDescriptionText(ConvertToText(
            GetMemberValue(selectionScreen, "_infoLabel") ?? GetMemberValue(selectionScreen, "InfoLabel"),
            "event.card_selection.info",
            textDiagnostics,
            "_infoLabel",
            "InfoLabel",
            "Text",
            "ParsedText",
            "BbcodeText"));
        var description = NormalizeDescriptionText(ConvertToText(
            GetMemberValue(selectionScreen, "_enchantmentDescription") ?? GetMemberValue(selectionScreen, "EnchantmentDescription"),
            "event.card_selection.description",
            textDiagnostics,
            "_enchantmentDescription",
            "EnchantmentDescription",
            "Text",
            "ParsedText",
            "BbcodeText"));
        return JoinDistinctDescriptionText(info, description);
    }

    private static string? JoinDistinctDescriptionText(params string?[] parts)
    {
        var normalized = parts
            .Select(NormalizeDescriptionText)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length == 0 ? null : string.Join("\n", normalized);
    }

    private static bool IsEventRoomVisible(object? eventRoom)
    {
        return GetBoolean(eventRoom, "Visible") ||
               GetBoolean(eventRoom, "IsVisible") ||
               InvokeBooleanMethod(eventRoom, "IsVisibleInTree");
    }

    private static bool HasEventProceedHook(object? eventRoom)
    {
        if (eventRoom is null)
        {
            return false;
        }

        return eventRoom.GetType().GetMethod("Proceed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;
    }

    private static bool IsEventContinueLabel(string label)
    {
        var normalized = NormalizeLabel(label);
        return EventContinueLabelHints.Any(hint => normalized.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private List<object> ExtractCardRewardChoiceItems(object cardRewardScreen)
    {
        var holders = ExtractCardRewardChoiceHoldersFromRow(cardRewardScreen);
        if (holders.Count > 0)
        {
            return holders;
        }

        foreach (var memberName in CardRewardChoiceCollectionMembers)
        {
            var value = GetMemberValue(cardRewardScreen, memberName);
            var items = EnumerateObjects(value).ToList();
            if (items.Count > 0)
            {
                return items;
            }
        }

        // Some screens keep the choice list nested under another node.
        var nestedContainers = new[]
        {
            GetMemberValue(cardRewardScreen, "CardGrid"),
            GetMemberValue(cardRewardScreen, "_cardGrid"),
            GetMemberValue(cardRewardScreen, "Grid"),
            GetMemberValue(cardRewardScreen, "_grid"),
            GetMemberValue(cardRewardScreen, "Selection"),
            GetMemberValue(cardRewardScreen, "_selection"),
            GetMemberValue(cardRewardScreen, "CardSelection"),
            GetMemberValue(cardRewardScreen, "_cardSelection"),
            GetMemberValue(cardRewardScreen, "CardSelectionContainer"),
            GetMemberValue(cardRewardScreen, "_cardSelectionContainer"),
            GetMemberValue(cardRewardScreen, "ChoiceContainer"),
            GetMemberValue(cardRewardScreen, "_choiceContainer"),
            GetMemberValue(cardRewardScreen, "CardsContainer"),
            GetMemberValue(cardRewardScreen, "_cardsContainer"),
        };
        foreach (var container in nestedContainers.Where(container => container is not null))
        {
            holders = ExtractCardRewardChoiceHoldersFromContainer(container!);
            if (holders.Count > 0)
            {
                return holders;
            }

            foreach (var memberName in CardRewardChoiceCollectionMembers)
            {
                var value = GetMemberValue(container, memberName);
                var items = EnumerateObjects(value).ToList();
                if (items.Count > 0)
                {
                    return items;
                }
            }
        }

        var scannedChoices = ExtractLikelyCardChoiceDescendants(cardRewardScreen);
        if (scannedChoices.Count > 0)
        {
            return scannedChoices;
        }

        return new List<object>();
    }

    private List<object> ExtractCardRewardChoiceHoldersFromRow(object cardRewardScreen)
    {
        var cardRow = GetMemberValue(cardRewardScreen, "_cardRow") ?? GetMemberValue(cardRewardScreen, "CardRow");
        if (cardRow is not null)
        {
            var holders = ExtractCardRewardChoiceHoldersFromContainer(cardRow);
            if (holders.Count > 0)
            {
                return holders;
            }
        }

        return ExtractCardRewardChoiceHoldersFromContainer(cardRewardScreen);
    }

    private List<object> ExtractCardRewardChoiceHoldersFromContainer(object container)
    {
        var cardHolderType = FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder");
        var holders = EnumerateNodeDescendants(container, maxDepth: 10)
            .Where(child =>
            {
                var typeName = GetTypeName(child) ?? string.Empty;
                if (cardHolderType is not null && cardHolderType.IsInstanceOfType(child))
                {
                    // Filter out non-card holders (e.g. hitboxes) that still derive from NCardHolder.
                    return GetMemberValue(child, "CardModel") is not null || GetMemberValue(child, "CardNode") is not null;
                }

                if (typeName.Contains("NCardHolder", StringComparison.Ordinal) ||
                    string.Equals(child.GetType().Name, "NCardHolder", StringComparison.Ordinal))
                {
                    return true;
                }

                return false;
            })
            .ToList();
        if (holders.Count > 0)
        {
            return holders;
        }

        return ExtractLikelyCardChoiceDescendants(container);
    }

    private List<object> ExtractLikelyCardChoiceDescendants(object container)
    {
        return EnumerateNodeDescendants(container, maxDepth: 10)
            .Where(child =>
            {
                if (LooksLikeCardHolder(child))
                {
                    return true;
                }

                var card = ResolveCardRewardChoiceCard(child);
                if (card is not null && LooksLikeCardModel(card))
                {
                    return true;
                }

                return LooksLikeCardModel(child);
            })
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToList();
    }

    private IEnumerable<object> EnumerateNodeDescendants(object root, int maxDepth)
    {
        if (maxDepth <= 0)
        {
            yield break;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, int Depth)>();
        queue.Enqueue((root, 0));
        visited.Add(root);

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in EnumerateNodeChildren(node))
            {
                if (child is null)
                {
                    continue;
                }

                yield return child;
                if (!child.GetType().IsValueType && visited.Add(child))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    private IEnumerable<object> EnumerateNodeChildren(object node)
    {
        var type = node.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Prefer GetChildren overloads when available.
        var method = type.GetMethod("GetChildren", flags, null, Type.EmptyTypes, null);
        if (method is not null)
        {
            object? value = null;
            try
            {
                value = method.Invoke(node, null);
            }
            catch
            {
                value = null;
            }

            foreach (var child in EnumerateObjects(value))
            {
                yield return child;
            }
            yield break;
        }

        method = type.GetMethod("GetChildren", flags, null, new[] { typeof(bool) }, null);
        if (method is not null)
        {
            object? value = null;
            try
            {
                // STS2 uses Godot 4 which may place important UI nodes under internal children.
                value = method.Invoke(node, new object?[] { true });
            }
            catch
            {
                value = null;
            }

            foreach (var child in EnumerateObjects(value))
            {
                yield return child;
            }
            yield break;
        }

        // Fallback: enumerate GetChildCount/GetChild.
        var getChildCount = type.GetMethod("GetChildCount", flags, null, Type.EmptyTypes, null)
                           ?? type.GetMethod("GetChildCount", flags, null, new[] { typeof(bool) }, null);
        var getChild = type.GetMethod("GetChild", flags, null, new[] { typeof(int), typeof(bool) }, null)
                     ?? type.GetMethod("GetChild", flags, null, new[] { typeof(int) }, null);
        if (getChildCount is null || getChild is null)
        {
            yield break;
        }

        int count;
        try
        {
            count = getChildCount.GetParameters().Length == 0
                ? Convert.ToInt32(getChildCount.Invoke(node, null))
                : Convert.ToInt32(getChildCount.Invoke(node, new object?[] { true }));
        }
        catch
        {
            yield break;
        }

        for (var index = 0; index < count; index++)
        {
            object? child = null;
            try
            {
                child = getChild.GetParameters().Length == 1
                    ? getChild.Invoke(node, new object?[] { index })
                    : getChild.Invoke(node, new object?[] { index, true });
            }
            catch
            {
                child = null;
            }

            if (child is not null)
            {
                yield return child;
            }
        }
    }

    private static object? ResolveCardRewardChoiceCard(object choice)
    {
        foreach (var memberName in CardRewardChoiceCardMembers)
        {
            var value = GetMemberValue(choice, memberName);
            if (value is not null)
            {
                return value;
            }
        }

        return choice;
    }

    private static bool HasAnyMethod(object target, IEnumerable<string> methodNames)
    {
        var type = target.GetType();
        foreach (var name in methodNames)
        {
            if (type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryInvokeFirstCompatibleMethod(
        object target,
        IEnumerable<string> methodNames,
        IReadOnlyList<object?[]> argCandidates,
        out string? invokedMethod)
    {
        invokedMethod = null;
        var type = target.GetType();
        foreach (var name in methodNames)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
                .ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            foreach (var method in methods)
            {
                foreach (var args in argCandidates)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != args.Length)
                    {
                        continue;
                    }

                    try
                    {
                        method.Invoke(target, args);
                        invokedMethod = method.Name;
                        return true;
                    }
                    catch
                    {
                        // Continue probing other signatures/argument sets.
                    }
                }
            }
        }

        return false;
    }

    private static bool IsRewardScreenVisible(object runNode, object rewardScreen)
    {
        var screenTracker = GetMemberValue(runNode, "ScreenStateTracker");
        return GetBoolean(screenTracker, "_rewardScreenVisible") ||
               GetBoolean(screenTracker, "RewardScreenVisible") ||
               GetBoolean(rewardScreen, "Visible") ||
               GetBoolean(rewardScreen, "IsVisible") ||
               InvokeBooleanMethod(rewardScreen, "IsVisibleInTree");
    }

    private object? GetOverlayRewardScreen(object runNode)
    {
        var overlayStack = GetOverlayStack(runNode);
        var overlayScreen = TryInvokeParameterlessMethod(overlayStack, "Peek");
        return IsRewardScreenObject(overlayScreen) ? overlayScreen : null;
    }

    private object? GetOverlayStack(object runNode)
    {
        var globalUi = GetMemberValue(runNode, "GlobalUi");
        return GetMemberValue(globalUi, "Overlays")
               ?? GetMemberValue(GetOverlayStackType(), "Instance");
    }

    private Type? GetOverlayStackType()
    {
        return FindSts2Assembly()?.GetType(OverlayStackTypeName);
    }

    private Type? GetRewardScreenType()
    {
        return FindSts2Assembly()?.GetType(RewardScreenTypeName);
    }

    private string ResolveRewardScreenSource(object runNode, object? rewardScreen)
    {
        if (rewardScreen is null)
        {
            return "none";
        }

        var screenTracker = GetMemberValue(runNode, "ScreenStateTracker");
        var trackerRewardScreen = GetMemberValue(screenTracker, "_connectedRewardsScreen")
                                  ?? GetMemberValue(screenTracker, "ConnectedRewardsScreen")
                                  ?? GetMemberValue(screenTracker, "_rewardScreen")
                                  ?? GetMemberValue(screenTracker, "RewardScreen");
        if (ReferenceEquals(trackerRewardScreen, rewardScreen))
        {
            return "screen_state_tracker";
        }

        var overlayRewardScreen = GetOverlayRewardScreen(runNode);
        if (ReferenceEquals(overlayRewardScreen, rewardScreen))
        {
            return "overlay_stack";
        }

        if (ReferenceEquals(GetMemberValue(GetRewardScreenType(), "Instance"), rewardScreen))
        {
            return "reward_screen_instance";
        }

        return "other";
    }

    private static bool IsRewardScreenObject(object? target)
    {
        if (target is null)
        {
            return false;
        }

        var typeName = GetTypeName(target);
        return string.Equals(typeName, RewardScreenTypeName, StringComparison.Ordinal) ||
               string.Equals(target.GetType().Name, "NRewardsScreen", StringComparison.Ordinal) ||
               GetMemberValue(target, "_rewardButtons") is not null ||
               GetMemberValue(target, "RewardButtons") is not null;
    }

    private object? GetCombatState(object runState)
    {
        var player = GetPlayers(runState).FirstOrDefault();
        var creature = GetMemberValue(player, "Creature");
        return GetMemberValue(creature, "CombatState");
    }

    private IReadOnlyList<object> GetPlayers(object? runState)
    {
        return EnumerateObjects(GetMemberValue(runState, "Players")).ToArray();
    }

    private Assembly? FindSts2Assembly()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, Sts2AssemblyName, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveCardCost(object? card)
    {
        if (GetBoolean(card, "HasEnergyCostX"))
        {
            return -1;
        }

        return GetNullableInt(card, "CanonicalEnergyCost")
               ?? GetNullableInt(card, "CurrentStarCost")
               ?? 0;
    }

    private static int? ResolveCardCostForTurn(object? card)
    {
        if (GetBoolean(card, "HasEnergyCostX"))
        {
            return -1;
        }

        return GetNullableInt(card, "CurrentStarCost")
               ?? GetNullableInt(card, "CurrentEnergyCost")
               ?? GetNullableInt(card, "StarCostForTurn")
               ?? GetNullableInt(card, "EnergyCost");
    }

    private static bool? ResolveCardUpgraded(object? card)
    {
        if (GetMemberValue(card, "IsUpgraded") is bool isUpgraded)
        {
            return isUpgraded;
        }

        if (GetMemberValue(card, "Upgraded") is bool upgraded)
        {
            return upgraded;
        }

        var upgradeCount = GetNullableInt(card, "UpgradeCount");
        return upgradeCount is null ? null : upgradeCount.Value > 0;
    }

    private static string? ResolveCardCanonicalId(object? card)
    {
        return ConvertToText(
                   GetMemberValue(card, "CardId")
                   ?? GetMemberValue(card, "CardKey")
                   ?? GetMemberValue(card, "Key")
                   ?? GetMemberValue(card, "Id")
                   ?? GetMemberValue(card, "DataId")
                   ?? GetMemberValue(card, "TemplateId")
                   ?? GetMemberValue(card, "CanonicalId")
                   ?? GetMemberValue(GetMemberValue(card, "Data"), "Id")
                   ?? GetMemberValue(GetMemberValue(card, "Definition"), "Id"))
               ?? ConvertToText(GetMemberValue(card, "InternalName"));
    }

    private DescriptionExtraction ResolveCardDescription(
        object? card,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        CardDescriptionContext descriptionContext,
        IReadOnlyList<string>? traits = null,
        IReadOnlyList<string>? keywords = null)
    {
        var source = ResolveCardDescriptionSource(card);
        var effectiveContext = descriptionContext == CardDescriptionContext.Unknown
            ? InferCardDescriptionContext(path)
            : descriptionContext;
        var descriptionValue =
            GetMemberValue(card, "Description")
            ?? GetMemberValue(source, "Description")
            ?? GetMemberValue(card, "RulesText")
            ?? GetMemberValue(source, "RulesText")
            ?? GetMemberValue(card, "CardText")
            ?? GetMemberValue(source, "CardText")
            ?? GetMemberValue(card, "BodyText")
            ?? GetMemberValue(source, "BodyText")
            ?? GetMemberValue(card, "Text");
        var boundDescriptionValue = TryBindLocStringWithDynamicVars(descriptionValue, source ?? card);
        var raw = ConvertDescriptionTemplateToText(
            descriptionValue,
            path,
            textDiagnostics,
            "Description",
            "RulesText",
            "CardText",
            "BodyText",
            "Text");
        var runtimeRendered = ConvertRenderedDescriptionToText(
            GetMemberValue(card, "RenderedDescription")
            ?? GetMemberValue(source, "RenderedDescription")
            ?? GetMemberValue(card, "RenderedText")
            ?? GetMemberValue(source, "RenderedText")
            ?? GetMemberValue(card, "DisplayDescription")
            ?? GetMemberValue(source, "DisplayDescription")
            ?? GetMemberValue(card, "DescriptionRendered")
            ?? GetMemberValue(source, "DescriptionRendered")
            ?? GetMemberValue(card, "ResolvedDescription")
            ?? GetMemberValue(source, "ResolvedDescription")
            ?? GetMemberValue(card, "CurrentDescription")
            ?? GetMemberValue(source, "CurrentDescription")
            ?? GetMemberValue(card, "DescriptionText")
            ?? GetMemberValue(source, "DescriptionText")
            ?? GetMemberValue(GetMemberValue(card, "Description"), "RenderedText")
            ?? GetMemberValue(GetMemberValue(card, "Description"), "Text")
            ?? boundDescriptionValue,
            $"{path}.rendered",
            textDiagnostics,
            "RenderedDescription",
            "RenderedText",
            "DisplayDescription",
            "DescriptionRendered",
            "ResolvedDescription",
            "CurrentDescription");
        var seedVariables = ResolveCardDescriptionSeedVariables(source ?? card, raw, keywords)
            .Concat(ExtractDescriptionVariablesFromLocString(descriptionValue, "loc_string", source ?? card))
            .Concat(ExtractDescriptionVariablesFromLocString(boundDescriptionValue, "bound_loc_string", source ?? card))
            .ToArray();
        var vars = ExtractDescriptionVariables(source ?? card, raw, seedVariables);
        var gameRendered = ShouldAttemptGameRenderedCardDescription(raw)
            ? ResolveGameRenderedCardDescription(card, source, effectiveContext, path)
            : new GameRenderedCardDescription(null, "game_render_skipped_placeholders");
        var renderOutcome = RenderCardDescription(raw, gameRendered.Text, runtimeRendered, vars);
        var glossary = ExtractGlossaryAnchors(
            canonicalId: null,
            displayName: ConvertToText(GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card),
            texts: new[] { renderOutcome.Text, raw },
            keywords: keywords,
            traits: traits,
            path: $"{path}.glossary",
            source ?? card,
            card);
        glossary = FilterCardGlossary(
            ResolveCardCanonicalId(card),
            ConvertToText(GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card) ?? path,
            glossary,
            $"{path}.glossary");
        var canonicalDescription = ChooseCanonicalDescription(renderOutcome.Text, raw);
        LogDescriptionDiagnostics(
            kind: "card",
            identifier: ResolveCardCanonicalId(card) ?? ConvertToText(GetMemberValue(card, "Title") ?? GetMemberValue(card, "Name") ?? card) ?? path,
            path: path,
            raw: raw,
            rendered: renderOutcome.Text,
            quality: renderOutcome.Quality,
            source: renderOutcome.Source,
            variables: vars,
            glossary: glossary,
            context: GetCardDescriptionContextLabel(effectiveContext));
        return new DescriptionExtraction(raw, renderOutcome.Text, canonicalDescription, renderOutcome.Quality, renderOutcome.Source, vars, glossary);
    }

    private static bool ShouldAttemptGameRenderedCardDescription(string? raw)
    {
        return !ContainsDescriptionPlaceholder(raw);
    }

    private IReadOnlyList<GlossaryAnchor> FilterCardGlossary(
        string? canonicalCardId,
        string cardName,
        IReadOnlyList<GlossaryAnchor> glossary,
        string path)
    {
        if (glossary.Count == 0)
        {
            return glossary;
        }

        var filtered = new List<GlossaryAnchor>(glossary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSemantic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in glossary)
        {
            var reason = ClassifyCardGlossaryFilterReason(anchor);
            if (reason is not null)
            {
                LogCardGlossaryFilter(canonicalCardId ?? cardName, path, anchor, reason);
                continue;
            }

            var dedupeKey = string.Join("|",
                NormalizeComparisonText(anchor.GlossaryId),
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!seen.Add(dedupeKey))
            {
                LogCardGlossaryFilter(canonicalCardId ?? cardName, path, anchor, "duplicate_glossary");
                continue;
            }

            var semanticKey = string.Join("|",
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!string.IsNullOrWhiteSpace(semanticKey) && !seenSemantic.Add(semanticKey))
            {
                LogCardGlossaryFilter(canonicalCardId ?? cardName, path, anchor, "duplicate_semantic_glossary");
                continue;
            }

            filtered.Add(anchor);
        }

        return filtered;
    }

    private static string? ClassifyCardGlossaryFilterReason(GlossaryAnchor anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor.Hint))
        {
            return "empty_hint";
        }

        if (string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_hint";
        }

        if (string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback_builtin";
        }

        if (ContainsDescriptionPlaceholder(anchor.Hint))
        {
            return "template_hint";
        }

        return null;
    }

    private void LogCardGlossaryFilter(string identifier, string path, GlossaryAnchor anchor, string reason)
    {
        var message =
            $"Card glossary filtered card={identifier} path={path} glossary_id={anchor.GlossaryId} " +
            $"display_text={anchor.DisplayText} source={anchor.Source ?? "unknown"} reason={reason} hint=\"{AbbreviateForLog(anchor.Hint)}\"";
        _logger?.Warn(message);
    }

    private static object? ResolveCardDescriptionSource(object? card)
    {
        return GetMemberValue(card, "CardModel")
            ?? GetMemberValue(card, "Model")
            ?? GetMemberValue(card, "Card")
            ?? card;
    }

    private GameRenderedCardDescription ResolveGameRenderedCardDescription(
        object? card,
        object? source,
        CardDescriptionContext context,
        string path)
    {
        var targets = new[] { card, source }
            .Where(target => target is not null)
            .Distinct()
            .Cast<object>()
            .ToArray();
        if (targets.Length == 0)
        {
            return new GameRenderedCardDescription(null, null);
        }

        if (context == CardDescriptionContext.UpgradePreview)
        {
            foreach (var target in targets)
            {
                var previewDescription = TryInvokeGameRenderedDescriptionMethod(
                    target,
                    "GetDescriptionForUpgradePreview",
                    Array.Empty<object?>(),
                    "game_upgrade_preview");
                if (!string.IsNullOrWhiteSpace(previewDescription.Text))
                {
                    return previewDescription;
                }
            }
        }

        if (!TryResolvePileTypeArgument(targets, context, out var pileTypeArgument))
        {
            return new GameRenderedCardDescription(null, null);
        }

        foreach (var target in targets)
        {
            var description = TryInvokeGameRenderedDescriptionMethod(
                target,
                "GetDescriptionForPile",
                new[] { pileTypeArgument, null },
                $"game_rendered_{GetCardDescriptionContextLabel(context)}");
            if (!string.IsNullOrWhiteSpace(description.Text))
            {
                return description;
            }
        }

        _logger?.Warn(
            $"Card description fallback path={path} context={GetCardDescriptionContextLabel(context)} stage=game_render_unavailable");
        return new GameRenderedCardDescription(null, null);
    }

    private GameRenderedCardDescription TryInvokeGameRenderedDescriptionMethod(
        object target,
        string methodName,
        object?[] args,
        string sourceLabel)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        foreach (var method in methods)
        {
            if (method.GetParameters().Length != args.Length)
            {
                continue;
            }

            try
            {
                var result = method.Invoke(target, args);
                var normalized = NormalizeDescriptionText(ConvertDescriptionTemplateToText(result, sourceLabel));
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return new GameRenderedCardDescription(normalized, sourceLabel);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(
                    $"Card description method {methodName} fallback source={sourceLabel} target={target.GetType().FullName} detail={ex.GetBaseException().Message}");
            }
        }

        return new GameRenderedCardDescription(null, null);
    }

    private static bool TryResolvePileTypeArgument(
        IReadOnlyList<object> targets,
        CardDescriptionContext context,
        out object? pileTypeArgument)
    {
        pileTypeArgument = null;
        var candidateNames = GetCardDescriptionPileTypeCandidates(context);
        if (candidateNames.Length == 0)
        {
            return false;
        }

        foreach (var target in targets)
        {
            var enumType = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(method => string.Equals(method.Name, "GetDescriptionForPile", StringComparison.Ordinal))
                .Select(method => method.GetParameters())
                .Where(parameters => parameters.Length == 2 && parameters[0].ParameterType.IsEnum)
                .Select(parameters => parameters[0].ParameterType)
                .FirstOrDefault();
            if (enumType is null)
            {
                continue;
            }

            foreach (var candidateName in candidateNames)
            {
                try
                {
                    pileTypeArgument = Enum.Parse(enumType, candidateName, ignoreCase: true);
                    return true;
                }
                catch
                {
                    // Probe other enum names.
                }
            }
        }

        return false;
    }

    private static string[] GetCardDescriptionPileTypeCandidates(CardDescriptionContext context)
    {
        return context switch
        {
            CardDescriptionContext.Hand => new[] { "Hand", "HandPile" },
            CardDescriptionContext.DrawPile => new[] { "DrawPile", "Draw", "Deck" },
            CardDescriptionContext.DiscardPile => new[] { "DiscardPile", "Discard" },
            CardDescriptionContext.ExhaustPile => new[] { "ExhaustPile", "Exhaust" },
            CardDescriptionContext.Preview => new[] { "Hand", "HandPile" },
            _ => Array.Empty<string>(),
        };
    }

    private static CardDescriptionContext InferCardDescriptionContext(string path)
    {
        if (path.Contains("draw_pile_cards", StringComparison.OrdinalIgnoreCase))
        {
            return CardDescriptionContext.DrawPile;
        }

        if (path.Contains("discard_pile_cards", StringComparison.OrdinalIgnoreCase))
        {
            return CardDescriptionContext.DiscardPile;
        }

        if (path.Contains("exhaust_pile_cards", StringComparison.OrdinalIgnoreCase))
        {
            return CardDescriptionContext.ExhaustPile;
        }

        if (path.Contains("player.hand", StringComparison.OrdinalIgnoreCase))
        {
            return CardDescriptionContext.Hand;
        }

        if (path.Contains("preview", StringComparison.OrdinalIgnoreCase))
        {
            return CardDescriptionContext.Preview;
        }

        return CardDescriptionContext.Unknown;
    }

    private static string GetCardDescriptionContextLabel(CardDescriptionContext context)
    {
        return context switch
        {
            CardDescriptionContext.Hand => "hand",
            CardDescriptionContext.DrawPile => "draw_pile",
            CardDescriptionContext.DiscardPile => "discard_pile",
            CardDescriptionContext.ExhaustPile => "exhaust_pile",
            CardDescriptionContext.Preview => "preview",
            CardDescriptionContext.UpgradePreview => "upgrade_preview",
            _ => "unknown",
        };
    }

    private static IReadOnlyList<DescriptionVariable> ExtractDescriptionVariablesFromLocString(object? descriptionValue, string sourceLabel, object? semanticContext = null)
    {
        if (descriptionValue is null)
        {
            return Array.Empty<DescriptionVariable>();
        }

        var variables = GetMemberValue(descriptionValue, "Variables");
        if (variables is not IDictionary dictionary || dictionary.Count == 0)
        {
            return Array.Empty<DescriptionVariable>();
        }

        var results = new List<DescriptionVariable>();
        foreach (DictionaryEntry entry in dictionary)
        {
            var rawKey = ConvertToText(entry.Key);
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            var key = NormalizeDescriptionVariableKey(rawKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var resolution = ResolveLocStringVariableValue(entry.Value, key, rawKey, semanticContext);
            results.Add(new DescriptionVariable(
                key,
                resolution.Value,
                resolution.Source ?? sourceLabel,
                rawKey,
                resolution.SemanticKind));
        }

        return DeduplicateVariables(results);
    }

    private static object? TryBindLocStringWithDynamicVars(object? descriptionValue, object? source)
    {
        if (descriptionValue is null || source is null)
        {
            return null;
        }

        var typeName = GetTypeName(descriptionValue) ?? string.Empty;
        if (!typeName.Contains("LocString", StringComparison.Ordinal))
        {
            return null;
        }

        var dynamicVars = GetMemberValue(source, "DynamicVars");
        if (dynamicVars is null)
        {
            return null;
        }

        var bound = CloneLocString(descriptionValue) ?? descriptionValue;
        TryInvokeSingleParameterMethod(bound, "AddVariablesFrom", descriptionValue);
        if (!TryInvokeSingleParameterMethod(dynamicVars, "AddTo", bound))
        {
            return null;
        }

        return bound;
    }

    private static object? CloneLocString(object descriptionValue)
    {
        var type = descriptionValue.GetType();
        var locTable = ConvertToText(GetMemberValue(descriptionValue, "LocTable"));
        var locEntryKey = ConvertToText(GetMemberValue(descriptionValue, "LocEntryKey"));
        if (string.IsNullOrWhiteSpace(locTable) || string.IsNullOrWhiteSpace(locEntryKey))
        {
            return null;
        }

        var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string), typeof(string) }, null);
        if (ctor is null)
        {
            return null;
        }

        try
        {
            return ctor.Invoke(new object?[] { locTable, locEntryKey });
        }
        catch
        {
            return null;
        }
    }

    private static bool TryInvokeSingleParameterMethod(object target, string methodName, object argument)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(argument))
            {
                continue;
            }

            try
            {
                method.Invoke(target, new[] { argument });
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static VariableResolution ResolveLocStringVariableValue(object? value, string? normalizedKey = null, string? rawKey = null, object? semanticContext = null)
    {
        var semanticKind = ResolveDescriptionSemanticKind(semanticContext, normalizedKey, rawKey, rawKey);
        if (value is null)
        {
            return new VariableResolution(null, null, semanticKind);
        }

        try
        {
            return new VariableResolution(Convert.ToInt32(value), "loc_string.value", semanticKind);
        }
        catch
        {
            // fall through
        }

        foreach (var memberName in new[] { "IntValue", "PreviewValue", "BaseValue", "EnchantedValue", "Value" })
        {
            var numeric = GetNullableInt(value, memberName);
            if (numeric is not null)
            {
                return new VariableResolution(numeric, $"loc_string.{memberName}", semanticKind);
            }
        }

        return new VariableResolution(null, null, semanticKind);
    }

    private static string ResolveEnemyId(object enemy, int index)
    {
        return ConvertToText(GetMemberValue(enemy, "CombatId"))
               ?? ConvertToText(GetMemberValue(enemy, "SlotName"))
               ?? ConvertToText(GetMemberValue(enemy, "Name"))
               ?? $"enemy_{index}";
    }

    private static string? ResolveEnemyCanonicalId(object? enemy)
    {
        return ConvertToText(
            GetMemberValue(enemy, "EnemyId")
            ?? GetMemberValue(enemy, "MonsterId")
            ?? GetMemberValue(enemy, "Id")
            ?? GetMemberValue(enemy, "Key")
            ?? GetMemberValue(enemy, "TemplateId")
            ?? GetMemberValue(GetMemberValue(enemy, "Monster"), "EnemyId")
            ?? GetMemberValue(GetMemberValue(enemy, "Monster"), "Id"));
    }

    private static string? ResolvePowerCanonicalId(object? power)
    {
        return ConvertToText(
            GetMemberValue(power, "PowerId")
            ?? GetMemberValue(power, "Id")
            ?? GetMemberValue(power, "Key")
            ?? GetMemberValue(power, "TemplateId")
            ?? GetMemberValue(power, "CanonicalId"));
    }

    private static string? ResolvePotionCanonicalId(object? potion)
    {
        var canonicalInstance = GetMemberValue(potion, "CanonicalInstance");
        return ConvertToText(
                   GetMemberValue(potion, "PotionId")
                   ?? GetMemberValue(potion, "Id")
                   ?? GetMemberValue(potion, "Key")
                   ?? GetMemberValue(potion, "TemplateId")
                   ?? GetMemberValue(potion, "CanonicalId")
                   ?? GetMemberValue(canonicalInstance, "PotionId")
                   ?? GetMemberValue(canonicalInstance, "Id")
                   ?? GetMemberValue(canonicalInstance, "Key")
                   ?? canonicalInstance)
               ?? GetTypeName(potion);
    }

    private static string? ResolveRelicCanonicalId(object? source, object? fallback = null)
    {
        var canonicalInstance = GetMemberValue(source, "CanonicalInstance")
                                ?? GetMemberValue(fallback, "CanonicalInstance");
        var definition = GetMemberValue(source, "Definition")
                         ?? GetMemberValue(fallback, "Definition");
        var model = GetMemberValue(source, "Model")
                    ?? GetMemberValue(fallback, "Model");
        return ConvertToText(
                   GetMemberValue(source, "RelicId")
                   ?? GetMemberValue(source, "InternalName")
                   ?? GetMemberValue(source, "Id")
                   ?? GetMemberValue(source, "Key")
                   ?? GetMemberValue(source, "TemplateId")
                   ?? GetMemberValue(source, "CanonicalId")
                   ?? GetMemberValue(definition, "RelicId")
                   ?? GetMemberValue(definition, "Id")
                   ?? GetMemberValue(definition, "Key")
                   ?? GetMemberValue(model, "RelicId")
                   ?? GetMemberValue(model, "Id")
                   ?? GetMemberValue(model, "Key")
                   ?? GetMemberValue(canonicalInstance, "RelicId")
                   ?? GetMemberValue(canonicalInstance, "Id")
                   ?? GetMemberValue(canonicalInstance, "Key")
                   ?? canonicalInstance)
               ?? GetTypeName(source)
               ?? GetTypeName(fallback);
    }

    private static EnemyIntentDescriptor ResolveEnemyIntent(object enemy, object? playerTargets, string path, TextDiagnosticsCollector textDiagnostics)
    {
        var raw = NormalizeEnemyUiText(ConvertToText(
            GetMemberValue(enemy, "Intent")
            ?? GetMemberValue(GetMemberValue(enemy, "Monster"), "Intent"),
            path,
            textDiagnostics,
            "Intent"));
        var intentObject = ResolvePrimaryEnemyIntentObject(enemy);
        var owner = ResolveEnemyOwnerCreature(enemy);
        var hoverTip = intentObject is not null && owner is not null
            ? TryInvokeCompatibleMethod(intentObject, "GetHoverTip", playerTargets, owner)
            : null;
        raw ??= NormalizeEnemyUiText(ConvertToText(
            intentObject is not null && owner is not null
                ? TryInvokeCompatibleMethod(intentObject, "GetIntentLabel", playerTargets, owner)
                : null,
            $"{path}.label",
            textDiagnostics,
            "Title",
            "Text",
            "Label"));
        var hoverTipTitle = NormalizeEnemyUiText(ConvertToText(GetMemberValue(hoverTip, "Title"), $"{path}.hover_tip_title", textDiagnostics, "Title"));
        if (IsLowQualityIntentLabel(raw) && !string.IsNullOrWhiteSpace(hoverTipTitle))
        {
            raw = hoverTipTitle;
        }
        else
        {
            raw ??= hoverTipTitle;
        }

        var moveSource = ResolveEnemyMoveSource(enemy);
        var hoverDescription = NormalizeEnemyUiText(ConvertToText(GetMemberValue(hoverTip, "Description"), $"{path}.hover_tip_description", textDiagnostics, "Description", "Text"));
        var semanticDescription = hoverDescription
            ?? NormalizeEnemyUiText(ConvertDescriptionTemplateToText(
                GetFirstMemberValue(
                    moveSource,
                    "Description",
                    "RulesText",
                    "Text",
                    "TooltipText",
                    "IntentDescription",
                    "IntentText",
                    "EffectText")
                ?? GetFirstMemberValue(
                    enemy,
                    "IntentDescription",
                    "IntentText",
                    "IntentTooltip",
                    "IntentTooltipText",
                    "MoveDescription",
                    "CurrentMoveDescription"),
                $"{path}.semantic_description",
                textDiagnostics,
                "Description",
                "RulesText",
                "Text",
                "TooltipText",
                "IntentDescription",
                "IntentText"));
        var damage = GetNullableInt(enemy, "IntentDamage")
                     ?? GetNullableInt(enemy, "DisplayedIntentDamage")
                     ?? GetNullableInt(enemy, "AttackDamage")
                     ?? GetNullableInt(GetMemberValue(enemy, "Monster"), "IntentDamage")
                     ?? GetNullableInt(intentObject, "Damage")
                     ?? ConvertToInt32(TryInvokeCompatibleMethod(intentObject, "GetTotalDamage", playerTargets, owner))
                     ?? ConvertToInt32(TryInvokeCompatibleMethod(intentObject, "GetSingleDamage", playerTargets, owner));
        var hits = GetNullableInt(enemy, "IntentHits")
                   ?? GetNullableInt(enemy, "AttackCount")
                   ?? GetNullableInt(enemy, "HitCount")
                   ?? GetNullableInt(GetMemberValue(enemy, "Monster"), "IntentHits")
                   ?? GetNullableInt(intentObject, "Repeats");
        var block = GetNullableInt(enemy, "IntentBlock")
                    ?? GetNullableInt(enemy, "BlockAmount")
                    ?? GetNullableInt(GetMemberValue(enemy, "Monster"), "IntentBlock");
        var effects = ExtractIntentEffects(enemy, raw, semanticDescription);
        var intentTypeText = ConvertToText(GetMemberValue(intentObject, "IntentType"));
        var type = NormalizeIntentType(intentTypeText, raw, semanticDescription, effects, damage, block);
        var display = raw;
        if ((string.IsNullOrWhiteSpace(display) || IsGenericIntentLabel(display) || string.Equals(display, "unknown", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(type) &&
            !string.Equals(type, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            display = type;
        }

        if ((string.IsNullOrWhiteSpace(display) || IsGenericIntentLabel(display) || string.Equals(display, "unknown", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(type) &&
            !string.Equals(type, "unknown", StringComparison.OrdinalIgnoreCase) &&
            damage is > 0)
        {
            display = hits is > 1
                ? $"{type}_{damage}x{hits}"
                : $"{type}_{damage}";
        }

        display = NormalizeEnemyUiText(display) ?? raw ?? type ?? "unknown";

        return new EnemyIntentDescriptor(
            Display: display,
            Raw: raw,
            Type: type,
            Damage: damage,
            Hits: hits,
            Block: block,
            Effects: effects);
    }

    private EnemyMoveNameResolution ResolveEnemyMoveName(
        object enemy,
        object? moveSource,
        object? playerTargets,
        EnemyIntentDescriptor intent,
        string path,
        TextDiagnosticsCollector textDiagnostics)
    {
        EnemyMoveNameResolution EvaluateCandidate(string? value, string source)
        {
            var candidate = NormalizeEnemyUiText(value);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return new EnemyMoveNameResolution(null);
            }

            if (IsGenericIntentLabel(candidate))
            {
                return new EnemyMoveNameResolution(null, "generic_intent_label", candidate, source);
            }

            var comparisonKey = NormalizeIntentLabelKey(candidate);
            if (!string.IsNullOrWhiteSpace(intent.Raw) &&
                string.Equals(comparisonKey, NormalizeIntentLabelKey(intent.Raw), StringComparison.OrdinalIgnoreCase))
            {
                return new EnemyMoveNameResolution(null, "duplicate_intent_raw", candidate, source);
            }

            if (!string.IsNullOrWhiteSpace(intent.Display) &&
                string.Equals(comparisonKey, NormalizeIntentLabelKey(intent.Display), StringComparison.OrdinalIgnoreCase))
            {
                return new EnemyMoveNameResolution(null, "duplicate_intent_display", candidate, source);
            }

            if (!string.IsNullOrWhiteSpace(intent.Type) &&
                string.Equals(comparisonKey, NormalizeIntentLabelKey(intent.Type), StringComparison.OrdinalIgnoreCase))
            {
                return new EnemyMoveNameResolution(null, "duplicate_intent_type", candidate, source);
            }

            return new EnemyMoveNameResolution(candidate, Source: source);
        }

        var intentObject = ResolvePrimaryEnemyIntentObject(enemy);
        var owner = ResolveEnemyOwnerCreature(enemy);
        var hoverTip = intentObject is not null && owner is not null
            ? TryInvokeCompatibleMethod(intentObject, "GetHoverTip", playerTargets, owner)
            : null;
        EnemyMoveNameResolution? suppressed = null;

        var moveName = ConvertToText(
            GetFirstMemberValue(
                moveSource,
                "Name",
                "Title",
                "DisplayName",
                "Label",
                "IntentName",
                "MoveName"),
            $"{path}.move_name",
            textDiagnostics,
            "Name",
            "Title",
            "DisplayName",
            "Label");
        var resolution = EvaluateCandidate(moveName, "move_source");
        if (!string.IsNullOrWhiteSpace(resolution.Value))
        {
            return resolution;
        }

        if (!string.IsNullOrWhiteSpace(resolution.SuppressedReason))
        {
            suppressed ??= resolution;
        }

        moveName = ConvertToText(
            intentObject is not null && owner is not null
                ? TryInvokeCompatibleMethod(intentObject, "GetIntentLabel", playerTargets, owner)
                : null,
            $"{path}.move_name_intent_label",
            textDiagnostics,
            "Title",
            "Text",
            "Label");
        resolution = EvaluateCandidate(moveName, "intent_label");
        if (!string.IsNullOrWhiteSpace(resolution.Value))
        {
            return resolution;
        }

        if (!string.IsNullOrWhiteSpace(resolution.SuppressedReason))
        {
            suppressed ??= resolution;
        }

        moveName = ConvertToText(GetMemberValue(hoverTip, "Title"), $"{path}.move_name_hover_tip", textDiagnostics, "Title");
        resolution = EvaluateCandidate(moveName, "hover_tip");
        if (!string.IsNullOrWhiteSpace(resolution.Value))
        {
            return resolution;
        }

        if (!string.IsNullOrWhiteSpace(resolution.SuppressedReason))
        {
            suppressed ??= resolution;
        }

        moveName = ConvertToText(
            GetFirstMemberValue(
                enemy,
                "IntentName",
                "MoveName",
                "ActionName",
                "CurrentMoveName",
                "NextMoveName"),
            $"{path}.move_name_fallback",
            textDiagnostics,
            "IntentName",
            "MoveName",
            "ActionName");
        resolution = EvaluateCandidate(moveName, "enemy_fallback");
        if (!string.IsNullOrWhiteSpace(resolution.Value))
        {
            return resolution;
        }

        if (!string.IsNullOrWhiteSpace(resolution.SuppressedReason))
        {
            suppressed ??= resolution;
        }

        return suppressed ?? new EnemyMoveNameResolution(null);
    }

    private DescriptionExtraction ResolveEnemyMoveDescription(
        object enemy,
        object? moveSource,
        object? playerTargets,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        string? moveName,
        IReadOnlyList<string> traits,
        IReadOnlyList<string> keywords)
    {
        var monster = GetMemberValue(enemy, "Monster");
        var source = moveSource ?? enemy;
        var intentObject = ResolvePrimaryEnemyIntentObject(enemy);
        var owner = ResolveEnemyOwnerCreature(enemy);
        var hoverTip = intentObject is not null && owner is not null
            ? TryInvokeCompatibleMethod(intentObject, "GetHoverTip", playerTargets, owner)
            : null;
        var rawDescriptionValue =
            GetFirstMemberValue(
                moveSource,
                "Description",
                "RulesText",
                "Text",
                "TooltipText",
                "IntentDescription",
                "IntentText",
                "EffectText",
                "DescriptionTemplate",
                "LocString")
            ?? GetFirstMemberValue(
                enemy,
                "IntentDescription",
                "IntentText",
                "IntentTooltip",
                "IntentTooltipText",
                "MoveDescription",
                "CurrentMoveDescription")
            ?? GetFirstMemberValue(
                monster,
                "IntentDescription",
                "IntentText",
                "MoveDescription")
            ?? GetMemberValue(hoverTip, "Description")
            ?? (intentObject is not null && owner is not null
                ? TryInvokeCompatibleMethod(intentObject, "GetIntentDescription", playerTargets, owner)
                : null);
        var renderedDescriptionValue =
            GetFirstMemberValue(
                moveSource,
                "RenderedDescription",
                "RenderedText",
                "DisplayDescription",
                "DescriptionRendered",
                "Tooltip",
                "TooltipText")
            ?? GetFirstMemberValue(
                enemy,
                "RenderedIntentDescription",
                "IntentDescriptionRendered",
                "MoveDescriptionRendered")
            ?? GetFirstMemberValue(
                monster,
                "RenderedIntentDescription",
                "MoveDescriptionRendered")
            ?? GetMemberValue(hoverTip, "Description");

        var raw = ConvertDescriptionTemplateToText(
            rawDescriptionValue,
            $"{path}.move_description",
            textDiagnostics,
            "Description",
            "RulesText",
            "Text",
            "TooltipText",
            "IntentDescription",
            "IntentText");
        var rendered = ConvertRenderedDescriptionToText(
            renderedDescriptionValue,
            $"{path}.move_description_rendered",
            textDiagnostics,
            "RenderedDescription",
            "RenderedText",
            "DisplayDescription",
            "DescriptionRendered");

        var vars = ExtractDescriptionVariables(
            source,
            raw,
            ResolveEnemyMoveDescriptionSeedVariables(moveSource, enemy, raw, traits, keywords));
        var renderOutcome = RenderDescription(raw, rendered, vars);
        var glossary = ExtractGlossaryAnchors(
            canonicalId: null,
            displayName: moveName,
            texts: new[] { renderOutcome.Text, raw, moveName },
            keywords: keywords,
            traits: traits,
            path: $"{path}.move_glossary",
            hoverTip,
            moveSource,
            source,
            enemy,
            monster);
        var canonicalDescription = ChooseCanonicalDescription(renderOutcome.Text, raw);

        if (!string.IsNullOrWhiteSpace(raw) || !string.IsNullOrWhiteSpace(rendered))
        {
            LogDescriptionDiagnostics(
                kind: "enemy_move",
                identifier: ResolveEnemyCanonicalId(enemy) ?? moveName ?? path,
                path: $"{path}.move_description",
                raw: raw,
                rendered: renderOutcome.Text,
                quality: renderOutcome.Quality,
                source: renderOutcome.Source,
                variables: vars,
                glossary: glossary);
        }

        return new DescriptionExtraction(raw, renderOutcome.Text, canonicalDescription, renderOutcome.Quality, renderOutcome.Source, vars, glossary);
    }

    private static IReadOnlyList<DescriptionVariable> ResolveEnemyMoveDescriptionSeedVariables(
        object? moveSource,
        object enemy,
        string? raw,
        IReadOnlyList<string> traits,
        IReadOnlyList<string> keywords)
    {
        var seedHints = (keywords ?? Array.Empty<string>())
            .Concat(traits ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var variables = new List<DescriptionVariable>();
        foreach (var key in new[] { "damage", "block", "draw", "strength", "weak", "vulnerable", "frail", "amount" })
        {
            if (!ShouldSeedDescriptionVariable(key, raw, seedHints))
            {
                continue;
            }

            if (moveSource is not null)
            {
                AddSeedVariable(variables, key, moveSource, "enemy_move_seed", null);
            }

            AddSeedVariable(variables, key, enemy, "enemy_seed", null);
        }

        return DeduplicateVariables(variables);
    }

    private IReadOnlyList<string> ExtractEnemyTraits(
        object enemy,
        object? monster,
        object? moveSource,
        string path,
        TextDiagnosticsCollector textDiagnostics)
    {
        return ExtractCombinedTextList(
            new object?[]
            {
                GetFirstMemberValue(enemy, "Traits", "Tags", "EnemyTags", "TraitIds", "TypeTags"),
                GetFirstMemberValue(monster, "Traits", "Tags", "EnemyTags", "TraitIds", "TypeTags"),
                GetFirstMemberValue(moveSource, "Traits", "Tags", "EnemyTags"),
            },
            path,
            textDiagnostics);
    }

    private IReadOnlyList<string> ExtractEnemyKeywords(
        EnemyIntentDescriptor intent,
        IReadOnlyList<GlossaryAnchor> moveGlossary,
        IReadOnlyList<string> traits,
        IReadOnlyList<RuntimePowerState> powers,
        object enemy,
        object? monster,
        object? moveSource,
        string path,
        TextDiagnosticsCollector textDiagnostics)
    {
        var keywords = new List<string>();
        keywords.AddRange(ExtractCombinedTextList(
            new object?[]
            {
                GetFirstMemberValue(enemy, "Keywords", "KeywordIds", "IntentKeywords"),
                GetFirstMemberValue(monster, "Keywords", "KeywordIds", "IntentKeywords"),
                GetFirstMemberValue(moveSource, "Keywords", "KeywordIds", "IntentKeywords"),
            },
            path,
            textDiagnostics));
        keywords.AddRange(intent.Effects ?? Array.Empty<string>());
        keywords.AddRange(moveGlossary.Select(anchor => anchor.GlossaryId));
        keywords.AddRange(traits.Select(trait => NormalizeGlossaryId(trait) ?? trait));
        return keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static object? ResolveEnemyMoveSource(object enemy)
    {
        var monster = GetMemberValue(enemy, "Monster");
        var candidates = new[]
        {
            GetFirstMemberValue(enemy, "CurrentMove", "Move", "MoveData", "IntentData", "PlannedMove", "SelectedMove", "CurrentAction", "CurrentIntent", "NextMove"),
            GetFirstMemberValue(monster, "CurrentMove", "Move", "MoveData", "IntentData", "PlannedMove", "SelectedMove", "CurrentAction", "CurrentIntent", "NextMove"),
            GetMemberValue(enemy, "Intent"),
            GetMemberValue(monster, "Intent"),
        };

        foreach (var candidate in candidates)
        {
            if (candidate is null || candidate is string || candidate.GetType().IsValueType)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static object? ResolvePrimaryEnemyIntentObject(object enemy)
    {
        var moveSource = ResolveEnemyMoveSource(enemy);
        var directIntent = EnumerateObjects(GetMemberValue(moveSource, "Intents")).FirstOrDefault();
        if (directIntent is not null)
        {
            return directIntent;
        }

        var monster = GetMemberValue(enemy, "Monster");
        return EnumerateObjects(GetMemberValue(GetMemberValue(monster, "NextMove"), "Intents")).FirstOrDefault();
    }

    private static object? ResolveEnemyOwnerCreature(object enemy)
    {
        return GetMemberValue(enemy, "Creature")
               ?? GetMemberValue(GetMemberValue(enemy, "Monster"), "Creature");
    }

    private object? BuildCreatureTargetCollection(object runState)
    {
        var creatures = GetPlayers(runState)
            .Select(player => GetMemberValue(player, "Creature"))
            .Where(creature => creature is not null)
            .Cast<object>()
            .ToArray();
        if (creatures.Length == 0)
        {
            return null;
        }

        var elementType = creatures[0].GetType();
        var typedArray = Array.CreateInstance(elementType, creatures.Length);
        for (var index = 0; index < creatures.Length; index += 1)
        {
            typedArray.SetValue(creatures[index], index);
        }

        return typedArray;
    }

    private static IReadOnlyDictionary<string, object?> CreateEnemyExportDiagnostic(
        int index,
        string enemyId,
        string field,
        string source,
        string detail,
        string status = "fallback")
    {
        return new Dictionary<string, object?>
        {
            ["enemy_index"] = index,
            ["enemy_id"] = enemyId,
            ["field"] = field,
            ["status"] = status,
            ["source"] = source,
            ["detail"] = detail,
        };
    }

    private static object? GetFirstMemberValue(object? target, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = GetMemberValue(target, memberName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static object? GetFirstSafeDescriptionMemberValue(object? target, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = GetSafeDescriptionMemberValue(target, memberName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static object? GetSafeDescriptionMemberValue(object? target, string memberName)
    {
        if (target is null)
        {
            return null;
        }

        if (!LooksLikeRiskyDescriptionMember(memberName))
        {
            return GetMemberValue(target, memberName);
        }

        foreach (var candidateName in EnumerateDescriptionMemberAliases(memberName))
        {
            var fieldValue = GetFieldValue(target, candidateName);
            if (fieldValue is not null)
            {
                return fieldValue;
            }
        }

        return null;
    }

    private static bool LooksLikeRiskyDescriptionMember(string memberName)
    {
        return memberName is "Description" or "SmartDescription" or "DynamicDescription" or "StaticDescription" or "TooltipText";
    }

    private static IEnumerable<string> EnumerateDescriptionMemberAliases(string memberName)
    {
        yield return memberName;
        if (string.IsNullOrWhiteSpace(memberName))
        {
            yield break;
        }

        var camelCase = char.ToLowerInvariant(memberName[0]) + memberName[1..];
        if (!string.Equals(camelCase, memberName, StringComparison.Ordinal))
        {
            yield return camelCase;
        }

        yield return "_" + camelCase;
        yield return "_" + memberName;
        yield return "m_" + camelCase;
        yield return "m_" + memberName;
    }

    private static object? GetFieldValue(object? target, string fieldName)
    {
        if (target is null)
        {
            return null;
        }

        var type = target as Type ?? target.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase |
                    (target is Type ? BindingFlags.Static : BindingFlags.Instance);
        var field = type.GetField(fieldName, flags);
        if (field is null)
        {
            return null;
        }

        try
        {
            return field.GetValue(target is Type ? null : target);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ExtractCombinedTextList(
        IEnumerable<object?> collections,
        string path,
        TextDiagnosticsCollector textDiagnostics)
    {
        var results = new List<string>();
        var index = 0;
        foreach (var collection in collections)
        {
            results.AddRange(ExtractTextList(collection, $"{path}[{index}]", textDiagnostics));
            index += 1;
        }

        return results
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static object? TryInvokeCompatibleMethod(object? target, string methodName, params object?[] args)
    {
        if (target is null)
        {
            return null;
        }

        var methods = target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        foreach (var method in methods)
        {
            if (method.GetParameters().Length != args.Length)
            {
                continue;
            }

            try
            {
                return method.Invoke(target, args);
            }
            catch
            {
                // Continue probing other overloads.
            }
        }

        return null;
    }

    private static int? ConvertToInt32(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLowQualityIntentLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        return trimmed.All(char.IsDigit) ||
               trimmed.StartsWith("FORMAT_", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildTargetConstraints(string? targetType, IReadOnlyList<string> liveEnemyIds)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return Array.Empty<string>();
        }

        var normalized = targetType.ToLowerInvariant();
        if (!normalized.Contains("enemy", StringComparison.Ordinal) &&
            !normalized.Contains("monster", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        return liveEnemyIds;
    }

    private IReadOnlyList<RuntimePowerState> ExtractPowers(
        object? source,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        int? enemyIndex = null,
        string? enemyId = null,
        List<IReadOnlyDictionary<string, object?>>? enemyDiagnostics = null)
    {
        var collection = ResolvePowerCollection(source);
        return EnumerateObjects(collection)
            .Select((power, index) => DescribePower(power, $"{path}[{index}]", textDiagnostics, enemyIndex, enemyId, enemyDiagnostics))
            .Where(power => power is not null)
            .Cast<RuntimePowerState>()
            .ToArray();
    }

    private IReadOnlyList<RuntimePotionState> ExtractPotions(object? collection, string path, TextDiagnosticsCollector textDiagnostics)
    {
        return EnumerateObjects(collection)
            .Select((potion, index) => DescribePotion(potion, $"{path}[{index}]", textDiagnostics))
            .Where(potion => potion is not null)
            .Cast<RuntimePotionState>()
            .ToArray();
    }

    private IReadOnlyList<RuntimeRelicState> ExtractRelics(object? collection, string path, TextDiagnosticsCollector textDiagnostics)
    {
        return EnumerateObjects(collection)
            .Select((relic, index) => DescribeRelic(relic, $"{path}[{index}]", textDiagnostics))
            .Where(relic => relic is not null)
            .Cast<RuntimeRelicState>()
            .ToArray();
    }

    private static object? ResolvePotionCollection(object? player)
    {
        if (player is null)
        {
            return null;
        }

        return GetMemberValue(player, "PotionSlots")
               ?? GetMemberValue(player, "Potions")
               ?? GetMemberValue(player, "PotionModels");
    }

    private RuntimeRelicState? DescribeRelic(object? relic, string path, TextDiagnosticsCollector textDiagnostics)
    {
        var source = GetMemberValue(relic, "Relic")
                     ?? GetMemberValue(relic, "Model")
                     ?? GetMemberValue(relic, "RelicModel")
                     ?? GetMemberValue(relic, "Definition")
                     ?? GetMemberValue(relic, "CanonicalInstance")
                     ?? relic;
        var hoverTip = GetMemberValue(source, "HoverTip")
                       ?? GetMemberValue(relic, "HoverTip");
        var name = ConvertToText(
            GetFirstMemberValue(
                source,
                "Title",
                "Name",
                "DisplayName",
                "Label")
            ?? GetFirstMemberValue(
                relic,
                "Title",
                "Name",
                "DisplayName",
                "Label")
            ?? source,
            $"{path}.name",
            textDiagnostics,
            "Title",
            "Name",
            "DisplayName",
            "Label");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var descriptionValue =
            GetFirstSafeDescriptionMemberValue(
                source,
                "Description",
                "SmartDescription",
                "DynamicDescription",
                "StaticDescription",
                "RulesText",
                "Text",
                "TooltipText",
                "LocString")
            ?? GetFirstSafeDescriptionMemberValue(
                relic,
                "Description",
                "SmartDescription",
                "DynamicDescription",
                "StaticDescription",
                "RulesText",
                "Text",
                "TooltipText",
                "LocString")
            ?? GetSafeDescriptionMemberValue(hoverTip, "Description")
            ?? GetMemberValue(source, "LocString")
            ?? GetMemberValue(relic, "LocString");
        var canonicalRelicId = ResolveRelicCanonicalId(source, relic);
        descriptionValue ??= TryResolveRelicDescriptionLocString(canonicalRelicId);
        var boundDescriptionValue = TryBindLocStringWithDynamicVars(descriptionValue, source);
        var raw = ConvertDescriptionTemplateToText(
            descriptionValue,
            $"{path}.description",
            textDiagnostics,
            "Description",
            "SmartDescription",
            "DynamicDescription",
            "StaticDescription",
            "RulesText",
            "Text",
            "TooltipText");
        var rendered = ConvertRenderedDescriptionToText(
            GetFirstSafeDescriptionMemberValue(
                source,
                "RenderedDescription",
                "RenderedText",
                "DisplayDescription",
                "DescriptionRendered")
            ?? GetFirstSafeDescriptionMemberValue(
                relic,
                "RenderedDescription",
                "RenderedText",
                "DisplayDescription",
                "DescriptionRendered")
            ?? GetSafeDescriptionMemberValue(hoverTip, "Description")
            ?? boundDescriptionValue,
            $"{path}.rendered",
            textDiagnostics,
            "RenderedDescription",
            "RenderedText",
            "DisplayDescription",
            "DescriptionRendered");
        var vars = ExtractDescriptionVariables(
            source,
            raw,
            ExtractDescriptionVariablesFromLocString(descriptionValue, "loc_string", source)
                .Concat(ExtractDescriptionVariablesFromLocString(boundDescriptionValue, "bound_loc_string", source))
                .ToArray());
        var renderOutcome = RenderDescription(raw, rendered, vars);
        var canonicalDescription = ChooseCanonicalDescription(renderOutcome.Text, raw);
        var glossary = ExtractGlossaryAnchors(
            canonicalRelicId,
            name,
            new[] { canonicalDescription, raw, name },
            keywords: null,
            traits: null,
            path: $"{path}.glossary",
            source,
            relic,
            hoverTip);
        glossary = FilterRelicGlossary(
            canonicalRelicId,
            name,
            canonicalDescription,
            glossary,
            path);
        LogDescriptionDiagnostics(
            kind: "relic",
            identifier: canonicalRelicId ?? name,
            path: path,
            raw: raw,
            rendered: canonicalDescription,
            quality: renderOutcome.Quality,
            source: renderOutcome.Source,
            variables: vars,
            glossary: glossary);
        if (string.IsNullOrWhiteSpace(canonicalDescription))
        {
            _logger?.Warn($"Relic description unavailable relic={canonicalRelicId ?? name} path={path} stage=no_runtime_description");
        }

        return new RuntimeRelicState(
            Name: name,
            Description: canonicalDescription,
            CanonicalRelicId: canonicalRelicId,
            Glossary: glossary);
    }

    private IReadOnlyList<GlossaryAnchor> FilterRelicGlossary(
        string? canonicalRelicId,
        string relicName,
        string? canonicalDescription,
        IReadOnlyList<GlossaryAnchor> glossary,
        string path)
    {
        if (glossary.Count == 0)
        {
            return glossary;
        }

        var filtered = new List<GlossaryAnchor>(glossary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSemantic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in glossary)
        {
            var reason = ClassifyRelicGlossaryFilterReason(anchor, canonicalRelicId, relicName, canonicalDescription);
            if (reason is not null)
            {
                LogRelicGlossaryFilter(canonicalRelicId ?? relicName, path, anchor, reason);
                continue;
            }

            var dedupeKey = string.Join("|",
                NormalizeComparisonText(anchor.GlossaryId),
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!seen.Add(dedupeKey))
            {
                LogRelicGlossaryFilter(canonicalRelicId ?? relicName, path, anchor, "duplicate_glossary");
                continue;
            }

            var semanticKey = string.Join("|",
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!string.IsNullOrWhiteSpace(semanticKey) && !seenSemantic.Add(semanticKey))
            {
                LogRelicGlossaryFilter(canonicalRelicId ?? relicName, path, anchor, "duplicate_semantic_glossary");
                continue;
            }

            filtered.Add(anchor);
        }

        return filtered;
    }

    private static string? ClassifyRelicGlossaryFilterReason(
        GlossaryAnchor anchor,
        string? canonicalRelicId,
        string relicName,
        string? canonicalDescription)
    {
        if (string.IsNullOrWhiteSpace(anchor.Hint))
        {
            return "empty_hint";
        }

        if (string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_hint";
        }

        if (string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback_builtin";
        }

        if (ContainsDescriptionPlaceholder(anchor.Hint))
        {
            return "template_hint";
        }

        var normalizedGlossaryId = NormalizeGlossaryId(anchor.GlossaryId);
        var normalizedRelicId = NormalizeGlossaryId(canonicalRelicId);
        var normalizedDisplay = NormalizeComparisonText(anchor.DisplayText);
        var normalizedName = NormalizeComparisonText(relicName);
        if (!string.IsNullOrWhiteSpace(normalizedDisplay) &&
            string.Equals(normalizedDisplay, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(normalizedGlossaryId) &&
                string.Equals(normalizedGlossaryId, normalizedRelicId, StringComparison.OrdinalIgnoreCase))
            {
                return "duplicate_relic_identity";
            }

            if (string.Equals(NormalizeComparisonText(anchor.Hint), NormalizeComparisonText(canonicalDescription), StringComparison.OrdinalIgnoreCase))
            {
                return "duplicate_relic_description";
            }
        }

        return null;
    }

    private void LogRelicGlossaryFilter(string identifier, string path, GlossaryAnchor anchor, string reason)
    {
        var message =
            $"Relic glossary filtered relic={identifier} path={path} glossary_id={anchor.GlossaryId} " +
            $"display_text={anchor.DisplayText} source={anchor.Source ?? "unknown"} reason={reason} hint=\"{AbbreviateForLog(anchor.Hint)}\"";
        _logger?.Warn(message);
    }

    private static string NormalizeComparisonText(string? text)
    {
        var normalized = NormalizeDescriptionText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var buffer = new char[normalized.Length];
        var count = 0;
        foreach (var ch in normalized)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            buffer[count] = char.ToLowerInvariant(ch);
            count += 1;
        }

        return count == 0 ? string.Empty : new string(buffer, 0, count);
    }

    private IReadOnlyList<GlossaryAnchor> FilterEnemyMoveGlossary(
        IReadOnlyList<GlossaryAnchor> glossary,
        string? moveName,
        string? moveDescription,
        int enemyIndex,
        string enemyId,
        string path,
        List<IReadOnlyDictionary<string, object?>> diagnostics)
    {
        if (glossary.Count == 0)
        {
            return glossary;
        }

        var filtered = new List<GlossaryAnchor>(glossary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in glossary)
        {
            var reason = ClassifyEnemyMoveGlossaryFilterReason(anchor, moveName, moveDescription);
            if (reason is not null)
            {
                RecordEnemyFieldFilter(
                    enemyIndex,
                    enemyId,
                    "move_glossary",
                    reason,
                    $"glossary_id={anchor.GlossaryId} source={anchor.Source ?? "unknown"} display_text=\"{AbbreviateForLog(anchor.DisplayText)}\"",
                    diagnostics);
                continue;
            }

            var dedupeKey = string.Join("|",
                NormalizeComparisonText(anchor.GlossaryId),
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!seen.Add(dedupeKey))
            {
                RecordEnemyFieldFilter(
                    enemyIndex,
                    enemyId,
                    "move_glossary",
                    "duplicate_glossary",
                    $"glossary_id={anchor.GlossaryId} source={anchor.Source ?? "unknown"}",
                    diagnostics);
                continue;
            }

            filtered.Add(anchor);
        }

        return filtered;
    }

    private static string? ClassifyEnemyMoveGlossaryFilterReason(
        GlossaryAnchor anchor,
        string? moveName,
        string? moveDescription)
    {
        if (string.IsNullOrWhiteSpace(anchor.Hint))
        {
            return "empty_hint";
        }

        if (string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_hint";
        }

        if (string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback_builtin";
        }

        if (ContainsDescriptionPlaceholder(anchor.Hint))
        {
            return "template_hint";
        }

        if (!string.IsNullOrWhiteSpace(moveName) &&
            string.Equals(NormalizeComparisonText(anchor.DisplayText), NormalizeComparisonText(moveName), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeComparisonText(anchor.Hint), NormalizeComparisonText(moveDescription), StringComparison.OrdinalIgnoreCase))
        {
            return "duplicate_move_identity";
        }

        return LooksLikeInternalEnemyToken(anchor.GlossaryId) ? "internal_glossary_id" : null;
    }

    private IReadOnlyList<string> FilterEnemyTerms(
        IReadOnlyList<string> values,
        int enemyIndex,
        string enemyId,
        string field,
        string path,
        List<IReadOnlyDictionary<string, object?>> diagnostics)
    {
        if (values.Count == 0)
        {
            return values;
        }

        var filtered = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var cleaned = NormalizeEnemyUiText(value);
            var reason = ClassifyEnemyTermFilterReason(value, cleaned);
            if (reason is not null)
            {
                RecordEnemyFieldFilter(
                    enemyIndex,
                    enemyId,
                    field,
                    reason,
                    $"path={path} value=\"{AbbreviateForLog(value)}\"",
                    diagnostics);
                continue;
            }

            var output = NormalizeGlossaryId(cleaned) ?? cleaned!;
            if (!seen.Add(output))
            {
                RecordEnemyFieldFilter(
                    enemyIndex,
                    enemyId,
                    field,
                    "duplicate_term",
                    $"path={path} value=\"{AbbreviateForLog(value)}\"",
                    diagnostics);
                continue;
            }

            filtered.Add(output);
        }

        return filtered;
    }

    private static string? ClassifyEnemyTermFilterReason(string? rawValue, string? cleanedValue)
    {
        if (string.IsNullOrWhiteSpace(cleanedValue))
        {
            return "empty_term";
        }

        return LooksLikeInternalEnemyToken(rawValue) || LooksLikeInternalEnemyToken(cleanedValue)
            ? "internal_token"
            : null;
    }

    private void RecordEnemyFieldFilter(
        int enemyIndex,
        string enemyId,
        string field,
        string source,
        string detail,
        List<IReadOnlyDictionary<string, object?>> diagnostics)
    {
        diagnostics.Add(CreateEnemyExportDiagnostic(enemyIndex, enemyId, field, source, detail, "filtered"));
        _logger?.Warn($"Enemy field filtered enemy={enemyId} field={field} source={source} detail={detail}");
    }

    private static string NormalizeEnemyDiagnosticField(string path)
    {
        return Regex.Replace(path, "^enemies\\[[0-9]+\\]\\.", string.Empty, RegexOptions.CultureInvariant);
    }

    private static object? ResolvePowerCollection(object? source)
    {
        if (source is null)
        {
            return null;
        }

        foreach (var memberName in new[] { "Powers", "StatusEffects", "Buffs", "Debuffs" })
        {
            var collection = GetMemberValue(source, memberName);
            if (collection is not null)
            {
                return collection;
            }
        }

        foreach (var nestedName in new[] { "Creature", "Monster" })
        {
            var nested = GetMemberValue(source, nestedName);
            if (nested is null || ReferenceEquals(nested, source))
            {
                continue;
            }

            var collection = ResolvePowerCollection(nested);
            if (collection is not null)
            {
                return collection;
            }
        }

        return null;
    }

    private RuntimePowerState? DescribePower(
        object? power,
        string path,
        TextDiagnosticsCollector textDiagnostics,
        int? enemyIndex = null,
        string? enemyId = null,
        List<IReadOnlyDictionary<string, object?>>? enemyDiagnostics = null)
    {
        var name = ConvertToText(
            GetMemberValue(power, "Name") ?? GetMemberValue(power, "DisplayName") ?? power,
            $"{path}.name",
            textDiagnostics,
            "Name",
            "DisplayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var descriptionValue =
            GetFirstSafeDescriptionMemberValue(power, "Description", "SmartDescription", "DynamicDescription", "StaticDescription")
            ?? GetMemberValue(power, "RulesText")
            ?? GetMemberValue(power, "Text");
        var canonicalPowerId = ResolvePowerCanonicalId(power);
        descriptionValue ??= TryResolvePowerDescriptionLocString(canonicalPowerId);
        var raw = ConvertDescriptionTemplateToText(
            descriptionValue,
            $"{path}.description",
            textDiagnostics,
            "Description",
            "SmartDescription",
            "DynamicDescription",
            "StaticDescription",
            "RulesText",
            "Text");
        var rendered = ConvertRenderedDescriptionToText(
            GetFirstSafeDescriptionMemberValue(power, "RenderedDescription", "RenderedText", "DisplayDescription", "DescriptionRendered")
            ?? descriptionValue,
            $"{path}.description_rendered",
            textDiagnostics,
            "RenderedDescription",
            "RenderedText",
            "DisplayDescription",
            "DescriptionRendered");
        var vars = ExtractDescriptionVariables(power, raw, ResolvePowerDescriptionSeedVariables(power, canonicalPowerId));
        var renderOutcome = RenderDescription(raw, rendered, vars);
        var canonicalDescription = ChooseCanonicalDescription(renderOutcome.Text, raw);
        var glossary = ExtractGlossaryAnchors(
            canonicalPowerId,
            name,
            new[] { renderOutcome.Text, raw },
            keywords: null,
            traits: null,
            path: $"{path}.glossary",
            power);
        canonicalDescription ??= ResolveCanonicalPowerDescriptionFromGlossary(canonicalPowerId, name, glossary);
        if (string.IsNullOrWhiteSpace(canonicalDescription))
        {
            _logger?.Warn(
                $"Power description unresolved power={canonicalPowerId ?? name} path={path} " +
                $"members={DescribeObjectMembersForLog(power)}");
        }
        if (enemyIndex is not null)
        {
            glossary = FilterPowerGlossary(
                canonicalPowerId,
                name,
                canonicalDescription,
                glossary,
                path,
                enemyIndex,
                enemyId,
                enemyDiagnostics);
        }
        LogDescriptionDiagnostics(
            kind: "power",
            identifier: canonicalPowerId ?? name,
            path: path,
            raw: raw,
            rendered: canonicalDescription,
            quality: renderOutcome.Quality,
            source: renderOutcome.Source,
            variables: vars,
            glossary: glossary);

        return new RuntimePowerState(
            PowerId: canonicalPowerId ?? name,
            Name: name,
            Amount: GetNullableInt(power, "Amount")
                    ?? GetNullableInt(power, "Stacks")
                    ?? GetNullableInt(power, "Value"),
            Description: canonicalDescription,
            CanonicalPowerId: canonicalPowerId,
            Glossary: glossary);
    }

    private static string? ResolveCanonicalPowerDescriptionFromGlossary(
        string? canonicalPowerId,
        string powerName,
        IReadOnlyList<GlossaryAnchor> glossary)
    {
        var normalizedPowerId = NormalizeGlossaryId(canonicalPowerId);
        var normalizedPowerName = NormalizeComparisonText(powerName);
        foreach (var anchor in glossary)
        {
            if (string.IsNullOrWhiteSpace(anchor.Hint) ||
                ContainsDescriptionPlaceholder(anchor.Hint) ||
                string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedAnchorId = NormalizeGlossaryId(anchor.GlossaryId);
            var normalizedDisplay = NormalizeComparisonText(anchor.DisplayText);
            var identityMatch =
                (!string.IsNullOrWhiteSpace(normalizedPowerId) &&
                 string.Equals(normalizedAnchorId, normalizedPowerId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(normalizedPowerName) &&
                 string.Equals(normalizedDisplay, normalizedPowerName, StringComparison.OrdinalIgnoreCase));
            if (!identityMatch)
            {
                continue;
            }

            return NormalizeDescriptionText(anchor.Hint);
        }

        return null;
    }

    private RuntimePotionState? DescribePotion(object? potion, string path, TextDiagnosticsCollector textDiagnostics)
    {
        var source = GetMemberValue(potion, "Potion")
                     ?? GetMemberValue(potion, "Model")
                     ?? GetMemberValue(potion, "PotionModel")
                     ?? GetMemberValue(potion, "CanonicalInstance")
                     ?? potion;
        var name = ConvertToText(
            GetMemberValue(source, "Title")
            ?? GetMemberValue(source, "Name")
            ?? GetMemberValue(source, "DisplayName")
            ?? potion,
            $"{path}.name",
            textDiagnostics,
            "Title",
            "Name",
            "DisplayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var hoverTipDescriptionValue = GetSafeDescriptionMemberValue(GetMemberValue(source, "HoverTip"), "Description");
        var descriptionValue =
            GetFirstSafeDescriptionMemberValue(source, "Description", "SmartDescription", "DynamicDescription", "StaticDescription")
            ?? GetMemberValue(source, "RulesText")
            ?? GetMemberValue(source, "Text");
        var canonicalPotionId = ResolvePotionCanonicalId(source);
        descriptionValue ??= TryResolvePotionDescriptionLocString(canonicalPotionId);
        var boundDescriptionValue = TryBindLocStringWithDynamicVars(descriptionValue, source);
        var raw = ConvertDescriptionTemplateToText(
            descriptionValue ?? hoverTipDescriptionValue,
            $"{path}.description",
            textDiagnostics,
            "Description",
            "DynamicDescription",
            "StaticDescription",
            "RulesText",
            "Text");
        var rendered = NormalizeDescriptionText(ConvertRenderedDescriptionToText(
            GetFirstSafeDescriptionMemberValue(source, "RenderedDescription", "RenderedText", "DisplayDescription", "DescriptionRendered")
            ?? boundDescriptionValue,
            $"{path}.rendered",
            textDiagnostics,
            "RenderedDescription",
            "RenderedText",
            "DisplayDescription"));
        if (string.IsNullOrWhiteSpace(rendered))
        {
            var hoverTipRendered = NormalizeDescriptionText(ConvertRenderedDescriptionToText(
                hoverTipDescriptionValue,
                $"{path}.hover_tip_description",
                textDiagnostics,
                "Description"));
            if (!string.IsNullOrWhiteSpace(hoverTipRendered) &&
                !ContainsDescriptionPlaceholder(hoverTipRendered))
            {
                rendered = hoverTipRendered;
            }
        }
        var vars = ExtractDescriptionVariables(
            source,
            raw,
            ExtractDescriptionVariablesFromLocString(descriptionValue, "loc_string", source)
                .Concat(ExtractDescriptionVariablesFromLocString(boundDescriptionValue, "bound_loc_string", source))
                .ToArray());
        var renderOutcome = RenderDescription(raw, rendered, vars);
        var canonicalDescription = ChooseCanonicalDescription(renderOutcome.Text, raw);
        var glossary = ExtractGlossaryAnchors(
            canonicalPotionId,
            name,
            new[] { renderOutcome.Text, raw },
            keywords: null,
            traits: null,
            path: $"{path}.glossary",
            source,
            potion);
        glossary = FilterPotionGlossary(
            canonicalPotionId,
            name,
            canonicalDescription,
            glossary,
            path);
        LogDescriptionDiagnostics(
            kind: "potion",
            identifier: canonicalPotionId ?? name,
            path: path,
            raw: raw,
            rendered: canonicalDescription,
            quality: renderOutcome.Quality,
            source: renderOutcome.Source,
            variables: vars,
            glossary: glossary);
        if (string.IsNullOrWhiteSpace(canonicalDescription))
        {
            _logger?.Warn($"Potion description unavailable potion={canonicalPotionId ?? name} path={path} stage=no_runtime_description");
        }

        return new RuntimePotionState(
            Name: name,
            Description: canonicalDescription,
            CanonicalPotionId: canonicalPotionId,
            Glossary: glossary);
    }

    private IReadOnlyList<GlossaryAnchor> FilterPotionGlossary(
        string? canonicalPotionId,
        string potionName,
        string? canonicalDescription,
        IReadOnlyList<GlossaryAnchor> glossary,
        string path)
    {
        if (glossary.Count == 0)
        {
            return glossary;
        }

        var filtered = new List<GlossaryAnchor>(glossary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSemantic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in glossary)
        {
            var reason = ClassifyPotionGlossaryFilterReason(anchor, canonicalPotionId, potionName, canonicalDescription);
            if (reason is not null)
            {
                LogPotionGlossaryFilter(canonicalPotionId ?? potionName, path, anchor, reason);
                continue;
            }

            var dedupeKey = string.Join("|",
                NormalizeComparisonText(anchor.GlossaryId),
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!seen.Add(dedupeKey))
            {
                LogPotionGlossaryFilter(canonicalPotionId ?? potionName, path, anchor, "duplicate_glossary");
                continue;
            }

            var semanticKey = string.Join("|",
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!string.IsNullOrWhiteSpace(semanticKey) && !seenSemantic.Add(semanticKey))
            {
                LogPotionGlossaryFilter(canonicalPotionId ?? potionName, path, anchor, "duplicate_semantic_glossary");
                continue;
            }

            filtered.Add(anchor);
        }

        return filtered;
    }

    private static string? ClassifyPotionGlossaryFilterReason(
        GlossaryAnchor anchor,
        string? canonicalPotionId,
        string potionName,
        string? canonicalDescription)
    {
        if (string.IsNullOrWhiteSpace(anchor.Hint))
        {
            return "empty_hint";
        }

        if (string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_hint";
        }

        if (string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback_builtin";
        }

        if (ContainsDescriptionPlaceholder(anchor.Hint))
        {
            return "template_hint";
        }

        var normalizedGlossaryId = NormalizeGlossaryId(anchor.GlossaryId);
        var normalizedPotionId = NormalizeGlossaryId(canonicalPotionId);
        var normalizedDisplay = NormalizeComparisonText(anchor.DisplayText);
        var normalizedName = NormalizeComparisonText(potionName);
        if (!string.IsNullOrWhiteSpace(normalizedDisplay) &&
            string.Equals(normalizedDisplay, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(normalizedGlossaryId) &&
                string.Equals(normalizedGlossaryId, normalizedPotionId, StringComparison.OrdinalIgnoreCase))
            {
                return "duplicate_potion_identity";
            }

            if (string.Equals(NormalizeComparisonText(anchor.Hint), NormalizeComparisonText(canonicalDescription), StringComparison.OrdinalIgnoreCase))
            {
                return "duplicate_potion_description";
            }
        }

        return null;
    }

    private void LogPotionGlossaryFilter(string identifier, string path, GlossaryAnchor anchor, string reason)
    {
        var message =
            $"Potion glossary filtered potion={identifier} path={path} glossary_id={anchor.GlossaryId} " +
            $"display_text={anchor.DisplayText} source={anchor.Source ?? "unknown"} reason={reason} hint=\"{AbbreviateForLog(anchor.Hint)}\"";
        _logger?.Warn(message);
    }

    private IReadOnlyList<GlossaryAnchor> FilterPowerGlossary(
        string? canonicalPowerId,
        string powerName,
        string? canonicalDescription,
        IReadOnlyList<GlossaryAnchor> glossary,
        string path,
        int? enemyIndex = null,
        string? enemyId = null,
        List<IReadOnlyDictionary<string, object?>>? enemyDiagnostics = null)
    {
        if (glossary.Count == 0)
        {
            return glossary;
        }

        var filtered = new List<GlossaryAnchor>(glossary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in glossary)
        {
            var reason = ClassifyPowerGlossaryFilterReason(anchor, canonicalPowerId, powerName, canonicalDescription);
            if (reason is not null)
            {
                LogPowerGlossaryFilter(canonicalPowerId ?? powerName, path, anchor, reason);
                RecordEnemyPowerGlossaryFilter(enemyIndex, enemyId, path, anchor, reason, enemyDiagnostics);
                continue;
            }

            var dedupeKey = string.Join("|",
                NormalizeComparisonText(anchor.GlossaryId),
                NormalizeComparisonText(anchor.DisplayText),
                NormalizeComparisonText(anchor.Hint));
            if (!seen.Add(dedupeKey))
            {
                LogPowerGlossaryFilter(canonicalPowerId ?? powerName, path, anchor, "duplicate_glossary");
                RecordEnemyPowerGlossaryFilter(enemyIndex, enemyId, path, anchor, "duplicate_glossary", enemyDiagnostics);
                continue;
            }

            filtered.Add(anchor);
        }

        return filtered;
    }

    private static string? ClassifyPowerGlossaryFilterReason(
        GlossaryAnchor anchor,
        string? canonicalPowerId,
        string powerName,
        string? canonicalDescription)
    {
        if (string.IsNullOrWhiteSpace(anchor.Hint))
        {
            return "empty_hint";
        }

        if (string.Equals(anchor.Source, "missing_hint", StringComparison.OrdinalIgnoreCase))
        {
            return "missing_hint";
        }

        if (string.Equals(anchor.Source, "fallback_builtin", StringComparison.OrdinalIgnoreCase))
        {
            return "fallback_builtin";
        }

        if (ContainsDescriptionPlaceholder(anchor.Hint))
        {
            return "template_hint";
        }

        var normalizedGlossaryId = NormalizeGlossaryId(anchor.GlossaryId);
        var normalizedPowerId = NormalizeGlossaryId(canonicalPowerId);
        var normalizedDisplay = NormalizeComparisonText(anchor.DisplayText);
        var normalizedName = NormalizeComparisonText(powerName);
        if (!string.IsNullOrWhiteSpace(canonicalDescription) &&
            string.Equals(NormalizeComparisonText(anchor.Hint), NormalizeComparisonText(canonicalDescription), StringComparison.OrdinalIgnoreCase))
        {
            return "duplicate_power_description";
        }

        if (!string.IsNullOrWhiteSpace(normalizedDisplay) &&
            string.Equals(normalizedDisplay, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(normalizedGlossaryId) &&
                string.Equals(normalizedGlossaryId, normalizedPowerId, StringComparison.OrdinalIgnoreCase))
            {
                return "duplicate_power_identity";
            }
        }

        return null;
    }

    private void LogPowerGlossaryFilter(string identifier, string path, GlossaryAnchor anchor, string reason)
    {
        var message =
            $"Power glossary filtered power={identifier} path={path} glossary_id={anchor.GlossaryId} " +
            $"display_text={anchor.DisplayText} source={anchor.Source ?? "unknown"} reason={reason} hint=\"{AbbreviateForLog(anchor.Hint)}\"";
        _logger?.Warn(message);
    }

    private void RecordEnemyPowerGlossaryFilter(
        int? enemyIndex,
        string? enemyId,
        string path,
        GlossaryAnchor anchor,
        string reason,
        List<IReadOnlyDictionary<string, object?>>? enemyDiagnostics)
    {
        if (enemyIndex is null || string.IsNullOrWhiteSpace(enemyId) || enemyDiagnostics is null)
        {
            return;
        }

        RecordEnemyFieldFilter(
            enemyIndex.Value,
            enemyId!,
            NormalizeEnemyDiagnosticField(path),
            $"power_glossary_{reason}",
            $"glossary_id={anchor.GlossaryId} source={anchor.Source ?? "unknown"} display_text=\"{AbbreviateForLog(anchor.DisplayText)}\"",
            enemyDiagnostics);
    }

    private static IReadOnlyList<DescriptionVariable> ResolveCardDescriptionSeedVariables(object? card, string? raw, IReadOnlyList<string>? keywords)
    {
        var variables = new List<DescriptionVariable>();
        foreach (var key in new[] { "damage", "block", "draw", "strength", "magic" })
        {
            if (ShouldSeedDescriptionVariable(key, raw, keywords))
            {
                AddSeedVariable(variables, key, card, "member_alias", null);
            }
        }

        if (keywords is not null)
        {
            foreach (var keyword in keywords)
            {
                var normalized = NormalizeGlossaryId(keyword);
                if (normalized is not null &&
                    (string.Equals(normalized, "damage", StringComparison.Ordinal) ||
                     string.Equals(normalized, "block", StringComparison.Ordinal) ||
                     string.Equals(normalized, "draw", StringComparison.Ordinal) ||
                     string.Equals(normalized, "strength", StringComparison.Ordinal)))
                {
                    AddSeedVariable(variables, normalized, card, "keyword_seed", keyword);
                }
            }
        }

        return DeduplicateVariables(variables);
    }

    private static bool ShouldSeedDescriptionVariable(string key, string? raw, IReadOnlyList<string>? keywords)
    {
        if (keywords is not null && keywords.Any(keyword => string.Equals(NormalizeGlossaryId(keyword), key, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (KnownGlossarySpecs.TryGetValue(key, out var spec))
        {
            return spec.Terms.Any(term => raw.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return raw.Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<DescriptionVariable> ResolvePowerDescriptionSeedVariables(object? power, string? canonicalPowerId)
    {
        var variables = new List<DescriptionVariable>();
        AddSeedVariable(variables, "amount", power, "member_alias", null);
        var normalizedPowerId = NormalizeGlossaryId(canonicalPowerId);
        if (normalizedPowerId is not null &&
            DescriptionVariableMemberAliases.ContainsKey(normalizedPowerId))
        {
            AddSeedVariable(variables, normalizedPowerId, power, "power_id", canonicalPowerId);
        }

        return DeduplicateVariables(variables);
    }

    private static void AddSeedVariable(List<DescriptionVariable> variables, string key, object? source, string sourceLabel, string? placeholder)
    {
        var resolution = ResolveDescriptionVariableValue(source, key);
        if (resolution.Value is null)
        {
            return;
        }

        variables.Add(new DescriptionVariable(key, resolution.Value, resolution.Source ?? sourceLabel, placeholder, resolution.SemanticKind));
    }

    private static IReadOnlyList<DescriptionVariable> ExtractDescriptionVariables(
        object? source,
        string? raw,
        IReadOnlyList<DescriptionVariable>? seedVariables = null)
    {
        var variables = new List<DescriptionVariable>();
        if (seedVariables is not null)
        {
            variables.AddRange(seedVariables);
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (Match match in PlaceholderRegex.Matches(raw))
            {
                var placeholder = match.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(placeholder))
                {
                    continue;
                }

                var key = NormalizeDescriptionVariableKey(placeholder);
                var resolution = ResolveDescriptionVariableValue(source, key);
                variables.Add(new DescriptionVariable(
                    key,
                    resolution.Value,
                    resolution.Source ?? "description_placeholder",
                    placeholder,
                    resolution.SemanticKind));
            }
        }

        return DeduplicateVariables(variables);
    }

    private static IReadOnlyList<DescriptionVariable> DeduplicateVariables(IEnumerable<DescriptionVariable> variables)
    {
        return variables
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Key))
            .GroupBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var preferred = group.FirstOrDefault(variable => variable.Value is not null && !string.IsNullOrWhiteSpace(variable.SemanticKind))
                    ?? group.FirstOrDefault(variable => variable.Value is not null);
                if (preferred is not null)
                {
                    return preferred;
                }

                return group.First();
            })
            .ToArray();
    }

    private static VariableResolution ResolveDescriptionVariableValue(object? source, string? key)
    {
        if (source is null || string.IsNullOrWhiteSpace(key))
        {
            return new VariableResolution(null, null, null);
        }

        if (!DescriptionVariableMemberAliases.TryGetValue(key, out var aliases))
        {
            aliases = new[] { key };
        }

        var semanticKind = ResolveDescriptionSemanticKind(source, key, key, key);

        foreach (var match in EnumerateDescriptionVariableMatches(source, aliases))
        {
            return new VariableResolution(match.Value, match.Source, semanticKind ?? match.SemanticKind);
        }

        return new VariableResolution(null, null, semanticKind);
    }

    private static string NormalizeDescriptionVariableKey(string? rawKey)
    {
        var normalized = NormalizeGlossaryId(rawKey);
        return normalized switch
        {
            "attackdamage" => "damage",
            "currentdamage" => "damage",
            "basedamage" => "damage",
            "damageamount" => "damage",
            "currentblock" => "block",
            "baseblock" => "block",
            "blockamount" => "block",
            "cards" => "draw",
            "drawamount" => "draw",
            "drawcount" => "draw",
            "cardstodraw" => "draw",
            "carddraw" => "draw",
            "magicnumber" => "magic",
            "magicvalue" => "magic",
            "strengthamount" => "strength",
            "strengthpower" => "strength",
            _ => normalized ?? string.Empty,
        };
    }

    private static string? ResolveDescriptionSemanticKind(object? source, string? normalizedKey, string? rawKey, string? placeholder)
    {
        foreach (var candidate in new[]
                 {
                     normalizedKey,
                     NormalizeGlossaryId(rawKey),
                     NormalizeGlossaryId(placeholder),
                 })
        {
            switch (candidate)
            {
                case "energy":
                case "energyamount":
                case "energygain":
                    return "energy";
                case "draw":
                case "cards":
                case "drawcount":
                case "cardstodraw":
                    return "cards";
                case "gold":
                case "goldamount":
                case "goldcost":
                case "price":
                case "cost":
                case "purchaseprice":
                case "priceincrease":
                case "costincrease":
                case "pricedelta":
                    return "gold";
            }
        }

        var normalizedTypeName = NormalizeGlossaryId(GetTypeName(source));
        if ((normalizedKey is "amount" or "value" || NormalizeGlossaryId(rawKey) is "amount" or "value") &&
            normalizedTypeName is not null &&
            normalizedTypeName.Contains("merchantcardremoval", StringComparison.Ordinal))
        {
            return "gold";
        }

        return null;
    }

    private static RenderOutcome RenderCardDescription(
        string? raw,
        string? gameRendered,
        string? runtimeRendered,
        IReadOnlyList<DescriptionVariable>? variables)
    {
        var preferredGameRendered = NormalizeDescriptionText(gameRendered);
        if (!string.IsNullOrWhiteSpace(preferredGameRendered) && !ContainsDescriptionPlaceholder(preferredGameRendered))
        {
            return new RenderOutcome(preferredGameRendered, "resolved", "game_rendered");
        }

        var preferredRuntimeRendered = NormalizeDescriptionText(runtimeRendered);
        if (!string.IsNullOrWhiteSpace(preferredRuntimeRendered) && !ContainsDescriptionPlaceholder(preferredRuntimeRendered))
        {
            return new RenderOutcome(preferredRuntimeRendered, "resolved", "runtime_rendered");
        }

        var template = RenderTemplateDescription(raw, variables);
        if (string.IsNullOrWhiteSpace(template) &&
            string.IsNullOrWhiteSpace(preferredRuntimeRendered) &&
            string.IsNullOrWhiteSpace(preferredGameRendered))
        {
            return new RenderOutcome(null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(template) && !ContainsDescriptionPlaceholder(template))
        {
            return new RenderOutcome(
                template,
                "resolved",
                "rendered_from_vars");
        }

        var hasResolvedVariables = (variables ?? Array.Empty<DescriptionVariable>()).Any(variable => variable.Value is not null);
        return new RenderOutcome(
            template ?? preferredRuntimeRendered ?? preferredGameRendered,
            hasResolvedVariables ? "partial" : "template_fallback",
            !string.IsNullOrWhiteSpace(preferredGameRendered)
                ? "game_template_fallback"
                : !string.IsNullOrWhiteSpace(preferredRuntimeRendered)
                    ? "runtime_template_fallback"
                    : "raw_template");
    }

    private static RenderOutcome RenderDescription(
        string? raw,
        string? runtimeRendered,
        IReadOnlyList<DescriptionVariable>? variables)
    {
        var preferred = NormalizeDescriptionText(runtimeRendered);
        var template = RenderTemplateDescription(raw, variables);
        if (string.IsNullOrWhiteSpace(template) && string.IsNullOrWhiteSpace(preferred))
        {
            return new RenderOutcome(null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(template) && !ContainsDescriptionPlaceholder(template))
        {
            return new RenderOutcome(
                template,
                "resolved",
                preferred is not null ? "runtime_rendered_with_markdown_glossary" : "rendered_from_vars");
        }

        if (!string.IsNullOrWhiteSpace(preferred) && !ContainsDescriptionPlaceholder(preferred))
        {
            return new RenderOutcome(preferred, "resolved", "runtime_rendered");
        }

        var hasResolvedVariables = (variables ?? Array.Empty<DescriptionVariable>()).Any(variable => variable.Value is not null);
        return new RenderOutcome(
            template ?? preferred,
            hasResolvedVariables ? "partial" : "template_fallback",
            preferred is not null ? "runtime_template_fallback" : "raw_template");
    }

    private static string? RenderTemplateDescription(string? raw, IReadOnlyList<DescriptionVariable>? variables)
    {
        var template = raw?.Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var variableMap = (variables ?? Array.Empty<DescriptionVariable>())
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Key))
            .GroupBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var rendered = PlaceholderRegex.Replace(template, match =>
        {
            var key = NormalizeDescriptionVariableKey(match.Groups["name"].Value);
            if (variableMap.TryGetValue(key, out var variable) && variable.Value is not null)
            {
                return FormatRenderedDescriptionVariable(template, match, variable);
            }

            return match.Value;
        });

        return NormalizeDescriptionText(rendered);
    }

    private static string FormatRenderedDescriptionVariable(string template, Match match, DescriptionVariable variable)
    {
        var valueText = variable.Value?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return match.Value;
        }

        if (string.IsNullOrWhiteSpace(variable.SemanticKind) ||
            !DescriptionSemanticDisplayText.TryGetValue(variable.SemanticKind, out var displayText) ||
            string.IsNullOrWhiteSpace(displayText) ||
            HasNearbySemanticDisplayText(template, match.Index, match.Length, displayText))
        {
            return valueText;
        }

        return $"{valueText}{displayText}";
    }

    private static bool HasNearbySemanticDisplayText(string template, int index, int length, string displayText)
    {
        const int contextSpan = 12;
        var start = Math.Max(0, index - contextSpan);
        var end = Math.Min(template.Length, index + length + contextSpan);
        var context = template[start..end];
        return context.Contains(displayText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeDescriptionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeDescriptionRichText(text).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeEnemyUiText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return NormalizeDescriptionText(text.Replace('×', 'x'));
    }

    private static string NormalizeDescriptionRichText(string text)
    {
        var normalized = NormalizeInlineIconPaths(text);
        for (var pass = 0; pass < 4; pass += 1)
        {
            var replaced = RichTextPairRegex.Replace(normalized, match =>
            {
                var tag = match.Groups["tag"].Value;
                var content = match.Groups["content"].Value;
                if (string.Equals(tag, "gold", StringComparison.OrdinalIgnoreCase))
                {
                    return $"**{content.Trim()}**";
                }

                var trimmedContent = content.Trim();
                var spec = ResolveKnownGlossarySpec(NormalizeGlossaryId(tag), trimmedContent);
                if (spec is not null &&
                    !string.IsNullOrWhiteSpace(trimmedContent) &&
                    (PlaceholderRegex.IsMatch(trimmedContent) || int.TryParse(trimmedContent, out _)) &&
                    !trimmedContent.Contains(spec.DisplayText, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{trimmedContent}{spec.DisplayText}";
                }

                return content;
            });
            if (string.Equals(replaced, normalized, StringComparison.Ordinal))
            {
                break;
            }

            normalized = replaced;
        }

        normalized = RichTextTagRegex.Replace(normalized, string.Empty);
        return CollapseRepeatedEnergyLabels(normalized);
    }

    private static string NormalizeInlineIconPaths(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !EnergyIconPathRegex.IsMatch(text))
        {
            return text;
        }

        var energyLabel = KnownGlossarySpecs["energy"].DisplayText;
        return EnergyIconRunRegex.Replace(text, match =>
        {
            var count = EnergyIconPathRegex.Matches(match.Value).Count;
            return count > 0 ? $"{count}{energyLabel}" : match.Value;
        });
    }

    private static string CollapseRepeatedEnergyLabels(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !EnergyLabelRunRegex.IsMatch(text))
        {
            return text;
        }

        return EnergyLabelRunRegex.Replace(text, match =>
        {
            var total = 0;
            foreach (Capture capture in match.Groups["count"].Captures)
            {
                if (int.TryParse(capture.Value, out var value))
                {
                    total += value;
                }
            }

            return total > 0 ? $"{total}能量" : match.Value;
        });
    }

    private static string? ChooseCanonicalDescription(string? rendered, string? raw)
    {
        return NormalizeDescriptionText(rendered) ?? NormalizeDescriptionText(raw);
    }

    private static bool ContainsDescriptionPlaceholder(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && PlaceholderRegex.IsMatch(text);
    }

    private static IEnumerable<VariableResolution> EnumerateDescriptionVariableMatches(object source, IReadOnlyList<string> aliases)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, string Path, int Depth)>();
        queue.Enqueue((source, "card", 0));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.Node))
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                var direct = GetNullableInt(current.Node, alias);
                if (direct is not null)
                {
                    yield return new VariableResolution(direct, $"member:{current.Path}.{alias}");
                }

                var method = InvokeNullableIntMethod(current.Node, alias);
                if (method is not null)
                {
                    yield return new VariableResolution(method, $"method:{current.Path}.{alias}()");
                }
            }

            foreach (var fuzzy in EnumerateNumericMembersByHint(current.Node, aliases, current.Path))
            {
                yield return fuzzy;
            }

            if (current.Depth >= 2)
            {
                continue;
            }

            foreach (var nestedName in DescriptionSearchNestedMembers)
            {
                var nested = GetMemberValue(current.Node, nestedName);
                if (nested is null || nested is string || nested is ValueType)
                {
                    continue;
                }

                if (nested is IEnumerable enumerable && nested is not IDictionary)
                {
                    var nestedIndex = 0;
                    foreach (var item in enumerable)
                    {
                        if (item is null || item is string || item is ValueType)
                        {
                            nestedIndex += 1;
                            continue;
                        }

                        queue.Enqueue((item, $"{current.Path}.{nestedName}[{nestedIndex}]", current.Depth + 1));
                        nestedIndex += 1;
                    }

                    continue;
                }

                queue.Enqueue((nested, $"{current.Path}.{nestedName}", current.Depth + 1));
            }
        }
    }

    private static IEnumerable<VariableResolution> EnumerateNumericMembersByHint(object source, IReadOnlyList<string> aliases, string path)
    {
        var type = source.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var aliasSet = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase);
        var tokenSet = aliases
            .SelectMany(alias => SplitIdentifierTokens(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var property in type.GetProperties(flags).Where(property => property.GetIndexParameters().Length == 0))
        {
            if (!IsNumericType(property.PropertyType))
            {
                continue;
            }

            if (!NameMatchesDescriptionHint(property.Name, aliasSet, tokenSet))
            {
                continue;
            }

            var value = GetNullableInt(source, property.Name);
            if (value is not null)
            {
                yield return new VariableResolution(value, $"property_hint:{path}.{property.Name}");
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            if (!IsNumericType(field.FieldType))
            {
                continue;
            }

            if (!NameMatchesDescriptionHint(field.Name, aliasSet, tokenSet))
            {
                continue;
            }

            var value = GetNullableInt(source, field.Name);
            if (value is not null)
            {
                yield return new VariableResolution(value, $"field_hint:{path}.{field.Name}");
            }
        }
    }

    private static int? InvokeNullableIntMethod(object source, string alias)
    {
        foreach (var methodName in new[] { alias, $"Get{alias}", $"Compute{alias}", $"Resolve{alias}", $"GetCurrent{alias}" })
        {
            var method = source.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method is null || !IsNumericType(method.ReturnType))
            {
                continue;
            }

            try
            {
                var value = method.Invoke(source, null);
                if (value is not null)
                {
                    return Convert.ToInt32(value);
                }
            }
            catch
            {
                // ignore dynamic runtime invocation failures
            }
        }

        return null;
    }

    private static bool NameMatchesDescriptionHint(string name, ISet<string> aliasSet, IReadOnlyList<string> tokenSet)
    {
        if (aliasSet.Contains(name))
        {
            return true;
        }

        return tokenSet.All(token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitIdentifierTokens(string value)
    {
        return Regex.Matches(value, "[A-Z]?[a-z]+|[0-9]+|[A-Z]+(?![a-z])")
            .Select(match => match.Value)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
    }

    private static bool IsNumericType(Type type)
    {
        var candidate = Nullable.GetUnderlyingType(type) ?? type;
        return candidate == typeof(int) ||
               candidate == typeof(short) ||
               candidate == typeof(long) ||
               candidate == typeof(byte);
    }

    private IReadOnlyList<GlossaryAnchor> ExtractGlossaryAnchors(
        string? canonicalId,
        string? displayName,
        IEnumerable<string?> texts,
        IReadOnlyList<string>? keywords,
        IReadOnlyList<string>? traits,
        string path,
        params object?[] hintSources)
    {
        var candidates = new List<GlossaryCandidate>();
        AddGlossaryCandidateFromId(candidates, canonicalId, displayName, "canonical_id");

        foreach (var keyword in keywords ?? Array.Empty<string>())
        {
            AddGlossaryCandidateFromId(candidates, keyword, keyword, "keyword");
        }

        foreach (var trait in traits ?? Array.Empty<string>())
        {
            AddGlossaryCandidateFromId(candidates, trait, trait, "trait");
        }

        var normalizedTexts = texts
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToArray();
        foreach (var text in normalizedTexts)
        {
            foreach (var spec in KnownGlossarySpecs.Values)
            {
                if (spec.Terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    AddGlossaryCandidate(candidates, spec.GlossaryId, spec.DisplayText, "text_match");
                }
            }
        }

        foreach (var hoverTip in EnumerateGlossaryHoverTips(hintSources))
        {
            var tipId = ConvertToText(GetMemberValue(hoverTip, "Id"), "Id");
            var tipTitle = NormalizeDescriptionText(ConvertToText(
                GetMemberValue(hoverTip, "Title")
                ?? GetMemberValue(hoverTip, "Name")
                ?? GetMemberValue(hoverTip, "Label"),
                path: "glossary.hover_tip_title",
                textDiagnostics: null,
                preferredMembers: new[] { "Title", "Name", "Label", "Text" }));
            var normalizedTipId = NormalizeGlossaryId(tipId);
            if (string.IsNullOrWhiteSpace(normalizedTipId) && !string.IsNullOrWhiteSpace(tipTitle))
            {
                normalizedTipId = NormalizeGlossaryId(Regex.Replace(tipTitle, @"\d+$", string.Empty));
            }
            else if (LooksLikeSyntheticGlossaryId(normalizedTipId) && !string.IsNullOrWhiteSpace(tipTitle))
            {
                normalizedTipId = NormalizeGlossaryId(Regex.Replace(tipTitle, @"\d+$", string.Empty));
            }

            if (string.IsNullOrWhiteSpace(normalizedTipId) || string.IsNullOrWhiteSpace(tipTitle))
            {
                continue;
            }

            var matchesText = normalizedTexts.Any(text => text.Contains(tipTitle, StringComparison.OrdinalIgnoreCase));
            if (!matchesText && !KnownGlossarySpecs.ContainsKey(normalizedTipId))
            {
                continue;
            }

            AddGlossaryCandidateFromId(candidates, normalizedTipId, tipTitle, "runtime_hover_tip");
        }

        var uniqueCandidates = candidates
            .GroupBy(candidate => candidate.GlossaryId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var resolvedAnchors = new List<GlossaryAnchor>(uniqueCandidates.Length);
        foreach (var candidate in uniqueCandidates)
        {
            resolvedAnchors.Add(BuildGlossaryAnchor(candidate, path, hintSources));
        }

        return resolvedAnchors
            .ToArray();
    }

    private static void AddGlossaryCandidateFromId(List<GlossaryCandidate> candidates, string? rawId, string? displayText, string matchSource)
    {
        var normalizedDisplayText = NormalizeDescriptionText(displayText) ?? displayText?.Trim();
        var normalizedId = NormalizeGlossaryId(rawId);
        if (string.IsNullOrWhiteSpace(normalizedId) && !string.IsNullOrWhiteSpace(normalizedDisplayText))
        {
            normalizedId = NormalizeGlossaryId(normalizedDisplayText);
        }
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return;
        }

        var spec = ResolveKnownGlossarySpec(normalizedId, normalizedDisplayText);
        if (spec is not null)
        {
            AddGlossaryCandidate(candidates, spec.GlossaryId, spec.DisplayText, matchSource);
            return;
        }

        if (string.IsNullOrWhiteSpace(normalizedDisplayText))
        {
            return;
        }

        AddGlossaryCandidate(candidates, normalizedId, normalizedDisplayText!, matchSource);
    }

    private static void AddGlossaryCandidate(List<GlossaryCandidate> candidates, string glossaryId, string displayText, string matchSource)
    {
        if (candidates.Any(candidate => string.Equals(candidate.GlossaryId, glossaryId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(new GlossaryCandidate(glossaryId, displayText, matchSource));
    }

    private static GlossarySpec? ResolveKnownGlossarySpec(string? normalizedId, string? displayText)
    {
        if (!string.IsNullOrWhiteSpace(normalizedId) && KnownGlossarySpecs.TryGetValue(normalizedId, out var specById))
        {
            return specById;
        }

        var normalizedDisplay = NormalizeComparisonText(displayText);
        if (string.IsNullOrWhiteSpace(normalizedDisplay))
        {
            return null;
        }

        foreach (var spec in KnownGlossarySpecs.Values)
        {
            if (string.Equals(NormalizeComparisonText(spec.DisplayText), normalizedDisplay, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }

            if (spec.Terms.Any(term => string.Equals(NormalizeComparisonText(term), normalizedDisplay, StringComparison.OrdinalIgnoreCase)))
            {
                return spec;
            }
        }

        return null;
    }

    private GlossaryAnchor BuildGlossaryAnchor(GlossaryCandidate candidate, string path, IReadOnlyList<object?> hintSources)
    {
        var hintResolution = ResolveGlossaryHint(candidate, path, hintSources);
        return new GlossaryAnchor(
            candidate.GlossaryId,
            candidate.DisplayText,
            hintResolution.Hint,
            hintResolution.Source);
    }

    private GlossaryHintResolution ResolveGlossaryHint(GlossaryCandidate candidate, string path, IReadOnlyList<object?> hintSources)
    {
        if (string.Equals(candidate.MatchSource, "trait", StringComparison.OrdinalIgnoreCase) &&
            TryResolveBuiltInGlossaryHint(candidate.GlossaryId, out var traitBuiltInHint))
        {
            return new GlossaryHintResolution(traitBuiltInHint, "fallback_builtin");
        }

        if (TryResolveRuntimeHoverTipHint(candidate, hintSources, out var hoverTipHint))
        {
            return new GlossaryHintResolution(RenderGlossaryHintTemplate(hoverTipHint, candidate, hintSources), "runtime_hover_tip");
        }

        if (TryResolveModelDescriptionHint(candidate, hintSources, out var modelHint))
        {
            return new GlossaryHintResolution(RenderGlossaryHintTemplate(modelHint, candidate, hintSources), "model_description");
        }

        if (TryResolveLocalizationHint(candidate.GlossaryId, out var locStringHint))
        {
            return new GlossaryHintResolution(RenderGlossaryHintTemplate(locStringHint, candidate, hintSources), "loc_string");
        }

        if (TryResolveBuiltInGlossaryHint(candidate.GlossaryId, out var builtInHint))
        {
            return new GlossaryHintResolution(builtInHint, "fallback_builtin");
        }

        _logger?.Warn(
            $"Glossary hint missing glossary_id={candidate.GlossaryId} display_text={candidate.DisplayText} " +
            $"match_source={candidate.MatchSource} path={path}");
        return new GlossaryHintResolution(null, "missing_hint");
    }

    private static string? RenderGlossaryHintTemplate(string? rawHint, GlossaryCandidate candidate, IReadOnlyList<object?> hintSources)
    {
        var normalizedHint = NormalizeDescriptionText(rawHint);
        if (string.IsNullOrWhiteSpace(normalizedHint) || !ContainsDescriptionPlaceholder(normalizedHint))
        {
            return normalizedHint;
        }

        var best = normalizedHint;
        var bestPlaceholderCount = PlaceholderRegex.Matches(normalizedHint).Count;
        foreach (var source in hintSources)
        {
            if (source is null)
            {
                continue;
            }

            var seedVariables = new List<DescriptionVariable>();
            AddSeedVariable(seedVariables, candidate.GlossaryId, source, "glossary_hint_seed", null);
            var rendered = NormalizeDescriptionText(RenderTemplateDescription(
                normalizedHint,
                ExtractDescriptionVariables(source, normalizedHint, seedVariables)));
            if (string.IsNullOrWhiteSpace(rendered))
            {
                continue;
            }

            var placeholderCount = PlaceholderRegex.Matches(rendered).Count;
            if (placeholderCount == 0)
            {
                return rendered;
            }

            if (placeholderCount < bestPlaceholderCount)
            {
                best = rendered;
                bestPlaceholderCount = placeholderCount;
            }
        }

        return best;
    }

    private static bool TryResolveRuntimeHoverTipHint(GlossaryCandidate candidate, IReadOnlyList<object?> hintSources, out string? hint)
    {
        foreach (var hoverTip in EnumerateGlossaryHoverTips(hintSources))
        {
            if (!HoverTipMatchesGlossaryCandidate(hoverTip, candidate))
            {
                continue;
            }

            var description = NormalizeDescriptionText(ConvertDescriptionTemplateToText(
                GetFirstSafeDescriptionMemberValue(
                    hoverTip,
                    "RenderedDescription",
                    "RenderedText",
                    "DisplayDescription",
                    "DescriptionRendered",
                    "Description",
                    "SmartDescription",
                    "Text")
                ?? hoverTip,
                "Description",
                null,
                "RenderedDescription",
                "RenderedText",
                "DisplayDescription",
                "DescriptionRendered",
                "Text"));
            if (!string.IsNullOrWhiteSpace(description))
            {
                hint = description;
                return true;
            }
        }

        hint = null;
        return false;
    }

    private static IEnumerable<object> EnumerateGlossaryHoverTips(IEnumerable<object?> hintSources)
    {
        foreach (var source in hintSources)
        {
            if (source is null)
            {
                continue;
            }

            if (LooksLikeHoverTip(source))
            {
                yield return source;
            }

            foreach (var memberName in GlossaryHintCollectionMembers)
            {
                foreach (var candidate in EnumerateObjects(GetMemberValue(source, memberName)))
                {
                    if (LooksLikeHoverTip(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private static bool LooksLikeHoverTip(object candidate)
    {
        return GetFirstSafeDescriptionMemberValue(
                   candidate,
                   "RenderedDescription",
                   "RenderedText",
                   "DisplayDescription",
                   "DescriptionRendered",
                   "Description",
                   "SmartDescription",
                   "Text") is not null &&
               (GetMemberValue(candidate, "Title") is not null || GetMemberValue(candidate, "Id") is not null);
    }

    private static bool HoverTipMatchesGlossaryCandidate(object hoverTip, GlossaryCandidate candidate)
    {
        var tipId = ConvertToText(GetMemberValue(hoverTip, "Id"), "Id");
        var tipTitle = ConvertToText(GetMemberValue(hoverTip, "Title") ?? GetMemberValue(hoverTip, "Name"), "Title", "Name");
        return MatchesGlossaryCandidate(candidate, tipId, tipTitle);
    }

    private static bool TryResolveModelDescriptionHint(GlossaryCandidate candidate, IReadOnlyList<object?> hintSources, out string? hint)
    {
        foreach (var source in hintSources)
        {
            if (source is null || !SourceMatchesGlossaryCandidate(source, candidate))
            {
                continue;
            }

            foreach (var memberName in GlossaryHintDescriptionMembers)
            {
                var value = GetSafeDescriptionMemberValue(source, memberName)
                    ?? (!LooksLikeRiskyDescriptionMember(memberName) ? GetMemberValue(source, memberName) : null);
                if (value is null)
                {
                    continue;
                }

                var text = NormalizeDescriptionText(ConvertDescriptionTemplateToText(value, "_", null, memberName));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    hint = text;
                    return true;
                }
            }
        }

        hint = null;
        return false;
    }

    private bool TryResolveLocalizationHint(string glossaryId, out string? hint)
    {
        foreach (var key in EnumerateGlossaryLocalizationKeys(glossaryId))
        {
            var locString = TryResolveLocStringByKey(key);
            var text = NormalizeDescriptionText(ConvertDescriptionTemplateToText(locString, key));
            if (!string.IsNullOrWhiteSpace(text))
            {
                hint = text;
                return true;
            }
        }

        hint = null;
        return false;
    }

    private object? TryResolvePowerDescriptionLocString(string? canonicalPowerId)
    {
        return TryResolveDescriptionLocString(canonicalPowerId, new[] { "powers" }, GlossaryLocStringSuffixes);
    }

    private object? TryResolveRelicDescriptionLocString(string? canonicalRelicId)
    {
        return TryResolveDescriptionLocString(canonicalRelicId, new[] { "relics", "relic" }, EntityDescriptionLocStringSuffixes);
    }

    private object? TryResolvePotionDescriptionLocString(string? canonicalPotionId)
    {
        return TryResolveDescriptionLocString(canonicalPotionId, new[] { "potions", "potion" }, EntityDescriptionLocStringSuffixes);
    }

    private object? TryResolveDescriptionLocString(
        string? canonicalId,
        IReadOnlyList<string> tables,
        IReadOnlyList<string> suffixes)
    {
        foreach (var normalizedId in EnumerateDescriptionLocalizationIds(canonicalId))
        {
            foreach (var suffix in suffixes)
            {
                foreach (var table in tables)
                {
                    foreach (var key in new[] { $"{normalizedId}.{suffix}", $"{table}/{normalizedId}.{suffix}", $"{table}.{normalizedId}.{suffix}" })
                    {
                        var locString = key.Contains('/')
                            ? TryResolveLocStringByKey(key)
                            : key.StartsWith($"{table}.", StringComparison.Ordinal)
                                ? TryResolveLocStringByKey(key)
                                : TryResolveLocStringByTableAndKey(table, key);
                        if (IsUsableDescriptionLocString(locString, normalizedId, suffix))
                        {
                            return locString;
                        }
                    }
                }

                var fallback = TryResolveLocStringByKey($"{normalizedId}.{suffix}");
                if (IsUsableDescriptionLocString(fallback, normalizedId, suffix))
                {
                    return fallback;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDescriptionLocalizationIds(string? canonicalId)
    {
        var normalized = NormalizeDescriptionLocalizationId(canonicalId);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
            yield return normalized.ToLowerInvariant();
        }

        if (string.IsNullOrWhiteSpace(canonicalId))
        {
            yield break;
        }

        var trimmed = canonicalId.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        if (!string.Equals(trimmed, normalized, StringComparison.Ordinal))
        {
            yield return trimmed;
            yield return trimmed.ToUpperInvariant();
            yield return trimmed.ToLowerInvariant();
        }
    }

    private static string? NormalizeDescriptionLocalizationId(string? canonicalId)
    {
        if (string.IsNullOrWhiteSpace(canonicalId))
        {
            return null;
        }

        var trimmed = canonicalId.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < trimmed.Length
            ? trimmed[(lastDot + 1)..].ToUpperInvariant()
            : trimmed.ToUpperInvariant();
    }

    private static bool IsUsableDescriptionLocString(object? locString, string normalizedId, string suffix)
    {
        if (locString is null)
        {
            return false;
        }

        var rawText = TryInvokeParameterlessMethod(locString, "GetRawText") as string;
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            return true;
        }

        var entryKey = ConvertToText(GetMemberValue(locString, "LocEntryKey"));
        if (string.IsNullOrWhiteSpace(entryKey))
        {
            return true;
        }

        return !string.Equals(entryKey, $"{normalizedId}.{suffix}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveBuiltInGlossaryHint(string glossaryId, out string? hint)
    {
        var normalizedId = NormalizeGlossaryId(glossaryId);
        if (normalizedId is not null &&
            KnownGlossarySpecs.TryGetValue(normalizedId, out var spec) &&
            !string.IsNullOrWhiteSpace(spec.FallbackHint))
        {
            hint = spec.FallbackHint;
            return true;
        }

        hint = null;
        return false;
    }

    private IEnumerable<string> EnumerateGlossaryLocalizationKeys(string glossaryId)
    {
        var upperId = glossaryId.ToUpperInvariant();
        foreach (var suffix in GlossaryLocStringSuffixes)
        {
            yield return $"{upperId}_POWER.{suffix}";
        }
    }

    private object? TryResolveLocStringByKey(string key)
    {
        var assembly = FindSts2Assembly();
        var locStringType = assembly?.GetType("MegaCrit.Sts2.Core.Localization.LocString");
        if (locStringType is null)
        {
            return null;
        }

        foreach (var methodName in new[] { "KeyPathToLocString", "GetIfExists" })
        {
            var methods = locStringType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
                {
                    continue;
                }

                try
                {
                    var value = method.Invoke(null, new object?[] { key });
                    if (value is not null)
                    {
                        return value;
                    }
                }
                catch
                {
                    // Ignore localization lookup failures and continue to the next strategy.
                }
            }
        }

        return null;
    }

    private object? TryResolveLocStringByTableAndKey(string table, string key)
    {
        var assembly = FindSts2Assembly();
        var locManagerType = assembly?.GetType("MegaCrit.Sts2.Core.Localization.LocManager");
        if (locManagerType is null)
        {
            return null;
        }

        foreach (var methodName in new[] { "GetIfExists", "Get", "KeyPathToLocString", "GetLocString" })
        {
            var methods = locManagerType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 2 ||
                    parameters[0].ParameterType != typeof(string) ||
                    parameters[1].ParameterType != typeof(string))
                {
                    continue;
                }

                try
                {
                    var value = method.Invoke(null, new object?[] { table, key });
                    if (value is not null)
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static bool SourceMatchesGlossaryCandidate(object source, GlossaryCandidate candidate)
    {
        var identifiers = GlossaryIdentityMembers
            .Select(member => ConvertToText(GetMemberValue(source, member), member))
            .Concat(GlossaryDisplayMembers.Select(member => ConvertToText(GetMemberValue(source, member), member)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return MatchesGlossaryCandidate(candidate, identifiers);
    }

    private static bool MatchesGlossaryCandidate(GlossaryCandidate candidate, params string?[] values)
    {
        var candidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            candidate.GlossaryId,
            NormalizeGlossaryId(candidate.DisplayText) ?? candidate.DisplayText,
        };

        if (KnownGlossarySpecs.TryGetValue(candidate.GlossaryId, out var spec))
        {
            foreach (var term in spec.Terms)
            {
                candidateIds.Add(term);
                var normalized = NormalizeGlossaryId(term);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    candidateIds.Add(normalized);
                }
            }
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (candidateIds.Contains(value))
            {
                return true;
            }

            var normalized = NormalizeGlossaryId(value);
            if (!string.IsNullOrWhiteSpace(normalized) && candidateIds.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeGlossaryId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray());
    }

    private static IReadOnlyList<string> ExtractTextList(object? collection, string path, TextDiagnosticsCollector textDiagnostics)
    {
        return EnumerateObjects(collection)
            .Select((item, index) => ConvertToText(item, $"{path}[{index}]", textDiagnostics, "Name", "Label", "Title", "Text", "Id", "Key"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? NormalizeIntentType(
        string? direct,
        string? raw,
        string? description,
        IReadOnlyList<string> effects,
        int? damage,
        int? block)
    {
        var directType = NormalizeKnownIntentType(direct);
        if (!string.IsNullOrWhiteSpace(directType) &&
            !string.Equals(directType, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return directType;
        }

        var rawType = NormalizeKnownIntentType(raw);
        if (!string.IsNullOrWhiteSpace(rawType) &&
            !string.Equals(rawType, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return rawType;
        }

        var descriptionType = NormalizeKnownIntentType(description);
        if (!string.IsNullOrWhiteSpace(descriptionType) &&
            !string.Equals(descriptionType, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return descriptionType;
        }

        var texts = new[] { direct, raw, description }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToLowerInvariant())
            .ToArray();
        var normalizedEffects = (effects ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .ToArray();
        var hasAttack = damage is > 0 ||
                        HasIntentSignal(texts, "attack", "damage", "攻击", "伤害");
        var hasBlock = block is > 0 ||
                       HasIntentSignal(texts, "block", "defend", "shield", "格挡", "防御", "护盾");
        var hasBuff = HasIntentSignal(texts, "buff", "strength", "ritual", "regen", "artifact", "增益", "力量", "再生", "人工制品") ||
                      HasIntentSignal(normalizedEffects, "buff", "strength", "ritual", "regen", "artifact");
        var hasDebuff = HasIntentSignal(texts, "debuff", "negative effect", "status", "weak", "frail", "vulnerable", "负面效果", "减益", "状态", "虚弱", "易伤", "脆弱") ||
                        HasIntentSignal(normalizedEffects, "debuff", "weak", "frail", "vulnerable", "status");

        if (hasAttack && hasDebuff)
        {
            return "attack_debuff";
        }

        if (hasAttack && hasBlock)
        {
            return "attack_block";
        }

        if (hasAttack && hasBuff)
        {
            return "attack_buff";
        }

        if (hasAttack)
        {
            return "attack";
        }

        if (hasBlock)
        {
            return "block";
        }

        if (hasDebuff)
        {
            return "debuff";
        }

        if (hasBuff)
        {
            return "buff";
        }

        return directType
            ?? rawType
            ?? descriptionType
            ?? "unknown";
    }

    private static IReadOnlyList<string> ExtractIntentEffects(object enemy, string? raw, string? description)
    {
        var effects = new List<string>();
        foreach (var item in EnumerateObjects(GetMemberValue(enemy, "IntentEffects") ?? GetMemberValue(enemy, "IntentKeywords")))
        {
            var text = ConvertToText(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                effects.Add(text!);
            }
        }

        foreach (var text in new[] { raw, description })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var normalized = text.ToLowerInvariant();
            if (normalized.Contains("weak", StringComparison.Ordinal))
            {
                effects.Add("weak");
            }

            if (normalized.Contains("frail", StringComparison.Ordinal))
            {
                effects.Add("frail");
            }

            if (normalized.Contains("vulnerable", StringComparison.Ordinal))
            {
                effects.Add("vulnerable");
            }

            if (normalized.Contains("debuff", StringComparison.Ordinal) ||
                normalized.Contains("negative effect", StringComparison.Ordinal) ||
                normalized.Contains("负面效果", StringComparison.Ordinal))
            {
                effects.Add("debuff");
            }

            if (normalized.Contains("status", StringComparison.Ordinal) ||
                normalized.Contains("状态", StringComparison.Ordinal))
            {
                effects.Add("status");
            }
        }

        return effects
            .Where(effect => !string.IsNullOrWhiteSpace(effect))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasIntentSignal(IEnumerable<string> texts, params string[] markers)
    {
        foreach (var text in texts)
        {
            foreach (var marker in markers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsGenericIntentLabel(string? value)
    {
        if (IsLowQualityIntentLabel(value))
        {
            return true;
        }

        var key = NormalizeIntentLabelKey(value);
        if (Regex.IsMatch(key, "^[0-9]+(x[0-9]+)?$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (Regex.IsMatch(key, "^(attack|block|buff|debuff|attack_buff|attack_block|attack_debuff)(_[0-9]+(x[0-9]+)?)?$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return key is "unknown" or
            "intent" or
            "action" or
            "move" or
            "strategy" or
            "skill" or
            "attack" or
            "block" or
            "defend" or
            "buff" or
            "debuff" or
            "attack_buff" or
            "attack_block" or
            "attack_debuff" or
            "攻击" or
            "攻势" or
            "格挡" or
            "防御" or
            "增益" or
            "负面效果" or
            "攻击_增益" or
            "攻击_格挡" or
            "攻击_负面效果" or
            "策略" or
            "技巧" or
            "未知";
    }

    private static string? NormalizeKnownIntentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeIntentLabelKey(value) switch
        {
            "attack" or "damage" or "攻击" or "伤害" => "attack",
            "block" or "defend" or "shield" or "格挡" or "防御" or "护盾" => "block",
            "buff" or "增益" => "buff",
            "debuff" or "负面效果" or "减益" => "debuff",
            "attack_buff" or "attackbuff" or "攻击_增益" or "攻击增益" => "attack_buff",
            "attack_block" or "attackblock" or "攻击_格挡" or "攻击格挡" or "攻击_防御" or "攻击防御" => "attack_block",
            "attack_debuff" or "attackdebuff" or "攻击_负面效果" or "攻击负面效果" or "攻击_减益" or "攻击减益" => "attack_debuff",
            "strategy" or "skill" or "策略" or "技巧" or "intent" or "未知" or "unknown" => "unknown",
            _ => null,
        };
    }

    private static string NormalizeIntentLabelKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value.Trim().Replace('×', 'x').ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+", "_").Trim('_');
    }

    private static bool LooksLikeInternalEnemyToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (Regex.IsMatch(trimmed, "^(POWER|MONSTER|RELIC|CARD|STATUS|INTENT|ROOM)\\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        if (!trimmed.Contains(' ') &&
            (trimmed.Contains('.', StringComparison.Ordinal) ||
             trimmed.Contains("::", StringComparison.Ordinal) ||
             trimmed.Contains('/', StringComparison.Ordinal) ||
             trimmed.Contains('\\', StringComparison.Ordinal)))
        {
            return true;
        }

        return Regex.IsMatch(trimmed, "^[A-Z][A-Za-z0-9]+(?:Power|Enemy|Monster|Move|Intent|State|Data|Behavior)$", RegexOptions.CultureInvariant);
    }

    private static IReadOnlyDictionary<string, object?> BuildCardPreview(HandCardDescriptor card)
    {
        var preview = new Dictionary<string, object?>
        {
            ["card_id"] = card.CardId,
            ["card_name"] = card.Name,
            ["target_type"] = card.TargetType,
            ["canonical_card_id"] = card.CanonicalCardId,
            ["description"] = card.Description,
            ["glossary"] = card.Glossary,
            ["upgraded"] = card.Upgraded,
            ["traits"] = card.Traits,
            ["keywords"] = card.Keywords,
        };
        return preview
            .Where(pair => pair.Value is not null &&
                           (!(pair.Value is string text) || !string.IsNullOrWhiteSpace(text)) &&
                           (!(pair.Value is IReadOnlyCollection<string> values) || values.Count > 0))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static IReadOnlyDictionary<string, object?> BuildCardPreview(RuntimeCard card)
    {
        var preview = new Dictionary<string, object?>
        {
            ["card_id"] = card.CardId,
            ["card_name"] = card.Name,
            ["target_type"] = card.TargetType,
            ["canonical_card_id"] = card.CanonicalCardId,
            ["description"] = card.Description,
            ["glossary"] = card.Glossary,
            ["upgraded"] = card.Upgraded,
            ["traits"] = card.Traits,
            ["keywords"] = card.Keywords,
        };
        return preview
            .Where(pair => pair.Value is not null &&
                           (!(pair.Value is string text) || !string.IsNullOrWhiteSpace(text)) &&
                           (!(pair.Value is IReadOnlyCollection<string> values) || values.Count > 0))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static IReadOnlyDictionary<string, object?> BuildPotionPreview(RuntimePotionState potion)
    {
        var preview = new Dictionary<string, object?>
        {
            ["name"] = potion.Name,
            ["description"] = potion.Description,
            ["canonical_potion_id"] = potion.CanonicalPotionId,
            ["glossary"] = potion.Glossary,
        };
        return preview
            .Where(pair => pair.Value is not null &&
                           (!(pair.Value is string text) || !string.IsNullOrWhiteSpace(text)) &&
                           (!(pair.Value is IReadOnlyCollection<string> values) || values.Count > 0))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static IReadOnlyDictionary<string, object?> BuildRelicPreview(RuntimeRelicState relic)
    {
        var preview = new Dictionary<string, object?>
        {
            ["name"] = relic.Name,
            ["description"] = relic.Description,
            ["canonical_relic_id"] = relic.CanonicalRelicId,
            ["glossary"] = relic.Glossary,
        };
        return preview
            .Where(pair => pair.Value is not null &&
                           (!(pair.Value is string text) || !string.IsNullOrWhiteSpace(text)) &&
                           (!(pair.Value is IReadOnlyCollection<string> values) || values.Count > 0))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private void LogTextDiagnostics(string windowKind, TextDiagnosticsCollector collector)
    {
        var metadata = collector.ToMetadata();
        var resolved = Convert.ToInt32(metadata["resolved"]);
        var fallback = Convert.ToInt32(metadata["fallback"]);
        var unresolved = Convert.ToInt32(metadata["unresolved"]);
        if (fallback == 0 && unresolved == 0)
        {
            if (_options.LogDescriptionSuccesses && resolved > 0)
            {
                _logger?.Info($"Text resolution window={windowKind} resolved={resolved} fallback=0 unresolved=0");
            }

            return;
        }

        var entries = metadata.TryGetValue("entries", out var rawEntries) && rawEntries is IReadOnlyCollection<IReadOnlyDictionary<string, object?>> diagnostics
            ? diagnostics.Take(5).Select(FormatTextDiagnosticEntry).ToArray()
            : Array.Empty<string>();
        var entrySummary = entries.Length == 0 ? "none" : string.Join(" | ", entries);
        _logger?.Warn($"Text resolution issues window={windowKind} resolved={resolved} fallback={fallback} unresolved={unresolved} entries={entrySummary}");
    }

    private void LogDescriptionDiagnostics(
        string kind,
        string identifier,
        string path,
        string? raw,
        string? rendered,
        string? quality,
        string? source,
        IReadOnlyList<DescriptionVariable> variables,
        IReadOnlyList<GlossaryAnchor> glossary,
        string? context = null)
    {
        var unresolvedVariables = variables.Where(variable => variable.Value is null).Select(variable => variable.Key).Distinct(StringComparer.Ordinal).ToArray();
        var expectedGlossaryNormalization = !string.IsNullOrWhiteSpace(raw) && RichTextPairRegex.IsMatch(raw);
        var glossaryNormalizationMissing = expectedGlossaryNormalization &&
                                          (!string.IsNullOrWhiteSpace(rendered) && rendered.Contains('[', StringComparison.Ordinal));
        var requiresWarning =
            string.Equals(quality, "template_fallback", StringComparison.Ordinal) ||
            unresolvedVariables.Length > 0 ||
            glossaryNormalizationMissing;

        if (!requiresWarning && !_options.LogDescriptionSuccesses)
        {
            return;
        }

        var message =
            $"Description {kind}={identifier} path={path} context={context ?? "-"} quality={quality ?? "unknown"} source={source ?? "unknown"} " +
            $"unresolved_vars={(unresolvedVariables.Length == 0 ? "-" : string.Join(",", unresolvedVariables))} " +
            $"glossary={glossary.Count} raw=\"{AbbreviateForLog(raw)}\" rendered=\"{AbbreviateForLog(rendered)}\"";
        if (requiresWarning)
        {
            _logger?.Warn(message);
            return;
        }

        _logger?.Info(message);
    }

    private static string FormatTextDiagnosticEntry(IReadOnlyDictionary<string, object?> entry)
    {
        var path = ConvertToText(GetDictionaryValue(entry, "path")) ?? "?";
        var status = ConvertToText(GetDictionaryValue(entry, "status")) ?? "?";
        var source = ConvertToText(GetDictionaryValue(entry, "source")) ?? "?";
        return $"{path}:{status}:{source}";
    }

    private static string AbbreviateForLog(string? text, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    private static List<string> ExtractLabels(object? collection, string path, TextDiagnosticsCollector textDiagnostics)
    {
        return EnumerateObjects(collection)
            .Select((item, index) => DescribeInventoryItem(item, $"{path}[{index}]", textDiagnostics))
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .ToList();
    }

    private static string? DescribeInventoryItem(object? item, string path, TextDiagnosticsCollector textDiagnostics)
    {
        return ConvertToText(item, path, textDiagnostics, "Potion", "Relic", "Name", "Title", "Description", "Label", "Text");
    }

    private static RewardOption? DescribeReward(object? reward, string path, TextDiagnosticsCollector textDiagnostics)
    {
        var resolution = RuntimeTextResolver.Resolve(reward, path, textDiagnostics, "Description", "Label", "Name", "Title", "RewardType");
        return resolution.HasText ? new RewardOption(resolution.Text!, resolution) : null;
    }

    private static string? DescribeMapNode(object? mapPoint, string path, TextDiagnosticsCollector textDiagnostics)
    {
        var pointType = ConvertToText(GetMemberValue(mapPoint, "PointType"), $"{path}.point_type", textDiagnostics) ?? "unknown";
        var coord = DescribeMapCoord(GetMemberValue(mapPoint, "coord"));
        return $"{pointType}@{coord}";
    }

    private static string DescribeMapCoord(object? coord)
    {
        var col = GetNullableInt(coord, "col") ?? -1;
        var row = GetNullableInt(coord, "row") ?? -1;
        return $"{col},{row}";
    }

    private static string? ConvertToText(object? value, string path, TextDiagnosticsCollector? textDiagnostics = null, params string[] preferredMembers)
    {
        return RuntimeTextResolver.Resolve(value, path, textDiagnostics, preferredMembers).Text;
    }

    private static string? ConvertDescriptionTemplateToText(object? value, string path, TextDiagnosticsCollector? textDiagnostics = null, params string[] preferredMembers)
    {
        if (value is not null)
        {
            var typeName = GetTypeName(value) ?? string.Empty;
            if (typeName.Contains("LocString", StringComparison.Ordinal))
            {
                if (TryInvokeParameterlessMethod(value, "GetRawText") is string rawText && !string.IsNullOrWhiteSpace(rawText))
                {
                    textDiagnostics?.Record(path, new TextResolutionResult(rawText, "fallback", "loc_string.raw"));
                    return rawText;
                }

                if (GetMemberValue(value, "LocEntryKey") is string entryKey && !string.IsNullOrWhiteSpace(entryKey))
                {
                    textDiagnostics?.Record(path, new TextResolutionResult(entryKey, "fallback", "loc_string.key"));
                    return entryKey;
                }
            }
        }

        return RuntimeTextResolver.Resolve(value, path, textDiagnostics, preferredMembers).Text;
    }

    private static string? ConvertRenderedDescriptionToText(object? value, string path, TextDiagnosticsCollector? textDiagnostics = null, params string[] preferredMembers)
    {
        if (value is not null)
        {
            var typeName = GetTypeName(value) ?? string.Empty;
            if (typeName.Contains("LocString", StringComparison.Ordinal))
            {
                if (TryInvokeParameterlessMethod(value, "GetRawText") is string rawText && !string.IsNullOrWhiteSpace(rawText))
                {
                    textDiagnostics?.Record(path, new TextResolutionResult(rawText, "fallback", "loc_string.raw_rendered_fallback"));
                    return rawText;
                }

                if (GetMemberValue(value, "LocEntryKey") is string entryKey && !string.IsNullOrWhiteSpace(entryKey))
                {
                    textDiagnostics?.Record(path, new TextResolutionResult(entryKey, "fallback", "loc_string.key_rendered_fallback"));
                    return entryKey;
                }
            }
        }

        return RuntimeTextResolver.Resolve(value, path, textDiagnostics, preferredMembers).Text;
    }

    private static string? ConvertToText(object? value, params string[] preferredMembers)
    {
        return RuntimeTextResolver.Resolve(value, "_", null, preferredMembers).Text;
    }

    private static object? GetMemberValue(object? target, string memberName)
    {
        if (target is null)
        {
            return null;
        }

        var type = target as Type ?? target.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    (target is Type ? BindingFlags.Static : BindingFlags.Instance);
        var property = type.GetProperty(memberName, flags);
        if (property is not null)
        {
            try
            {
                return property.GetValue(target is Type ? null : target);
            }
            catch
            {
                return null;
            }
        }

        var field = type.GetField(memberName, flags);
        if (field is null)
        {
            return null;
        }

        try
        {
            return field.GetValue(target is Type ? null : target);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryInvokeParameterlessMethod(object? target, string methodName)
    {
        if (target is null)
        {
            return null;
        }

        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(target, null);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object> EnumerateObjects(object? source)
    {
        if (source is null)
        {
            yield break;
        }

        if (source is string text)
        {
            yield return text;
            yield break;
        }

        if (source is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        yield return source;
    }

    private static bool GetBoolean(object? target, string memberName, bool defaultValue = false)
    {
        var value = GetMemberValue(target, memberName);
        return value is bool boolean ? boolean : defaultValue;
    }

    private static bool InvokeBooleanMethod(object? target, string methodName, bool defaultValue = false)
    {
        var value = TryInvokeParameterlessMethod(target, methodName);
        return value is bool boolean ? boolean : defaultValue;
    }

    private static int? GetNullableInt(object? target, string memberName)
    {
        var value = GetMemberValue(target, memberName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetTypeName(object? target)
    {
        return target?.GetType().FullName ?? target?.GetType().Name;
    }

    private static string? NormalizeTypeName(object? target)
    {
        var type = target as Type ?? target?.GetType();
        if (type is null)
        {
            return null;
        }

        return type.Name;
    }

    private readonly record struct PlayerBuildResult(
        RuntimePlayerState? Player,
        IReadOnlyDictionary<string, object?> Diagnostics);

    private readonly record struct PileCardExtraction(
        IReadOnlyList<RuntimeCard> Cards,
        string Source,
        int ExpectedCount,
        string? FallbackReason = null)
    {
        public bool IsDegraded => !string.IsNullOrWhiteSpace(FallbackReason) || Cards.Count != ExpectedCount;
    }

    private readonly record struct RuntimeRoot(object GameInstance, object? RunNode, object? RunState);

    private sealed record GlossarySpec(
        string GlossaryId,
        string DisplayText,
        IReadOnlyList<string> Terms,
        string? FallbackHint = null);

    private readonly record struct GlossaryCandidate(
        string GlossaryId,
        string DisplayText,
        string MatchSource);

    private readonly record struct GlossaryHintResolution(
        string? Hint,
        string Source);

    private readonly record struct VariableResolution(int? Value, string? Source, string? SemanticKind = null);

    private enum CardDescriptionContext
    {
        Unknown,
        Hand,
        DrawPile,
        DiscardPile,
        ExhaustPile,
        Preview,
        UpgradePreview,
    }

    private readonly record struct RenderOutcome(string? Text, string? Quality, string? Source);

    private readonly record struct GameRenderedCardDescription(string? Text, string? Source);

    private readonly record struct DescriptionExtraction(
        string? Raw,
        string? Rendered,
        string? Description,
        string? Quality,
        string? Source,
        IReadOnlyList<DescriptionVariable> Vars,
        IReadOnlyList<GlossaryAnchor> Glossary);

    private readonly record struct HandCardDescriptor(
        string CardId,
        string Name,
        TextResolutionResult NameResolution,
        string? TargetType,
        bool Playable,
        string? CanonicalCardId,
        string? Description,
        IReadOnlyList<GlossaryAnchor> Glossary,
        bool? Upgraded,
        IReadOnlyList<string> Traits,
        IReadOnlyList<string> Keywords);
    private readonly record struct EnemyBuildResult(
        IReadOnlyList<RuntimeEnemyState> Enemies,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Diagnostics);
    private readonly record struct EnemyEnrichmentDescriptor(
        string? MoveName,
        string? MoveDescription,
        IReadOnlyList<GlossaryAnchor> MoveGlossary,
        IReadOnlyList<string> Traits,
        IReadOnlyList<string> Keywords);
    private readonly record struct EnemyIntentDescriptor(
        string Display,
        string? Raw,
        string? Type,
        int? Damage,
        int? Hits,
        int? Block,
        IReadOnlyList<string> Effects);
    private readonly record struct RewardOption(string Label, TextResolutionResult Resolution);
    private readonly record struct ShopOfferAnalysis(
        string OfferId,
        string Kind,
        int Index,
        string Name,
        int Price,
        bool Purchasable,
        string? UnavailableReason,
        string? Description,
        IReadOnlyList<GlossaryAnchor> Glossary,
        string? CanonicalId,
        string DetectionSource,
        object? OfferNode,
        object? ActivationNode,
        RuntimeCard? Card,
        RuntimeRelicState? Relic,
        RuntimePotionState? Potion,
        string? ServiceKind);
    private readonly record struct ShopPhaseAnalysis(
        bool TreatAsShop,
        IReadOnlyList<ShopOfferAnalysis> Offers,
        object? LeaveButton,
        string LeaveLabel,
        bool FallbackLeaveSupported,
        string DetectionSource);
    private readonly record struct EventOptionAnalysis(
        int Index,
        string Label,
        bool Available,
        bool IsContinue,
        object? OptionObject,
        object? ButtonNode,
        string DetectionSource,
        object? SelectionScreen = null,
        object? SelectionValue = null,
        string? StableId = null,
        string? PreviewText = null,
        string? Description = null,
        IReadOnlyList<string>? Keywords = null,
        IReadOnlyList<GlossaryAnchor>? Glossary = null);
    private readonly record struct EventPhaseAnalysis(
        bool TreatAsEvent,
        object? EventRoom,
        object? EventLayout,
        object? EventModel,
        string? Title,
        string? Body,
        IReadOnlyList<EventOptionAnalysis> Options,
        bool ContinueAvailable,
        string ContinueLabel,
        string WindowKind,
        string DetectionSource,
        string? Diagnostics,
        string? EventSubphase = null,
        string? SelectionPrompt = null);
    private readonly record struct EventCardSelectionContext(
        object SelectionScreen,
        string? Title,
        string? Body,
        string? Prompt,
        string DetectionSource,
        IReadOnlyList<EventOptionAnalysis> Options);
    private readonly record struct CombatSelectionContext(
        object SelectionScreen,
        string SelectionKind,
        string? SelectionPrompt,
        bool CancelAvailable,
        string? CancelReason,
        string DetectionSource,
        string? SelectionScreenType,
        string PromptSource,
        IReadOnlyList<RuntimeActionDefinition> Actions);
    private readonly record struct CombatSelectionScreenResolution(
        object SelectionScreen,
        object PromptScreen,
        string DetectionSource,
        string? SelectionScreenType,
        string PromptSource);
    private readonly record struct RewardPhaseAnalysis(
        bool TreatAsReward,
        bool HasRewardScreen,
        bool RewardScreenComplete,
        bool RewardScreenVisible,
        int RewardButtonCount,
        bool HasLiveEnemies,
        string RewardScreenSource,
        bool CardRewardSelectionDetected,
        bool AdvanceButtonDetected,
        string RewardSubphase,
        string DetectionSource,
        string? OverlayTopType)
    {
        public IReadOnlyDictionary<string, object?> ToMetadata()
        {
            return new Dictionary<string, object?>
            {
                ["treat_as_reward"] = TreatAsReward,
                ["has_reward_screen"] = HasRewardScreen,
                ["reward_screen_complete"] = RewardScreenComplete,
                ["reward_screen_visible"] = RewardScreenVisible,
                ["reward_button_count"] = RewardButtonCount,
                ["has_live_enemies"] = HasLiveEnemies,
                ["reward_screen_source"] = RewardScreenSource,
                ["card_reward_selection_detected"] = CardRewardSelectionDetected,
                ["advance_button_detected"] = AdvanceButtonDetected,
                ["reward_subphase"] = RewardSubphase,
                ["detection_source"] = DetectionSource,
                ["overlay_top_type"] = OverlayTopType,
            };
        }
    }

    private RuntimeActionResult ExecuteUsePotion(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["action_type"] = "use_potion",
            ["runtime_handler_candidates"] = new[]
            {
                "game_action.UsePotionAction",
                "potion_model.EnqueueManualUse",
                "potion_holder.UsePotion",
            },
        };

        if (!IsPlayerTurn(runState))
        {
            metadata["failure_stage"] = "phase_gate";
            return new RuntimeActionResult(false, "Potions can only be used during the player's turn.", "not_player_turn", metadata);
        }

        var potionIndex = GetNullableIntFromObject(GetDictionaryValue(action.Params, "potion_index"));
        if (potionIndex is null)
        {
            metadata["failure_stage"] = "action_validation";
            return new RuntimeActionResult(false, "Action does not contain a potion_index.", "invalid_action", metadata);
        }

        metadata["potion_index"] = potionIndex.Value;
        var requestedCanonicalPotionId = ConvertToText(GetDictionaryValue(action.Params, "canonical_potion_id"));
        var requestedPotionName = ConvertToText(GetDictionaryValue(action.Params, "potion"));
        if (!string.IsNullOrWhiteSpace(requestedCanonicalPotionId))
        {
            metadata["canonical_potion_id"] = requestedCanonicalPotionId;
        }

        if (!string.IsNullOrWhiteSpace(requestedPotionName))
        {
            metadata["potion_name"] = requestedPotionName;
        }

        var player = GetPlayers(runState).FirstOrDefault();
        if (player is null)
        {
            metadata["failure_stage"] = "player_resolution";
            return new RuntimeActionResult(false, "Player state is not available.", "runtime_not_ready", metadata);
        }

        if (!TryResolvePotionAction(runNode, player, potionIndex.Value, requestedCanonicalPotionId, requestedPotionName, metadata, out var resolved, out var errorCode, out var errorMessage))
        {
            metadata["failure_stage"] ??= "potion_resolution";
            return new RuntimeActionResult(false, errorMessage ?? "Potion is no longer available.", errorCode ?? "stale_action", metadata);
        }

        metadata["holder_resolution"] = resolved!.HolderResolution;
        metadata["holder_found"] = resolved.Holder is not null;
        if (resolved.Holder is not null)
        {
            metadata["holder_type"] = GetTypeName(resolved.Holder);
            metadata["holder_usable"] = GetBoolean(resolved.Holder, "IsPotionUsable", defaultValue: true);
        }

        var probe = ProbePotionTargetRequirement(resolved.PotionModel);
        if (!string.IsNullOrWhiteSpace(probe.TargetType))
        {
            metadata["target_type"] = probe.TargetType;
        }

        if (!string.IsNullOrWhiteSpace(probe.Usage))
        {
            metadata["potion_usage"] = probe.Usage;
        }

        if (!string.IsNullOrWhiteSpace(probe.SelectionPrompt))
        {
            metadata["selection_prompt"] = probe.SelectionPrompt;
        }

        metadata["target_probe_source"] = probe.ProbeSource;
        if (!string.IsNullOrWhiteSpace(probe.ProbeMessage))
        {
            metadata["target_probe_message"] = probe.ProbeMessage;
        }

        var targetConstraints = BuildTargetConstraints(probe.TargetType, EnumerateObjects(GetMemberValue(GetCombatState(runState), "Enemies"))
            .Where(enemy => GetBoolean(enemy, "IsAlive", defaultValue: true))
            .Select((enemy, index) => ResolveEnemyId(enemy, index))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToArray());
        var inferredTargetRequirement = probe.RequiresTarget || targetConstraints.Count > 0;
        object? target = null;
        var targetId = ConvertToText(GetDictionaryValue(request.Params, "target_id"));
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            target = EnumerateObjects(GetMemberValue(GetCombatState(runState), "Enemies"))
                .Select((enemy, index) => new { Enemy = enemy, EnemyId = ResolveEnemyId(enemy, index) })
                .FirstOrDefault(entry => string.Equals(entry.EnemyId, targetId, StringComparison.Ordinal))
                ?.Enemy;
            if (target is null)
            {
                metadata["failure_stage"] = "target_validation";
                return new RuntimeActionResult(false, $"Target '{targetId}' is no longer available.", "invalid_target", metadata);
            }

            metadata["target_id"] = targetId;
        }

        if (inferredTargetRequirement && target is null)
        {
            metadata["failure_stage"] = "target_validation";
            metadata["requires_target"] = true;
            return new RuntimeActionResult(false, "This potion requires an explicit target.", "target_required", metadata);
        }

        metadata["requires_target"] = inferredTargetRequirement;

        if (GetBoolean(resolved.PotionModel, "HasBeenRemovedFromState"))
        {
            metadata["failure_stage"] = "potion_resolution";
            return new RuntimeActionResult(false, "Potion has already been removed from the live state.", "stale_action", metadata);
        }

        if (!GetBoolean(resolved.PotionModel, "PassesCustomUsabilityCheck", defaultValue: true))
        {
            metadata["failure_stage"] = "usability_check";
            return new RuntimeActionResult(false, "Current rules do not allow this potion to be used.", "not_allowed", metadata);
        }

        if (resolved.Holder is not null && !GetBoolean(resolved.Holder, "IsPotionUsable", defaultValue: true))
        {
            metadata["failure_stage"] = "usability_check";
            return new RuntimeActionResult(false, "Potion holder is currently not usable.", "not_allowed", metadata);
        }

        if (TryExecutePotionViaGameAction(player, resolved.PotionModel, potionIndex.Value, targetId, target, metadata, out var gameActionFailure))
        {
            metadata["runtime_handler"] = "game_action.UsePotionAction";
            return new RuntimeActionResult(true, $"Used potion '{resolved.PotionState.Name}'.", metadata: metadata);
        }

        if (TryExecutePotionViaModel(resolved.PotionModel, target, metadata, out var modelFailure))
        {
            metadata["runtime_handler"] = "potion_model.EnqueueManualUse";
            return new RuntimeActionResult(true, $"Used potion '{resolved.PotionState.Name}'.", metadata: metadata);
        }

        string? holderFailure = null;
        if (resolved.Holder is not null && TryExecutePotionViaHolder(resolved.Holder, target, metadata, out holderFailure))
        {
            metadata["runtime_handler"] = "potion_holder.UsePotion";
            return new RuntimeActionResult(true, $"Used potion '{resolved.PotionState.Name}'.", metadata: metadata);
        }

        metadata["failure_stage"] = "runtime_handler";
        if (!string.IsNullOrWhiteSpace(gameActionFailure))
        {
            metadata["game_action_failure"] = gameActionFailure;
        }

        if (!string.IsNullOrWhiteSpace(modelFailure))
        {
            metadata["model_handler_failure"] = modelFailure;
        }

        if (!string.IsNullOrWhiteSpace(holderFailure))
        {
            metadata["holder_handler_failure"] = holderFailure;
        }

        return new RuntimeActionResult(false, "Potion runtime handlers are not available in this runtime.", "runtime_incompatible", metadata);
    }

    private bool TryResolvePotionAction(
        object runNode,
        object player,
        int potionIndex,
        string? requestedCanonicalPotionId,
        string? requestedPotionName,
        Dictionary<string, object?> metadata,
        out ResolvedPotionAction? resolved,
        out string? errorCode,
        out string? errorMessage)
    {
        resolved = null;
        errorCode = null;
        errorMessage = null;

        var potionCollection = ResolvePotionCollection(player);
        var slots = EnumerateObjects(potionCollection).ToList();
        metadata["live_potion_slot_count"] = slots.Count;
        if (potionIndex < 0 || potionIndex >= slots.Count)
        {
            metadata["failure_stage"] = "potion_resolution";
            errorCode = "stale_action";
            errorMessage = "Potion slot is no longer available.";
            return false;
        }

        var slot = slots[potionIndex];
        var textDiagnostics = new TextDiagnosticsCollector();
        var potionState = DescribePotion(slot, $"apply.use_potion[{potionIndex}]", textDiagnostics);
        if (potionState is null)
        {
            metadata["failure_stage"] = "potion_resolution";
            errorCode = "stale_action";
            errorMessage = "Potion slot no longer contains a usable potion.";
            return false;
        }

        var potionModel = ResolvePotionActionModel(slot);
        if (potionModel is null)
        {
            metadata["failure_stage"] = "potion_resolution";
            errorCode = "runtime_incompatible";
            errorMessage = "Could not resolve the live potion model for this action.";
            return false;
        }

        metadata["live_potion_name"] = potionState.Name;
        if (!string.IsNullOrWhiteSpace(potionState.CanonicalPotionId))
        {
            metadata["live_canonical_potion_id"] = potionState.CanonicalPotionId;
        }

        metadata["live_potion_preview"] = BuildPotionPreview(potionState);

        if (!PotionMatchesRequestedAction(potionState, requestedCanonicalPotionId, requestedPotionName, metadata))
        {
            metadata["failure_stage"] = "consistency_check";
            errorCode = "stale_action";
            errorMessage = "Potion slot no longer matches the exported action.";
            return false;
        }

        var holder = ResolvePotionHolder(runNode, potionIndex, potionModel, potionState, metadata, out var holderResolution);
        resolved = new ResolvedPotionAction(slot, potionModel, potionState, holder, holderResolution);
        return true;
    }

    private bool PotionMatchesRequestedAction(
        RuntimePotionState potionState,
        string? requestedCanonicalPotionId,
        string? requestedPotionName,
        Dictionary<string, object?> metadata)
    {
        if (!string.IsNullOrWhiteSpace(requestedCanonicalPotionId) &&
            !string.IsNullOrWhiteSpace(potionState.CanonicalPotionId) &&
            !string.Equals(requestedCanonicalPotionId, potionState.CanonicalPotionId, StringComparison.OrdinalIgnoreCase))
        {
            metadata["consistency_error"] = "canonical_potion_id_changed";
            metadata["requested_canonical_potion_id"] = requestedCanonicalPotionId;
            metadata["live_canonical_potion_id"] = potionState.CanonicalPotionId;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requestedPotionName) &&
            !string.IsNullOrWhiteSpace(potionState.Name) &&
            !string.Equals(
                NormalizeComparisonText(requestedPotionName),
                NormalizeComparisonText(potionState.Name),
                StringComparison.OrdinalIgnoreCase))
        {
            metadata["consistency_error"] = "potion_name_changed";
            metadata["requested_potion_name"] = requestedPotionName;
            metadata["live_potion_name"] = potionState.Name;
            return false;
        }

        return true;
    }

    private object? ResolvePotionHolder(
        object runNode,
        int potionIndex,
        object potionModel,
        RuntimePotionState potionState,
        Dictionary<string, object?> metadata,
        out string resolution)
    {
        var holders = DiscoverPotionHolders(runNode);
        metadata["potion_holder_count"] = holders.Count;
        if (holders.Count == 0)
        {
            resolution = "holder_missing";
            return null;
        }

        if (potionIndex >= 0 && potionIndex < holders.Count)
        {
            var indexedHolder = holders[potionIndex];
            if (PotionHolderMatches(indexedHolder, potionModel, potionState))
            {
                resolution = "slot_index";
                return indexedHolder;
            }

            metadata["holder_index_mismatch"] = true;
        }

        var matchedHolder = holders.FirstOrDefault(holder => PotionHolderMatches(holder, potionModel, potionState));
        if (matchedHolder is not null)
        {
            resolution = "scan_match";
            return matchedHolder;
        }

        if (potionIndex >= 0 && potionIndex < holders.Count)
        {
            resolution = "slot_index_fallback";
            return holders[potionIndex];
        }

        resolution = "holder_missing";
        return null;
    }

    private List<object> DiscoverPotionHolders(object runNode)
    {
        var holders = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var container in EnumerateNodeDescendants(runNode, maxDepth: 8).Prepend(runNode))
        {
            var typeName = GetTypeName(container) ?? string.Empty;
            if (!typeName.Contains("NPotionContainer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var holder in EnumerateObjects(GetMemberValue(container, "_holders") ?? GetMemberValue(container, "Holders")))
            {
                if (seen.Add(holder))
                {
                    holders.Add(holder);
                }
            }
        }

        if (holders.Count > 0)
        {
            return holders;
        }

        foreach (var node in EnumerateNodeDescendants(runNode, maxDepth: 8).Prepend(runNode))
        {
            var typeName = GetTypeName(node) ?? string.Empty;
            if (!typeName.Contains("NPotionHolder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(node))
            {
                holders.Add(node);
            }
        }

        return holders;
    }

    private bool PotionHolderMatches(object holder, object potionModel, RuntimePotionState potionState)
    {
        var holderPotion = ResolvePotionActionModel(GetMemberValue(holder, "Potion") ?? holder);
        if (holderPotion is not null && ReferenceEquals(holderPotion, potionModel))
        {
            return true;
        }

        var textDiagnostics = new TextDiagnosticsCollector();
        var holderPotionState = DescribePotion(GetMemberValue(holder, "Potion") ?? holder, "holder.potion", textDiagnostics);
        if (holderPotionState is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(potionState.CanonicalPotionId) &&
            !string.IsNullOrWhiteSpace(holderPotionState.CanonicalPotionId) &&
            string.Equals(potionState.CanonicalPotionId, holderPotionState.CanonicalPotionId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            NormalizeComparisonText(potionState.Name),
            NormalizeComparisonText(holderPotionState.Name),
            StringComparison.OrdinalIgnoreCase);
    }

    private static object? ResolvePotionActionModel(object? slotOrPotion)
    {
        return GetMemberValue(slotOrPotion, "Potion")
               ?? GetMemberValue(slotOrPotion, "Model")
               ?? GetMemberValue(slotOrPotion, "PotionModel")
               ?? GetMemberValue(slotOrPotion, "CanonicalInstance")
               ?? slotOrPotion;
    }

    private bool TryExecutePotionViaModel(object potionModel, object? target, Dictionary<string, object?> metadata, out string? failure)
    {
        failure = null;
        var method = potionModel.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "EnqueueManualUse", StringComparison.Ordinal) &&
                                         candidate.GetParameters().Length == 1);
        if (method is null)
        {
            failure = "enqueue_manual_use_missing";
            return false;
        }

        try
        {
            method.Invoke(potionModel, new[] { target });
            metadata["model_handler_type"] = potionModel.GetType().FullName ?? potionModel.GetType().Name;
            return true;
        }
        catch (TargetInvocationException ex) when (LooksLikePotionTargetRequired(ex.GetBaseException().Message))
        {
            metadata["failure_stage"] = "target_validation";
            failure = ex.GetBaseException().Message;
            return false;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
    }

    private bool TryExecutePotionViaGameAction(
        object player,
        object potionModel,
        int potionIndex,
        string? targetId,
        object? target,
        Dictionary<string, object?> metadata,
        out string? failure)
    {
        failure = null;
        var assembly = FindSts2Assembly();
        var usePotionActionType = assembly?.GetType("MegaCrit.Sts2.Core.GameActions.UsePotionAction");
        if (usePotionActionType is null)
        {
            failure = "use_potion_action_type_missing";
            return false;
        }

        metadata["game_action_type"] = usePotionActionType.FullName;
        var isCombatInProgress = GetBoolean(GetMemberValue(assembly?.GetType("MegaCrit.Sts2.Core.Combat.CombatManager"), "Instance"), "IsInProgress");
        var runManager = GetMemberValue(assembly?.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
        var actionQueueSynchronizer = GetMemberValue(runManager, "ActionQueueSynchronizer");
        var actionQueueSet = GetMemberValue(runManager, "ActionQueueSet");
        var playerNetId = GetNullableULong(player, "NetId");
        if (runManager is not null)
        {
            metadata["run_manager_type"] = runManager.GetType().FullName ?? runManager.GetType().Name;
        }

        if (actionQueueSynchronizer is not null)
        {
            metadata["action_queue_synchronizer_type"] = actionQueueSynchronizer.GetType().FullName ?? actionQueueSynchronizer.GetType().Name;
        }

        if (actionQueueSet is not null)
        {
            metadata["action_queue_set_type"] = actionQueueSet.GetType().FullName ?? actionQueueSet.GetType().Name;
        }

        if (playerNetId.HasValue)
        {
            metadata["player_net_id"] = playerNetId.Value;
        }

        var constructors = usePotionActionType
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(ctor => ctor.GetParameters().Length)
            .ToArray();
        if (constructors.Length == 0)
        {
            failure = "use_potion_action_ctor_missing";
            return false;
        }

        try
        {
            object? action = null;
            var constructorFailures = new List<string>();
            foreach (var constructor in constructors)
            {
                var parameters = constructor.GetParameters();
                try
                {
                    if (parameters.Length == 5 &&
                        IsParameterType(parameters[0], "MegaCrit.Sts2.Core.Entities.Players.Player") &&
                        parameters[1].ParameterType == typeof(uint))
                    {
                        var resolvedTargetId = TryResolveActionTargetId(targetId, target);
                        var targetPlayerIdType = parameters[3].ParameterType;
                        action = constructor.Invoke(new object?[]
                        {
                            player,
                            (uint)Math.Max(potionIndex, 0),
                            resolvedTargetId,
                            targetPlayerIdType.IsValueType ? Activator.CreateInstance(targetPlayerIdType) : null,
                            isCombatInProgress,
                        });
                        metadata["game_action_ctor"] = "player+potion_index+target_id";
                        if (resolvedTargetId.HasValue)
                        {
                            metadata["game_action_target_id"] = resolvedTargetId.Value;
                        }

                        break;
                    }

                    if (parameters.Length == 3 &&
                        IsParameterType(parameters[0], "MegaCrit.Sts2.Core.Models.PotionModel"))
                    {
                        action = constructor.Invoke(new object?[]
                        {
                            potionModel,
                            target,
                            isCombatInProgress,
                        });
                        metadata["game_action_ctor"] = "potion_model+target";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    constructorFailures.Add($"{constructor}: {ex.GetBaseException().Message}");
                }
            }

            if (action is null)
            {
                if (constructorFailures.Count > 0)
                {
                    metadata["game_action_ctor_failures"] = constructorFailures;
                    failure = constructorFailures[0];
                }
                else
                {
                    failure = "use_potion_action_no_compatible_ctor";
                }

                return false;
            }

            metadata["game_action_initial_state"] = ConvertToText(GetMemberValue(action, "State"));
            if (!TryEnqueuePotionGameAction(action, actionQueueSynchronizer, actionQueueSet, playerNetId, metadata, out failure))
            {
                return false;
            }

            metadata["game_action_state_after_enqueue"] = ConvertToText(GetMemberValue(action, "State"));
            if (GetMemberValue(action, "CompletionTask") is Task completionTask)
            {
                metadata["game_action_task_status"] = completionTask.Status.ToString();
                if (completionTask.IsFaulted)
                {
                    failure = completionTask.Exception?.GetBaseException().Message ?? "use_potion_action_task_faulted";
                    metadata["game_action_task_faulted"] = true;
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
    }

    private bool TryEnqueuePotionGameAction(
        object action,
        object? actionQueueSynchronizer,
        object? actionQueueSet,
        ulong? playerNetId,
        Dictionary<string, object?> metadata,
        out string? failure)
    {
        failure = null;
        var enqueueFailures = new List<string>();

        if (actionQueueSynchronizer is not null && playerNetId.HasValue)
        {
            var enqueueMethod = actionQueueSynchronizer.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "EnqueueAction", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType.IsInstanceOfType(action) &&
                           parameters[1].ParameterType == typeof(ulong);
                });
            if (enqueueMethod is not null)
            {
                try
                {
                    enqueueMethod.Invoke(actionQueueSynchronizer, new object?[] { action, playerNetId.Value });
                    metadata["game_action_enqueue_method"] = "ActionQueueSynchronizer.EnqueueAction";
                    metadata["game_action_owner_id"] = playerNetId.Value;
                    return true;
                }
                catch (Exception ex)
                {
                    enqueueFailures.Add($"ActionQueueSynchronizer.EnqueueAction: {ex.GetBaseException().Message}");
                }
            }

            var requestMethod = actionQueueSynchronizer.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "RequestEnqueue", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(action);
                });
            if (requestMethod is not null)
            {
                try
                {
                    requestMethod.Invoke(actionQueueSynchronizer, new[] { action });
                    metadata["game_action_enqueue_method"] = "ActionQueueSynchronizer.RequestEnqueue";
                    metadata["game_action_owner_id"] = playerNetId.Value;
                    return true;
                }
                catch (Exception ex)
                {
                    enqueueFailures.Add($"ActionQueueSynchronizer.RequestEnqueue: {ex.GetBaseException().Message}");
                }
            }
        }

        if (actionQueueSet is not null)
        {
            var enqueueWithoutSynchronizingMethod = actionQueueSet.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "EnqueueWithoutSynchronizing", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(action);
                });
            if (enqueueWithoutSynchronizingMethod is not null)
            {
                try
                {
                    enqueueWithoutSynchronizingMethod.Invoke(actionQueueSet, new[] { action });
                    metadata["game_action_enqueue_method"] = "ActionQueueSet.EnqueueWithoutSynchronizing";
                    if (playerNetId.HasValue)
                    {
                        metadata["game_action_owner_id"] = playerNetId.Value;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    enqueueFailures.Add($"ActionQueueSet.EnqueueWithoutSynchronizing: {ex.GetBaseException().Message}");
                }
            }
        }

        if (enqueueFailures.Count > 0)
        {
            metadata["game_action_enqueue_failures"] = enqueueFailures;
            failure = enqueueFailures[0];
            return false;
        }

        failure = "game_action_enqueue_unavailable";
        return false;
    }

    private bool TryExecutePotionViaHolder(object holder, object? target, Dictionary<string, object?> metadata, out string? failure)
    {
        failure = null;
        try
        {
            var task = target is null
                ? TryInvokeParameterlessMethod(holder, "UsePotion")
                : TryInvokeFirstCompatibleMethod(
                    holder,
                    new[] { "UsePotion", "UsePotionOnTarget", "UsePotionTargeted" },
                    new[]
                    {
                        new[] { target },
                        new object?[] { target, null },
                    },
                    out _)
                    ? new object()
                    : null;
            metadata["holder_handler_type"] = holder.GetType().FullName ?? holder.GetType().Name;
            if (task is Task asyncTask)
            {
                metadata["holder_task_status"] = asyncTask.Status.ToString();
            }

            return task is not null;
        }
        catch (Exception ex)
        {
            failure = ex.GetBaseException().Message;
            return false;
        }
    }

    private PotionTargetProbe ProbePotionTargetRequirement(object potionModel)
    {
        var targetType = ConvertToText(GetMemberValue(potionModel, "TargetType"));
        var usage = ConvertToText(GetMemberValue(potionModel, "Usage"));
        var selectionPrompt = ConvertToText(GetMemberValue(potionModel, "SelectionScreenPrompt"));
        var assertMethod = potionModel.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => string.Equals(method.Name, "AssertValidForTargetedPotion", StringComparison.Ordinal) &&
                                      method.GetParameters().Length == 1);
        if (assertMethod is null)
        {
            return new PotionTargetProbe(false, targetType, usage, selectionPrompt, "no_probe_method", null);
        }

        try
        {
            assertMethod.Invoke(potionModel, new object?[] { null });
            return new PotionTargetProbe(false, targetType, usage, selectionPrompt, "assert_valid_for_targeted_potion", null);
        }
        catch (TargetInvocationException ex) when (LooksLikePotionTargetRequired(ex.GetBaseException().Message))
        {
            return new PotionTargetProbe(true, targetType, usage, selectionPrompt, "assert_valid_for_targeted_potion", ex.GetBaseException().Message);
        }
        catch (Exception ex)
        {
            return new PotionTargetProbe(false, targetType, usage, selectionPrompt, "assert_valid_for_targeted_potion_probe_failed", ex.GetBaseException().Message);
        }
    }

    private static bool LooksLikePotionTargetRequired(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("target must be present", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("single target potion", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("targeted potion", StringComparison.OrdinalIgnoreCase);
    }

    private static uint? TryResolveActionTargetId(string? targetId, object? target)
    {
        if (!string.IsNullOrWhiteSpace(targetId) && uint.TryParse(targetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTargetId))
        {
            return parsedTargetId;
        }

        if (target is null)
        {
            return null;
        }

        var candidate = GetNullableUInt(target, "CombatId")
                        ?? GetNullableUInt(target, "Id")
                        ?? GetNullableUInt(GetMemberValue(target, "Monster"), "CombatId")
                        ?? GetNullableUInt(GetMemberValue(target, "Monster"), "Id");
        return candidate;
    }

    private static uint? GetNullableUInt(object? value, string memberName)
    {
        return GetNullableUInt(GetMemberValue(value, memberName));
    }

    private static uint? GetNullableUInt(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static ulong? GetNullableULong(object? value, string memberName)
    {
        return GetNullableULong(GetMemberValue(value, memberName));
    }

    private static ulong? GetNullableULong(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is ulong unsignedLongValue)
        {
            return unsignedLongValue;
        }

        if (value is uint unsignedIntValue)
        {
            return unsignedIntValue;
        }

        if (value is long longValue && longValue >= 0)
        {
            return (ulong)longValue;
        }

        if (value is int intValue && intValue >= 0)
        {
            return (ulong)intValue;
        }

        try
        {
            return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsParameterType(ParameterInfo parameter, string fullName)
    {
        return string.Equals(parameter.ParameterType.FullName, fullName, StringComparison.Ordinal);
    }

    private RuntimeActionResult ExecutePlayCard(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        if (!IsPlayerTurn(runState))
        {
            return new RuntimeActionResult(false, "Cards can only be played during the player's turn.", "not_player_turn");
        }

        var player = GetPlayers(runState).FirstOrDefault();
        var playerCombatState = GetMemberValue(player, "PlayerCombatState");
        var hand = GetMemberValue(playerCombatState, "Hand");
        var cardId = ConvertToText(GetDictionaryValue(action.Params, "card_id"));
        if (string.IsNullOrWhiteSpace(cardId))
        {
            return new RuntimeActionResult(false, "Action does not contain a card_id.", "invalid_action");
        }

        var card = EnumerateObjects(GetMemberValue(hand, "Cards"))
            .Select((candidate, index) => new { Card = candidate, CardId = RuntimeCardIdentity.CreateCardId(candidate, index) })
            .FirstOrDefault(candidate => string.Equals(candidate.CardId, cardId, StringComparison.Ordinal))
            ?.Card;
        if (card is null)
        {
            return new RuntimeActionResult(false, $"Card '{cardId}' is no longer in hand.", "stale_action");
        }

        var canonicalCardId = ResolveCardCanonicalId(card);
        var selectionDiagnosticsEnabled = LooksLikeCombatSelectionCard(card, canonicalCardId);
        var beforeSnapshot = CapturePlayCardRuntimeSnapshot(runNode, runState, hand);

        object? target = null;
        var targetId = ConvertToText(GetDictionaryValue(request.Params, "target_id"));
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            target = EnumerateObjects(GetMemberValue(GetCombatState(runState), "Enemies"))
                .FirstOrDefault(enemy => string.Equals(ResolveEnemyId(enemy, 0), targetId, StringComparison.Ordinal));

            if (target is null)
            {
                return new RuntimeActionResult(false, $"Target '{targetId}' is no longer available.", "invalid_target");
            }
        }

        var runtimeHandler = "card.TryManualPlay";
        Dictionary<string, object?>? playPathMetadata = null;
        var played = false;
        var tryManualPlay = card.GetType().GetMethod("TryManualPlay", BindingFlags.Public | BindingFlags.Instance);
        if (tryManualPlay is not null)
        {
            try
            {
                played = tryManualPlay.Invoke(card, new[] { target }) as bool? == true;
            }
            catch (TargetInvocationException ex)
            {
                return new RuntimeActionResult(
                    false,
                    $"Card '{cardId}' could not be played: {ex.GetBaseException().Message}",
                    "play_rejected",
                    new Dictionary<string, object?>
                    {
                        ["card_id"] = cardId,
                        ["target_id"] = targetId,
                        ["runtime_handler"] = runtimeHandler,
                        ["play_strategy_attempt"] = "card_try_manual_play",
                        ["play_strategy_failure_step"] = "try_manual_play_threw",
                    });
            }
        }
        else if (TryExecutePlayCardViaPlayerHand(card, target, out var uiRuntimeHandler, out var uiMetadata))
        {
            runtimeHandler = uiRuntimeHandler;
            playPathMetadata = uiMetadata;
            played = true;
        }
        else
        {
            if (uiMetadata is not null)
            {
                playPathMetadata = uiMetadata;
            }

            return new RuntimeActionResult(false, "Card.TryManualPlay is not available in this runtime.", "runtime_incompatible");
        }

        if (!played)
        {
            return new RuntimeActionResult(
                false,
                $"Card '{cardId}' could not be played.",
                "play_rejected",
                new Dictionary<string, object?>
                {
                    ["card_id"] = cardId,
                    ["target_id"] = targetId,
                    ["runtime_handler"] = runtimeHandler,
                    ["play_strategy_attempt"] = string.Equals(runtimeHandler, "card.TryManualPlay", StringComparison.Ordinal)
                        ? "card_try_manual_play"
                        : "player_hand",
                    ["play_strategy_failure_step"] = "play_returned_false",
                });
        }

        var afterSnapshot = CapturePlayCardRuntimeSnapshot(runNode, runState, hand);
        var progressEvidence = CollectPlayCardProgressEvidence(beforeSnapshot, afterSnapshot);

        var metadata = new Dictionary<string, object?>
        {
            ["card_id"] = cardId,
            ["target_id"] = targetId,
            ["runtime_handler"] = runtimeHandler,
            ["state_progress_detected"] = progressEvidence.Count > 0,
            ["state_progress_evidence"] = progressEvidence,
            ["queue_stage"] = "executing",
        };
        if (playPathMetadata is not null)
        {
            foreach (var entry in playPathMetadata)
            {
                metadata[entry.Key] = entry.Value;
            }
        }
        if (!string.IsNullOrWhiteSpace(canonicalCardId))
        {
            metadata["canonical_card_id"] = canonicalCardId;
        }
        AppendPlayCardSnapshotMetadata(metadata, "before", beforeSnapshot);
        AppendPlayCardSnapshotMetadata(metadata, "after", afterSnapshot);

        if (selectionDiagnosticsEnabled)
        {
            var selectionScreen = GetCombatCardSelectionScreen(runNode, runState);
            var overlayTop = GetOverlayTopScreen(runNode);
            metadata["play_card_runtime_type"] = GetTypeName(card);
            metadata["selection_window_visible_after_play"] = selectionScreen is not null;
            metadata["overlay_top_type_after_play"] = GetTypeName(overlayTop);
            metadata["selection_candidate_method_parameters"] = DescribeSelectionMethodParameters(card, runNode, runState, player, target);
            if (selectionScreen is not null)
            {
                metadata["selection_screen_type_after_play"] = GetTypeName(selectionScreen);
                metadata["selection_prompt_after_play"] = ResolveCombatSelectionPrompt(selectionScreen, textDiagnostics: null);
                metadata["selection_kind_after_play"] = ResolveCombatSelectionKind(
                    selectionScreen,
                    ResolveCombatSelectionPrompt(selectionScreen, textDiagnostics: null));
                _logger?.Info(
                    $"Combat selection detected after playing {canonicalCardId ?? cardId}: " +
                    $"screen={GetTypeName(selectionScreen)} prompt={ResolveCombatSelectionPrompt(selectionScreen, textDiagnostics: null) ?? "<none>"}");
            }
            else
            {
                var cardMethods = DescribeCandidateMethods(card, maxCount: 20);
                var handMethods = DescribeCandidateMethods(hand, maxCount: 12);
                var combatState = GetCombatState(runState);
                var combatStateMethods = DescribeCandidateMethods(combatState, maxCount: 12);
                metadata["selection_candidate_card_methods"] = cardMethods;
                metadata["selection_candidate_hand_methods"] = handMethods;
                metadata["selection_candidate_combat_state_methods"] = combatStateMethods;
                _logger?.Warn(
                    $"Combat selection did not appear after playing {canonicalCardId ?? cardId}; " +
                    $"overlay={GetTypeName(overlayTop) ?? "<none>"} card_methods=[{string.Join(", ", cardMethods)}] " +
                    $"hand_methods=[{string.Join(", ", handMethods)}] combat_state_methods=[{string.Join(", ", combatStateMethods)}]");
            }
        }

        if (progressEvidence.Count == 0)
        {
            _logger?.Warn(
                $"Play card produced no live progress card={canonicalCardId ?? cardId} handler={runtimeHandler} " +
                $"before_hand={beforeSnapshot.HandSignature} after_hand={afterSnapshot.HandSignature} " +
                $"before_energy={beforeSnapshot.Energy?.ToString() ?? "?"} after_energy={afterSnapshot.Energy?.ToString() ?? "?"}");
            metadata["play_strategy_failure_step"] = "no_live_state_progress";
            return new RuntimeActionResult(
                false,
                $"Card '{cardId}' did not advance the live state after execution.",
                "runtime_not_applied",
                metadata);
        }

        return new RuntimeActionResult(true, $"Played card '{cardId}'.", metadata: metadata);
    }

    private PlayCardRuntimeSnapshot CapturePlayCardRuntimeSnapshot(object runNode, object runState, object? hand)
    {
        var player = GetPlayers(runState).FirstOrDefault();
        var combatState = GetCombatState(runState);
        var selectionScreen = GetCombatCardSelectionScreen(runNode, runState);
        var selectionPrompt = selectionScreen is null
            ? null
            : ResolveCombatSelectionPrompt(selectionScreen, textDiagnostics: null);
        return new PlayCardRuntimeSnapshot(
            Energy: GetNullableInt(player, "Energy")
                ?? GetNullableInt(GetMemberValue(player, "PlayerCombatState"), "Energy")
                ?? GetNullableInt(GetMemberValue(GetMemberValue(player, "PlayerCombatState"), "Energy"), "Current")
                ?? GetNullableInt(GetMemberValue(GetMemberValue(player, "PlayerCombatState"), "Energy"), "Value"),
            PlayerHp: GetNullableInt(player, "Hp") ?? GetNullableInt(player, "Health"),
            PlayerBlock: GetNullableInt(player, "Block"),
            HandCount: EnumerateObjects(GetMemberValue(hand, "Cards")).Count(),
            HandSignature: BuildHandSignature(hand),
            EnemySignature: BuildEnemySignature(GetMemberValue(combatState, "Enemies")),
            SelectionScreenType: GetTypeName(selectionScreen),
            SelectionKind: selectionScreen is null ? null : ResolveCombatSelectionKind(selectionScreen, selectionPrompt),
            SelectionPrompt: selectionPrompt,
            OverlayTopType: GetTypeName(GetOverlayTopScreen(runNode)));
    }

    private static string BuildHandSignature(object? hand)
    {
        return string.Join(
            "|",
            EnumerateObjects(GetMemberValue(hand, "Cards"))
                .Select((candidate, index) => RuntimeCardIdentity.CreateCardId(candidate, index)));
    }

    private string BuildEnemySignature(object? enemies)
    {
        return string.Join(
            "|",
            EnumerateObjects(enemies)
                .Select((enemy, index) =>
                {
                    var enemyId = ResolveEnemyId(enemy, index);
                    var hp = GetNullableInt(enemy, "Hp") ?? GetNullableInt(enemy, "Health");
                    var block = GetNullableInt(enemy, "Block");
                    return $"{enemyId}:{hp?.ToString() ?? "?"}:{block?.ToString() ?? "?"}";
                }));
    }

    private static List<string> CollectPlayCardProgressEvidence(PlayCardRuntimeSnapshot before, PlayCardRuntimeSnapshot after)
    {
        var evidence = new List<string>();
        if (!string.Equals(before.HandSignature, after.HandSignature, StringComparison.Ordinal))
        {
            evidence.Add("hand_changed");
        }

        if (before.HandCount != after.HandCount)
        {
            evidence.Add("hand_count_changed");
        }

        if (before.Energy != after.Energy)
        {
            evidence.Add("energy_changed");
        }

        if (before.PlayerHp != after.PlayerHp)
        {
            evidence.Add("player_hp_changed");
        }

        if (before.PlayerBlock != after.PlayerBlock)
        {
            evidence.Add("player_block_changed");
        }

        if (!string.Equals(before.EnemySignature, after.EnemySignature, StringComparison.Ordinal))
        {
            evidence.Add("enemy_state_changed");
        }

        if (!string.Equals(before.SelectionScreenType, after.SelectionScreenType, StringComparison.Ordinal) ||
            !string.Equals(before.SelectionKind, after.SelectionKind, StringComparison.Ordinal))
        {
            evidence.Add("selection_window_changed");
        }

        if (!string.Equals(before.SelectionPrompt, after.SelectionPrompt, StringComparison.Ordinal))
        {
            evidence.Add("selection_prompt_changed");
        }

        if (!string.Equals(before.OverlayTopType, after.OverlayTopType, StringComparison.Ordinal))
        {
            evidence.Add("overlay_changed");
        }

        return evidence;
    }

    private static void AppendPlayCardSnapshotMetadata(
        IDictionary<string, object?> metadata,
        string prefix,
        PlayCardRuntimeSnapshot snapshot)
    {
        metadata[$"{prefix}_energy"] = snapshot.Energy;
        metadata[$"{prefix}_player_hp"] = snapshot.PlayerHp;
        metadata[$"{prefix}_player_block"] = snapshot.PlayerBlock;
        metadata[$"{prefix}_hand_count"] = snapshot.HandCount;
        metadata[$"{prefix}_hand_signature"] = snapshot.HandSignature;
        metadata[$"{prefix}_enemy_signature"] = snapshot.EnemySignature;
        metadata[$"{prefix}_selection_screen_type"] = snapshot.SelectionScreenType;
        metadata[$"{prefix}_selection_kind"] = snapshot.SelectionKind;
        metadata[$"{prefix}_selection_prompt"] = snapshot.SelectionPrompt;
        metadata[$"{prefix}_overlay_top_type"] = snapshot.OverlayTopType;
    }

    private bool TryExecutePlayCardViaPlayerHand(
        object card,
        object? target,
        out string runtimeHandler,
        out Dictionary<string, object?>? metadata)
    {
        runtimeHandler = string.Empty;
        metadata = new Dictionary<string, object?>
        {
            ["play_strategy_attempt"] = "player_hand",
        };

        var playerHandType = FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
        metadata["player_hand_type"] = playerHandType?.FullName;
        var playerHand = GetMemberValue(playerHandType, "Instance");
        if (playerHand is null)
        {
            metadata["play_strategy_failure_step"] = "player_hand_instance_missing";
            return false;
        }
        metadata["player_hand_runtime_type"] = GetTypeName(playerHand);

        var getCardHolder = playerHand.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => string.Equals(method.Name, "GetCardHolder", StringComparison.Ordinal));
        if (getCardHolder is null)
        {
            metadata["play_strategy_failure_step"] = "get_card_holder_missing";
            metadata["player_hand_candidate_methods"] = DescribeNamedMethods(playerHand, "Card", "Holder", "Play");
            return false;
        }

        object? holder;
        try
        {
            holder = getCardHolder.Invoke(playerHand, new[] { card });
        }
        catch
        {
            metadata["play_strategy_failure_step"] = "get_card_holder_failed";
            return false;
        }

        if (holder is null)
        {
            metadata["play_strategy_failure_step"] = "card_holder_missing";
            return false;
        }

        var startCardPlay = playerHand.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => string.Equals(method.Name, "StartCardPlay", StringComparison.Ordinal));
        if (startCardPlay is null)
        {
            metadata["play_strategy_failure_step"] = "start_card_play_missing";
            metadata["player_hand_candidate_methods"] = DescribeNamedMethods(playerHand, "Card", "Holder", "Play");
            return false;
        }

        try
        {
            startCardPlay.Invoke(playerHand, new object?[] { holder, false });
        }
        catch
        {
            metadata["play_strategy_failure_step"] = "start_card_play_failed";
            return false;
        }

        var currentCardPlay = GetMemberValue(playerHand, "_currentCardPlay") ?? GetMemberValue(playerHand, "CurrentCardPlay");
        if (currentCardPlay is null)
        {
            metadata["play_strategy_failure_step"] = "current_card_play_missing";
            TryCancelPlayerHandCardPlay(playerHand);
            return false;
        }

        var tryPlayCard = currentCardPlay.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => string.Equals(method.Name, "TryPlayCard", StringComparison.Ordinal));
        if (tryPlayCard is null)
        {
            metadata["play_strategy_failure_step"] = "try_play_card_missing";
            metadata["card_play_candidate_methods"] = DescribeNamedMethods(currentCardPlay, "Play", "Target", "Card");
            TryCancelPlayerHandCardPlay(playerHand);
            return false;
        }

        try
        {
            tryPlayCard.Invoke(currentCardPlay, new[] { target });
        }
        catch
        {
            metadata["play_strategy_failure_step"] = "try_play_card_failed";
            TryCancelPlayerHandCardPlay(playerHand);
            return false;
        }

        runtimeHandler = "player_hand.StartCardPlay+ncard_play.TryPlayCard";
        metadata["play_strategy"] = "player_hand";
        metadata["card_holder_type"] = GetTypeName(holder);
        metadata["card_play_type"] = GetTypeName(currentCardPlay);
        return true;
    }

    private static IReadOnlyList<string> DescribeNamedMethods(object? target, params string[] fragments)
    {
        if (target is null)
        {
            return Array.Empty<string>();
        }

        return target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(method => fragments.Any(fragment => method.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Select(method =>
            {
                var parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
                return $"{method.Name}({parameters})";
            })
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();
    }

    private static void TryCancelPlayerHandCardPlay(object playerHand)
    {
        try
        {
            playerHand.GetType()
                .GetMethod("CancelAllCardPlay", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(playerHand, null);
        }
        catch
        {
        }
    }

    private bool LooksLikeCombatSelectionCard(object? card, string? canonicalCardId)
    {
        if (!string.IsNullOrWhiteSpace(canonicalCardId) &&
            canonicalCardId.Contains("TRUE_GRIT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = string.Join(
            " ",
            new[]
            {
                canonicalCardId,
                ConvertDescriptionTemplateToText(
                    GetFirstMemberValue(card, "RenderedDescription", "RenderedText", "RawDescription", "DescriptionTemplate", "RulesText"),
                    "_"),
                ConvertToText(GetMemberValue(card, "Name")),
                ConvertToText(GetMemberValue(card, "Title")),
                ConvertToText(GetMemberValue(card, "CardType")),
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return text.Contains("选择", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("消耗", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("弃", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("choose", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("select", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("exhaust", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("discard", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> DescribeCandidateMethods(object? target, int maxCount)
    {
        if (target is null)
        {
            return Array.Empty<string>();
        }

        return target.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(method =>
                method.Name.Contains("Play", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Choose", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Click", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Use", StringComparison.OrdinalIgnoreCase))
            .Select(method =>
            {
                var parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
                return $"{method.Name}({parameters})";
            })
            .Distinct(StringComparer.Ordinal)
            .Take(maxCount)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?> DescribeSelectionMethodParameters(
        object? card,
        object runNode,
        object runState,
        object? player,
        object? target)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (card is null)
        {
            return result;
        }

        foreach (var methodName in new[] { "OnPlayWrapper", "OnPlay" })
        {
            var method = card.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal));
            if (method is null)
            {
                continue;
            }

            var parameters = method.GetParameters()
                .Select(parameter => new Dictionary<string, object?>
                {
                    ["name"] = parameter.Name,
                    ["type"] = parameter.ParameterType.FullName ?? parameter.ParameterType.Name,
                    ["candidates"] = DescribeParameterCandidates(
                        parameter.ParameterType,
                        runNode,
                        runState,
                        player,
                        card,
                        target),
                })
                .ToArray();
            result[methodName] = parameters;
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> DescribeParameterCandidates(
        Type parameterType,
        object runNode,
        object runState,
        object? player,
        object? card,
        object? target)
    {
        var roots = new (string Path, object? Value)[]
        {
            ("runNode", runNode),
            ("runState", runState),
            ("player", player),
            ("card", card),
            ("target", target),
        };
        var matches = new List<IReadOnlyDictionary<string, object?>>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            DescribeParameterCandidatesRecursive(root.Path, root.Value, parameterType, depth: 0, maxDepth: 3, matches, visited);
            if (matches.Count >= 8)
            {
                break;
            }
        }

        return matches.ToArray();
    }

    private static void DescribeParameterCandidatesRecursive(
        string path,
        object? value,
        Type parameterType,
        int depth,
        int maxDepth,
        List<IReadOnlyDictionary<string, object?>> matches,
        HashSet<object> visited)
    {
        if (value is null || matches.Count >= 8)
        {
            return;
        }

        if (parameterType.IsInstanceOfType(value))
        {
            matches.Add(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["runtime_type"] = GetTypeName(value),
            });
            return;
        }

        if (depth >= maxDepth || value is string || value.GetType().IsValueType)
        {
            return;
        }

        if (!visited.Add(value))
        {
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var property in value.GetType().GetProperties(flags).Where(property => property.GetIndexParameters().Length == 0))
        {
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            DescribeParameterCandidatesRecursive($"{path}.{property.Name}", child, parameterType, depth + 1, maxDepth, matches, visited);
            if (matches.Count >= 8)
            {
                return;
            }
        }

        foreach (var field in value.GetType().GetFields(flags))
        {
            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch
            {
                continue;
            }

            DescribeParameterCandidatesRecursive($"{path}.{field.Name}", child, parameterType, depth + 1, maxDepth, matches, visited);
            if (matches.Count >= 8)
            {
                return;
            }
        }
    }

    private RuntimeActionResult ExecuteEndTurn(object runState, ActionRequest request)
    {
        if (!IsPlayerTurn(runState))
        {
            return new RuntimeActionResult(false, "End turn is only available during the player's turn.", "not_player_turn");
        }

        var assembly = FindSts2Assembly();
        var playerCommandType = assembly?.GetType("MegaCrit.Sts2.Core.Commands.PlayerCmd");
        var method = playerCommandType?.GetMethod(
            "EndTurn",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var player = GetPlayers(runState).FirstOrDefault();
        if (method is null || player is null)
        {
            return new RuntimeActionResult(false, "PlayerCmd.EndTurn is not available.", "runtime_incompatible");
        }

        var callback = new Func<Task>(() => Task.CompletedTask);
        method.Invoke(null, new object?[] { player, false, callback });
        return new RuntimeActionResult(true, "Ended the current turn.", metadata: new Dictionary<string, object?>
        {
            ["action_type"] = "end_turn",
            ["runtime_handler"] = "PlayerCmd.EndTurn",
        });
    }

    private RuntimeActionResult ExecuteChooseCombatCard(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        var selectionScreen = GetCombatCardSelectionScreen(runNode, runState);
        if (selectionScreen is null)
        {
            return new RuntimeActionResult(false, "Combat selection window is no longer available.", "selection_window_changed");
        }

        var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(selectionScreen));
        if (choices.Count == 0)
        {
            return new RuntimeActionResult(false, "No combat selection choices are currently available.", "stale_action");
        }

        var selectionIndex = GetNullableIntFromObject(GetDictionaryValue(action.Params, "selection_index"));
        var selectedIndex = selectionIndex ?? 0;
        if (selectedIndex < 0 || selectedIndex >= choices.Count)
        {
            return new RuntimeActionResult(false, "Combat selection target is no longer available.", "stale_action");
        }

        var choice = choices[selectedIndex];
        if (TryExecutePlayerHandCombatSelection(selectionScreen, choice, selectedIndex, out var playerHandSelectionResult))
        {
            return playerHandSelectionResult;
        }

        var card = ResolveCardRewardChoiceCard(choice);
        var exportedCardId = ConvertToText(GetDictionaryValue(action.Params, "card_id"));
        if (!string.IsNullOrWhiteSpace(exportedCardId))
        {
            var currentCardId = RuntimeCardIdentity.CreateCardId(card ?? choice, selectedIndex);
            if (!string.Equals(exportedCardId, currentCardId, StringComparison.Ordinal))
            {
                return new RuntimeActionResult(false, "Combat selection window changed before the action executed.", "selection_window_changed");
            }
        }

        var choiceTypeName = GetTypeName(choice) ?? string.Empty;
        var directSelect = selectionScreen.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "SelectCard", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(choice);
            });
        if (directSelect is not null)
        {
            try
            {
                directSelect.Invoke(selectionScreen, new[] { choice });
                return new RuntimeActionResult(true, "Selected combat card.", metadata: new Dictionary<string, object?>
                {
                    ["action_type"] = "choose_combat_card",
                    ["selection_index"] = selectedIndex,
                    ["card_id"] = exportedCardId,
                    ["runtime_handler"] = $"combat_selection_screen.{directSelect.Name}",
                    ["choice_type"] = choiceTypeName,
                });
            }
            catch (Exception ex)
            {
                return new RuntimeActionResult(false, $"Combat selection failed: {ex.GetBaseException().Message}", "runtime_incompatible");
            }
        }

        var handlers = new List<(object Target, string Label)>
        {
            (selectionScreen, "combat_selection_screen"),
            (choice, "combat_selection_choice"),
        };
        if (card is not null)
        {
            handlers.Add((card, "combat_selection_card"));
        }

        var argSets = new List<object?[]>
        {
            Array.Empty<object?>(),
            new object?[] { choice },
            new object?[] { card },
            new object?[] { selectedIndex },
            new object?[] { choice, selectedIndex },
            new object?[] { card, selectedIndex },
        };
        foreach (var handler in handlers)
        {
            if (TryInvokeFirstCompatibleMethod(handler.Target, CardRewardChoiceSelectMethodNames, argSets, out var methodName))
            {
                return new RuntimeActionResult(true, "Selected combat card.", metadata: new Dictionary<string, object?>
                {
                    ["action_type"] = "choose_combat_card",
                    ["selection_index"] = selectedIndex,
                    ["card_id"] = exportedCardId,
                    ["runtime_handler"] = $"{handler.Label}.{methodName}",
                });
            }
        }

        return new RuntimeActionResult(false, "Combat selection hooks are not available.", "runtime_incompatible");
    }

    private RuntimeActionResult ExecuteCancelCombatSelection(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        var selectionScreen = GetCombatCardSelectionScreen(runNode, runState);
        if (selectionScreen is null)
        {
            return new RuntimeActionResult(false, "Combat selection window is no longer available.", "selection_window_changed");
        }

        if (LooksLikePlayerHandSelectionScreen(selectionScreen) &&
            TryInvokeFirstCompatibleMethod(
                selectionScreen,
                CombatCardChoiceCancelMethodNames,
                new[] { Array.Empty<object?>() },
                out var handCancelMethod))
        {
            return new RuntimeActionResult(true, "Cancelled combat selection.", metadata: new Dictionary<string, object?>
            {
                ["action_type"] = "cancel_combat_selection",
                ["runtime_handler"] = $"player_hand_selection.{handCancelMethod}",
            });
        }

        var argSets = new List<object?[]>
        {
            Array.Empty<object?>(),
            new object?[] { false },
            new object?[] { 0 },
        };
        if (TryInvokeFirstCompatibleMethod(selectionScreen, CombatCardChoiceCancelMethodNames, argSets, out var methodName))
        {
            return new RuntimeActionResult(true, "Cancelled combat selection.", metadata: new Dictionary<string, object?>
            {
                ["action_type"] = "cancel_combat_selection",
                ["runtime_handler"] = $"combat_selection_screen.{methodName}",
            });
        }

        return new RuntimeActionResult(false, "Combat selection cancel hooks are not available.", "runtime_incompatible");
    }

    private bool TryExecutePlayerHandCombatSelection(
        object selectionScreen,
        object choice,
        int selectedIndex,
        out RuntimeActionResult result)
    {
        result = default!;
        if (!LooksLikePlayerHandSelectionScreen(selectionScreen))
        {
            return false;
        }

        var holder = ResolvePlayerHandSelectionHolder(selectionScreen, choice);
        var card = ResolveCardRewardChoiceCard(choice);
        if (holder is null)
        {
            result = new RuntimeActionResult(false, "Combat selection holder is no longer available.", "stale_action");
            return true;
        }

        if (TryInvokeFirstCompatibleMethod(
            selectionScreen,
            new[] { "SelectCardInSimpleMode", "SelectCardInUpgradeMode", "OnHolderPressed" },
            new[]
            {
                new object?[] { holder },
                new object?[] { choice },
                new object?[] { GetMemberValue(choice, "CardNode") ?? GetMemberValue(holder, "CardNode") ?? choice },
            },
            out var methodName))
        {
            CompletePlayerHandSelectionIfNeeded(selectionScreen);
            result = new RuntimeActionResult(true, "Selected combat card.", metadata: new Dictionary<string, object?>
            {
                ["action_type"] = "choose_combat_card",
                ["selection_index"] = selectedIndex,
                ["card_id"] = RuntimeCardIdentity.CreateCardId(card ?? choice, selectedIndex),
                ["runtime_handler"] = $"player_hand_selection.{methodName}",
                ["selection_screen_type"] = GetTypeName(selectionScreen),
                ["holder_type"] = GetTypeName(holder),
            });
            return true;
        }

        return false;
    }

    private void CompletePlayerHandSelectionIfNeeded(object selectionScreen)
    {
        TryInvokeFirstCompatibleMethod(
            selectionScreen,
            new[] { "CheckIfSelectionComplete" },
            new[] { Array.Empty<object?>() },
            out _);

        if (!LooksLikePlayerHandSelectionScreen(selectionScreen))
        {
            return;
        }

        var confirmButton = GetMemberValue(selectionScreen, "_selectModeConfirmButton")
                            ?? GetMemberValue(selectionScreen, "SelectModeConfirmButton");
        if (confirmButton is null)
        {
            return;
        }

        TryInvokeFirstCompatibleMethod(
            selectionScreen,
            new[] { "OnSelectModeConfirmButtonPressed" },
            new[]
            {
                new object?[] { confirmButton },
                new object?[] { null },
            },
            out _);
    }

    private static bool LooksLikePlayerHandSelectionScreen(object selectionScreen)
    {
        var typeName = GetTypeName(selectionScreen) ?? string.Empty;
        return typeName.Contains("NPlayerHand", StringComparison.OrdinalIgnoreCase) ||
               GetBoolean(selectionScreen, "IsInCardSelection") ||
               GetBoolean(selectionScreen, "InSelectMode");
    }

    private static object? ResolvePlayerHandSelectionHolder(object selectionScreen, object choice)
    {
        if (LooksLikeCardHolder(choice))
        {
            return choice;
        }

        var card = ResolveCardRewardChoiceCard(choice);
        var cardModel = GetMemberValue(choice, "CardModel")
                        ?? GetMemberValue(card, "CardModel")
                        ?? card;
        var getCardHolder = selectionScreen.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => string.Equals(method.Name, "GetCardHolder", StringComparison.Ordinal));
        if (cardModel is null || getCardHolder is null)
        {
            return null;
        }

        try
        {
            return getCardHolder.Invoke(selectionScreen, new[] { cardModel });
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeCardHolder(object? value)
    {
        var typeName = GetTypeName(value) ?? string.Empty;
        return typeName.Contains("NHandCardHolder", StringComparison.OrdinalIgnoreCase) ||
               typeName.Contains("NCardHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCardModel(object? value)
    {
        if (value is null)
        {
            return false;
        }

        var typeName = GetTypeName(value) ?? string.Empty;
        return typeName.Contains("Card", StringComparison.OrdinalIgnoreCase) &&
               (GetMemberValue(value, "CardId") is not null ||
                GetMemberValue(value, "CardType") is not null ||
                GetMemberValue(value, "TargetType") is not null ||
                GetMemberValue(value, "CardData") is not null ||
                GetMemberValue(value, "Definition") is not null ||
                GetMemberValue(value, "Data") is not null);
    }

    private RuntimeActionResult ExecuteChooseReward(object runNode, ActionRequest request, LegalAction action)
    {
        var rewardIndex = GetNullableIntFromObject(GetDictionaryValue(action.Params, "reward_index"));

        var cardRewardScreen = GetCardRewardSelectionScreen(runNode);
        if (cardRewardScreen is not null)
        {
            var choices = FilterCardRewardSelectionChoices(ExtractCardRewardChoiceItems(cardRewardScreen));
            if (choices.Count == 0)
            {
                return new RuntimeActionResult(false, "No card reward choices are currently available.", "stale_action");
            }

            var selectedIndex = rewardIndex ?? 0;
            if (selectedIndex < 0 || selectedIndex >= choices.Count)
            {
                return new RuntimeActionResult(false, "Card reward selection target is no longer available.", "stale_action");
            }

            var choice = choices[selectedIndex];
            var card = ResolveCardRewardChoiceCard(choice);

            var choiceTypeName = GetTypeName(choice) ?? string.Empty;
            var directSelect = cardRewardScreen.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    if (!string.Equals(candidate.Name, "SelectCard", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = candidate.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(choice);
                });

            if (directSelect is not null)
            {
                try
                {
                    directSelect.Invoke(cardRewardScreen, new[] { choice });
                    return new RuntimeActionResult(true, "Selected card reward.", metadata: new Dictionary<string, object?>
                    {
                        ["reward"] = ConvertToText(GetDictionaryValue(action.Params, "reward")),
                        ["reward_index"] = selectedIndex,
                        ["runtime_handler"] = $"card_reward_screen.{directSelect.Name}",
                        ["choice_type"] = choiceTypeName,
                    });
                }
                catch (Exception ex)
                {
                    return new RuntimeActionResult(false, $"Card reward selection failed: {ex.GetBaseException().Message}", "runtime_incompatible");
                }
            }
            var handlers = new List<(object Target, string Label)>
            {
                (cardRewardScreen, "card_reward_screen"),
                (choice, "card_reward_choice"),
            };
            if (card is not null)
            {
                handlers.Add((card, "card_reward_card"));
            }

            var argSets = new List<object?[]>
            {
                Array.Empty<object?>(),
                new object?[] { choice },
                new object?[] { card },
                new object?[] { selectedIndex },
                new object?[] { choice, selectedIndex },
                new object?[] { card, selectedIndex },
            };

            foreach (var handler in handlers)
            {
                if (TryInvokeFirstCompatibleMethod(handler.Target, CardRewardChoiceSelectMethodNames, argSets, out var methodName))
                {
                    return new RuntimeActionResult(true, "Selected card reward.", metadata: new Dictionary<string, object?>
                    {
                        ["reward"] = ConvertToText(GetDictionaryValue(action.Params, "reward")),
                        ["reward_index"] = selectedIndex,
                        ["runtime_handler"] = $"{handler.Label}.{methodName}",
                    });
                }
            }

            return new RuntimeActionResult(false, "Card reward selection hooks are not available.", "runtime_incompatible");
        }

        var rewardScreen = GetRewardScreen(runNode);
        if (rewardScreen is null)
        {
            return new RuntimeActionResult(false, "Rewards screen is not available.", "runtime_not_ready");
        }

        var rewardButtons = GetRewardButtons(rewardScreen).ToArray();
        if (rewardButtons.Length == 0)
        {
            return new RuntimeActionResult(false, "No reward buttons are currently available.", "stale_action");
        }

        var button = rewardIndex is not null && rewardIndex.Value >= 0 && rewardIndex.Value < rewardButtons.Length
            ? rewardButtons[rewardIndex.Value]
            : rewardButtons.FirstOrDefault();
        if (button is null)
        {
            return new RuntimeActionResult(false, "Reward selection target is no longer available.", "stale_action");
        }

        var reward = GetMemberValue(button, "Reward");
        var onSelectWrapper = reward?.GetType().GetMethod("OnSelectWrapper", BindingFlags.Public | BindingFlags.Instance);
        var rewardCollectedFrom = rewardScreen.GetType().GetMethod("RewardCollectedFrom", BindingFlags.Public | BindingFlags.Instance);
        if (reward is null || onSelectWrapper is null || rewardCollectedFrom is null)
        {
            return new RuntimeActionResult(false, "Reward selection hooks are not available.", "runtime_incompatible");
        }

        _ = onSelectWrapper.Invoke(reward, null);
        rewardCollectedFrom.Invoke(rewardScreen, new[] { button });
        return new RuntimeActionResult(true, "Selected reward.", metadata: new Dictionary<string, object?>
        {
            ["reward"] = ConvertToText(GetDictionaryValue(action.Params, "reward")),
            ["reward_index"] = rewardIndex,
        });
    }

    private RuntimeActionResult ExecuteSkipReward(object runNode, ActionRequest request)
    {
        var cardRewardScreen = GetCardRewardSelectionScreen(runNode);
        if (cardRewardScreen is not null)
        {
            var argSets = new List<object?[]>
            {
                Array.Empty<object?>(),
                new object?[] { false },
                new object?[] { 0 },
            };

            if (TryInvokeFirstCompatibleMethod(cardRewardScreen, CardRewardChoiceSkipMethodNames, argSets, out var methodName))
            {
                return new RuntimeActionResult(true, "Skipped card reward selection.", metadata: new Dictionary<string, object?>
                {
                    ["action_type"] = "skip_reward",
                    ["runtime_handler"] = $"card_reward_screen.{methodName}",
                });
            }

            return new RuntimeActionResult(false, "Card reward skip hooks are not available.", "runtime_incompatible");
        }

        var rewardScreen = GetRewardScreen(runNode);
        if (rewardScreen is null)
        {
            return new RuntimeActionResult(false, "Rewards screen is not available.", "runtime_not_ready");
        }

        var rewardButtons = GetRewardButtons(rewardScreen).ToArray();
        if (rewardButtons.Length == 0)
        {
            return new RuntimeActionResult(false, "No reward buttons are currently available.", "stale_action");
        }

        var button = rewardButtons[0];
        var reward = GetMemberValue(button, "Reward");
        var onSkipped = reward?.GetType().GetMethod("OnSkipped", BindingFlags.Public | BindingFlags.Instance);
        var rewardSkippedFrom = rewardScreen.GetType().GetMethod("RewardSkippedFrom", BindingFlags.Public | BindingFlags.Instance);
        if (reward is null || onSkipped is null || rewardSkippedFrom is null)
        {
            return new RuntimeActionResult(false, "Reward skip hooks are not available.", "runtime_incompatible");
        }

        onSkipped.Invoke(reward, null);
        rewardSkippedFrom.Invoke(rewardScreen, new[] { button });
        return new RuntimeActionResult(true, "Skipped current reward.", metadata: new Dictionary<string, object?>
        {
            ["action_type"] = "skip_reward",
        });
    }

    private RuntimeActionResult ExecuteAdvanceReward(object runNode, ActionRequest request, LegalAction action)
    {
        var rewardScreen = GetRewardScreen(runNode);
        if (rewardScreen is null)
        {
            return new RuntimeActionResult(false, "Rewards screen is not available.", "runtime_not_ready");
        }

        var requestedLabel = ConvertToText(GetDictionaryValue(action.Params, "button_label"));
        var textDiagnostics = new TextDiagnosticsCollector();
        if (!TryFindRewardAdvanceButton(runNode, rewardScreen, textDiagnostics, out var buttonNode, out var buttonLabel))
        {
            return new RuntimeActionResult(false, "Reward advance button is not currently available.", "stale_action", new Dictionary<string, object?>
            {
                ["action_type"] = "advance_reward",
                ["button_label"] = requestedLabel,
            });
        }

        if (!string.IsNullOrWhiteSpace(requestedLabel) &&
            !string.Equals(buttonLabel, requestedLabel, StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeActionResult(false, "Reward advance button changed since the action was generated.", "stale_action", new Dictionary<string, object?>
            {
                ["action_type"] = "advance_reward",
                ["button_label"] = requestedLabel,
                ["resolved_button_label"] = buttonLabel,
            });
        }

        if (TryInvokeFirstCompatibleMethod(rewardScreen, RewardAdvanceMethodNames, new[] { new object?[] { buttonNode }, Array.Empty<object?>() }, out var rewardScreenMethod))
        {
            return new RuntimeActionResult(true, "Advanced reward screen.", metadata: new Dictionary<string, object?>
            {
                ["action_type"] = "advance_reward",
                ["button_label"] = buttonLabel,
                ["runtime_handler"] = $"reward_screen.{rewardScreenMethod}",
                ["next_window_expected"] = "map",
            });
        }

        if (TryActivateMenuNode(buttonNode, out var buttonHandler))
        {
            return new RuntimeActionResult(true, "Advanced reward screen.", metadata: new Dictionary<string, object?>
            {
                ["action_type"] = "advance_reward",
                ["button_label"] = buttonLabel,
                ["runtime_handler"] = buttonHandler,
                ["next_window_expected"] = "map",
            });
        }

        return new RuntimeActionResult(false, "Reward advance button is not clickable.", "not_clickable", new Dictionary<string, object?>
        {
            ["action_type"] = "advance_reward",
            ["button_label"] = buttonLabel,
            ["target_type"] = GetTypeName(buttonNode),
        });
    }

    private RuntimeActionResult ExecuteBuyShopOffer(object runNode, object runState, ActionRequest request, LegalAction action, string expectedKind)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["action_type"] = action.Type,
            ["expected_kind"] = expectedKind,
        };
        var analysis = AnalyzeShopPhase(runNode, runState, textDiagnostics: null);
        if (!analysis.TreatAsShop)
        {
            metadata["failure_stage"] = "phase_gate";
            return new RuntimeActionResult(false, "Shop window is not ready.", "runtime_not_ready", metadata);
        }

        var requestedOfferId = ConvertToText(GetDictionaryValue(action.Params, "offer_id"));
        var requestedCanonicalId = ConvertToText(GetDictionaryValue(action.Params, "canonical_id"));
        var requestedKind = ConvertToText(GetDictionaryValue(action.Params, "kind"));
        var requestedPrice = GetNullableIntFromObject(GetDictionaryValue(action.Params, "price"));
        var requestedIndex = GetNullableIntFromObject(GetDictionaryValue(action.Params, "offer_index"));
        if (!string.IsNullOrWhiteSpace(requestedOfferId))
        {
            metadata["offer_id"] = requestedOfferId;
        }
        if (!string.IsNullOrWhiteSpace(requestedCanonicalId))
        {
            metadata["canonical_id"] = requestedCanonicalId;
        }
        if (!string.IsNullOrWhiteSpace(requestedKind))
        {
            metadata["kind"] = requestedKind;
        }
        if (requestedPrice is not null)
        {
            metadata["price"] = requestedPrice.Value;
        }
        if (requestedIndex is not null)
        {
            metadata["offer_index"] = requestedIndex.Value;
        }

        var offer = analysis.Offers.FirstOrDefault(candidate =>
            (string.IsNullOrWhiteSpace(requestedOfferId) || string.Equals(candidate.OfferId, requestedOfferId, StringComparison.OrdinalIgnoreCase)) &&
            (requestedIndex is null || candidate.Index == requestedIndex.Value));
        if (offer == default)
        {
            return new RuntimeActionResult(false, "Shop offer is no longer available.", "stale_action", metadata);
        }

        if (!string.Equals(offer.Kind, expectedKind, StringComparison.OrdinalIgnoreCase))
        {
            metadata["resolved_kind"] = offer.Kind;
            return new RuntimeActionResult(false, "Shop offer kind changed since the action was generated.", "stale_action", metadata);
        }

        if (!string.IsNullOrWhiteSpace(requestedKind) && !string.Equals(offer.Kind, requestedKind, StringComparison.OrdinalIgnoreCase))
        {
            metadata["resolved_kind"] = offer.Kind;
            return new RuntimeActionResult(false, "Shop offer kind changed since the action was generated.", "stale_action", metadata);
        }

        if (!string.IsNullOrWhiteSpace(requestedCanonicalId) && !string.Equals(offer.CanonicalId, requestedCanonicalId, StringComparison.OrdinalIgnoreCase))
        {
            metadata["resolved_canonical_id"] = offer.CanonicalId;
            return new RuntimeActionResult(false, "Shop offer identity changed since the action was generated.", "stale_action", metadata);
        }

        if (requestedPrice is not null && offer.Price != requestedPrice.Value)
        {
            metadata["resolved_price"] = offer.Price;
            return new RuntimeActionResult(false, "Shop offer price changed since the action was generated.", "stale_action", metadata);
        }

        if (!offer.Purchasable)
        {
            metadata["unavailable_reason"] = offer.UnavailableReason;
            return new RuntimeActionResult(false, "Shop offer cannot currently be purchased.", offer.UnavailableReason ?? "stale_action", metadata);
        }

        var currentRoom = GetMemberValue(runState, "CurrentRoom");
        var inventoryEntity = GetMemberValue(currentRoom, "Inventory")
            ?? GetMemberValue(GetMemberValue(ResolveMerchantRoomNode(runNode), "Inventory"), "Inventory");
        var activationNode = offer.ActivationNode ?? offer.OfferNode;
        if (activationNode is not null)
        {
            if (inventoryEntity is not null &&
                TryInvokeFirstCompatibleMethod(
                    activationNode,
                    new[] { "OnTryPurchase" },
                    new[]
                    {
                        new object?[] { inventoryEntity },
                        new object?[] { inventoryEntity, false },
                        new object?[] { inventoryEntity, false, true },
                    },
                    out var purchaseMethod))
            {
                metadata["runtime_handler"] = $"shop_node.{purchaseMethod}";
                metadata["resolved_name"] = offer.Name;
                metadata["resolved_price"] = offer.Price;
                metadata["next_window_expected"] = offer.Kind == "service" ? "shop_card_selection" : "shop";
                return new RuntimeActionResult(true, offer.Kind == "service" ? "Opened shop card removal." : "Purchased shop offer.", metadata: metadata);
            }

            if (TryActivateMenuNode(activationNode, out var buttonHandler))
            {
                metadata["runtime_handler"] = buttonHandler;
                metadata["resolved_name"] = offer.Name;
                metadata["resolved_price"] = offer.Price;
                metadata["next_window_expected"] = offer.Kind == "service" ? "shop_card_selection" : "shop";
                return new RuntimeActionResult(true, offer.Kind == "service" ? "Opened shop card removal." : "Purchased shop offer.", metadata: metadata);
                    }

            if (TryInvokeFirstCompatibleMethod(
                    activationNode,
                    ShopPurchaseMethodNames,
                    new[] { Array.Empty<object?>(), new object?[] { activationNode }, new object?[] { inventoryEntity } },
                    out var methodName))
            {
                metadata["runtime_handler"] = $"shop_node.{methodName}";
                metadata["resolved_name"] = offer.Name;
                metadata["resolved_price"] = offer.Price;
                metadata["next_window_expected"] = offer.Kind == "service" ? "shop_card_selection" : "shop";
                return new RuntimeActionResult(true, offer.Kind == "service" ? "Opened shop card removal." : "Purchased shop offer.", metadata: metadata);
            }
        }

        if (offer.OfferNode is not null &&
            inventoryEntity is not null &&
            TryInvokeFirstCompatibleMethod(
                offer.OfferNode,
                new[] { "OnTryPurchase", "OnTryPurchaseWrapper" },
                new[]
                {
                    new object?[] { inventoryEntity, false },
                    new object?[] { inventoryEntity, false, true },
                    new object?[] { inventoryEntity, false, false },
                },
                out var entryMethod))
        {
            metadata["runtime_handler"] = $"merchant_entry.{entryMethod}";
            metadata["resolved_name"] = offer.Name;
            metadata["resolved_price"] = offer.Price;
            metadata["next_window_expected"] = offer.Kind == "service" ? "shop_card_selection" : "shop";
            return new RuntimeActionResult(true, offer.Kind == "service" ? "Opened shop card removal." : "Purchased shop offer.", metadata: metadata);
        }

        return new RuntimeActionResult(false, "Shop offer hooks are not available.", "runtime_incompatible", metadata);
    }

    private RuntimeActionResult ExecuteLeaveShop(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["action_type"] = "leave_shop",
        };
        var analysis = AnalyzeShopPhase(runNode, runState, textDiagnostics: null);
        if (!analysis.TreatAsShop)
        {
            metadata["failure_stage"] = "phase_gate";
            return new RuntimeActionResult(false, "Shop window is not ready.", "runtime_not_ready", metadata);
        }

        var requestedChoice = ConvertToText(GetDictionaryValue(action.Params, "choice"));
        if (!string.IsNullOrWhiteSpace(requestedChoice))
        {
            metadata["choice"] = requestedChoice;
        }

        var merchantRoomNode = ResolveMerchantRoomNode(runNode);
        var inventoryNode = GetMemberValue(merchantRoomNode, "Inventory");
        var proceedButton = GetMemberValue(merchantRoomNode, "ProceedButton") ?? GetMemberValue(merchantRoomNode, "_proceedButton");
        var backButton = GetMemberValue(inventoryNode, "_backButton") ?? GetMemberValue(inventoryNode, "BackButton");

        if (analysis.LeaveButton is not null)
        {
            if (!string.IsNullOrWhiteSpace(requestedChoice) && !string.Equals(requestedChoice, "leave_shop", StringComparison.OrdinalIgnoreCase))
            {
                metadata["resolved_choice"] = "leave_shop";
                return new RuntimeActionResult(false, "Shop leave action changed since the action was generated.", "stale_action", metadata);
            }

            if (ReferenceEquals(analysis.LeaveButton, backButton) &&
                merchantRoomNode is not null &&
                TryInvokeFirstCompatibleMethod(
                    merchantRoomNode,
                    new[] { "HideScreen" },
                    new[]
                    {
                        new object?[] { backButton },
                        new object?[] { null },
                    },
                    out var hideHandler))
            {
                if (proceedButton is not null && TryActivateMenuNode(proceedButton, out var proceedHandler))
                {
                    metadata["runtime_handler"] = $"merchant_room.{hideHandler}+{proceedHandler}";
                    metadata["resolved_button_label"] = analysis.LeaveLabel;
                    metadata["next_window_expected"] = "map";
                    return new RuntimeActionResult(true, "Left shop.", metadata: metadata);
                }

                metadata["runtime_handler"] = $"merchant_room.{hideHandler}";
                metadata["resolved_button_label"] = analysis.LeaveLabel;
                metadata["next_window_expected"] = "shop";
                return new RuntimeActionResult(true, "Closed shop inventory.", metadata: metadata);
            }

            if (TryActivateMenuNode(analysis.LeaveButton, out var handler))
            {
                metadata["runtime_handler"] = handler;
                metadata["resolved_button_label"] = analysis.LeaveLabel;
                metadata["next_window_expected"] = "map";
                return new RuntimeActionResult(true, "Left shop.", metadata: metadata);
            }
        }

        var currentRoom = GetMemberValue(runState, "CurrentRoom");
        if (merchantRoomNode is not null &&
            proceedButton is not null &&
            TryActivateMenuNode(proceedButton, out var proceedOnlyHandler))
        {
            metadata["runtime_handler"] = proceedOnlyHandler;
            metadata["resolved_button_label"] = analysis.LeaveLabel;
            metadata["next_window_expected"] = "map";
            return new RuntimeActionResult(true, "Left shop.", metadata: metadata);
        }

        if (currentRoom is not null &&
            TryInvokeFirstCompatibleMethod(currentRoom, ShopLeaveMethodNames, new[] { Array.Empty<object?>(), new object?[] { analysis.LeaveButton } }, out var methodName))
        {
            metadata["runtime_handler"] = $"merchant_room.{methodName}";
            metadata["resolved_button_label"] = analysis.LeaveLabel;
            metadata["next_window_expected"] = "map";
            return new RuntimeActionResult(true, "Left shop.", metadata: metadata);
        }

        return new RuntimeActionResult(false, "Shop leave hooks are not available.", "runtime_incompatible", metadata);
    }

    private RuntimeActionResult ExecuteChooseEventOption(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["action_type"] = "choose_event_option",
        };
        var analysis = AnalyzeEventPhase(runNode, runState, textDiagnostics: null);
        if (!analysis.TreatAsEvent)
        {
            metadata["failure_stage"] = "phase_gate";
            return new RuntimeActionResult(false, "Event window is not ready.", "runtime_not_ready", metadata);
        }

        if (!string.Equals(analysis.WindowKind, "event_choice", StringComparison.Ordinal))
        {
            metadata["window_kind"] = analysis.WindowKind;
            return new RuntimeActionResult(false, "Event choice window changed before the action executed.", "selection_window_changed", metadata);
        }

        var optionIndex = GetNullableIntFromObject(GetDictionaryValue(action.Params, "option_index"));
        if (optionIndex is null)
        {
            metadata["failure_stage"] = "action_validation";
            return new RuntimeActionResult(false, "Action does not contain an option_index.", "invalid_action", metadata);
        }

        metadata["option_index"] = optionIndex.Value;

        var option = analysis.Options.FirstOrDefault(candidate => candidate.Index == optionIndex.Value);
        if (option == default)
        {
            return new RuntimeActionResult(false, "Event option is no longer available.", "stale_action", metadata);
        }

        if (option.IsContinue)
        {
            metadata["resolved_window_kind"] = analysis.WindowKind;
            return new RuntimeActionResult(false, "Event option turned into a continue action.", "selection_window_changed", metadata);
        }

        var requestedCardId = ConvertToText(GetDictionaryValue(action.Params, "card_id"));
        if (!string.IsNullOrWhiteSpace(requestedCardId))
        {
            metadata["card_id"] = requestedCardId;
        }

        if (!option.Available)
        {
            return new RuntimeActionResult(false, "Event option is not clickable.", "not_clickable", metadata);
        }

        if (option.SelectionScreen is not null)
        {
            return ExecuteEventCardSelectionOption(option, requestedCardId, metadata);
        }

        if (analysis.EventRoom is not null &&
            option.OptionObject is not null &&
            TryInvokeFirstCompatibleMethod(
                analysis.EventRoom,
                new[] { "OptionButtonClicked" },
                new[]
                {
                    new object?[] { option.OptionObject, option.Index },
                    new object?[] { option.OptionObject },
                },
                out var handler))
        {
            metadata["runtime_handler"] = $"event_room.{handler}";
            metadata["resolved_option_label"] = option.Label;
            return new RuntimeActionResult(true, "Selected event option.", metadata: metadata);
        }

        if (option.ButtonNode is not null && TryActivateMenuNode(option.ButtonNode, out var buttonHandler))
        {
            metadata["runtime_handler"] = buttonHandler;
            metadata["resolved_option_label"] = option.Label;
            return new RuntimeActionResult(true, "Selected event option.", metadata: metadata);
        }

        if (option.OptionObject is not null &&
            TryInvokeFirstCompatibleMethod(option.OptionObject, new[] { "Chosen" }, new[] { Array.Empty<object?>() }, out handler))
        {
            metadata["runtime_handler"] = $"event_option.{handler}";
            metadata["resolved_option_label"] = option.Label;
            return new RuntimeActionResult(true, "Selected event option.", metadata: metadata);
        }

        return new RuntimeActionResult(false, "Event option hooks are not available.", "runtime_incompatible", metadata);
    }

    private RuntimeActionResult ExecuteEventCardSelectionOption(
        EventOptionAnalysis option,
        string? requestedCardId,
        Dictionary<string, object?> metadata)
    {
        var selectionScreen = option.SelectionScreen;
        if (selectionScreen is null)
        {
            return new RuntimeActionResult(false, "Event card selection screen is no longer available.", "selection_window_changed", metadata);
        }
        var liveSelectionScreen = selectionScreen;

        if (!string.IsNullOrWhiteSpace(option.StableId))
        {
            metadata["resolved_card_id"] = option.StableId;
        }

        if (!string.IsNullOrWhiteSpace(requestedCardId) &&
            !string.Equals(option.StableId, requestedCardId, StringComparison.Ordinal))
        {
            return new RuntimeActionResult(false, "Event card selection target changed since the action was generated.", "stale_action", metadata);
        }

        var choice = option.OptionObject;
        var card = option.SelectionValue ?? (choice is null ? null : ResolveCardRewardChoiceCard(choice));
        if (card is null && choice is null)
        {
            return new RuntimeActionResult(false, "Event card selection target is no longer available.", "stale_action", metadata);
        }

        var handlers = new List<(object Target, string Label)>();
        if (selectionScreen is not null)
        {
            handlers.Add((selectionScreen, "event_card_selection_screen"));
        }

        if (choice is not null)
        {
            handlers.Add((choice, "event_card_selection_choice"));
        }

        if (card is not null && !ReferenceEquals(card, choice))
        {
            handlers.Add((card, "event_card_selection_card"));
        }

        var argSets = new List<object?[]>
        {
            Array.Empty<object?>(),
            new object?[] { choice },
            new object?[] { card },
            new object?[] { option.Index },
            new object?[] { choice, option.Index },
            new object?[] { card, option.Index },
        };

        foreach (var handler in handlers)
        {
            if (!TryInvokeFirstCompatibleMethod(handler.Target, CardRewardChoiceSelectMethodNames, argSets, out var methodName))
            {
                continue;
            }

            var completionHandler = CompleteEventCardSelectionIfNeeded(liveSelectionScreen);
            metadata["runtime_handler"] = string.IsNullOrWhiteSpace(completionHandler)
                ? $"{handler.Label}.{methodName}"
                : $"{handler.Label}.{methodName}+{completionHandler}";
            metadata["resolved_option_label"] = option.Label;
            metadata["event_subphase"] = "card_selection";
            return new RuntimeActionResult(true, "Selected event card option.", metadata: metadata);
        }

        return new RuntimeActionResult(false, "Event card selection hooks are not available.", "runtime_incompatible", metadata);
    }

    private string? CompleteEventCardSelectionIfNeeded(object selectionScreen)
    {
        TryInvokeFirstCompatibleMethod(
            selectionScreen,
            new[] { "CheckIfSelectionComplete", "RefreshConfirmButtonVisibility" },
            new[] { Array.Empty<object?>() },
            out _);

        var confirmButtons = new[]
        {
            GetMemberValue(selectionScreen, "_confirmButton"),
            GetMemberValue(selectionScreen, "ConfirmButton"),
            GetMemberValue(selectionScreen, "_singlePreviewConfirmButton"),
            GetMemberValue(selectionScreen, "SinglePreviewConfirmButton"),
            GetMemberValue(selectionScreen, "_multiPreviewConfirmButton"),
            GetMemberValue(selectionScreen, "MultiPreviewConfirmButton"),
        };
        foreach (var confirmButton in confirmButtons.Where(button => button is not null))
        {
            if (TryInvokeFirstCompatibleMethod(
                    selectionScreen,
                    new[] { "ConfirmSelection" },
                    new[]
                    {
                        new object?[] { confirmButton },
                        new object?[] { null },
                    },
                    out var confirmMethod))
            {
                return $"event_card_selection_screen.{confirmMethod}";
            }

            if (TryActivateMenuNode(confirmButton!, out var buttonHandler))
            {
                return buttonHandler;
            }
        }

        return null;
    }

    private RuntimeActionResult ExecuteContinueEvent(object runNode, object runState, ActionRequest request, LegalAction action)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["action_type"] = "continue_event",
        };
        var analysis = AnalyzeEventPhase(runNode, runState, textDiagnostics: null);
        if (!analysis.TreatAsEvent)
        {
            metadata["failure_stage"] = "phase_gate";
            return new RuntimeActionResult(false, "Event continue window is not ready.", "runtime_not_ready", metadata);
        }

        if (!analysis.ContinueAvailable)
        {
            metadata["window_kind"] = analysis.WindowKind;
            return new RuntimeActionResult(false, "Event continue action is no longer available.", "stale_action", metadata);
        }

        if (string.Equals(analysis.WindowKind, "event_choice", StringComparison.Ordinal))
        {
            metadata["window_kind"] = analysis.WindowKind;
            return new RuntimeActionResult(false, "Event still requires selecting an option before continuing.", "selection_window_changed", metadata);
        }

        var requestedLabel = ConvertToText(GetDictionaryValue(action.Params, "button_label"));
        if (!string.IsNullOrWhiteSpace(requestedLabel))
        {
            metadata["button_label"] = requestedLabel;
        }

        var continueOption = analysis.Options.FirstOrDefault(option => option.IsContinue && option.Available);
        if (continueOption != default)
        {
            if (!string.IsNullOrWhiteSpace(requestedLabel) &&
                !string.Equals(continueOption.Label, requestedLabel, StringComparison.OrdinalIgnoreCase))
            {
                metadata["resolved_button_label"] = continueOption.Label;
                return new RuntimeActionResult(false, "Event continue label changed since the action was generated.", "stale_action", metadata);
            }

            if (analysis.EventRoom is not null &&
                continueOption.OptionObject is not null &&
                TryInvokeFirstCompatibleMethod(
                    analysis.EventRoom,
                    new[] { "OptionButtonClicked" },
                    new[]
                    {
                        new object?[] { continueOption.OptionObject, continueOption.Index },
                        new object?[] { continueOption.OptionObject },
                    },
                    out var optionHandler))
            {
                metadata["runtime_handler"] = $"event_room.{optionHandler}";
                metadata["resolved_button_label"] = continueOption.Label;
                return new RuntimeActionResult(true, "Continued event.", metadata: metadata);
            }

            if (continueOption.ButtonNode is not null && TryActivateMenuNode(continueOption.ButtonNode, out var buttonHandler))
            {
                metadata["runtime_handler"] = buttonHandler;
                metadata["resolved_button_label"] = continueOption.Label;
                return new RuntimeActionResult(true, "Continued event.", metadata: metadata);
            }
        }

        if (analysis.EventRoom is not null &&
            TryInvokeFirstCompatibleMethod(analysis.EventRoom, new[] { "Proceed" }, new[] { Array.Empty<object?>() }, out var handler))
        {
            metadata["runtime_handler"] = $"event_room.{handler}";
            metadata["resolved_button_label"] = analysis.ContinueLabel;
            return new RuntimeActionResult(true, "Continued event.", metadata: metadata);
        }

        return new RuntimeActionResult(false, "Event continue hooks are not available.", "runtime_incompatible", metadata);
    }

    private RuntimeActionResult ExecuteChooseMapNode(ActionRequest request, LegalAction action)
    {
        var mapScreenType = FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen");
        var mapScreen = GetMemberValue(mapScreenType, "Instance");
        if (mapScreen is null)
        {
            return new RuntimeActionResult(false, "Map screen is not available.", "runtime_not_ready");
        }

        var node = ConvertToText(GetDictionaryValue(action.Params, "node"));
        if (string.IsNullOrWhiteSpace(node))
        {
            return new RuntimeActionResult(false, "Action does not contain a node label.", "invalid_action");
        }

        var coord = ParseMapCoord(node);
        if (coord is null)
        {
            return new RuntimeActionResult(false, $"Could not parse map node '{node}'.", "invalid_action");
        }

        var mapCoordType = FindSts2Assembly()?.GetType("MegaCrit.Sts2.Core.Map.MapCoord");
        var travelMethod = mapScreen.GetType().GetMethod("TravelToMapCoord", BindingFlags.Public | BindingFlags.Instance);
        if (mapCoordType is null || travelMethod is null)
        {
            return new RuntimeActionResult(false, "Map travel hooks are not available.", "runtime_incompatible");
        }

        var mapCoord = Activator.CreateInstance(mapCoordType, coord.Value.Col, coord.Value.Row);
        _ = travelMethod.Invoke(mapScreen, new[] { mapCoord });
        return new RuntimeActionResult(true, $"Traveling to map node '{node}'.", metadata: new Dictionary<string, object?>
        {
            ["action_type"] = "choose_map_node",
            ["node"] = node,
            ["coord"] = $"{coord.Value.Col},{coord.Value.Row}",
            ["next_window_expected"] = "room_transition",
            ["transition_kind"] = "map_travel_submitted",
        });
    }

    private static object? GetDictionaryValue(IReadOnlyDictionary<string, object?> dictionary, string key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : null;
    }

    private static int? GetNullableIntFromObject(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static (int Col, int Row)? ParseMapCoord(string nodeLabel)
    {
        var atIndex = nodeLabel.LastIndexOf('@');
        if (atIndex < 0 || atIndex + 1 >= nodeLabel.Length)
        {
            return null;
        }

        var coordinate = nodeLabel[(atIndex + 1)..].Split(',');
        if (coordinate.Length != 2 ||
            !int.TryParse(coordinate[0], out var col) ||
            !int.TryParse(coordinate[1], out var row))
        {
            return null;
        }

        return (col, row);
    }
}
