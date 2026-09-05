using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace MagicBulletFullEffects;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "MagicBulletFullEffects";

    private const uint MagicBulletActionId = 29415;

    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private bool _isActive;
    private byte _savedSelf;
    private byte _savedParty;
    private byte _savedOther;

    public Plugin()
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

        var actionManager = ActionManager.Instance();
        if (actionManager == null) return false;

        // 检查是否正在施放魔弹射手
        if (Condition[ConditionFlag.Casting])
        {
            if (actionManager->CastActionId == MagicBulletActionId)
                return true;
        }

        // 检查是否刚施放（动画播放期间，施放条已结束但动画还在播）
        if (Condition[ConditionFlag.InCombat])
        {
            if (actionManager->CastActionId == MagicBulletActionId &&
                actionManager->CastTimeElapsed < actionManager->CastTimeTotal)
            {
                return true;
            }
        }

        return false;
    }

    private unsafe void EnableFullEffects()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;

        _savedSelf = uiState->BattleEffectSelf;
        _savedParty = uiState->BattleEffectParty;
        _savedOther = uiState->BattleEffectOther;

        uiState->BattleEffectSelf = 0;
        uiState->BattleEffectParty = 0;
        uiState->BattleEffectOther = 0;
    }

    private unsafe void RestoreEffects()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;

        uiState->BattleEffectSelf = _savedSelf;
        uiState->BattleEffectParty = _savedParty;
        uiState->BattleEffectOther = _savedOther;
    }
}
