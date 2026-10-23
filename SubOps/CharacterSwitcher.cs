using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SubOps;

public enum SwitchState
{
    /// <summary>轮换没开。</summary>
    Off,

    /// <summary>在等下一站（切号中 / 角色加载中）。</summary>
    Idle,

    /// <summary>正在处理当前角色的潜水艇。</summary>
    Running,

    /// <summary>正在切角色 / 等登录。</summary>
    Switching,

    /// <summary>名册走完一轮了。</summary>
    AllDone,

    /// <summary>缺依赖（没装 Lifestream）。</summary>
    Unavailable,
}

/// <summary>
/// 多角色轮换：按名册顺序「一站一站」地走。
///
/// 一轮的推进方式：
/// <code>
/// 名册[0] → 切过去 → 进屋 → 开面板 → 收 → 修 → 出航 → 切 名册[1] → … → 走完 → 收工
/// </code>
/// 走的是<b>顺序全扫</b>，不是「谁有活才去谁」——因为潜水艇返航时间只有站进工房才读得到，
/// 靠旧快照猜「这号没活」经常猜错（比如上次出航后又改了航线）。
/// 想让没活的号跳过，用 <c>/subops multi skip on</c>。
///
/// 真正的麻烦都在容错上：切号会失败（排队、断线、加载卡住），
/// 所以每次切换记时间、超时就给那个角色上冷却并跳过，避免整夜在登录界面来回弹。
///
/// 切号本身不自己实现，交给 Lifestream 的 IPC（它能处理排队和过场）。
/// 返回值不解析：Lifestream 的 ErrorCode 是混淆过的枚举，靠「ContentId 有没有变成目标」判断成败更可靠。
/// </summary>
public sealed class CharacterSwitcher
{
    /// <summary>等待切号完成的上限（秒）。超过就认定这次失败，跳到下一个角色。</summary>
    private const int SwitchTimeoutSeconds = 150;

    /// <summary>切失败后该角色冷却时长（分钟）。</summary>
    private const int FailCooldownMinutes = 30;

    /// <summary>跑完一站后留给游戏回写返航数据的时间（秒）。</summary>
    private const int SettleAfterRunSeconds = 4;

    /// <summary>刚切到目标角色后，等它加载完再开工（秒）。</summary>
    private const int SettleAfterSwitchSeconds = 8;

    /// <summary>单个角色的一站最多耗多久（分钟）。超时直接跳过，保证整轮一定能走完。</summary>
    private const int StationTimeoutMinutes = 15;

    private readonly Configuration config;
    private readonly CharacterBook book;
    private readonly SubmarineRunner runner;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<string, string, object>? changeCharacter;
    private readonly ICallGateSubscriber<object>? logout;
    private readonly ICallGateSubscriber<object>? isBusy;

    private DateTime nextActionAt = DateTime.MinValue;
    private string pendingTarget = string.Empty;
    private ulong pendingCid;
    private DateTime pendingSince = DateTime.MinValue;
    private bool notifiedAllDone;

    /// <summary>本轮处理到名册的第几个（0 起）。-1 = 没有正在进行的轮次。</summary>
    private int cursor = -1;

    /// <summary>本轮已成功处理的角色数，仅用于汇报。</summary>
    private int doneCount;

    /// <summary>当前这一站是从哪个 cursor 开始的，用来算这一站耗时。</summary>
    private int stationCursor = -1;

    private DateTime stationStartedAt = DateTime.MinValue;

    public CharacterSwitcher(
        Configuration config,
        CharacterBook book,
        SubmarineRunner runner,
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        ICondition condition,
        IChatGui chat,
        IPluginLog log)
    {
        this.config = config;
        this.book = book;
        this.runner = runner;
        this.clientState = clientState;
        this.condition = condition;
        this.chat = chat;
        this.log = log;

        try
        {
            this.changeCharacter = pluginInterface.GetIpcSubscriber<string, string, object>("Lifestream.ChangeCharacter");
            this.logout = pluginInterface.GetIpcSubscriber<object>("Lifestream.Logout");
            this.isBusy = pluginInterface.GetIpcSubscriber<object>("Lifestream.IsBusy");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubOps] 订阅 Lifestream IPC 失败");
        }
    }

    public SwitchState State { get; private set; } = SwitchState.Off;

    /// <summary>Lifestream 在不在（决定能不能切号）。</summary>
    public bool CanSwitch => this.changeCharacter is { HasFunction: true };

    /// <summary>有没有正在进行的轮次。</summary>
    public bool Active => this.cursor >= 0;

    /// <summary>进度文字，如 <c>2/4</c>；没有进行中的轮次时为空。</summary>
    public string Progress => this.cursor >= 0 && this.config.Characters.Count > 0
        ? $"{Math.Min(this.cursor + 1, this.config.Characters.Count)}/{this.config.Characters.Count}"
        : string.Empty;

    public string StatusText => this.State switch
    {
        SwitchState.Off => "轮换未开启",
        SwitchState.Idle => "等下一站",
        SwitchState.Running => $"收取中 {this.Progress}",
        SwitchState.Switching => string.IsNullOrEmpty(this.pendingTarget) ? "切角色中" : $"切换到 {this.pendingTarget}",
        SwitchState.AllDone => $"全部完成（共 {this.doneCount} 个角色）",
        SwitchState.Unavailable => "未安装 Lifestream",
        _ => "—",
    };

    /// <summary>
    /// 启动一轮全扫：从名册第 0 个开始，按顺序逐个处理。
    /// 当前角色若不是名册第一个，会先被切走。
    /// </summary>
    public bool StartSweep()
    {
        if (!this.CanSwitch)
        {
            this.chat.Print("[SubOps] 没找到 Lifestream 的 IPC，切不了角色。先装 Lifestream 并启用。");
            return false;
        }

        if (!this.config.Characters.Any(c => c.Enabled))
        {
            this.chat.Print("[SubOps] 名册里没有可轮换的角色。先每个角色登录一次，或用 /subops char on <名> 启用。");
            return false;
        }

        this.cursor = 0;
        this.doneCount = 0;
        this.pendingTarget = string.Empty;
        this.notifiedAllDone = false;
        this.nextActionAt = DateTime.UtcNow;
        this.stationCursor = -1;
        this.stationStartedAt = DateTime.MinValue;
        this.config.MultiMode = true;
        this.State = SwitchState.Idle;

        var total = this.config.Characters.Count(c => c.Enabled);
        var first = this.config.Characters.FirstOrDefault(c => c.Enabled);
        this.chat.Print($"[SubOps] 开始轮换：共 {total} 个角色参与，从 {first?.Key ?? "?"} 开始逐个处理。");
        this.chat.Print("[SubOps] 每个角色都会进屋 → 开面板 → 收 → 修 → 出航，然后切下一个。想中途停用 /subops multi off");

        return true;
    }

    /// <summary>中止当前轮次（不登出）。</summary>
    public void Abort()
    {
        this.cursor = -1;
        this.pendingTarget = string.Empty;
        this.config.MultiMode = false;
        this.State = SwitchState.Off;
        this.runner.Stop();
    }

    public void Tick()
    {
        if (!this.config.MultiMode)
        {
            this.cursor = -1;
            this.pendingTarget = string.Empty;

            // AllDone 状态留着显示，直到下次启动。
            if (this.State is SwitchState.Running or SwitchState.Switching or SwitchState.Idle)
                this.State = SwitchState.Off;

            return;
        }

        if (!this.CanSwitch)
        {
            this.State = SwitchState.Unavailable;
            this.config.MultiMode = false;
            this.cursor = -1;
            this.chat.Print("[SubOps] 没找到 Lifestream 的 IPC，切不了角色。装好 Lifestream 再用 /subops multi on。");
            return;
        }

        // 开关打开了但还没启动轮次：等 /subops multi on 或 /subops auto。
        if (this.cursor < 0)
            return;

        if (DateTime.UtcNow < this.nextActionAt)
            return;

        this.nextActionAt = DateTime.UtcNow.AddSeconds(1);

        // 换站了：重置计时，用于「这一站卡太久就跳过」的兜底。
        if (this.stationCursor != this.cursor)
        {
            this.stationCursor = this.cursor;
            this.stationStartedAt = DateTime.UtcNow;
        }

        if (this.stationStartedAt != DateTime.MinValue
            && (DateTime.UtcNow - this.stationStartedAt).TotalMinutes > StationTimeoutMinutes)
        {
            this.SkipStuckStation();
            return;
        }

        // 切号请求已发出：等角色到位，超时就跳过它。
        if (!string.IsNullOrEmpty(this.pendingTarget))
        {
            this.VerifyPending();
            return;
        }

        // 读图 / 过场期间不动，否则会打断加载。
        if (this.condition[ConditionFlag.BetweenAreas] || this.condition[ConditionFlag.OccupiedInCutSceneEvent])
            return;

        if (this.runner.Busy)
        {
            this.State = SwitchState.Running;
            return;
        }

        if (!this.TryResolveTarget(out var target))
        {
            this.FinishSweep();
            return;
        }

        // 已经在目标角色上：直接开工，不用切。
        if (this.book.LoggedIn && this.book.Current?.ContentId == target.ContentId)
        {
            this.StartRunnerFor(target);
            return;
        }

        this.BeginSwitch(target);
    }

    /// <summary>手动切到某个角色（不影响正在进行的轮次）。</summary>
    public bool SwitchTo(string name)
    {
        if (!this.CanSwitch)
        {
            this.chat.Print("[SubOps] 没找到 Lifestream 的 IPC，切不了角色。");
            return false;
        }

        var profile = this.book.Find(name);
        if (profile == null)
        {
            this.chat.Print($"[SubOps] 名册里没有「{name}」。先登录一次那个角色，或 /subops char list 看已有谁。");
            return false;
        }

        if (this.book.Current?.ContentId == profile.ContentId)
        {
            this.chat.Print($"[SubOps] 已经在 {profile.Key} 上了。");
            return false;
        }

        if (this.runner.Busy)
        {
            this.chat.Print("[SubOps] 正在收潜艇，等这一轮跑完再切。");
            return false;
        }

        return this.BeginSwitch(profile);
    }

    /// <summary>
    /// 从 cursor 往后找一个能处理的角色：跳掉停用的、冷却中的，
    /// 以及（开了 skip 时）确定还没返航的。返回 false 表示名册走完了。
    /// </summary>
    private bool TryResolveTarget(out CharacterProfile target)
    {
        target = null!;
        var now = DateTime.UtcNow;

        while (this.cursor >= 0 && this.cursor < this.config.Characters.Count)
        {
            var candidate = this.config.Characters[this.cursor];

            var reason = !candidate.Enabled
                ? "已停用"
                : candidate.OnCooldown(now)
                    ? $"冷却至 {DateTimeExt.FromUnix(candidate.CooldownUntil).ToLocalTime():HH:mm}"
                    : this.config.MultiSkipIdle && !candidate.HasDueWork(now, this.config.MultiAdvanceSeconds)
                        ? "没有到港的艇（skip 已开）"
                        : null;

            if (reason != null)
            {
                this.log.Information($"[SubOps] 跳过 {candidate.Key}：{reason}");
                this.chat.Print($"[SubOps] 跳过 {candidate.Key}（{reason}）");
                this.cursor++;
                continue;
            }

            target = candidate;
            return true;
        }

        return false;
    }

    private void StartRunnerFor(CharacterProfile profile)
    {
        this.chat.Print($"[SubOps] {profile.Key}（{this.Progress}）开始处理");
        this.State = SwitchState.Running;
        this.nextActionAt = DateTime.UtcNow.AddSeconds(5);

        if (this.runner.Start())
            return;

        // 连状态机都起不来（通常是没装 vnavmesh），整轮没意义，直接收尾。
        this.chat.Print("[SubOps] 启动失败，本轮中止。");
        this.cursor = -1;
        this.config.MultiMode = false;
        this.State = SwitchState.Off;
    }

    private bool BeginSwitch(CharacterProfile target)
    {
        var now = DateTime.UtcNow;

        if (this.LifestreamBusy())
        {
            this.State = SwitchState.Switching;
            this.nextActionAt = now.AddSeconds(5);
            return false;
        }

        // 上一个角色的状态机状态别带过来。
        this.runner.Stop();

        try
        {
            // 返回值是 Lifestream 混淆过的 ErrorCode，不解析，只记录。
            var raw = this.changeCharacter!.InvokeFunc(target.Name, target.World);
            this.log.Information($"[SubOps] ChangeCharacter({target.Key}) → {raw}");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubOps] 调用 Lifestream.ChangeCharacter 失败");
            target.CooldownUntil = now.AddMinutes(FailCooldownMinutes).ToUnix();
            this.chat.Print($"[SubOps] 切换 {target.Key} 调用失败，跳过（{FailCooldownMinutes} 分钟内不再尝试）。");
            this.cursor++;
            this.nextActionAt = now.AddSeconds(5);
            return false;
        }

        this.pendingTarget = target.Key;
        this.pendingCid = target.ContentId;
        this.pendingSince = now;
        this.State = SwitchState.Switching;
        this.chat.Print($"[SubOps] 切换到 {target.Key}（{this.Progress}）…");
        this.nextActionAt = now.AddSeconds(20);
        return true;
    }

    private void VerifyPending()
    {
        var now = DateTime.UtcNow;
        var current = this.book.Current;

        // 到位了
        if (current != null && current.ContentId == this.pendingCid)
        {
            this.chat.Print($"[SubOps] 已到 {current.Key}，等加载完成");
            this.pendingTarget = string.Empty;
            this.State = SwitchState.Idle;
            this.nextActionAt = now.AddSeconds(SettleAfterSwitchSeconds);
            return;
        }

        if ((now - this.pendingSince).TotalSeconds < SwitchTimeoutSeconds)
            return;

        // 超时：给目标角色上冷却，跳到下一个。
        var target = this.book.FindByKey(this.pendingTarget);
        if (target != null)
            target.CooldownUntil = now.AddMinutes(FailCooldownMinutes).ToUnix();

        this.chat.Print($"[SubOps] 切换 {this.pendingTarget} 超时（{SwitchTimeoutSeconds} 秒没进去），跳过它。");
        this.pendingTarget = string.Empty;
        this.cursor++;
        this.State = SwitchState.Idle;
        this.nextActionAt = now.AddSeconds(5);
    }

    /// <summary>
    /// 这一站拖太久（通常是角色加载没完、或界面卡住），强制跳过，别把整轮卡死。
    /// </summary>
    private void SkipStuckStation()
    {
        var stuck = this.book.Current;
        if (stuck != null)
        {
            stuck.CooldownUntil = DateTime.UtcNow.AddMinutes(FailCooldownMinutes).ToUnix();
            this.chat.Print($"[SubOps] {stuck.Key} 这一站超过 {StationTimeoutMinutes} 分钟没完成，跳过（{FailCooldownMinutes} 分钟内不再尝试）。");
        }
        else
        {
            this.chat.Print($"[SubOps] 这一站超过 {StationTimeoutMinutes} 分钟没完成，跳过。");
        }

        this.runner.Stop();
        this.pendingTarget = string.Empty;
        this.cursor++;
        this.stationStartedAt = DateTime.MinValue;
        this.stationCursor = -1;
        this.State = SwitchState.Idle;
        this.nextActionAt = DateTime.UtcNow.AddSeconds(5);
    }

    private void FinishSweep()
    {
        this.State = SwitchState.AllDone;
        this.cursor = -1;
        this.pendingTarget = string.Empty;

        if (this.notifiedAllDone)
            return;

        this.notifiedAllDone = true;
        this.chat.Print($"[SubOps] 本轮结束：处理了 {this.doneCount} 个角色。");

        if (!this.config.MultiLogoutWhenDone || !this.book.LoggedIn)
            return;

        try
        {
            this.logout?.InvokeFunc();
            this.chat.Print("[SubOps] 已请求登出。");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubOps] 调用 Lifestream.Logout 失败");
        }
    }

    private bool LifestreamBusy()
    {
        try
        {
            return this.isBusy is { HasFunction: true } subscriber
                   && Convert.ToBoolean(subscriber.InvokeFunc());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前角色这一站跑完了（状态机从 Busy 变空闲时由外部调用）。
    /// 推进到名册下一个，并留几秒给游戏把新的返航时间写回内存。
    /// </summary>
    public void NotifyRunFinished()
    {
        if (this.cursor >= 0)
        {
            this.doneCount++;
            this.log.Information($"[SubOps] 第 {this.cursor + 1} 站完成，进入下一站");
            this.cursor++;
        }

        this.nextActionAt = DateTime.UtcNow.AddSeconds(SettleAfterRunSeconds);
    }

    /// <summary>清掉所有角色的冷却，方便手动重试。</summary>
    public void ResetCooldowns()
    {
        foreach (var c in this.config.Characters)
            c.CooldownUntil = 0;

        this.pendingTarget = string.Empty;
        this.notifiedAllDone = false;
    }

    /// <summary>切号期间不要让人以为插件死了。</summary>
    public bool WaitingForCharacter => !string.IsNullOrEmpty(this.pendingTarget);

    /// <summary>当前是否需要在标题界面等（供命令提示用）。</summary>
    public bool AtTitleScreen => !this.clientState.IsLoggedIn;

    /// <summary>供调试：Lifestream 是否报告忙。</summary>
    public bool LifestreamIsBusy => this.LifestreamBusy();
}
