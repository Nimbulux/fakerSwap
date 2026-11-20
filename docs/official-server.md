# 国服（SDGB 1.55 / maimai-gm.wahlap.com）实测约束

> 全部来自 2026-11-20/21 的实机抓包与对照（手动游玩 vs 插件）。

## 1. returnCode 语义

```
{"returnCode":1,"apiName":"..."}   = 成功
{"returnCode":0,"apiName":"..."}   = 失败（游戏会弹「数据保存失败 无法反映本次游戏成绩」）
```

注意与常见 SEGA 私服实现相反，不要按 0=成功排查。

## 2. 每个 credit 都必须有新的登录会话

`UpsertUserAll.playlogId` 必须等于**本次登录**的 `loginId`。

- 第 1 个 credit：`playlogId` = 刷卡登录返回的 `loginId`；
- 续关后：游戏调用 `Process.ContinueProcess.ReLogin(id)` → 发出
  `UserLoginApi { "isContinue": true }` → 返回**新的 loginId**；
- 第 2 个 credit 的 `UpsertUserAll.playlogId` 必须等于这个新 `loginId`。

手动游玩实测（2026-11-21 01:52）：

```
01:52:31  Upsert  plid=7710309965335887980  lpc=1  playCredit=1  isNew=111  → rc=1
01:52:36.4 玩家点按 4 号键 50ms（state 2→3）
01:52:39.36 ContinueProcess.ReLogin(id=0)
01:52:39.36 UserLoginApi isContinue=true → loginId=7710313079187177581
02:02:22  Upsert  plid=7710313079187177581  lpc=1  playCredit=1  isNew=011  → rc=1
```

要点：

- 续关是**点按一次** 4 号键（约 50ms），不是长按；游戏自己等约 3 秒后调 ReLogin；
- **续关后 `lastPlayCredit` / `playCredit` 仍然是 1**，不要改成 2；
- 每个存档包里 `userMusicDetailList` 只带本 credit 修改过的行（不会累积上一个 credit 的行）。

## 3. 包大小限制

- 每包只能 **1 或 3 首**，**2 首包会被服务端拒收**；
- 長尺曲占 2 轨、会形成 2 首包 → 官方服建议设置「长版歌曲 = 跳过」。

## 4. 上传时间下限（Astral 实测）

- 首曲 `userPlayDate` ≥ 登录后 15 秒，之后每曲 +15 秒；
- **登录后至少 50 秒**才允许上报，否则整包被直接丢弃；
- 所以 B·秒跳模式下也要保证登录后等够时间再存档。

## 5. 登录/登出

- 登出 `UserLogoutApi` 的 `type` 必须为 **1（Logout）**，不要用 4（TestIn）；
- 客户端 `AimeCode`/`aime.txt` 为空时只能靠 AimeGen 物理读卡。

## 6. 票券

- 官方服默认**不要选票**（测试服安全）；
- 需要时用手动「下次用票 / 选票保持」，不要自动确认。

## 7. 判定/成绩真实性

- 手动游玩与插件的差异：插件会把 `UpsertUserAll` 的曲目行改成目标成绩；
- `UploadUserPlaylog`（单曲 playlog）仍由游戏生成，若其 `achievement`/`deluxscore`
  与改写后的行差异过大，官方服可能校验不一致——当前流程依赖游戏先按目标判定结算；
- 已被服务端拒绝的包可在 `Package\Sinmai-Assist\NetworkLogs\` 里按时间对照请求/响应。

## 8. 用户接管（UserHoldSec，默认 3 秒）

每个需要选择的界面（MODE SELECT / 区域 / 票券 / 旅行伙伴 / 选歌各屏 / 续关 / Entry 确认）
进入后先停 `UserHoldSec` 秒：

- 期间**释放机台锁**，玩家可以直接操作；
- 检测到真人输入（按钮/触摸）→ 该屏整屏交给玩家，插件不再注入，直到界面切换；
- 无操作 → 3 秒后插件继续原自动流程。

配置项：`"UserHoldSec": 3.0`（0 = 关闭）。

## 9. 输入默认解锁 + 时间预估

- `LockInput` 默认改为 **false**：机台锁不再默认拦截玩家输入，等待/空档时可以直接玩；
  插件仍通过注入按键推进，按用户实测「游戏界面操作不影响最终结果」。
- `EstimateCreditOverheadSec` 已计入 `UserHoldSec`：每个 credit 约 8 次界面等待 × 3s = **+24s/credit**，
  预估总时长会相应变长。
