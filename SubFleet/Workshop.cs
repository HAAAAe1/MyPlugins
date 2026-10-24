using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace SubFleet;

/// <summary>
/// 潜水艇数据与修理。全部照 DailyRoutines 的读法，不自己猜结构。
/// </summary>
public static unsafe class Workshop
{
    // ───────────────────────── 位置 ─────────────────────────

    public static bool InWorkshop
    {
        get
        {
            var manager = HousingManager.Instance();
            if (manager == null)
                return false;

            try
            {
                // 游戏自己的判定最准：只有真的站在工房里才是 true。
                return manager->IsInWorkshop();
            }
            catch
            {
                // 特征码没解析出来时才退回指针判断
                return manager->WorkshopTerritory != null;
            }
        }
    }

    public static bool InHouse
    {
        get
        {
            var manager = HousingManager.Instance();
            if (manager == null || InWorkshop)
                return false;

            return manager->IndoorTerritory != null;
        }
    }

    public static bool Outdoors
    {
        get
        {
            var manager = HousingManager.Instance();
            return manager != null && manager->OutdoorTerritory != null;
        }
    }

    // ───────────────────────── 潜艇状态 ─────────────────────────

    /// <summary>一个槽位。Rank == 0 表示这个位置没有潜艇。</summary>
    public readonly record struct Vessel(int Slot, byte Rank, string Name, DateTime ReturnTime)
    {
        public bool Enabled => this.Rank != 0;

        /// <summary>已返航 / 从未派出，可以收取或再次派遣。</summary>
        public bool IsReady(DateTime utcNow) => this.Enabled && this.ReturnTime <= utcNow.AddSeconds(2);
    }

    /// <summary>读 4 个槽位。不在工房或者数据还没加载时返回空列表。</summary>
    public static List<Vessel> Read()
    {
        var result = new List<Vessel>();

        var manager = HousingManager.Instance();
        if (manager == null)
            return result;

        var territory = manager->WorkshopTerritory;
        if (territory == null)
            return result;

        try
        {
            var data = territory->Submersible.Data;
            for (var i = 0; i < data.Length; i++)
            {
                var sub = data[i];
                string name;
                try
                {
                    name = sub.NameString;
                }
                catch
                {
                    name = $"#{i + 1}";
                }

                result.Add(new Vessel(i, sub.RankId, name, sub.GetReturnTime()));
            }
        }
        catch
        {
            // 刚进屋那一两帧指针还没切过来，当成没读到
            result.Clear();
        }

        return result;
    }

    /// <summary>
    /// 服务器时间（和返航时间同一时基）。
    /// DailyRoutines 用的是新版 CS 的 StandardTimeManager，这个版本的 FFXIVClientStructs 里没有，
    /// 换成 Framework.GetServerTime()（同样是服务器 UTC 秒）。
    /// </summary>
    public static DateTime Now
    {
        get
        {
            try
            {
                var seconds = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.GetServerTime();
                if (seconds > 0)
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            catch
            {
                // 读不到就用本机时间
            }

            return DateTime.UtcNow;
        }
    }

    // ───────────────────────── 零件与修理 ─────────────────────────

    /// <summary>零件容器。DailyRoutines 用的是这一个，每艘艇占 5 格。</summary>
    public static InventoryType PartsContainer => InventoryType.HousingInteriorPlacedItems2;

    private const int PartsPerSubmarine = 5;
    private const int PartSlotCount = 4;

    /// <summary>游戏命令：修理潜水艇零件（ExecuteCommandFlag.RepairSubmarinePart）。</summary>
    private const int RepairSubmarinePartCommand = 1153;

    /// <summary>桶装青磷水（出航消耗）与魔导机械修理材料（修理消耗）。</summary>
    public const uint CeruleumTank = 10155;
    public const uint RepairMaterial = 10373;

    public static (int Ceruleum, int Material) Materials()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return (0, 0);

        return (
            manager->GetInventoryItemCount(CeruleumTank, false, true, true, 0),
            manager->GetInventoryItemCount(RepairMaterial, false, true, true, 0));
    }

    /// <summary>
    /// 读某艘艇的 4 个零件，返回**耐久为 0 的零件下标**。
    /// 读不到数据时返回 false——这一点必须和「没有坏件」区分开：
    /// 以前两者都用空列表表示，容器没加载就被当成「不用修」，直接把出发点下去，
    /// 结果游戏拒绝出港，还把这一艘记成了已处理。
    /// </summary>
    public static bool TryReadDamagedParts(int submarineIndex, out List<int> damaged, out string diagnostic)
    {
        damaged = new List<int>();
        diagnostic = string.Empty;

        if (submarineIndex is < 0 or > 3)
        {
            diagnostic = $"潜艇下标非法 {submarineIndex}";
            return false;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnostic = "InventoryManager 未就绪";
            return false;
        }

        var container = manager->GetInventoryContainer(PartsContainer);
        if (container == null)
        {
            diagnostic = $"容器 {(int)PartsContainer} 取不到";
            return false;
        }

        if (!container->IsLoaded)
        {
            diagnostic = $"容器 {(int)PartsContainer} 未加载";
            return false;
        }

        var start = PartsPerSubmarine * submarineIndex;
        var seen = 0;

        for (var i = 0; i < PartSlotCount; i++)
        {
            var slot = container->GetInventorySlot(start + i);
            if (slot == null || slot->ItemId == 0)
                continue;

            seen++;
            if (slot->Condition == 0)
                damaged.Add(i);
        }

        if (seen == 0)
        {
            diagnostic = $"槽位 {submarineIndex} 没有零件数据";
            return false;
        }

        diagnostic = $"容器 {(int)PartsContainer} 已加载，槽位 {submarineIndex} 读到 {seen} 个零件，坏 {damaged.Count} 个";
        return true;
    }

    /// <summary>下发一条修理命令。参数是（潜艇下标, 零件下标），与 DailyRoutines 一致。</summary>
    public static bool RepairPart(int submarineIndex, int partIndex)
    {
        try
        {
            return GameMain.ExecuteCommand(RepairSubmarinePartCommand, submarineIndex, partIndex);
        }
        catch
        {
            return false;
        }
    }

    // ──────────────── 当前潜艇下标（照 DailyRoutines 的静态地址） ────────────────

    private const string CurrentSubmarineIndexSignature = "48 8D 0D ?? ?? ?? ?? 80 A3";

    private static nint indexAddress;
    private static bool scanned;
    private static ISigScanner? sigScanner;
    private static IPluginLog? log;

    public static void Initialize(ISigScanner scanner, IPluginLog pluginLog)
    {
        sigScanner = scanner;
        log = pluginLog;
    }

    /// <summary>
    /// 游戏内部「当前查看的潜艇」下标。读不到返回 -1，调用方退回菜单里点的那个。
    /// </summary>
    public static int CurrentIndex
    {
        get
        {
            try
            {
                if (!scanned)
                {
                    scanned = true;

                    if (sigScanner != null && sigScanner.TryScanText(CurrentSubmarineIndexSignature, out var match) && match != nint.Zero)
                    {
                        // lea rcx, [rip + disp32]：目标 = 指令尾 + disp32
                        var displacement = *(int*)(match + 3);
                        indexAddress = match + 7 + displacement;
                        log?.Information($"[SubFleet] 当前潜艇下标地址 = 0x{indexAddress:X}");
                    }
                    else
                    {
                        log?.Warning("[SubFleet] 扫不到「当前潜艇下标」特征码，改用菜单选中的下标");
                    }
                }

                if (indexAddress == nint.Zero)
                    return -1;

                var index = *(int*)indexAddress;
                return index is >= 0 and <= 3 ? index : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
