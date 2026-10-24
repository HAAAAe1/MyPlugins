# MyPlugins

个人 Dalamud 第三方插件仓库（FF14 国服 / XIVLauncherCN，API 15）。

## 添加方法

1. 打开 **Dalamud 设置 → 实验性功能 → 自定义插件库**
2. 添加以下地址：
   ```
   https://raw.githubusercontent.com/HAAAAe1/MyPlugins/main/pluginmaster.json
   ```
3. 点击页面顶部的 **保存**
4. 打开 **Dalamud 插件管理器**，即可浏览/安装本仓库中的插件

> 地址固定，发版不需要改。

## 插件列表

| 名称 | 版本 | 说明 |
|------|------|------|
| **SubFleet** | 1.0.0 | 部队工房潜艇作业，全新重写版：按 DailyRoutines 的顺序收取、修理、再出航，多角色按名册轮换。切角色需要 Lifestream |
| **SubOps** | 0.5.8 | 部队工房潜水艇作业：一键收取、自动修理、重新委派，支持多角色轮换。带配置窗口（名册 / 实时状态 / 行为 / 校准）。场景移动用瞬移，不需要 vnavmesh；切角色需要 Lifestream |

## 说明

- `pluginmaster.json` 遵循 [Dalamud 第三方插件仓库格式](https://github.com/goatcorp/Dalamud/wiki/Third-Party-Plugin-Repo-Format)
- **真正会被校验的只有三个字段**（见 `PluginRepository.IsValidManifest`）：`InternalName`、`Name`、
  `AssemblyVersion`。缺任何一个，这条目会被静默丢弃（只写进 `dalamud.log`，界面上什么都不提示）。
- 其余字段都是可选的，但影响显示：`Punchline`/`Description` 是卡片正文，`Author` 是作者，
  `LastUpdate`（Unix 秒）是「最后更新」列，`DownloadCount` 是下载数，`Changelog` 是更新说明，
  `DalamudApiLevel` 决定与客户端 API 的兼容判定。
- `CategoryTags` 只能填白名单里的词，否则不生效：`other` / `jobs` / `ui` / `minigames` /
  `inventory` / `sound` / `social` / `utility`（大小写不敏感）。
- `ApplicableVersion` 填 `"any"` 是合法的，表示不限制游戏版本。
- 第三方仓库**不能覆盖**官方仓库里已存在的 `InternalName`，同名条目会被整条丢弃（安全策略）。
- 仓库地址必须返回**原始 JSON**（`raw.githubusercontent.com` 这种），填 GitHub 网页地址会解析失败。

## 发布 SubFleet

```bash
cd SubFleet
dotnet build -c Release
cd ../MyPlugins
rm -f dist/SubFleet-<版本>.zip
(cd ../SubFleet/bin/Release && zip -q ../../../MyPlugins/dist/SubFleet-<版本>.zip SubFleet.dll SubFleet.json)
```

然后改 `pluginmaster.json` 里 SubFleet 的 `AssemblyVersion` / 两个 `DownloadLink` / `LastUpdate`。

## 发布 SubOps

国服（XIVLauncherCN）用 **API 15**，国际服 Dalamud 稳定版是 **API 14**，CI 里下载的 Dalamud 与国服
不匹配、容易编译失败，所以 SubOps 一律本地构建发布：

```bash
./scripts/release-subops.sh 0.5.8
```

脚本依次做：改 `SubOps.csproj` 的 `Version` → 编译 → 校验产物清单里的 `AssemblyVersion`
是否和期望一致（不一致就停）→ 打包 → 回写 `pluginmaster.json`
的 `AssemblyVersion`、两个 `DownloadLink`、`LastUpdate`。

发完记得：提交 → 打 tag `vX.Y.Z` → 推送。订阅走 raw，提交后立刻生效，不用等缓存。

`SubOps/Directory.Build.props` 会自动适配：国服机器指向 `XIVLauncherCN` 的 Hooks，
没装国服的环境（比如 GitHub Actions）回落到 SDK 默认路径。
