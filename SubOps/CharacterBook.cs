using System;
using System.Linq;
using Dalamud.Plugin.Services;

namespace SubOps;

/// <summary>
/// 角色名册：识别当前角色、自动登记、在工房里给潜水艇拍快照。
///
/// 为什么要快照：潜水艇的返航时间只存在于当前角色的 HousingManager 里，
/// 不站在工房就读不到。想让插件判断「隔壁角色该不该去」，只能靠自己存一份。
/// </summary>
public sealed class CharacterBook
{
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    private DateTime lastSnapshot = DateTime.MinValue;

    public CharacterBook(
        Configuration config,
        IClientState clientState,
        IPlayerState playerState,
        IChatGui chat,
        IPluginLog log)
    {
        this.config = config;
        this.clientState = clientState;
        this.playerState = playerState;
        this.chat = chat;
        this.log = log;
    }

    /// <summary>当前登录的角色档案；没登录或还没读到名字时为 null。</summary>
    public CharacterProfile? Current { get; private set; }

    public bool LoggedIn => this.clientState.IsLoggedIn && this.playerState.ContentId != 0;

    public void Tick()
    {
        if (!this.LoggedIn)
        {
            this.Current = null;
            return;
        }

        var name = this.playerState.CharacterName;
        var world = this.CurrentWorldName();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(world))
            return;

        var cid = this.playerState.ContentId;
        var profile = this.config.Characters.FirstOrDefault(c => c.ContentId == cid);

        if (profile == null)
        {
            profile = new CharacterProfile { ContentId = cid, Name = name, World = world };
            this.config.Characters.Add(profile);
            this.log.Information($"[SubOps] 新角色入册：{profile.Key}");
            this.chat.Print($"[SubOps] 已记录角色 {profile.Key}，用 /subops char list 查看名册");
        }
        else if (profile.Name != name || profile.World != world)
        {
            // 改名 / 转服
            profile.Name = name;
            profile.World = world;
        }

        this.Current = profile;

        // 站在工房里就顺手拍一张快照，8 秒一次，避免每帧读内存。
        if (Workshop.InWorkshop && DateTime.UtcNow - this.lastSnapshot > TimeSpan.FromSeconds(8))
        {
            this.lastSnapshot = DateTime.UtcNow;
            this.Snapshot(profile);
        }
    }

    /// <summary>立刻拍一次快照。收完潜艇后调用，把新的返航时间写进档案。</summary>
    public void Snapshot(CharacterProfile profile)
    {
        var subs = Workshop.Read();
        if (subs.Count == 0)
            return;

        profile.Vessels = subs
            .Select(s => new VesselSnapshot { Slot = s.Slot, Rank = s.Rank, ReturnAt = s.ReturnTime.ToUnix() })
            .ToList();
        profile.SnapshotAt = DateTime.UtcNow.ToUnix();
    }

    public CharacterProfile? Find(string name)
        => this.config.Characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>名字或「名@服务器」都能查到。</summary>
    public CharacterProfile? FindByKey(string key) => this.Find(key);

    private string CurrentWorldName()
    {
        var world = this.playerState.HomeWorld;
        if (world.RowId == 0)
            return string.Empty;

        return world.Value.Name.ExtractText();
    }
}
