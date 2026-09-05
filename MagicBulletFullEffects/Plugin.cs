using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Framework;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.IoC.ServiceInterface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagicBulletFullEffects;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "MagicBulletFullEffects";

    // 魔弹射手 Action ID
    private const uint MagicBulletActionId = 29415;

    private bool _isActive;
    private byte _savedSelf;
    private byte _savedParty;
    private byte _savedOther;

    [PluginService] internal IFramework Framework { get; init; } = null!;
    [PluginService] internal ICondition Condition { get; init; } = null!;
    [PluginService] internal ITargetManager TargetManager { get; init; } = null!;
    [PluginService] internal IObjectTable ObjectTable { get; init; } = null!;
    [PluginService] internal IPluginLog Log { get; init; } = null!;

    public unsafe Plugin()
    {
        Framework.Update += OnFrameworkUpdate;
        Log.Info("MagicBulletFullEffects loaded. Action ID: {ActionId}", MagicBulletActionId);
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // 检测是否正在施放/刚施放魔弹射手
            if (ShouldActivate())
            {
                if (!_isActive)
                {
                    _isActive = true;
                    EnableFullEffects();
                    Log.Debug("魔弹射手特效已激活：强制全部特效");
                }
            }
            else if (_isActive)
            {
                _isActive = false;
                RestoreEffects();
                Log.Debug("魔弹射手特效已恢复：返回原始设置");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnFrameworkUpdate");
        }
    }

    private unsafe bool ShouldActivate()
    {
        var player = Control.GetLocalPlayer();
        if (player == null) return false;

        var uiState = UIState.Instance();
        if (uiState == null) return false;

        // 检查是否正在施放魔弹射手
        // CastingActionId: 当前正在咏唱的技能ID
        // 正在施放且是魔弹射手
        if (Condition[ConditionFlag.Casting])
        {
            var castingActionId = uiState->PlayerState.CastingActionId;
            if (castingActionId == MagicBulletActionId)
                return true;
        }

        // 检查是否刚施放（动画播放期间）
        // IsCasting: 是否在施法中
        // ActionStatus: 动作状态
        if (!Condition[ConditionFlag.OccupiedInEvent] &&
            !Condition[ConditionFlag.OccupiedInQuestEvent] &&
            !Condition[ConditionFlag.BetweenAreas] &&
            !Condition[ConditionFlag.InCombat])
        {
            return false;
        }

        // 在战斗中且刚施放过魔弹射手（动画窗口）
        // 检查最近施放的技能
        if (Condition[ConditionFlag.InCombat])
        {
            // 检查目标是否是敌对目标（魔弹射手是攻击技能）
            var target = TargetManager.Target;
            if (target == null) return false;

            // 检查是否刚施放过（通过检查动作状态）
            var actionManager = ActionManager.Instance();
            if (actionManager == null) return false;

            // IsCastning: 是否正在施法
            // LastUsedActionSequence: 最近使用的技能序列号
            // 检查是否是魔弹射手的动画窗口期
            var lastActionId = GetLastUsedActionId();
            if (lastActionId == MagicBulletActionId)
                return true;
        }

        return false;
    }

    private unsafe uint GetLastUsedActionId()
    {
        // 通过检查 ActionManager 的状态获取最近使用的技能
        var actionManager = ActionManager.Instance();
        if (actionManager == null) return 0;

        // 检查是否在施法中
        if (Condition[ConditionFlag.Casting])
        {
            var uiState = UIState.Instance();
            if (uiState != null)
                return uiState->PlayerState.CastingActionId;
        }

        // 检查动作队列
        // CastAction: 当前正在执行的动作
        if (actionManager->IsCastning)
        {
            return actionManager->CastAction;
        }

        return 0;
    }

    private unsafe void EnableFullEffects()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;

        // 保存当前设置
        _savedSelf = uiState->BattleEffectSelf;
        _savedParty = uiState->BattleEffectParty;
        _savedOther = uiState->BattleEffectOther;

        // 设置为全部特效 (0 = 全部)
        uiState->BattleEffectSelf = 0;
        uiState->BattleEffectParty = 0;
        uiState->BattleEffectOther = 0;
    }

    private unsafe void RestoreEffects()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;

        // 恢复原始设置
        uiState->BattleEffectSelf = _savedSelf;
        uiState->BattleEffectParty = _savedParty;
        uiState->BattleEffectOther = _savedOther;
    }
}
