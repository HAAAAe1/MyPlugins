#!/usr/bin/env bash
# 构建并发布 SubOps 到 MyPlugins 仓库。
#
# 用法：
#   ./scripts/release-subops.sh 0.1.0
#
# 做的事：编译 → 打包 SubOps.zip（dll + json）→ 创建 GitHub Release。
# 发布后记得同步改两处版本号：
#   - SubOps/SubOps.csproj 的 <Version>
#   - pluginmaster.json 的 AssemblyVersion 与两个 DownloadLink
set -euo pipefail

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
  echo "用法: $0 <版本号>   例: $0 0.1.0"
  exit 2
fi

TAG="subops-v$VERSION"
REPO="HAAAAe1/MyPlugins"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "==> 编译 SubOps $VERSION"
cd "$ROOT/SubOps"
dotnet build -c Release

echo "==> 打包"
cd "$ROOT/SubOps/bin/Release"
rm -f SubOps.zip
python - <<'PY'
import zipfile

with zipfile.ZipFile("SubOps.zip", "w", zipfile.ZIP_DEFLATED) as z:
    z.write("SubOps.dll")
    z.write("SubOps.json")

print("SubOps.zip:", __import__("os").path.getsize("SubOps.zip"), "bytes")
PY

echo "==> 创建 Release $TAG"
gh release create "$TAG" SubOps.zip --repo "$REPO" --title "SubOps v$VERSION" --generate-notes

echo "完成。别忘了确认 pluginmaster.json 里的版本号和下载链接已指向 $TAG"
