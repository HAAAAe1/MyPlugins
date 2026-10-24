using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SubFleet;

public enum SweepPhase
{
    Off,
    Idle,
    Running,
    Switching,
    Done,
    Unavailable,
}

/// <summary>
/// 多角色轮换：按名册顺序，一个角色收完切下一个。
///
/// 切号本身交给 Lifestream 的 IPC（它能处理排队、过场、读图），
/// 这边只负责「什么时候该切、切到没到、到了什么时候开工」。
/// 每一步都记时间，超时就给那个角色上冷却跳过，避免整夜卡在登录界面。
/// </summary>
public sealed class Sweep
{
    private const int SwitchTimeoutSeconds = 150;
    private const int FailCooldownMinutes = 30;
    private const int SettleAfterSwitchSeconds = 8;
    private const int SettleAfterStationSeconds = 4;

    private readonly Config config;
    private readonly Roster roster;
    private readonly Collector collector;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Action save;

    private readonly ICallGateSubscriber<string, string, object>? changeCharacter;
    private readonly ICallGateSubscriber<object>? logout;
    private readonly ICallGateSubscriber<object>? isBusy;

    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime settleUntil = DateTime.MinValue;
    private string pendingKey = string.Empty;
    private ulong pendingCid;
    private DateTime pendingSince = DateTime.MinValue;
    private bool notifiedDone;
    private int doneCount;

    public Sweep(
        Config config,
        Roster roster,
        Collector collector,
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        ICondition condition,
        IChatGui chat,
        IPluginLog log,
        Action save)
    {
        this.config = config;
        this.roster = roster;
        this.collector = collector;
        this.clientState = clientState;
        this.condition = condition;
        this.chat = chat;
        this.log = log;
        this.save = save;

        try
        {
            this.changeCharacter = pluginInterface.GetIpcSubscriber<string, string, object>("Lifestream.ChangeCharacter");
            this.logout = pluginInterface.GetIpcSubscriber<object>("Lifestream.Logout");
            this.isBusy = pluginInterface.GetIpcSubscriber<object>("Lifestream.IsBusy");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubFleet] 订阅 Lifestream IPC 失败");
        }
    }

    public SweepPhase Phase { get; private set; } = SweepPhase.Off;

    public bool CanSwitch => this.changeCharacter is { HasFunction: true };

    public string Progress => this.config.Roster.Count == 0
        ? string.Empty
        : $"{Math.Min(this.config.SweepCursor + 1, this.config.Roster.Count)}/{this.config.Roster.Count}";

    public string StatusText => this.Phase switch
    {
        SweepPhase.Off => "轮换未开启",
        SweepPhase.Idle => "等下一站",
        SweepPhase.Running => $"收取中 {this.Progress}",
        SweepPhase.Switching => string.IsNullOrEmpty(this.pendingKey) ? "切角色中" : $"切换到 {this.pendingKey}",
        SweepPhase.Done => $"全部完成（{this.doneCount} 个角色）",
        SweepPhase.Unavailable => "未安装 Lifestream",
        _ => "—",
    };

    public bool Start()
    {
        if (!this.CanSwitch)
        {
            this.chat.Print("[SubFleet] 没找到 Lifestream 的 IPC，切不了角色。先装 Lifestream 并启用。");
            this.Phase = SweepPhase.Unavailable;
            return false;
        }

        if (!this.config.Roster.Any(x => x.Enabled))
        {
            this.chat.Print("[SubFleet] 名册里没有可轮换的角色。先用每个角色登录一次。");
            return false;
        }

        this.config.SweepActive = true;
        this.config.SweepCursor = 0;
        this.pendingKey = string.Empty;
        this.notifiedDone = false;
        this.doneCount = 0;
        this.nextActionAt = DateTime.UtcNow;
        this.settleUntil = DateTime.MinValue;
        this.Phase = SweepPhase.Idle;
        this.save();

        var first = this.config.Roster.FirstOrDefault(x => x.Enabled);
        this.chat.Print($"[SubFleet] 开始轮换：{this.config.EnabledCount} 个角色，从 {first?.Key ?? "?"} 开始。");
        return true;
    }

    public void Stop()
    {
        this.config.SweepActive = false;
        this.pendingKey = string.Empty;
        this.config.SweepCursor = 0;
        this.Phase = SweepPhase.Off;
        this.collector.Stop();
        this.save();
    }

    /// <summary>当前角色这一站结束了（由 Plugin 在 Collector 从运行变空闲时调用）。</summary>
    public void OnStationFinished(bool success)
    {
        if (!this.config.SweepActive)
            return;

        var current = this.roster.Current;
        if (!success && current != null)
        {
            current.CooldownUntil = CharacterEntry.ToUnix(DateTime.UtcNow.AddMinutes(FailCooldownMinutes));
            this.chat.Print($"[SubFleet] {current.Key} 这一站失败，{FailCooldownMinutes} 分钟内不再尝试。");
        }
        else if (success)
        {
            this.doneCount++;
        }

        this.config.SweepCursor++;
        this.nextActionAt = DateTime.UtcNow.AddSeconds(SettleAfterStationSeconds);
        this.save();
    }

    public void Tick()
    {
        if (!this.config.SweepActive)
        {
            if (this.Phase is SweepPhase.Idle or SweepPhase.Running or SweepPhase.Switching)
                this.Phase = SweepPhase.Off;

            return;
        }

        if (!this.CanSwitch)
        {
            this.Phase = SweepPhase.Unavailable;
            this.config.SweepActive = false;
            this.save();
            this.chat.Print("[SubFleet] 没找到 Lifestream 的 IPC，轮换已停止。");
            return;
        }

        if (this.collector.Running)
        {
            this.Phase = SweepPhase.Running;
            return;
        }

        var now = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(this.pendingKey))
        {
            this.VerifyPending(now);
            return;
        }

        if (now < this.nextActionAt)
            return;

        if (!this.roster.LoggedIn)
        {
            this.Phase = SweepPhase.Switching;
            return;
        }

        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        if (now < this.settleUntil)
            return;

        if (!this.TryResolveTarget(now, out var target))
        {
            this.Finish();
            return;
        }

        if (this.roster.Current?.ContentId == target.ContentId)
        {
            this.StartStation(target);
            return;
        }

        this.BeginSwitch(target, now);
    }

    private bool TryResolveTarget(DateTime now, out CharacterEntry target)
    {
        target = null!;

        while (this.config.SweepCursor >= 0 && this.config.SweepCursor < this.config.Roster.Count)
        {
            var candidate = this.config.Roster[this.config.SweepCursor];

            string? reason = null;
            if (!candidate.Enabled)
                reason = "已停用";
            else if (candidate.OnCooldown(now))
                reason = $"冷却到 {CharacterEntry.FromUnix(candidate.CooldownUntil).ToLocalTime():HH:mm}";
            else if (this.config.SweepSkipIdle && !candidate.HasDueWork(now, this.config.SweepAdvanceSeconds))
                reason = "没有到港的艇";

            if (reason != null)
            {
                this.chat.Print($"[SubFleet] 跳过 {candidate.Key}（{reason}）");
                this.log.Information($"[SubFleet] 跳过 {candidate.Key}：{reason}");
                this.config.SweepCursor++;
                continue;
            }

            target = candidate;
            return true;
        }

        return false;
    }

    private void StartStation(CharacterEntry target)
    {
        this.Phase = SweepPhase.Running;
        this.nextActionAt = DateTime.UtcNow.AddSeconds(5);
        this.chat.Print($"[SubFleet] {target.Key}（{this.Progress}）开始处理");

        if (this.collector.Start())
            return;

        this.chat.Print("[SubFleet] 启动失败，本轮中止。");
        this.config.SweepActive = false;
        this.Phase = SweepPhase.Off;
        this.save();
    }

    private void BeginSwitch(CharacterEntry target, DateTime now)
    {
        if (this.LifestreamBusy())
        {
            this.Phase = SweepPhase.Switching;
            this.nextActionAt = now.AddSeconds(5);
            return;
        }

        this.collector.Stop();

        try
        {
            // 返回值是 Lifestream 混淆过的错误码，不解析，只看「ContentId 有没有变过去」。
            var raw = this.changeCharacter!.InvokeFunc(target.Name, target.World);
            this.log.Information($"[SubFleet] ChangeCharacter({target.Key}) → {raw}");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubFleet] 调用 Lifestream.ChangeCharacter 失败");
            target.CooldownUntil = CharacterEntry.ToUnix(now.AddMinutes(FailCooldownMinutes));
            this.chat.Print($"[SubFleet] 切换 {target.Key} 调用失败，跳过。");
            this.config.SweepCursor++;
            this.nextActionAt = now.AddSeconds(5);
            this.save();
            return;
        }

        this.pendingKey = target.Key;
        this.pendingCid = target.ContentId;
        this.pendingSince = now;
        this.Phase = SweepPhase.Switching;
        this.chat.Print($"[SubFleet] 切换到 {target.Key}…");
        this.nextActionAt = now.AddSeconds(20);
    }

    private void VerifyPending(DateTime now)
    {
        var current = this.roster.Current;

        if (current != null && current.ContentId == this.pendingCid)
        {
            this.chat.Print($"[SubFleet] 已到 {current.Key}，等加载完成");
            this.pendingKey = string.Empty;
            this.Phase = SweepPhase.Idle;
            this.settleUntil = now.AddSeconds(SettleAfterSwitchSeconds);
            this.nextActionAt = now.AddSeconds(SettleAfterSwitchSeconds);
            return;
        }

        if ((now - this.pendingSince).TotalSeconds < SwitchTimeoutSeconds)
            return;

        // Lifestream 还在忙（排队/读图）就继续等，别在人家切到一半时判失败
        if (this.LifestreamBusy() && (now - this.pendingSince).TotalSeconds < SwitchTimeoutSeconds * 2)
        {
            this.nextActionAt = now.AddSeconds(5);
            return;
        }

        var target = this.config.FindByName(this.pendingKey);
        if (target != null)
            target.CooldownUntil = CharacterEntry.ToUnix(now.AddMinutes(FailCooldownMinutes));

        this.chat.Print($"[SubFleet] 切换 {this.pendingKey} 超时，跳过。");
        this.pendingKey = string.Empty;
        this.config.SweepCursor++;
        this.Phase = SweepPhase.Idle;
        this.nextActionAt = now.AddSeconds(5);
        this.save();
    }

    private void Finish()
    {
        this.Phase = SweepPhase.Done;
        this.config.SweepActive = false;
        this.config.SweepCursor = 0;
        this.pendingKey = string.Empty;
        this.save();

        if (this.notifiedDone)
            return;

        this.notifiedDone = true;
        this.chat.Print($"[SubFleet] 本轮结束：完成 {this.doneCount} 个角色。");

        if (!this.config.SweepLogoutWhenDone || !this.roster.LoggedIn)
            return;

        try
        {
            this.logout?.InvokeFunc();
            this.chat.Print("[SubFleet] 已请求登出。");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubFleet] 调用 Lifestream.Logout 失败");
        }
    }

    private bool LifestreamBusy()
    {
        try
        {
            return this.isBusy is { HasFunction: true } subscriber && Convert.ToBoolean(subscriber.InvokeFunc());
        }
        catch
        {
            return false;
        }
    }

    public void ResetCooldowns()
    {
        foreach (var entry in this.config.Roster)
            entry.CooldownUntil = 0;

        this.notifiedDone = false;
        this.save();
    }

    public bool Waiting => !string.IsNullOrEmpty(this.pendingKey);

    public bool AtTitleScreen => !this.clientState.IsLoggedIn;
}
