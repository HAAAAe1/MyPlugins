using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace SubOps;

/// <summary>
/// 读取游戏内的潜水艇数据。
/// 数据源：HousingManager.WorkshopTerritory-&gt;Submersible.Data（4 个槽位），
/// 每个槽位有 RankId（等级，0 = 该槽位未启用）和 ReturnTime（返航时间，UTC）。
/// </summary>
public static unsafe class Workshop
{
    /// <summary>部队工房区域（territory 423 等）里为 true。</summary>
    public static bool InWorkshop
    {
        get
        {
            var manager = HousingManager.Instance();
            if (manager == null)
                return false;

            // 游戏自己的判定（HousingManager.IsInWorkshop），比看指针稳：
            // 指针在「刚进屋那一帧」可能还没切过来。
            try
            {
                if (manager->IsInWorkshop())
                    return true;
            }
            catch
            {
                // 特征码没解析出来时退回指针判断
            }

            return manager->WorkshopTerritory != null;
        }
    }

    /// <summary>在房屋内部（不是工房）时为 true。</summary>
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

    /// <summary>在房屋外部（庭院/住宅区）时为 true。</summary>
    public static bool Outdoors
    {
        get
        {
            var manager = HousingManager.Instance();
            return manager != null && manager->OutdoorTerritory != null;
        }
    }

    public readonly record struct Submarine(int Slot, byte Rank, string Name, DateTime ReturnTime)
    {
        public bool Enabled => Rank != 0;

        /// <summary>已返航、可以收取 / 再次派遣。</summary>
        public bool IsBack => Enabled && ReturnTime <= DateTime.UtcNow;
    }

    /// <summary>读取 4 个槽位的状态。不在工房时返回空列表。</summary>
    public static List<Submarine> Read()
    {
        var result = new List<Submarine>();
        if (!InWorkshop)
            return result;

        var manager = HousingManager.Instance();
        if (manager == null)
            return result;

        // IsInWorkshop() 为 true 时指针仍可能还没切过来（刚进屋那一两帧），
        // 直接解引用会 AccessViolation 把游戏带崩。
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
                    name = $"#{i}";
                }

                result.Add(new Submarine(i, sub.RankId, name, sub.GetReturnTime()));
            }
        }
        catch
        {
            // 指针失效就当作「还没读到」，调用方会重试。
        }

        return result;
    }

    /// <summary>找一个已返航的槽位下标，没有则返回 -1。</summary>
    public static int NextAvailable()
    {
        foreach (var sub in Read())
        {
            if (sub.IsBack)
                return sub.Slot;
        }

        return -1;
    }

    /// <summary>
    /// 燃料 / 修理材料。物品 ID 与 DailyRoutines 的常量逐字对齐：
    ///   10155 = 桶装青磷水（出航消耗）
    ///   10373 = 魔导机械修理材料（修理消耗）
    /// 这两个以前写反了：修理由此可能读到「0 桶青磷水」而误判成「没有修理材料」，
    /// 直接跳过修理去点出发，游戏就弹「无法进行出港，需要修理配件」。
    /// </summary>
    public const uint CeruleumTank = 10155;      // 桶装青磷水
    public const uint RepairMaterial = 10373;    // 魔导机械修理材料

    public static (int Ceruleum, int RepairMaterial) ReadMaterials()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return (0, 0);

        return (
            manager->GetInventoryItemCount(CeruleumTank, false, true, true, 0),
            manager->GetInventoryItemCount(RepairMaterial, false, true, true, 0));
    }

    // ───────────────────────── 零件与修理 ─────────────────────────

    /// <summary>
    /// 潜水艇零件所在的背包容器。
    /// DailyRoutines 用的是 InventoryType.HousingInteriorPlacedItems2 —— 工房里零件的容器就是它。
    /// 每艘艇占 5 格（用前 4 格：船体 / 舾装 / 船首 / 船尾），所以第 n 艘的起点是 5*n。
    /// 以前这里硬编码 25004，一旦枚举值不是这个数就 GetInventoryContainer 拿到 null，
    /// 读零件永远失败 → 被当成「不用修」→ 点出发 → 游戏拒绝出港。
    /// </summary>
    public static InventoryType PartsContainer => InventoryType.HousingInteriorPlacedItems2;

    /// <summary>每艘艇在容器里占的格数。</summary>
    public const int PartsPerSubmarine = 5;

    /// <summary>实际有意义的零件数（第 5 格空着）。</summary>
    public const int PartSlotCount = 4;

    /// <summary>游戏内命令 ID：修理潜水艇零件（ExecuteCommandFlag.RepairSubmarinePart）。</summary>
    private const int RepairSubmarinePartCommand = 1153;

    /// <summary>一个零件槽：Slot 是 0~3，Condition 是耐久（0 = 需要修理）。</summary>
    public readonly record struct Part(int Slot, uint ItemId, ushort Condition)
    {
        public bool Damaged => this.Condition == 0;
    }

    /// <summary>读取某艘艇的 4 个零件。不在工房、容器未加载时返回空列表。</summary>
    public static List<Part> ReadParts(int submarineSlot)
        => TryReadParts(submarineSlot, out var parts) ? parts : new List<Part>();

    /// <summary>
    /// 读取零件，并且**告诉调用方到底读到了没有**。
    /// 这一点很关键：以前用空列表同时表示「没有零件要修」和「数据没读到」，
    /// 结果容器没加载时被当成「这艘不用修」，直接把出航点下去了，
    /// 游戏弹「无法进行出港，需要修理配件」——看起来就是「不会维修潜艇」。
    /// </summary>
    public static bool TryReadParts(int submarineSlot, out List<Part> parts)
        => TryReadParts(submarineSlot, out parts, out _);

    /// <summary>同上，额外返回一句人话诊断，方便 /subops parts 定位容器到底卡在哪。</summary>
    public static bool TryReadParts(int submarineSlot, out List<Part> parts, out string diagnostic)
    {
        parts = new List<Part>();
        diagnostic = string.Empty;

        if (submarineSlot is < 0 or > 3)
        {
            diagnostic = $"槽位非法 {submarineSlot}";
            return false;
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnostic = "InventoryManager 还没就绪";
            return false;
        }

        var container = manager->GetInventoryContainer(PartsContainer);
        if (container == null)
        {
            diagnostic = $"容器 {(int)PartsContainer} 取不到（工房界面没开？）";
            return false;
        }

        if (!container->IsLoaded)
        {
            diagnostic = $"容器 {(int)PartsContainer} 还没加载";
            return false;
        }

        var start = PartsPerSubmarine * submarineSlot;
        for (var i = 0; i < PartSlotCount; i++)
        {
            var item = container->GetInventorySlot(start + i);
            if (item == null || item->ItemId == 0)
                continue;

            parts.Add(new Part(i, item->ItemId, item->Condition));
        }

        diagnostic = $"容器 {(int)PartsContainer} 已加载，槽位 {submarineSlot} 读到 {parts.Count} 个零件";
        return parts.Count > 0;
    }

    /// <summary>某艘艇需要修理的零件下标。</summary>
    public static List<int> DamagedPartSlots(int submarineSlot)
    {
        var list = new List<int>();
        foreach (var part in ReadParts(submarineSlot))
        {
            if (part.Damaged)
                list.Add(part.Slot);
        }

        return list;
    }

    /// <summary>
    /// 修理一个零件。底层走游戏的 ExecuteCommand(1153, 潜水艇槽位, 零件下标)：
    /// 不点界面、不扫描内存特征码，也不需要事先选中该艇。
    /// 消耗 1 个「魔导机械修理材料」，耐久为 0 时游戏本身不允许出航。
    /// </summary>
    public static bool RepairPart(int submarineSlot, int partSlot)
    {
        try
        {
            return GameMain.ExecuteCommand(RepairSubmarinePartCommand, submarineSlot, partSlot);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>批量修理，返回实际下发的条数。</summary>
    public static int RepairAll(int submarineSlot)
    {
        var count = 0;
        foreach (var part in DamagedPartSlots(submarineSlot))
        {
            if (RepairPart(submarineSlot, part))
                count++;
        }

        return count;
    }

    // ─────────────────── 当前潜艇索引（照 DailyRoutines 的做法） ───────────────────

    /// <summary>
    /// 游戏内部的「当前查看的潜艇索引」。
    /// DailyRoutines 用这条特征码拿到一个静态 int 的地址再读值，而不是去猜「列表第几行」。
    /// 这里照抄同一套；扫不到时返回 -1，由调用方退回自己的槽位。
    /// </summary>
    private const string CurrentSubmarineIndexSignature = "48 8D 0D ?? ?? ?? ?? 80 A3";

    private static ISigScanner? sigScanner;
    private static IPluginLog? log;
    private static nint currentSubmarineIndexAddress;
    private static bool currentSubmarineIndexScanned;

    public static void Initialize(ISigScanner scanner, IPluginLog pluginLog)
    {
        sigScanner = scanner;
        log = pluginLog;
    }

    /// <summary>游戏内部当前潜艇索引；读不到返回 -1。</summary>
    public static unsafe int CurrentSubmarineIndex
    {
        get
        {
            try
            {
                if (!currentSubmarineIndexScanned)
                {
                    currentSubmarineIndexScanned = true;

                    if (sigScanner != null
                        && sigScanner.TryScanText(CurrentSubmarineIndexSignature, out var match)
                        && match != nint.Zero)
                    {
                        // lea rcx, [rip + disp32] 是 7 字节指令：目标 = 指令尾 + disp32
                        var displacement = *(int*)(match + 3);
                        currentSubmarineIndexAddress = match + 7 + displacement;
                        log?.Information($"[SubOps] 当前潜艇索引静态地址 = 0x{currentSubmarineIndexAddress:X}");
                    }
                    else
                    {
                        log?.Warning("[SubOps] 扫不到「当前潜艇索引」特征码，改用菜单槽位");
                    }
                }

                if (currentSubmarineIndexAddress == nint.Zero)
                    return -1;

                var index = *(int*)currentSubmarineIndexAddress;
                return index is >= 0 and <= 3 ? index : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
