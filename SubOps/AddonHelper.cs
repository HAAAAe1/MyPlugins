using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
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
}
