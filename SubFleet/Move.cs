using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace SubFleet;

/// <summary>靠近目标的三种结果。</summary>
public enum ApproachResult
{
    /// <summary>已经在交互距离内，可以点了。</summary>
    Arrived,

    /// <summary>这一拍刚挪过去，下一拍再看。</summary>
    Moving,

    /// <summary>瞬移落不下去（被墙/楼层挡住，或者还在读图），别再无脑绕圈了。</summary>
    Blocked,
}

/// <summary>
/// 移动与交互。
///
/// 移动＝一步到位的瞬移（直接写 BattleChara 坐标），和 DailyRoutines 的 TPSmart_InZone 一个思路：
/// 住宅区里用不着寻路，室内网格也不适合寻路。vnavmesh 只用来查脚下的地板高度。
///
/// 一条硬规则：**已经在交互距离内就一步都别动**。
/// 之前围着面板一直瞬移、甚至瞬移出地图，就是因为在已经贴着面板时又去重算落点。
/// </summary>
public sealed unsafe class Move
{
    /// <summary>进入这个距离就算站到位了，直接交互。</summary>
    public const float InteractRange = 2.4f;

    /// <summary>
    /// 判定「站到位」的余量。
    ///
    /// 必需：落点如果正好落在判定距离上，`sqrt(dx²+dz²)` 的浮点误差会让距离变成 2.0000001，
    /// 于是永远判「没到」→ 每一拍重新瞬移到同一个点 → 人就在原地或者绕着目标左右弹。
    /// </summary>
    public const float ArriveSlack = 0.4f;

    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?>? pointOnFloor;
    private readonly ICallGateSubscriber<bool>? navReady;
    private readonly ICallGateSubscriber<object>? pathStop;
    private readonly ICallGateSubscriber<bool>? pathRunning;

    private DateTime settleUntil = DateTime.MinValue;

    /// <summary>上一次瞬移想去哪、什么时候去的——用来判断「瞬移到底站没站住」。</summary>
    private Vector3 lastLanding;
    private DateTime lastLandingAt = DateTime.MinValue;
    private Vector3 lastTarget;
    private int stuckTries;

    public Move(IDalamudPluginInterface pluginInterface, IObjectTable objects, IClientState clientState, IPluginLog log)
    {
        this.objects = objects;
        this.clientState = clientState;
        this.log = log;

        this.pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        this.navReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.pathStop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        this.pathRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    }

    public bool HasVnavmesh => this.pointOnFloor is { HasFunction: true };

    public IGameObject? Player => this.objects.LocalPlayer;

    public Vector3 PlayerPosition => this.objects.LocalPlayer?.Position ?? Vector3.Zero;

    public uint Territory => this.clientState.TerritoryType;

    public float FlatDistanceTo(Vector3 position)
    {
        var player = this.Player;
        if (player == null)
            return float.MaxValue;

        return Flat(player.Position, position);
    }

    /// <summary>三点距离（带高度）。住宅区有楼层，只看平面距离会把楼上当成门口。</summary>
    public float DistanceTo(Vector3 position)
    {
        var player = this.Player;
        if (player == null)
            return float.MaxValue;

        return Vector3.Distance(player.Position, position);
    }

    public static string Fmt(Vector3 v) => $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";

    /// <summary>停掉别的插件正在跑的寻路，免得把刚瞬移过去的人又拉回去。</summary>
    public void StopPathing()
    {
        try
        {
            if (this.pathRunning is { HasFunction: true } running && running.InvokeFunc())
                this.pathStop?.InvokeAction();
        }
        catch
        {
            // 没装 vnavmesh，忽略
        }
    }

    /// <summary>对目标交互一次。</summary>
    public bool Interact(IGameObject obj)
    {
        if (obj == null || !obj.IsValid())
            return false;

        var system = TargetSystem.Instance();
        if (system == null)
            return false;

        return system->InteractWithObject(
                   (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, true) != 0;
    }

    /// <summary>直接对原生指针交互（事件表里拿到的物件，对象表里未必查得到）。</summary>
    public bool InteractRaw(nint address)
    {
        if (address == 0)
            return false;

        var system = TargetSystem.Instance();
        if (system == null)
            return false;

        return system->InteractWithObject(
                   (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address, true) != 0;
    }

    /// <summary>直接落到指定坐标（用于住宅区内跨几十米跳过去）。</summary>
    public bool Teleport(Vector3 destination, bool useTargetY = true)
    {
        var player = this.Player;
        if (player == null)
            return false;

        var current = player.Position;
        var y = current.Y;

        if (useTargetY)
            y = destination.Y;

        var floor = this.QueryFloorY(destination);
        if (floor.HasValue)
            y = floor.Value;

        this.StopPathing();

        try
        {
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)player.Address;
            chara->SetPosition(destination.X, y, destination.Z);
            chara->SetRotation(MathF.Atan2(destination.X - current.X, destination.Z - current.Z));
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubFleet] 瞬移失败");
            return false;
        }

        this.settleUntil = DateTime.UtcNow.AddMilliseconds(250);
        return true;
    }

    /// <summary>
    /// 靠到目标身边。
    /// 返回 <see cref="ApproachResult.Arrived"/> ＝ 已经在 <paramref name="standOff"/> 米内，可以交互了；
    /// <see cref="ApproachResult.Moving"/> ＝ 这一拍刚挪过去，下一拍再看；
    /// <see cref="ApproachResult.Blocked"/> ＝ 连着几次瞬移都没落下去，别再绕圈了。
    ///
    /// 落点照 DailyRoutines 的做法：**直接落到目标坐标上**（它也是 TPSmart 到门口再交互）。
    /// 旧版是落到「离目标 standOff 米」的圆周上，那个点正好压在判定线上，
    /// 浮点误差一大就永远差那么一点 → 每三次换个 90° 方向重来 → 人就在房子周围左右弹。
    /// </summary>
    public ApproachResult Approach(Vector3 target, float standOff = InteractRange, int attempt = 0)
    {
        var player = this.Player;
        if (player == null)
            return ApproachResult.Moving;

        // 目标变了（换门/换面板/换房间）就把卡住计数清掉
        if (Vector3.Distance(target, this.lastTarget) > 3f)
        {
            this.lastTarget = target;
            this.stuckTries = 0;
            this.lastLandingAt = DateTime.MinValue;
        }

        var pos = player.Position;

        // 先看有没有到：到了就什么都不做（这条永远优先，避免把「已经贴着」误判成「没站住」）
        if (Vector3.Distance(pos, target) <= standOff + ArriveSlack)
        {
            this.stuckTries = 0;
            this.lastLandingAt = DateTime.MinValue;
            return ApproachResult.Arrived;
        }

        // 刚瞬移完的那一小会儿，等客户端站稳，别再挪。
        if (DateTime.UtcNow < this.settleUntil)
            return ApproachResult.Moving;

        // 上一次瞬移落下去之后，人到底在不在那个点附近？
        if (this.lastLandingAt != DateTime.MinValue
            && (DateTime.UtcNow - this.lastLandingAt).TotalMilliseconds >= 400)
        {
            this.lastLandingAt = DateTime.MinValue;

            if (Flat(pos, this.lastLanding) > 1.5f)
            {
                this.stuckTries++;
                this.log.Warning(
                    $"[SubFleet] 瞬移没站住：想去 {Fmt(this.lastLanding)}，实际在 {Fmt(pos)}"
                    + $"（连续 {this.stuckTries} 次，离目标 {Vector3.Distance(pos, target):F1}m）");
            }
            else
            {
                this.stuckTries = 0;
            }

            if (this.stuckTries >= 3)
                return ApproachResult.Blocked;
        }

        var landing = target;

        if (this.stuckTries > 0)
        {
            // 同一个点连着落不下去（被墙/台阶挡）→ 沿「目标→我」的方向退开一点再落
            var dx = pos.X - target.X;
            var dz = pos.Z - target.Z;
            var len = MathF.Sqrt((dx * dx) + (dz * dz));
            if (len > 0.05f)
            {
                var back = 1.2f + (this.stuckTries * 0.8f);
                landing = new Vector3(target.X + (dx / len * back), target.Y, target.Z + (dz / len * back));
            }
        }
        else if (attempt > 0 && Flat(pos, target) < 8f)
        {
            // 就在旁边却够不着（门在上一层 / 被栏杆夹住）→ 才换方位，一次 90°
            var angle = MathF.Atan2(pos.X - target.X, pos.Z - target.Z) + (attempt * (MathF.PI / 2f));
            landing = new Vector3(
                target.X + (MathF.Sin(angle) * (standOff * 0.6f)),
                target.Y,
                target.Z + (MathF.Cos(angle) * (standOff * 0.6f)));
        }

        this.lastLanding = landing;
        if (!this.Teleport(landing, true))
            return ApproachResult.Blocked;

        this.lastLandingAt = DateTime.UtcNow;
        return ApproachResult.Moving;
    }

    /// <summary>
    /// 按 EventId 找事件物件（DailyRoutines 的取法）。
    /// 同一个事件可能挂好几个物件（每层楼各一个面板、每个房间一扇门），
    /// 取**离玩家最近**的那个，不要取第一个——取第一个会把人瞬移到别的楼层去。
    /// </summary>
    public bool TryFindEvent(uint eventId, out Vector3 position, out nint address)
    {
        position = Vector3.Zero;
        address = nint.Zero;

        try
        {
            var framework = EventFramework.Instance();
            if (framework == null)
                return false;

            FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler* handler = null;
            foreach (var pair in framework->EventHandlerModule.EventHandlerMap)
            {
                var candidate = pair.Item2.Value;
                if (candidate == null)
                    continue;

                if (candidate->Info.EventId.Id != eventId)
                    continue;

                handler = candidate;
                break;
            }

            if (handler == null)
                return false;

            var playerPosition = this.PlayerPosition;
            var best = float.MaxValue;
            var found = false;

            foreach (var pointer in handler->EventObjects)
            {
                var go = pointer.Value;
                if (go == null)
                    continue;

                var raw = go->Position;
                var candidate = new Vector3(raw.X, raw.Y, raw.Z);
                if (candidate == Vector3.Zero)
                    continue;

                var distance = Vector3.Distance(candidate, playerPosition);
                if (distance >= best)
                    continue;

                best = distance;
                position = candidate;
                address = (nint)go;
                found = true;
            }

            return found;
        }
        catch (Exception ex)
        {
            this.log.Debug($"[SubFleet] 读事件表失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>对象表里按 BaseId（DataId）找最近的可交互物件。</summary>
    public IGameObject? NearestByBaseId(uint baseId, float radius = 80f)
        => this.NearestByBaseIdTo(baseId, this.PlayerPosition, radius);

    /// <summary>
    /// 按 BaseId 找离 <paramref name="anchor"/> 最近的物件。
    /// 房屋入口这个 DataId 全住宅区共用，必须按「离自家地块最近」挑，
    /// 否则会跑去点别人家的门。
    /// </summary>
    public IGameObject? NearestByBaseIdTo(uint baseId, Vector3 anchor, float radius = 80f)
    {
        return this.objects
            .Where(x => x.ObjectKind == ObjectKind.EventObj && x.IsValid() && x.BaseId == baseId)
            .Select(x => (Obj: x, Distance: Vector3.Distance(x.Position, anchor)))
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Select(x => (IGameObject?)x.Obj)
            .FirstOrDefault();
    }

    private float? QueryFloorY(Vector3 target)
    {
        try
        {
            if (this.navReady is { HasFunction: true } ready && !ready.InvokeFunc())
                return null;

            var floor = this.pointOnFloor?.InvokeFunc(target, true, 2.5f);
            if (floor.HasValue && MathF.Abs(floor.Value.Y - target.Y) <= 4f)
                return floor.Value.Y;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
