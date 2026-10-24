using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace SubFleet;

public enum RunOutcome
{
    Idle,
    Running,
    Success,
    Failed,
}

/// <summary>
/// 收潜艇。整个流程照 DailyRoutines 的 AutoSubmarineCollect 抄，只是把它的
/// TaskHelper 换成了这里的 <see cref="TaskQueue"/>（同样是「一步做完才做下一步」）。
///
/// DailyRoutines 的顺序：
/// <code>
/// 进屋/进工房 → 开航行管制面板 → 选「管理潜水艇」
///   → 列表里挑一艘已返航的 → 等远航报告（自动确认收取）
///   → 出航确认页：读 4 个零件，耐久 0 的逐个下发修理，修到没有坏件
///   → 点「出发」→ 等 2 秒 → 回到列表
///   → 没有待收的了就退出
/// </code>
///
/// 关键点（也是之前一直出错的地方）：
/// ① 「该收哪艘」只看游戏里的返航时间，不看「点过什么」；
/// ② 「修没修好」只看零件耐久，读不到数据就当没读到（一直等），绝不盲点出发；
/// ③ 已经在面板前就不许再瞬移，避免围着面板转圈。
/// </summary>
public sealed unsafe class Collector
{
    private const uint HouseEntranceDataId = 2002737;
    private const uint RoomSelectEventId = 721074;
    private const uint PanelEventId = 3276843;

    private readonly Config config;
    private readonly Move move;
    private readonly ICondition condition;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private readonly TaskQueue queue = new();

    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime yesnoAt = DateTime.MinValue;
    private DateTime resultAt = DateTime.MinValue;
    private DateTime lastTeleportAt = DateTime.MinValue;
    private string yesnoKey = string.Empty;
    private int yesnoCount;

    private readonly HashSet<int> handled = new();
    private int selectedIndex = -1;
    private string selectedName = string.Empty;
    private bool expectList;

    private int panelTries;
    private int panelMenuFires;
    private int pickFires;
    private int roomFires;
    private DateTime roomFirstFireAt = DateTime.MinValue;

    /// <summary>「什么都没开」连续等了几拍（给刚关掉的窗口一点时间）。</summary>
    private int idleTicks;
    private int doorTries;
    private int approachTries;
    private int repairTicks;
    private int reopenTries;
    private int exitTries;
    private int plotHops;
    private int teleportTries;
    private int departures;

    /// <summary>本轮有没有真的看到过潜艇列表。没看到过就不能断言「没有待收的」。</summary>
    private bool listSeen;

    /// <summary>潜艇数据还没加载时等了几拍（别一读不到就收工）。</summary>
    private int dataWaits;

    /// <summary>这个时间之前不许瞬移：刚进屋/刚进工房时区域还在读，瞬移会被判「正在加载区域」。</summary>
    private DateTime moveGateUntil = DateTime.MinValue;

    public Collector(Config config, Move move, ICondition condition, IChatGui chat, IPluginLog log)
    {
        this.config = config;
        this.move = move;
        this.condition = condition;
        this.chat = chat;
        this.log = log;
    }

    public RunOutcome Outcome { get; private set; } = RunOutcome.Idle;

    public string Status { get; private set; } = "待机";

    public string FailReason { get; private set; } = string.Empty;

    public string Summary { get; private set; } = string.Empty;

    public bool Running => this.Outcome == RunOutcome.Running;

    public string StepText => this.queue.Running ? $"{this.queue.Progress} {this.queue.CurrentStep}" : string.Empty;

    public int HandledCount => this.handled.Count;

    // ───────────────────────── 控制 ─────────────────────────

    public bool Start()
    {
        if (this.Running)
            return false;

        this.handled.Clear();
        this.selectedIndex = -1;
        this.selectedName = string.Empty;
        this.expectList = false;
        this.panelTries = 0;
        this.panelMenuFires = 0;
        this.pickFires = 0;
        this.roomFires = 0;
        this.roomFirstFireAt = DateTime.MinValue;
        this.idleTicks = 0;
        this.doorTries = 0;
        this.approachTries = 0;
        this.repairTicks = 0;
        this.reopenTries = 0;
        this.exitTries = 0;
        this.plotHops = 0;
        this.teleportTries = 0;
        this.departures = 0;
        this.listSeen = false;
        this.dataWaits = 0;
        this.moveGateUntil = DateTime.MinValue;
        this.nextActionAt = DateTime.MinValue;
        this.Outcome = RunOutcome.Running;
        this.FailReason = string.Empty;
        this.Summary = string.Empty;

        if (!this.move.HasVnavmesh)
            this.Say("提示：没检测到 vnavmesh，瞬移时只能沿用当前高度，遇到台阶请手动走一下。");

        // 背包还没加载时两个数都是 0，别拿它当「没材料」吓人。
        var (ceruleum, material) = Workshop.Materials();
        if (ceruleum > 0 || material > 0)
        {
            if (this.config.ReDispatch && ceruleum < 15)
                this.Say($"桶装青磷水只剩 {ceruleum} 个（一艇一趟要 15 个），不足的艇会出不了港。");

            if (this.config.AutoRepair && material < 20)
                this.Say($"魔导机械修理材料只剩 {material} 个，零件坏了可能修不上。");
        }

        this.queue.Reset(900);
        this.queue.Add("等待场景就绪", this.StepWaitFree, 180);
        this.queue.Add("前往部队工房", this.StepGotoWorkshop, 360);
        this.queue.Add("打开航行管制面板", this.StepOpenPanel, 120);
        this.queue.Add("收取潜水艇", this.StepCollect, 600);
        this.queue.Add("退出工房", this.StepExit, 60);
        this.queue.Start();

        this.Status = "开始";
        return true;
    }

    public void Stop()
    {
        if (this.Running)
            this.Say("已手动停止");

        // 不管是手动停还是切角色前停，都顺手把潜艇列表关掉：
        // 之前切角色时窗户还挂在屏幕上，切完留在那儿，看着就像「收完不取消」。
        this.CloseList();

        this.queue.Reset();
        this.Outcome = RunOutcome.Idle;
        this.Status = "已停止";
    }

    public void Tick()
    {
        if (!this.Running)
            return;

        var now = DateTime.UtcNow;

        // 确认框和远航报告优先级最高，不受各步自己的节奏限制。
        if (this.HandleYesno(now))
            return;

        if (this.HandleResult(now))
            return;

        if (now < this.nextActionAt)
            return;

        this.queue.Tick();

        if (this.queue.Failed)
        {
            this.Fail(this.queue.Failure);
            return;
        }

        if (!this.queue.Running)
            this.Succeed();
    }

    // ───────────────────────── 全局窗口处理 ─────────────────────────

    private bool HandleYesno(DateTime now)
    {
        if (!Addons.Shown(Addons.SelectYesno))
        {
            this.yesnoKey = string.Empty;
            this.yesnoCount = 0;
            return false;
        }

        if ((now - this.yesnoAt).TotalMilliseconds < 250)
            return true;

        this.yesnoAt = now;

        var strings = Addons.Strings(Addons.SelectYesno);
        var text = strings.Count > 0 ? strings[0] : string.Empty;

        if (text != this.yesnoKey)
        {
            this.yesnoKey = text;
            this.yesnoCount = 0;
            this.Say("确认框：" + text);
        }

        this.yesnoCount++;
        if (this.yesnoCount > 8)
        {
            this.log.Warning($"[SubFleet] 确认框点了 8 次还在：{text}");
            Addons.Close(Addons.SelectYesno);
            return true;
        }

        Addons.Yesno(out _);
        return true;
    }

    /// <summary>远航报告：点「确认」（callback 1）把奖励收下，等于「真的收完了」。</summary>
    private bool HandleResult(DateTime now)
    {
        if (!Addons.Shown(Addons.Result))
            return false;

        if ((now - this.resultAt).TotalMilliseconds < 300)
            return true;

        this.resultAt = now;

        if (Addons.Get(Addons.Result) == null)
            return true;

        Addons.Fire(Addons.Result, 1);
        Addons.Hide(Addons.Result);
        this.pickFires = 0;
        this.Say("确认远航报告");
        return true;
    }

    // ───────────────────────── 第一步：到工房 ─────────────────────────

    private bool StepWaitFree()
    {
        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.OccupiedInCutSceneEvent]
            || this.condition[ConditionFlag.WatchingCutscene])
        {
            this.SetStatus("等待场景切换…");
            this.Wait(0.4);
            return false;
        }

        if (this.move.Player == null)
        {
            this.SetStatus("等待角色加载…");
            this.Wait(0.4);
            return false;
        }

        return true;
    }

    private bool StepGotoWorkshop()
    {
        if (Workshop.InWorkshop)
            return true;

        if (Workshop.InHouse)
            return this.StepRoomSelect();

        if (Workshop.Outdoors)
            return this.StepHouseEntrance();

        return this.StepTeleportToEstate();
    }

    private bool StepTeleportToEstate()
    {
        if (!Housing.TryGetFcEstate(out var estate))
        {
            this.Fail("这个角色没有部队房屋（或读不到部队房屋信息）");
            return false;
        }

        if (this.move.Territory == estate.TerritoryId)
        {
            // 已经在住宅区里了，但房屋指针还没切过来：等一拍。
            this.SetStatus("已在住宅区，等待房屋数据…");
            this.Wait(0.5);
            return false;
        }

        if ((DateTime.UtcNow - this.lastTeleportAt).TotalSeconds < 5)
        {
            this.SetStatus("传送中…");
            return false;
        }

        this.lastTeleportAt = DateTime.UtcNow;

        if (this.teleportTries++ >= 6)
        {
            this.Fail("传送到部队房屋失败（住宅区以太之光没调谐？）");
            return false;
        }

        if (!Housing.TeleportToEstateZone(estate.TerritoryId))
        {
            this.SetStatus("传送失败，重试…");
            this.Wait(1.5);
            return false;
        }

        this.SetStatus("传送到住宅区…");
        this.Wait(1.2);
        return false;
    }

    private bool StepHouseEntrance()
    {
        var hasEstate = Housing.TryGetFcEstate(out var estate);

        // 房屋入口的 DataId 全住宅区共用，按「离自家地块最近」挑，别跑去点别人家的门。
        var door = hasEstate && estate.HasPlotPosition
            ? this.move.NearestByBaseIdTo(HouseEntranceDataId, estate.PlotPosition, 120f)
            : this.move.NearestByBaseId(HouseEntranceDataId, 120f);

        if (door == null)
        {
            // 以太之光离地块可能上百米，先按地块坐标跳过去
            if (hasEstate && estate.HasPlotPosition && this.plotHops++ < 6)
            {
                this.SetStatus("前往部队房屋…");
                this.move.Teleport(estate.PlotPosition);
                this.Wait(0.8);
                return false;
            }

            this.SetStatus("等房屋入口出现…");
            this.Wait(0.6);
            return false;
        }

        this.plotHops = 0;

        if (!this.move.Approach(door.Position, 2.0f, Math.Min(this.approachTries / 3, 3)))
        {
            this.approachTries++;
            this.SetStatus("靠近房屋入口…");
            this.Wait(0.25);
            return false;
        }

        this.approachTries = 0;

        if (this.doorTries++ > 12)
        {
            this.Fail("点了 12 次房屋入口都没进去");
            return false;
        }

        this.move.Interact(door);
        this.SetStatus("进入房屋…");

        // 进屋之后客户端还要读室内场景。这时候马上瞬移，系统会判「正在加载区域」
        // （人卡在原地、提示刷屏）。给一段静默期：至少 2 秒，且读图标志消失后再多等 0.5 秒。
        this.moveGateUntil = DateTime.UtcNow.AddSeconds(2.0);
        this.Wait(1.6);
        return false;
    }

    /// <summary>
    /// 区域是否加载完、可以瞬移了。
    ///
    /// 只要还在 BetweenAreas，就把静默期往后推；读图结束后再多等 0.5 秒，
    /// 避免「刚进图那一帧」瞬移被判成「正在加载区域」。
    /// </summary>
    private bool GateZoneLoaded()
    {
        if (this.condition[ConditionFlag.BetweenAreas])
        {
            this.moveGateUntil = DateTime.UtcNow.AddSeconds(0.5);
            this.SetStatus("等区域加载…");
            this.Wait(0.3);
            return false;
        }

        if (DateTime.UtcNow < this.moveGateUntil)
        {
            this.SetStatus("等区域加载…");
            this.Wait(0.25);
            return false;
        }

        return true;
    }

    private bool StepRoomSelect()
    {
        // 房间菜单已经弹出：按文字挑「工房」
        if (Addons.Shown(Addons.SelectString))
        {
            // 已经点过了就等窗口自己关，别再点第二次（会重复进房间／把菜单顶回来）；
            // 但等了 6 秒还挂着，说明这一下没点上，允许再点。
            if (this.roomFires >= 3)
            {
                if ((DateTime.UtcNow - this.roomFirstFireAt).TotalSeconds > 6)
                {
                    this.roomFires = 0;
                }
                else
                {
                    this.SetStatus("进入工房…");
                    this.Wait(0.5);
                    return false;
                }
            }

            var entries = Addons.MenuEntries();
            if (entries.Count == 0)
            {
                this.Wait(0.3);
                return false;
            }

            var index = Addons.FindEntry(entries, "潜水艇工房", "工房", "Workshop", "Submarine");
            if (index < 0)
                index = Math.Clamp(this.config.RoomMenuIndex, 0, entries.Count - 1);

            if (this.roomFires == 0)
                this.roomFirstFireAt = DateTime.UtcNow;

            this.roomFires++;
            Addons.Fire(Addons.SelectString, index);
            this.SetStatus($"进入工房（第 {index + 1} 项）");

            // 进工房又是一次读图，同样先别瞬移。
            this.moveGateUntil = DateTime.UtcNow.AddSeconds(2.0);
            this.Wait(1.6);
            return false;
        }

        if (!this.GateZoneLoaded())
            return false;

        if (!this.move.TryFindEvent(RoomSelectEventId, out var position, out var address))
        {
            this.SetStatus("等房间入口加载…");
            this.Wait(0.5);
            return false;
        }

        if (!this.move.Approach(position, 1.8f, Math.Min(this.approachTries / 3, 3)))
        {
            this.approachTries++;
            this.SetStatus("靠近房间入口…");
            this.Wait(0.25);
            return false;
        }

        this.approachTries = 0;
        this.move.InteractRaw(address);
        this.SetStatus("打开房间列表…");
        this.Wait(1.2);
        return false;
    }

    // ───────────────────────── 第二步：开面板 ─────────────────────────

    private bool StepOpenPanel()
    {
        if (Addons.Shown(Addons.SelectString))
            return true;

        if (!this.GateZoneLoaded())
            return false;

        if (!this.move.TryFindEvent(PanelEventId, out var position, out var address))
        {
            this.SetStatus("等航行管制面板加载…");
            this.Wait(0.5);
            return false;
        }

        if (!this.move.Approach(position, 2.2f, Math.Min(this.approachTries / 3, 3)))
        {
            this.approachTries++;
            this.SetStatus("靠近航行管制面板…");
            this.Wait(0.25);
            return false;
        }

        this.approachTries = 0;

        if (this.panelTries++ > 12)
        {
            this.Fail("点了 12 次航行管制面板都没弹出菜单");
            return false;
        }

        this.move.InteractRaw(address);
        this.expectList = false;
        this.SetStatus($"打开航行管制面板…（第 {this.panelTries} 次）");
        this.Wait(1.2);
        return false;
    }

    // ───────────────────────── 第三步：收 ─────────────────────────

    private bool StepCollect()
    {
        // 出航确认页
        if (Addons.Shown(Addons.Detail))
            return this.HandleDetail();

        // 菜单
        if (Addons.Shown(Addons.SelectString))
            return this.HandleMenu();

        // 什么都没开：还有待收的就重新开面板，没有就收工
        //
        // 注意：刚进屋那一两帧潜艇数据可能还没加载（4 个槽位都是 Rank 0），
        // 这时候不能当成「没有待收的」。必须真的见过列表，且数据确实读到了，才允许收工。
        if (this.listSeen)
        {
            var pending = this.NextPending(out var diagnostic);

            if (pending != PendingUnknown)
                this.dataWaits = 0;

            if (pending == PendingDone)
            {
                this.SetStatus("没有待收取的潜艇");
                this.Say($"本轮处理 {this.handled.Count} 艘，再次出航 {this.departures} 艘");
                return true;
            }

            if (pending == PendingUnknown)
            {
                // 读不到 ≠ 没有。等几拍再说，别一读不到就整轮收工。
                if (this.dataWaits++ < 24)
                {
                    this.SetStatus($"读潜艇数据…（{diagnostic}）");
                    this.Wait(0.25);
                    return false;
                }

                this.dataWaits = 0;
                this.log.Warning($"[SubFleet] 6 秒读不到潜艇数据（{diagnostic}），重新开面板再读");
            }
        }

        // 刚点完「出发」那一两秒，列表可能还在回来的路上。先等两秒再重新开面板，
        // 否则会抢在列表出现前把面板又点一遍，把刚回来的列表关掉。
        if (this.idleTicks++ < 4)
        {
            this.SetStatus("等界面…");
            this.Wait(0.5);
            return false;
        }

        if (this.reopenTries++ > 12)
        {
            this.Fail("面板反复关闭，收不下去");
            return false;
        }

        if (!this.move.TryFindEvent(PanelEventId, out var position, out var address))
        {
            this.SetStatus("等航行管制面板…");
            this.Wait(0.5);
            return false;
        }

        if (!this.move.Approach(position, 2.2f, Math.Min(this.approachTries / 3, 3)))
        {
            this.approachTries++;
            this.SetStatus("靠近航行管制面板…");
            this.Wait(0.25);
            return false;
        }

        this.approachTries = 0;

        this.move.InteractRaw(address);
        this.expectList = false;   // 面板重新打开后又是主菜单，别把主菜单当成潜艇列表
        this.SetStatus("重新打开面板…");
        this.Wait(1.2);
        return false;
    }

    private bool HandleMenu()
    {
        this.idleTicks = 0;
        var entries = Addons.MenuEntries();
        if (entries.Count == 0)
        {
            this.Wait(0.3);
            return false;
        }

        var looksLikePanelMenu = Addons.FindEntry(entries, "管理潜水艇") >= 0;
        if (this.IsSubmarineList(entries) && !looksLikePanelMenu)
        {
            this.listSeen = true;

            var pending = this.NextPending(out var diagnostic);
            var entry = -1;
            var name = string.Empty;

            if (pending != PendingUnknown)
                this.dataWaits = 0;

            if (pending == PendingUnknown)
            {
                // 潜艇数据还没填进来：等。等不到就退回「看菜单文字」——
                // 还没回来的艇，菜单那行一定带「正在探索 / 剩余时间」。
                if (this.dataWaits++ < 24)
                {
                    this.SetStatus($"读潜艇数据…（{diagnostic}）");
                    this.Wait(0.25);
                    return false;
                }

                this.dataWaits = 0;
                this.log.Warning($"[SubFleet] 6 秒读不到潜艇数据（{diagnostic}），改用菜单文字判断");
                this.Say($"读不到潜艇数据（{diagnostic}），改看菜单文字");

                entry = this.PickByMenuText(entries);
                if (entry < 0)
                {
                    this.SetStatus("没有待收取的潜艇");
                    this.Say("菜单里没有可收的（全部还在探索），收工");
                    this.CloseList();
                    return true;
                }

                // 槽位先按「第几项」算，到出航页再用游戏内下标纠正
                pending = entry;
                name = EntryName(entries[entry]);
            }
            else if (pending == PendingDone)
            {
                this.SetStatus("没有待收取的潜艇");
                this.Say($"本轮处理 {this.handled.Count} 艘，再次出航 {this.departures} 艘");

                // 收完必须点「取消」把列表关掉。
                // 只调 Close(true) 的话，游戏那边还认为菜单开着，下一帧又摆回来——
                // 这就是「潜艇收完了，列表还挂在屏幕上不消失」。
                this.CloseList();
                return true;
            }
            else
            {
                name = this.SlotName(pending);
                for (var i = 0; i < entries.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(name) && entries[i].Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        entry = i;
                        break;
                    }
                }

                if (entry < 0)
                    entry = Math.Clamp(pending, 0, entries.Count - 1);
            }

            this.selectedIndex = pending;
            this.selectedName = name;
            this.expectList = true;

            if (this.pickFires++ > 6)
            {
                this.Fail($"点了 6 次「{name}」都没弹出远航报告，菜单项：{string.Join(" | ", entries)}");
                return false;
            }

            Addons.Fire(Addons.SelectString, entry);
            this.SetStatus($"选择 {name}（第 {entry + 1} 项）");
            this.Say($"选择 {name}");
            this.Wait(1.6);
            return false;
        }

        // 面板主菜单 → 「管理潜水艇」
        if (this.panelMenuFires++ > 8)
        {
            this.Fail($"点了 8 次「管理潜水艇」都没进潜艇列表，菜单项：{string.Join(" | ", entries)}");
            return false;
        }

        var index = Addons.FindEntry(entries, "管理潜水艇", "潜水艇", "Submarine", "Manage");
        if (index < 0)
            index = Math.Clamp(this.config.PanelMenuIndex, 0, entries.Count - 1);

        if (this.panelMenuFires == 1)
            this.log.Information($"[SubFleet] 面板菜单：{string.Join(" | ", entries)} → 选第 {index + 1} 项");

        Addons.Fire(Addons.SelectString, index);
        this.expectList = true;
        this.SetStatus($"进入潜艇列表（{entries[index]}）");
        this.Wait(1.2);
        return false;
    }

    private bool IsSubmarineList(IReadOnlyList<string> entries)
    {
        if (this.expectList)
            return true;

        // 菜单里出现潜艇名 → 一定是列表
        var subs = Workshop.Read();
        foreach (var sub in subs)
        {
            if (!sub.Enabled || string.IsNullOrWhiteSpace(sub.Name))
                continue;

            if (entries.Any(x => x.Contains(sub.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        var title = Addons.MenuTitle();
        if (string.IsNullOrEmpty(title))
            return false;

        // 潜艇列表的标题是「请选择潜水艇。」（后面跟着机体数/燃料数那几行）
        return title.Contains("潜水艇", StringComparison.Ordinal)
               || title.Contains("潛水艇", StringComparison.Ordinal)
               || title.Contains("Submarine", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>出航确认页：先修再出发。返回 true 只在「没有潜艇可处理了」时才用不到，这里固定 false。</summary>
    private bool HandleDetail()
    {
        if (!Addons.Ready(Addons.Detail))
        {
            this.Wait(0.2);
            return false;
        }

        this.pickFires = 0;

        var index = this.ResolveIndex();
        if (index < 0)
        {
            this.Fail("拿不到当前潜艇下标（特征码没扫到，菜单里也没选中）");
            return false;
        }

        var gameIndex = Workshop.CurrentIndex;
        if (gameIndex >= 0 && this.selectedIndex >= 0 && gameIndex != this.selectedIndex)
            this.log.Warning($"[SubFleet] 下标不一致：菜单选的是 {this.selectedIndex}，游戏内是 {gameIndex}，按游戏内的来");

        if (this.config.AutoRepair)
        {
            var state = this.EnsureRepaired(index);
            if (state != RepairState.Ready)
                return false;
        }

        if (!this.config.ReDispatch)
        {
            Addons.Fire(Addons.Detail, -1);
            Addons.Close(Addons.Detail);
            this.handled.Add(index);
            this.Say($"{this.SlotName(index)} 已收取（不再次出航）");
            this.Wait(1.5);
            return false;
        }

        Addons.Fire(Addons.Detail, 0);
        Addons.Close(Addons.Detail);
        this.handled.Add(index);
        this.departures++;
        this.reopenTries = 0;
        this.Say($"{this.SlotName(index)} 已收取并再次出航");
        this.Wait(2.0);
        return false;
    }

    /// <summary>
    /// 修到没有坏件为止。返回 Ready 才允许点「出发」。
    ///
    /// DailyRoutines 的做法：读 4 个零件 → 耐久为 0 的全部下发 RepairSubmarinePart(1153)
    /// → 下一拍重新读 → 还是 0 就继续下发，直到一个坏件都没有。
    ///
    /// 读不到零件数据时一直等；等太久（15 秒）就**跳过这艘、绝不盲点出发**——
    /// 数据读不到还要点出发，等于赌它没坏，赌输就是「点了出发却出不去」。
    /// </summary>
    private RepairState EnsureRepaired(int index)
    {
        if (!Workshop.TryReadDamagedParts(index, out var damaged, out var diagnostic))
        {
            this.repairTicks++;
            this.SetStatus($"读零件…（{diagnostic}）");

            if (this.repairTicks == 1 || this.repairTicks % 20 == 0)
                this.log.Warning($"[SubFleet] {index + 1} 号艇读不到零件：{diagnostic}");

            if (this.repairTicks < 60)
            {
                this.Wait(0.25);
                return RepairState.Waiting;
            }

            this.Say($"{this.SlotName(index)}：读不到零件耐久（{diagnostic}），这艘不出发。");
            this.log.Warning($"[SubFleet] {index + 1} 号艇 15 秒读不到零件，跳过该艇");
            Addons.Fire(Addons.Detail, -1);
            Addons.Close(Addons.Detail);
            this.handled.Add(index);
            this.repairTicks = 0;
            this.Wait(1.5);
            return RepairState.Skipped;
        }

        if (damaged.Count == 0)
        {
            if (this.repairTicks > 0)
                this.Say($"{this.SlotName(index)} 耐久已确认，出发");

            this.repairTicks = 0;
            return RepairState.Ready;
        }

        var sent = 0;
        foreach (var part in damaged)
        {
            if (Workshop.RepairPart(index, part))
                sent++;
        }

        this.repairTicks++;
        this.SetStatus($"修理 {index + 1} 号艇（坏 {damaged.Count} 个，下发 {sent} 条，第 {this.repairTicks} 次）");

        if (this.repairTicks == 1 || this.repairTicks % 10 == 0)
        {
            this.Say($"{this.SlotName(index)} 有 {damaged.Count} 个零件耐久为 0，下发 {sent} 条修理命令（第 {this.repairTicks} 次）");
            this.log.Information($"[SubFleet] 修理 {index + 1} 号艇：坏件 [{string.Join(",", damaged)}]，下发 {sent}（{diagnostic}）");
        }

        if (this.repairTicks > 120)
        {
            this.Say($"{this.SlotName(index)} 修了 30 秒还是坏的，这艘先跳过。");
            Addons.Fire(Addons.Detail, -1);
            Addons.Close(Addons.Detail);
            this.handled.Add(index);
            this.repairTicks = 0;
            this.Wait(1.5);
            return RepairState.Skipped;
        }

        this.Wait(0.2);
        return RepairState.Waiting;
    }

    private enum RepairState
    {
        Waiting,
        Ready,
        Skipped,
    }

    // ───────────────────────── 第四步：退出 ─────────────────────────

    private bool StepExit()
    {
        if (Addons.Shown(Addons.Detail))
        {
            Addons.Close(Addons.Detail);
            this.Wait(0.8);
            return false;
        }

        if (Addons.Shown(Addons.Result))
        {
            Addons.Fire(Addons.Result, 1);
            this.Wait(0.6);
            return false;
        }

        if (Addons.Shown(Addons.SelectString))
        {
            if (this.exitTries >= 10)
            {
                // 连「取消」都点不掉：最后手段直接隐藏。整轮其实已经收完了，
                // 不能因为这个判失败、把角色打进 30 分钟冷却。
                this.log.Warning("[SubFleet] 点了 10 次取消都没关掉潜艇列表，改为直接隐藏");
                Addons.Hide(Addons.SelectString);
                this.Wait(0.5);
                this.expectList = false;
                return true;
            }

            if (this.exitTries == 0 || this.exitTries == 5)
                this.log.Information($"[SubFleet] 关闭列表（第 {this.exitTries + 1} 次）：{string.Join(" | ", Addons.MenuEntries())}");

            this.exitTries++;
            this.SetStatus("关闭潜艇列表…");
            this.CloseList();
            this.Wait(0.7);
            return false;
        }

        this.expectList = false;
        return true;
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private int ResolveIndex()
    {
        var index = Workshop.CurrentIndex;
        if (index is >= 0 and <= 3)
            return index;

        return this.selectedIndex;
    }

    /// <summary>该收哪一艘。-1 ＝ 都收完了，-2 ＝ 潜艇数据读不到（还没加载）。</summary>
    private const int PendingDone = -1;
    private const int PendingUnknown = -2;

    private int NextPending(out string diagnostic)
    {
        if (!Workshop.TryRead(out var subs, out diagnostic))
            return PendingUnknown;

        var now = Workshop.Now;
        foreach (var sub in subs)
        {
            if (!sub.Enabled)
                continue;

            if (this.handled.Contains(sub.Slot))
                continue;

            if (!sub.IsReady(now))
                continue;

            return sub.Slot;
        }

        return PendingDone;
    }

    /// <summary>
    /// 数据读不到时的兜底：按菜单文字挑一行。
    /// 还在外面的艇，菜单里一定带「正在探索」和剩余时间；已返航的不会带。
    /// </summary>
    private int PickByMenuText(IReadOnlyList<string> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var text = entries[i];
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (text.Contains("取消", StringComparison.Ordinal)
                || text.Contains("キャンセル", StringComparison.Ordinal)
                || text.Contains("返回", StringComparison.Ordinal)
                || text.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
                continue;

            if (text.Contains("正在探索", StringComparison.Ordinal)
                || text.Contains("探索中", StringComparison.Ordinal)
                || text.Contains("剩余时间", StringComparison.Ordinal)
                || text.Contains("剩餘時間", StringComparison.Ordinal))
                continue;

            return i;
        }

        return -1;
    }

    /// <summary>「泡芙一号 [99级] [正在探索: …]」→「泡芙一号」。</summary>
    private static string EntryName(string entry)
    {
        var cut = entry.IndexOf(" [", StringComparison.Ordinal);
        return cut > 0 ? entry[..cut].Trim() : entry.Trim();
    }

    /// <summary>点「取消」把潜艇列表关掉（不能只 Close，游戏会把窗口摆回来）。</summary>
    private void CloseList()
    {
        if (!Addons.Shown(Addons.SelectString))
            return;

        if (Addons.CancelMenu())
            this.Say("关闭潜艇列表（取消）");
        else
            Addons.Close(Addons.SelectString);
    }

    private string SlotName(int index)
    {
        if (!string.IsNullOrWhiteSpace(this.selectedName) && index == this.selectedIndex)
            return this.selectedName;

        var sub = Workshop.Read().FirstOrDefault(x => x.Slot == index);
        return string.IsNullOrWhiteSpace(sub.Name) ? $"{index + 1} 号艇" : sub.Name;
    }

    private void Succeed()
    {
        this.Outcome = RunOutcome.Success;
        this.Summary = $"收取 {this.handled.Count} 艘，再次出航 {this.departures} 艘";
        this.Status = "完成：" + this.Summary;
        this.Say("完成：" + this.Summary);
    }

    private void Fail(string reason)
    {
        this.queue.Reset();
        this.Outcome = RunOutcome.Failed;
        this.FailReason = reason;
        this.Status = "失败：" + reason;
        this.Say("失败：" + reason);
        this.log.Warning("[SubFleet] " + reason);
    }

    private void Wait(double seconds) => this.nextActionAt = DateTime.UtcNow.AddSeconds(seconds);

    private void SetStatus(string text) => this.Status = text;

    private void Say(string message)
    {
        if (this.config.VerboseChat)
            this.chat.Print($"[SubFleet] {message}");

        this.log.Information("[SubFleet] " + message);
    }
}
