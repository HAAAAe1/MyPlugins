using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace SubOps;

/// <summary>
/// 配置窗口。
///
/// 卫月只会在「插件订阅了 UiBuilder.OpenConfigUi」时才显示设置按钮
/// （判据是 UiBuilder.HasConfigUi => OpenConfigUi != null），
/// 所以这里必须由 Plugin 订阅该事件，光有窗口不订阅是看不到按钮的。
/// </summary>
public sealed class ConfigUi
{
    private readonly Configuration config;
    private readonly CharacterBook book;
    private readonly CharacterSwitcher switcher;
    private readonly SubmarineRunner runner;
    private readonly Navigator nav;
    private readonly IDalamudPluginInterface pi;

    /// <summary>实时数据的刷新间隔，避免每帧都读内存。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private bool open;
    private DateTime nextRefresh = DateTime.MinValue;
    private List<Workshop.Submarine> live = new();
    private Dictionary<int, List<Workshop.Part>> parts = new();
    private int ceruleum;
    private int repairMaterial;

    public ConfigUi(
        Configuration config,
        CharacterBook book,
        CharacterSwitcher switcher,
        SubmarineRunner runner,
        Navigator nav,
        IDalamudPluginInterface pi)
    {
        this.config = config;
        this.book = book;
        this.switcher = switcher;
        this.runner = runner;
        this.nav = nav;
        this.pi = pi;
    }

    public bool IsOpen => this.open;

    public void Toggle() => this.open = !this.open;

    public void Draw()
    {
        if (!this.open)
            return;

        ImGui.SetNextWindowSize(new Vector2(780, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("SubOps 配置", ref this.open, ImGuiWindowFlags.None))
        {
            ImGui.End();
            return;
        }

        var dirty = false;
        var now = DateTime.UtcNow;

        this.DrawStatus(now);
        ImGui.Spacing();

        if (ImGui.BeginTabBar("subops-tabs"))
        {
            if (ImGui.BeginTabItem("潜水艇名册"))
            {
                dirty |= this.DrawRoster(now);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("当前角色"))
            {
                this.DrawCurrent();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("行为"))
            {
                dirty |= this.DrawBehavior();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("校准"))
            {
                dirty |= this.DrawCalibration();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (dirty)
            this.pi.SavePluginConfig(this.config);

        ImGui.End();
    }

    // ───────────────────────── 顶部状态 ─────────────────────────

    private void DrawStatus(DateTime now)
    {
        var running = this.runner.Busy || this.switcher.Active;

        ImGui.Text("状态：");
        ImGui.SameLine();
        if (running)
            ImGui.TextColored(new Vector4(0.95f, 0.65f, 0.2f, 1f), $"{this.runner.Stage} · {this.runner.StatusText}");
        else
            ImGui.TextDisabled($"{this.runner.Stage} · {this.runner.StatusText}");

        ImGui.SameLine();
        ImGui.TextDisabled($"| 轮换：{this.switcher.StatusText} {this.switcher.Progress}");

        ImGui.BeginDisabled(running);
        if (ImGui.Button("开始一轮（全部角色）"))
        {
            if (this.switcher.StartSweep())
                this.pi.SavePluginConfig(this.config);
        }

        ImGui.SameLine();
        if (ImGui.Button("只收当前角色"))
            this.runner.Start();
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!running);
        if (ImGui.Button("停止"))
        {
            this.switcher.Abort();
            this.runner.Stop();
            this.pi.SavePluginConfig(this.config);
        }

        ImGui.EndDisabled();

        if (!this.switcher.CanSwitch)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.4f, 1f), "没检测到 Lifestream，切角色用不了（单角色收取不受影响）。");
        }

        ImGui.Separator();
    }

    // ───────────────────────── 名册 ─────────────────────────

    /// <summary>这就是「登记了哪些」：每个角色底下挂着它 4 个槽位的等级和返航时间。</summary>
    private bool DrawRoster(DateTime now)
    {
        var dirty = false;

        if (this.config.Characters.Count == 0)
        {
            ImGui.TextDisabled("名册是空的。每个角色登录一次就会自动登记，进一次工房才会存下潜水艇数据。");
            return false;
        }

        ImGui.TextDisabled($"共 {this.config.Characters.Count} 个角色，轮换按下面这个顺序走。改任何一项都会立刻保存。");
        ImGui.Spacing();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersH | ImGuiTableFlags.BordersV;

        if (!ImGui.BeginTable("subops-roster", 6, flags))
            return false;

        ImGui.TableSetupColumn("角色", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("启用");
        ImGui.TableSetupColumn("4 个槽位（等级 / 返航）", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("数据时间");
        ImGui.TableSetupColumn("冷却");
        ImGui.TableSetupColumn("操作");
        ImGui.TableHeadersRow();

        var removeIndex = -1;
        var moveFirstIndex = -1;
        var nowUnix = now.ToUnix();

        for (var i = 0; i < this.config.Characters.Count; i++)
        {
            var profile = this.config.Characters[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            // 角色
            ImGui.TableNextColumn();
            var isCurrent = profile.ContentId == this.book.Current?.ContentId;
            ImGui.Text(isCurrent ? $"{i + 1}. ★ {profile.Key}" : $"{i + 1}. {profile.Key}");
            if (isCurrent)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("（当前）");
            }

            // 启用
            ImGui.TableNextColumn();
            var enabled = profile.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                profile.Enabled = enabled;
                dirty = true;
            }

            // 槽位
            ImGui.TableNextColumn();
            ImGui.Text(this.SlotsText(profile, nowUnix));

            // 快照时间
            ImGui.TableNextColumn();
            if (profile.SnapshotAt == 0)
                ImGui.TextDisabled("未采集");
            else
                ImGui.Text(DateTimeExt.FromUnix(profile.SnapshotAt).ToLocalTime().ToString("MM-dd HH:mm"));

            // 冷却
            ImGui.TableNextColumn();
            if (profile.OnCooldown(now))
                ImGui.TextColored(
                    new Vector4(0.95f, 0.65f, 0.2f, 1f),
                    DateTimeExt.FromUnix(profile.CooldownUntil).ToLocalTime().ToString("HH:mm"));
            else
                ImGui.TextDisabled("—");

            // 操作
            ImGui.TableNextColumn();
            if (i > 0 && ImGui.SmallButton("置顶"))
                moveFirstIndex = i;

            ImGui.SameLine();
            if (ImGui.SmallButton("移除"))
                removeIndex = i;

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (moveFirstIndex > 0)
        {
            var profile = this.config.Characters[moveFirstIndex];
            this.config.Characters.RemoveAt(moveFirstIndex);
            this.config.Characters.Insert(0, profile);
            dirty = true;
        }

        if (removeIndex >= 0)
        {
            this.config.Characters.RemoveAt(removeIndex);
            dirty = true;
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(!this.config.Characters.Any(p => p.OnCooldown(now)));
        if (ImGui.Button("清空所有冷却"))
        {
            this.switcher.ResetCooldowns();
            dirty = true;
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("切号失败后角色会被冷却，这里可以手动放出来。");

        return dirty;
    }

    /// <summary>把 4 个槽位拼成一行：<c>1:Lv40 10-25 08:30</c>，没启用的槽位显示 ——。</summary>
    private string SlotsText(CharacterProfile profile, long nowUnix)
    {
        if (profile.SnapshotAt == 0)
            return "未采集（还没进过工房）";

        if (profile.Vessels.Count == 0)
            return "没有数据";

        var pieces = profile.Vessels
            .OrderBy(v => v.Slot)
            .Select(v =>
            {
                if (v.Rank == 0)
                    return $"{v.Slot + 1}:—";

                var back = v.ReturnAt <= nowUnix;
                var when = back
                    ? "已回港"
                    : DateTimeExt.FromUnix(v.ReturnAt).ToLocalTime().ToString("MM-dd HH:mm");
                return $"{v.Slot + 1}:Lv{v.Rank} {when}";
            });

        return string.Join("   ", pieces);
    }

    // ───────────────────────── 当前角色 ─────────────────────────

    private void DrawCurrent()
    {
        var current = this.book.Current;
        if (current == null)
        {
            ImGui.TextDisabled("还没读到当前角色（未登录或正在加载）。");
            return;
        }

        ImGui.Text($"当前角色：{current.Key}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| 名册第 {this.config.Characters.FindIndex(p => p.ContentId == current.ContentId) + 1} 位");

        ImGui.TextDisabled($"在工房：{(Workshop.InWorkshop ? "是" : "否")}   在房屋内：{(Workshop.InHouse ? "是" : "否")}");
        ImGui.Separator();

        this.RefreshLive();

        if (this.live.Count == 0)
        {
            ImGui.TextDisabled("读不到潜水艇：只有站在部队工房里才拿得到数据。");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersH | ImGuiTableFlags.BordersV;
        if (!ImGui.BeginTable("subops-live", 5, flags))
            return;

        ImGui.TableSetupColumn("槽位");
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("等级");
        ImGui.TableSetupColumn("返航");
        ImGui.TableSetupColumn("零件耐久", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var sub in this.live.Where(s => s.Enabled))
        {
            ImGui.TableNextRow();
            ImGui.PushID(sub.Slot);

            ImGui.TableNextColumn();
            ImGui.Text($"{sub.Slot + 1} 号");

            ImGui.TableNextColumn();
            if (sub.IsBack)
                ImGui.TextColored(new Vector4(0.45f, 0.8f, 0.45f, 1f), "已返航，可收取");
            else
                ImGui.Text($"探索中");

            ImGui.TableNextColumn();
            ImGui.Text($"Lv{sub.Rank}");

            ImGui.TableNextColumn();
            ImGui.Text(sub.IsBack ? "—" : sub.ReturnTime.ToLocalTime().ToString("MM-dd HH:mm"));

            ImGui.TableNextColumn();
            ImGui.Text(this.PartsText(sub.Slot));

            ImGui.PopID();
        }

        ImGui.EndTable();

        ImGui.Spacing();
        ImGui.Text($"材料：桶装青磷水 {this.ceruleum} · 魔导机械修理材料 {this.repairMaterial}");
        if (this.repairMaterial < 15)
            ImGui.TextColored(new Vector4(0.95f, 0.65f, 0.2f, 1f), "修理材料偏少（建议 ≥15），自动修理可能会跳过。");
    }

    private string PartsText(int slot)
    {
        if (!this.parts.TryGetValue(slot, out var list) || list.Count == 0)
            return "—";

        return string.Join("  ", list.Select(p =>
            p.Damaged ? $"{p.Slot + 1}:坏" : $"{p.Slot + 1}:{p.Condition}%"));
    }

    private void RefreshLive()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextRefresh)
            return;

        this.nextRefresh = now.Add(RefreshInterval);

        this.live = Workshop.Read();
        (this.ceruleum, this.repairMaterial) = Workshop.ReadMaterials();

        this.parts = new Dictionary<int, List<Workshop.Part>>();
        foreach (var sub in this.live.Where(s => s.Enabled))
            this.parts[sub.Slot] = Workshop.ReadParts(sub.Slot);
    }

    // ───────────────────────── 行为 ─────────────────────────

    private bool DrawBehavior()
    {
        var dirty = false;

        var redist = this.config.ReDispatch;
        if (ImGui.Checkbox("收取完成后按原航线再次出航", ref redist))
        {
            this.config.ReDispatch = redist;
            dirty = true;
        }

        var repair = this.config.AutoRepair;
        if (ImGui.Checkbox("出航前自动修理耐久为 0 的零件", ref repair))
        {
            this.config.AutoRepair = repair;
            dirty = true;
        }

        ImGui.TextDisabled("每个零件消耗 1 个魔导机械修理材料，材料不够会自动跳过。");
        ImGui.Separator();

        ImGui.Text("多角色轮换");
        var skip = this.config.MultiSkipIdle;
        if (ImGui.Checkbox("跳过「确定没返航」的角色", ref skip))
        {
            this.config.MultiSkipIdle = skip;
            dirty = true;
        }

        ImGui.TextDisabled("默认关：每个角色都进工房看一眼。开了靠上次快照判断，快但可能漏收。");

        var logout = this.config.MultiLogoutWhenDone;
        if (ImGui.Checkbox("全部收完后登出到标题界面", ref logout))
        {
            this.config.MultiLogoutWhenDone = logout;
            dirty = true;
        }

        var advance = this.config.MultiAdvanceSeconds;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("提前量（秒）", ref advance, 0, 3600))
        {
            this.config.MultiAdvanceSeconds = advance;
            dirty = true;
        }

        ImGui.TextDisabled("返航时间落在这个范围内就算该去收了，只在上面「跳过」开着时生效。");

        return dirty;
    }

    // ───────────────────────── 校准 ─────────────────────────

    private bool DrawCalibration()
    {
        var dirty = false;

        ImGui.TextDisabled("工房是独立副本房间，坐标全局一致，已内置默认值，正常不用动。");
        ImGui.TextDisabled("只有找错了（比如自定义装修挡路）才需要站到目标前点「记录当前位置」覆盖。");
        ImGui.Spacing();

        dirty |= this.DrawAnchor("房门锚点", this.config.DoorAnchor, this.config.HasDoorAnchor,
            Configuration.BuiltinDoorAnchor, () => this.config.SetDoorAnchor(this.CurrentAnchor()));
        dirty |= this.DrawAnchor("面板锚点", this.config.PanelAnchor, this.config.HasPanelAnchor,
            null, () => this.config.SetPanelAnchor(this.CurrentAnchor()));

        ImGui.Separator();
        ImGui.Text("菜单下标（默认值按国服现行界面，认错了就改这里）");

        var room = this.config.RoomMenuIndex;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("「其他房间」里工房那一项", ref room))
        {
            this.config.RoomMenuIndex = Math.Clamp(room, 0, 9);
            dirty = true;
        }

        var panel = this.config.PanelMenuIndex;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("面板里「管理潜水艇」那一项", ref panel))
        {
            this.config.PanelMenuIndex = Math.Clamp(panel, 0, 9);
            dirty = true;
        }

        return dirty;
    }

    private bool DrawAnchor(string label, Vector3 value, bool recorded, Vector3? builtin, Action record)
    {
        ImGui.Text($"{label}：");
        ImGui.SameLine();
        ImGui.Text($"({value.X:F2}, {value.Y:F2}, {value.Z:F2})");

        if (recorded)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(手动覆盖)");
        }
        else if (builtin != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(内置默认)");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(未设置，按名字自动找)");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"记录当前位置##{label}"))
        {
            record();
            return true;
        }

        if (recorded)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"恢复默认##{label}"))
            {
                if (label.StartsWith("房门", StringComparison.Ordinal))
                    this.config.SetDoorAnchor(Vector3.Zero);
                else
                    this.config.SetPanelAnchor(Vector3.Zero);
                return true;
            }
        }

        return false;
    }

    /// <summary>就近找一个可交互物件当锚点，找不到就用脚下的位置。</summary>
    private Vector3 CurrentAnchor()
        => this.nav.FindEventObject(0, this.nav.PlayerPosition, 6f)?.Position ?? this.nav.PlayerPosition;
}
