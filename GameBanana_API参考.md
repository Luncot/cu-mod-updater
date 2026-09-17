# GameBanana API v11 实测参考（Casualties: Unknown）

> 实测日期：2026-08-21 · 全部端点免费、**无需 API Key**（v11 公开接口）
> 游戏：Casualties: Unknown → **game id = `24260`**
> 本游戏在 GameBanana 上的全部 Mod：6 个（CUCoreLib、PROSTHETICS、ICE-CREAM、Casualties: XL、QoL: Unknown、Custom Structures）

---

## 1. Mod 列表（找游戏下所有 mods）

```
GET https://gamebanana.com/apiv11/Game/24260/Subfeed?_nPage=1
```

**响应结构**（`_aRecords` 是数组，`_aMetadata._nRecordCount` 是总数）：

```json
{
  "_aMetadata": { "_nRecordCount": 6 },
  "_aRecords": [
    {
      "_idRow": 684559,                      // ← modId，后续请求都用它
      "_sName": "QoL: Unknown",
      "_aSubmitter": { "_sName": "Jimmy_king" },   // ← 作者（权威来源）
      "_sProfileUrl": "https://gamebanana.com/mods/684559"
    }
  ]
}
```

- 分页：`_nPage` 从 1 开始递增。
- **作者信息就以 `_aSubmitter._sName` 为准**，别用内置列表硬编码——Subfeed 实时返回的才是权威数据。

---

## 2. Mod 详情（作者 / 更新时间 / 版本 / 图片）

```
GET https://gamebanana.com/apiv11/Mod/684559/ProfilePage
```

**关键字段**：

```json
{
  "_sName": "QoL: Unknown",
  "_aSubmitter": { "_sName": "Jimmy_king" },
  "_tsDateUpdated": 1781244443,              // ← 更新时间（Unix 秒，UTC）
  "_aVersion": { "_sVersionLabel": "v1.0.4.5" },
  "_sVersion": "v1.0.4.5",                   // 有些 mod 直接挂这个字段
  "_aPreviewMedia": {
    "_aImages": [
      {
        "_sBaseUrl": "https://images.gamebanana.com/img/ss/mods",  // 前缀
        "_sFile": "6a281ce4dbf26.jpg",        // 文件名 → 拼完整 URL
        "_sFile100": "...", "_hFile100": ..., "_wFile100": ...,     // 100px 缩略图
        "_sFile220": "...",                                        // 220px
        "_sFile530": "..."                                        // 530px
      }
    ]
  },
  "_aFiles": [ ... ]
}
```

**图片 URL 拼接规则**：`_sBaseUrl + "/" + _sFile` = 原图；小图用 `_sFile100` / `_sFile220` / `_sFile530` 字段值直接做 URL。

> ⚠️ **必须带 UA 和 Referer 才能加载图片**（这是之前"图片加载不出来"的根因）：
> ```csharp
> http.DefaultRequestHeaders.UserAgent.ParseAdd("CU-ModUpdater/1.1.0 (+https://github.com)");
> http.DefaultRequestHeaders.Referrer = new Uri("https://gamebanana.com/");
> ```

---

## 3. Mod 文件列表（下载直链）

```
GET https://gamebanana.com/apiv11/Mod/684559/Files
```

**响应**：数组，每项：

```json
{
  "_sFile": "qol_unknown.zip",
  "_nFilesize": 3835091,
  "_sDownloadUrl": "https://gamebanana.com/dl/1724126",
  "_nDownloadCount": 12345
}
```

- 同一个 mod 可能有多个文件（旧版、快速安装版…），按需选（比如优先最新 `_nFilesize` 大 / 名字含版本号的）。
- `_sDownloadUrl` 会 **302 重定向**到真实文件，HttpClient 默认跟随即可。
- 下载时同样带 UA，否则可能被拒。

---

## 4. 检查更新的建议流程（给更新器用）

1. 启动时扫描本地 mod → 拿 GUID/文件名 → 映射到 `modId`（存配置：`%APPDATA%\CU-ModUpdater\modupdater_config.json`）。
2. 定期 `Subfeed` 拉全列表，用 modId 直接比对（Subfeed 记录里没版本号，比版本走 ProfilePage）。
3. 需要比对版本时：`ProfilePage._tsDateUpdated` 或 `_aVersion`。
4. 要下载：`Files` → 挑文件 → `_sDownloadUrl`。

**推荐源优先级**：GameBanana 免费无 key、覆盖本游戏全部 mod，**优先于 Nexus（要 key + Premium）**；GitHub 保留作手动配置的补充源。

---

## 5. 已实测的完整响应示例

### Subfeed（6 个 mods 全部）

| modId | 名称 | 作者 |
|-------|------|------|
| 701298 | CUCoreLib | Jimmy_king |
| 684249 | PROSTHETICS | gilgameshh |
| 689892 | ICE-CREAM | gilgameshh |
| 683438 | Casualties: XL | TheSoftEeveeBoy |
| 684559 | QoL: Unknown | Jimmy_king |
| 684563 | Custom Structures | Jimmy_king |

### ProfilePage 684559（QoL: Unknown）

- 版本：v1.0.4.5
- 更新时间：2026-06-12 06:07 UTC
- 图片：5 张
- 文件：3 个（qol_unknown.zip 3.8MB / qol_quick_install.zip 4.5MB / qol_unknown_f278d.zip 3.8MB）

---

## 6. 常见坑

- `Game/{id}/Subfeed` 的 id 是 **game id（24260）**，不是 mod id——填错返回空数组且不报错。
- `_tsDateUpdated` 是 Unix 秒，要转 DateTime：`DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime()`。
- 图片域名 `images.gamebanana.com` 会校验 UA/Referer，裸 HttpClient 必挂。
- API 无需认证但有速率上限（免费层约每分钟 30 次），批量请求间加个小延时（300ms）即可。
