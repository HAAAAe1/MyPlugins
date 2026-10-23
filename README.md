# MyPlugins

个人 Dalamud 第三方插件仓库（FF14）。

## 添加方法

1. 打开 **Dalamud 设置 → 实验性功能 → 自定义插件库**
2. 添加以下地址：
   ```
   https://raw.githubusercontent.com/HAAAAe1/MyPlugins/main/pluginmaster.json
   ```
3. 点击页面顶部的 **保存**
4. 打开 **Dalamud 插件管理器**，即可浏览/安装本仓库中的插件

## 插件列表

| 名称 | 版本 | 说明 |
|------|------|------|
| **SubOps** | 0.2.0 | 部队工房潜水艇作业：一键收取、自动修理、重新委派，支持多角色轮换。带配置窗口（名册 / 实时状态 / 行为 / 校准）。需 vnavmesh（寻路）+ Lifestream（切角色） |

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
- 仓库地址必须返回**原始 JSON**（`raw.githubusercontent.com` 这类），填 GitHub 的网页地址会解析失败。
- 插件 zip 通过 GitHub Releases 发布，release 附件命名与 tag 对应：

| 插件 | tag | 附件 |
|------|-----|------|
| SubOps | `subops-v0.2.0` | `SubOps.zip` |

## 发布 SubOps

国服（XIVLauncherCN）用的是 **API 15**，而国际服 Dalamud 当前稳定版是 **API 14**，
CI 里下载的 Dalamud 与国服不匹配，容易编译失败。所以 SubOps 用本地构建发布，一条命令搞定：

```bash
./scripts/release-subops.sh 0.2.0
```

脚本会依次做：改 `SubOps.csproj` 的 `Version` → 编译 → 校验产物清单里的 `AssemblyVersion`
是否和期望一致（不一致就停）→ 打包 `SubOps.zip` → 创建 `subops-v0.2.0` release
→ 回写 `pluginmaster.json` 的 `AssemblyVersion`、两个 `DownloadLink`、`LastUpdate`。

跑完按提示提交推送即可。**不用再手工改版本号**，唯一要确认的是插件管理器里能看到新版本。

`SubOps/Directory.Build.props` 会自动适配：国服机器指向 `XIVLauncherCN` 的 Hooks，
没装国服的环境（比如 GitHub Actions）回落到 SDK 默认路径。
