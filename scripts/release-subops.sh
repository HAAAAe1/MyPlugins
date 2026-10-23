#!/usr/bin/env bash
# 构建并发布 SubOps 到 MyPlugins 仓库。
#
# 用法：
#   ./scripts/release-subops.sh 0.2.0
#
# 做的事：编译 → 打包 SubOps.zip（dll + json）→ 创建 GitHub Release → 回写 pluginmaster.json
#
# Dalamud 侧真正校验的只有 InternalName / Name / AssemblyVersion 三个字段，
# 少任何一个这条目会被静默丢弃（只在 dalamud.log 里留一行），界面上不会报错。
set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  echo "用法: $0 <版本号>   例: $0 0.2.0"
  exit 2
fi

TAG="subops-v$VERSION"
REPO="HAAAAe1/MyPlugins"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# 0.2.0 -> 0.2.0.0（AssemblyVersion 是四段式，少一段会被当成非法版本）
if [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  ASM_VERSION="$VERSION.0"
elif [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  ASM_VERSION="$VERSION"
else
  echo "版本号格式不对，应为 x.y.z 或 x.y.z.w：$VERSION" >&2
  exit 2
fi

echo "==> 同步 csproj 版本号为 $VERSION"
sed -i.bak "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$ROOT/SubOps/SubOps.csproj"
rm -f "$ROOT/SubOps/SubOps.csproj.bak"

echo "==> 编译 SubOps $VERSION"
cd "$ROOT/SubOps"
dotnet build -c Release

# 编译出来的清单版本要和 pluginmaster 声明的一致，不一致就直接停
BUILT_VERSION="$(python -c "import json;print(json.load(open('bin/Release/SubOps.json',encoding='utf-8'))['AssemblyVersion'])")"
if [[ "$BUILT_VERSION" != "$ASM_VERSION" ]]; then
  echo "产物清单里的 AssemblyVersion 是 $BUILT_VERSION，与期望的 $ASM_VERSION 不一致，停止发布" >&2
  exit 3
fi

echo "==> 打包"
cd "$ROOT/SubOps/bin/Release"
rm -f SubOps.zip
python - <<'PY'
import os, zipfile

with zipfile.ZipFile("SubOps.zip", "w", zipfile.ZIP_DEFLATED) as z:
    z.write("SubOps.dll")
    z.write("SubOps.json")

print("SubOps.zip:", os.path.getsize("SubOps.zip"), "bytes")
PY

echo "==> 创建 Release $TAG"
gh release create "$TAG" SubOps.zip --repo "$REPO" --title "SubOps v$VERSION" --generate-notes

echo "==> 回写 pluginmaster.json"
cd "$ROOT"
python - "$TAG" "$ASM_VERSION" <<'PY'
import json, sys, time

tag, asm_version = sys.argv[1], sys.argv[2]
path = "pluginmaster.json"
with open(path, encoding="utf-8") as f:
    data = json.load(f)

url = f"https://github.com/HAAAAe1/MyPlugins/releases/download/{tag}/SubOps.zip"
matched = False
for plugin in data:
    if plugin["InternalName"] != "SubOps":
        continue
    plugin["AssemblyVersion"] = asm_version
    plugin["DownloadLinkInstall"] = url
    plugin["DownloadLinkUpdate"] = url
    plugin["LastUpdate"] = int(time.time())   # 插件管理器的「最后更新」列读这个
    matched = True
    print("  版本:", asm_version)
    print("  链接:", url)

if not matched:
    sys.exit("pluginmaster.json 里找不到 InternalName = SubOps 的条目")

with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
    f.write("\n")
PY

echo "完成。提交并推送："
echo "  git add -A && git commit -m \"release: SubOps $VERSION\" && git -c http.proxy= -c https.proxy= push origin main"
