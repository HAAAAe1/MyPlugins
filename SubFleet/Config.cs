using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Configuration;

namespace SubFleet;

public sealed class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ───────────────────────── 界面 ─────────────────────────

    /// <summary>屏幕上那个可以随便拖的小图标，点一下开关主窗口。</summary>
    public bool ShowFloatIcon { get; set; } = true;

    /// <summary>图标位置（拖到哪记到哪）。</summary>
    public Vector2 FloatIconPos { get; set; } = new(60f, 320f);

    // ───────────────────────── 收取 ─────────────────────────

    /// <summary>出发前把耐久为 0 的零件修好（消耗魔导机械修理材料）。</summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>收完之后按原航线再次出航。关掉就只收不出。</summary>
    public bool ReDispatch { get; set; } = true;

    /// <summary>聊天框汇报每一步。</summary>
    public bool VerboseChat { get; set; } = true;

    /// <summary>航行管制面板主菜单里「管理潜水艇」大概在第几项（文字匹配失败时的兜底）。</summary>
    public int PanelMenuIndex { get; set; } = 0;

    /// <summary>「移动到其他房间」菜单里工房大概在第几项（兜底）。</summary>
    public int RoomMenuIndex { get; set; } = 0;

    // ───────────────────────── 多角色 ─────────────────────────

    public List<CharacterEntry> Roster { get; set; } = new();

    /// <summary>轮换是否正在进行（会持久化，插件重载后能接着跑）。</summary>
    public bool SweepActive { get; set; }

    /// <summary>本轮处理到名册第几个。</summary>
    public int SweepCursor { get; set; }

    /// <summary>全部角色处理完后登出到标题界面。</summary>
    public bool SweepLogoutWhenDone { get; set; }

    /// <summary>跳过「确定没有到港潜艇」的角色（默认关：多切一趟也别漏收）。</summary>
    public bool SweepSkipIdle { get; set; }

    /// <summary>提前量（秒）：返航时间落在「现在 + 提前量」内就算该去。</summary>
    public int SweepAdvanceSeconds { get; set; } = 300;

    public CharacterEntry? Find(ulong contentId) => this.Roster.FirstOrDefault(x => x.ContentId == contentId);

    public CharacterEntry? FindByName(string name) => this.Roster.FirstOrDefault(
        x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)
             || string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase));

    public int EnabledCount => this.Roster.Count(x => x.Enabled);
}

/// <summary>名册里的一个角色。</summary>
public sealed class CharacterEntry
{
    public ulong ContentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>服务器名，Lifestream 切角色用「名@服务器」。</summary>
    public string World { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>这个角色上次在工房拍到数据的时间（Unix 秒）。0 = 还没去过。</summary>
    public long SnapshotAt { get; set; }

    /// <summary>在此之前不再尝试这个角色（Unix 秒）。</summary>
    public long CooldownUntil { get; set; }

    public List<VesselSnapshot> Vessels { get; set; } = new();

    public string Key => $"{this.Name}@{this.World}";

    public bool OnCooldown(DateTime utcNow) => this.CooldownUntil > ToUnix(utcNow);

    public int ActiveVessels => this.Vessels.Count(v => v.Rank != 0);

    /// <summary>
    /// 有没有该去收的活。还没拍过快照的角色一律算「有活」，否则永远不会去第一次。
    /// </summary>
    public bool HasDueWork(DateTime utcNow, int advanceSeconds)
    {
        if (this.SnapshotAt == 0)
            return true;

        var limit = ToUnix(utcNow.AddSeconds(advanceSeconds));
        return this.Vessels.Any(v => v.Rank != 0 && v.ReturnAt <= limit);
    }

    public DateTime? NextReturn()
    {
        var times = this.Vessels.Where(v => v.Rank != 0).Select(v => v.ReturnAt).ToList();
        if (times.Count == 0)
            return null;

        return FromUnix(times.Min());
    }

    public static long ToUnix(DateTime value) => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds();

    public static DateTime FromUnix(long value) => DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
}

public sealed class VesselSnapshot
{
    public int Slot { get; set; }

    /// <summary>等级；0 = 空槽。</summary>
    public int Rank { get; set; }

    public long ReturnAt { get; set; }
}
