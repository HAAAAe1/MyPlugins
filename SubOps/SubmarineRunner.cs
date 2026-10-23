using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
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
    Collect,
    Done,
    Failed,
}

/// <summary>
/// 收潜水艇的状态机。流程照搬 DailyRoutines 的 AutoSubmarineCollect：
///
/// <code>
/// 判定位置 → 传送回部队房区 → 点入口进屋（确认「进入」）→ 房屋内走到房间切换点
///          → 选「潜水艇工房」→ 走到航行管制面板 → 选「管理潜水艇」
///          → 列表里按槽位下标选艇 → 收远航报告 → 修理 → 出发 → 下一艘
/// </code>
///
/// 三个容易踩坑的地方，这里的处理方式：
/// ① 「进入/离开房屋」是同一个 SelectYesno。旧版无条件点「是」，人在大厅里
///    误交互到出口时会被自动点「是」送出屋，于是「进屋→立刻离开」来回循环。
///    现在含「离开」的一律点「否」，只有其它确认才点「是」。
/// ② 菜单项文本在 AtkValues[i + 7]，不在 PopupMenu 的缓存里。
/// ③ 艇列表里的项顺序 = 槽位顺序（0~3），直接按下标选，不用去认「远航报告/出发」子菜单；
///    已经收过的艇本来就没有报告可收，游戏会自己跳过，少一层判断就少一处错。
/// </summary>
public sealed class SubmarineRunner
{
    /// <summary>部队房屋入口（庭院里那扇门）的 DataId。DR 用的就是这个常量。</summary>
    private const uint HouseEntranceDataId = 2002737;

    /// <summary>「移动到别的房间」的交互点子类型，用于在黑名单命中后换下一个候选。</summary>
    private const int MaxDoorCandidates = 6;

    private readonly Configuration config;
    private readonly Navigator nav;
    private readonly IGameGui gui;
    private readonly ICondition condition;
    private readonly IChatGui chat;

    private DateTime nextActionAt = DateTime.MinValue;

    /// <summary>上一次自动确认的提示文本，避免每 0.4 秒刷一次聊天框。</summary>
    private string lastYesnoText = string.Empty;

    /// <summary>当前这艘艇已经点了多少次「出发」。</summary>
    private int departFires;

    private int handled;

    /// <summary>当前正在处理的槽位（= 列表里的下标 = 潜艇槽位）。</summary>
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

    /// <summary>大厅里候选门的轮换下标：交互后没弹出房间菜单就换下一个。</summary>
    private int doorCursor;

    /// <summary>上一次房门交互的时间，用来判断「超时没反应」。</summary>
    private DateTime doorInteractAt = DateTime.MinValue;

    /// <summary>已排除的房门 ObjectId，避免反复去点「离开房屋」那扇门。</summary>
    private readonly HashSet<ulong> badDoors = new();

    /// <summary>上一次交互的房门 ObjectId（用于在没反应时拉黑它）。</summary>
    private ulong currentDoorId;

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
        this.departFires = 0;
        this.lookupRetries = 0;
        this.doorCursor = 0;
        this.badDoors.Clear();
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
    /// 运行期间弹出的「是 / 否」确认框。
    ///
    /// 「要进入这间房屋吗？」和「要离开这间房屋吗？」用的是同一个窗口，
    /// 旧版一律点「是」，人在大厅里误交互到出口就会被送出去，看起来像
    /// 「刚进屋又自己走了」。所以这里把「离开」单独拎出来点「否」。
    /// </summary>
    public void OnSelectYesno()
    {
        if (!this.Busy)
            return;

        var strings = AddonHelper.ReadStrings(this.gui, AddonHelper.SelectYesno);
        var text = strings.Count > 0 ? strings[0] : string.Empty;

        var isLeave = text.Contains("离开", StringComparison.Ordinal)
                      || text.Contains("離", StringComparison.Ordinal)
                      || text.Contains("出", StringComparison.Ordinal) && text.Contains("房屋", StringComparison.Ordinal);

        if (isLeave)
        {
            if (text != this.lastYesnoText)
            {
                this.lastYesnoText = text;
                this.Say($"拒绝离开：{text}");
            }

            AddonHelper.Fire(this.gui, AddonHelper.SelectYesno, 1);
            return;
        }

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
            case RunnerStage.Collect:
                this.Collect();
                break;
        }
    }

    private void Decide()
    {
        // 进到工房了：直接开面板。
        if (Workshop.InWorkshop)
        {
            this.lookupRetries = 0;
            this.SetStage(RunnerStage.GoPanel, 0);
            return;
        }

        // 在房屋大厅里：走到房间切换点，选工房。
        if (Workshop.InHouse)
        {
            this.lookupRetries = 0;
            this.SetStage(RunnerStage.PickRoom, 0);
            return;
        }

        // 在庭院里：点门进屋。
        if (Workshop.Outdoors)
        {
            this.lookupRetries = 0;
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
        this.teleportAttempts++;
        this.SetStage(RunnerStage.Decide, 6);
    }

    /// <summary>
    /// 庭院里点房门进屋。DR 是按 DataId 找这个 EventObj 的，
    /// 用 DataId 而不是 BaseId 能躲开「同区域里有好几扇长得一样的门」。
    /// </summary>
    private void GoEntrance()
    {
        var entrance = this.FindEventByDataId(HouseEntranceDataId);
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

        // 进屋要读条 + 换场景，给足时间；Decide 会重新判断位置。
        this.SetStage(RunnerStage.Decide, 3);
    }

    /// <summary>
    /// 房屋大厅 → 部队工房。
    /// 大厅里「移动到别的房间」和「离开房屋」是两扇长得一样的门，
    /// 所以这里不再盲目点最近的那个，而是挨个试：交互后没能弹出房间菜单的，
    /// 拉黑换下一个。
    /// </summary>
    private void PickRoom()
    {
        // 房间菜单已经弹出：按文字挑「工房」。
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            var entries = AddonHelper.ReadMenuEntries(this.gui);
            var index = AddonHelper.FindMenuEntry(entries, "潜水艇工房");
            if (index < 0)
                index = AddonHelper.FindMenuEntry(entries, "工房");
            if (index < 0)
                index = AddonHelper.FindMenuEntry(entries, "Workshop");
            if (index < 0)
                index = this.config.RoomMenuIndex;

            if (!AddonHelper.Fire(this.gui, AddonHelper.SelectString, index))
            {
                this.Delay(0.5);
                return;
            }

            this.StatusText = "进入部队工房…";
            this.lookupRetries = 0;
            this.SetStage(RunnerStage.Decide, 4);
            return;
        }

        // 上一步交互了门但什么都没弹出来：判定这扇不是房间切换点，拉黑换下一个。
        if (this.doorInteractAt != DateTime.MinValue
            && (DateTime.UtcNow - this.doorInteractAt).TotalSeconds > 3)
        {
            this.doorInteractAt = DateTime.MinValue;

            if (this.currentDoorId != 0 && this.badDoors.Add(this.currentDoorId))
            {
                this.doorCursor = 0;
                this.Say($"这扇门不通房间菜单，已排除（累计 {this.badDoors.Count} 扇）");
            }
            else
            {
                this.doorCursor++;
            }
        }

        var door = this.PickDoor();
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

        this.currentDoorId = (ulong)door.GameObjectId;
        this.nav.Interact(door);
        this.doorInteractAt = DateTime.UtcNow;
        this.Delay(1.2);
    }

    /// <summary>按锚点距离排序列出候选门，跳过已拉黑的。</summary>
    private IGameObject? PickDoor()
    {
        var anchor = this.config.DoorAnchor;

        var candidates = this.nav.EventObjectsNear(anchor, Configuration.DoorSearchRadius)
            .Where(x => !this.badDoors.Contains((ulong)x.GameObjectId))
            .ToList();

        if (candidates.Count == 0)
            return null;

        var index = Math.Clamp(this.doorCursor, 0, candidates.Count - 1);
        return candidates[index];
    }

    /// <summary>工房里找航行管制面板并打开「管理潜水艇」。</summary>
    private void GoPanel()
    {
        // 面板菜单已经弹出。
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            this.SetStage(RunnerStage.Collect, 0);
            return;
        }

        var panel = this.FindPanel();
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

    /// <summary>
    /// 优先按名字找「航行管制面板」，名字对不上再退回「锚点/脚下附近最近的可交互物件」。
    /// 工房很小，30 码内除了面板没有别的可交互东西。
    /// </summary>
    private IGameObject? FindPanel()
    {
        var anchor = this.config.HasPanelAnchor ? this.config.PanelAnchor : default;

        return this.nav.FindEventObjectByName("航行", anchor, Configuration.PanelSearchRadius)
               ?? this.nav.FindEventObjectByName("管制", anchor, Configuration.PanelSearchRadius)
               ?? this.nav.EventObjectsNear(anchor == default ? this.nav.PlayerPosition : anchor,
                       Configuration.PanelSearchRadius)
                   .FirstOrDefault();
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

        // ① 远航报告窗口。1 = 确认并收起报告（DailyRoutines 同款）。
        if (AddonHelper.IsShown(this.gui, AddonHelper.ExplorationResult))
        {
            AddonHelper.Fire(this.gui, AddonHelper.ExplorationResult, 1);
            this.StatusText = "收取远航报告…";
            this.Delay(1.5);
            return;
        }

        // ② 出航详情页：先修零件，再点出发。
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

        // ③ 选择菜单。
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

        // 和 DR 一样一次性把待修部位全发出去，下一轮再看还有没有剩的。
        foreach (var part in damaged)
            Workshop.RepairPart(this.currentSlot, part);

        this.repairAttempts++;
        this.StatusText = $"修理 {this.currentSlot + 1} 号艇（{damaged.Count} 个零件）…";
        this.Delay(1.5);
        return true;
    }

    /// <summary>
    /// 处理 SelectString。只有两种情况会走到这里：
    /// ① 面板主菜单（「管理潜水艇」…）
    /// ② 潜水艇列表（4 艘艇）
    /// 靠「菜单项能不能匹配到艇名 / 是不是 4 项」区分，比认标题文字稳。
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

        // 艇列表：按下标直接选（= 槽位），DR 也是这么干的。
        var slot = this.PickVesselSlot(entries);
        if (slot >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, slot);
            this.currentSlot = slot;
            this.repairAttempts = 0;
            this.StatusText = $"处理 {slot + 1} 号槽位…";
            this.Delay(1.8);
            return;
        }

        // 列表里已经没有被收的艇了：收工。
        if (entries.Count >= 4 && this.handled > 0)
        {
            this.Finish($"全部收完，共 {this.handled} 艘");
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

    /// <summary>
    /// 在菜单里找一艘该处理的艇，返回它在菜单里的下标（= 槽位）。
    ///
    /// 先按「艇名匹配 + 已返航」挑；艇名读不到时退化成「4 项列表里第一个
    /// 还没处理过的槽位」，保证不会因为名字对不上就卡死。
    /// </summary>
    private int PickVesselSlot(List<string> entries)
    {
        var subs = Workshop.Read();

        foreach (var sub in subs)
        {
            if (!sub.IsBack || string.IsNullOrWhiteSpace(sub.Name))
                continue;

            if (sub.Slot == this.currentSlot)
                continue;

            var index = AddonHelper.FindMenuEntry(entries, sub.Name);
            if (index >= 0)
                return index;
        }

        // 名字对不上（国服译名差异 / 自定义名）：只要菜单正好是 4 项，
        // 就按槽位顺序找下一个已返航的。
        if (entries.Count == 4)
        {
            foreach (var sub in subs)
            {
                if (sub.IsBack && sub.Slot != this.currentSlot)
                    return sub.Slot;
            }
        }

        return -1;
    }

    /// <summary>按 DataId 找事件对象（房屋入口用）。</summary>
    private IGameObject? FindEventByDataId(uint dataId)
    {
        return this.nav.EventObjectsNear(this.nav.PlayerPosition, 120f)
            .FirstOrDefault(x => x.BaseId == dataId);
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
