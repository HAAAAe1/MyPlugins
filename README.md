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

| 名称 | 说明 |
|------|------|
| _(暂无)_ | 还没加插件 |

## 说明

- `pluginmaster.json` 遵循 [Dalamud 第三方插件仓库格式](https://github.com/goatcorp/Dalamud/wiki/Third-Party-Plugin-Repo-Format)
- 每个插件条目需要：`Name`、`InternalName`、`AssemblyVersion`、`Author`、`Punchline`、`Description`、`DalamudApiLevel`、`DownloadLinkInstall`、`DownloadLinkUpdate`
- 插件 zip 通过 GitHub Releases 发布（tag = 版本号，附件 = `latest.zip`）
