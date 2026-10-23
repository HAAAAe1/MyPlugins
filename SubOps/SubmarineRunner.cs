using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace SubOps;

public enum RunnerStage
{
    Idle,
    Decide,
    Teleport,
    GoEntrance,
    PickRoom,
    GoPanel,
    OpenList,
    Collect,
    Done,
    Failed,
}

/// <summary>
/// 收潜水艇的状态机。
///
/// 流程：判断所在地 → （不在房屋）传送到部队房屋 → 进庭院入口 → 选「移动到部队工房」
///      → 走到航行管制面板 → 选「管理潜水艇」 → 逐艘确认 / 再次出航。
///
/// 每个阶段都做了节流，失败会停在 Failed 并给出可操作的原因，不会无限重试。
/// </summary>
public sealed class SubmarineRunner
{
    /// <summary>部队房屋入口的事件对象 BaseId（住宅区里那个门）。</summary>
    private const uint HouseEntranceBaseId = 2002737;

    private readonly Configuration config;
    private readonly Navigator nav;
    private readonly IGameGui gui;
    private readonly ICondition condition;
    private readonly IChatGui chat;

    private DateTime nextActionAt = DateTime.MinValue;

    /// <summary>上一次自动确认的提示文本，避免每 0.4 秒刷一次聊天框。</summary>
    private string lastYesnoText = string.Empty;

    /// <summary>当前这艘艇已经点了多少次「出发」（详情页一次 + 确认框一次）。</summary>
    private int departFires;
    private int handled;

    /// <summary>当前正在处理的槽位（选中列表项后记录，用于读该艇的零件）。</summary>
    private int currentSlot = -1;

    /// <summary>当前槽位已经尝试修理的次数，避免修理失败时反复下发命令。</summary>
    private int repairAttempts;

    /// <summary>传送回房屋的重试次数，防止没有部队房屋时死循环。</summary>
    private int teleportAttempts;

    private const int MaxTeleportAttempts = 8;

    /// <summary>场景物件查找的重试次数。刚传送完/刚切房间时，对象表可能还没加载完。</summary>
    private int lookupRetries;

    private const int MaxLookupRetries = 20;

    /// <summary>单次处理的总时长上限（分钟）。超时即判失败，让轮换去下一个角色。</summary>
    private const int MaxRunMinutes = 10;

    private DateTime runStartedAt = DateTime.MinValue;

    public SubmarineRunner(Configuration config, Navigator nav, IGameGui gui, ICondition condition, IChatGui chat)
    {
        this.config = config;
        this.nav = nav;
        this.gui = gui;
        this.condition = condition;
        this.chat = chat;
    }

    public RunnerStage Stage { get; private set; } = RunnerStage.Idle;

    public string StatusText { get; private set; } = "待机";

    public bool Busy => this.Stage is not (RunnerStage.Idle or RunnerStage.Done or RunnerStage.Failed);

    /// <summary>启动一轮处理。</summary>
    public bool Start()
    {
        if (!this.nav.HasVnavmesh)
            this.Say("提示：没检测到 vnavmesh，瞬移会沿用脚下高度，遇到台阶请手动走一下。");

        var (ceruleum, repair) = Workshop.ReadMaterials();
        if (ceruleum < 20 || repair < 15)
            this.Say($"材料偏少：青磷水 {ceruleum}（建议 ≥20）、修理材料 {repair}（建议 ≥15）。");

        this.handled = 0;
        this.currentSlot = -1;
        this.repairAttempts = 0;
        this.teleportAttempts = 0;
        this.lastYesnoText = string.Empty;
        this.runStartedAt = DateTime.UtcNow;
        this.SetStage(RunnerStage.Decide, 0);
        this.Say("开始。");
        return true;
    }

    public void Stop()
    {
        this.nav.StopMoving();
        this.Stage = RunnerStage.Idle;
        this.StatusText = "已停止";
    }

    /// <summary>由外部（界面回调）调用：结果窗口出现时自动确认。</summary>
    public void OnExplorationResult()
    {
        if (!this.Busy)
            return;

        AddonHelper.Fire(this.gui, AddonHelper.ExplorationResult, 1);
    }

    /// <summary>
    /// 由外部（界面回调）调用：运行期间弹出的「是 / 否」确认框一律点「是」。
    /// 进工房、收取返航结果、再次出航都会经过这个框；不在运行中时不动它。
    /// </summary>
    public void OnSelectYesno()
    {
        if (!this.Busy)
            return;

        var strings = AddonHelper.ReadStrings(this.gui, AddonHelper.SelectYesno);
        var text = strings.Count > 0 ? strings[0] : string.Empty;
        if (!string.IsNullOrEmpty(text) && text != this.lastYesnoText)
        {
            this.lastYesnoText = text;
            this.Say("自动确认：" + text);
        }

        AddonHelper.Fire(this.gui, AddonHelper.SelectYesno, 0);
    }

    public void Tick()
    {
        if (!this.Busy)
            return;

        // 兜底：界面异常 / 反复进入同一个阶段时，别把整夜卡在一艘艇上。
        if (this.runStartedAt != DateTime.MinValue
            && (DateTime.UtcNow - this.runStartedAt).TotalMinutes > MaxRunMinutes)
        {
            this.Fail($"{MaxRunMinutes} 分钟没跑完（卡在 {this.Stage}：{this.StatusText}）");
            return;
        }

        if (this.condition[ConditionFlag.OccupiedInCutSceneEvent] || this.condition[ConditionFlag.WatchingCutscene])
            return;

        if (DateTime.UtcNow < this.nextActionAt)
            return;

        // 兜底轮询：SelectYesno 的 PostSetup 只有一次，而「要进入这间房屋吗？」这类
        // 窗口在那一帧还没初始化完，回调会被吞掉、然后就一直卡着。
        // 只要窗口还开着就每 0.4 秒补一次，直到它关掉为止。
        if (AddonHelper.IsOpen(this.gui, AddonHelper.SelectYesno))
        {
            this.OnSelectYesno();
            this.Delay(0.4);
            return;
        }

        switch (this.Stage)
        {
            case RunnerStage.Decide:
                this.Decide();
                break;
            case RunnerStage.Teleport:
                this.TeleportToEstate();
                break;
            case RunnerStage.GoEntrance:
                this.GoEntrance();
                break;
            case RunnerStage.PickRoom:
                this.PickRoom();
                break;
            case RunnerStage.GoPanel:
                this.GoPanel();
                break;
            case RunnerStage.OpenList:
            case RunnerStage.Collect:
                this.Collect();
                break;
        }
    }

    private void Decide()
    {
        if (Workshop.InWorkshop)
        {
            this.SetStage(RunnerStage.GoPanel, 0);
            return;
        }

        if (Workshop.InHouse)
        {
            this.SetStage(RunnerStage.PickRoom, 0);
            return;
        }

        if (Workshop.Outdoors)
        {
            this.SetStage(RunnerStage.GoEntrance, 0);
            return;
        }

        this.SetStage(RunnerStage.Teleport, 0);
    }

    private unsafe void TeleportToEstate()
    {
        // 没有部队房屋、或传送被拒时会一直重试，这里设个上限，失败就让上层跳过这个角色。
        if (this.teleportAttempts >= MaxTeleportAttempts)
        {
            this.Fail("传送回部队房屋失败（这个角色可能没有部队房屋，或传送被拦截）");
            return;
        }

        // 96 = 部队房屋。传送需要几秒咏唱，之后区域会变，Decide 会重新判断。
        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            this.Fail("传送模块不可用");
            return;
        }

        if (!telepo->Teleport(96, 0))
        {
            this.teleportAttempts++;
            this.SetStage(RunnerStage.Teleport, 2);
            return;
        }

        this.StatusText = "传送到部队房屋…";
        this.SetStage(RunnerStage.Decide, 5);
    }

    private void GoEntrance()
    {
        var entrance = this.nav.FindEventObject(HouseEntranceBaseId, default, 0);
        if (entrance == null)
        {
            if (this.lookupRetries++ < MaxLookupRetries)
            {
                this.StatusText = "等房屋入口加载…";
                this.Delay(0.5);
                return;
            }

            this.lookupRetries = 0;
            this.Fail("附近找不到部队房屋入口。执行 /subops objects 看看周围有什么。");
            return;
        }

        this.lookupRetries = 0;

        switch (this.nav.Approach(entrance, this.config.InteractRange))
        {
            case ApproachState.Moving:
                this.StatusText = "瞬移到房屋入口…";
                this.Delay(0.2);
                return;
            case ApproachState.TooFar:
                this.Fail("离房屋入口太远（超过 200 米），先传送到住宅区再运行。");
                return;
            case ApproachState.Blocked:
                this.Fail("房屋入口不可交互，靠近门口再试。");
                return;
        }

        this.nav.Interact(entrance);
        this.StatusText = "进入房屋…";
        this.SetStage(RunnerStage.Decide, 2.5);
    }

    private void PickRoom()
    {
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            // 门口菜单按文字挑「工房 / 潜水艇」，配置里的下标只作为兜底。
            var entries = AddonHelper.ReadMenuEntries(this.gui);
            var index = AddonHelper.FindMenuEntry(entries, "潜水艇工房");
            if (index < 0)
                index = AddonHelper.FindMenuEntry(entries, "工房");
            if (index < 0)
                index = this.config.RoomMenuIndex;

            if (!AddonHelper.Fire(this.gui, AddonHelper.SelectString, index))
            {
                this.Delay(0.5);
                return;
            }

            this.StatusText = "进入部队工房…";
            this.SetStage(RunnerStage.Decide, 3.5);
            return;
        }

        var anchor = this.config.DoorAnchor;
        var door = this.nav.FindEventObject(0, anchor, Configuration.DoorSearchRadius);
        if (door == null)
        {
            this.Fail("找不到「移动到其他房间」的入口。可执行 /subops objects 看看附近有什么，再用 /subops learn door 覆盖。");
            return;
        }

        switch (this.nav.Approach(door, this.config.InteractRange))
        {
            case ApproachState.Moving:
                this.StatusText = "瞬移到房间切换点…";
                this.Delay(0.2);
                return;
            case ApproachState.TooFar:
                this.Fail("离房间切换点太远（可能已不在房屋内），重新运行。");
                return;
            case ApproachState.Blocked:
                this.Fail("房间切换点不可交互。");
                return;
        }

        this.nav.Interact(door);
        this.Delay(1.5);
    }

    private void GoPanel()
    {
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            this.SetStage(RunnerStage.Collect, 0);
            return;
        }

        // 优先按名字找「航行管制面板」，名字对不上再退回坐标搜索。
        var anchor = this.config.HasPanelAnchor ? this.config.PanelAnchor : default;
        var panel = this.nav.FindEventObjectByName("航行", anchor, Configuration.PanelSearchRadius)
                    ?? this.nav.FindEventObjectByName("潜水艇", anchor, Configuration.PanelSearchRadius)
                    ?? this.nav.FindEventObject(0, anchor == default ? this.nav.PlayerPosition : anchor,
                        Configuration.PanelSearchRadius);
        if (panel == null)
        {
            if (this.lookupRetries++ < MaxLookupRetries)
            {
                this.StatusText = "等航行管制面板加载…";
                this.Delay(0.5);
                return;
            }

            this.lookupRetries = 0;
            this.Fail("找不到航行管制面板。可执行 /subops objects 看看附近有什么，再用 /subops learn panel 覆盖。");
            return;
        }

        this.lookupRetries = 0;

        switch (this.nav.Approach(panel, this.config.InteractRange))
        {
            case ApproachState.Moving:
                this.StatusText = "瞬移到航行管制面板…";
                this.Delay(0.2);
                return;
            case ApproachState.TooFar:
                this.Fail("离航行管制面板太远，可能没进工房。");
                return;
            case ApproachState.Blocked:
                this.Fail("航行管制面板不可交互。");
                return;
        }

        this.nav.Interact(panel);
        this.StatusText = "打开航行管制面板…";
        this.SetStage(RunnerStage.Collect, 1.5);
    }

    private void Collect()
    {
        // 已点过出发、且所有流程窗口都关了 = 这艘真的派出去了，这时才计一艘。
        if (this.departFires > 0 && !this.AnyVoyageWindowOpen())
        {
            this.handled++;
            this.currentSlot = -1;
            this.repairAttempts = 0;
            this.departFires = 0;
            this.StatusText = $"已处理 {this.handled} 艘";

            if (this.handled >= this.config.MaxLoops)
            {
                this.Finish($"达到轮数上限，先停下（共 {this.handled} 艘）");
                return;
            }

            // 列表窗口通常还开着，直接在列表里选下一艘；关了就回面板重开。
            this.SetStage(RunnerStage.Collect, 2);
            return;
        }

        // ① 远航报告窗口。1 = 确认并收起报告（DailyRoutines 同款），0 = 只关闭。
        if (AddonHelper.IsShown(this.gui, AddonHelper.ExplorationResult))
        {
            AddonHelper.Fire(this.gui, AddonHelper.ExplorationResult, 1);
            this.StatusText = "收取远航报告…";
            this.Delay(1.5);
            return;
        }

        // ② 出航详情页：先修零件，再点出发，最后关窗。
        if (AddonHelper.IsShown(this.gui, AddonHelper.ExplorationDetail))
        {
            if (this.config.AutoRepair && this.TryRepairParts())
                return;

            AddonHelper.Fire(this.gui, AddonHelper.ExplorationDetail, 0);
            AddonHelper.Close(this.gui, AddonHelper.ExplorationDetail);
            this.departFires++;
            this.StatusText = $"出发 {this.currentSlot + 1} 号艇…";
            this.Delay(2);
            return;
        }

        // ③ 选择菜单：面板主菜单 / 艇列表，靠文本分辨。
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            this.HandleSelectMenu();
            return;
        }

        // ④ 什么都没开：回去点面板。
        this.SetStage(RunnerStage.GoPanel, 1);
    }

    private bool AnyVoyageWindowOpen() =>
        AddonHelper.IsShown(this.gui, AddonHelper.ExplorationResult) ||
        AddonHelper.IsShown(this.gui, AddonHelper.ExplorationDetail) ||
        AddonHelper.IsShown(this.gui, AddonHelper.SelectString);

    /// <summary>
    /// 在详情页里修损坏零件。用的是游戏的 RepairSubmarinePart(1153) 命令，
    /// 和 DailyRoutines 完全一致：不需要手动选中部位、也不需要打开配件菜单。
    /// 返回 true 表示「本次做了修理，等窗口刷新后再继续」。
    /// </summary>
    private bool TryRepairParts()
    {
        if (this.currentSlot < 0 || this.repairAttempts >= 8)
            return false;

        var damaged = Workshop.DamagedPartSlots(this.currentSlot);
        if (damaged.Count == 0)
            return false;

        var (_, repairMaterial) = Workshop.ReadMaterials();
        if (repairMaterial <= 0)
        {
            if (this.repairAttempts == 0)
                this.Say("修理材料用完了，这次跳过修理。");
            this.repairAttempts = 8;
            return false;
        }

        var part = damaged[0];
        Workshop.RepairPart(this.currentSlot, part);
        this.repairAttempts++;
        this.StatusText = $"修理 {this.currentSlot + 1} 号艇 · {part + 1} 号零件（{damaged.Count} 个待修）…";
        this.Delay(1.2);
        return true;
    }

    /// <summary>
    /// 菜单按文字挑，不按下标猜：
    /// 面板主菜单 → 「管理潜水艇」；单艇菜单 → 「修理配件」/「上次的远航报告」/「出发」；
    /// 艇列表 → 按艇名匹配。已经收过报告的那艘本来就没有「远航报告」项，自然就落到「出发」。
    /// </summary>
    private void HandleSelectMenu()
    {
        var entries = AddonHelper.ReadMenuEntries(this.gui);
        if (entries.Count == 0)
        {
            this.Delay(0.3);
            return;
        }

        var manage = AddonHelper.FindMenuEntry(entries, "管理潜水艇");
        if (manage < 0)
            manage = AddonHelper.FindMenuEntry(entries, "管理潛水艇");
        if (manage >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, manage);
            this.StatusText = "打开潜水艇列表…";
            this.Delay(1.5);
            return;
        }

        var log = AddonHelper.FindMenuEntry(entries, "远航报告");
        if (log >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, log);
            this.StatusText = "打开上次的远航报告…";
            this.Delay(1.5);
            return;
        }

        var deploy = AddonHelper.FindMenuEntry(entries, "出发");
        if (deploy >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, deploy);
            this.StatusText = "选择出航…";
            this.Delay(1.5);
            return;
        }

        var (entryIndex, slot) = this.PickVessel(entries);
        if (entryIndex >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, entryIndex);
            this.currentSlot = slot;
            this.repairAttempts = 0;
            this.StatusText = $"处理 {slot + 1} 号槽位…";
            this.Delay(1.5);
            return;
        }

        var quit = AddonHelper.FindMenuEntry(entries, "取消");
        if (quit < 0)
            quit = AddonHelper.FindMenuEntry(entries, "やめる");
        if (quit >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, quit);
            this.Delay(1);
            return;
        }

        this.Delay(0.5);
    }

    /// <summary>在艇列表菜单里找一艘已返航的艇：返回（菜单项下标, 槽位）。</summary>
    private (int EntryIndex, int Slot) PickVessel(System.Collections.Generic.List<string> entries)
    {
        foreach (var sub in Workshop.Read())
        {
            if (!sub.IsBack || string.IsNullOrWhiteSpace(sub.Name))
                continue;

            var index = AddonHelper.FindMenuEntry(entries, sub.Name);
            if (index >= 0)
                return (index, sub.Slot);
        }

        return (-1, -1);
    }

    private void SetStage(RunnerStage stage, double delaySeconds)
    {
        this.Stage = stage;
        this.nextActionAt = DateTime.UtcNow.AddSeconds(delaySeconds);
    }

    private void Delay(double seconds) => this.nextActionAt = DateTime.UtcNow.AddSeconds(seconds);

    private void Finish(string message)
    {
        this.Stage = RunnerStage.Done;
        this.StatusText = message;
        this.Say(message);
    }

    private void Fail(string message)
    {
        this.Stage = RunnerStage.Failed;
        this.StatusText = message;
        this.Say("停止：" + message);
    }

    private void Say(string message) => this.chat.Print($"[SubOps] {message}");
}
