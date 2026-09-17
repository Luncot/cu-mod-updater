# tools/ — 开发调试脚本

这些不是程序的一部分，是开发时用来**独立核对数据**的临时工具。
它们**没有做参数化，路径写死在作者机器上**，直接用需要自己改路径。

| 脚本 | 用途 |
|---|---|
| `scan_local.ps1` | 用 Mono.Cecil 独立扫描插件目录并打印 GUID/版本 —— 用来交叉验证程序内 `PluginScanner` 的解析结果对不对 |
| `check_full.py` | 全量核对：GameBanana 列表 + GitHub 作者仓库 + 未发布 commit，用来确认「程序显示的版本/来源」与线上真实情况一致 |
| `check_deep.py` | 单点深查：翻某个仓库的最近 commits / 元数据仓库来源（排查「这个 mod 的最新版到底是多少」时用） |

## 为什么保留

Mod 的版本信息分散在 N 网、GitHub Releases、GitHub 裸 commit、GameBanana 四处，
**程序的判断出错时，必须有一个不依赖程序自身的通道去核对**。
它们就是那个通道——之前发现的几个「版本解析错位」「下载文件名解析错位」
都是靠这些脚本对出来的。

## 运行

```bash
# Python 脚本（只需要标准库）
python tools/check_full.py

# PowerShell 脚本（需要本机已还原 Mono.Cecil，路径见脚本头部）
powershell -ExecutionPolicy Bypass -File tools/scan_local.ps1
```
