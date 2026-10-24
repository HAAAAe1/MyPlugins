using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace SubFleet;

/// <summary>
/// 部队房屋信息与传送。
///
/// 目标只有一个：把人送到工房里。顺序照 DailyRoutines：
///   不在房屋区域 → 传送到住宅区以太之光 → 走到部队房屋门口 → 进屋 → 选工房
/// 已经在房屋里就跳过对应步骤，绝不重复传送。
/// </summary>
public static unsafe class Housing
{
    public readonly record struct EstateInfo(uint TerritoryId, uint WardIndex, uint PlotIndex, Vector3 PlotPosition, uint MapId)
    {
        public bool HasPlotPosition => this.PlotPosition != Vector3.Zero;
    }

    private static IDataManager? data;
    private static IPluginLog? log;

    public static void Initialize(IDataManager dataManager, IPluginLog pluginLog)
    {
        data = dataManager;
        log = pluginLog;
    }

    /// <summary>当前角色所属部队的房屋。没有部队房屋时返回 false。</summary>
    public static bool TryGetFcEstate(out EstateInfo info)
    {
        info = default;

        try
        {
            var houseId = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);
            if (houseId.Id == 0)
                return false;

            var position = Vector3.Zero;
            var mapId = 0u;

            var sheet = data?.GetSubrowExcelSheet<HousingMapMarkerInfo>();
            if (sheet != null)
            {
                var marker = sheet
                    .SelectMany(x => x)
                    .Where(x => x.Map.RowId != 0)
                    .Where(x => x.Map.Value.TerritoryType.RowId == houseId.TerritoryTypeId)
                    .FirstOrDefault(x => x.SubrowId == houseId.PlotIndex);

                if (marker.Map.RowId != 0)
                {
                    position = new Vector3(marker.X, marker.Y, marker.Z);
                    mapId = marker.Map.RowId;
                }
            }

            info = new EstateInfo(houseId.TerritoryTypeId, houseId.WardIndex, houseId.PlotIndex, position, mapId);
            return true;
        }
        catch (Exception ex)
        {
            log?.Debug($"[SubFleet] 读部队房屋信息失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>一个可用的传送目标：以太之光 ID + 子索引。</summary>
    public readonly record struct TeleportTarget(uint Id, byte SubIndex, bool IsEstate);

    /// <summary>
    /// 从游戏的传送列表里挑目标。优先「部队房屋」那一条（直接落在庭院里），
    /// 没有再退到住宅区的以太之光。
    ///
    /// 不用 IsAetheryteUnlocked：Telepo 没有这个方法，能进传送列表的本来就都是调谐过的。
    /// </summary>
    public static bool TryFindTeleportTarget(uint territoryId, out TeleportTarget target)
    {
        target = default;

        var telepo = Telepo.Instance();
        if (telepo == null)
            return false;

        try
        {
            foreach (var info in telepo->TeleportList)
            {
                if (info.EstateType == EstateType.FreeCompanyEstate
                    && info.HouseId.TerritoryTypeId == territoryId)
                {
                    target = new TeleportTarget(info.AetheryteId, info.SubIndex, true);
                    return true;
                }
            }

            foreach (var info in telepo->TeleportList)
            {
                if (info.TerritoryId == territoryId)
                {
                    target = new TeleportTarget(info.AetheryteId, info.SubIndex, false);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Debug($"[SubFleet] 读传送列表失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>传送到部队房屋（或它所在的住宅区）。已经在区域内时不要调用。</summary>
    public static bool TeleportToEstateZone(uint territoryId)
    {
        var telepo = Telepo.Instance();
        if (telepo == null)
            return false;

        try
        {
            telepo->UpdateAetheryteList();
        }
        catch
        {
            // 刷新失败就用现有列表
        }

        if (!TryFindTeleportTarget(territoryId, out var target))
        {
            log?.Warning($"[SubFleet] 传送列表里没有区域 {territoryId} 的目标");
            return false;
        }

        try
        {
            return telepo->Teleport(target.Id, target.SubIndex);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "[SubFleet] 传送失败");
            return false;
        }
    }
}
