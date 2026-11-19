#!/usr/bin/env bash
# 打 Thunderstore 发布包：编译 → 组装 → 校验 → 输出 dist/<name>-<version>.zip
#
# 用法：
#   bash package.sh
#
# 产物结构（全部位于 zip 根目录，Thunderstore 要求）：
#   DSPLogisticsHub.dll
#   manifest.json
#   icon.png        256x256
#   README.md
#   CHANGELOG.md
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
SRC="$ROOT/src/LogisticsHub"
DIST="$ROOT/dist"
STAGE="$DIST/stage"
NAME="AerialMiningLogisticsStation"          # 必须与 manifest.name 一致
PY="/c/Users/Administrator/AppData/Local/Python/bin/python"

VERSION="$(sed -n 's/.*public const string Version = "\([0-9.]*\)".*/\1/p' "$SRC/C.cs" | head -1)"
if [ -z "$VERSION" ]; then
  echo "!! 读不到版本号（C.cs 的 Version 常量）" >&2
  exit 1
fi
ZIP="$DIST/$NAME-$VERSION.zip"

echo "== 1/5 编译（复用 build.sh，顺带重新导出几何）"
bash "$ROOT/build.sh" > /tmp/pkg_build.log 2>&1 || { tail -30 /tmp/pkg_build.log; exit 1; }
grep -E "个错误|个警告|error " /tmp/pkg_build.log | tail -3 || true

DLL="$SRC/bin/Release/DSPLogisticsHub.dll"
[ -f "$DLL" ] || { echo "!! 找不到 $DLL" >&2; exit 1; }

echo "== 2/5 组装 $VERSION"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$DLL" "$STAGE/DSPLogisticsHub.dll"
cp "$ROOT/icon.png" "$STAGE/icon.png"
cp "$ROOT/README.md" "$STAGE/README.md"
cp "$ROOT/CHANGELOG.md" "$STAGE/CHANGELOG.md"

# manifest.json：Thunderstore 的 description 上限 250 字符，和 build.sh 里给本地
# r2modman 用的长描述不是一回事，这里单独生成一份短的。
cat > "$STAGE/manifest.json" <<JSON
{
  "name": "$NAME",
  "version_number": "$VERSION",
  "website_url": "",
  "description": "空中采矿机物流运输站：底座钻孔采全星球的矿脉/原油/海洋，地热 6MW 自发电；50 泊位停靠环、22 座炮塔、43m 半球护盾；站体窗口 20 格 50 万 + 100MW 核电。",
  "dependencies": [
    "xiaoye97-BepInEx-5.4.17",
    "xiaoye97-LDBTool-3.0.3",
    "CommonAPI-CommonAPI-1.6.7"
  ]
}
JSON

echo "== 3/5 打包"
"$PY" - "$STAGE" "$ZIP" <<'PY'
import os, sys, zipfile

stage, out = sys.argv[1], sys.argv[2]
os.makedirs(os.path.dirname(out), exist_ok=True)
# 固定时间戳，保证同样内容打出来的 zip 字节一致
FIXED = (2026, 11, 19, 0, 0, 0)
names = sorted(os.listdir(stage))
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for n in names:
        p = os.path.join(stage, n)
        info = zipfile.ZipInfo(n, FIXED)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with open(p, "rb") as f:
            z.writestr(info, f.read())
print("zip:", out, os.path.getsize(out), "bytes,", len(names), "entries")
PY

echo "== 4/5 校验（Thunderstore 规则）"
"$PY" - "$ZIP" "$NAME" "$VERSION" <<'PY'
import json, re, sys, zipfile
from PIL import Image
import io

path, want_name, want_ver = sys.argv[1], sys.argv[2], sys.argv[3]
ok = True

def bad(msg):
    global ok
    ok = False
    print("  [FAIL]", msg)

def good(msg):
    print("  [ OK ]", msg)

with zipfile.ZipFile(path) as z:
    names = z.namelist()
    print("  根目录条目:", ", ".join(names))

    for need in ("manifest.json", "README.md", "icon.png"):
        if need in names:
            good("存在 " + need)
        else:
            bad("缺少根目录文件 " + need)

    for n in names:
        if "/" in n or "\\" in n:
            bad("条目不在根目录: " + n)

    # icon
    if "icon.png" in names:
        im = Image.open(io.BytesIO(z.read("icon.png")))
        if im.size == (256, 256):
            good("icon.png 256x256 (%s, %d B)" % (im.mode, len(z.read("icon.png"))))
        else:
            bad("icon.png 尺寸 %s，必须 256x256" % (im.size,))
        if im.format != "PNG":
            bad("icon 不是 PNG")

    # manifest
    if "manifest.json" in names:
        m = json.loads(z.read("manifest.json").decode("utf-8"))
        for k in ("name", "version_number", "description", "dependencies", "website_url"):
            if k not in m:
                bad("manifest 缺字段 " + k)
        if not re.fullmatch(r"[A-Za-z0-9_]+", m.get("name", "")):
            bad("name 只能含字母/数字/下划线: %r" % m.get("name"))
        if m.get("name") != want_name:
            bad("name %r != 期望 %r" % (m.get("name"), want_name))
        if not re.fullmatch(r"\d+\.\d+\.\d+", m.get("version_number", "")):
            bad("version_number 不是 MAJOR.MINOR.PATCH: %r" % m.get("version_number"))
        if m.get("version_number") != want_ver:
            bad("version_number %r != C.cs 的 %r" % (m.get("version_number"), want_ver))
        d = m.get("description", "")
        if len(d) > 250:
            bad("description %d 字符，超过 250" % len(d))
        else:
            good("description %d 字符 (<=250)" % len(d))
        urls = [m.get("website_url", "")] + list(m.get("dependencies", []))
        for u in urls:
            if u and not re.fullmatch(r"https?://\S+", u) and not re.fullmatch(
                    r"[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+", u):
                bad("url/依赖格式可疑: %r" % u)
        good("依赖 %d 个: %s" % (len(m.get("dependencies", [])), ", ".join(m["dependencies"])))

    # dll
    dlls = [n for n in names if n.endswith(".dll")]
    if dlls:
        good("包含程序集 " + ", ".join(dlls))
    else:
        bad("zip 里没有 dll")

print("  => " + ("全部通过" if ok else "有失败项"))
sys.exit(0 if ok else 1)
PY

echo "== 5/5 完成"
ls -lh "$ZIP"
