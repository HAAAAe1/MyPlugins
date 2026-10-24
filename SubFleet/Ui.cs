using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace SubFleet;

/// <summary>主窗口：状态 + 按钮 + 名册。</summary>
public sealed class Ui
{
    private readonly Config config;
    private readonly Collector collector;
    private readonly Sweep sweep;
    private readonly Roster roster;
    private readonly Action save;

    private bool open;

    /// <summary>浮动图标按下时的鼠标位置，用来区分「点一下」和「拖走」。</summary>
    private Vector2 floatPressPos;

    private static readonly Vector2 FloatIconSize = new(52f, 40f);

    public Ui(Config config, Collector collector, Sweep sweep, Roster roster, Action save)
    {
        this.config = config;
        this.collector = collector;
        this.sweep = sweep;
        this.roster = roster;
        this.save = save;
    }

    public bool IsOpen => this.open;

    public void Toggle() => this.open = !this.open;

    public void Draw()
    {
        this.DrawFloatIcon();

        if (!this.open)
            return;

        ImGui.SetNextWindowSize(new Vector2(620, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("SubFleet 潜水艇", ref this.open, ImGuiWindowFlags.None))
        {
            ImGui.End();
            return;
        }

        var dirty = false;

        this.DrawStatus();
        ImGui.Spacing();
        this.DrawButtons();
        ImGui.Separator();

        dirty |= this.DrawRoster();
        ImGui.Separator();
        this.DrawLive();

        ImGui.Spacing();

        var autoRepair = this.config.AutoRepair;
        if (ImGui.Checkbox("自动修理零件", ref autoRepair))
        {
            this.config.AutoRepair = autoRepair;
            dirty = true;
        }

        var reDispatch = this.config.ReDispatch;
        if (ImGui.Checkbox("收完再次出航", ref reDispatch))
        {
            this.config.ReDispatch = reDispatch;
            dirty = true;
        }

        var logoutWhenDone = this.config.SweepLogoutWhenDone;
        if (ImGui.Checkbox("全部完成后登出", ref logoutWhenDone))
        {
            this.config.SweepLogoutWhenDone = logoutWhenDone;
            dirty = true;
        }

        var skipIdle = this.config.SweepSkipIdle;
        if (ImGui.Checkbox("跳过确定没活可干的角色", ref skipIdle))
        {
            this.config.SweepSkipIdle = skipIdle;
            dirty = true;
        }

        var verbose = this.config.VerboseChat;
        if (ImGui.Checkbox("聊天框汇报", ref verbose))
        {
            this.config.VerboseChat = verbose;
            dirty = true;
        }

        var floatIcon = this.config.ShowFloatIcon;
        if (ImGui.Checkbox("显示浮动图标（可拖动）", ref floatIcon))
        {
            this.config.ShowFloatIcon = floatIcon;
            dirty = true;
        }

        if (dirty)
            this.save();

        ImGui.End();
    }

    /// <summary>
    /// 屏幕上那个小图标：不用进卫月设置，点一下就能开关主窗口，拖到哪记到哪。
    ///
    /// 用画布自己画，不用 ImGui.Button——按钮会把整块区域吃掉，就没地方按着拖了。
    /// 判断「点一下」还是「拖走」：按下和松开的位置差 4 像素以内才算点击。
    /// </summary>
    private void DrawFloatIcon()
    {
        if (!this.config.ShowFloatIcon)
            return;

        ImGui.SetNextWindowPos(this.config.FloatIconPos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(FloatIconSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus
            | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        var shown = ImGui.Begin("##subfleet-float", flags);
        ImGui.PopStyleVar(2);

        if (shown)
        {
            var p0 = ImGui.GetCursorScreenPos();
            var p1 = p0 + FloatIconSize;
            var draw = ImGui.GetWindowDrawList();

            // 配色跟状态走：运行中绿、轮换中蓝、完成灰蓝、待机深灰
            var fill = this.collector.Running
                ? new Vector4(0.15f, 0.45f, 0.22f, 0.92f)
                : this.sweep.Phase is SweepPhase.Running or SweepPhase.Switching
                    ? new Vector4(0.16f, 0.30f, 0.55f, 0.92f)
                    : this.sweep.Phase == SweepPhase.Done
                        ? new Vector4(0.24f, 0.28f, 0.34f, 0.92f)
                        : new Vector4(0.16f, 0.16f, 0.19f, 0.85f);

            draw.AddRectFilled(p0, p1, ImGui.ColorConvertFloat4ToU32(fill), 6f);
            draw.AddRect(p0, p1, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.18f)), 6f);

            var label = this.open ? "艇 ×" : "艇";
            var textSize = ImGui.CalcTextSize(label);
            draw.AddText(p0 + ((FloatIconSize - textSize) * 0.5f), ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)), label);

            var hovered = ImGui.IsWindowHovered();
            if (hovered)
            {
                draw.AddRect(p0, p1, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.55f)), 6f);

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    this.floatPressPos = ImGui.GetMousePos();

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)
                    && Vector2.Distance(this.floatPressPos, ImGui.GetMousePos()) < 4f)
                    this.open = !this.open;

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    this.collector.Start();

                ImGui.SetTooltip(
                    (this.collector.Running ? $"SubFleet：{this.collector.Status}" : "SubFleet 潜水艇")
                    + "\n左键：打开/收起窗口\n右键：收取当前角色\n按住拖动：换位置");
            }
        }

        ImGui.End();

        // 拖到哪记到哪（松手时存一次，别每帧写配置）
        var pos = ImGui.GetWindowPos();
        if (Vector2.Distance(pos, this.config.FloatIconPos) > 0.5f)
        {
            this.config.FloatIconPos = pos;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                this.save();
        }
    }

    private void DrawStatus()
    {
        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), "状态");

        ImGui.TextUnformatted($"收取：{this.collector.Status}");
        if (!string.IsNullOrEmpty(this.collector.StepText))
            ImGui.TextDisabled($"　{this.collector.StepText}");

        ImGui.TextUnformatted($"轮换：{this.sweep.StatusText}");
        if (this.sweep.Phase != SweepPhase.Off && !string.IsNullOrEmpty(this.sweep.Progress))
            ImGui.TextDisabled($"　进度 {this.sweep.Progress}");

        if (!this.sweep.CanSwitch)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "未检测到 Lifestream，切角色不可用");
    }

    private void DrawButtons()
    {
        var busy = this.collector.Running;

        if (busy)
            ImGui.BeginDisabled();

        if (ImGui.Button("收取当前角色", new Vector2(130, 0)))
            this.collector.Start();

        if (busy)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (this.config.SweepActive)
            ImGui.BeginDisabled();

        if (ImGui.Button("开始轮换", new Vector2(110, 0)))
            this.sweep.Start();

        if (this.config.SweepActive)
            ImGui.EndDisabled();

        ImGui.SameLine();

        if (ImGui.Button("停止", new Vector2(80, 0)))
        {
            this.sweep.Stop();
            this.collector.Stop();
        }

        ImGui.SameLine();

        if (ImGui.Button("清冷却", new Vector2(80, 0)))
            this.sweep.ResetCooldowns();
    }

    private bool DrawRoster()
    {
        var dirty = false;
        var list = this.config.Roster;

        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), $"名册（{this.config.EnabledCount}/{list.Count} 启用）");
        ImGui.SameLine();
        ImGui.TextDisabled("顺序 = 轮换顺序");

        if (list.Count == 0)
        {
            ImGui.TextDisabled("还没有角色。用每个角色登录一次就会自动入册。");
            return false;
        }

        // 一键排序：返航时间近的排前面；还没进过工房的排最后（没数据，无从判断）
        if (ImGui.SmallButton("按返航时间排序"))
        {
            this.SortByReturn();
            dirty = true;
        }

        ImGui.SameLine();

        var selfIndex = this.roster.Current == null ? -1 : list.FindIndex(x => x.ContentId == this.roster.Current.ContentId);
        if (selfIndex <= 0)
            ImGui.BeginDisabled();

        if (ImGui.SmallButton("当前角色置顶") && selfIndex > 0)
        {
            this.MoveEntry(selfIndex, 0);
            dirty = true;
        }

        if (selfIndex <= 0)
            ImGui.EndDisabled();

        if (!ImGui.BeginTable("subfleet-roster", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return dirty;

        ImGui.TableSetupColumn("次序", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableSetupColumn("角色", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var now = DateTime.UtcNow;
        var moveFrom = -1;
        var moveTo = -1;

        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.PushID(i);

            if (i == 0)
                ImGui.BeginDisabled();

            if (ImGui.ArrowButton("##up", ImGuiDir.Up))
            {
                moveFrom = i;
                moveTo = i - 1;
            }

            if (i == 0)
                ImGui.EndDisabled();

            if (ImGui.IsItemHovered() && i > 0)
                ImGui.SetTooltip("上移（越靠前越先轮换到）");

            ImGui.SameLine();

            if (i == list.Count - 1)
                ImGui.BeginDisabled();

            if (ImGui.ArrowButton("##down", ImGuiDir.Down))
            {
                moveFrom = i;
                moveTo = i + 1;
            }

            if (i == list.Count - 1)
                ImGui.EndDisabled();

            if (ImGui.IsItemHovered() && i < list.Count - 1)
                ImGui.SetTooltip("下移");

            ImGui.PopID();

            ImGui.TableNextColumn();
            var isCurrent = this.roster.Current?.ContentId == entry.ContentId;
            ImGui.TextUnformatted(isCurrent ? $"▶ {entry.Key}" : entry.Key);

            ImGui.TableNextColumn();
            var enabled = entry.Enabled;
            if (ImGui.Checkbox($"##en{entry.ContentId}", ref enabled))
            {
                entry.Enabled = enabled;
                dirty = true;
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(this.Describe(entry, now));
        }

        ImGui.EndTable();

        // 改列表要等表格画完再做，否则会在遍历中途改集合
        if (moveFrom >= 0 && moveTo >= 0 && moveTo < list.Count)
        {
            this.MoveEntry(moveFrom, moveTo);
            dirty = true;
        }

        return dirty;
    }

    /// <summary>
    /// 移动名册里的一项。轮换进度是按下标记的（<see cref="Config.SweepCursor"/>），
    /// 所以动完必须把游标挪回原来那个角色，否则中途排序会把正在跑的角色跳过去或者重来一遍。
    /// </summary>
    private void MoveEntry(int from, int to)
    {
        var list = this.config.Roster;
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to)
            return;

        var cursorCid = this.CursorContentId();

        var moved = list[from];
        list.RemoveAt(from);
        list.Insert(to, moved);

        this.RestoreCursor(cursorCid);
        this.save();
    }

    private void SortByReturn()
    {
        var list = this.config.Roster;
        var cursorCid = this.CursorContentId();

        var ordered = list
            .Select((entry, index) => (entry, index))
            .OrderBy(x => x.entry.NextReturn()?.Ticks ?? long.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.entry)
            .ToList();

        list.Clear();
        list.AddRange(ordered);

        this.RestoreCursor(cursorCid);
        this.save();
    }

    private ulong CursorContentId()
    {
        var list = this.config.Roster;
        if (this.config.SweepCursor < 0 || this.config.SweepCursor >= list.Count)
            return 0;

        return list[this.config.SweepCursor].ContentId;
    }

    private void RestoreCursor(ulong cursorCid)
    {
        var list = this.config.Roster;

        if (cursorCid != 0)
        {
            var index = list.FindIndex(x => x.ContentId == cursorCid);
            if (index >= 0)
                this.config.SweepCursor = index;
        }

        this.config.SweepCursor = Math.Clamp(this.config.SweepCursor, 0, list.Count);
    }

    private string Describe(CharacterEntry entry, DateTime now)
    {
        if (entry.OnCooldown(now))
            return $"冷却到 {CharacterEntry.FromUnix(entry.CooldownUntil).ToLocalTime():HH:mm}";

        if (entry.SnapshotAt == 0)
            return "还没进过工房";

        var next = entry.NextReturn();
        var when = next?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
        return $"{entry.ActiveVessels} 艘 · 下次返航 {when}";
    }

    private void DrawLive()
    {
        if (!Workshop.InWorkshop)
        {
            ImGui.TextDisabled("工房实时数据：不在工房");
            return;
        }

        var subs = Workshop.Read();
        if (subs.Count == 0)
        {
            ImGui.TextDisabled("工房实时数据：读取中…");
            return;
        }

        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), "工房实时数据");

        var now = Workshop.Now;
        foreach (var sub in subs)
        {
            if (!sub.Enabled)
            {
                ImGui.TextDisabled($"  {sub.Slot + 1} 号：空");
                continue;
            }

            var left = sub.ReturnTime - now;
            var text = left.TotalSeconds <= 0
                ? "已返航"
                : $"航行中，还有 {left.Hours:D2}:{left.Minutes:D2}:{left.Seconds:D2}";

            ImGui.TextUnformatted($"  {sub.Slot + 1} 号 {sub.Name}：Lv{sub.Rank} · {text}");
        }

        var (ceruleum, material) = Workshop.Materials();
        ImGui.TextDisabled($"  青磷水 {ceruleum} · 修理材料 {material}");
    }
}
