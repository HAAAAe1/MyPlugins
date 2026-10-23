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
            this.Say("提示：没检测到 vnavmesh，将只用内置平滑移动（室内反而更稳）。");

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
                this.OpenSubmarineList();
                break;
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
            this.Fail("附近找不到部队房屋入口，请先走到门口再运行。");
            return;
        }

        switch (this.nav.Approach(entrance, this.config.InteractRange))
        {
            case ApproachState.Moving:
                this.StatusText = "移动到房屋入口…";
                this.Delay(0.2);
                return;
            case ApproachState.TooFar:
                this.Fail("离房屋入口太远，先传送到住宅区再运行。");
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
        if (AddonHelper.IsOpen(this.gui, AddonHelper.SelectString))
        {
            if (!AddonHelper.Fire(this.gui, AddonHelper.SelectString, this.config.RoomMenuIndex))
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
                this.StatusText = "移动到房间切换点…";
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
        if (AddonHelper.IsOpen(this.gui, AddonHelper.SelectString))
        {
            var strings = AddonHelper.ReadStrings(this.gui, AddonHelper.SelectString);
            if (strings.Exists(s => s.Contains("潜水艇", StringComparison.Ordinal)))
            {
                this.SetStage(RunnerStage.OpenList, 0);
                return;
            }

            // 误点到房间切换的门了，退回工房
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, this.config.RoomMenuIndex);
            this.Delay(2);
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
            this.Fail("找不到航行管制面板。可执行 /subops objects 看看附近有什么，再用 /subops learn panel 覆盖。");
            return;
        }

        switch (this.nav.Approach(panel, this.config.InteractRange))
        {
            case ApproachState.Moving:
                this.StatusText = "移动到航行管制面板…";
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
        this.Delay(1.2);
    }

    private void OpenSubmarineList()
    {
        if (!AddonHelper.Fire(this.gui, AddonHelper.SelectString, this.config.PanelMenuIndex))
        {
            this.Delay(0.5);
            return;
        }

        this.StatusText = "打开潜水艇列表…";
        this.SetStage(RunnerStage.Collect, 2);
    }

    private void Collect()
    {
        if (AddonHelper.IsShown(this.gui, AddonHelper.ExplorationResult))
        {
            AddonHelper.Fire(this.gui, AddonHelper.ExplorationResult, 1);
            this.Delay(0.8);
            return;
        }

        var detailOpen = AddonHelper.IsShown(this.gui, AddonHelper.ExplorationDetail);

        // 已经点过「出发」，窗口也消失了 = 这艘真的派出去了，这时才算处理完一艘。
        if (!detailOpen && this.departFires > 0)
        {
            this.handled++;
            this.currentSlot = -1;
            this.repairAttempts = 0;
            this.departFires = 0;
            this.StatusText = $"已处理 {this.handled} 艘";

            if (this.handled >= this.config.MaxLoops)
            {
                this.Finish("达到轮数上限，先停下");
                return;
            }

            this.Delay(2);
            return;
        }

        if (detailOpen)
        {
            // 出航前先修零件：耐久为 0 的零件会让「出发」失败。
            if (this.TryRepairBeforeDeparture())
                return;

            // 「出发」会连点两次：第一次是详情页的出发，第二次是
            // 「确定要消耗桶装青磷水……」的确认框。所以这里不关窗、不计数，
            // 每 tick 点一次，直到窗口自己消失。
            if (this.config.ReDispatch && this.departFires < 5)
            {
                AddonHelper.Fire(this.gui, AddonHelper.ExplorationDetail, 0);
                this.departFires++;
                this.StatusText = $"出发确认（第 {this.departFires} 次）…";
                this.Delay(1.2);
                return;
            }

            // 点了 5 次还不走，认为卡住，关窗跳过，别把整轮堵死。
            AddonHelper.Close(this.gui, AddonHelper.ExplorationDetail);
            this.departFires = 0;
            this.handled++;
            this.currentSlot = -1;
            this.StatusText = $"已处理 {this.handled} 艘（出发未确认，已跳过）";
            this.Delay(2);
            return;
        }

        if (!AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            this.SetStage(RunnerStage.GoPanel, 1);
            return;
        }

        var slot = Workshop.NextAvailable();
        if (slot < 0)
        {
            this.Finish($"完成：共处理 {this.handled} 艘");
            return;
        }

        AddonHelper.Fire(this.gui, AddonHelper.SelectString, slot);
        this.currentSlot = slot;
        this.repairAttempts = 0;
        this.departFires = 0;
        this.StatusText = $"处理 {slot} 号槽位…";
        this.Delay(2);
    }

    /// <summary>
    /// 详情窗口打开时检查零件，有耐久为 0 的就下发修理命令。
    /// 返回 true 表示「本次做了修理，需要等窗口刷新后再继续」。
    /// </summary>
    private bool TryRepairBeforeDeparture()
    {
        if (!this.config.AutoRepair || this.currentSlot < 0)
            return false;

        // 已修过 2 次还坏着，说明材料不够或被游戏拒绝，不再纠缠，直接往下走。
        if (this.repairAttempts >= 2)
            return false;

        var damaged = Workshop.DamagedPartSlots(this.currentSlot);
        if (damaged.Count == 0)
            return false;

        var (_, repairMaterial) = Workshop.ReadMaterials();
        if (repairMaterial < damaged.Count)
        {
            this.Say($"{this.currentSlot} 号艇有 {damaged.Count} 个零件坏了，但修理材料只有 {repairMaterial} 个，跳过修理。");
            this.repairAttempts = 2;
            return false;
        }

        var done = 0;
        foreach (var part in damaged)
        {
            if (Workshop.RepairPart(this.currentSlot, part))
                done++;
        }

        this.repairAttempts++;
        this.StatusText = $"{this.currentSlot} 号艇修理 {done} 个零件…";
        this.Delay(1.2);
        return true;
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
