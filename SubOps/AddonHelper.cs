using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SubOps;

/// <summary>
/// 界面（Addon）访问的小工具集。
/// 这里只做三件事：取窗口指针、按下标触发回调、读出窗口里的文本。
/// </summary>
public static unsafe class AddonHelper
{
    public const string SelectString = "SelectString";
    public const string SelectYesno = "SelectYesno";
    public const string ExplorationResult = "AirShipExplorationResult";
    public const string ExplorationDetail = "AirShipExplorationDetail";
    public const string PartsMenu = "AirShipPartsMenu";
    public const string CraftSupply = "CompanyCraftSupply";

    /// <summary>取一个已就绪且可见的窗口指针，否则返回 null。</summary>
    public static AtkUnitBase* Get(IGameGui gui, string name)
    {
        var addon = gui.GetAddonByName<AtkUnitBase>(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            return null;

        return addon;
    }

    public static bool IsOpen(IGameGui gui, string name) => Get(gui, name) != null;

    /// <summary>
    /// 触发窗口回调。菜单类窗口（SelectString）用下标，按钮类窗口用按钮序号。
    /// 这是「点按钮」最稳的方式，不用模拟鼠标。
    /// </summary>
    public static bool Fire(IGameGui gui, string name, int index)
    {
        var addon = Get(gui, name);
        return addon != null && addon->FireCallbackInt(index);
    }

    /// <summary>关闭窗口。</summary>
    public static void Close(IGameGui gui, string name)
    {
        var addon = Get(gui, name);
        addon->Close(true);
    }

    /// <summary>把窗口里的字符串值全读出来（用于调试、或按文本匹配菜单项）。</summary>
    public static List<string> ReadStrings(IGameGui gui, string name)
    {
        var result = new List<string>();
        var addon = Get(gui, name);
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

    /// <summary>按关键字找菜单项下标，找不到返回 -1。</summary>
    public static int FindIndex(IGameGui gui, string name, string keyword)
    {
        var strings = ReadStrings(gui, name);
        for (var i = 0; i < strings.Count; i++)
        {
            if (strings[i].Contains(keyword, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// 读 SelectString 的菜单项文本。
    ///
    /// 菜单文字在 <c>AtkValues[i + 7]</c>（DailyRoutines 的 AddonSelectStringEvent
    /// 就是这么扫的）。不要读 PopupMenu 里的缓存——那个在部分窗口上还是上一次的内容，
    /// 会出现「菜单里明明是『出发』却匹配不到」。
    /// </summary>
    public static List<string> ReadMenuEntries(IGameGui gui)
    {
        var result = new List<string>();
        var addon = gui.GetAddonByName<AddonSelectString>(SelectString);
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->AtkUnitBase.AtkValues == null)
            return result;

        var count = Math.Clamp((int)addon->PopupMenu.EntryCount, 0, 32);

        // 有的菜单（潜水艇的单艘艇菜单）EntryCount 会是 0，这时候自己往下数到空值为止。
        var guess = count == 0;
        if (guess)
            count = 32;

        var values = addon->AtkUnitBase.AtkValues;

        for (var i = 0; i < count; i++)
        {
            var value = values[i + 7];
            var kind = value.Type & AtkValueType.TypeMask;
            if (kind == AtkValueType.Null)
            {
                if (guess)
                    break;

                continue;
            }

            string text;
            try
            {
                text = value.String.ToString();
            }
            catch
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text.Trim());
        }

        return result;
    }

    /// <summary>在菜单项里按关键字找下标（找不到 -1）。</summary>
    public static int FindMenuEntry(List<string> entries, string keyword)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Contains(keyword, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// 用任意个整数触发回调（修理、关闭配件菜单这类需要多参数的操作）。
    /// 对应 ECommons 的 Callback.Fire(addon, true, 3, 0, slot, 0, 0, 0) 写法。
    /// </summary>
    public static bool FireValues(IGameGui gui, string name, params int[] values)
    {
        var addon = Get(gui, name);
        if (addon == null)
            return false;

        var buffer = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            buffer[i].Type = AtkValueType.Int;
            buffer[i].Int = values[i];
        }

        return addon->FireCallback((uint)values.Length, buffer, true);
    }

    /// <summary>
    /// 只判断「窗口是否可见」，不要求 IsReady。
    /// 有些窗口（如旅程详情）初始化的那一帧 ready 还是 false，
    /// 用 IsOpen 会漏判，导致插件以为界面没开、又去点一次面板。
    /// </summary>
    public static bool IsShown(IGameGui gui, string name)
    {
        var addon = gui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    /// <summary>候选名探测：返回其中当前可见的窗口名。用于校准 addon 名字。</summary>
    public static List<string> ProbeVisible(IGameGui gui, params string[] names)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            if (IsShown(gui, name))
                result.Add(name);
        }

        return result;
    }

    /// <summary>潜水艇相关的窗口名候选。国服/国际服、飞空艇/潜水艇共用的几套名字都列上。</summary>
    public static readonly string[] SubmarineAddons =
    {
        "AirShipExplorationResult",
        "AirShipExplorationDetail",
        "AirShipExplorationDetailWindow",
        "AirShipPartsMenu",
        "CompanyCraftSupply",
        "SubmarineExplorationResult",
        "SubmarineExplorationDetail",
        "AirShipExploration",
        "SubmarineExploration",
        "SubmarineSelect",
        "AirShipTimer",
    };
}
