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
    private byte _savedPvpEnemy;

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
                    Log.Information("魔弹射手特效已激活：强制对战敌方玩家完全显示");
                }
            }
            else if (_isActive)
            {
                _isActive = false;
                RestoreEffects();
                Log.Information("魔弹射手特效已恢复：返回原始设置");
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
            var castId = actionManager->CastActionId;
            Log.Verbose("Casting action: {ActionId}", castId);
            if (castId == MagicBulletActionId)
                return true;
        }

        return false;
    }

    private unsafe void EnableFullEffects()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;

        _savedPvpEnemy = uiState->BattleEffectPvPEnemyPc;
        uiState->BattleEffectPvPEnemyPc = 0;
        Log.Info("PvP特效已改为完全显示 (was {Old}, now 0)", _savedPvpEnemy);
    }

    private unsafe void RestoreEffects()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return;

        uiState->BattleEffectPvPEnemyPc = _savedPvpEnemy;
        Log.Info("PvP特效已恢复为 {Value}", _savedPvpEnemy);
    }
}
