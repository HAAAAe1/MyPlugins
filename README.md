# MyPlugins

Personal Dalamud third-party plugin repository for FFXIV.

## How to Add

1. Open **Dalamud Settings → Experimental → Custom Plugin Repositories**
2. Add this URL:
   ```
   https://raw.githubusercontent.com/HAAAAe1/MyPlugins/main/pluginmaster.json
   ```
3. Click **Save** at the top of the settings page
4. Open the **Dalamud Plugin Installer** and browse/install plugins from this repo

## Plugins

| Name | Description |
|------|-------------|
| _(empty)_ | No plugins added yet |

## Notes

- `pluginmaster.json` follows the [Dalamud third-party repo format](https://github.com/goatcorp/Dalamud/wiki/Third-Party-Plugin-Repo-Format)
- Each plugin entry needs: `Name`, `InternalName`, `AssemblyVersion`, `Author`, `Punchline`, `Description`, `DalamudApiLevel`, `DownloadLinkInstall`, `DownloadLinkUpdate`
- Plugin zip files are hosted as GitHub Releases (tag = version, asset = `latest.zip`)
