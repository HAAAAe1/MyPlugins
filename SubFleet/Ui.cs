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

        if (dirty)
            this.save();

        ImGui.End();
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

        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), $"名册（{this.config.EnabledCount}/{this.config.Roster.Count} 启用）");

        if (this.config.Roster.Count == 0)
        {
            ImGui.TextDisabled("还没有角色。用每个角色登录一次就会自动入册。");
            return false;
        }

        if (!ImGui.BeginTable("subfleet-roster", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
            return false;

        ImGui.TableSetupColumn("角色", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var now = DateTime.UtcNow;

        foreach (var entry in this.config.Roster)
        {
            ImGui.TableNextRow();

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
        return dirty;
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
