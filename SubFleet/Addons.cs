using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SubFleet;

/// <summary>
/// 窗口（Addon）读写。
///
/// 菜单文字从 <c>AtkValues[i + 7]</c> 读（DailyRoutines 的 AddonSelectStringEvent 就是这么扫的）。
/// 不要读 PopupMenu 里缓存的 EntryNames：那份数据在部分窗口上还停在上一次的内容，
/// 会出现「菜单里明明是『出发』却匹配不到」。
/// </summary>
public static unsafe class Addons
{
    public const string SelectString = "SelectString";
    public const string SelectYesno = "SelectYesno";
    public const string Result = "AirShipExplorationResult";
    public const string Detail = "AirShipExplorationDetail";

    /// <summary>
    /// 「选择航海图」——航行器没有可复用的上次航线时，点出发会弹这个窗口。
    /// 它不在收取流程的主线上，出现就说明这艘需要重新选航线，只能退回菜单。
    /// </summary>
    public const string ChartSelect = "AirShipExploration";

    public const string PartsMenu = "AirShipPartsMenu";

    /// <summary>SelectString 的菜单项文字从这个下标开始。</summary>
    private const int MenuTextOffset = 7;

    private static IGameGui? gui;
    private static IPluginLog? log;

    public static void Initialize(IGameGui gameGui, IPluginLog pluginLog)
    {
        gui = gameGui;
        log = pluginLog;
    }

    /// <summary>取窗口指针，不看可见/就绪状态（恢复窗口时要用）。</summary>
    public static AtkUnitBase* Raw(string name)
    {
        if (gui == null)
            return null;

        var addon = gui.GetAddonByName<AtkUnitBase>(name);
        return addon == null ? null : addon;
    }

    /// <summary>取窗口指针；不可见或未就绪时返回 null。</summary>
    public static AtkUnitBase* Get(string name)
    {
        var addon = Raw(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return null;

        return addon;
    }

    /// <summary>
    /// 窗口连节点都加载完了没有——这一条是照 DailyRoutines 的 <c>IsAddonAndNodesReady()</c> 写的。
    ///
    /// 光看 <c>IsReady</c> 不够：窗口刚弹出时 OnSetup 已经跑过（IsReady = true），
    /// 但 ULD 节点还在加载（<c>UldManager.LoadedState &lt; 3</c>）。这时候发回调，
    /// 游戏会**静默丢弃**——表现就是「确认了但没收取」「点了出发但没出航」。
    /// </summary>
    public static bool NodesReady(string name)
    {
        var addon = Raw(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return false;

        var state = (byte)addon->UldManager.LoadedState;
        return state is 3 or 4;   // 3 = Loaded，4 = LoadError（窗口照样能用）
    }

    /// <summary>把之前被我们藏掉的窗口重新显示出来（游戏那边还以为它开着）。</summary>
    public static bool ForceShow(string name)
    {
        var addon = Raw(name);
        if (addon == null || addon->IsVisible)
            return false;

        addon->IsVisible = true;
        return true;
    }

    /// <summary>只要求可见，不要求 ready（有些窗口初始那一帧 ready 还是 false）。</summary>
    public static bool Shown(string name)
    {
        if (gui == null)
            return false;

        var addon = gui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    public static bool Ready(string name) => Get(name) != null;

    /// <summary>窗口是否存在（不管可不可见）。用来发现「被藏掉、但游戏还认为开着」的窗口。</summary>
    public static bool Exists(string name) => Raw(name) != null;

    /// <summary>
    /// 按下标触发回调（菜单项第几行 / 按钮第几个）。
    ///
    /// **必须等节点加载完再发**：节点没加载完时发出去的回调会被游戏丢掉，
    /// 而调用方看到的只是「窗口没动」。返回 false 就表示这次没发出去，要重试。
    ///
    /// 安全阀：万一某个窗口的 <c>LoadedState</c> 一直不变成 3（个别窗口会这样），
    /// 等 2 秒后按「可见 + IsReady」照发，免得整个流程永远卡在等节点上。
    /// </summary>
    public static bool Fire(string name, int index)
    {
        var addon = Raw(name);
        if (addon == null || !addon->IsVisible)
        {
            blockedSince.Remove(name);
            return false;
        }

        if (!NodesReady(name))
        {
            if (!blockedSince.TryGetValue(name, out var since))
            {
                blockedSince[name] = DateTime.UtcNow;
                return false;
            }

            if ((DateTime.UtcNow - since).TotalSeconds < 2.0 || !addon->IsReady)
                return false;

            blockedSince.Remove(name);
            log?.Warning($"[SubFleet] {name} 的节点状态 2 秒都不是 Loaded，按「可见 + IsReady」照发回调");
            return addon->FireCallbackInt(index);
        }

        blockedSince.Remove(name);
        return addon->FireCallbackInt(index);
    }

    /// <summary>每个窗口「开始等节点」的时刻，只用于上面那个安全阀。</summary>
    private static readonly Dictionary<string, DateTime> blockedSince = new();

    public static void Close(string name)
    {
        var addon = Raw(name);
        if (addon == null)
            return;

        addon->Close(true);
    }

    /// <summary>
    /// 关掉 SelectString 菜单——**必须点「取消」，不能只 Close**。
    ///
    /// 潜水艇列表这类菜单是游戏事件弹出的：只调 <c>Close(true)</c> 把窗口藏起来，
    /// 游戏那边还认为「菜单开着」，下一帧又把窗口摆回来——表现就是
    /// 「潜艇收完了，列表还挂在屏幕上，怎么都不消失」。
    ///
    /// 正确做法和按 ESC 一样：触发「取消」那一项的回调（找不到「取消」就发 -1），
    /// 让游戏自己走「事件结束」的流程。
    /// </summary>
    public static bool CancelMenu()
    {
        var entries = MenuEntries();
        var cancel = FindEntry(entries, "取消", "キャンセル", "Cancel");

        if (cancel >= 0 && Fire(SelectString, cancel))
            return true;

        return Fire(SelectString, -1);
    }

    public static void Hide(string name)
    {
        var addon = gui?.GetAddonByName<AtkUnitBase>(name);
        if (addon != null && addon->IsVisible)
            addon->IsVisible = false;
    }

    /// <summary>把窗口里的字符串值按顺序读出来。</summary>
    public static List<string> Strings(string name)
    {
        var result = new List<string>();
        var addon = Get(name);
        if (addon == null)
            return result;

        foreach (var value in addon->AtkValuesSpan)
        {
            var kind = value.Type & AtkValueType.TypeMask;
            if (kind is not (AtkValueType.String or AtkValueType.ConstString or AtkValueType.WideString))
                continue;

            var text = value.String.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// 读 SelectString 的菜单项文字。
    /// EntryCount 为 0 的窗口（单艘艇菜单）就自己往下数，直到读到空值。
    /// </summary>
    public static List<string> MenuEntries()
    {
        var result = new List<string>();

        var addon = gui?.GetAddonByName<AddonSelectString>(SelectString);
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.AtkValues == null)
            return result;

        var entryCount = Math.Clamp((int)addon->PopupMenu.EntryCount, 0, 32);
        var guess = entryCount == 0;
        var limit = guess ? 24 : entryCount;

        // AtkValues 的实际长度可能小于 EntryCount（窗口刚刷新那一帧），
        // 越界读会读到垃圾字符串甚至崩，所以先夹一下。
        var available = Math.Max(0, addon->AtkUnitBase.AtkValuesCount - MenuTextOffset);
        if (available > 0 && limit > available)
            limit = available;

        var values = addon->AtkUnitBase.AtkValues;

        for (var i = 0; i < limit; i++)
        {
            var value = values[i + MenuTextOffset];
            var kind = value.Type & AtkValueType.TypeMask;
            if (kind == AtkValueType.Null)
            {
                if (guess)
                    break;

                result.Add(string.Empty);
                continue;
            }

            string text;
            try
            {
                text = value.String.ToString();
            }
            catch
            {
                text = string.Empty;
            }

            result.Add(text.Trim());
        }

        return result;
    }

    /// <summary>菜单标题（SelectString 的 AtkValues[2]）。</summary>
    public static string MenuTitle()
    {
        var addon = gui?.GetAddonByName<AddonSelectString>(SelectString);
        if (addon == null || addon->AtkUnitBase.AtkValues == null || addon->AtkUnitBase.AtkValuesCount < 3)
            return string.Empty;

        try
        {
            return addon->AtkUnitBase.AtkValues[2].String.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>按关键字在菜单里找下标，任意一个命中都算；找不到返回 -1。</summary>
    public static int FindEntry(IReadOnlyList<string> entries, params string[] keywords)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var text = entries[i];
            if (string.IsNullOrEmpty(text))
                continue;

            foreach (var keyword in keywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 「是 / 否」确认框。
    ///
    /// 「要进入这间房屋吗？」和「要离开这间房屋吗？」是同一个窗口。
    /// 一律点「是」的话，人在大厅里误交互到出口就会被送出去——以前就是这样
    /// 「刚进屋又自己走了」。含「离开」的一律点「否」。
    /// </summary>
    public static bool Yesno(out string text)
    {
        text = string.Empty;
        var addon = Get(SelectYesno);
        if (addon == null)
            return false;

        var strings = Strings(SelectYesno);
        text = strings.Count > 0 ? strings[0] : string.Empty;

        var isLeave = text.Contains("离开", StringComparison.Ordinal)
                      || text.Contains("離", StringComparison.Ordinal)
                      || (text.Contains("出", StringComparison.Ordinal) && text.Contains("房屋", StringComparison.Ordinal));

        var index = isLeave ? 1 : 0;
        addon->FireCallbackInt(index);

        if (log != null)
            log.Debug($"[SubFleet] Yesno({(isLeave ? "否" : "是")}): {text}");

        return true;
    }
}
