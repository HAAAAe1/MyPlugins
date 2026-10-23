using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace SubOps;

/// <summary>靠近目标的结果。</summary>
public enum ApproachState
{
    /// <summary>已进入交互距离。</summary>
    Arrived,

    /// <summary>正在推进，本帧还没到。</summary>
    Moving,

    /// <summary>目标太远（跨区域），必须先传送，不能硬推。</summary>
    TooFar,

    /// <summary>目标无效或无法推进。</summary>
    Blocked,
}

/// <summary>
/// 移动与交互。
/// 移动用「一步到位瞬移」：直接写 BattleChara 坐标，不做逐帧插值。
/// vnavmesh 只用来查地板高度（Y 轴），不用它寻路——
/// 住宅室内网格只有几 KB，寻路在里面必失败。
/// </summary>
public sealed unsafe class Navigator
{
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    /// <summary>地板高度查询（vnavmesh.Query.Mesh.PointOnFloor）。</summary>
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?>? pointOnFloor;

    /// <summary>离目标最近的可站点（vnavmesh.Query.Mesh.NearestPoint）。</summary>
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?>? nearestPoint;

    /// <summary>vnavmesh 导航是否就绪（网格没建好时查询会返回空）。</summary>
    private readonly ICallGateSubscriber<bool>? navReady;

    /// <summary>停止寻路（万一有别的插件在跑路径，瞬移前掐掉）。</summary>
    private readonly ICallGateSubscriber<object>? pathStop;

    /// <summary>寻路是否在跑（只用于瞬移前清理，不用它走路）。</summary>
    private readonly ICallGateSubscriber<bool>? pathRunning;

    public Navigator(IDalamudPluginInterface pluginInterface, IObjectTable objects, ITargetManager targets, IClientState clientState, IPluginLog log)
    {
        this.objects = objects;
        this.targets = targets;
        this.clientState = clientState;
        this.log = log;

        // 这些名字在 vnavmesh 0.8.x 上都存在；取不到时对应调用会抛异常，
        // 所以每个查询都包了 try/catch，缺一个也不影响其它功能。
        this.pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        this.nearestPoint = pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        this.navReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        this.pathStop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        this.pathRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    }

    public bool HasVnavmesh => this.pointOnFloor != null;

    public IGameObject? Player => this.objects.LocalPlayer;

    public Vector3 PlayerPosition => this.objects.LocalPlayer?.Position ?? Vector3.Zero;

    public float DistanceTo(Vector3 position) => this.Player == null ? float.MaxValue : Vector3.Distance(this.Player.Position, position);

    /// <summary>有没有别的插件在跑寻路。瞬移前要掐掉，否则会被拉回去。</summary>
    public bool IsMoving()
    {
        if (this.pathRunning == null)
            return false;

        try
        {
            return this.pathRunning.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>停掉寻路（不走路，只为瞬移让路）。</summary>
    public void StopMoving()
    {
        try
        {
            if (this.IsMoving())
                this.pathStop?.InvokeAction();
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>对目标发动一次「交互」，成功返回 true。</summary>
    public bool Interact(IGameObject obj)
    {
        if (!obj.IsValid())
            return false;

        this.targets.Target = obj;

        var system = TargetSystem.Instance();
        if (system == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        var result = system->InteractWithObject(native, true);
        return result != 0;
    }

    /// <summary>直接对原生 GameObject 指针交互（对象表里未必查得到，事件对象走这条）。</summary>
    public unsafe bool InteractRaw(nint address)
    {
        if (address == 0)
            return false;

        var system = TargetSystem.Instance();
        if (system == null)
            return false;

        return system->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address, true) != 0;
    }

    /// <summary>水平面之外还带上高度差的距离（判断「到没到门口」用）。</summary>
    public float Distance3D(Vector3 position)
    {
        var player = this.Player;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, position);
    }

    /// <summary>
    /// 按 EventId 在当前区域里找事件对象。
    /// 这是 DailyRoutines 的做法：直接翻 EventFramework 的事件表，
    /// 找到「移动到其他房间」/「航行管制面板」对应的那个物件，拿它的坐标去瞬移。
    /// 好处是完全不需要维护坐标表，换房子、换布局都不用改。
    /// </summary>
    public unsafe bool TryFindEventObject(uint eventId, out EventObjectRef found)
    {
        found = default;

        try
        {
            var framework = EventFramework.Instance();
            if (framework == null)
                return false;

            FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler* handler = null;
            var objectCount = 0;

            foreach (var pair in framework->EventHandlerModule.EventHandlerMap)
            {
                var candidate = pair.Item2.Value;
                if (candidate == null)
                    continue;

                if (candidate->Info.EventId.Id != eventId)
                    continue;

                handler = candidate;
                objectCount = candidate->EventObjects.Count;
                break;
            }

            if (handler == null)
                return false;

            foreach (var pointer in handler->EventObjects)
            {
                var go = pointer.Value;
                if (go == null)
                    continue;

                // 同一个事件可能挂了多个物件（每个房间一扇门），取第一个即可，
                // 反正都在同一栋房子里。
                found = new EventObjectRef(go->Position, (ulong)go->GetGameObjectId(), (nint)go, objectCount);
                return true;
            }

            found = new EventObjectRef(Vector3.Zero, 0, 0, objectCount);
            return false;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[SubOps] 读事件表失败");
            return false;
        }
    }

    /// <summary>事件对象：坐标、对象 ID、原生地址，以及这个事件一共挂了几个物件（诊断用）。</summary>
    public readonly record struct EventObjectRef(Vector3 Position, ulong ObjectId, nint Address, int ObjectCount);

    /// <summary>
    /// 找一个事件对象（EventObj）。
    /// baseId 非 0 时精确匹配；否则在 anchor 附近 radius 范围内找最近的一个。
    /// </summary>
    public IGameObject? FindEventObject(uint baseId, Vector3 anchor, float radius)
    {
        var candidates = this.objects
            .Where(x => x.ObjectKind == ObjectKind.EventObj && x.IsValid() && x.IsTargetable)
            .ToList();

        if (baseId != 0)
            return candidates.FirstOrDefault(x => x.BaseId == baseId);

        if (anchor == Vector3.Zero)
            return null;

        var nearest = candidates
            .Select(x => (Obj: x, Distance: Vector3.Distance(x.Position, anchor)))
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .FirstOrDefault();

        return nearest.Obj;
    }

    /// <summary>
    /// 按名字找事件对象。工房里的「航行管制面板」这类固定的场景物件，
    /// 名字比坐标更靠谱（布局固定，但用名字就不用维护坐标表）。
    /// 命中多个时取离 anchor 最近的。
    /// </summary>
    public IGameObject? FindEventObjectByName(string namePart, Vector3 anchor, float radius)
    {
        var origin = anchor == Vector3.Zero ? this.PlayerPosition : anchor;

        return this.objects
            .Where(x => x.ObjectKind == ObjectKind.EventObj && x.IsValid() && x.IsTargetable)
            .Where(x => !string.IsNullOrEmpty(x.Name.TextValue)
                        && x.Name.TextValue.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            .Select(x => (Obj: x, Distance: Vector3.Distance(x.Position, origin)))
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Select(x => (IGameObject?)x.Obj)
            .FirstOrDefault();
    }

    /// <summary>
    /// 列出附近的可交互事件对象，按距离从近到远排好。
    /// 用于「大厅里有一堆长得一样的门」这种场景：挨个试，不靠猜。
    /// </summary>
    public List<IGameObject> EventObjectsNear(Vector3 anchor, float radius)
    {
        return this.objects
            .Where(x => x.ObjectKind == ObjectKind.EventObj && x.IsValid() && x.IsTargetable)
            .Select(x => (Obj: x, Distance: Vector3.Distance(x.Position, anchor)))
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Select(x => x.Obj)
            .ToList();
    }

    /// <summary>
    /// 列出附近的可交互事件对象，用于校准（/subops objects）。
    /// </summary>
    public List<(string Name, uint BaseId, Vector3 Position, float Distance)> ListEventObjects(float radius)
    {
        var origin = this.PlayerPosition;
        return this.objects
            .Where(x => x.ObjectKind == ObjectKind.EventObj && x.IsValid())
            .Select(x => (
                Name: string.IsNullOrEmpty(x.Name.TextValue) ? "(无名)" : x.Name.TextValue,
                BaseId: x.BaseId,
                Position: x.Position,
                Distance: Vector3.Distance(x.Position, origin)))
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .ToList();
    }

    /// <summary>
    /// 直接瞬移到目标身边。一步到位，不做逐帧插值。
    /// 返回 Arrived 表示已经在交互距离内、可以动手；
    /// 返回 Moving 表示这一 tick 刚跳过去，等下一 tick 再动手（给客户端一帧落地时间）。
    /// </summary>
    public ApproachState Approach(IGameObject obj, float range)
    {
        if (obj == null || !obj.IsValid())
            return ApproachState.Blocked;

        return this.ApproachPosition(obj.Position, range);
    }

    /// <summary>
    /// 站到目标的「交互位」上：以目标为圆心、standOff 为半径，取靠近玩家这一侧的点。
    /// attempt 用来在第一次失败后换个角度再试（0 = 玩家这一侧，1/2/3 = 依次转 90°）。
    ///
    /// 面板这类物件必须这么处理：直接瞬移到物件坐标 = 站到柜台上，
    /// 交互打不开（人卡在模型里），而且 Y 会被物件高度顶起来。
    /// </summary>
    public ApproachState ApproachStand(Vector3 target, float standOff, int attempt)
    {
        var player = this.Player;
        if (player == null)
            return ApproachState.Blocked;

        var pos = player.Position;
        var dx = pos.X - target.X;
        var dz = pos.Z - target.Z;
        var len = MathF.Sqrt((dx * dx) + (dz * dz));

        float angle;
        if (len < 0.05f)
        {
            // 玩家正好在物件里：没有方向可用，从正面开始试。
            angle = 0f;
        }
        else
        {
            angle = MathF.Atan2(dx, dz);
        }

        angle += attempt * (MathF.PI / 2f);

        var stand = new Vector3(
            target.X + (MathF.Sin(angle) * standOff),
            target.Y,
            target.Z + (MathF.Cos(angle) * standOff));

        // 已经站好了就别再动，避免来回抖。
        if (FlatDistance(pos, stand) <= 0.7f)
            return ApproachState.Arrived;

        return this.ApproachPosition(stand, 0.8f);
    }

    /// <summary>直接瞬移到指定坐标。</summary>
    public ApproachState ApproachPosition(Vector3 target, float range)
    {
        var player = this.Player;
        if (player == null)
            return ApproachState.Blocked;

        var pos = player.Position;
        var flatDist = FlatDistance(pos, target);

        // 刚落点：先做一次贴地校正，再等一帧让客户端站稳。
        // 这一步是"悬空"的兜底：不管前面 Y 怎么算的，只要脚下明显高于地面就压下去。
        if (DateTime.UtcNow < this.settleUntil)
        {
            this.SnapToGround(player);
            return ApproachState.Moving;
        }

        if (flatDist <= range)
            return ApproachState.Arrived;

        // 跨区域不能瞬移，交给上层先传送。
        if (flatDist > MaxTeleportStep)
            return ApproachState.TooFar;

        // 落到物件前方一点，而不是踩进模型里：朝向目标的方向退 range 的六成。
        if (flatDist < 0.01f)
            return ApproachState.Arrived;

        var dir = new Vector3((target.X - pos.X) / flatDist, 0f, (target.Z - pos.Z) / flatDist);
        var stopBack = MathF.Min(range * 0.6f, MathF.Max(flatDist - 0.2f, 0f));
        var landing = new Vector3(target.X - dir.X * stopBack, target.Y, target.Z - dir.Z * stopBack);

        var dest = this.ResolveDestination(landing, flatDist);

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)player.Address;
        if (chara == null)
            return ApproachState.Blocked;

        // 同一个目标反复瞬移却始终没靠近，说明位置被服务器/碰撞拉回了，别再死磕。
        if (Vector3.Distance(this.jumpTarget, landing) <= 1.5f
            && (DateTime.UtcNow - this.jumpAt).TotalSeconds < 8)
        {
            this.jumpCount++;
        }
        else
        {
            this.jumpCount = 0;
        }

        if (this.jumpCount > MaxJumpsPerTarget)
        {
            this.log.Warning($"[SubOps] 瞬移到 ({landing.X:F1}, {landing.Z:F1}) 反复失败，判定不可达");
            this.jumpCount = 0;
            return ApproachState.Blocked;
        }

        this.jumpTarget = landing;
        this.jumpAt = DateTime.UtcNow;

        this.StopMoving();

        try
        {
            chara->SetPosition(dest.X, dest.Y, dest.Z);
            chara->SetRotation(MathF.Atan2(dest.X - pos.X, dest.Z - pos.Z));
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "瞬移失败");
            return ApproachState.Blocked;
        }

        this.settleUntil = DateTime.UtcNow.AddMilliseconds(250);
        this.lastDestination = dest;
        this.lastDestinationAt = DateTime.UtcNow;
        return ApproachState.Moving;
    }

    /// <summary>
    /// 脚下明显高于地面时把人压回地面（只压、不抬，避免掉进地板）。
    /// 用 vnavmesh 的地板查询；查不到或差值离谱就不动。
    /// </summary>
    private void SnapToGround(IGameObject player)
    {
        var pos = player.Position;
        var floor = this.QueryFloorY(pos);
        if (!floor.HasValue)
            return;

        var drop = pos.Y - floor.Value;
        if (drop <= 0.15f || drop > FloorSanityLimit)
            return;

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)player.Address;
        if (chara == null)
            return;

        try
        {
            chara->SetPosition(pos.X, floor.Value, pos.Z);
            this.log.Debug($"[SubOps] 落地校正 -{drop:F2}");
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// 决定落点的 Y 轴。顺序：
    /// ① vnavmesh 查到的地板高度（最准）；
    /// ② 同一房间内：**只用脚下高度**，绝不用物件自身的 Y。
    ///    航行管制面板的 Y 在柜台上（比地面高一米多），早期这里采信了它，
    ///    结果人被摆到柜台上、面板反而打不开。现在的原则是「只降不升」。
    /// ③ 跨房间/跨区域才采信目标自身的 Y（门口、传送点都在地面）。
    /// </summary>
    private Vector3 ResolveDestination(Vector3 target, float flatDist)
    {
        var currentY = this.PlayerPosition.Y;

        var floor = this.QueryFloorY(target);
        if (floor.HasValue)
        {
            // 地板比脚下高很多（柜台面/二楼）就不采信，免得被抬起来。
            if (floor.Value <= currentY + RaiseTolerance)
                return new Vector3(target.X, floor.Value, target.Z);
        }

        if (flatDist <= SameRoomDistance)
            return new Vector3(target.X, currentY, target.Z);

        return target;
    }

    /// <summary>查目标点附近的地面高度，查不到返回 null。</summary>
    private float? QueryFloorY(Vector3 target)
    {
        var currentY = this.PlayerPosition.Y;

        try
        {
            if (this.navReady != null && !this.navReady.InvokeFunc())
                return null;

            // 只信「和脚下差不多高」的结果：室内网格很小，偶尔会返回离谱的值，
            // 直接采用会把角色送上天。
            var floor = this.pointOnFloor?.InvokeFunc(target, true, 3f);
            if (floor.HasValue && MathF.Abs(floor.Value.Y - currentY) <= FloorSanityLimit)
                return floor.Value.Y;

            var near = this.nearestPoint?.InvokeFunc(target, 3f, 3f);
            if (near.HasValue && MathF.Abs(near.Value.Y - currentY) <= FloorSanityLimit)
                return near.Value.Y;

            return null;
        }
        catch
        {
            // vnavmesh 没装/没就绪：走后面的兜底分支
            return null;
        }
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private DateTime settleUntil = DateTime.MinValue;
    private Vector3 lastDestination;
    private DateTime lastDestinationAt = DateTime.MinValue;

    private Vector3 jumpTarget;
    private DateTime jumpAt = DateTime.MinValue;
    private int jumpCount;

    /// <summary>同一个落点最多连续瞬移几次（超过判定为不可达）。</summary>
    private const int MaxJumpsPerTarget = 5;

    /// <summary>上次瞬移落点（诊断用）。</summary>
    public (Vector3 Destination, DateTime At) LastTeleport => (this.lastDestination, this.lastDestinationAt);

    /// <summary>
    /// 这个距离以内视为「同一房间」，落点沿用脚下高度。
    /// 房屋大厅到房门、工房入口到面板都在这个范围内，同一层地面高度一致，
    /// 沿用脚下 Y 就不会被门牌/面板这些挂在墙上的物件吊到半空。
    /// </summary>
    public const float SameRoomDistance = 40f;

    /// <summary>查某个点附近的地板高度（诊断用，查不到返回 null）。</summary>
    public float? FloorYAt(Vector3 position) => this.QueryFloorY(position);

    /// <summary>地板高度与脚下高度差超过这个值就不采信（防止把角色送上天）。</summary>
    public const float FloorSanityLimit = 3f;

    /// <summary>同一房间内，允许用目标物件的高度纠正脚下滑落的范围。</summary>
    public const float FloorSnapRange = 2.5f;

    /// <summary>地板比脚下高过这个值就不采信（柜台面、二楼地板）。</summary>
    public const float RaiseTolerance = 0.35f;

    /// <summary>单次瞬移允许的最大水平距离（同一区域内）。部队房区两个门之间可能隔上百米。</summary>
    public const float MaxTeleportStep = 200f;

    public uint Territory => this.clientState.TerritoryType;
}
