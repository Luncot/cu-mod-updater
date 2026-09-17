# -*- coding: utf-8 -*-
"""深查：CUCoreLib 最近 commits + Metadata-generator 是什么"""
import json, urllib.request

UA = {"User-Agent": "CU-ModUpdater/1.1.0", "Accept": "application/vnd.github+json"}

def get_json(url):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode("utf-8"))

print("=== CUCoreLib 最近 10 条 commits ===")
try:
    commits = get_json("https://api.github.com/repos/jimmyking9999999/CUCoreLib/commits?per_page=10")
    for c in commits:
        date = c["commit"]["committer"]["date"][:16].replace("T", " ")
        msg = (c["commit"]["message"] or "").split("\n")[0][:60]
        print(f"  {date}  {msg}")
except Exception as e:
    print(f"  失败: {e}")

print()
print("=== Metadata-generator 仓库详情 ===")
try:
    r = get_json("https://api.github.com/repos/jimmyking9999999/Metadata-generator")
    print(f"  描述: {r.get('description')}")
    print(f"  创建: {r.get('created_at','')[:10]}  主页: {r.get('homepage') or '-'}")
    print(f"  语言: {r.get('language')}  Topics: {r.get('topics')}")
    readme = get_json("https://api.github.com/repos/jimmyking9999999/Metadata-generator/readme")
    import base64
    text = base64.b64decode(readme.get("content","")).decode("utf-8", "replace")
    lines = [l for l in text.splitlines() if l.strip()][:14]
    print("  --- README 摘录 ---")
    for l in lines:
        print(f"    {l[:90]}")
except Exception as e:
    print(f"  失败: {e}")

print()
print("=== Metadata-generator 是否有 release/编译产物 ===")
try:
    rel = get_json("https://api.github.com/repos/jimmyking9999999/Metadata-generator/releases")
    if not rel:
        print("  无 release（源码仓库，需要自己编译或等作者发）")
    for r0 in rel[:3]:
        assets = [a["name"] for a in r0.get("assets", [])]
        print(f"  {r0.get('tag_name')} ({(r0.get('published_at') or '')[:10]}): {assets or '无附件'}")
except Exception as e:
    print(f"  失败: {e}")
