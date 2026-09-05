using Dalamud.Game.ClientState.Conditions;
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

    private readonly IFramework _framework;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;

    private bool _isActive;
    private byte _savedSelf;
    private byte _savedParty;
    private byte _savedOther;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        ICondition condition,
        IPluginLog log)
    {
        _framework = framework;
        _condition = condition;
        _log = log;

        _framework.Update += OnFrameworkUpdate;
        _log.Info("MagicBulletFullEffects loaded. Action ID: {ActionId}", MagicBulletActionId);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
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
                    _log.Debug("魔弹射手特效已激活：强制全部特效");
                }
            }
            else if (_isActive)
            {
                _isActive = false;
                RestoreEffects();
                _log.Debug("魔弹射手特效已恢复：返回原始设置");
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error in OnFrameworkUpdate");
        }
    }

    private unsafe bool ShouldActivate()
    {
        var player = Control.GetLocalPlayer();
        if (player == null) return false;

        var uiState = UIState.Instance();
        if (uiState == null) return false;

        // 检查是否正在施放魔弹射手
        if (_condition[ConditionFlag.Casting])
        {
            var castingActionId = uiState->PlayerState.CastingActionId;
            if (castingActionId == MagicBulletActionId)
                return true;
        }

        // 检查是否刚施放（动画播放期间）
        if (_condition[ConditionFlag.InCombat])
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null) return false;

            if (actionManager->IsCastning)
            {
                return actionManager->CastAction == MagicBulletActionId;
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
