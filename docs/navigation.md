# 导航层（Flow/Navigation · ScoreHooks · MapHooks）实现说明

> 维护者：nav-dev。适用版本：SDEZ 1.65（`ConstParameter.GameIDStr="SDEZ"`、`NowGameVersion=26500`）。
> 换游戏版本时先看 §8「换版本检查清单」。

---

## 1. 定位与设计原则

| 原则 | 说明 |
|---|---|
| **不模拟按键** | 全部靠直接写 `GameManager` 静态状态 + 操作 `ProcessManager` 进程栈完成。`InputLock` 把物理输入全吞掉也不影响导航。 |
| **幂等** | `GoToMusicSelect / SelectChart / StartCurrentSelection / SkipCurrentChart / ContinueToNextTrack / DeclineContinue` 都可以被 `RebirthFlow.Tick()` 每帧无脑调用；`false` = 还没到/没做成，下帧再来。 |
| **不改成绩** | 只有 `SkipCurrentChart` 会消费 `ScoreHooks` 的期望成绩；`filler`/中止轨走游戏原生 TrackSkip，绝不写分。 |
| **不猜签名** | 私有成员、版本间改名成员一律经 `NavReflect`（带缓存 + 失败只报一次），`Install` 时打 API probe。 |
| **不依赖他人** | 进程栈自己看（`ProcessManager._processList`），`GameStageTracker` 只作附加信息；登录/登出通过委托桥接 flow-dev 的 `LoginHooks`。 |

文件分工：

```
Flow/Navigation.cs   导航主干：进选歌页 / 选曲 / 进入 / 跳过 / Continue / 登录登出桥 / API probe
Flow/ScoreHooks.cs   结算钩子：期望成绩注入、filler 不改分、回读校验、ForceAchivement 单位可切换
Flow/MapHooks.cs     区域选择的 3s 超时、人工换区检测、课题曲扫描/取消
Flow/NavReflect.cs   反射工具：字段/属性/方法/构造函数查找（含基类链）、枚举写入、限频告警
Flow/NavProcess.cs   进程栈快照、ProcessDataContainer 获取、Add/Release/Fade 进程
Flow/TimeEstimator.cs 用 Navigation 的时间常量做 ETA（签名不变）
```

---

## 2. 整体流程与各阶段的判定特征

```
Title
 └─(刷卡) Entry 确认页  ConfirmPlay/ConfirmGuest/ConfirmNewUser/ConfirmContinue…
        │  EntryProcess.GetCurrentState() + EntryMonitor.DecideEntry()（自动按「是」）
        ▼
ModeSelect / LoginBonus / CharacterSelect(Proces) / TicketSelect
        │  FadeProcess(from → new MusicSelectProcess(container))
        ▼
Information（活动/新曲提示）
        │  私有 _state 置 3 = GotoEnd（AquaMai SkipEventInfo 同款）
        ▼
RegionalSelect（区域选择）
        │  MapHooks：人工选择窗口 AreaSelectTimeoutSec（默认 3s）→ 超时调 TimeUp() 走默认项
        ▼
MusicSelect（选歌页）
        │  SelectChart(musicId, level) → StartCurrentSelection()（GameStart）
        ▼
TrackStart → GamePlay
        │  SkipCurrentChart()（等满 15s 轨间隔；有期望成绩则 ForceAchivement 后结算）
        ▼
Result →（track<3 回选歌页 / track=3 进 Continue 或 DataSave）
```

### 2.1 判定用到的"硬证据"

| 阶段 | 判定方式（代码里用的） |
|---|---|
| Title | 进程栈里出现 `TitleProcess` / `StartupProcess` / `PowerOnProcess`，或 `GameStageTracker.Current==Title` |
| Entry | `NavProcess.Any<EntryProcess>()`（或 tracker=Entry）。具体子状态用 `EntryProcess.GetCurrentState(out StateType, out int mode)`：`ConfirmEntry=1, ConfirmGuest=2, ConfirmNewUser=3, ConfirmPlay=4, ConfirmFreedom=5/6, ConfirmAccessCode=7, ConfirmContinue=8/9, DoneEntry=10` |
| Information | `Process.Information.InformationProcess` 存在；私有 `_state` 枚举 `Wait=0, Disp=1, Released=2, GotoEnd=3` |
| 区域选择 | `Process.RegionalSelectProcess` 存在；光标 `SelectCursolIndex(0)`；子状态 `_subStates[0] >= 10`(ToDecide) 表示已确认 |
| 选歌页 | `Process.MusicSelectProcess` 存在（`GameStageTracker.MusicSelect` 只作辅助） |
| 进入游戏 | `TrackStartProcess` 或 `GameProcess` 存在 |
| 游玩中 | `GameProcess._sequence`（私有枚举）：`Init=0, Sync=1, Start=2, StartWait=3, Play=4, PlayEnd=5, Result=6, ResultEnd=7, FinalWait=8, Release=9`；只有 `4 ≤ seq < 9` 才允许跳曲 |
| Continue | `Process.ContinueProcess` 存在 |
| 选歌页里"曲在不在" | `MusicSelectProcess.CombineMusicDataList`（`List<ReadOnlyCollection<CombineMusicSelectData>>`，外层=分类）→ 每条的 `GetID(ScoreKind)` + `musicSelectData[sk].Difficulty` |

---

## 3. 关键实现

### 3.1 进选歌页 `Navigation.GoToMusicSelect()`

按优先级处理当前进程栈（每次只做一件事，返回 false 让 flow 下帧继续）：

1. **已在选歌页** → `true`。
2. **Information** → 反射把私有 `_state` 写成 `Enum.ToObject(字段类型, 3)`（GotoEnd）。
3. **RegionalSelect** → 交给 `MapHooks.TickAreaSelect()`（3s 超时后 `TimeUp()`），这里返回 false。
4. **ModeSelect / LoginBonus / CharacterSelectProces / CharacterSelectProcess / TicketSelectProcess** →
   `GameManager.IsNormalMode=true; GameManager.SetMaxTrack();`
   `AddProcess(new FadeProcess(container, from, new MusicSelectProcess(container)), 50)`。
   这是 AquaMai `OneKeyEntryEnd` 的成熟做法；`CharacterSelectProces` 是 Assembly-CSharp 里的原始拼写。
5. **Entry** → `TryConfirmEntry()`：按状态对每个 `EntryMonitor` 调 `DecideEntry()`（失败再试 `ResponseYes()`），
   同时用 `InputManager.SetButtonDown(monitor, Button04)` 合成一次真实按键（「ボタンをおしてください」屏最直接），
   0.6s 限频、60 次上限（超限停手交人工，打 `[nav] Entry 确认页按了 60 次仍未前进`）。

   **安全白名单（flow-dev 要求，改这里前先看）**：

   | StateType | 动作 | 理由 |
   |---|---|---|
   | `ConfirmEntry(1)` + mode==**Request(1)** | 按「是」 | 只有 Request 是"等玩家确认用这张卡开始"，安全 |
   | `ConfirmEntry(1)` + mode==**Reading(2)** | **绝对不碰** | 实机事故：按键会打断读卡 → 用户被踢进 ConfirmGuest 游客流程 |
   | `ConfirmEntry(1)` + mode==Suspend(3)/0/≥4 | **不碰** | 挂起中 / Setup / NewAime / NewFelica / 迁移 / 二维码 / Error |
   | `ConfirmPlay(4)` + mode∈{Select(10)} | 边沿触发注入「是」 | 见 ConfirmPlay 专节（WaitResponse 17 仅久卡兜底） |
   | `ConfirmPlay(4)` / `ConfirmContinue(8,9)` | 按「是」 | 开始游戏 / 继续上次会话 |
   | `ConfirmGuest(2)` / `ConfirmFreedom(5,6)` | 按「否」 | 不能自动进游客/自由模式 |
   | `ConfirmNewUser(3)` | 需要 `Navigation.AllowConfirmNewUser=true`（默认 false）：每个 ProcMode 按一次「是」建号，每实例上限 6 次；配置键 `AllowConfirmNewUser` | 未注册新 aime 的建号确认；默认等人工，用户明确要求建号时才开 |
   | `ConfirmAccessCode(7)` / 其它 | **什么都不做** | 手动输码等；自动按「是」可能给账号建新数据 |

   > **官方 YesNo 路径**：`PerformEntryAnswer` 会优先用 `StateEntry` 的
   > `StartYesNoResponse()` → `SetActiveYesNoButton(true, isNewAime)` → `SetYesNoButton(monitorIndex, isNewAime)`，
   > 再 `EntryMonitor.DecideEntry()/ResponseYes()` → 直写 `InputResponse=Yes` → 合成按键。
   > `ConfirmNewUser` / `ConfirmPlay` 都继承自 `StateEntry`，都有 `_responseMonitor`，走的是同一套 YES/NO 机制。

   > `ConfirmEntry.ProcMode` 完整枚举：`Setup=0, Request=1, Reading=2, Suspend=3, ResultNewAime=4,
   > ResultNewFelica=5, NewFelicaPolling=6, NewFelicaWaitNotice=7, NewFelicaWaitConfirmSite=8,
   > NewFelicaNoticeDataMigration=9, NewFelicaWaitDispQR=10, NewFelicaDone=11,
   > NewAimeDispFelicaSiteAndQR=12, Error=13`（取自 `Libs/Assembly-CSharp.dll` 元数据）。

   **任务闸门（实机事故修复）**：Entry 自动推进只在**插件任务运行中**启用 ——
   `Navigation.IsEntryAutomationAllowed()` = `ForceEntryAdvanceWhenIdle` || `!RequireTaskActiveForEntry` ||
   `MachineLock.Active` || `RebirthFlow.Phase ∈ {SourceLoginPending, SourceReading, TargetLoginPending,
   TargetReading, Transferring, Stopping}`。用户自己玩（Idle/Finished/Failed/Planning/AwaitingConfirm）时
   **绝不碰 Entry 输入**，只打一次 `[nav] Entry 自动推进已跳过：插件任务未运行 …`。
   该闸门同时作用于 `AutoDriveEntry`（EntryProcess.OnUpdate 自动驱动）、`TryAdvanceEntry`、`ConfirmPlay.Exec` 注入。

   `DoneEntry(10)` 卡住 3s 会用 `SetNextProcess()` 催一次。

   **登录成功后（`IsRealPlayerLoggedIn`）不再"全静默"，只停确认键、继续推进受付屏**（实机 21:55 教训）：
   登录成功后画面是「1P/2P + FREE PLAY + タイムスキップで受付終了」，进程栈仍是 `EntryProcess`，stage=Entry。
   **登录后的受付屏（EntryProcess）→ MODE SELECT（安全边界，lead 23:56 + flow-dev 结论 + 用户实测）**：
   用户实机链路是「登录 → 受付屏连点跳过 → MODE SELECT 屏停手 → flow 读数据 → 在 MODE SELECT 什么都不按、
   等约 30s 游戏自己超时登出 → SourceReady」。因此：
   - **连点 timeskip 只对 `EntryProcess` 承载的受付屏启用**（`HandlePostLoginEntry`）：
     ~50Hz 连按 `DecrementTimerSecond(30)`（=按 timeskip 键），最多 `ModeSelectRapidPressFrames` 帧；
     **不做** `IsTimeCounting(false)` / `IsFastSkip(true)` / `SetNextProcess()`（实机证据：SetNextProcess 会跳过用户数据下载）；
     进入条件是"已登录 且 当前不是新规建号确认(3)"，**不要求 `state==DoneEntry`**
     （实机 01:14：登录成功后 EntryProcess 立刻停在 `ConfirmEntry(state=1, mode=2 Reading)`，旧逻辑因此没进来）；
     若 120 帧后仍没离开受付 → **直调 `EntryProcess.CreateDownloadProcess()`**（只读阶段一次）触发数据下载，
     日志 `[nav] 受付连点未离开 → 直调 EntryProcess.CreateDownloadProcess() 触发数据下载 ok=…`；
   - **一旦出现 `ModeSelectProcess`（MODE SELECT 屏）→ 立刻停止一切注入**：
     日志 `[nav] 已到 MODE SELECT，停止一切注入（等待 flow 读取/被动登出）`；
   - **MODE SELECT 屏上绝不注入**：不 timeskip、不 `OKStart`、不 `_state=Released`、不 Fade 强切
     （那一屏按 OK = 开始一局游玩，只读模式会产生脏 playlog）；
   - **登出阶段的正确语义 = 倒计时归零 → 游戏自己超时 → 回 Title → 游戏自己发 UserLogout**：
     `ForceCountdownToZero()` 只在 `_logoutRequested` 时生效（0.2s 节流），做法是
     反射 `Process.GenericProcess._timerController`（`TimerController[]`）→ 每个 controller：
     关 `IsInfinity`（`DisableTimeout` 会把无限/冻结算打开）→ `IsTimeCounting=true` →
     **`SetTimeZero()`**（失败则直写 `_countDownSecond=0`）→ 补 `DecrementTime(9999)`；
     同时打印 `before/after/isInfinity/isTimeUp/ok`，并在 stage 变成 Title/Entry 时打
     `[nav] 登出: 倒计时生效 → stage=Title，游戏将自行 UserLogout`。
     300 次（60s）仍不回 Title 就停手，交回 flow 的 `PacketUserLogout` 兜底。
   - 登出阶段在 MODE SELECT **不按键**（OK=开局），只做倒计时归零；受付屏在 EntryProcess 时先连点 timeskip。
   - **只有写入阶段（`Phase == Transferring`）**才允许在 MODE SELECT 开始本 credit 的游玩；
     做法和 timeskip 同构（实机 00:59 教训：只调 `isOKTimerZero()+OKStart()` 都返回 True，但
     `_isOK/_isDecidedOK` 永远 False → 状态机吃的是**按钮动画累积**）：

     | 阶段 | 动作 | 日志 |
     |---|---|---|
     | P0 | **0.05s** settle + 写 `_isEnableOK=true`；`PleaseWaitProcess` 最多等 3s | `[nav] MODE SELECT 开始按 OK(NORMAL): state=…, …` |
     | **P1 快速通道** | 直接写 `_decidedSelectModeType=0`(NORMAL) + `_isDecidedOK=true` + `_isOK=true` + `_isCheckOK=true` + `isOKTimerZero()` + `OKStart()`（5s 窗口最关键） | `[nav] MODE SELECT 快速通道: 写 NORMAL(modeType=…), …` |
     | P2 | 每帧 `OKButtonAnim(Button05)` 连点 **最多 60 帧**（≈0.3~0.5s），字段一变立刻推进；无效换 Button04 再 60 帧 | `[nav] MODE SELECT 连点 OK: n/60, button=…` |
     | P3 | 0.3s 间隔：`isOKTimerZero()+OKStart()` + 触摸 `{E5,E4,E6,D5,D4}` | `[nav] MODE SELECT 直调: …` |
     | P4 | 0.3s 间隔：再写决策字段 + `OKStart()` + 触摸 | `[nav] MODE SELECT 直写+OKStart: …` |
     | P5 | 0.5s 后仍不动 → 强切 Fade 到 `MusicSelectProcess` | `[nav] MODE SELECT 快速梯次均无效 → 强切 MusicSelect ok=…` |

     > 续关后 MODE SELECT 窗口只有 **~5s**（`InGameStart=false`），所以整体梯次压缩到 **≈1.5~2s** 走完；
     > 日志在连点阶段每 10 帧（≈0.1s）一条，其余每步一条。

   **ModeSelect 结束受付（实机 23:39 用户确认：要"连续快速按"，不是长按 3 帧）**：
   真实按键路径是——`UpdateInput` 里检测 `GetButtonDown(Button05)` → `TimeSkipButtonAnim(Button05)` + `TimeUp()` → **`return false` 跳过原 `UpdateInput`**。
   改为"**连续快速按 + 直接触发函数 + 直接写终态**"的梯次（`HandleModeSelect`，由 `UpdateInput` Prefix 每帧驱动；每步都打返回值 + `_state`）：

   | 阶段 | 动作 | 日志 |
   |---|---|---|
   | P0 | 先给游戏自己走 **2s**（登出场景只等 0.3s） | — |
   | P1 | **每帧调 `TimeSkipButtonAnim(Button05)`，持续 `ModeSelectRapidPressFrames` 帧（默认 120 ≈ 1~2s 连续快速按）**，每 10 帧一条进度 | `[nav] ModeSelect 连点 timeskip: 1/120, anim=True, state=Disp(1), _isTimeSkip=…` |
   | P2 | +1s 没动 → 直调 `TimeSkipStart()`（按钮真正的处理函数） | `[nav] ModeSelect 直调: TimeSkipStart=True, state=Disp(1), …` |
   | P3 | +1s 没动 → **直接写 `_state = Released(2)`** | `[nav] ModeSelect 直接写 _state=Released: ok=True, was=Disp(1)` |
   | P4 | +1s 没动 → OK 路径 `isOKTimerZero()` / `OKStart()` | `[nav] ModeSelect 直调(OK 路径): …` |
   | P5 | +1s 没动 → 强切 `FadeProcess(container, modeSelect, new MusicSelectProcess(container))`，失败再 `AddProcess+ReleaseProcess` | `[nav] ModeSelect 强切 MusicSelect (…) ok=…` |
   | P6 | 强切后 **5s** 仍未到选歌页 → 打进程栈并放弃 | `[nav] ModeSelect 强切后 5s 仍未到选歌页 → 停止重试; stack=[…]` |

   **登出也必须能结束受付屏**（只读模式同样关键）：
   - `Navigation.RequestLogoutByTimeout()`：先走 flow 的 `LogoutHandler`/`ForceTimeUp()`，**然后无论成败都调 `EndReceptionBurst()`**；
     ModeSelect 每次 burst **连点 20 次**（第 6 组起补 `TimeSkipStart`，第 12 组起补 `OKStart`），
     EntryProcess 受付屏则 `IsTimeCounting(false)` + **连按 `DecrementTimerSecond(30)` 10 次**（第 8 组起补 `SetNextProcess`）；
   - 另外 patch `ProcessManager.ForceTimeUp`：flow 的登出循环一调它，就自动打开 `_logoutRequested`，
     让 ModeSelect/Entry 的每帧驱动开始连点 —— 即使 flow 不调用 `Navigation.RequestLogoutByTimeout` 也能生效；
   - 用户自己玩的超时登出不介入（`IsTaskActive()==false` 时直接忽略）。

   > 1.65 的 `ModeSelectProcess` 成员（元数据实测）：`_state`(Wait0/Disp1/Released2)、`_isTimeSkip`、`_isTimeUp`、
   > `_isEnableTimeSkip`、`TimeSkipButtonAnim(ButtonSetting)`、`TimeSkipStart()`、`ResetTimeSkipStart()`、
   > `isOKTimerZero()`、`OKStart()`、`ResetOKStart()`、私有 `TimeUp()`（**这个 1.65 里仍然存在**，只是不是按钮的入口）。
   > **总时限**：登录成功起 **25s**（`Navigation.PostLoginTimeoutSec`）未到选歌页 → 停止一切注入交回 flow。

   **PlInformation（プレイ情報/隐私提示）**：patch `Monitor.PlInformationMonitor.IsPlayPlInfoEnd`（任务运行时返回 true）；
   同时 `GoToMusicSelect` 里把 `PlInformationProcess._state` 置 `DispEnd(4)`（`Init0/Wait1/Disp2/DownloadWait3/DispEnd4/DispErrorWait5/Release6`），
   卡住 6s 再 Fade 到选歌页兜底。

   **自动驱动**（实机教训：flow 的 TickLogin 只轮询登录状态，没人调导航会一直卡在确认页）：
   `Navigation.Install()` patch 了 `EntryProcess.OnUpdate`（Postfix）→ `AutoDriveEntry()`，每 0.5s 最多一次；
   `Navigation.TryAdvanceEntry()` 是 public、幂等、可每帧调的入口（内部 0.25s 限频），flow 也可以直接调。
   `GenericProcess` 这类覆盖进程压在 EntryProcess 上面**不影响**（EntryProcess.OnUpdate 仍然每帧执行）。

   **ConfirmPlay(4) 的特殊处理（实机踩坑：mode 一直停在 9/10）**：
   `ConfirmPlay.ProcMode`：`SelectIn(8)/SelectInWait(9)` 是列表滑入过场，**`Select(10)` 才是屏幕上等玩家点 YES/NO**，
   `WaitResponse(17)` 是玩家按完之后等服务器的状态。在过场阶段注入会重置动画，所以：
   - 拿到当前状态机实例（`Context._current` 私有字段；回退 `Context.GetCurrentState(out StateType, out State)`）后，
     **只在 `Select(10)` 边沿触发注入**（每个 mode 刚进入时一次 + 每 6 tick 重试，最多 4 次；`WaitResponse(17)` 只在久卡时兜底 1 次）；
   - patch **`ConfirmPlay.Exec` 的 Prefix**，在状态机读取响应之前注入（同帧）；
   - 优先用 `StateEntry` 官方 YES/NO API：`StartYesNoResponse()` → `SetActiveYesNoButton(bool,bool)` →
     `SetYesNoButton(int,bool)` → `EntryMonitor.ResponseYes()`；再辅以 `InputResponse` 与合成按键；
   - 卡住救援：`10 tick` 清零 `_buttonAnimWait/_agreeStartWait/_warningInfoWait/_timer` → `20 tick` 强推 `_mode 9→10` → `30 tick` 停手；
   - `_isConfirmNewUser == true`（服务端把卡当新卡）时**一律不动作**，只告警；
   - 响应目标优先用状态机 `_responseMonitor` 字段，而不是固定 monitor 0。

   > ⚠️ 反射 `Context.GetCurrentState` 时第一个 out 参数是**枚举 `StateType`**（不是 int）：
   > 必须给 byref 值类型参数传对应类型的装箱默认值（`Activator.CreateInstance(elementType)`），
   > 传 `0` 会抛 `Int32 cannot be converted to StateType&`（这就是实机 state=null 的根因）。
   > 现在首选读 `Context._current`，不依赖 out 参数反射。

   **防刷屏 / 防误按（lead Bug C 修正）**：
   - 每个 `EntryProcess` 实例最多尝试 `Navigation.MaxEntryAttempts`（默认 30）次，到顶打一条 Warn 后停手（换新 EntryProcess 自动归零）；
   - **登录成功后立刻停手**：检测到 `UserDataManager` 里有真实（非游客、UserID≠0、IsEntry）账号就不再按；
   - `DoneEntry(10)` 只催 `SetNextProcess()` 最多 3 次；
   - 尝试日志节流：前 5 次 + 之后每 5 次一条，不再 0.5s 刷屏。

   **推进动作（三条路径全试，日志逐项报告）**：
   `EntryMonitor.DecideEntry()`（decide）、`EntryMonitor.ResponseYes()/ResponseNo()`（response）、
   直接写 `InputResponse` backing field（inputResponse）、`InputManager.SetButtonDown(0, Button04/05)`（synthetic）。
   日志格式：`[nav] Entry 确认页 -> 按「是」(decide=True, btn=Button04, synthetic=ok, responseYes=False, inputResponse=True, state=1, mode=3/(Suspend))`。
   首次进入 ConfirmEntry 时还会一次性打印 `[nav] Entry 候选方法: EntryMonitor=[...] ConfirmEntry=[...] StateEntry=[...] ContextEntry=[...]`，
   给"第三条路径"（反射调状态机的确认方法）备料。

### 3.2 选曲 `Navigation.SelectChart(musicId, level)`

```
遍历 CombineMusicDataList 全部分类 × ScoreKind(Standard=0/Deluxe=1)
  优先精确匹配 (musicId, difficulty)
  普通排序下条目 Difficulty 恒为 0 → 退化为只匹配 musicId，难度单独写
命中后写：
  MusicSelectProcess.CurrentCategorySelect / CurrentMusicSelect / ScoreType
  MusicSelectProcess.ClampMusicIndex()
  MusicSelectProcess.DifficultySelectIndex[player] / CurrentDifficulty[player]
  GameManager.SelectMusicID[player] / SelectDifficultyID[player] / SelectGhostID[player]=End
  GameManager.SelectedDeleteGhostID=End / SelectScoreType = (musicId>=10000 ? 1 : 0)
```

> **为什么也写 GameManager**：游戏自己的 `MusicSelectProcess.GameStart` 前缀读的是 `GameManager.SelectMusicID/SelectDifficultyID`
> （AquaMai `FixMissingDifficultyCrash` 的做法证明了这一点），只写进程内的 `CurrentMusicSelect` 不够。
> `player` 取 `MusicSelectProcess.SortDecidePlayer`（钳到 0..1）。

### 3.3 进入 `Navigation.StartCurrentSelection()`

1. `PrepareGameManagerBeforeStart()`：
   - 补 `GameManager.SelectMusicID/SelectDifficultyID/SelectGhostID/SelectScoreType`
   - `GameManager.IsNormalMode=true`、`SetMaxTrack()`
   - `GameManager.IsLongMusic = DataManager.IsLong(musicData.longMusic)`（1.65 字段，**1.55 没有 → 反射写法**）
   - `MusicSelectProcess.PrepareFinish=true`、`IsPreparationCompletes[player]=true`
2. **反射调用私有 `MusicSelectProcess.GameStart()`**（走游戏完整的记账 + Fade 到 TrackStart）。
3. 失败才兜底：`Fade(from=MusicSelectProcess → new TrackStartProcess(container))`。
4. 发出后 **8 秒内一律返回 true，绝不重复调 GameStart**（重复会往进程栈塞第二个 TrackStartProcess）；
   8s 后仍停在选歌页则 warn 一次并返回 false。

### 3.4 跳曲 `Navigation.SkipCurrentChart()`

前置条件：`GameProcess._sequence ∈ [4, 9)`，且距本轨开始已满 `MinTrackGapSec=15s`
（服务端要求同一 credit 内相邻 playlog 间隔 ≥15s，见 `docs/upload-protocol.md`）；未满则返回 false 由 flow 每帧重试。

**A. 要改成绩（`ScoreHooks` 有期望成绩）** —— Sinmai-Assist `FastSkip` 的 CustomSkip 路径：

```
每 monitor: GameMonitor.Seek(0)
NotesManager.StartPlay(0f) ; NotesManager.Pause(true)
GamePlayManager.Initialize(IsPartyPlay())
每 monitor: GameMonitor.ForceAchivement(achievement, dxScore)
```

### 3.4.1 判定表重建（B 级：像真人打的）

`GameScoreList.SetForceAchivement` 被 Prefix `return false` 接管，按目标成绩**自建一整轨判定表**：

| 步骤 | 规则 |
|---|---|
| miss 数 | 由目标决定：≥100% → 0；97%+ → 0~1；95~97% → 1~3；90~95% → 3~8；85~90% → 5~12；80~85% → 8~24；更低按比例 |
| 分布来源 | **优先用 `ScoreRecord.Judge`（data-dev `ScoreMath.BuildJudgeBreakdown`）的 per-type 计数**（`TapCounts/HoldCounts/SlideCounts/BreakCounts` 各 5 档，0=CP/1=Perfect/2=Great/3=Good/4=Miss）；按本轨类型 note 数缩放后逐类型分配，**TouchTap 并入 Tap 池、TouchHold 并入 Hold 池**（Def 区分；与 `.ma2` 的 `T_NUM_TAP`/`T_NUM_HLD` 权重一致）。`Judge == null` 时走 `PickMissCount` + loss 分配兜底 |
| miss 位置 | 均匀散布在整轨（不是全塞末尾） |
| 其余 note | 先全 CP，再按 `round-robin` 把 loss 均摊成 Perfect/Great/Good（CP→Perfect→Great→Good 逐级降），不会大量 TooFast |
| maxCombo | 由 miss 位置实际算：最长连续非 miss 段 |
| sync / combo | 无 miss 且全 CP → `SyncPlay(=FullSyncPlus)` / `AllPerfectPlus`；无 miss → `SyncHi(=FullSync)` / `Gold`；有 miss → `None` |
| 拟合 | 最多 20 次微调（仅 fallback），把误差压到 0.0001%；**拟合值按游戏真实公式**：`achievement% = baseRatio*100 + 1% * breakBonusRatio`（分母不含 break bonus，全 CP = 101%） |
| 回读一致 | 直接把目标百分比写进 `GameScoreList.<Achivement>k__BackingField`，并回写 `CriticalNum/PerfectNum/GreatNum/GoodNum/MissNum/MaxCombo/Combo/TheoryCombo/SyncType/ComboType` → `GetAchivement` 回读 == 目标 |

日志：
```
[score] 采用 data-dev 判定分布: total=1118 cp=952 perfect=134 great=22 good=9 miss=1 maxCombo=1117 sync=2 → 本轨 notes=1118 miss=1
[score] 判定表重建: 目标=97.7342% notes=1118 cp=952 perfect=134 great=22 good=9 miss=1 maxCombo=1117 sync=FullSync 拟合=97.7341%
[score] 结算回读 OK: 入参=977342 回读 raw=977342 (97.7342%)
```
若回读仍不一致 → `UpdateSubbMonitorData` 后置钩子会**就地重建一次**（每轨一次）：
`[score] 结算回读不一致 → 就地重建判定表 repair=True 回读before=… 回读after=…`

**C 模式（不调 `SkipCurrentChart`、挂机自然结算）补充**：游戏自然结算时没有 `ForceAchivement` 入口，
AFK 真实判定是全 MISS(0%)，因此 `ResultProcess.OnStart` 会做一次「Result 阶段补写判定表」：
`[score] Result 阶段补写判定表: monitor=0 ok=True before=raw=0 after=raw=977342 (C 模式自然结算兜底)`
（幂等；与 UpdateSubb 兜底重复时第二次会因回读已一致而跳过）。

**B. filler / 中止轨（不改成绩）** → 游戏原生跳过：

```
GameScoreList.SetTrackSkip()                         // 标记 TRACK SKIP
GamePlayManager.SetTrackSkipFlag(i, trackNo)          // 1.65 名字；1.55 是 SetTrackSkipFrag，两个都试
```

**共同收尾**（两路都走，FastSkip 同款顺序）：

```
每 monitor: GameProcess.UpdateSubbMonitorData(i)      // 生成结算数据
每 monitor: container.processManager.SendMessage(_message[i])
每 monitor: GamePlayManager.SetSyncResult(i, trackNo)
GameProcess.SetRelease()                              // 进入 Result
```

`trackNo = GameManager.MusicTrackNumber - 1`（0-based；取 GameScoreList 时 0/1-based 两种都兜底）。

### 3.5 续关（批次之间走 CONTINUE，不刷卡）

用户拍板：批次之间用 **CONTINUE YES** 续关（同一会话跑完 33 个 credit），避免"登出→刷卡"时上一局
`GameProcess` 残留导致的 `NullReferenceException at Process.GameProcess.OnUpdate`。

```csharp
bool Navigation.PressContinueYes(int monitor = 0);   // 幂等，flow 每 1.5s 调一次
```

梯次（`ContinueSequence`：Init0/Wait1/Disp2/DispEnd3/Continue4/DownloadWait5/Error6/Release7）：

| step | 动作 | 日志 |
|---|---|---|
| 1 | `ContinueMonitor.SelectContinue()` | `[nav] 续关: 按「是」 step=1 method=… ok=…, state=Disp(2), entry=…` |
| 2 | `ContinueMonitor.PlayContinue()` | 同上 step=2 |
| 3 | `ContinueProcess.DetailInputOnDisp(monitor)` | 同上 step=3 |
| 4 | `ContinueProcess.NextContinueProcess()`（私有，兜底） | 同上 step=4 |
| 5/6 | 触摸注入 `ContinueButtonTouchAreas = {"E5","E4","E6","D5","D4"}`（右下角） | `[nav] 续关: 按「是」 step=5 method=touch:E5 ok=True` |

- `_state >= Continue(4)` → 视为已成功（`[nav] 续关: 状态已进入 Continue/DownloadWait → 下一局进行中`）；
- `ContinueProcess` 消失后打 `[nav] 续关: ContinueProcess 已消失 → 已进入下一局`；
- 按 YES 后 `_state` 走 `Continue(4) → DownloadWait(5) → Release(7)`，`NextContinueProcess()` 建新 credit；
  下一屏通常是 `ModeSelectProcess`（写入阶段 OK 梯次会接管），也可能直接 `MusicSelectProcess`，两种都覆盖。

**崩溃防护**（刷卡路径也生效）：patch `Process.GameProcess.OnUpdate` 的防御 Prefix ——
`_monitors` / `_message` / container / `GamePlayManager` 任一为空（旧进程残留、状态已拆）时跳过原版，
日志每 5s 一条：
`[nav] GameProcess.OnUpdate 防御跳过 (state 已拆): monitors 为空; stack=[…]`

### 3.6 收尾展示页（一局结束后"很多页"）

一局结束后的全屏展示页链：`Result → PhotoEdit(SKIP) → MapResult → FriendSeasonRanking(SKIP) → CollectionGet(SKIP) → DataSave → Continue`。
每页都有独立 API（1.5s/步、幂等），flow 在 `stage==Other` 且栈里有这些页时每帧调 `PressAnyTailScreen()` 即可，**不要判失败**：

```csharp
bool Navigation.IsTailScreen();               // 四类页任一存在
bool Navigation.PressAnyTailScreen(int monitor = 0);
bool Navigation.PressPhotoEditSkip(int monitor = 0);
bool Navigation.PressCollectionGetSkip(int monitor = 0);   // 获得收集品的提示页
bool Navigation.PressCollectionSkip(int monitor = 0);      // 收藏品浏览页（CollectionProcess，与上面不同）
bool Navigation.PressGetPresentSkip(int monitor = 0);      // 礼物/解禁提示（GetPresentProcess）
bool Navigation.PressUnlockMusicSkip(int monitor = 0);     // 解禁乐曲（UnlockMusicProcess）
bool Navigation.PressFriendRankingSkip(int monitor = 0);
```

| 页面 | 状态机 | 跳过的梯次 |
|---|---|---|
| `PhotoEditProcess` | `_sequence` Initialize0/Staging1/ReleaseWait2/Release3/End4；`_subSequences` Init0/FirstInfoInit1/FirstInformation2/Staging3/Update4/TimerWait5/Release6 | 仅 `sequence==Initialize` 或 `sub<=2` 时等待入场；之后 `OnTimeUp → OnTimerComplete → CheckButton → 触摸 E8/D8/E7/D7/E6/E5`；**15s 无进展 → 直写 `_sequence=ReleaseWait(2)`** |
| `CollectionGetProcess`（全局命名空间） | Init0/GotoEnd1/StartAnim2/Wait3/Update4/Finish5/Released6 | `_isComplete[monitor]=true + ChangeFinishState → GotoSkipNextProcess → GotoNextProcess → 触摸 E7/E8/D7/D8/E6`；**15s → 直写 `_isFinished=true` + ChangeFinishState** |
| `CollectionProcess`（全局命名空间，浏览页） | Sequence Init0/Start1/Update2/GuestWait3/Released4/EndWait5；SubSequence …/Exit12/ExitLeft13 | **`sequence==Start(1)` 先写 `_sequence=Update(2)`**（信用 2 实机：Start 状态下 SetSubSequence 被忽略）→ `SetSubSequence(Exit=12) → ExitLeft=13 → 若 sub 不变则写 _sequence=Update 重置再试 → 直写 _subSequence=Exit → 写 _sequence=Released(4)`；15s 后强制 Released + 触摸 E5/D5/E4/D4/E6/B1/B2/E1（OK/决定区 + BACK 区） |
| `GetPresentProcess` / `UnlockMusicProcess` | `_state` Wait0/GotoEnd1/Disp2/Released3（无公开 Skip 方法） | 通用"简单展示页"：试 `GotoEnd/OnTimeUp/Skip/ToNext/GotoNextProcess` → 写 `_state=GotoEnd(1)` → 写 `_state=Released(3)` → 15s 后触摸 E5/D5/E4/D4/E6 |
| `FriendSeasonRankingProcess` | Init0/Wait1/PlaySubMode2/Release3/Released4；`_subState[]` | `_isSkipSequence=true + _isSkipPlayer[monitor]=true + SetButtonSkip → monitor.Skip() → SubUpdate → SetButtonOK + 触摸`；**15s → 直写 `_state=Release(3)`** |

> 用户截图证明 SKIP 按钮可能一直可见，但 `_isShowSkipButton` 字段并不可靠 —— 只用于日志，不再作为等待条件。

### 3.6.1 误触「設定」防护（实机 03:51）

MODE SELECT 左上角是「設定」按钮（`Process.SimpleSettingProcess`）。为避免注入误触：

1. **`Touch` 安全闸门**：在 `ModeSelectProcess` / `MusicSelectProcess` 存在时，**拒绝 A/B 组触摸区（index 0..15）**，
   日志 `[nav] Touch 拒绝：菜单页(MODE SELECT/选歌)禁止 A/B 组触摸区 area=…（防止误触左上角設定）`；
2. 所有收尾页候选区已去掉 A/B/C 组，只保留右侧/底部（D/E 环）；
3. **`PressMenuButton`（合成 Button04/05）在菜单页/設定页直接拒绝**，日志 `[nav] PressMenuButton 拒绝：菜单页/設定页禁止合成按键（防误触）`；
4. **收尾页与菜单页共存时不注入**（收尾页可能在后面释放），日志 `[nav] 收尾页与菜单页共存 → 暂不注入（防止误触）`；
5. 检测到設定页立即退出：
   ```csharp
   bool Navigation.ExitSettings(int monitor = 0);   // _sequence=Release(5) → GotoNextProcess → _subSequence=None(7) → Button05(BACK)
   ```
   日志 `[nav] 误触設定页 → 退出; stack=[…]` / `[nav] 設定页退出 step=… method=… ok=…`；
   `GoToMusicSelect()` / `HandleModeSelect()` / `PressAnyTailScreen()` 入口都会先调它。

### 3.6.2 直调选区（`RegionalSelectProcess`）

```csharp
bool Navigation.IsAreaSelectScreen();
List<Navigation.AreaInfo> Navigation.ListAreas(int monitor = 0);
void Navigation.RequestSelectArea(int cursorIndex);          // flow 指定要选的区光标
bool Navigation.SelectAreaDirect(int cursorIndex, int monitor = 0);
bool Navigation.SelectArea(int areaId, int monitor = 0);     // 按 areaId
```

**`SelectAreaDirect` 梯次**（1s 节流）：
1. `_state <= Initialize(0)` → 等下一帧（`[nav] 直调选区: 等待 Initialize→Update`）；
2. `_subStates[monitor] ∈ {FirstInformation(1), FirstInfoWait(2), Discover(3)}` → `TimeUp()` 推进；
3. `SelectCursolIndex(cursor)` + 直写 `_selectCursolIndex[monitor]`；
4. `SetShowDecideButton(monitor)` → `RegionSelectDecide(monitor)`（失败再 `Decide(monitor)`）；
5. **15s 无进展 → `TimeUp()` 兜底**（选默认区）；
6. 日志：`[nav] 直调选区: cursor=N→M state=Update(1) sub=Update(6) → SelectCursolIndex=… SetShowDecideButton=… RegionSelectDecide/Decide=…`

**关键约束**：`GoToMusicSelect()` 遇到 `RegionalSelectProcess` → 直接走 `SelectAreaDirect` 并返回（**绝不 fade 跳过选区**）；
MODE SELECT OK 梯次的 P5 强切也会检查选区页：有选区页时**不强切**，只有 60s 仍推不动才允许 fade 兜底。

### 3.7 15s 轨间隔与 ETA
| 常量 | 值 | 含义 |
|---|---|---|
| `Navigation.MusicSelectFixedOverheadSec` | 8.0 | 每次进选歌页固定开销 |
| `ChartSelectOverheadSec` | 2.0 | 选曲+难度+确认 |
| `StartSelectionOverheadSec` | 3.5 | 选歌页→TrackStart→GamePlay |
| `SkipToSettleSec` | 2.5 | 跳过→结算就绪 |
| `ResultToSelectSec` | 4.5 | 结算→回选歌页/Continue |
| `MinTrackGapSec` | 15.0 | 相邻 playlog 最小间隔 |

运行时观测：`Navigation.RemainingTrackGapSec / SecondsSinceTrackStart / SecondsSinceTrackSettled / MusicSelectElapsed`。
`TimeEstimator.CreditOverheadSec(config, skip)` = `max(Config.BaseOverheadPerCreditSec, Navigation.EstimateCreditOverheadSec(skip))`。

### 3.6 成绩注入 `ScoreHooks`

| 成员 | 作用 |
|---|---|
| `PendingForThisTrack` | 本轨期望成绩（flow 在选曲前设置；filler 时为 null） |
| `SkipCurrentTrack` | true = 本轨绝对不改成绩（filler/中止） |
| `BeginTransfer / BeginFiller / BeginSkipUnmodified / BeginMission` | flow 用的便捷入口（`BeginMission` 强制 `MinClearAchievement=800000`=80.0000%） |
| `EndTrack()` | 每轨结束清状态（`Navigation.SkipCurrentChart` 也会自动清一次） |
| `LastSettledRecord / LastSettledAchievement / LastTrackWasUntouched` | 结算后可回读的结果，给 data-dev / flow 对齐 |
| `ForceUnit` / `ConvertForForce` / `SetForceUnit` / `LoadFromConfig` | ForceAchivement 入参单位（见 §6） |

两个 Harmony Prefix 兜底：`GameMonitor.ForceAchivement` 与 `GameScoreList.SetForceAchivement`
只要有 `PendingForThisTrack` 且 `SkipCurrentTrack==false`，就把入参改写成期望值；
`GameProcess.UpdateSubbMonitorData` 的 Postfix 回读 `GamePlayManager.GetAchivement(monitor, trackNo)` 并打印对比日志。

**写回 UserData（per-track，和 data-dev / flow-dev 约定的方案 A）**：
`Process.ResultProcess.OnStart` Postfix → 在 `+0.25s / +0.8s / +1.8s` 三次机会里调
`UserDataPatcher.Commit(monitor, record)`（游戏写完本轨 ScoreDic 之后、上传之前），
`_committedThisTrack` 保证只成功写一次。filler / 中止轨（`SkipCurrentTrack` 或没有候选记录）**不调用 Commit**。
**不使用 `CommitBatch`、不在 DataSave 批量写**（避免与上传时序耦合；WriteMode=Both 时 `PayloadRewriter` 还有报文兜底）。

`Commit` 返回值语义（data-dev 定义）：`true` = **内存写了 或 报文已排队**（WriteMode=Both 时内存 `touched=0` 也可能 true）。
所以提交后还会读 `UserDataPatcher.LastCommitMemoryTouched` / `LastCommitQueuedPayload` 两个诊断字段，日志形如：

```
[score] 成绩写回 UserData(Result+0.25s): monitor=0 内存touched=1 报文排队=True 1234-3 100.5000%
```

`内存touched=0 报文排队=True` = 只走了报文兜底（内存时序没赶上，WriteMode=Memory 时会丢分）；
`Commit 返回 false` 会打 Warn 并在 +0.8s/+1.8s 重试。data-dev 侧对应的行是 `UserDataPatcher.Apply: ... touched=N added=M rating=...`
（`added>0` 表示目标账号原本没有这首歌，正常）。

**只读闸门 / 安全写（Lead 的 `Core.WriteGuard`）**：
- `ScoreHooks.DisableAllScoreWrites`：**默认绑定只读闸门**（`WriteGuard.WritesAllowed == false` 时为 true），
  为 true 时**连游戏内 `ForceAchivement` 都不写**——因为强制成绩会进 `userPlaylogList`，被游戏原生 DataSave 上传。
  手动 `= true` 可强制关掉（即使闸门允许写）；`= false` 不会绕过只读闸门。
- `WriteGuard.WritesAllowed == false`（`Config.SafeNoWrite=true`，默认）时，ScoreHooks **不安排任何 Commit、不重试**、
  `ConsumeTrackScore` 直接返回 false（跳曲走 TrackSkip，不改成绩），每 credit 只打一条 INFO：
  `[score] 只读模式 (WriteGuard.SafeNoWrite=true / DisableAllScoreWrites) → 本 credit 不改任何成绩（不 ForceAchivement、不入 UserData）`
- `Commit` 返回 false 且 `内存touched==0 && 报文未排队`（只读/写前备份失败/游客/越界等硬失败）→ 立即停止本轨重试，
  打 `[score] Commit 未接受任何写路径（内存=0 且报文未排队）→ 停止本轨重试`。
- 真正写入前，data-dev 会强制导出 `FakerSwap/Backup/pre-write-*.json`（120s 内复用）；导出失败会拒绝写入
  （`PRE-WRITE BACKUP FAILED ... -> account write REFUSED`，纯 ASCII），不要强行重试（由 Lead 处理磁盘/权限后重跑）。

> **DeluxscoreMax**：传源账号记录的原始值；只有 `<= 0` 时才不覆盖，交给游戏自己按判定算。

### 3.7 地图 / 课题曲 `MapHooks`

| 能力 | 实现 |
|---|---|
| 区域选择 3s 超时 | Harmony Postfix `RegionalSelectProcess.OnStart/OnUpdate`；`OnStart` 记基准时间/光标，`OnUpdate` 计时到 `Config.AreaSelectTimeoutSec`（0.5~60 钳制）后调 **public `TimeUp()`** = 游戏默认项 |
| 人工换区检测 | 每帧比较 `SelectCursolIndex(0)` 与基准；变化即 `AreaManuallyChanged=true` + `CancelMission()`；并锁定"本次区域选择不再自动加课题曲"（`_areaGeneration/_cancelledGeneration`，下一次区域选择界面出现自动解除） |
| 已确认区域 | `_subStates[0] >= 10`(ToDecide) → 记录 `SelectedAreaId/SelectedAreaName` |
| 区域候选日志 | 每个区域选择界面**只打一行摘要** `[map] 区域候选 N 个 (当前光标=…, 课题区=有/无)`；完整列表只在 `Config.Verbose=true` 时输出。仅 `Navigation.IsTaskActive()` 为真时接管（用户自己玩时**不打日志、不采样、不 TimeUp**） |
| 课题曲扫描 | 进入选歌页时扫 `MusicSelectProcess` 私有字段：`_combineMapTaskDataList`(MapTask) / `_combineCircleDataList`(1.65 CircleTask) / `_combineChallengeDataList`(Challenge)；每首取**最低可用难度**（`MusicSelectData.isEnable` 第一个 true；否则 `Difficulty`），成绩 = `ScoreHooks.MinClearAchievement` |
| 结果传递 | `MissionTaskActive`、`PendingMissionCharts`、`TakeMissionCharts()`、`CancelMission(reason)`、`ResetForCredit()` |

---

## 4. 进程 / 容器访问（NavProcess）

- **进程栈**：反射 `ProcessManager._processList`（`LinkedList<ProcessControle>`，`ProcessControle.Process` 是公开字段），每帧快照，遍历时不会因为游戏改链表而崩（异常吞掉 + 限频告警）。
- **ProcessDataContainer 获取**（两条路）：
  1. `Navigation.Install()` patch `ProcessDataContainer` 构造函数（Postfix 抓实例）—— 正常路径；
  2. 兜底：`FindObjectOfType<Main.GameMainObject>()` → 私有字段 `main` → `Main.GameMain._container` / `_processManager`（无 patch 也能工作）。
- **进程操作**：`ProcessManager.AddProcess(process, priority)`、`ReleaseProcess(process)`、`new FadeProcess(container, from, to)`。

---

## 5. 反射成员清单（换版本重点核对）

`Navigation.Probe()` 在 `Install()` 时检查并打印 `[nav] API probe: ok=N miss=M`。清单：

| 类型 | 成员 | 用途 |
|---|---|---|
| `Process.MusicSelectProcess` | `GameStart()`(private) | 进入当前选曲（首选路径） |
| | `CombineMusicDataList`(prop) | 曲目列表（分类→条目） |
| | `CurrentCategorySelect` / `CurrentMusicSelect`(prop) | 选中位置 |
| | `DifficultySelectIndex` / `CurrentDifficulty`(prop) | 难度 |
| | `SortDecidePlayer` / `ScoreType`(prop) | 玩家索引 / STD-DX |
| | `ClampMusicIndex()` | 选中后夹紧 |
| | `GetLevelToListPositoin(int,int)` | 预留（等级分类定位） |
| | `PrepareFinish` / `IsPreparationCompletes` | 进入前置标志 |
| `MusicSelectProcess.CombineMusicSelectData` | `GetID(ScoreKind)` / `musicSelectData`(field) | 取 musicId / 难度数据 |
| `MusicSelectProcess.MusicSelectData` | `Difficulty` / `isEnable`(field) | 难度 / 可用难度表 |
| `Process.GameProcess` | `_sequence`(field, enum) | 跳曲窗口判定 |
| | `_monitors`(field) / `_message`(field) | 跳曲所需 |
| | `UpdateSubbMonitorData(int)` / `SetRelease()` / `IsPartyPlay()` (private) | 跳曲收尾 |
| `Monitor.GameMonitor` | `Seek(int)` / `ForceAchivement(int,int)` | 强制成绩 |
| `Manager.GameScoreList` | `SetForceAchivement(int,int)` / `SetTrackSkip()` / `ScoreTotal` | 成绩注入 / 跳过 |
| `Manager.GamePlayManager` | `Initialize(bool)` / `SetSyncResult(int,int)` / `GetGameScore(int,int)` / `GetGameScores(int)` / `GetAchivement(int,int)` / `SetTrackSkipFlag(int,int)`(1.55=`SetTrackSkipFrag`) | 跳曲与回读 |
| `Manager.NotesManager` | `StartPlay(float)` / `Pause(bool)` | 强制成绩前置 |
| `Manager.GameManager` | `SelectMusicID/SelectDifficultyID/SelectGhostID/SelectScoreType/SelectedDeleteGhostID/IsNormalMode/IsSelectContinue/MusicTrackNumber/SetMaxTrack()` | 选曲与模式 |
| | `IsLongMusic`(1.65 新) | 长曲标记（反射写，1.55 不存在） |
| `Process.RegionalSelectProcess` | `TimeUp()` / `SelectCursolIndex(int)` / `GetUserMapDatas(int)`；`_userMapDataList` / `_selectCursolIndex` / `_subStates` | 区域选择 |
| `Process.Information.InformationProcess` | `_state`(private enum) | 跳过提示页 |
| `Process.EntryProcess` | `GetCurrentState(out StateType, out int)` / `SetNextProcess()` / `_monitors` | Entry 自动确认 |
| `Monitor.Entry.EntryMonitor` | `DecideEntry()` / `ResponseYes()` / `ResponseNo()` | 确认页按键 |
| `Manager.InputManager` | `SetButtonDown(int, ButtonSetting)` | 合成按键（Entry 确认页） |
| `InputLock`(本项目) | `AllowSyntheticInput`(bool 字段) | 合成输入时临时放行（反射读写，缺失则不处理） |
| `Process.GenericProcess` | `_timerController`(`TimerController[]`) | 找到游戏倒计时控制器 |
| `TimerController`（global） | `CountDownSecond` / `IsInfinity` / `IsTimeCounting` / `IsTimeUp` / `SetTimeZero()` / `DecrementTime(int)` / `UpdateTimer(float,int)` / `PrepareTimer(int)` | 登出时强制倒计时归零 |
| `Process.ResultProcess` | `OnStart()` | 每轨结算起点：per-track 写回 UserData |
| `Process.ModeSelect.ModeSelectProcess` | `_state(Wait0/Disp1/Released2)` / `_isTimeSkip` / `_isTimeUp` / `TimeSkipButtonAnim(ButtonSetting)` / `TimeSkipStart()` / `isOKTimerZero()` / `OKStart()` / 私有 `TimeUp()` | 结束受付（直调函数 + 直写终态） |
| `Process.EntryProcess` | `IsTimeCounting(bool)` / `IsFastSkip(bool)` / `DecrementTimerSecond(int)` / `SetNextProcess()` | 登录后受付屏推进 |
| `Process.Entry.State.ConfirmPlay` | `Exec(float)` / `_mode` / `_responseMonitor` / `_isConfirmNewUser` / `_isBAN` / `_isWarning` / `_buttonAnimWait` / `_agreeStartWait` / `_warningInfoWait` / `_timer` | ConfirmPlay 状态机推进与诊断 |
| `Process.Entry.State.ProcEnum<T>` | `Mode`(get/set，setter 触发 `_onSet`) | 状态机当前阶段读写（强推 9→10 用） |
| `Process.Entry.State.Context` | `GetCurrentState(out int key, out State state)` | 拿到当前 Entry 状态机实例 |
| `Manager.DataManager` | `GetMusic(int)` / `IsLong(int)` | 长曲判定 |
| `Process.ContinueProcess` | `_state` / `NextContinueProcess()` | 续关 |
| `Manager.ProcessManager` | `AddProcess` / `ReleaseProcess` / `SendMessage` / `ForceTimeUp()` | 进程操作 |
| `Process.FadeProcess` | `.ctor(container, from, to)` | 界面切换 |
| `Process.TrackStartProcess` | `.ctor(container)` | 选歌→开场兜底 |

---

## 6. ForceAchivement 单位校准（实机必看）

`GameMonitor.ForceAchivement(int achivement, int dxscore)` 的入参单位无法静态确认（1.65 方法体是加密的），
所以做成**配置可切换**，默认 `TenThousandth`：

| `ScoreHooks.ForceUnit` | 传给游戏的 achievement | 说明 |
|---|---|---|
| `TenThousandth`（默认） | 原值（1005000 = 100.5%） | 与 `UserMusicDetail.achievement` 同单位 |
| `Percent100` | `原值 / 10000`（整数百分比，100.5% → 100） | 若游戏要整数百分比 |
| `Raw` | 原值，不做任何换算 | 排查用 |

配置方式：`config.json` 里加 `"ForceAchievementUnit": "Percent100"` 即可（`ScoreHooks.LoadFromConfig()`
用反射读取，**Core/ModConfig.cs 没加这个字段也完全不影响编译**；加了就自动生效）。

日志怎么判断：

```
[score] 结算回读 OK: 入参=1005000 回读 raw=1005000 (100.5000%) 期望 raw=1005000 (100.5000%)
[score] 结算回读 不一致: 入参=1005000 [TenThousandth] 回读 raw=1010000 (101.0000%) 期望 raw=1005000 (100.5000%)
[score] 建议: 入参被游戏当成百分比整数后封顶 → 把 ForceAchievementUnit 改成 Percent100
```

只要 `回读 raw` 与 `期望 raw` 差 >100（0.01%）就会告警，并给出三种常见单位错误的建议。

---

## 7. 日志关键字表（`Package/FakerSwap/Logs/*.log`）

| 关键字 | 含义 |
|---|---|
| `[nav] Navigation 已安装` | harmony id `fakerswap.flow.navigation` 安装成功 |
| `[nav] API probe: ok=53 miss=0` | 反射成员自检结果；miss>0 会把缺失项列在下一行 |
| `[nav] 游戏版本: SDEZ / 26500` | 运行时实际版本 |
| `[nav] 进入选歌页 (player=0)` | 选歌页 OnStart 捕获（后续选曲从这里开始） |
| `[nav] GoToMusicSelect 无匹配处理器: stack=[...] stage=...` | 每 10s 一次：卡在没人处理的界面，看进程栈定位 |
| `[nav] Entry 状态: ConfirmPlay (mode=0)` | Entry 子状态变化 |
| `[nav] Entry 确认页(state=4) -> 按「是」x1 (含合成按键)` | 旧格式（保留兼容） |
| `[nav] ConfirmNewUser → 已按「是」建号（AllowConfirmNewUser=true）; mode=…, try=N/6` | 新卡建号（需开关） |
| `[nav] ConfirmNewUser 已按 N 次仍未完成建号 → 停手等人工; mode=…` | 建号上限 |
| `[nav] Entry 确认页 -> 按「是」(…, officialYesNo=True, isNewAime=True, state=3, try=1)` | 官方 YesNo 路径生效 |
| `[nav] Entry 候选方法: ...` | 首次进入 ConfirmEntry 时打印相关类型的候选方法名 |
| `[nav] ConfirmPlay 诊断(mode 变化): mode=9/(SelectInWait) _isConfirmNewUser=False _isBAN=False _isWarning=False … _responseMonitor=EntryMonitor _timer=…` | ConfirmPlay 现场（新卡/BAN/警告/等待计数/响应 monitor） |
| `[nav] ConfirmPlay.Exec 注入「是」(mode=WaitResponse(17), monitor=EntryMonitor, try=N)` | 真正注入的瞬间（只在 mode 17） |
| `[nav] ConfirmPlay 卡在 SelectInWait(9) → 强制推进 _mode 9→10 (Select) ok=True` | 20 tick 救援：强推状态机 |
| `[nav] ConfirmPlay 报告 _isConfirmNewUser=true … → 为安全起见不自动按是` | 服务端把卡当新卡（账号坏了），拒绝自动确认 |
| `[nav] Entry 自动推进已跳过：插件任务未运行（RebirthFlow=…, MachineLock=…）` | 用户自己在玩，自动推进被任务闸门挡住（只打一次） |
| `[nav] ConfirmEntry 的 mode=2 (Reading)，属于新卡/继承/错误类子流程，为安全起见不自动按是` | 读卡中/危险子流程，故意不按 |
| `[nav] 检测到已登录成功 → 停止自动按键（共尝试 N 次）` | 登录完成，Entry 确认键收手；随后继续推进受付屏 |
| `[nav] 登录成功 → 受付屏连点 timeskip（仅 EntryProcess 受付）; state=…, mode=…` | 受付屏连点开始 |
| `[nav] 受付连点 timeskip N/120, state=…, mode=…, stack=[…]` | 受付屏连点进度（每 10 帧一条） |
| `[nav] 已到 MODE SELECT，停止一切注入（等待 flow 读取/被动登出）` | 到达 MODE SELECT，全部静默 |
| `[nav] ModeSelect 等待进入 MODE SELECT(state=…)，不注入` | 读取阶段等待 MODE SELECT |
| `[nav] 登出: 强制倒计时归零 attempt=N, controller=TimerController[0], before=…, after=0, isInfinity=…, isTimeUp=…, ok=True` | 登出时把游戏倒计时推到 0 |
| `[nav] 登出: 倒计时归零后 stage=…（期待 -> Title）` | 首次归零后的阶段 |
| `[nav] 登出: 倒计时生效 → stage=Title，游戏将自行 UserLogout` | 游戏进入超时登出流程（目标状态） |
| `[nav] 登出: 当前在 MODE SELECT(state=…) → 不按键，只强制倒计时归零` | MODE SELECT 登出策略 |
| `[nav] 登出: 强制倒计时归零 300 次仍没回 Title → 停手（flow 的 PacketUserLogout 兜底）` | 失败交给 flow |
| `[nav] 登出连点 timeskip: EntryProcess burst=N (10 次/组)` | 登出时受付屏仍在 EntryProcess 才连点 |
| `[nav] MODE SELECT 开始按 OK(NORMAL): state=…, _isEnableOK=…, _isCheckOK=…, _isOK=…, _isDecidedOK=…` | 写入阶段开始按 OK |
| `[nav] MODE SELECT 连点 OK: n/120, button=Button05, anim=True, …` | OK 连点进度（每 10 帧） |
| `[nav] MODE SELECT 连点 OK Button05 120 帧无效果 → 换 Button04 再试` | 换按钮 |
| `[nav] MODE SELECT 连点后直调: …` / `直接写 _isDecidedOK/_isOK…` | 直调/直写梯次 |
| `[nav] MODE SELECT 上有 PleaseWaitProcess 挡住输入 → 等它结束再按 OK` | PleaseWait 阻塞检测 |
| `[nav] touch map: GamePanelDown dims=2x2x35` / `touch map TouchAreaX[n] = …` | Install 时 dump 触摸数组与面积分组 |
| `[nav] MODE SELECT 连点后直调: … touch=True(area=E5)` | OK 触摸候选区尝试 |
| `[nav] TouchButton ButtonSetting.Button05 → areas=[…] ok=True` | 按按钮名注入触摸 |
| `[nav] Result NEXT 推进: step=N/6, method=…, ok=…` | 结算页 NEXT 梯次 |
| `[score] patch targets: …=ok/MISS` | ScoreHooks install 时确认 patch 目标签名 |
| `[score] SetForceAchivement 重写完成: … 目标=95.5690% 判定=N miss=M` | 判定表重建（成绩注入关键） |
| `[nav] 90s 未到选歌页，交回 flow`（写入阶段） | 写入阶段总时限 |
| `[nav] NameEntry`（无） | **未注册新账号的名字页不做任何注入**（用户要求人工起名） |
| `[nav] ModeSelect 连点 timeskip: 1/120, anim=True, state=Disp(1), …` | 仅写入阶段：MODE SELECT 前的受付连点 |
| `[nav] 检测到登出(ForceTimeUp) → 若卡在受付屏将用 timeskip 连点结束` | flow 登出信号被捕获（MODE SELECT 屏上仍不注入） |
| `[nav] ModeSelect 连点完成 120 帧, state=…, _isTimeSkip=…, _isTimeUp=…` | 连点结束，进入直调梯次 |
| `[nav] 检测到登出(ForceTimeUp) → 若卡在受付屏将用 timeskip 连点结束` | flow 登出信号被捕获 |
| `[nav] 登出连点 timeskip: ModeSelect burst=N (20 次/组), state=…` | 登出时连点推进受付屏 |
| `[nav] 登出连点 timeskip: EntryProcess burst=N (10 次/组)` | 受付屏仍在 EntryProcess 时的登出连点 |
| `[nav] 登出: ForceTimeUp 未推进，改用 timeskip 连点结束受付` | 登出兜底生效 |
| `[nav] ModeSelect 直调: TimeSkipStart=True, state=Disp(1), _isTimeSkip=…` | 直调按钮处理函数 |
| `[nav] ModeSelect 直接写 _state=Released: ok=True, was=Disp(1)` | 直接写终态字段 |
| `[nav] ModeSelect 直调(OK 路径): isOKTimerZero=…, OKStart=…` | OK 按钮兜底 |
| `[nav] ModeSelect 强切 MusicSelect (…) ok=…` | 强切兜底 |
| `[nav] ModeSelect 强切后 5s 仍未到选歌页 → 停止重试; stack=[…]` | 强切失败后的现场 |
| `[nav] 25s 未到选歌页，交回 flow（停止一切导航注入）; stage=… stack=[…]` | 总时限到，交回 flow |
| `[nav] PlInformation 提示页跳过 (state=2 -> DispEnd) ok=…` | 跳过隐私/プレイ情報提示 |
| `[nav] PlInformation 卡住 → Fade 到 MusicSelect 兜底` | PlInformation 兜底 |
| `[nav] 登录后受付屏推进 20 次仍未进入选歌页 → 停手` | 受付屏推进上限 |
| `[nav] Entry 确认页已尝试 N 次仍未前进 → 停手` | 到 `MaxEntryAttempts` 上限，等人工/flow 超时 |
| `[nav] Entry DoneEntry → SetNextProcess 第 N 次` | DoneEntry 卡住时催场（最多 3 次） |
| `[nav] Entry 状态: ConfirmEntry (state=1, mode=0/(Setup))` | Entry 状态 + mode（判断是否安全白名单用） |
| `[nav] Entry 状态 3 属于「不自动确认」白名单...` | 新用户/输码页，故意不按 |
| `[nav] ConfirmEntry 的 mode=1（非 0）...` | 非标准 ConfirmEntry 子页，不自动按（请把日志发 nav-dev） |
| `[nav] 合成按键失败` | `InputManager.SetButtonDown` 不可用/调用异常 |
| `[score] 成绩写回 UserData(Result+0.25s): monitor=0 内存touched=1 报文排队=True ...` | per-track 写回结果（touched/queued 见 §3.6） |
| `[score] 只读模式 (WriteGuard.SafeNoWrite=true / DisableAllScoreWrites) → 本 credit 不改任何成绩（不 ForceAchivement、不入 UserData）` | 只读验证时每 credit 一条，无噪音 |
| `[score] Commit 未接受任何写路径（内存=0 且报文未排队）→ 停止本轨重试` | 硬失败（只读/备份失败/游客/越界），不再重试 |
| `PRE-WRITE BACKUP FAILED (...) -> account write REFUSED` | data-dev 侧：写前备份失败，拒绝写入（纯 ASCII） |
| `[nav] Information 提示页跳过` | 跳过活动提示 |
| `[nav] Fade X -> MusicSelectProcess` | 菜单页跳过 |
| `[nav] 选中 11414-3 (cat=2 idx=17 kind=1)` | 选曲结果（cat/idx 是分类内坐标，kind: 0=STD 1=DX） |
| `[nav] 选歌列表里找不到 11414-3` | 选曲失败（下架/排序未加载） |
| `[nav] 进入第 N 轨: musicId-level` | 已调 GameStart |
| `[nav] GameStart 不可用(...)，改用 Fade->TrackStartProcess 兜底` | 走了兜底路径 |
| `[nav] 等 15s 轨间隔：还差 x.xs` | 15s 间隔未满，SkipCurrentChart 主动等待 |
| `[nav] 跳过第 N 轨 (music=..., score=...)` | 跳曲已触发（score=不改 表示 filler/中止） |
| `[score] 结算回读 OK / 不一致` | 成绩注入结果（§6） |
| `[score] 成绩写回 UserData(Result+0.25s): monitor=0 ...` | per-track 写回成功（data-dev 的 Commit） |
| `[score] UserDataPatcher.Commit 返回 false (Result+...)` | 写回失败（会自动重试；最终有报文兜底） |
| `[score] 本轨未改成绩（filler/中止）` | 本轨不写分（也**不会** Commit） |
| `[map] 区域选择界面出现，等待操作员 3.0s` | 区域选择开始 |
| `[map] 区域候选 N 个 (当前光标=…, 课题区=有/无)` | **每个区域选择只一条**（任务运行时）；完整列表需 `Verbose=true` |
| `[map] 课题区候选: id=… name="…"（要看完整列表请把 config.Verbose 设为 true）` | 检测到疑似课题/事件区 |
| `[map] 操作员手动换区: 光标 a -> b` | 人工换区（触发取消课题曲任务） |
| `[map] 区域选择超时 3.0s -> 走默认项 (TimeUp)` | 3s 超时走默认 |
| `[map] 区域已确认: id=.. name=".."` | 区域选择落定 |
| `[map] 检测到课题曲 N 首 (MapTask/CircleTask)` / `[map]   课题曲: 1234-0@80.0000%(MapTask)` | 课题曲扫描结果 |
| `[map] 取消课题曲任务: 操作员手动换区 (清掉 N 首)` | 课题任务取消 |
| `[nav] 底登出: ProcessManager.ForceTimeUp()` | 没有 LoginHandler 时的登出兜底 |
| `[nav] 自动绑定 LoginHooks.RequestLogin` | 委托未显式设置时自动反射绑定 |

---

## 8. 换版本检查清单

1. 把游戏目录 `Package/Sinmai_Data/Managed/Assembly-CSharp.dll`（以及 `Assembly-CSharp-firstpass.dll`、`AMDaemon.NET.dll`）覆盖到 `FakerSwap/Libs/`，**否则编译能过、运行时 MissingMethod/Field**。
2. `dotnet build FakerSwap/FakerSwap.csproj -c Release` → 0 error。
3. 游戏内看 `[nav] API probe: ok=? miss=?`：
   - `miss=0` 一般可以直接用；
   - 有 miss 时对照 §5 表格改 `Navigation.Probe()` 的成员名；运行时对应功能会打 `[nav] 方法缺失/字段缺失` 限频告警。
4. 重点确认这几个"容易改名"的：
   - `GamePlayManager.SetTrackSkipFlag`（1.55 叫 `SetTrackSkipFrag`）
   - `GameManager.IsLongMusic`（1.65 才加；旧版没有这个字段 → 反射写，缺失不会崩）
   - `MusicSelectProcess.GameStart` 是否仍然无参 private
   - `RegionalSelectProcess.TimeUp` 是否仍然 public 无参
   - `EntryProcess.GetCurrentState` / `EntryMonitor.DecideEntry` / `InputManager.SetButtonDown`
   - `Process.ResultProcess.OnStart`（per-track 写回 UserData 的触发点）
5. 跑一遍 §6 的单位校验，确认 `ForceUnit` 是否要切。
6. 若 `MusicSelectProcess.CombineMusicDataList` 结构改了（分类/条目/ScoreKind），只需要改 `LocateChart()`。

---

## 9. 给 flow-dev 的接口速查

```csharp
// 进选歌页（幂等，每帧可调）
bool Navigation.GoToMusicSelect();

// 通用"直调/直写"工具（任何界面卡住都能直接调用按钮对应函数 + 直接写终态字段）
//   net35 没有 ValueTuple，所以是 "bool + out string error"，不是元组
bool Navigation.CallInstanceMethod(object instance, string method, object[] args, out string error);
bool Navigation.SetInstanceField(object instance, string field, object value, out string error);
int  Navigation.GetInstanceInt(object instance, string field, int fallback = -1);

// 触摸注入（MODE SELECT OK / 结算页 NEXT / 选歌按钮都是触摸区域，不是机台按键）
// 1.65 实测：GamePanelDown/GamePanelPush = bool[2,2,35] = [monitor, 写满, TouchPanelArea(0..34)]
//   area 0..34 与枚举顺序一致：A1..A8=0..7, B1..B8=8..15, C1/C2=16/17, D1..D8=18..25, E1..E8=26..33, 34=Blank
bool Navigation.Touch(int monitor, Manager.InputManager.TouchPanelArea area, int frames = 2);
bool Navigation.Touch(int monitor, string areaName, int frames = 2);            // 例："D4"/"E5"
bool Navigation.TouchAreas(int monitor, string[] areas, int frames = 2);
bool Navigation.TouchButton(int monitor, Manager.InputManager.ButtonSetting b, int frames = 2); // 1.65 映射非按钮→区域，慎用
bool Navigation.PressResultNext();   // 结算页每帧调：SetNextWait → MoveNext → ToNextCheck → ToNextProcess
void Navigation.ClearPendingTouches();
// MODE SELECT「OK」候选触摸区（底部中央），按顺序尝试：E5, E4, E6, D5, D4
string[] Navigation.OkButtonTouchAreas;

// Entry 确认页推进（幂等，每帧可调；Install 里已有 EntryProcess.OnUpdate 自动驱动，flow 可不调）
bool Navigation.TryAdvanceEntry();
// Entry 自动推进任务闸门（用户自己玩时 IsEntryAutomationAllowed()=false，绝不碰输入）
bool Navigation.IsEntryAutomationAllowed();
bool Navigation.IsTaskActive();              // MachineLock.Active || Phase ∈ 执行阶段（MapHooks 也用它）
bool Navigation.RequireTaskActiveForEntry;   // 默认 true
bool Navigation.ForceEntryAdvanceWhenIdle;   // 测试用，默认 false
bool Navigation.AllowConfirmNewUser;         // 默认 false：新卡建号确认（config 键 AllowConfirmNewUser）

// 选曲 + 选难度（幂等；跨分类定位；同时写 GameManager 选择状态）
bool Navigation.SelectChart(int musicId, int level);

// 进入当前选曲（调 GameStart，失败 Fade 到 TrackStartProcess）
bool Navigation.StartCurrentSelection();

// 立刻结算当前谱面（等满 15s；有期望成绩则 ForceAchivement，否则 TrackSkip）
bool Navigation.SkipCurrentChart();

// 阶段查询
bool Navigation.AtEntry / AtMusicSelect / InGameplay / AtAreaSelect / AtContinue / AtTitle;
double Navigation.SecondsSinceTrackStart / SecondsSinceTrackSettled / RemainingTrackGapSec / MusicSelectElapsed;

// 登录/登出桥（flow-dev 在 LoginHooks.Install() 里设置委托）
Func<string,bool> Navigation.LoginHandler;   // = LoginHooks.RequestLogin
Func<bool>       Navigation.LogoutHandler;   // = LoginHooks.RequestLogoutByTimeout
bool Navigation.RequestLogin(string accessCode);
bool Navigation.RequestLogoutByTimeout();    // GamePlay 阶段 handler 返回 false 时不强退；受付屏会连点 timeskip
int  Navigation.ModeSelectRapidPressFrames;  // 连点帧数，默认 120（≈1~2s 连续快速按）

// Continue（flow 目前自己写 IsSelectContinue，这两个是可选实现）
bool Navigation.ContinueToNextTrack();
bool Navigation.DeclineContinue();

// 成绩
ScoreHooks.BeginTransfer(record) / BeginFiller() / BeginSkipUnmodified() / BeginMission(record);
ScoreHooks.EndTrack();
int ScoreHooks.MinClearAchievement;            // = 800000 (80.0000%)
ScoreHooks.AchievementUnit ForceUnit;          // 见 §6
ScoreHooks.AutoCommitToUserData;               // 默认 true：Result 阶段 per-track Commit
bool ScoreHooks.DisableAllScoreWrites;         // 默认绑定 !WriteGuard.WritesAllowed；true = 连 ForceAchivement 都不写
bool ScoreHooks.CommitTrackScore(record, why); // 手动写回（一般不用，Result 钩子会自动调）
bool ScoreHooks.LastCommitOk;                  // true = 内存或报文至少一路接受
int  ScoreHooks.LastCommitMemoryTouched;       // 真正写进内存的行数（0=只走报文）
bool ScoreHooks.LastCommitQueuedPayload;       // 是否已排队报文兜底
ScoreRecord ScoreHooks.LastSettledRecord;      // 结算后可回读
int ScoreHooks.LastSettledAchievement;

// 合成按键（Entry 确认页用；其他界面不要依赖按键）
bool Navigation.UseSyntheticButtons;           // 默认 true
string Navigation.ConfirmButtonName;           // 默认 "Button04"
string Navigation.ConfirmButtonNameAlt;        // 默认 "Button05"（轮换用）
bool Navigation.PressSyntheticButton(monitor, "Button04");

// 地图/课题曲
bool MapHooks.MissionTaskActive;
string MapHooks.MissionSource;
List<MapHooks.MissionChart> MapHooks.PendingMissionCharts;   // 只读消费用
MapHooks.MissionChart[] MapHooks.TakeMissionCharts();         // 取走（清空）
void MapHooks.CancelMission(string reason);
void MapHooks.ResetForCredit();
```
