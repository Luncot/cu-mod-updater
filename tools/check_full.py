# -*- coding: utf-8 -*-
"""全量更新检查：GameBanana 6 mod + GitHub 作者全部仓库 + 未发布 commit"""
import json, re, time, datetime, urllib.request

UA_GB = {"User-Agent": "CU-ModUpdater/1.1.0 (+https://github.com)", "Referer": "https://gamebanana.com/"}
UA_GH = {"User-Agent": "CU-ModUpdater/1.1.0", "Accept": "application/vnd.github+json"}

def get_json(url, headers, timeout=20):
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))

def parse_ver(s):
    if not s: return (0,0,0,0)
    s = s.strip().lstrip("vV")
    s = re.split(r"[-+ ]", s)[0]
    nums = [int(x) for x in re.findall(r"\d+", s)][:4]
    while len(nums) < 4: nums.append(0)
    return tuple(nums)

LOCAL = {"CUCoreLib": "1.0.5", "QoL: Unknown": "1.0.4.8"}

def cmp(local, online):
    if local is None: return "— 本地未装"
    if not online: return "? 线上无版本号"
    a, b = parse_ver(online), parse_ver(local)
    return "↑ 可更新" if a > b else ("✓ 最新" if a == b else "· 本地更新")

print("=" * 64)
print("[1] GameBanana 全部 Mod（game 24260）")
print("=" * 64)
sub = get_json("https://gamebanana.com/apiv11/Game/24260/Subfeed?_nPage=1", UA_GB)
for rec in sub.get("_aRecords", []):
    mid, name = rec.get("_idRow"), rec.get("_sName", "?")
    author = rec.get("_aSubmitter", {}).get("_sName", "?")
    try:
        prof = get_json(f"https://gamebanana.com/apiv11/Mod/{mid}/ProfilePage", UA_GB)
        av = prof.get("_aVersion") or {}
        ver = av.get("_sVersionLabel") or prof.get("_sVersion") or "(未填)"
        ts = prof.get("_tsDateUpdated", 0)
        upd = datetime.datetime.fromtimestamp(ts).strftime("%m-%d %H:%M") if ts else "?"
        files = get_json(f"https://gamebanana.com/apiv11/Mod/{mid}/Files", UA_GB)
        latest_file = files[0].get("_sFile", "?") if files else "-"
        fdate = "?"
        if files and files[0].get("_tsDateAdded"):
            fdate = datetime.datetime.fromtimestamp(files[0]["_tsDateAdded"]).strftime("%m-%d")
    except Exception as e:
        ver, upd, latest_file, fdate = f"ERR {e}", "-", "-", "-"
    status = cmp(LOCAL.get(name), ver)
    print(f"  {name:<20} v{ver:<14} 本地 {LOCAL.get(name) or '未装':<10} {status}")
    print(f"    └ 最新文件: {latest_file} ({fdate})  mod更新: {upd}  by {author}")
    time.sleep(0.4)

print()
print("=" * 64)
print("[2] GitHub: jimmyking9999999 全部仓库（按推送时间）")
print("=" * 64)
try:
    repos = get_json("https://api.github.com/users/jimmyking9999999/repos?sort=pushed&per_page=20", UA_GH)
    for r in repos:
        print(f"  {r['name']:<28} push {r['pushed_at'][:10]}  ★{r['stargazers_count']}  {(r.get('description') or '')[:40]}")
except Exception as e:
    print(f"  获取失败: {e}")

print()
print("=" * 64)
print("[3] 已知仓库 Releases + 未发布 commit 检查")
print("=" * 64)
KNOWN = [
    ("jimmyking9999999", "QoL-Unknown", "1.0.4.8"),
    ("jimmyking9999999", "CUCoreLib", "1.0.5"),
]
for owner, repo, local in KNOWN:
    try:
        rel = get_json(f"https://api.github.com/repos/{owner}/{repo}/releases/latest", UA_GH)
        tag = (rel.get("tag_name") or "").lstrip("vV")
        date = (rel.get("published_at") or "")[:10]
        print(f"  {repo}: 最新 release {tag} ({date})  本地 {local}  {cmp(local, tag)}")
        # 未发布 commit：比较最新 commit 与最新 release
        commits = get_json(f"https://api.github.com/repos/{owner}/{repo}/commits?per_page=1", UA_GH)
        c = commits[0]
        cdate = c["commit"]["committer"]["date"][:10]
        cmsg = (c["commit"]["message"] or "").split("\n")[0][:50]
        rel_date = (rel.get("published_at") or "")[:10]
        if cdate > rel_date:
            print(f"    ⚠ 有未发布改动: commit {cdate} 「{cmsg}」> release {rel_date}")
        else:
            print(f"    无未发布改动 (最新 commit {cdate})")
    except Exception as e:
        print(f"  {repo}: 获取失败 {e}")
    time.sleep(0.3)
