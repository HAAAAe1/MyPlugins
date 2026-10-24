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

    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?>? pointOnFloor;
    private readonly ICallGateSubscriber<bool>? navReady;
    private readonly ICallGateSubscriber<object>? pathStop;
    private readonly ICallGateSubscriber<bool>? pathRunning;

    private DateTime settleUntil = DateTime.MinValue;

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
    /// 返回 true ＝ 已经在 <paramref name="standOff"/> 米内，可以交互了；
    /// 返回 false ＝ 这一拍刚挪过去，下一拍再看。
    /// </summary>
    public bool Approach(Vector3 target, float standOff = InteractRange, int attempt = 0)
    {
        var player = this.Player;
        if (player == null)
            return false;

        // 刚瞬移完的那一小会儿，等客户端站稳，别再挪。
        if (DateTime.UtcNow < this.settleUntil)
            return false;

        var pos = player.Position;

        if (Flat(pos, target) <= standOff)
            return true;

        var dx = pos.X - target.X;
        var dz = pos.Z - target.Z;
        var length = MathF.Sqrt((dx * dx) + (dz * dz));

        // 站在目标上（长度≈0）时没方向可用，从正面开始，失败再按 90° 转。
        var angle = length < 0.05f ? 0f : MathF.Atan2(dx, dz);
        angle += attempt * (MathF.PI / 2f);

        var landing = new Vector3(
            target.X + (MathF.Sin(angle) * standOff),
            target.Y,
            target.Z + (MathF.Cos(angle) * standOff));

        this.Teleport(landing, false);
        return false;
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
