# FakerSwap 实机验收报告

日期：2026-11-15 凌晨（约 03:00 – 06:07）
环境：SDEZ 1.65 / MelonLoader 0.6.4 / AquaDX 私服 `aime.my-aqua.cn`
源账号：`<源账号>` uid <源uid> · R13700 · 104 条
目标账号：`<目标账号>` uid <目标uid> · 转生前 0 条

## 最终结果

| 项 | 值 |
|---|---|
| 上传批次 | 11 / 11（`uploads=11`） |
| 目标账号最终 | **99 条成绩 / RATING 13669**（源 104 条 / 13700） |
| 最终报文一致率 | 74 / 74 条与源数据逐条精确一致，0 条不一致 |
| 崩溃 | **0** |
| 结束方式 | `flow: wrap-up complete -> Finished`（正常登出） |

## 目标逐条验收

### ① 判定分布生成 + 真实谱长
```
[score] 采用 data-dev 判定分布: total=893 cp=276 perfect=549 great=4 good=60 miss=4 maxCombo=280 sync=0 → 本轨 notes=929 miss=4
[score] 判定表重建: 目标=93.1479% notes=929 cp=280 perfect=581 great=4 good=60 miss=4 maxCombo=907 拟合=93.1769% dx=71.98% perType=judge
[play] B 模式: 跳过前等待 45.0s (~30% × 151.2s, src=ChartCatalog) music=432
```
- 判定分布：CP / Perfect / Great / Good / Miss 自洽，MaxCombo 实算，SyncStatus 自洽
- 谱长：`src=ChartCatalog`（.ma2 时间轴解析，非占位值）

### ② 结算回读 == 目标
```
[score] 强制成绩校验 OK: 入参=931479 回读 raw=931479 (93.1479%) 期望 raw=931479
[score] DX 校验(OK): 源DX=2607/3309=78.7851%(0★) → 目标DX=78.79%(0★) 实际DX=78.79%(0★)
```
- 回读与目标完全一致（此前恒为 0）
- DX 分对齐源数据（不再刷出 4 星）

### ③ B 模式完整 credit + 报文与源一致
```
[play] B 模式: 跳过前等待 45.0s (~30% × 163.7s, src=ChartCatalog)
[play] B 模式: 等待完成 45.0s → 跳过 (判定明细自洽)
PayloadRewriter: patched 3 row(s) in UpsertUserAllApi (uploads=N, pendingLeft=0)
```
- 单 credit = 3 首不同歌曲（`11842 → 12015 → 12052`），slot 逐轨 +1
- 3 行成绩改写上传；实测报文与源数据逐条一致（见"最终结果"）

### ④ C 模式（AFK 真实游玩时长）
```
[play] C 模式: 挂机等待谱面自然结束 (已等 0s→160s) music=11813
[play] C 模式: 谱面自然结束 用时 162.7s → 进入结算
[score] Result 阶段补写判定表: before=99.x% → after=95.0227%（自然结算兜底）
```
- 整首播完再结算，playlog 时长天然真实
- 自然结算后由 `ResultProcess.OnStart` 兜底补写判定表

### ⑤ 稳定性
- **崩溃 0 次**（关闭 AquaMai `UX.DontRuinMyAccount` 之后；它会检测 AutoPlay 主动退出游戏）
- **pre-write 备份 = 10 份**（每 credit 一份 + 自动裁剪到最新 10）
- **机器锁不阻断触摸**：`InputLock` 只在 `GamePlay/TrackStart` 吞游戏内触摸；菜单/选歌/收尾页触摸放行；`ContinueProcess` 在栈上时**完全不吞**输入

## 收尾页面链（全部实机通过）

```
Result → MapResult → 写真(PhotoEdit) → 收集品获得(CollectionGet) → 收藏品浏览(CollectionProcess)
       → 好友赛季(FriendSeasonRanking) → 礼物(GetPresent) → 解禁乐曲(UnlockMusic) → DataSave → Continue
```
- `CollectionProcess` 需要 `sequence==Start → 写 Update 再 SetSubSequence(Exit)`
- 写真页 `showSkip=False` 时仍可直接 `OnTimeUp`

## 已知未完成项

1. **续关（CONTINUE）**：功能可用（等待态注入触摸+按键即可推进，同一会话进入下一批，省 25s），但**续关后第一轨 `GameProcess.OnStart` 必空引用崩溃**（同一会话中上一局进程残留）。当前默认 `ContinueRelogin=false` 走刷卡兜底，稳定。
2. **区域爬取（选区）**：MODE SELECT 的 OK 在 `_decidedSelectModeType=-1` 时按不动，nav 会强切到选歌页，导致「区域选择」整屏被跳过 → 爬区实际未工作。UI 上的区域复选框面板已实现，但底层未产生选区页。
3. **选区倒计时安全**：因为从未进入选区页，用户报告过的"选区倒计时残留到游玩中导致崩溃"**未复现、未验证**。

## 安全红线遵守情况

- 所有写入经 `WriteGuard` + 写前必备份
- 全程 `Process.Kill()` 仅用于**开发期强制重启**，产品逻辑内绝不调用；结束一律走游戏自身登出/超时
- 建号后「名字输入页」保持人工，未做任何自动确认
