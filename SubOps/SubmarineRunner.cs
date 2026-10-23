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

    /// <summary>房屋大厅里「移动到其他房间」的事件 ID（DailyRoutines 用的同一个）。</summary>
    private const uint RoomSelectEventId = 721074;

    /// <summary>部队工房里「航行管制面板」的事件 ID。</summary>
    private const uint PanelEventId = 3276843;

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

    /// <summary>本次运行里点过多少次「移动到其他房间」。</summary>
    private int roomInteractions;

    /// <summary>「事件表里找不到房间入口」的次数，用来决定要不要退回逐个试门。</summary>
    private int roomEventAttempts;

    /// <summary>是否已经退回到「逐个试门」的兜底模式。</summary>
    private bool useBruteDoors;

    /// <summary>兜底模式下「附近连一个候选门都没有」的连续次数。</summary>
    private int bruteMisses;

    /// <summary>
    /// 每个槽位本轮是否已经处理完（收完报告 + 已派出）。
    /// 必须按槽位记，不能只看「窗口关没关」——那样会在菜单里原地打转。
    /// </summary>
    private readonly bool[] slotDone = new bool[4];

    /// <summary>该槽位是否已经点过「上次的远航报告」（点过一次就算，避免死循环）。</summary>
    private readonly bool[] slotReportTried = new bool[4];

    /// <summary>该槽位是否真的看到并确认过远航报告窗口（= 真的收完了）。</summary>
    private readonly bool[] slotReportSeen = new bool[4];

    /// <summary>该槽位在单艘艇菜单里点过多少次，超上限就跳过这艘。</summary>
    private readonly int[] slotMenuFires = new int[4];

    /// <summary>是否进入「收尾」：只按退出/取消关窗口，不再重新打开任何菜单。</summary>
    private bool finishing;

    /// <summary>收尾阶段按了多少次退出。</summary>
    private int exitClicks;

    /// <summary>面板交互失败后换角度重试的次数。</summary>
    private int panelAttempts;

    /// <summary>确认框上次点击的时间（点完冷却一小会儿，别一帧点十下）。</summary>
    private DateTime yesnoFiredAt = DateTime.MinValue;

    /// <summary>同一个确认框连续点了多少次（防死循环）。</summary>
    private int yesnoSameCount;

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
        this.roomInteractions = 0;
        this.roomEventAttempts = 0;
        this.useBruteDoors = false;
        this.bruteMisses = 0;
        this.doorInteractAt = DateTime.MinValue;
        this.currentDoorId = 0;
        this.lastYesnoText = string.Empty;
        this.yesnoFiredAt = DateTime.MinValue;
        this.finishing = false;
        this.exitClicks = 0;
        this.panelAttempts = 0;
        for (var i = 0; i < 4; i++)
        {
            this.slotDone[i] = false;
            this.slotReportTried[i] = false;
            this.slotReportSeen[i] = false;
            this.slotMenuFires[i] = 0;
        }

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

        // 确认框必须排在最前面、并且不受「动作间隔」限制。
        // 之前它排在 nextActionAt 之后：交互完门要等 3 秒才轮到处理确认框，
        // 于是「要进入这间房屋吗？」摆在那里两三秒才被点，看起来就像反应很慢。
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectYesno))
        {
            // 同一个确认框连点 10 下还没关掉，就别再和它纠缠了，
            // 让状态机继续跑（超时兜底会负责收场）。
            if (this.yesnoSameCount > 10)
                return;

            if ((DateTime.UtcNow - this.yesnoFiredAt).TotalMilliseconds < 200)
                return;

            this.yesnoFiredAt = DateTime.UtcNow;
            this.yesnoSameCount++;
            this.OnSelectYesno();
            return;
        }

        this.yesnoFiredAt = DateTime.MinValue;
        this.yesnoSameCount = 0;

        if (DateTime.UtcNow < this.nextActionAt)
            return;

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
        // 场景切换中三个指针会暂时全是 null，这时候千万别急着传送——
        // 否则「刚点进工房」会被判成「不在房屋里」，又传送回住宅区，来回死循环。
        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.OccupiedInEvent])
        {
            this.StatusText = "等待场景切换…";
            this.SetStage(RunnerStage.Decide, 1);
            return;
        }

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

        // 点完门马上就会弹「要进入这间房屋吗？」。这里只给 1.2 秒的冷却，
        // 剩下的交给 Decide 判场景；再长就会拖慢确认框的响
        this.SetStage(RunnerStage.Decide, 1.2);
    }

    /// <summary>
    /// 房屋大厅 → 部队工房。
    ///
    /// 定位方式和 DailyRoutines 一致：翻游戏的事件表，找 EventId = 721074
    /// （「移动到其他房间」）挂着的那个物件，拿到它的坐标直接瞬移过去，再交互。
    /// 之前是按内置坐标找「附近 8 码内的门」，一旦坐标对不上（换房子、或者用过
    /// /subops learn 把面板坐标记成了房门坐标）就一个候选都没有，于是进屋后
    /// 原地不动、什么也不做——这就是「进房间以后根本没传送」的原因。
    /// </summary>
    private void PickRoom()
    {
        // ① 房间菜单已经弹出：按文字挑「工房」。
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

        // ② 按事件 ID 找「移动到其他房间」，瞬移过去再点开。
        if (this.nav.TryFindEventObject(RoomSelectEventId, out var room))
        {
            this.roomEventAttempts = 0;
            this.useBruteDoors = false;

            if (this.nav.Distance3D(room.Position) > 1.6f)
            {
                this.StatusText = "瞬移到房间切换点…";

                switch (this.nav.ApproachPosition(room.Position, 1.2f))
                {
                    case ApproachState.TooFar:
                        this.Fail("离房间切换点太远，先传送回部队房屋。");
                        return;
                    case ApproachState.Blocked:
                        this.Fail("房间切换点过不去。");
                        return;
                }

                this.Delay(0.35);
                return;
            }

            this.nav.InteractRaw(room.Address);
            this.roomInteractions++;
            this.StatusText = $"点开房间菜单…（第 {this.roomInteractions} 次）";

            if (this.roomInteractions > 6)
            {
                this.Fail("点了 6 次「移动到其他房间」都没弹菜单，可能是界面被别的插件挡住，或者这栋房子没有工房。");
                return;
            }

            this.Delay(1.2);
            return;
        }

        // ③ 事件表里找不到（刚进屋那一帧事件还没注册 / 户型不同 / 事件 ID 变了）：
        //    先等几拍，实在没有就退回「逐个试门」。
        if (!this.useBruteDoors)
        {
            this.roomEventAttempts++;
            if (this.roomEventAttempts <= 6)
            {
                this.StatusText = "等房间入口加载…";
                this.Delay(0.5);
                return;
            }

            this.useBruteDoors = true;
            this.Say("事件表里没有 721074，改成逐个试附近的门。");
        }

        this.BruteForceRoomDoor();
    }

    /// <summary>
    /// 兜底：把附近的可交互物件挨个点一遍。
    /// 点完 3 秒没弹出房间菜单就拉黑换下一个——「离开房屋」那扇门会被排除掉，
    /// 再加上确认框里含「离开」一律点「否」，不会再出现「进屋又自己出去」。
    /// </summary>
    private void BruteForceRoomDoor()
    {
        if (this.doorInteractAt != DateTime.MinValue
            && (DateTime.UtcNow - this.doorInteractAt).TotalSeconds > 3)
        {
            this.doorInteractAt = DateTime.MinValue;

            if (this.currentDoorId != 0 && this.badDoors.Add(this.currentDoorId))
                this.Say($"这扇门没弹房间菜单，已排除（累计 {this.badDoors.Count} 扇）");
            else
                this.doorCursor++;
        }

        var door = this.PickDoor();
        if (door == null)
        {
            if (this.bruteMisses++ < 6)
            {
                this.StatusText = "等房间入口加载…";
                this.Delay(1);
                return;
            }

            this.Fail("找不到「移动到其他房间」的入口。执行 /subops room 把事件表与附近的物件发我。");
            return;
        }

        this.bruteMisses = 0;

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

    /// <summary>按距离列出候选门，跳过已拉黑的。</summary>
    private IGameObject? PickDoor()
    {
        var candidates = this.nav.EventObjectsNear(this.nav.PlayerPosition, 60f)
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
            this.panelAttempts = 0;
            this.SetStage(RunnerStage.Collect, 0);
            return;
        }

        // 和进屋同一个思路：按事件 ID 找面板物件，走到面板前的地面再点开。
        if (this.nav.TryFindEventObject(PanelEventId, out var panel))
        {
            this.lookupRetries = 0;

            if (this.panelAttempts > 7)
            {
                this.Fail("面板点了 8 次都没开。可能站位被家具挡住，先手动站到面板前再 /subops run。");
                return;
            }

            // 站到面板前 2.2 米处（每次失败换个 90° 角度）。
            // 不要直接瞬移到面板坐标：面板的 Y 在柜面高度，人会站到柜台上，
            // 交互反而打不开——这就是「瞬移到柜台里、面板打不开」的原因。
            if (this.nav.ApproachStand(panel.Position, 2.2f, this.panelAttempts) != ApproachState.Arrived)
            {
                this.StatusText = "走到航行管制面板前…";
                this.Delay(0.25);
                return;
            }

            this.nav.InteractRaw(panel.Address);
            this.panelAttempts++;
            this.StatusText = "打开航行管制面板…";
            this.SetStage(RunnerStage.Collect, 1.2);
            return;
        }

        // 兜底：按名字找（"航行管制面板"）。
        var named = this.FindPanel();
        if (named == null)
        {
            if (this.lookupRetries++ < MaxLookupRetries)
            {
                this.StatusText = "等航行管制面板加载…";
                this.Delay(0.5);
                return;
            }

            this.lookupRetries = 0;
            this.Fail("找不到航行管制面板。执行 /subops room 把事件表和附近物件发我。");
            return;
        }

        this.lookupRetries = 0;

        if (this.nav.ApproachStand(named.Position, 2.2f, this.panelAttempts) != ApproachState.Arrived)
        {
            this.StatusText = "走到航行管制面板前…";
            this.Delay(0.25);
            return;
        }

        this.nav.Interact(named);
        this.panelAttempts++;
        this.StatusText = "打开航行管制面板…";
        this.SetStage(RunnerStage.Collect, 1.2);
    }

    /// <summary>
    /// 优先按名字找「航行管制面板」，名字对不上再退回「锚点/脚下附近最近的可交互物件」。
    /// 工房很小，30 码内除了面板没有别的可交互东西。
    /// </summary>
    private IGameObject? FindPanel()
    {
        // 记过锚点、而且锚点就在附近才用；否则一律以脚下为圆心找，
        // 免得旧配置里存了别处的坐标把搜索半径带偏。
        var configured = this.config.HasPanelAnchor ? this.config.PanelAnchor : default;
        var anchor = configured != default && this.nav.Distance3D(configured) <= 50f
            ? configured
            : this.nav.PlayerPosition;

        return this.nav.FindEventObjectByName("航行", anchor, Configuration.PanelSearchRadius)
               ?? this.nav.FindEventObjectByName("管制", anchor, Configuration.PanelSearchRadius)
               ?? this.nav.EventObjectsNear(anchor, Configuration.PanelSearchRadius)
                   .FirstOrDefault();
    }

    /// <summary>
    /// 收艇 / 修理 / 再出发。
    ///
    /// 按「当前开着哪个窗口」来驱动，而不是按「点过什么」来猜：
    ///   远航报告窗口 → 点确认收取（点到了才算真收完）
    ///   出航确认窗口 → 修零件 → 点出发 → 关窗
    ///   菜单窗口     → 面板主菜单 / 单艘艇菜单 / 艇列表 三种分别处理
    ///   什么都没开   → 还有艇要处理就回面板，没了就退出收尾
    ///
    /// 每艘艇的完成状态记在 slotDone[] 里，靠游戏里的返航时间判断「还有没有要处理的」，
    /// 不会出现「刚点开始就以为全收完了」。
    /// </summary>
    private void Collect()
    {
        // ⓪ 收尾阶段：把还开着的菜单一层层关掉，关干净了就结束这一轮。
        if (this.finishing)
        {
            if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
            {
                this.HandleSelectMenu();
                return;
            }

            this.Finish(this.handled > 0 ? $"全部处理完（共 {this.handled} 艘）" : "没有已返航的艇");
            return;
        }

        // ① 远航报告：1 = 确认收取。这是「真的收到」的唯一凭据。
        if (AddonHelper.IsShown(this.gui, AddonHelper.ExplorationResult))
        {
            AddonHelper.Fire(this.gui, AddonHelper.ExplorationResult, 1);

            if (this.currentSlot is >= 0 and < 4)
                this.slotReportSeen[this.currentSlot] = true;

            this.StatusText = this.currentSlot is >= 0 and < 4
                ? $"确认收取 {this.currentSlot + 1} 号艇的远航报告…"
                : "确认收取远航报告…";
            this.Delay(1.2);
            return;
        }

        // ② 出航确认页：先修耐久为 0 的零件，再点「出发」。
        if (AddonHelper.IsShown(this.gui, AddonHelper.ExplorationDetail))
        {
            if (this.config.AutoRepair && this.TryRepairParts())
                return;

            if (!this.config.ReDispatch)
            {
                AddonHelper.Close(this.gui, AddonHelper.ExplorationDetail);
                this.MarkSlotDone(this.currentSlot, "已收取（不重新出航）");
                this.Delay(1.2);
                return;
            }

            AddonHelper.Fire(this.gui, AddonHelper.ExplorationDetail, 0);
            AddonHelper.Close(this.gui, AddonHelper.ExplorationDetail);
            this.MarkSlotDone(
                this.currentSlot,
                this.slotReportSeen[Math.Clamp(this.currentSlot, 0, 3)] ? "已收取并再次出航" : "已出航");
            this.Delay(2.2);
            return;
        }

        // ③ 菜单：面板主菜单 / 单艘艇菜单 / 艇列表。
        if (AddonHelper.IsShown(this.gui, AddonHelper.SelectString))
        {
            this.HandleSelectMenu();
            return;
        }

        // ④ 窗口都关了：没剩下要处理的就收尾，否则回去点面板。
        if (this.NoSlotLeft())
        {
            this.finishing = true;
            this.StatusText = "没有待处理的艇了，关闭面板…";
            this.Delay(0.4);
            return;
        }

        this.SetStage(RunnerStage.GoPanel, 0.6);
    }

    /// <summary>把某个槽位标记为本轮已处理完。</summary>
    private void MarkSlotDone(int slot, string reason)
    {
        this.departFires = 0;
        this.repairAttempts = 0;

        if (slot is >= 0 and < 4 && !this.slotDone[slot])
        {
            this.slotDone[slot] = true;
            this.handled++;
            this.Say($"{slot + 1} 号艇：{reason}（本轮第 {this.handled} 艘）");

            if (this.handled >= this.config.MaxLoops)
            {
                this.Say($"达到轮数上限 {this.config.MaxLoops}，收尾。");
                this.finishing = true;
            }
        }

        this.currentSlot = -1;
        this.StatusText = $"已处理 {this.handled} 艘";
    }

    /// <summary>游戏里还有没有「已返航、且本轮没处理过」的槽位。</summary>
    private bool NoSlotLeft()
    {
        var subs = Workshop.Read();
        if (subs.Count == 0)
            return false; // 数据没读到，别急着收尾

        foreach (var sub in subs)
        {
            if (sub.IsBack && sub.Slot is >= 0 and < 4 && !this.slotDone[sub.Slot])
                return false;
        }

        return true;
    }

    /// <summary>下一个要处理的槽位（已返航 + 本轮没处理过），没有返回 -1。</summary>
    private int NextSlotToDo()
    {
        var subs = Workshop.Read();
        if (subs.Count == 0)
            return -1;

        foreach (var sub in subs)
        {
            if (!sub.IsBack)
                continue;

            if (sub.Slot is >= 0 and < 4 && this.slotDone[sub.Slot])
                continue;

            return sub.Slot;
        }

        return -1;
    }

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
    /// 处理 SelectString。这里有三种完全不同的菜单，靠菜单文字区分：
    /// ① 面板主菜单（「管理潜水艇」…）→ 进列表
    /// ② 单艘艇菜单（出发 / 上次的远航报告 / 更换配件 / 修理配件 / … / 退出）→ 收报告、再出发
    /// ③ 艇列表（4 项，就是 4 艘艇）→ 选一艘要处理的
    ///
    /// 收尾阶段（finishing）只按退出，不再重新打开任何菜单。
    /// </summary>
    private void HandleSelectMenu()
    {
        var entries = AddonHelper.ReadMenuEntries(this.gui);
        if (entries.Count == 0)
        {
            this.Delay(0.25);
            return;
        }

        // ── 收尾：一路按退出，把列表 → 主菜单 → 关闭 逐层关干净 ──
        if (this.finishing)
        {
            var close = AddonHelper.FindMenuEntry(entries, "退出");
            if (close < 0)
                close = AddonHelper.FindMenuEntry(entries, "取消");
            if (close < 0)
                close = entries.Count - 1;

            if (this.exitClicks >= 5)
            {
                this.Finish($"全部处理完（共 {this.handled} 艘）");
                return;
            }

            AddonHelper.Fire(this.gui, AddonHelper.SelectString, close);
            this.exitClicks++;
            this.StatusText = $"关闭面板…（第 {this.exitClicks} 次退出）";
            this.Delay(0.9);
            return;
        }

        // ── ① 面板主菜单 ──
        var manage = AddonHelper.FindMenuEntry(entries, "管理潜水艇");
        if (manage < 0)
            manage = AddonHelper.FindMenuEntry(entries, "管理潛水艇");
        if (manage < 0)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Contains("潜水艇", StringComparison.Ordinal)
                    && entries[i].Contains("管理", StringComparison.Ordinal))
                {
                    manage = i;
                    break;
                }
            }
        }

        if (manage >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, manage);
            this.StatusText = "打开潜水艇列表…";
            this.Delay(1.2);
            return;
        }

        // ── ② 单艘艇菜单 ──
        var depart = AddonHelper.FindMenuEntry(entries, "出发");
        var report = AddonHelper.FindMenuEntry(entries, "远航报告");
        if (depart >= 0 || report >= 0)
        {
            this.HandleVesselMenu(entries, depart, report);
            return;
        }

        // ── ③ 艇列表 ──
        var next = this.NextSlotToDo();
        var subs = Workshop.Read();
        var looksLikeVesselList = entries.Count >= 4
                                  || subs.Any(s => !string.IsNullOrWhiteSpace(s.Name)
                                                   && entries.Any(e => e.Contains(s.Name, StringComparison.Ordinal)));

        if (next >= 0 && looksLikeVesselList)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, next);
            this.currentSlot = next;
            this.repairAttempts = 0;
            this.slotMenuFires[next] = 0;
            this.slotReportTried[next] = false;
            this.slotReportSeen[next] = false;
            this.StatusText = $"处理 {next + 1} 号艇…";
            this.Delay(1.5);
            return;
        }

        // 列表里没有要处理的了：进入收尾，逐层退出。
        this.finishing = true;
        this.StatusText = "没有待处理的艇了，关闭面板…";
        this.Delay(0.3);
    }

    /// <summary>
    /// 单艘艇菜单。顺序必须是「先看报告、再出发」：
    /// 只有真的把远航报告确认掉，才算这艘收完了。
    /// </summary>
    private void HandleVesselMenu(List<string> entries, int depart, int report)
    {
        var slot = this.currentSlot;
        var valid = slot is >= 0 and < 4;

        // 这艘已经处理完、菜单还没关掉的残影：直接退出回列表，绝对不能再点一次出发。
        if (!valid)
        {
            var back = AddonHelper.FindMenuEntry(entries, "退出");
            if (back < 0)
                back = AddonHelper.FindMenuEntry(entries, "取消");
            if (back < 0)
                back = entries.Count - 1;

            if (back >= 0)
                AddonHelper.Fire(this.gui, AddonHelper.SelectString, back);

            this.Delay(0.8);
            return;
        }

        var fires = this.slotMenuFires[slot];

        // ① 报告：点一次「上次的远航报告」。有没有报告，点完就知道——
        //    有报告会弹出远航报告窗口（Collect 里点确认）；
        //    没有报告菜单不会变，1.2 秒后自动转到「出发」。
        if (valid && !this.slotReportTried[slot] && report >= 0)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, report);
            this.slotReportTried[slot] = true;
            this.slotMenuFires[slot]++;
            this.StatusText = $"打开 {slot + 1} 号艇的远航报告…";
            this.Delay(1.2);
            return;
        }

        // ② 出发：打开出航确认页，真正的「出发」在那边点。
        if (depart >= 0 && fires <= 5)
        {
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, depart);
            if (valid)
                this.slotMenuFires[slot]++;

            this.departFires++;
            this.StatusText = $"打开 {slot + 1} 号艇的出航确认…";
            this.Delay(1.2);
            return;
        }

        // ③ 点不动（材料不够 / 艇在航行中）：退出这艘，别把整轮拖死。
        var quit = AddonHelper.FindMenuEntry(entries, "退出");
        if (quit < 0)
            quit = AddonHelper.FindMenuEntry(entries, "取消");
        if (quit >= 0)
            AddonHelper.Fire(this.gui, AddonHelper.SelectString, quit);

        this.Say($"{slot + 1} 号艇点不动「出发」（可能材料不足），跳过。");
        this.MarkSlotDone(slot, "跳过");
        this.Delay(1.0);
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
