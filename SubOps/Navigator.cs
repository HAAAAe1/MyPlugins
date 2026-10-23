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
/// 主用「平滑瞬移」（逐帧把角色坐标推向目标，和 DailyRoutines 的 TPSmooth 同原理），
/// 因为房屋/工房属于室内区域，vnavmesh 几乎没有可用网格，寻路在里面必失败。
/// vnavmesh 只在距离较远的室外场景作为备选。
/// </summary>
public sealed unsafe class Navigator
{
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<Vector3, bool, object>? moveTo;
    private readonly ICallGateSubscriber<bool>? isRunning;
    private readonly ICallGateSubscriber<object>? stop;

    public Navigator(IDalamudPluginInterface pluginInterface, IObjectTable objects, ITargetManager targets, IClientState clientState, IPluginLog log)
    {
        this.objects = objects;
        this.targets = targets;
        this.clientState = clientState;
        this.log = log;

        try
        {
            this.moveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, object>("vnavmesh.SimpleMove.PathfindAndMoveTo");
            this.isRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            this.stop = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
            this.HasVnavmesh = true;
        }
        catch (Exception ex)
        {
            this.HasVnavmesh = false;
            this.log.Warning(ex, "未找到 vnavmesh，移动能力受限");
        }
    }

    public bool HasVnavmesh { get; }

    public IGameObject? Player => this.objects.LocalPlayer;

    public Vector3 PlayerPosition => this.objects.LocalPlayer?.Position ?? Vector3.Zero;

    public float DistanceTo(Vector3 position) => this.Player == null ? float.MaxValue : Vector3.Distance(this.Player.Position, position);

    /// <summary>寻路到指定点。fly = 是否可以飞（住宅区内一律 false）。</summary>
    public void MoveTo(Vector3 position, bool fly = false)
    {
        if (!this.HasVnavmesh || this.moveTo == null)
            return;

        try
        {
            this.moveTo.InvokeAction(position, fly);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "寻路调用失败");
        }
    }

    public bool IsMoving()
    {
        if (this.isRunning == null)
            return false;

        try
        {
            return this.isRunning.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public void StopMoving()
    {
        try
        {
            this.stop?.InvokeAction();
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
    /// 逐帧把角色推向目标（平滑瞬移）。到达范围返回 true。
    /// 原理同 DailyRoutines 的 TPSmooth：直接写 BattleChara 的位置，每帧一小步，
    /// 服务器看到的就是「正常速度跑过去」，室内没有寻路网格也能走。
    /// </summary>
    public bool SmoothTeleport(Vector3 target, float stopRange, float step = 1.2f)
    {
        var player = this.objects.LocalPlayer;
        if (player == null)
            return false;

        var pos = player.Position;
        var flat = new Vector3(target.X - pos.X, 0f, target.Z - pos.Z);
        var flatDist = flat.Length();
        if (flatDist <= stopRange)
            return true;

        // 只推到「差一点进范围」，避免在目标点上来回抖动。
        var stepLen = MathF.Min(step, flatDist - stopRange * 0.5f);
        if (stepLen <= 0.01f)
            return true;

        var dir = flat / flatDist;
        var ratio = stepLen / flatDist;
        var nextY = pos.Y + (target.Y - pos.Y) * ratio;

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)player.Address;
        if (chara == null)
            return false;

        try
        {
            chara->SetPosition(pos.X + dir.X * stepLen, nextY, pos.Z + dir.Z * stepLen);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "平滑移动失败");
            return false;
        }

        return Vector3.Distance(player.Position, target) <= stopRange;
    }

    /// <summary>靠近目标对象。每帧调一次即可。</summary>
    public ApproachState Approach(IGameObject obj, float range)
    {
        if (obj == null || !obj.IsValid())
            return ApproachState.Blocked;

        var distance = this.DistanceTo(obj.Position);
        if (distance <= range)
        {
            this.StopMoving();
            this.approachTargetId = 0;
            return ApproachState.Arrived;
        }

        // 超过 60 米说明不在同一个院子/房间里，硬推会穿图，必须先传送。
        if (distance > MaxTeleportStep)
            return ApproachState.TooFar;

        // 同一个目标连推 10 秒还没靠近 1 米，说明推不动（被事件锁定、位置被覆盖等），
        // 直接判定失败，别在原地空转。
        var id = obj.GameObjectId;
        if (this.approachTargetId != id)
        {
            this.approachTargetId = id;
            this.approachBestDistance = distance;
            this.approachProgressAt = DateTime.UtcNow;
        }
        else if (distance < this.approachBestDistance - 1f)
        {
            this.approachBestDistance = distance;
            this.approachProgressAt = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - this.approachProgressAt).TotalSeconds > 10)
        {
            this.approachTargetId = 0;
            return ApproachState.Blocked;
        }

        return this.SmoothTeleport(obj.Position, range) ? ApproachState.Arrived : ApproachState.Moving;
    }

    private ulong approachTargetId;
    private float approachBestDistance = float.MaxValue;
    private DateTime approachProgressAt = DateTime.UtcNow;

    /// <summary>单次推进的最大允许距离，防止跨区域乱飞。</summary>
    public const float MaxTeleportStep = 60f;

    public uint Territory => this.clientState.TerritoryType;
}
