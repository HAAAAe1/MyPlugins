import base64
import json
import subprocess
import sys

REPO = "HAAAAe1/MyPlugins"
ROOT = r"C:\Users\Administrator\WorkBuddy\2026-10-21-19-49-42\MyPlugins"


def api(path, method="GET", payload=None, binary=False):
    cmd = ["gh", "api", "-X", method, path, "-H", "Accept: application/vnd.github+json"]
    data = None
    if payload is not None:
        cmd += ["--input", "-"]
        data = json.dumps(payload)
    p = subprocess.run(cmd, input=data, capture_output=True, text=True, encoding="utf-8")
    if p.returncode != 0:
        raise RuntimeError(f"{method} {path} failed: {p.stderr[:800]}")
    if binary:
        return p.stdout
    return json.loads(p.stdout) if p.stdout.strip() else {}


def main():
    head = api(f"repos/{REPO}/git/ref/heads/main")["object"]["sha"]
    print("head:", head)

    base_tree = api(f"repos/{REPO}/git/commits/{head}")["tree"]["sha"]
    print("base_tree:", base_tree)

    files = [
        "SubOps/Navigator.cs",
        "SubOps/AddonHelper.cs",
        "SubOps/SubmarineRunner.cs",
        "SubOps/CharacterSwitcher.cs",
        "SubOps/SubOps.csproj",
        "pluginmaster.json",
        "README.md",
        "dist/SubOps-0.5.1.zip",
    ]

    tree_entries = []
    for rel in files:
        full = f"{ROOT}\\{rel.replace('/', chr(92))}"
        with open(full, "rb") as fh:
            raw = fh.read()
        blob = api(
            f"repos/{REPO}/git/blobs",
            "POST",
            {"content": base64.b64encode(raw).decode("ascii"), "encoding": "base64"},
        )
        tree_entries.append(
            {"path": rel, "mode": "100644", "type": "blob", "sha": blob["sha"]}
        )
        print(f"blob {rel}: {len(raw)} bytes")

    tree = api(
        f"repos/{REPO}/git/trees",
        "POST",
        {"base_tree": base_tree, "tree": tree_entries},
    )

    message = (
        "feat(SubOps): v0.5.1 复刻 DailyRoutines 交互链路\n\n"
        "- 修掉「进屋后立刻离开」：进出房屋共用同一个 SelectYesno，\n"
        "  旧逻辑无条件点「是」，误交互到出口就被送出屋、来回兜圈子。\n"
        "  现在文本含「离开」一律点「否」。\n"
        "- 菜单文字改从 AtkValues[i + 7] 读取（与 AddonSelectStringEvent 一致），\n"
        "  不再读 PopupMenu 里会过期的缓存。\n"
        "- 潜艇列表按槽位下标直接选，不再猜「远航报告 / 出发」子菜单。\n"
        "- 房屋入口按 DataId 2002737 定位；大厅里试错的门会被拉黑换下一个。\n"
        "- 修理改为一次性下发改完所有损坏部位，与 DR 的 RepairSubmarinePart 一致。\n"
        "- 清单改为 tag 固定地址，规避 jsDelivr 对分支名的长缓存。"
    )

    commit = api(
        f"repos/{REPO}/git/commits",
        "POST",
        {"message": message, "tree": tree["sha"], "parents": [head]},
    )
    print("commit:", commit["sha"])

    api(
        f"repos/{REPO}/git/refs/heads/main",
        "PATCH",
        {"sha": commit["sha"], "force": False},
    )
    print("main updated")

    # 同一个 tag 允许覆盖：旧的下载链缓存会失效，重发同名版本用得上。
    try:
        api(f"repos/{REPO}/git/refs/tags/v0.5.1", "DELETE")
        print("old tag v0.5.1 removed")
    except RuntimeError:
        pass
    api(
        f"repos/{REPO}/git/refs",
        "POST",
        {"ref": "refs/tags/v0.5.1", "sha": commit["sha"]},
    )
    print("tag v0.5.1 created")

    # 老的 v1.0.0 tag 会把 jsDelivr 的 @latest 抢走，删掉。
    for tag in ("v1.0.0",):
        try:
            api(f"repos/{REPO}/git/refs/tags/{tag}", "DELETE")
            print(f"tag {tag} deleted")
        except RuntimeError as ex:
            print(f"tag {tag} delete skipped: {ex}")

    # 对应的 release 也清掉，免得侧边栏还挂着 v1.0.0 Latest。
    try:
        releases = api(f"repos/{REPO}/releases?per_page=50")
        for rel in releases:
            if rel["tag_name"] == "v1.0.0":
                api(f"repos/{REPO}/releases/{rel['id']}", "DELETE")
                print("release v1.0.0 deleted")
    except RuntimeError as ex:
        print("release cleanup skipped:", ex)

    body = (
        "### 修复\n\n"
        "- **进屋后立刻自己离开**：进出房屋共用同一个「是 / 否」确认框，旧版无条件点「是」；"
        "人在大厅里误交互到出口时就被自动送出屋，然后重新进门，来回兜圈子。"
        "现在文本含「离开」一律点「否」。\n"
        "- **菜单匹配不到**：菜单项文字改从 `AtkValues[i + 7]` 读，不再读会过期的 PopupMenu 缓存。\n"
        "- **已收过的艇跑去点「出发」**：潜艇列表改为按槽位下标直接选，"
        "不再猜「远航报告 / 出发」子菜单，收没收藏由游戏自己决定。\n"
        "- **房屋入口定位**：按 DataId `2002737` 找门；大厅里试错的门会被拉黑，换下一个候选。\n"
        "- **修理**：一次性下发改完所有损坏部位，与 DailyRoutines 的 `RepairSubmarinePart` 一致。\n\n"
        "### 升级方式\n\n"
        "插件管理器里点「重新加载」，或重新安装。清单订阅地址请改为带 tag 的版本：\n\n"
        "```\nhttps://jsdelivr.b-cdn.net/gh/HAAAAe1/MyPlugins@v0.5.1/pluginmaster.json\n```\n"
    )

    # 同名 release 先删，避免 422。
    try:
        for rel in api(f"repos/{REPO}/releases?per_page=50"):
            if rel["tag_name"] == "v0.5.1":
                api(f"repos/{REPO}/releases/{rel['id']}", "DELETE")
                print("old release v0.5.1 removed")
    except RuntimeError:
        pass

    try:
        rel = api(
            f"repos/{REPO}/releases",
            "POST",
            {
                "tag_name": "v0.5.1",
                "name": "SubOps v0.5.1",
                "body": body,
                "target_commitish": "main",
            },
        )
        print("release:", rel.get("html_url"))
    except RuntimeError as ex:
        print("release create failed:", ex)

    print("DONE", commit["sha"])


if __name__ == "__main__":
    sys.exit(main())
