using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace SubOps;

/// <summary>
/// 移动与交互。
/// 移动走 vnavmesh 的 IPC（没装就退化为「只走近距离」），交互直接调用游戏的 TargetSystem。
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

    /// <summary>走到对象身边；已经在范围内返回 true。</summary>
    public bool Approach(IGameObject obj, float range)
    {
        if (obj == null || !obj.IsValid())
            return false;

        if (this.DistanceTo(obj.Position) <= range)
            return true;

        if (!this.IsMoving())
            this.MoveTo(obj.Position);

        return false;
    }

    public uint Territory => this.clientState.TerritoryType;
}
