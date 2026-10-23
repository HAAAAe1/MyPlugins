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
| **SubOps** | 0.1.0 | 部队工房潜水艇作业：一键收取、自动修理、重新委派，支持多角色轮换。需 vnavmesh（寻路）+ Lifestream（切角色） |

## 说明

- `pluginmaster.json` 遵循 [Dalamud 第三方插件仓库格式](https://github.com/goatcorp/Dalamud/wiki/Third-Party-Plugin-Repo-Format)
- 每个插件条目需要：`Name`、`InternalName`、`AssemblyVersion`、`Author`、`Punchline`、`Description`、`DalamudApiLevel`、`DownloadLinkInstall`、`DownloadLinkUpdate`
- 插件 zip 通过 GitHub Releases 发布，release 附件命名与 tag 对应：

| 插件 | tag | 附件 |
|------|-----|------|
| SubOps | `subops-v0.1.0` | `SubOps.zip` |

## 发布 SubOps

国服（XIVLauncherCN）用的是 **API 15**，而国际服 Dalamud 当前稳定版是 **API 14**，
CI 里下载的 Dalamud 与国服不匹配，容易编译失败。所以 SubOps 用本地构建发布：

```bash
cd SubOps
dotnet build -c Release
# 打包 bin/Release/SubOps.dll + SubOps.json 为 SubOps.zip
gh release create subops-v0.1.0 SubOps.zip --repo HAAAAe1/MyPlugins --title "SubOps v0.1.0"
```

版本号要同步改三处：`SubOps/SubOps.csproj` 的 `Version`、`pluginmaster.json` 的
`AssemblyVersion` 与两个 `DownloadLink`。

`SubOps/Directory.Build.props` 会自动适配：国服机器指向 `XIVLauncherCN` 的 Hooks，
没装国服的环境（比如 GitHub Actions）回落到 SDK 默认路径。
