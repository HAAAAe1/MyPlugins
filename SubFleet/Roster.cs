using System;
using System.Linq;
using Dalamud.Plugin.Services;

namespace SubFleet;

/// <summary>
/// 角色名册：认出当前角色、自动入册、在工房里顺手拍一张潜艇快照。
///
/// 为什么要快照：潜艇返航时间只存在于当前角色的 HousingManager 里，
/// 不站在工房就读不到。想让插件判断「隔壁角色要不要去」，只能自己存一份。
/// 但快照只用来显示和可选跳过，绝不拿它当「不用去」的硬依据。
/// </summary>
public sealed class Roster
{
    private readonly Config config;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Action save;

    private DateTime lastSnapshotAt = DateTime.MinValue;

    /// <summary>上一次记进日志的角色，避免每帧刷同一条。</summary>
    private ulong lastLoggedInContentId;

    public Roster(Config config, IClientState clientState, IPlayerState playerState, IChatGui chat, IPluginLog log, Action save)
    {
        this.config = config;
        this.clientState = clientState;
        this.playerState = playerState;
        this.chat = chat;
        this.log = log;
        this.save = save;
    }

    public CharacterEntry? Current { get; private set; }

    public bool LoggedIn => this.clientState.IsLoggedIn && this.playerState.ContentId != 0;

    public void Tick()
    {
        if (!this.LoggedIn)
        {
            this.Current = null;
            this.lastLoggedInContentId = 0;
            return;
        }

        var name = this.playerState.CharacterName;
        var world = this.WorldName();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return;

        var cid = this.playerState.ContentId;
        var entry = this.config.Find(cid);

        if (entry == null)
        {
            entry = new CharacterEntry { ContentId = cid, Name = name, World = world };
            this.config.Roster.Add(entry);
            this.log.Information($"[SubFleet] 角色入册：{entry.Key}");
            this.chat.Print($"[SubFleet] 已记录角色 {entry.Key}");
            this.save();
        }
        else if (entry.Name != name || entry.World != world)
        {
            entry.Name = name;
            entry.World = world;
        }

        this.Current = entry;

        // 日志里带上名字，出问题时一眼看出是哪一站：切号之后只有「已登录」三个字根本对不上人。
        if (this.lastLoggedInContentId != cid)
        {
            this.lastLoggedInContentId = cid;
            this.log.Information($"[SubFleet] 当前角色：{entry.Key}");
        }

        if (Workshop.InWorkshop && DateTime.UtcNow - this.lastSnapshotAt > TimeSpan.FromSeconds(10))
        {
            this.lastSnapshotAt = DateTime.UtcNow;
            this.Snapshot(entry);
        }
    }

    public void Snapshot(CharacterEntry entry)
    {
        var subs = Workshop.Read();
        if (subs.Count == 0)
            return;

        entry.Vessels = subs
            .Select(s => new VesselSnapshot
            {
                Slot = s.Slot,
                Rank = s.Rank,
                ReturnAt = CharacterEntry.ToUnix(s.ReturnTime),
            })
            .ToList();

        entry.SnapshotAt = CharacterEntry.ToUnix(DateTime.UtcNow);
        this.save();
    }

    private string WorldName()
    {
        var world = this.playerState.HomeWorld;
        if (world.RowId == 0)
            return string.Empty;

        return world.Value.Name.ExtractText();
    }
}
