# 自动化流程改造：每个界面都用真实输入操作（不跳屏）

> 2026-11-15 23:30 · SDEZ 1.65 / AquaMai 1.9.24 / 私服 AquaDX
>
> 用户要求：**不要直接跳过界面，每个界面都正常操作**；选区、选票两个界面必须在流程里；
> 课题曲要在**本 credit 内**打完（占用一个轨道位置）；测试服**不能默认用票**；
> 国服必须走**续关**（重新刷卡在国服不可用）。

## 1. 真实输入注入（`Flow/InputInject.cs`）

Harmony Postfix：`InputManager.GetButtonDown` / `GetInputDown` / `GetInputLongPush`。
按 `(monitor, button)` 精确注入**一次**边沿，读一次即消费、3 帧内未消费自动过期（不可能卡键）。

键盘/机台按键对照（实机键盘映射，便于人工复现）：

| 键 | Button |
|---|---|
| W | Button01 |
| E | Button02 |
| D | Button03（菜单右移） |
| C | Button04（決定/是） |
| X | Button05（否/返回，**禁用**） |
| Z | Button06（菜单左移） |
| A | Button07 |
| Q | Button08 |

## 2. 逐屏处理（全部走游戏自己的输入链路）

| 界面 | 处理 | 依据 |
|---|---|---|
| Entry 受付 / ConfirmPlay | 已有的 nav 连点 + 真实按键 | — |
| PlInformation（プレイ情報/下载） | `IsPlayPlInfoEnd` 钩子（数据未下载完不跳） | AquaMai 同款 |
| **MODE SELECT** | 真实 Button04 **按两次**（8→24/25→33/35→39…）；见 `docs/mode-select.md` | 运行时 IL |
| **写真确认（PhotoShootProcess）** | `PhotoCofirm(1)` 时走游戏自己的「不拍」分支：CloseWindow + ChangePhotographingSequence(Confirmation) + GotoNextProcess；**只有存在该进程才动作**（国服无此流程） | 运行时 IL |
| **登录奖励（LoginBonusProcess）** | **不 Fade**，等游戏自己结束 —— 它会按 `IsGotoPhotoShoot/IsCourseMode/IsFreedomMode` 之后进入 **RegionalSelectProcess**（以前强行跳选歌，整屏选区被跳过） | 运行时 IL |
| **区域选择（RegionalSelectProcess）** | 真实输入：subState 2/3 → Button04 推进；subState 6 → Button03/06 移光标到目标区，Button04 決定；7/8 → Button04 关窗。之后游戏自己走 ToDecide(10)→Decide(13)→Release(16)，`MapHooks` 靠 subState>=10 自然确认区域 | 运行时 IL `InputUpdateState` |
| **选票（TicketSelectProcess）** | **绝不按決定选票**（测试服会写异常数据）：调用游戏自己的 `TimeUp()`（= 倒计时结束、不选票进入下一屏），只调用一次 | 运行时 IL `TicketSelectProcess.TimeUp` |
| 角色选择（CharacterSelectProces） | 真实 Button04 決定 | — |
| 选歌 / 游玩 / 结算 / 收尾页 | 原有逻辑（Result NEXT、写真编辑、收藏品等） | — |
| **CONTINUE（续关）** | 真实按住 Button04 ~2.2s（state 2/3 才按）；**只用 4 号键，绝不碰 5 号键** | 实机验证 |
| DataSave / 登出 | 游戏自身链路 | — |

## 3. 续关路径（国服唯一可用）

- `ContinueRelogin=true`（`config.json`）。
- 续关成功后 **必须重置游戏轨道计数**：`GameManager.MusicTrackNumber = 1` + `SelectMusicID` 清零。
  否则新 credit 第一轨 `GameProcess.OnStart` 用 `MusicTrackNumber-1=2` 取分数列表得到 null → NRE，
  随后 `ResultProcess.OnStart` 的 `GamePlayManager.GetAchivement` 再 NRE 整局崩（实机 22:57 / 23:05）。
  日志标志：`[flow] 续关后重置游戏轨道计数: MusicTrackNumber=1, SelectMusicID 清零`。
- 旧局 `GameProcess/TrackStartProcess` 可能以 `Released` 状态留在栈上：`HasLiveGameProcess` 只算未 Released，
  并有 60/90s 有界强制回收（`Navigation.ReleaseGameProcesses`，只调游戏自己的 `ReleaseProcess`）。

## 4. 课题曲：本 credit 内完成（占一个位置）

- 选区确认后（`MapHooks.SelectedAreaId > 0`），**直接从区域主数据**排 1 首：
  `MapHooks.QueueAreaBonusMissions(areaId, 1)`（`Map.xml → BonusMusicId → MapBonusMusic.xml`），
  不依赖选歌页「ちほー課題曲」分类的懒加载（那是最初检测不到课题曲的原因）。
- `ApplyMissionToCurrentCredit()`：选歌页第一轨之前，把本 credit 的 **filler 槽**（最后一批的占位曲）
  换成课题曲；没有 filler 就换最后一轨，被挤掉的成绩项排到后面批次（**绝不丢成绩**）。
  日志：`[flow] 课题曲占用本批第 3 轨: 11975-2@80.0000%（本批 3 轨；共 34 批）`。
- **只能放进满 3 轨的 credit**：轨道数 <3 的 credit 会让 `GameProcess.OnStart` 读 `SelectMusicID[2]==0` 而 NRE。
- 每首课题曲打完记入 `PlayedMissionMusicIds`，下一 credit 自动排下一首 → 逐步跑区。

## 5. 已验证的实机日志（2026-11-15 23:21~23:26）

```
[map] 区域已确认: id=650051 name="メダリストちほー"
[flow] 课题曲占用本批第 3 轨: 11975-2@80.0000%（本批 3 轨；共 34 批）
[flow] 续关成功（游戏自动继续）→ 进入下一批 stage=TrackStart
[flow] 续关后重置游戏轨道计数: MusicTrackNumber=1, SelectMusicID 清零
[nav] MODE SELECT 注入 Button04(決定) #1/#2/#3 → CreditJudgeInit → … → End
[nav] 选区: 光标=1 按 4 号键決定 (areaId=650051)
[crash] GameProcess.OnStart 现场: trackNo=1, musicIds=[11813,0,0,0]   ← 不再崩溃
```

## 6. 仍需观察

- 每个 credit 的课题曲是否都从区域表成功排入（`区域表直接排入课题曲` 日志）。
- 选票页：本服流程暂未出现该界面；出现时日志应是 `选票页: 已调用 TimeUp（不消耗票）`。
- 上传对账：`credit N upload finished (… DataSave#N, pendingRewrite=…)`，pendingRewrite 应在请求发出后归零。

## 7. 课题曲数量上限（防止队列无限增长）

每插入一首课题曲，会把被挤掉的 transfer 成绩项排到后面的新批次（保成绩不丢），所以批次数会从 33 增加。
如果不设上限，每 credit 都插一首 → 每个 credit 都新增一批 → 队尾永远后退（历史 bug 复现）。
现在用 `ConfigData.MaxMissionCredits`（默认 **3**）限制本次运行最多打几首课题曲：

- 每成功插入一首 `_missionsPlayed++`；达到上限后不再从区域表排歌，队列稳定在 33+3=36 批。
- 想多跑区就调大 `MaxMissionCredits`（每多一首 ≈ 多一批、多 ~3.5 分钟）。
- 已排过的课题曲记入 `PlayedMissionMusicIds`，同一首永不重复。

运行中的标志日志：

```
[flow] 区域表直接排入课题曲 1 首（area=650051）
[flow] 课题曲占用本批第 3 轨: 10201-2@80.0000%（本批 3 轨；共 35 批）
```

---

## 8. 2026-11-16 追加：课题曲列表优化 + 选票 UI + 陈旧进程

### 8.1 课题曲「向后排」但不堆 filler

旧实现：课题曲挤掉一条成绩 → 新建一批 [成绩 + 2×性隐432]，每首课题曲都叠一批 filler（用户反馈"越叠越多"）。
现在 `ApplyMissionToCurrentCredit` = **重排剩余队列**：

1. 收集当前批 + 后面所有批次里**未打**的成绩项（保持顺序）；
2. 取 1 首新课题曲（每个 credit 最多 1 首，`MaxMissionCredits` 为总上限，已可配置/序列化）；
3. 重新分批：`[成绩, 成绩, 课题]`，正好 3 槽；课题用完就是 `[成绩×3]`；
4. 不新增任何 filler，批次数稳定（实测 33→34 后不再增长）。

防死循环：
- `_missionQueuedThisCredit`：每个 credit 只重排一次；
- `_missionPlacedIds`：选歌页扫描会把同一首课题曲反复塞回 Pending，这里过滤已排过的曲目；
- `BeginCredit` 清理 Pending 里已排过的 id，避免挡住下一首。

日志：`[flow][mission] 课题曲占本批第 3 轨: 11903-2 → 剩余重排 34 批（无 filler 堆积）`

### 8.2 选票 UI（默认不用票）

选票页默认调用游戏 `TimeUp()`（不消耗票）。UI「设置」新增：

- **下次用票**（一次性）：下一次选票页不自动跳过，交给操作员手选；选完自动记住票 ID
  （读 `TicketSelectMonitor._decidedSelectTicketID`）。
- **选票保持**（复选）：每次都用上一次手选的票 —— 把 `_ticketDefaultRefID` 设到该票在
  `_ticketSelectTicketData` 里的下标，再按 4 号键決定；票不在列表里则回退到不选票。

配置字段（`config.json`）：`TicketUseNext` / `TicketUseKeep` / `TicketLastManualId`。

### 8.3 陈旧登录会话的界面处理（实机 17:27/17:31）

上一轮结束后游戏可能仍处于已登录状态（流程直接复用），此时登录奖励等旧屏会以 **Released**
状态留在进程栈上。修复：

- `Navigation.FindLiveByName(...)`：只把**未 Released** 的 LoginBonus/TicketSelect/CharacterSelect
  当作"还在显示"；否则会误判、15s 后 Fade 到选歌页，把选区整屏跳过。
- 登录奖励页 5s 未结束 → 调用游戏自己的 `TimeUp()`（monitor `_isTimeUp/_isDecidedOK` → IsEnd →
  进程自己 Fade 到 `RegionalSelectProcess`）；30s 才最后兜底 Fade。
- `MapHooks` 的"操作员手动换区"判定：导航 `DriveRegionalSelect` 正在驱动光标时不算手动
  （否则会 `CancelMission` 把刚排好的课题曲清掉，实机 17:40 pending 丢失）。
- `InputInject`：`GetInputLongPush` 的官方参数名拼错为 `monitoId`（少 r），Harmony 按名绑定，
  已改成同名；`GetInputDown`/`GetInputLongPush` 用显式参数类型避免重载歧义。

---

## 9. 2026-11-16 晚：选区/选票改为「直接篡改」

### 9.1 选区：瞬间跳光标（不再逐步按 3/6）

正式机台选区只有 30 秒，逐步移动（每步 0.25s）根本走不完 200 多个区。现在：

```csharp
// 直接写 _selectCursolIndex[monitor] = 目标下标，再调 monitor.ListScroll(目标) 同步 UI
SetArrayInt(process, "_selectCursolIndex", monitor, target);
monitor.ListScroll(target);
// 然后按一次真实 4 号键，仍走游戏自己的 CanVisitRegion → RegionSelectDecide 校验
```

实机验证（21:48:31，光标从 137 瞬移到 3，1 秒内完成决定）：

```
[nav] 选区: 直接跳光标 137 → 3（不再逐步移动）
[nav] 选区: 光标=3 按 4 号键決定 (areaId=600060)
[map] 区域已确认: id=600060 name="IOSYSちほー"
```

超时保护同步调整：`AreaSelectTimeoutSec` 默认 3s→8s，且 `SelectedAreaId>0` 或导航正在驱动时
MapHooks 不再抢跑 `TimeUp`；`AreaSelectDriveActive` 窗口 3s→8s。

### 9.2 选票：直接置 monitor End（state=44），不等 30 秒倒计时

`TicketSelectMonitor.IsEnd()` 的定义就是 `_state == 44`；`TicketSelectProcess.OnUpdate` 在两个
monitor 都 IsEnd 后才会 Released → Fade 到 GetPresentProcess → 选歌。所以直接：

```csharp
mon._isTimeUp = true;  mon._isDecidedOK = true;  mon._isDecidedEntry = true;
mon._decidedSelectTicketID = 0;      // 0 = 不选票
mon._state = 44;                      // MonitorState.End
```

实机验证（21:48:43，<1 秒通过选票页）：

```
[nav] 选票页: 直接置 End(state=44) 跳过倒计时 → 不选票（票 ID=0） monitors=2
```

UI 语义保持不变：

- 默认：票 ID=0（不选票）；
- 「选票保持」：票 ID = `TicketLastManualId`（之前人工选过的那张）；
- 「下次用票」：这一次不强制 End，等操作员手动选（C 键=決定），选完记录票 ID 并复位标志。

### 9.3 账户转生已完成

2026-11-16 01:53 的整轮任务已把 98 条成绩全部转生成功（`wrap-up complete -> Finished（全部完成（98 条）`）。
之后再点开始会立即提示「计划为空：missing=0 alreadyDone=98」——这是正常状态，不是失败。
需要换源文件/换目标账号时再开始即可。

---

## 10. 2026-11-16 深夜：UI 下拉框修复 + 关闭测试自动刷卡

### 10.1 下拉框「点了没反应」根因

`DrawDropdownOverlay` 在 `EndContent()` **之后**绘制，而 `_dropdownY` 是**内容坐标**；此时
`NativeCanvas.ScreenY()` 因为 `_inContent=false` 会原样返回内容坐标 → 列表被画到窗口外
（内容坐标 923 直接当成客户区 923，窗口高度只有 733），所以点击有反应（状态已置 open）但看不见，
下一次点击又被「点外面关闭」逻辑吃掉。

修复：`NativeCanvas.ContentToScreenY(int)`（永远套用 `ContentTop + y - ScrollY`）+
`DrawDropdownOverlay` 先换算成客户区 Y 再画/命中测试，并修正上翻时的 `controlY` 换算。

实机验证（23:11，window 1350x1100）：

```
[ui] dropdown overlay draw open=1 opts=2 y=923   ← 打开
UI: setting saved: 游玩模式=Duration（真实时长）  ← 点选项生效
UI: setting saved: 游玩模式=Judge（秒跳）         ← 切回
```

「游玩模式 / 写入方式 / 难度下限 / 难度上限」四个下拉共用同一套代码，均已实测可用。

### 10.2 关闭测试用自动刷卡

`RebirthFlow.AutoSwipe` 之前**无条件**调用 `LoginHooks.RequestLogin`（写 aime.txt + 回车），
即使 `AutoSwipe=false` 也照样刷卡 —— 这就是测试期自动登录的来源。现在：

```csharp
if (!ModConfig.Data.AutoSwipe) return;   // 默认 false：停在登录页等人工刷卡
```

`AutoSwipe` 已加入 config 序列化，部署配置为 `"AutoSwipe": false`。
`Navigation.RequestLogin` 是没人调用的死 API（LoginHooks 只把自己的 RequestLogin 挂到
`Navigation.LoginHandler`），不受影响。

### 10.3 本机 UI 自动化脚本（tools/uitest.ps1）

DPI-aware 找窗口、`WM_LBUTTONDOWN/UP` 直投（不依赖前台焦点）、滚轮、截图：
用于实机验证下拉框/开关，坐标按 preview 0.943 缩放换算。

---

## 11. 2026-11-18 深夜：新手教程选择页跳过（5 号键）

新账号首次登录会停在教程选择页（あそびかた説明を選んでね），以前流程没有处理 → 一直卡在
`等待干净的选歌页… (Other)`。实机截图里屏幕底部明示：**SKIP チュートリアルをスキップ = 5 号键**。

游戏 IL（`Process.TutorialSelectProces.DetailInputOnDisp`，参数是 `ButtonSetting` 枚举，注意值偏移）：

| 按键 | 枚举值 | entryStatus | 结果 |
|---|---|---|---|
| Button06 | 5 | 1 | SelectBasicTutorialSelect → TutorialPlayed=1（きほん） |
| Button03 | 2 | 2 | SelectNewTutorialSelect → TutorialPlayed=2（新要素） |
| **Button05** | **4** | **3** | **SelectNotTutorialSelect → TutorialPlayed=0 → 直接进选歌（SKIP）** |

> 之前按 4 号键是错的：4 号键（枚举值 3）在这个界面没有绑定。

实现 `Navigation.HandleTutorialSelect()`：
- 教程选择页 `state=Disp(3)` 时注入真实 **5 号键**（0.8s 一次）；
- 0.8s 后仍停在 Disp(3) → 直接 `NavReflect.SetEnum(process, "_entryStatus", 3)` 兜底；
- 若真进了教程游玩页（`TutorialProcess`），只等待不再强按（避免破坏教程完成标记）。

---

## 12. 2026-11-18 深夜：课题曲只打「已出现」的 + 中止/填充曲上传空成绩

### 12.1 课题曲：不再按区域表提前排整张列表

用户反馈：**课题曲是游戏里出现后才需要打**，旧逻辑却用 `Map.xml → BonusMusicId → MapBonusMusic` 把
整个区域表（例如 650052 有 81 首）里未打过的歌提前排进计划 → 把还没出现的课题曲提前打了。

现在：

- `TickWaitMusicSelect` 里**删除**了 `MapHooks.QueueAreaBonusMissions(...)` 调用；
- 课题曲唯一来源 = 选歌页游戏自己的「ちほー課題曲」列表（`_combineMapTaskDataList` 扫描）；
- 扫描到的条目难度改用 `MinLevel`（默认 EXPERT），挑战曲用 `challengeUnlockDiff`；
  不再信任条目里不可靠的 `difficultyId`（实机读到 0，会打出绿谱）；
- 已打过的曲子（`PlayedMissionMusicIds`）不再排队；
- 没出现就不排 → 该 credit 就正常打 3 首成绩，不硬塞课题曲。

### 12.2 中止 / 填充曲：上传空成绩（0%）

实机网络日志证据：中止后本轨（12052）与填充曲 432 分别以 **100.1374% / 45.4623%** 上传了
（Sinmai-Assist 自动打歌把谱面打完了，而旧代码对它 `return true` 保持原生）。

现在 `SetForceAchivement` 钩子里，`SkipCurrentTrack`（中止轨 / filler）改为**强制 0% 全 MISS**
（仍正常选曲、正常执行、正常结算、正常占 3 轨），强制**全 MISS** → 上传 0.0000%，不会再带 autoplay 的高分。`PickMissCount` 对 0% 只能降到 ~50%，所以加了 `ForceAllMiss` 直接全 MISS。

---

## 13. 2026-11-19 凌晨：选歌倒计时崩溃 + 中止不再放性隐

### 13.1 选歌页倒计时导致游戏崩溃（PassTicketStatus.FadeOut NRE）

实机 Crash Handler 栈：

```
System.NullReferenceException
  at UnityEngine.Animator.Play(...)
  at PassTicketStatus.FadeOut()
  at Monitor.MusicSelectMonitor.OnGameStart()
  at Process.MusicSelectProcess.OnGameStart()
  at Process.MusicSelectProcess.TimerCountUp()
  at Process.GenericProcess.CheckCompleted()
  at Process.GenericProcess.UpdateTimer(...)
```

选歌页的「选择倒计时」到 0 时会自动开局，走 `MusicSelect.OnGameStart → PassTicketStatus.FadeOut`，
票券状态为 null 时整局崩。用户观察到平时没触发是因为倒计时没启用。

修复（`Core/CrashFixes.cs`）：

- `MusicSelectTimerCountUpGuard`：任务运行期间 Prefix 返回 false，直接跳过 `TimerCountUp`，
  倒计时不再自动开局（插件本来就是显式选曲 + 按決定）；
- `MusicSelectOnGameStartGuard`：Finalizer 兜底吞掉这里的 NullReferenceException，
  避免任何残留路径把游戏带崩。

### 13.2 中止：剩余轨保留占位，但立刻跳过（占满 3 轨）

服务端/游戏**不接受只有 1 首的成绩**（一局必须是 3 轨），所以中止不能把剩余轨删掉。现在：

- 中止时把 `_abortSlot + 1` 起的槽位置 `SkipPlay = true`（保留原计划曲目，**不放性隐**、不删槽位）；
- 当前轨 `SkipCurrentChart()` 立刻跳过，成绩按「全 MISS / 0.0000」上传；
- 剩余占位轨照常选曲把 credit 占满 3 轨，但一进谱面就每隔 2s 调 `SkipCurrentChart()` 秒跳到结算，
  **不再挂机播 2 分多钟**；
- 与 `SkipCurrentTrack` 绑定：成绩全 MISS（0.0000%），且不计入转生完成数。

日志：

```
flow: 中止 → 当前轨(第 N 轨)跳过不改，剩余 M 轨保留占位并立刻跳过（空成绩，不挂机播放）
[play] 占位轨立刻跳过（空成绩） ...
```

---

## 14. 2026-11-19 凌晨 2：选歌进入改为「模拟真人按 1 号键」

用户指出：直接调 `MusicSelectProcess.GameStart()` 会跳过游戏自己的选曲状态机（票券/倒计时/过场），
实机出现 `PassTicketStatus.FadeOut()` NRE 崩溃；正确做法是像真人一样移动光标到目标曲目、再按決定键进入。

现在 `Navigation.StartCurrentSelection()`：

1. 第一次：注入真实 **1 号键**（`InputInject.Press(Button01)`），日志
   `[nav] 选歌决定: 模拟真人按 1 号键进入 (musicId-level)`；
2. 1.5s 内游戏没走 → 再补一次 1 号键；
3. 仍没走 → 回退直接 `GameStart()` 保底（日志 `1 号键 1.5s 未进入 → 回退直接 GameStart（保底）`）；
4. 选歌页倒计时仍在任务期间被禁用（`MusicSelect.TimerCountUp` Prefix），`OnGameStart` 另有 Finalizer 兜底。

这样正常路径完全走游戏自己的「选曲→決定→过场」链路，崩溃概率大幅降低；万一 1 号键不是该版本的決定键，
1.5s 后仍会自动保底进入，不会卡住。

---

## 15. 2026-11-19 凌晨 3：选歌页卡在分类页 → 用真实 1 号键逐屏推进子状态

实机现象：直接写 `CurrentMusicSelect` 后按 1 号键没反应，卡在「分类选择页」
（オトモダチ対戦 / みんなの人気楽曲 / ちほーBonus曲 + NEXT つぎへ）。

原因：选歌页有自己的子状态机 `_currentPlayerSubSequence`：
`Genre(分类) → Music(歌单) → Difficulty(难度) → 开局`。直接写字段只改数据，**不会推进子状态**，
所以界面停在 Genre，按 1 号键也不会进入歌单。

现在 `Navigation.DriveMusicSubSequence()` 跟着子状态走，全部用真实 1 号键推进：

- `Genre`：先写 `CurrentCategorySelect = 目标分类`，按 1 号键 → 进入歌单；
- `Music`：写 `CurrentMusicSelect = 目标下标`，按 1 号键 → 进入难度/开局；
- `Difficulty`：交给 `StartCurrentSelection()` 按 1 号键，由游戏自己的状态机开局；
- 只有 1.5s 没反应才回退直接 `GameStart()` 保底（正常运行不会走到）。

日志：`[nav] 选歌子状态: Genre → 1 号键进入歌单 (...)` / `Music → 1 号键进入难度/开始 (...)`。

---

## 16. 2026-11-19 凌晨 4：选歌卡住/崩溃的真正原因与分类无关定位

实机 01:34 日志：`选歌决定: 模拟真人按 1 号键进入` 之后一直停在 MusicSelect 两分多钟。
原因是 `Navigation.StartRequested` 只表示「按过键/发过请求」，`TickStart` 却把它当成「已经开局」→
flow 提前 `MarkTrackStarted()` 进入 BatchPlay，之后再也没人重试 StartCurrentSelection。

修复：

1. `StartCurrentSelection()` 改为**只有游戏真的到 TrackStart/GamePlay 才返回 true**；
   按 1 号键后返回 false，TickStart 继续每帧重试；
2. 1.2s 没反应 → 回退直接 `GameStart()`（只做一次）；
3. 保底后 6s 仍未进 → 重置，下一帧重来；
4. `TickStart` 不再把 `StartRequested` 当作已开始。

分类排序问题：难度分类/版本分类会改变 `CurrentCategorySelect` 的下标含义。
`SelectChart` 现在额外调用游戏自己的 `SetSortIndexToMaiList(musicId, level, false, true)`
（`isOtherTypeSearch=true`，跨分类按 musicId 定位），和当前分类排序无关；
分类/歌单/难度页仍用真实 1 号键逐屏推进。

### 更正：选歌页的「決定」是 4 号键

用户实测：进歌单/难度/开局都是 **4 号键**（C 键），不是 1 号键。已把
`DriveMusicSubSequence` 和 `StartCurrentSelection` 里注入的键全部改为 `Button04`；
之前按 1 号键没反应也是卡界面的原因之一。

### 定稿：直接 GameStart

用户确认倒计时崩溃封堵（`TimerCountUp` 禁用 + `OnGameStart` Finalizer）有效后，选歌进入改为：

1. **直接调用 `MusicSelectProcess.GameStart()`**（不再依赖分类/歌单/难度子状态机，也不要用 1 号键）；
2. 只有游戏真的到 `TrackStart/GamePlay` 才返回 true（避免提前 MarkTrackStarted 卡 BatchPlay）；
3. 2s 没进 → 补一次真实 **4 号键**（決定）；
4. 6s 还没进 → 重置下一帧重来。

### 完整选歌流程：分类 → 歌曲 → 难度 → 设置/最后确认 → 进入

用户指出之前少了「设置/最后确认」界面。现在 `DriveMusicSubSequence` 对以下每个子状态都按一次
真实 4 号键（決定）推进：`Genre / Music / Difficulty / Menu / Option / Character / SortSetting`，
其中 Genre 前写 `CurrentCategorySelect`、Music 前写 `CurrentMusicSelect`。

`StartCurrentSelection()` 也在每帧驱动同一套界面（因为 TickStart 阶段 SelectChart 不再调用）；
所有界面都过了还进不去，才在 5s 后用 `GameStart` 兜底，12s 仍不进则重置重试。

### 修：进入后 flow 没发现、25s 选曲超时（表现为"自动中止"）

实机 01:54 日志：

```
flow failed: 选曲超时 11788-3 (stage=GamePlay, stack=[CommonProcess > GameProcess > ...])
```

歌曲其实已经开始了（GamePlay），但 flow 还停在 BatchSelect：`DriveMusicSubSequence` 在 Menu/Option
子状态继续按 4 号键并返回 true，SelectChart 一直返回 false，直到 25s 超时 fail→登出（看起来像自动中止）。

修复：

1. `TickSelect` 在选曲前检查 `IsInGameplay()/TrackStart`：已经开局就直接 `MarkTrackStarted()`，
   不再重复选曲；
2. `Navigation.SelectChart` 顶部同样：已有 TrackStartProcess/GameProcess 时直接返回 true。

---

## 17. 2026-11-19 凌晨 5：长版歌曲（長尺曲）额外逻辑

用户确认之前的问题和**長尺曲**有关：長尺曲会占 2 个轨道、改变 credit 的 TRACK 计数，塞进普通 3 轨流程会错位。

处理方式（不再只是跳过）：

1. 规划阶段把長尺曲从普通批次里拆出来（`DataManager.IsLong(music.longMusic)`）；
2. `LongSongDirectWrite=true` 时：**不游玩**，在收尾（登出前）用
   `UserDataPatcher.Commit(0, record)` 直接写 UserData、`PayloadRewriter.AppendMissingRows = true`，
   再 `LoginHooks.ForceDataSave("长版歌曲直写")` 触发一次上传；
3. 等这次 DataSave 完成（最多 25s）再继续登出；
4. 若 `LongSongDirectWrite=false`，按 `SkipLongSongs` 跳过并打日志；
5. 同时修正 `GameManager.IsLongMusic`：1.65 是**静态属性**不是字段，之前写字段失败。

日志：`[flow] 长版歌曲直写 N 首，强制 DataSave 上传…` / `[flow] 长版歌曲上传完成 (DataSave #x)`。

### 长版歌曲正常游玩（换算）

用户要求：长版歌曲不能直写，必须正常跑。实现 `LongSongMode="PlayWithOne"`：

- 规划时把長尺曲单独成 credit：`[长曲, 1 首普通]`（游戏内长曲占 TRACK 1-2，普通在 TRACK 3，共 2 playlogs）；
- 该 credit **不参与课题曲重排**（保持 2 轨结构）；
- 进入下一轨前把 `GameManager.MusicTrackNumber` 换算成 2（`Math.Min(2, _slot+1)`），
  避免 `GameProcess.OnStart` 用 TRACK 3 去取不存在的分数列表（NRE）；
- `LongSongMode` 可选 `PlayWithOne`（默认）/ `Skip` / `DirectWrite`。

### UI：「长版歌曲」下拉

设置页新增下拉（正常跑 / 跳过 / 直写），对应 `LongSongMode`：`PlayWithOne` / `Skip` / `DirectWrite`，
选择立即写入 config.json。`Skip` 就是用户要的「跳过长区（长曲）」选项。

---

## 18. 2026-11-19 深夜：官方服「数据保存失败」= UpsertUserAll returnCode=1

SDGB 网络日志（maimai-gm.wahlap.com）里 `UpsertUserAllApiMaimaiChn` 会返回 `returnCode:1`（保存失败）。
规律（对比 22:12/22:21、22:50/22:59 两组）：

- 每次登录后的**第一个 credit 存档**：`userData.playCount` 仍是旧值（28/29），返回 1；
- 第二个 credit 存档：`playCount` 已 +1（29/30），返回 0。

即：官方服要求 playlog 与 playCount 同步递增；游戏在第一个 credit 的 DataSave 时还没把 playCount 加 1。

修复：`PayloadRewriter` 在「登录后第一次带 playlog 的 Upsert」里把 `userData.playCount`（和 currentPlayCount）
补 1，并打日志 `[net] 登录后首存 playCount 补 1`。版本差异（SDEZ 1.65 / SDGB 1.55 的 VO 字段不同）用反射读取。

### 更正：每次存档都要递增（只补第一次会导致第二次失败）

实机 23:2x：只补登录后第一次存档 → 第一个 credit 存档成功、**第二个 credit 存档失败**（playCount 没再涨）。
改为单调递增：每次带 playlog 的 Upsert 都保证 `playCount > 上次发送值`
（`target = max(gameValue + 1, lastSent + 1)`），日志 `[net] 存档 playCount 递增修正: 30 → 31`。

### 关键：VO 字段不是属性（前两次补丁其实没执行）

`Net.VO.Mai2.UserDetail.playCount` / `currentPlayCount` 是 **public 字段**，不是属性；
之前用 `GetProperty` 拿不到 → `pcProp == null` → 静默跳过（无日志）。

现在改为 `GetField("playCount"/"currentPlayCount", Public|NonPublic|Instance)`，并增加诊断：
`[net] UpsertUserAll 里没有 playlog 字段（playlogCount=0…）`。

### 最终定位：官方服拒绝的是「重复声明 new」而不是 playCount

补丁确认执行（日志 `存档 playCount 递增修正: 33 → 34`），但 UpsertUserAll 仍返回 1 →
playCount 不是根因。对比网络日志与登录后的 GetUserMusic 响应：

- 23:56 失败的 3 行（11619/11222/852） уже 在 23:45 成功保存过；
- 00:18 失败的 3 行（11809/689/11646）在 00:04 的包里已保存过；
- 这些行仍被游戏标记 `isNewMusicDetailList="111"` → 官方服把「已存在的谱面声明为 new」判为
  `returnCode=1`（数据保存失败）。

根因：重登后 flow 重新规划时**重播了上一批**（target 读到的歌未被识别为 done）。

修复：
1. `_uploadedThisRun`（musicId-level）记录本 run 已上传谱面，重登重新规划时剔除
   （日志 `flow: 跳过本 run 已上传的 N 条`）；
2. PayloadRewriter 修正 `isNewMusicDetailList`：`playCount>1` 的行标 `0`，只有真正新谱面标 `1`
   （日志 `[net] isNewMusicDetailList 修正: "111" → "000"`）。

### 真正根因（用「手动游玩」对照确认）

官方服 `returnCode`：**0=失败，1=成功**（之前理解反了）。
手动游玩成功存档：save1 3 行 isNew="111"；save2 3 行 isNew="001"（打过的 0、新曲 1），
且不会重复带上一个 credit 的行。

插件 flow 失败存档：save2 带 6 行（上一批 3 行原样重发）且 isNew="111111" → rc=0。

原因是 `UserDataPatcher.Apply` 直接写 UserData，行保持 dirty/new；游戏原生路径会在成功后清空。

修复（PayloadRewriter）：
1. 发送前剔除「与上次发送内容完全相同」的旧行（模拟清 dirty）；
2. isNew 用「登录时账号已有 + 本次已发送」集合：已有 0、新曲 1；
   日志 `[net] 去掉 N 行…` / `[net] isNewMusicDetailList 修正: "111111" → "000111"`。

### 生成成绩更像真人（按用户 4 张对照图）

手动游玩 vs 生成成绩的差异：
- 手动：GREAT/GOOD 分布在 TAP/HOLD/SLIDE/BREAK（Crush On You TAP GREAT55/GOOD13；三日月 TAP GREAT31）。
- 生成：TAP 的 GREAT/GOOD 全 0，错误全挤在 SLIDE（神威 SLIDE GREAT19/GOOD55）→ 明显不自然。

调整：新增配置 `RealisticJudgeSpread`（默认 true），为 true 时不使用 data-dev 的 per-type 计数，
改为按音符数量随机散布 GREAT/GOOD/MISS，再按类型约束修正。

FAST/LATE：用户确认「保持约等于即可」——手动那把 277/20 是延迟设置不对造成的，不需要模拟；
已撤销之前加的 FAST/LATE 偏置补丁。

### 02:00 失败复盘：登录后读数据太早（残缺快照）+ 续关未等初始化

02:00:39 这一存：报文已经是 3 行（去掉重复行生效）、isNew="111"，但 `returnCode=0`。
查包内容：rows 里有 11860（playCount=11、achievement 986941），而账号该谱面本来就有 99.8877%
——计划把「已有更高成绩的曲」当成 missing 重打了，而且把成绩改低。

原因：目标账号登录后 **4 秒**就读了 UserData（01:43:36 登录，01:43:40 读取），
游戏的分页下载还没完成 → 快照残缺 → 重打已有曲/降分 → 官方服拒绝。

修复：
1. 读账号快照要求「连续两次条数相同」且登录满 **5 秒**才接受；
2. 续关按「是」后等 **3 秒** credit 初始化（track 计数/脏数据清理）再进下一批；
3. `WriteRow` 增加「不降分」保护：目标现有 achievement 更高时保留原值。

### 2026-11-20 02:03 崩溃：选歌页初始化未完成就按键

CrashHandler：
```
NullReferenceException
  at Monitor.MusicSelectMonitor.getTabString (MusicSelectProcess+GenreSelectData data)
  at Monitor.MusicSelectMonitor.OnStartMusicSelect ()
  at Process.SubSequence.GenreSelectSequence.Update ()
```
进选歌页后分类/标签数据还没初始化完，flow 就按 4 号键进歌单 → getTabString NRE 直接崩。

修复：
1. `DriveMusicSubSequence` 在进选歌页后 **2 秒内不按键**（`NavReflect.WarnOnce("ms-init-wait", ...)`）；
2. `MusicSelectMonitor.OnStartMusicSelect` 增加 Harmony Finalizer 吞 NRE（兜底，界面后补），
   安装日志 `CrashFixes: MusicSelectMonitor.OnStartMusicSelect NRE 已兜底`。

### 2026-11-20 07:20 新发现：同一 session 第二次存档 rc=0

- 02:15 credit 1 存档（同一 session 第一存）→ rc=1 成功；
- 02:25 credit 2 存档（同一 session 第二存，去掉重复行后 3 行、isNew 111）→ **rc=0**；
- 手动那两把其实是**两次不同 session**（root playlogId 不同），所以没验证过「同 session 第二存」。

`lastPlayCredit` 与 `userGamePlaylogList[0].playCredit` 在我们第二次存档时仍是 **1**（应为 2）。
修复：PayloadRewriter 按 session 内第几次 UpsertUserAll 强制设置 `lastPlayCredit` 与 `playCredit`，
日志 `[net] lastPlayCredit 修正: 1 → 2`。

### 关键：VO 的 UserDetail / UserGamePlaylog 是 struct

17:34 第二存仍然 rc=0。查游戏自己的 wire 日志（MelonLoader Latest.log）发现
`lastPlayCredit` 仍是 1 —— 尽管我们的补丁日志说 1→2。

原因：`Net.VO.Mai2.UserDetail` / `Net.VO.Mai2.UserGamePlaylog` 都是 **struct**；
`Array.GetValue` 得到的是装箱副本，改它不会回写数组（playCount 补丁以前也是这样失效的）。

修复：修改后 `udArr.SetValue(ud0, 0)` / `gpl.SetValue(g, i)` 写回。
现在 wire 日志应显示 `lastPlayCredit=2`、`playCredit=2`。

### 对照 Astral（官方服可用工具）后的结论

Astral reincarnate/README 的实测规则：
1. `UpsertUserAll.playlogId` 必须等于**本次登录**的 loginId；一局结束会话即被消费，要传下一局必须
   `UserLogin(isContinue=true)` 拿**新的 loginId/loginDateTime**。真机在
   `Process.ContinueProcess.ReLogin` 里发 `new PacketUserLogin(userID, accessCode, isContinue:true, ...)`。
2. 每包只能是 **1 或 3 首**（2 首包被服务端禁止）——長曲 2 轨 credit 会被拒。
3. 上传有下限：首曲 userPlayDate ≥ 登录+15s、每曲 +15s，登录后**至少 50s** 才允许上报，否则整包被丢。
4. 登出必须 `type=1(Logout)`。

我们之前的续关只按了 Continue 屏的 YES，**没有完成 isContinue 登录**，所以第二存 playlogId 还是旧 loginId → rc=0。

修复：
- `PressContinueYes` 在按完 YES 后 1.2s 若 loginId 未变化，主动调 `ContinueProcess.ReLogin()`；
- flow 进入下一批前等待 loginId 变化（最多 10s），日志 `[flow] 续关登录完成: 新 loginId=...`。
- 待办：长曲在官方服应设 LongSongMode=Skip（2 轨包会被拒）；快速模式下要保证登录后 ≥50s 才上传。

### 22:52 失败复盘：isContinue 登录从未发生

网络日志：整个 22:05–22:53 只有 **一次** UserLoginApi（22:35:54，会话登录），之后续关**没有**任何登录请求。
22:52 第二存虽然 wire 已修好（playlogId 换成新值、lastPlayCredit=2、playCredit=2、rows=3、isNew=111），
但仍然 rc=0 —— 因为服务器不认「伪造的 playlogId」，它要求 playlogId == **服务器本次会话签发的 loginId**。

我们的 `ContinueProcess.ReLogin()` 主动调用没触发：条件写成了 `_continueStep >= 3`，但这一局的续关是游戏
自动推进的（step 一直是 0），所以永远不会调用。

修复：
1. ReLogin 触发条件改为与 step 无关：只要 `重登基线 != 当前 loginId` 且等待 1.2s 就调；
2. 删除伪造 playlogId 的补丁（改用真实 isContinue 登录拿到的新 loginId）。

### 23:04 复盘：ReLogin 一次都没被调用

23:04 续关：按了 SelectContinue/PlayContinue/DetailInputOnDisp（step 1→3），
但 23:04:16 只出现「续关登录未完成」，**没有任何 ReLogin 日志**，网络日志也没有 UserLoginApi。
说明触发条件（base/loginId 比较）实际为 false。

修复：ReLogin 改为「第一次看到 ContinueProcess 后 1.2s 无条件调一次」，并打全量诊断：
`续关开始: baseLoginId=...` / `调 ContinueProcess.ReLogin (base=..., last=..., step=...)` / `ReLogin err=...`。
flow 等待 loginId 变化最多 10s（base==0 也等待）。

### 2026-11-21 01:2x 架构调整：完全走原版流程，只改上传数据

用户要求：续关/登录等全部交给游戏原版，插件只改发出的报文。

1. `Navigation.PressContinueYes` 不再合成调用（SelectContinue/PlayContinue/DetailInputOnDisp/
   `_isContinueReady`/ReLogin 全部移除），续关只由 `RebirthFlow` 里「按住真实 4 号键 ~2.2s」驱动，
   让 ContinueProcess 自己完成 ReLogin 与初始化；
2. SDGB 配置 `WriteMode: "Payload"`：不写游戏内存 UserData，只改 `UpsertUserAll` 报文
   （保留去重 / isNew / lastPlayCredit 等**只改报文**的修正）。

### 2026-11-21 01:4x 关键 bug：多段代码被合并进 // 注释行

之前用 PowerShell 拼接补丁时，部分多行插入被压成一行，代码落在 `// ...` 之后 → **整段被注释掉**，根本没有执行：

- `ContinueProcess.ReLogin()` 立即调用（一直没触发）；
- 续关屏触摸 YES 区域（只按 4 号键国服卡在 state=2）；
- TickSelect 的「已开局直接 MarkTrackStarted」；
- 选歌页 2 秒初始化等待（getTabString NRE 防崩）；
- SetSortIndexToMaiList 选歌定位。

已全部还原为真正的多行代码。这解释了为什么「ReLogin 日志一次都没有」。
