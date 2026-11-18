# 上传协议关键约束（来自 Astral 的实测文档 + 反编译行号）

> 来源：`<Astral目录>`（`reincarnate.py` 文档、`api/UpsertUserAll.py`、`api/UserLogout.py`）。
> 那是 SDGB 国服（另一台私服）的实测结论；我们这台是 `aime.my-aqua.net`（AquaDX 系），规则未必 100% 相同，
> **但先按最严格的一方实现，实机再校准**。

## 1. 一次 UpsertUserAll = 一个 credit = 1~3 首

- ✅ 1 首可以；✅ 3 首可以；❌ **恰好 2 首会被服务端拒绝**。
- 所以分批必须凑 1 或 3：`[3,3,2]` → `[3,3,1,1]`。**这就是"用性隐 BASIC 填充"的真正原因**：
  最后一批剩 2 首时补 1 首 filler、剩 1 首时补 2 首 filler，凑成 3。
- `userPlaylogList` **必须非空**，否则整包被静默丢弃（returnCode=1 但不落库）。

## 2. 时间戳约束（服务端会校验）

```
userPlaylogList[i].userPlayDate = loginDateTime + 15s + i*15s   // i=0 是第一首
上传（DataSave）必须发生在 loginDateTime + 50s 之后
```
- 间隔 < 15s 会被判无效；首曲 < 15s 也不行。
- 跳过谱面模式下必须人为拉开每首的间隔（≥15s）并保证整批 ≥50s 才结算上传。

## 3. 会话与登出

- `UpsertUserAll.playlogId` 必须等于**本次登录**的 `loginId`。
- 一局结算后该会话即被消耗，继续上传必须重新登录：`UserLogin(isContinue=true)`（真机走 `ContinueProcess`），
  会返回**新的** `loginId/loginDateTime`。
- 登出必须 `UserLogout(type=1, 带 delayLog)`；`type=4 (TestIn)` 语义是测机顶号，会出问题。
- **不登出 / 异常退出 → 服务端 `isLogin=True` → 下次登录 `returnCode=100`，卡 15 分钟。**
  → 插件的铁律：任何结束/中止/异常路径都要走"超时登出"。

## 4. 数据节点语义

- 服务端对 `userData` 是**整表覆盖**语义：把旧值原样发回去等于什么都没发生。
- 差分节点若为空，服务端也看不出本局有变化。
- `userMusicDetailList` 主键 = `(musicId, level)`。
- 每条数组节点配一条 `isNew*` 位串，**一位对应一条**：
  - `0` = 存量更新（服务器已有同主键）
  - `1` = 新增（服务器没有）
  - `2` = 无变化（只有 `userFavoriteList` 用）
- 对已存在的条目填 `1` 会让服务端 HTTP 500（重复 INSERT）。
- `musicId >= 100000`（宴谱）→ `level = 10`；`trialPlayAchievement = -1`。

## 5. 字段对应（UserMusicDetail → UserPlaylog）

| UserMusicDetail | UserPlaylog |
|---|---|
| musicId | musicId |
| level | level |
| achievement | achievement |
| deluxscoreMax | deluxscore |
| scoreRank | scoreRank / isClear |
| comboStatus | comboStatus |
| syncStatus | syncStatus |

`achievement` 单位 = 1/10000 %（`1005000` == `100.5000%`）。

## 6. 对我们的两点直接结论

1. **批大小只能是 3（或 1）**，最后一批用 filler 补齐 —— 与用户要求完全一致。
2. **必须节流 + 必须重新登录**：每批之间要 `登陆 → ≥50s → 上传 → 登出(type=1) → 续关登录`。
   这正是 `Config.UploadCooldownSec = 50` 的由来；`TimeEstimator` 里"每次进选歌的固定开销"按这个算。
