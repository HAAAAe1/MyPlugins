using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SubFleet;

/// <summary>
/// 插件入口。
///
/// 这个插件是重新写的，不继承 SubOps 的任何逻辑——SubOps 那套「按窗口状态猜进度」
/// 的状态机是 bug 的根源。这里的结构只有三层：
///   <see cref="Collector"/>  一个角色怎么收（照 DailyRoutines 的顺序，用任务队列驱动）
///   <see cref="Sweep"/>      什么时候切下一个角色
///   <see cref="Ui"/>         状态与开关
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string CommandName = "/subfleet";
    public const string AliasName = "/sf";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private readonly Config config;
    private readonly Move move;
    private readonly Collector collector;
    private readonly Roster roster;
    private readonly Sweep sweep;
    private readonly Ui ui;

    private bool wasRunning;
    private DateTime snapshotAt = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IFramework framework,
        IObjectTable objects,
        IClientState clientState,
        IPlayerState playerState,
        IGameGui gui,
        ICondition condition,
        IChatGui chat,
        ISigScanner sigScanner,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.framework = framework;
        this.clientState = clientState;
        this.chat = chat;
        this.log = log;

        Addons.Initialize(gui, log);
        Workshop.Initialize(sigScanner, log);
        Housing.Initialize(dataManager, log);

        this.config = pluginInterface.GetPluginConfig() as Config ?? new Config();

        this.move = new Move(pluginInterface, objects, clientState, log);
        this.collector = new Collector(this.config, this.move, condition, chat, log);
        this.roster = new Roster(this.config, clientState, playerState, chat, log, this.Save);
        this.sweep = new Sweep(this.config, this.roster, this.collector, pluginInterface, clientState, condition, chat, log, this.Save);
        this.ui = new Ui(this.config, this.collector, this.sweep, this.roster, this.Save);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "潜水艇作业：/subfleet | run | sweep | stop | status | char | debug",
        });

        this.commands.AddHandler(AliasName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "同 /subfleet",
            ShowInHelp = false,
        });

        pluginInterface.UiBuilder.Draw += this.ui.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.ui.Toggle;
        pluginInterface.UiBuilder.OpenMainUi += this.ui.Toggle;

        this.framework.Update += this.OnUpdate;
        this.clientState.Login += this.OnLogin;

        this.log.Information("[SubFleet] 已加载");
    }

    public string Name => "SubFleet";

    public void Dispose()
    {
        this.framework.Update -= this.OnUpdate;
        this.clientState.Login -= this.OnLogin;

        this.pluginInterface.UiBuilder.Draw -= this.ui.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.ui.Toggle;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.ui.Toggle;

        this.commands.RemoveHandler(CommandName);
        this.commands.RemoveHandler(AliasName);

        this.collector.Stop();
        this.Save();
    }

    private void Save() => this.pluginInterface.SavePluginConfig(this.config);

    private void OnLogin()
    {
        var name = this.clientState.IsLoggedIn ? "已登录" : "已登出";
        this.log.Information($"[SubFleet] {name}");
    }

    private void OnUpdate(IFramework _)
    {
        this.roster.Tick();
        this.collector.Tick();

        var running = this.collector.Running;
        if (this.wasRunning && !running)
        {
            var success = this.collector.Outcome == RunOutcome.Success;
            if (success)
                this.snapshotAt = DateTime.UtcNow.AddSeconds(3);

            this.sweep.OnStationFinished(success);
        }

        this.wasRunning = running;

        if (this.snapshotAt != DateTime.MinValue && DateTime.UtcNow >= this.snapshotAt)
        {
            this.snapshotAt = DateTime.MinValue;
            if (this.roster.Current != null)
                this.roster.Snapshot(this.roster.Current);
        }

        this.sweep.Tick();
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "":
                this.ui.Toggle();
                break;
            case "run":
                this.collector.Start();
                break;
            case "sweep":
                this.sweep.Start();
                break;
            case "stop":
                this.sweep.Stop();
                this.collector.Stop();
                break;
            case "status":
                this.PrintStatus();
                break;
            case "char":
            case "roster":
                this.PrintRoster();
                break;
            case "debug":
                this.DumpDebug();
                break;
            default:
                this.chat.Print("[SubFleet] 可用：run | sweep | stop | status | char | debug");
                break;
        }
    }

    private void PrintStatus()
    {
        this.chat.Print($"[SubFleet] 收取：{this.collector.Status}");
        if (!string.IsNullOrEmpty(this.collector.StepText))
            this.chat.Print($"[SubFleet] 　{this.collector.StepText}");

        this.chat.Print($"[SubFleet] 轮换：{this.sweep.StatusText} {this.sweep.Progress}");
        this.chat.Print($"[SubFleet] Lifestream：{(this.sweep.CanSwitch ? "可用" : "未检测到")}");
        this.chat.Print($"[SubFleet] 当前角色：{this.roster.Current?.Key ?? "未登录"}");
    }

    private void PrintRoster()
    {
        if (this.config.Roster.Count == 0)
        {
            this.chat.Print("[SubFleet] 名册是空的，用每个角色登录一次就会自动入册。");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var entry in this.config.Roster)
        {
            var state = !entry.Enabled
                ? "停用"
                : entry.OnCooldown(now)
                    ? $"冷却到 {CharacterEntry.FromUnix(entry.CooldownUntil).ToLocalTime():HH:mm}"
                    : $"下次返航 {(entry.NextReturn()?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—")}";

            this.chat.Print($"[SubFleet] {(entry.Enabled ? "●" : "○")} {entry.Key}  {state}");
        }
    }

    /// <summary>诊断：把「现在到底读到什么」一次性打出来，省得靠猜。</summary>
    private void DumpDebug()
    {
        this.chat.Print($"[SubFleet] 工房={Workshop.InWorkshop} 房内={Workshop.InHouse} 庭院={Workshop.Outdoors} 区域={this.clientState.TerritoryType}");

        if (Housing.TryGetFcEstate(out var estate))
        {
            var plot = estate.HasPlotPosition ? estate.PlotPosition.ToString("F1") : "未知";
            this.chat.Print($"[SubFleet] 部队房屋：区域 {estate.TerritoryId} · {estate.WardIndex + 1} 区 {estate.PlotIndex + 1} 号 · 地块坐标 {plot}");
        }
        else
        {
            this.chat.Print("[SubFleet] 部队房屋：读不到");
        }

        var (ceruleum, material) = Workshop.Materials();
        this.chat.Print($"[SubFleet] 青磷水 {ceruleum} · 修理材料 {material} · 游戏内潜艇下标 {Workshop.CurrentIndex}");

        var subs = Workshop.Read();
        if (subs.Count == 0)
        {
            this.chat.Print("[SubFleet] 潜艇数据：读不到（不在工房？）");
        }
        else
        {
            foreach (var sub in subs)
            {
                var left = sub.ReturnTime - Workshop.Now;
                var state = !sub.Enabled
                    ? "空"
                    : left.TotalSeconds <= 0
                        ? "已返航"
                        : $"还有 {(int)left.TotalHours} 小时 {left.Minutes} 分";

                this.chat.Print($"[SubFleet] {sub.Slot + 1} 号 {sub.Name} Lv{sub.Rank}：{state}");

                if (Workshop.TryReadDamagedParts(sub.Slot, out var damaged, out var diag))
                    this.chat.Print($"[SubFleet] 　零件：坏 [{string.Join(",", damaged)}]（{diag}）");
                else
                    this.chat.Print($"[SubFleet] 　零件：读不到（{diag}）");
            }
        }

        var watched = new[] { Addons.SelectString, Addons.SelectYesno, Addons.Detail, Addons.Result };

        var open = watched.Where(Addons.Shown).ToList();
        this.chat.Print($"[SubFleet] 当前窗口：{(open.Count == 0 ? "无" : string.Join(", ", open))}");

        // 被藏掉但游戏还认为开着的窗口：点列表没反应时多半是它
        var hidden = watched.Where(x => Addons.Exists(x) && !Addons.Shown(x)).ToList();
        if (hidden.Count > 0)
            this.chat.Print($"[SubFleet] 存在但不可见：{string.Join(", ", hidden)}（点列表没反应就是它卡着）");

        this.chat.Print(
            "[SubFleet] 节点就绪："
            + $"列表={Addons.NodesReady(Addons.SelectString)} "
            + $"出航页={Addons.NodesReady(Addons.Detail)} "
            + $"报告={Addons.NodesReady(Addons.Result)}");
    }
}
