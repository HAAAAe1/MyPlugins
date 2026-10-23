using System;
using System.Collections.Generic;
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

        var data = HousingManager.Instance()->WorkshopTerritory->Submersible.Data;
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

    /// <summary>燃料 / 修理材料的持有量，用于出航前的可行性提示。物品ID见下。</summary>
    public const uint CeruleumTank = 10373;      // 桶装青磷水
    public const uint RepairMaterial = 10155;    // 魔导机械修理材料

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
    /// 潜水艇零件所在的背包容器 ID。工房里这个数值固定是 25004，
    /// 每艘艇占 5 格（用前 4 格：船体 / 舾装 / 船首 / 船尾），所以第 n 艘的起点是 5*n。
    /// </summary>
    public const int PartsContainerId = 25004;

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
    {
        var result = new List<Part>();
        if (submarineSlot < 0 || submarineSlot > 3)
            return result;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return result;

        var container = manager->GetInventoryContainer((InventoryType)PartsContainerId);
        if (container == null || !container->IsLoaded)
            return result;

        var start = PartsPerSubmarine * submarineSlot;
        for (var i = 0; i < PartSlotCount; i++)
        {
            var item = container->GetInventorySlot(start + i);
            if (item == null || item->ItemId == 0)
                continue;

            result.Add(new Part(i, item->ItemId, item->Condition));
        }

        return result;
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
}
