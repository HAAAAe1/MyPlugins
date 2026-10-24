using System;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SubOps;

/// <summary>
/// 插件入口。命令全部挂在 /subops 下，不依赖任何第三方库，方便自己往上加功能。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string CommandName = "/subops";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IChatGui chat;
    private readonly IGameGui gui;
    private readonly IPluginLog log;

    private readonly Configuration configuration;
    private readonly Navigator navigator;
    private readonly SubmarineRunner runner;
    private readonly CharacterBook characters;
    private readonly CharacterSwitcher switcher;
    private readonly ConfigUi configUi;

    private bool wasRunnerBusy;

    /// <summary>收完之后延迟拍快照的时间点（等游戏把新返航时间写回内存）。</summary>
    private DateTime pendingSnapshotAt = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commands,
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IObjectTable objects,
        IClientState clientState,
        IPlayerState playerState,
        ITargetManager targets,
        IGameGui gui,
        ICondition condition,
        IChatGui chat,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.clientState = clientState;
        this.chat = chat;
        this.gui = gui;
        this.log = log;

        this.configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.configuration.Migrate();
        this.navigator = new Navigator(pluginInterface, objects, targets, clientState, log);
        this.runner = new SubmarineRunner(this.configuration, this.navigator, gui, condition, chat);
        this.characters = new CharacterBook(this.configuration, clientState, playerState, chat, log);
        this.switcher = new CharacterSwitcher(
            this.configuration,
            this.characters,
            this.runner,
            pluginInterface,
            clientState,
            condition,
            chat,
            log);

        this.configUi = new ConfigUi(this.configuration, this.characters, this.switcher, this.runner, this.navigator, pluginInterface);

        this.commands.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "潜水艇作业：/subops | config | run | stop | status | multi on|off | char list | learn door|panel",
        });

        // 卫月判据是 UiBuilder.HasConfigUi => OpenConfigUi != null，
        // 不订阅这个事件，插件列表里根本不会出现齿轮按钮。
        pluginInterface.UiBuilder.OpenConfigUi += this.configUi.Toggle;
        pluginInterface.UiBuilder.OpenMainUi += this.configUi.Toggle;
        pluginInterface.UiBuilder.Draw += this.configUi.Draw;

        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonHelper.ExplorationResult, this.OnResultAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonHelper.SelectYesno, this.OnYesnoAddon);
        this.framework.Update += this.OnUpdate;
        this.clientState.Login += this.OnLogin;
    }

    public string Name => "SubOps";

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.Draw -= this.configUi.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.configUi.Toggle;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.configUi.Toggle;

        this.framework.Update -= this.OnUpdate;
        this.clientState.Login -= this.OnLogin;
        this.addonLifecycle.UnregisterListener(this.OnResultAddon);
        this.addonLifecycle.UnregisterListener(this.OnYesnoAddon);
        this.commands.RemoveHandler(CommandName);
        this.runner.Stop();
        this.pluginInterface.SavePluginConfig(this.configuration);
    }

    private void OnLogin() => this.chat.Print($"[SubOps] 已登录。{this.MultiHint()}");

    private void OnUpdate(IFramework _)
    {
        this.characters.Tick();

        // 刚跑完一轮：等几秒让游戏把新的返航时间写回内存，再写进名册。
        var busy = this.runner.Busy;
        if (this.wasRunnerBusy && !busy)
        {
            this.switcher.NotifyRunFinished();

            if (this.runner.Stage == RunnerStage.Done)
                this.pendingSnapshotAt = DateTime.UtcNow.AddSeconds(3);

            // 跑挂了就别换个角色再撞一遍，先让这个角色冷一会儿。
            if (this.runner.Stage == RunnerStage.Failed && this.characters.Current != null)
                this.characters.Current.CooldownUntil = DateTime.UtcNow.AddMinutes(20).ToUnix();
        }

        this.wasRunnerBusy = busy;

        if (this.pendingSnapshotAt != DateTime.MinValue && DateTime.UtcNow >= this.pendingSnapshotAt)
        {
            this.pendingSnapshotAt = DateTime.MinValue;
            if (this.characters.Current != null)
            {
                this.characters.Snapshot(this.characters.Current);
                this.pluginInterface.SavePluginConfig(this.configuration);
            }
        }

        this.switcher.Tick();
        this.runner.Tick();
    }

    private string MultiHint()
        => this.switcher.Active
            ? $"轮换进行中（{this.switcher.Progress}，{this.switcher.StatusText}）。"
            : "需要收潜水艇请输入 /subops auto（多角色）或 /subops run（只收当前角色）。";

    private void OnResultAddon(AddonEvent type, AddonArgs args) => this.runner.OnExplorationResult();

    private void OnYesnoAddon(AddonEvent type, AddonArgs args) => this.runner.OnSelectYesno();

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (verb)
        {
            case "config":
            case "ui":
            case "settings":
                this.configUi.Toggle();
                break;

            case "run":
                this.runner.Start();
                break;

            case "stop":
                this.switcher.Abort();
                this.runner.Stop();
                this.pluginInterface.SavePluginConfig(this.configuration);
                this.chat.Print("[SubOps] 已停止");
                break;

            case "status":
                this.PrintStatus();
                break;

            case "learn":
                this.Learn(parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty);
                break;

            case "objects":
            case "obj":
            case "scan":
                this.DumpObjects();
                break;

            case "y":
            case "pos":
            case "height":
                this.DumpHeight();
                break;

            case "where":
                this.DumpWhere();
                break;

            case "room":
            case "event":
            case "events":
                this.DumpRoomEvents();
                break;

            case "menu":
                this.DumpMenu();
                break;

            case "redist":
                this.configuration.ReDispatch = parts.Length > 1 ? parts[1] is "on" or "true" or "1" : !this.configuration.ReDispatch;
                this.pluginInterface.SavePluginConfig(this.configuration);
                this.chat.Print($"[SubOps] 收完是否再次出航：{(this.configuration.ReDispatch ? "是" : "否")}");
                break;

            case "autorepair":
                this.configuration.AutoRepair = parts.Length > 1 ? parts[1] is "on" or "true" or "1" : !this.configuration.AutoRepair;
                this.pluginInterface.SavePluginConfig(this.configuration);
                this.chat.Print($"[SubOps] 出航前自动修理：{(this.configuration.AutoRepair ? "开" : "关")}");
                break;

            case "parts":
                this.PrintParts();
                break;

            case "repair":
                this.RepairNow(parts.Length > 1 ? parts[1] : string.Empty);
                break;

            case "multi":
            case "auto":
            case "all":
                this.HandleMulti(parts);
                break;

            case "char":
            case "chars":
                this.HandleChar(parts);
                break;

            case "switch":
                this.SwitchCharacter(parts.Length > 1 ? parts[1] : string.Empty);
                break;

            default:
                this.PrintHelp();
                break;
        }
    }

    /// <summary>
    /// /subops auto                       启动一轮全扫（等价 multi on）
    /// /subops multi off | stop           停止
    /// /subops multi advance &lt;秒&gt;        提前量
    /// /subops multi skip on|off          跳过确定没返航的角色
    /// /subops multi logout on|off        收完是否登出
    /// </summary>
    private void HandleMulti(string[] parts)
    {
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "advance":
                if (parts.Length > 2 && int.TryParse(parts[2], out var seconds) && seconds is > 0 and < 7200)
                {
                    this.configuration.MultiAdvanceSeconds = seconds;
                    this.pluginInterface.SavePluginConfig(this.configuration);
                    this.chat.Print($"[SubOps] 提前量：{seconds} 秒");
                }
                else
                {
                    this.chat.Print($"[SubOps] 提前量现在是 {this.configuration.MultiAdvanceSeconds} 秒（用法：/subops multi advance 300）");
                }

                return;

            case "skip":
                this.configuration.MultiSkipIdle = parts.Length > 2
                    ? parts[2] is "on" or "true" or "1"
                    : !this.configuration.MultiSkipIdle;
                this.pluginInterface.SavePluginConfig(this.configuration);
                this.chat.Print(this.configuration.MultiSkipIdle
                    ? "[SubOps] 跳过没返航的角色：开（切号快，但快照过时可能漏收）"
                    : "[SubOps] 跳过没返航的角色：关（每个角色都进屋看一遍，最稳）");
                return;

            case "logout":
                this.configuration.MultiLogoutWhenDone = parts.Length > 2
                    ? parts[2] is "on" or "true" or "1"
                    : !this.configuration.MultiLogoutWhenDone;
                this.pluginInterface.SavePluginConfig(this.configuration);
                this.chat.Print($"[SubOps] 全部收完后登出：{(this.configuration.MultiLogoutWhenDone ? "是" : "否")}");
                return;

            case "status":
                this.chat.Print($"[SubOps] 轮换：{this.switcher.StatusText}（{this.switcher.Progress}）");
                return;
        }

        var enable = sub switch
        {
            "off" or "false" or "0" or "stop" => false,
            _ => true,
        };

        if (!enable)
        {
            this.switcher.Abort();
            this.runner.Stop();
            this.pluginInterface.SavePluginConfig(this.configuration);
            this.chat.Print("[SubOps] 多角色轮换：已停止");
            return;
        }

        if (!this.switcher.StartSweep())
            return;

        this.pluginInterface.SavePluginConfig(this.configuration);
        this.PrintCharacters();
    }

    /// <summary>/subops char list|add|on &lt;名&gt;|off &lt;名&gt;|remove &lt;名&gt;|reset</summary>
    private void HandleChar(string[] parts)
    {
        var sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
                this.PrintCharacters();
                return;

            case "add":
                if (this.characters.Current == null)
                {
                    this.chat.Print("[SubOps] 还没读到当前角色（未登录或正在加载）。");
                    return;
                }

                this.chat.Print($"[SubOps] 已在名册：{this.characters.Current.Key}");
                this.pluginInterface.SavePluginConfig(this.configuration);
                return;

            case "on":
            case "off":
                this.ToggleCharacter(parts.Length > 2 ? parts[2] : string.Empty, sub == "on");
                return;

            case "remove":
                this.RemoveCharacter(parts.Length > 2 ? parts[2] : string.Empty);
                return;

            case "first":
                this.MoveCharacterFirst(parts.Length > 2 ? parts[2] : string.Empty);
                return;

            case "reset":
                this.switcher.ResetCooldowns();
                this.pluginInterface.SavePluginConfig(this.configuration);
                this.chat.Print("[SubOps] 已清空所有角色的冷却。");
                return;

            default:
                this.chat.Print("[SubOps] 用法：/subops char list | add | on <名> | off <名> | remove <名> | first <名> | reset");
                return;
        }
    }

    private void ToggleCharacter(string name, bool enabled)
    {
        var profile = this.characters.Find(name);
        if (profile == null)
        {
            this.chat.Print($"[SubOps] 名册里没有「{name}」。先登录一次那个角色。");
            return;
        }

        profile.Enabled = enabled;
        this.pluginInterface.SavePluginConfig(this.configuration);
        this.chat.Print($"[SubOps] {profile.Key} 参与轮换：{(enabled ? "是" : "否")}");
    }

    private void RemoveCharacter(string name)
    {
        var profile = this.characters.Find(name);
        if (profile == null)
        {
            this.chat.Print($"[SubOps] 名册里没有「{name}」。");
            return;
        }

        this.configuration.Characters.Remove(profile);
        this.pluginInterface.SavePluginConfig(this.configuration);
        this.chat.Print($"[SubOps] 已从名册移除 {profile.Key}");
    }

    /// <summary>把某个角色挪到名册最前 —— 轮换永远从第一个开始。</summary>
    private void MoveCharacterFirst(string name)
    {
        var profile = this.characters.Find(name);
        if (profile == null)
        {
            this.chat.Print($"[SubOps] 名册里没有「{name}」。");
            return;
        }

        this.configuration.Characters.Remove(profile);
        this.configuration.Characters.Insert(0, profile);
        this.pluginInterface.SavePluginConfig(this.configuration);
        this.chat.Print($"[SubOps] {profile.Key} 已排到名册第一位，轮换会从它开始。");
        this.PrintCharacters();
    }

    private void SwitchCharacter(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            this.chat.Print("[SubOps] 用法：/subops switch <角色名>");
            this.PrintCharacters();
            return;
        }

        this.switcher.SwitchTo(name);
    }

    private void PrintCharacters()
    {
        var now = DateTime.UtcNow;
        var advance = this.configuration.MultiAdvanceSeconds;

        if (this.configuration.Characters.Count == 0)
        {
            this.chat.Print("[SubOps] 名册还是空的：登录过的角色会自动登记。");
            return;
        }

        this.chat.Print($"[SubOps] 角色名册（共 {this.configuration.Characters.Count} 个，轮换按这个顺序走）：");
        for (var i = 0; i < this.configuration.Characters.Count; i++)
        {
            var profile = this.configuration.Characters[i];
            var mark = profile.ContentId == this.characters.Current?.ContentId ? " ←当前" : string.Empty;
            var enabled = profile.Enabled ? string.Empty : "（已停用）";
            this.chat.Print($"  {i + 1}. {profile.Name}@{profile.World}{enabled} · {profile.Describe(now, advance)}{mark}");
        }
    }

    private void PrintHelp()
    {
        this.chat.Print("[SubOps] 用法：");
        this.chat.Print("  /subops config           打开配置窗口（功能同插件列表里的「设置」按钮）");
        this.chat.Print("  /subops auto             一键：从名册第一个角色开始，逐个收/修/出航/切下一个");
        this.chat.Print("  /subops stop             中断（同时停掉轮换）");
        this.chat.Print("  /subops run              只收当前这个角色");
        this.chat.Print("  /subops status           查看 4 个槽位状态与材料数量");
        this.chat.Print("  /subops objects          列出 40 码内可交互物件（校准用，结果也写进日志）");
        this.chat.Print("  /subops where            列出当前开着的窗口（卡在哪一步时用）");
        this.chat.Print("  /subops room             看「房间切换点 / 航行管制面板」两个事件有没有读到（进屋后不动时用）");
        this.chat.Print("  /subops y                看脚下坐标与地板高度（检查有没有悬空）");
        this.chat.Print("  /subops menu             列出当前选择菜单的每一项（菜单没匹配上时用）");
        this.chat.Print("  /subops learn door       覆盖房门坐标（一般不用，已内置默认值）");
        this.chat.Print("  /subops learn panel      覆盖面板坐标（一般不用，默认按名字自动找）");
        this.chat.Print("  /subops redist on|off    收完是否按原航线再次出航");
        this.chat.Print("  /subops autorepair on|off 出航前是否自动修理坏零件（默认开）");
        this.chat.Print("  /subops parts            查看每艘艇 4 个零件的耐久");
        this.chat.Print("  /subops repair [0-3]     立刻修理（不带槽位号 = 全部）");
        this.chat.Print("  /subops multi on|off     开启 / 停止多角色轮换（on 即启动一轮）");
        this.chat.Print("  /subops multi skip on|off 跳过确定没返航的角色（默认关 = 每个都看一眼）");
        this.chat.Print("  /subops multi advance 300  提前量（秒），配合 skip 使用");
        this.chat.Print("  /subops multi logout on|off 全部收完后登出到标题界面");
        this.chat.Print("  /subops char list        查看角色名册与顺序");
        this.chat.Print("  /subops char first <名>  把某个角色排到第一位");
        this.chat.Print("  /subops char on|off <名> 让某个角色参与 / 退出轮换");
        this.chat.Print("  /subops char remove <名> 从名册移除");
        this.chat.Print("  /subops char reset       清空所有冷却（切号失败后手动重试）");
        this.chat.Print("  /subops switch <名>      立刻切到指定角色");
        this.chat.Print($"  当前状态：{this.runner.Stage} / {this.runner.StatusText} / 轮换：{this.switcher.StatusText}");
    }

    private void PrintStatus()
    {
        var current = this.characters.Current;
        if (current != null)
        {
            var progress = this.switcher.Active ? $" · 轮换 {this.switcher.Progress}（{this.switcher.StatusText}）" : $" · 轮换：{this.switcher.StatusText}";
            this.chat.Print($"[SubOps] 当前角色 {current.Key}{progress}");
        }

        var subs = Workshop.Read();
        if (subs.Count == 0)
        {
            this.chat.Print("[SubOps] 当前不在部队工房，读不到潜水艇数据。");
        }
        else
        {
            foreach (var sub in subs.Where(s => s.Enabled))
            {
                var state = sub.IsBack ? "已返航" : $"探索中，{sub.ReturnTime.ToLocalTime():MM-dd HH:mm} 返回";
                this.chat.Print($"[SubOps] {sub.Slot} 号 {sub.Name} 等级 {sub.Rank} · {state}");
            }
        }

        var (ceruleum, repair) = Workshop.ReadMaterials();
        this.chat.Print($"[SubOps] 青磷水 {ceruleum} / 修理材料 {repair}");
    }

    /// <summary>调试用：把每艘艇 4 个零件的物品 ID 与耐久打出来，校准容器/槽位用。</summary>
    /// <summary>
    /// /subops parts：把 4 个槽位的零件耐久和修理材料打出来。
    /// 修理不生效时先看这里——耐久是 0 却「没修」，说明是命令没落到位；
    /// 一条都读不到，说明零件容器没加载。
    /// </summary>
    private void PrintParts()
    {
        var (ceruleum, material) = Workshop.ReadMaterials();
        this.chat.Print($"[SubOps] 工房内={Workshop.InWorkshop}  修理材料={material}  青磷水={ceruleum}");

        if (!Workshop.InWorkshop)
        {
            this.chat.Print("[SubOps] 不在部队工房，零件数据读不到。");
            return;
        }

        for (var slot = 0; slot < 4; slot++)
        {
            if (!Workshop.TryReadParts(slot, out var parts))
            {
                this.chat.Print($"  {slot + 1} 号：读不到零件（容器未加载，或该槽位为空）");
                continue;
            }

            var text = string.Join("  ", parts.Select(p => $"{p.Slot}号 耐久{p.Condition}{(p.Damaged ? "（坏）" : string.Empty)}"));
            this.chat.Print($"  {slot + 1} 号：{text}");
            this.log.Information($"[SubOps/parts] {slot + 1} 号：{text}");
        }
    }

    /// <summary>手动修理。带槽位号（0~3）只修那艘，不带就修所有有坏零件的。</summary>
    private void RepairNow(string arg)
    {
        var only = int.TryParse(arg, out var parsed) && parsed is >= 0 and <= 3 ? parsed : -1;

        var (_, material) = Workshop.ReadMaterials();
        var ships = 0;
        var partsDone = 0;

        foreach (var sub in Workshop.Read())
        {
            if (!sub.Enabled)
                continue;

            if (only >= 0 && sub.Slot != only)
                continue;

            var damaged = Workshop.DamagedPartSlots(sub.Slot);
            if (damaged.Count == 0)
                continue;

            if (material - partsDone < damaged.Count)
            {
                this.chat.Print($"[SubOps] {sub.Slot} 号艇需修 {damaged.Count} 个零件，修理材料只剩 {material - partsDone} 个，跳过。");
                continue;
            }

            var done = Workshop.RepairAll(sub.Slot);
            partsDone += done;
            if (done > 0)
                ships++;
        }

        if (partsDone == 0)
            this.chat.Print("[SubOps] 没有需要修理的零件（或不在部队工房）。");
        else
            this.chat.Print($"[SubOps] 已修理 {ships} 艘 / {partsDone} 个零件，剩余修理材料 {material - partsDone}。");
    }

    private void Learn(string what)
    {
        if (what is "objects" or "obj" or "scan")
        {
            this.DumpObjects();
            return;
        }

        var anchor = this.navigator.FindEventObject(0, this.navigator.PlayerPosition, 6f)?.Position
                     ?? this.navigator.PlayerPosition;

        switch (what)
        {
            case "door":
                this.configuration.SetDoorAnchor(anchor);
                break;
            case "panel":
                this.configuration.SetPanelAnchor(anchor);
                break;
            default:
                this.chat.Print("[SubOps] learn 后面跟 door 或 panel");
                return;
        }

        this.pluginInterface.SavePluginConfig(this.configuration);
        this.chat.Print($"[SubOps] 已记录 {what}：({anchor.X:F2}, {anchor.Y:F2}, {anchor.Z:F2})");
    }

    /// <summary>
    /// 打印附近的可交互事件对象（名字 / BaseId / 坐标），用来校准房门与面板。
    /// 结果同时写进 Dalamud 日志，方便整段复制出来。
    /// </summary>
    private void DumpObjects()
    {
        var list = this.navigator.ListEventObjects(40f);
        if (list.Count == 0)
        {
            this.chat.Print("[SubOps] 40 码内没有可交互事件对象（屋子里/工房里再试）。");
            return;
        }

        this.chat.Print($"[SubOps] 40 码内可交互物件 {list.Count} 个（详见 /xllog）：");
        foreach (var item in list)
        {
            var line = $"  {item.Name}  baseId={item.BaseId}  " +
                       $"({item.Position.X:F2}, {item.Position.Y:F2}, {item.Position.Z:F2})  {item.Distance:F1}码";
            this.chat.Print(line);
            this.log.Information($"[SubOps/objects]{line}");
        }

        this.chat.Print("[SubOps] 站在房门/面板前，用 /subops learn door | /subops learn panel 记录。");
    }

    /// <summary>
    /// 打印坐标与脚下地面高度。/subops y 用来确认「是不是悬空」：
    /// 脚下 Y 和地板 Y 差得越多，人浮得越高。
    /// </summary>
    private void DumpHeight()
    {
        var pos = this.navigator.PlayerPosition;
        var floor = this.navigator.FloorYAt(pos);
        var last = this.navigator.LastTeleport;

        this.chat.Print($"[SubOps] 脚下坐标 ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");
        if (floor.HasValue)
        {
            var drop = pos.Y - floor.Value;
            this.chat.Print($"[SubOps] 地板高度 {floor.Value:F2}，落差 {drop:+0.00;-0.00;0.00}");
            if (drop > 0.5f)
                this.chat.Print("[SubOps] 落差偏大：人在半空，重跑一次会自动贴地校正。");
        }
        else
        {
            this.chat.Print("[SubOps] 这块地方 vnavmesh 没网格，查不到地板高度（室内正常）。");
        }

        if (last.At != DateTime.MinValue)
            this.chat.Print($"[SubOps] 上次瞬移落点 ({last.Destination.X:F2}, {last.Destination.Y:F2}, {last.Destination.Z:F2})，{DateTime.UtcNow - last.At:F1} 前");

        this.log.Information($"[SubOps/y] pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) floor={floor?.ToString("F2") ?? "null"}");
    }

    /// <summary>
    /// 打印当前阶段 + 所有可见窗口。卡住时执行 /subops where，
    /// 就能看出「界面开着但插件没认出来」是哪个名字对不上。
    /// </summary>
    private void DumpWhere()
    {
        this.chat.Print($"[SubOps] 阶段 {this.runner.Stage} · {this.runner.StatusText}");
        this.chat.Print($"[SubOps] 工房内={Workshop.InWorkshop} 屋内={Workshop.InHouse} 屋外={Workshop.Outdoors}");

        var open = AddonHelper.ProbeVisible(
            this.gui,
            AddonHelper.SubmarineAddons
                .Concat(new[] { AddonHelper.SelectString, AddonHelper.SelectYesno })
                .ToArray());

        this.chat.Print(open.Count == 0
            ? "[SubOps] 潜艇/菜单类窗口：一个都没开"
            : $"[SubOps] 开着：{string.Join("、", open)}");
        this.log.Information($"[SubOps/where] open={string.Join(",", open)}");
    }

    /// <summary>
    /// 打印「房间里到底有没有那两个关键事件」。
    /// /subops room 用于「进屋后不动」这种情况：能看出是事件表没读到，
    /// 还是读到了但坐标不对。
    /// </summary>
    private void DumpRoomEvents()
    {
        var pos = this.navigator.PlayerPosition;
        this.chat.Print($"[SubOps] 位置 ({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})  territory={this.navigator.Territory}");
        this.chat.Print($"[SubOps] 工房内={Workshop.InWorkshop} 屋内={Workshop.InHouse} 屋外={Workshop.Outdoors}");

        this.DumpOneEvent("移动到其他房间", 721074, pos);
        this.DumpOneEvent("航行管制面板", 3276843, pos);
    }

    private void DumpOneEvent(string label, uint eventId, System.Numerics.Vector3 playerPos)
    {
        if (!this.navigator.TryFindEventObject(eventId, out var found))
        {
            var line = $"[SubOps] {label}（{eventId}）：事件表里没找到";
            this.chat.Print(line);
            this.log.Information($"[SubOps/room] {line}");
            return;
        }

        var distance = System.Numerics.Vector3.Distance(playerPos, found.Position);
        var text = $"[SubOps] {label}（{eventId}）：({found.Position.X:F2}, {found.Position.Y:F2}, {found.Position.Z:F2}) 距 {distance:F1} 码，物件 {found.ObjectCount} 个";
        this.chat.Print(text);
        this.log.Information($"[SubOps/room] {text}");
    }

    /// <summary>把当前 SelectString 菜单的每一行打出来，用于核对「按文字匹配」有没有匹配上。</summary>
    private void DumpMenu()
    {
        var entries = AddonHelper.ReadMenuEntries(this.gui);
        if (entries.Count == 0)
        {
            this.chat.Print("[SubOps] 当前没有选择菜单（SelectString）。");
            return;
        }

        this.chat.Print($"[SubOps] 菜单共 {entries.Count} 项：");
        for (var i = 0; i < entries.Count; i++)
        {
            this.chat.Print($"  [{i}] {entries[i]}");
            this.log.Information($"[SubOps/menu] [{i}] {entries[i]}");
        }
    }
}
