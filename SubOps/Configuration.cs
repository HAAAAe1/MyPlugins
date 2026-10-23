using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace SubOps;

/// <summary>
/// 插件配置。
/// 位置用三个 float 分开存：System.Text.Json 默认不序列化字段，
/// 直接存 Vector3 会丢数据，所以这里手动拆开。
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Dalamud 要求的配置版本号，结构变化时递增以做迁移。</summary>
    public int Version { get; set; } = 3;

    /// <summary>与目标对象交互的距离阈值（码）。</summary>
    public float InteractRange { get; set; } = 2.5f;

    /// <summary>房屋内「移动到其他房间」菜单里，部队工房那一项的下标。</summary>
    public int RoomMenuIndex { get; set; } = 0;

    /// <summary>航行管制面板菜单里「管理潜水艇」那一项的下标。</summary>
    public int PanelMenuIndex { get; set; } = 1;

    /// <summary>收取完成后是否立刻按原航线再次出航（对应详情界面的「出发」）。</summary>
    public bool ReDispatch { get; set; } = true;

    /// <summary>
    /// 再次出航前自动修理耐久为 0 的零件。
    /// 用游戏命令 1153 下发，每个零件消耗 1 个「魔导机械修理材料」。
    /// </summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>连续操作轮数上限，防止界面异常时无限循环。</summary>
    public int MaxLoops { get; set; } = 12;

    public float PanelX { get; set; }

    public float PanelY { get; set; }

    public float PanelZ { get; set; }

    public float DoorX { get; set; }

    public float DoorY { get; set; }

    public float DoorZ { get; set; }

    /// <summary>
    /// 部队工房是独立副本房间，所有部队的室内布局与坐标完全一致，
    /// 所以「移动到其他房间」这个门的位置可以直接内置，不用每个用户自己记。
    /// </summary>
    public static readonly Vector3 BuiltinDoorAnchor = new(-841.40f, -6.66f, -779.00f);

    /// <summary>房门锚点搜索半径（码）。</summary>
    public const float DoorSearchRadius = 8f;

    /// <summary>
    /// 面板优先按名字找（名字里带「航行」），名字对不上时退回这个半径内的最近可交互物件。
    /// 工房很小，30 码足够覆盖。
    /// </summary>
    public const float PanelSearchRadius = 30f;

    public bool HasPanelAnchor => this.PanelX != 0 || this.PanelY != 0 || this.PanelZ != 0;

    public bool HasDoorAnchor => this.DoorX != 0 || this.DoorY != 0 || this.DoorZ != 0;

    public Vector3 PanelAnchor => new(this.PanelX, this.PanelY, this.PanelZ);

    /// <summary>用户没记过就用内置坐标。</summary>
    public Vector3 DoorAnchor => this.HasDoorAnchor ? new Vector3(this.DoorX, this.DoorY, this.DoorZ) : BuiltinDoorAnchor;

    public void SetPanelAnchor(Vector3 value)
    {
        this.PanelX = value.X;
        this.PanelY = value.Y;
        this.PanelZ = value.Z;
    }

    public void SetDoorAnchor(Vector3 value)
    {
        this.DoorX = value.X;
        this.DoorY = value.Y;
        this.DoorZ = value.Z;
    }

    // ───────────────────────── 多角色轮换 ─────────────────────────

    /// <summary>角色名册，按首次登录顺序排列。</summary>
    public List<CharacterProfile> Characters { get; set; } = new();

    /// <summary>
    /// 多角色轮换开关。开着表示「有一轮正在进行」——由 /subops multi on 启动，
    /// 走完名册或 /subops multi off / stop 结束。
    /// </summary>
    public bool MultiMode { get; set; }

    /// <summary>
    /// 轮换时跳过「确定没有到港的艇」的角色（默认关 = 每个角色都进去看一眼）。
    ///
    /// 默认关是有原因的：返航时间只有站进工房才读得到，靠上次的快照判断经常过时
    /// （改了航线、修了零件都会变），所以宁可多切一趟也别漏收。
    /// </summary>
    public bool MultiSkipIdle { get; set; }

    /// <summary>
    /// 提前量（秒）：返航时间落在「现在 + 提前量」之内就算该去收了。
    /// 默认 300 秒，避免刚好差几十秒就白切一趟。
    /// </summary>
    public int MultiAdvanceSeconds { get; set; } = 300;

    /// <summary>所有角色都收完后是否登出到标题界面。</summary>
    public bool MultiLogoutWhenDone { get; set; }
}
