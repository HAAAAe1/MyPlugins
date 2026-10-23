# SubOps

部队工房潜水艇作业插件（自研骨架）。

功能：读取游戏内潜水艇数据 → 判断哪几艘已返航 → 自动进屋、进工房、开航行管制面板 →
逐艘确认、检查零件耐久并自动修理 → 再次出航。
支持多角色轮换：从名册第一个角色开始，逐个「进屋 → 收 → 修 → 出航 → 切下一个」，走完名册再停。
代码按「数据 / 导航 / 状态机 / 入口」四层拆开，方便往上加自己的功能。

## 环境要求

| 项 | 版本 |
| --- | --- |
| .NET SDK | **10.0.x（x64）** —— 唯一必须自己装的东西 |
| Dalamud | API 15（当前国服卫月版本，`DalamudApiLevel = 15`） |
| 插件 | vnavmesh（寻路 IPC）、**Lifestream**（切角色，多角色功能必需）、FFXIVClientStructs（编译时由 Dalamud.NET.Sdk 带下来） |

.NET 10 SDK 下载：<https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0>

装完在终端确认：

```bash
dotnet --list-sdks
# 应出现 10.0.1xx [C:\Program Files\dotnet\sdk]
```

`Dalamud.NET.Sdk/15.0.0` 是 NuGet 包，`dotnet build` 时自动还原，不需要手动装。
如果还原很慢，可以加国内源：

```bash
dotnet nuget add source https://nuget.azure.cn/v3/index.json -n cn
```

## 编译与加载

```bash
cd SubOps
dotnet build -c Release
```

产物：`bin/Release/SubOps.dll` + `SubOps.json`（以及 `SubOps.deps.json`）。

> 编译如果报 `Dalamud not found`，是因为 SDK 默认去找国际服路径。国服由 `Directory.Build.props`
> 自动指向 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`（没有 dev 就退回日期目录），
> 只有在没装国服的环境里才会走 SDK 默认路径（GitHub Actions 就是这样）。
> 卫月更新后如果仍找不到，用 `dotnet build -c Release -p:DALAMUD_HOME="...\Hooks\<新目录>"` 覆盖。

放到：

```
%APPDATA%\XIVLauncherCN\devPlugins\SubOps\
```

然后在游戏里 `/xlplugins` → 开发插件里加载。

## 配置窗口

插件列表里点 SubOps 的「设置」（齿轮）按钮，或者打 `/subops config`。

> 卫月显示齿轮的判据是 `UiBuilder.HasConfigUi => OpenConfigUi != null`，
> 所以插件必须订阅 `pluginInterface.UiBuilder.OpenConfigUi`。只写窗口不订阅，
> 插件列表里就是没有设置按钮，也不会有任何报错。

四个页签：

| 页签 | 内容 |
| --- | --- |
| 潜水艇名册 | 每个角色一行，列出它 4 个槽位的等级与返航时间（这就是「登记了哪些」）；可勾选启用、置顶、移除 |
| 当前角色 | 实时读工房：4 艘艇的状态/返航/每个零件耐久 + 青磷水与修理材料 |
| 行为 | 收完是否再出航、出航前自动修理、跳过没返航的角色、收完登出、提前量 |
| 校准 | 房门/面板锚点的记录与清除、两个菜单下标 |

名册里的数据来自每次进工房时拍的快照（`SnapshotAt`），所以没进过工房的角色会显示「未采集」。
实时数据每秒刷新一次，不在工房时读不到。

## 命令

| 命令 | 作用 |
| --- | --- |
| `/subops config` | 打开配置窗口（同插件列表里的设置按钮） |
| `/subops run` | 开始收：判断所在位置 → 进屋 → 进工房 → 开面板 → 逐艘确认 |
| `/subops stop` | 中断，并停掉寻路 |
| `/subops status` | 打印 4 个槽位状态（等级 / 是否返航）与材料数量 |
| `/subops learn door` | 把脚下位置记为「移动到其他房间」的触发点位置 |
| `/subops learn panel` | 把脚下位置记为航行管制面板的位置 |
| `/subops redist on\|off` | 收完是否按原航线再次出航 |
| `/subops autorepair on\|off` | 出航前是否自动修理耐久为 0 的零件（默认开） |
| `/subops parts` | 打印每艘艇 4 个零件的物品 ID 与耐久（校准用） |
| `/subops repair [0-3]` | 立刻修理，带槽位号只修那艘，不带就修所有坏零件 |
| `/subops auto` | **一键轮换**：从名册第一个开始逐个处理（等价 `multi on`） |
| `/subops multi on\|off` | 开启一轮 / 中途停止 |
| `/subops multi skip on\|off` | 跳过确定没返航的角色（默认关 = 每个都进屋看一眼） |
| `/subops multi advance 300` | 提前量（秒），只在 skip 开着时起作用 |
| `/subops multi logout on\|off` | 走完名册后是否登出到标题界面 |
| `/subops char list` | 角色名册（带序号）+ 每个角色的返航状态、快照时间、冷却 |
| `/subops char first <名>` | 把某个角色排到名册第一位（轮换从它开始） |
| `/subops char on\|off <名>` | 让某个角色参与 / 退出轮换 |
| `/subops char remove <名>` | 从名册移除 |
| `/subops char reset` | 清空所有冷却（切号失败后手动重试） |
| `/subops switch <名>` | 立刻切到指定角色 |

`learn` 只在找不到目标时用得上：插件默认先按对象 BaseId 找房屋入口，房间门和面板
按记录的位置就近找。第一次使用建议在门口和面板前各执行一次 `learn`。

## 代码结构

```
SubOps/
├─ Plugin.cs            入口：注册命令、监听结果窗口、驱动状态机
├─ Configuration.cs     所有配置项（位置拆成 float 存，避免序列化丢字段）
├─ Workshop.cs          读游戏数据：槽位等级/返航时间、材料数量、零件耐久；下发修理命令
├─ Navigator.cs         移动（vnavmesh IPC）与交互（TargetSystem.InteractWithObject）
├─ AddonHelper.cs       窗口读写：取指针、按下标触发回调、读文本、关闭窗口
├─ SubmarineRunner.cs   状态机：Decide → Teleport → GoEntrance → PickRoom → GoPanel → OpenList → Collect
├─ CharacterProfile.cs  单角色的潜水艇快照（4 槽）与「该不该去」的判定
├─ CharacterBook.cs     角色名册：识别当前角色、自动登记、在工房拍快照
├─ CharacterSwitcher.cs 轮换调度：按名册顺序一站一站走（切号走 Lifestream IPC）
└─ ConfigUi.cs          配置窗口（ImGui）：名册 / 实时状态 / 行为 / 校准
```

### 关键接口（改代码时最常用）

```csharp
// 潜水艇数据
var data = HousingManager.Instance()->WorkshopTerritory->Submersible.Data; // 4 个槽位
data[i].RankId;          // 0 = 该槽位未启用
data[i].GetReturnTime(); // UTC，<= 现在表示已返航
data[i].NameString;      // 潜艇名

// 界面按钮：按下标触发回调，比模拟鼠标稳
addon->FireCallbackInt(index);
addon->Close(true);

// 零件与修理（容器 25004，每艇 5 格取前 4 格）
var parts = InventoryManager.Instance()->GetInventoryContainer((InventoryType)25004);
parts->GetInventorySlot(5 * slot + i)->Condition;   // 0 = 坏了
GameMain.ExecuteCommand(1153, slot, partIndex);      // ExecuteCommandFlag.RepairSubmarinePart

// 交互
TargetSystem.Instance()->InteractWithObject(objNative, true);

// 寻路
moveTo.InvokeAction(position, false); // vnavmesh.SimpleMove.PathfindAndMoveTo
```

## 状态机怎么改

`SubmarineRunner` 里每个阶段一个方法，`SetStage(下一个阶段, 延迟秒)` 推进。
加新功能的最低成本做法：

1. 加一个 `RunnerStage` 值；
2. 在 `Tick()` 的 switch 里加分支；
3. 写一个方法，返回时用 `SetStage` 决定下一步。

比如「出航前检查燃料」：在 `Collect()` 里 `ExplorationDetail` 打开后，先读
`Workshop.ReadMaterials()` 判断青磷水够不够，不够就 `Finish()` 并提示补给，
写法照抄现有的 `TryRepairBeforeDeparture()` 即可（返回 true 表示「本次做完，等界面刷新再继续」）。

## 多角色轮换

### 为什么要自己存数据

潜水艇的返航时间在 `HousingManager.WorkshopTerritory` 里，而这个结构**只对当前登录角色加载**。
所以不站在工房，就读不到别的角色还剩几小时返航。做法和 AutoRetainer 一样：

> 每进一次工房，把 4 个槽位的返航时间拍一份快照存进配置（`CharacterProfile.Vessels`），
> 之后用于 `/subops char list` 展示，以及开着 `multi skip` 时判断「这个号用不用去」。

角色在登录时自动登记（`CharacterBook.Tick`），名册存在 `Configuration.Characters` 里，
内容包括 ContentId、名字、服务器、是否参与轮换、快照时间、冷却时间。

### 调度：顺序全扫

```
名册[0] → 切过去 → 进屋 → 开面板 → 收 → 修零件 → 出航 → 名册[1] → … → 走完 → 收工
```

走的是**顺序全扫**，不是「谁有活才去谁」。原因是返航时间只有站进工房才读得到，
拿上次的快照猜「这号没活」经常猜错（改了航线、修了零件都会变），宁可多切一趟也别漏收。

- 起点固定是名册第一个（用 `/subops char first <名>` 可以调），**当前角色不是第一个就先切走**。
- 想让没活的号跳过：`/subops multi skip on`（靠快照判断，快就快，但可能误判）。
- 停用的角色（`/subops char off`）和冷却中的角色会被跳过，不会中断整轮。
- 每一站跑完等 4 秒再切，让游戏把新的返航时间写回内存。

### 切角色

自己实现切号要处理排队、断线、加载卡住，成本太高；这里直接调 **Lifestream** 的两个 IPC：

```csharp
pluginInterface.GetIpcSubscriber<string, string, object>("Lifestream.ChangeCharacter")  // (名, 服务器)
pluginInterface.GetIpcSubscriber<object>("Lifestream.Logout")
pluginInterface.GetIpcSubscriber<object>("Lifestream.IsBusy")
```

返回值不解析 —— Lifestream 的 `ErrorCode` 是混淆过的枚举，猜错就会误判。
**成败用「角色名有没有变过来」判断**：调用后等最多 150 秒，`PlayerState.ContentId` 变成目标角色就算成功。

### 容错（比主流程更重要）

| 场景 | 处理 |
| --- | --- |
| 切号 150 秒没进去 | 目标角色冷却 30 分钟，跳过它走下一个 |
| 这一站超过 15 分钟没完成 | 强制跳过（加载卡住、界面卡住），保证整轮能走完 |
| 单次收艇超过 10 分钟 | 状态机判失败 → 该角色冷却 20 分钟 → 下一站 |
| 传送回部队房屋失败 8 次 | 判失败（这个角色可能没有部队房屋）→ 下一站 |
| 没有 vnavmesh | 不启动，直接中止整轮 |
| Lifestream 没装 | 拒绝开启轮换，提示装插件 |

### 用法（完整流程）

1. **登记**：每个参与的角色各登录一次，进一次部队工房。插件会自动登记角色、顺便存下 4 艘艇的返航时间。
2. **排顺序**：`/subops char list` 看序号；想让谁第一个被收，用 `/subops char first 角色名`。
3. **起跑**：在任意角色上打 `/subops auto`。它会先切到名册第一个，然后一路走完。
4. **收工**：走完名册自动停。想直接下线就提前 `/subops multi logout on`。
5. 中途想停：`/subops multi off`（或 `/subops stop`）。

> 注意：如果同时开着 **AutoRetainer 的多角色模式**，两个插件会互相抢窗口，别同时开。
> 跨大区（不同 DC）的角色 Lifestream 未必切得过去，失败会走「超时→冷却→跳过」这条路。

## 待实测校准的点

代码是按游戏接口写出来的，但这几个细节必须在游戏里跑一遍才能收敛（我这边没有运行环境）：

1. **房屋入口 BaseId**：`2002737` 取自现行版本，若找不到入口，先走到门口用 `learn`。
2. **房间菜单下标**：`RoomMenuIndex = 0`（部队工房）。若菜单顺序变了，改配置值。
3. **面板菜单下标**：`PanelMenuIndex = 1`（管理潜水艇）。
4. **详情界面回调下标**：`0` = 出发（再次派遣），结果窗口 `1` = 确认。
5. **面板 / 房门定位**：靠 `learn` 记录的位置就近搜索，第一次必须各记一次。
6. **零件容器**：`25004` 取自现行版本，每艇占 5 格、前 4 格是船体/舾装/船首/船尾，
   第 5 格恒空。进工房后先用 `/subops parts` 核对物品 ID 和耐久是否对得上。
7. **修理材料**：每个零件消耗 1 个「魔导机械修理材料」（10155），代码会先数够不够再修，不够就跳过并在聊天框说明。
8. **切号 IPC**：`Lifestream.ChangeCharacter` 的名字与参数照 AutoRetainer 的用法写的（`(名, 服务器)`），
   Lifestream 2.6.0.5 上应该一致；如果 `/subops multi on` 报没找到 IPC，先看 Lifestream 是否启用。
   第一次用建议 `/subops switch 角色名` 单独试一次，确认能进得去再开轮换。
9. **快照时机**：收完等 3 秒再拍，因为游戏要等服务器回来才更新返航时间。
   如果发现「刚收完又判定该去收」，说明 3 秒不够，把 `Plugin.OnUpdate` 里的延迟调大。

## 与 Daily Routines 的关系

功能相同的模块 DR 里叫 `AutoSubmarineCollect`，它需要在线验证才能用。
本项目是从游戏公开接口（FFXIVClientStructs / Dalamud API）重新实现的，不包含 DR 的任何代码，
也不依赖它的运行时。差别：

- DR 用自研的 `MovementManager`（TPSmart）和一堆内存签名（`CurrentSubmarineIndexSig` 等）；
  这里用 vnavmesh 的 IPC + 结构体直读，少一层签名维护。
- DR 会往航行管制面板里塞自绘 UI（物品图标、自动收取按钮）；这里不注入任何界面元素。
- DR 的「自动修理」靠内存签名 `CurrentSubmarineIndexSig` 拿当前选中的艇，再用
  `ExecuteCommandFlag.RepairSubmarinePart`（= 1153）下发；这里直接复用 `FFXIVClientStructs` 的
  `GameMain.ExecuteCommand`，用自己记录的槽位号，不需要扫特征码。
- DR 在任务期间对所有 `SelectYesno` 都点「是」，这里同样处理，但只在 `/subops run` 运行中生效。
