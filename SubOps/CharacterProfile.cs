using System;
using System.Collections.Generic;
using System.Linq;

namespace SubOps;

/// <summary>
/// 一个角色的潜水艇档案。
///
/// 潜水艇的返航时间只有站进工房才能读到（HousingManager 只加载当前角色的房屋），
/// 所以每进一次工房就把 4 个槽位拍个快照存下来，据此判断「这个角色还有没有必要去」。
/// </summary>
public sealed class CharacterProfile
{
    /// <summary>角色标识（登录时唯一）。</summary>
    public ulong ContentId { get; set; }

    /// <summary>角色名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所在服务器名（切角色时要用，Lifestream 需要「名@服务器」）。</summary>
    public string World { get; set; } = string.Empty;

    /// <summary>是否参与轮换。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>上次在工房拍到数据的时间（Unix 秒）。0 = 还没去过。</summary>
    public long SnapshotAt { get; set; }

    /// <summary>在此时间之前不再考虑这个角色（Unix 秒），用于失败后冷却。</summary>
    public long CooldownUntil { get; set; }

    /// <summary>4 个槽位的快照。</summary>
    public List<VesselSnapshot> Vessels { get; set; } = new();

    public string Key => $"{this.Name}@{this.World}";

    /// <summary>已启用的艇数。</summary>
    public int ActiveVessels => this.Vessels.Count(v => v.Rank != 0);

    public bool OnCooldown(DateTime utcNow) => this.CooldownUntil > utcNow.ToUnix();

    /// <summary>最近一次返航时间，没有任何艇时返回 null。</summary>
    public DateTime? NextReturn(DateTime utcNow)
    {
        var times = this.Vessels.Where(v => v.Rank != 0).Select(v => v.ReturnAt).ToList();
        if (times.Count == 0)
            return null;

        return DateTimeExt.FromUnix(times.Min());
    }

    /// <summary>
    /// 有没有「该去收了」的活。advanceSeconds 是提前量：
    /// 返航时间落在 <c>now + 提前量</c> 之内就算，免得掐着秒表等。
    /// 还没拍过快照的角色一律算「有活」——否则永远不会去第一次，也就永远拿不到数据。
    /// </summary>
    public bool HasDueWork(DateTime utcNow, int advanceSeconds)
    {
        if (this.SnapshotAt == 0)
            return true;

        var limit = utcNow.AddSeconds(advanceSeconds).ToUnix();
        return this.Vessels.Any(v => v.Rank != 0 && v.ReturnAt <= limit);
    }

    public string Describe(DateTime utcNow, int advanceSeconds)
    {
        if (this.ActiveVessels == 0)
            return this.SnapshotAt == 0 ? "没有数据（还没进过工房）" : "没有启用的潜水艇";

        var due = this.HasDueWork(utcNow, advanceSeconds) ? "待收取" : "不需处理";
        var next = this.NextReturn(utcNow);
        var when = next == null ? "—" : next.Value.ToLocalTime().ToString("MM-dd HH:mm");
        var cd = this.OnCooldown(utcNow)
            ? $"，冷却至 {DateTimeExt.FromUnix(this.CooldownUntil).ToLocalTime():HH:mm}"
            : string.Empty;
        var snap = this.SnapshotAt == 0
            ? string.Empty
            : $"，快照 {DateTimeExt.FromUnix(this.SnapshotAt).ToLocalTime():MM-dd HH:mm}";

        return $"{this.ActiveVessels} 艘 · {due} · 下次返航 {when}{snap}{cd}";
    }
}

/// <summary>单个潜水艇槽位的快照。</summary>
public sealed class VesselSnapshot
{
    public int Slot { get; set; }

    /// <summary>等级；0 = 该槽位没启用。</summary>
    public int Rank { get; set; }

    /// <summary>返航时间（Unix 秒）。</summary>
    public long ReturnAt { get; set; }
}

internal static class DateTimeExt
{
    public static long ToUnix(this DateTime value) => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds();

    public static DateTime FromUnix(long value) => DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
}
