# FakerSwap 代码审计报告（只读）

- 审计对象：`FakerSwap/`（MelonLoader Mod，Sinmai / maimai DX SDEZ 1.65 / Mono / Harmony）
- 审计日期：2026-11-15 22:15–23:00
- 代码快照：`git HEAD = dbd356d`（`nav: MODE SELECT 长按改回 ButtonSetting.Select`）+ 工作区未提交改动
  （+350 行，均为**新增**：`Core/IlDump.cs`、UI 队列面板、`RebirthFlow.GetQueueSnapshot`、`CommandFile` ildump 命令）。
  行号以该快照为准；若后续有人继续提交，行号可能漂移。
- 方式：全量通读（~24k 行）+ 与同目录 `AquaMai/`、`Sinmai-Assist/` 源码逐 patch 对比 + `docs/*.md` 契约核对。
- **本次未修改任何源码、未构建、未运行游戏、未部署。** 唯一写入文件是本文件。
- 标「待实机确认」的条目表示需要 1.65 真机日志/报文才能定性，代码层面已能确认逻辑存在。

---

## 0. 结论摘要（先看这里）

整体工程质量明显高于一般 Mod：写前必备份 + 全局只读闸门 + 明确的登出语义 + 进程栈驱动状态机 + 窗口线程只读快照，
这几条主干设计是对的，事故（`docs/incident-2026-11-14.md`）之后补的防护也基本落到了正确位置
（`WriteGuard` 在 4 个写入口都调了：`UserDataPatcher.Apply/Commit/CommitBatch`、`PayloadRewriter.TryRewrite`、
`LoginHooks.ForceDataSave`、`ScoreHooks.DisableAllScoreWrites`）。

但仍有 **4 个 P0 级问题**，其中 2 个直接命中用户红线/历史事故：

1. **续关屏会按下 5 号键（=「否」）** —— `PressContinueYes` 在 YES/NO 选择屏上调用 `PressMenuButton`，
   而后者每两次就按一次 `Button05`。这与 `docs/continue-crash.md` 的「绝不能扫键」红线直接冲突，
   后果是本 credit 被提前结束、剩余 track 消失（成绩静默丢失），并且解释「续关后崩溃/续关不稳定」。
2. **`CrashFixes` 直接注入 `DataSaveProcess`**，绕过了 `WriteGuard`、写前备份、「非空 UserData」三道门 ——
   这正是 `incident-2026-11-14` 的形态，一旦 `MapResultGuard=true` 就可能再次整表覆盖。
3. **GameProcess 残留看门狗只「跳过」不「回收」** —— 前缀永久 `return false` 且不释放进程，
   flow 等 `HasLiveGameProcess()` 退出，最长 8 分钟卡死，机台锁一直不放。
4. **没有 DataSave 也把 credit 记为「已上传」并继续** —— 静默丢批，最终「Finished（N 条）」不可信。

另外 P1 里最值得立刻看的三条：`EnsurePreWriteBackup` 的备份复用不看账号、`PayloadRewriter.Pending` 全局永不清空
（跨账号残留注入）、`ButtonHold` 长按没有兜底取消（离开续关屏后 4 号键还会继续按住 ~3.3s）。

### 风险排行

| # | 级别 | 标题 | 位置 | 一句话 |
|---|---|---|---|---|
| P0-1 | **P0** | 续关屏扫键含 5 号键（NO） | `Flow/Navigation.cs:1143,1192,1199,1797` + `Flow/RebirthFlow.cs:2797,2804` | 红线违背，提前结束 credit → 丢成绩 |
| P0-2 | **P0** | 强制 DataSave 绕过全部安全门 | `Core/CrashFixes.cs:103-127`（注入点 `:117`） | 历史事故同形态，可整表覆盖 |
| P0-3 | **P0** | GameProcess 残留永不回收 → 8 分钟卡死 | `Flow/Navigation.cs:606-640`、`Flow/RebirthFlow.cs:2650,2683,2665` | 机台锁死 + 进程堆积 |
| P0-4 | **P0** | 无 DataSave 也推进批次并报完成 | `Flow/RebirthFlow.cs:2399-2412,2504` | 静默丢批，最终报告失真 |
| P1-1 | P1 | 写前备份复用窗口不区分账号/monitor | `Data/UserDataPatcher.cs:192,219-225,245-255` | 「写前必备份」保护错账号 |
| P1-2 | P1 | Payload 待改写队列全局、永不清空 | `Net/PayloadRewriter.cs:32,170,302`（`ClearPending` 无调用者） | 跨 credit/跨账号注入 |
| P1-3 | P1 | `Commit` 仅「排队报文」即算成功 | `Data/UserDataPatcher.cs:386` | 该轨成绩可能既没进内存也没上传 |
| P1-4 | P1 | playlog 不随 detail 改写 | `Net/PayloadRewriter.cs:187-239`、`Flow/ScoreHooks.cs:319-343` | 报文内部不一致（待实机确认） |
| P1-5 | P1 | 结算判定表「半改写」失败回退 | `Flow/ScoreHooks.cs:961-1000,1452-1458` | 结算页/playlog 自相矛盾 |
| P1-6 | P1 | 写回 UserData 的 monitor 不跟 flow | `Flow/ScoreHooks.cs:446-460,349-379` | 双人机台写错账号 |
| P1-7 | P1 | Finalizer 无差别吞 `GameProcess.OnStart` 异常 | `Core/CrashFixes.cs:179-207` | 掩盖其它 mod 崩溃 + 进程滞留 |
| P1-8 | P1 | 机台锁被 AquaMai TouchToButton 绕过 | `Flow/InputLock.cs:143-148` vs `AquaMai/.../TouchToButtonInput.cs:47-60` | 任务中触摸可触发按钮 |
| P1-9 | P1 | MODE SELECT 双 bool-prefix 抢同一方法 | `Flow/Navigation.cs:534-546` vs `AquaMai/.../DisableTimeout.cs:98-107` | 谁先 `return false` 谁赢 → 卡 MODE SELECT |
| P1-10 | P1 | 计划构建在主线程做 `.ma2` + 判定表重算 | `Flow/RebirthFlow.cs:1552-1567`、`Data/ChartCatalog.cs:948`（无缓存）、`Data/ScoreMath.cs:1103-1151` | 数秒～数十秒主线程冻结 |
| P1-11 | P1 | 等数据阶段每帧全量 `ReadScores` | `Flow/RebirthFlow.cs:1417-1423` | 每帧上千对象分配 |
| P1-12 | P1 | 网络日志同步写盘、无上限、关不掉 | `Flow/NetworkHooks.cs:74,101`、`Net/NetworkLog.cs:14,18-32` | 主线程 IO + 磁盘增长 |
| P1-13 | P1 | `TickTransfer` 等 ProcessManager 无超时 | `Flow/RebirthFlow.cs:1883-1887` | 永远卡在 Transferring |
| P1-14 | P1 | `CommandFile` 整文件重放 | `Core/CommandFile.cs:45-47,55-75` | 编辑一次 → 历史命令全重跑 |
| P1-15 | P1 | 重复 `Abort()` 覆盖中止槽位 | `Flow/RebirthFlow.cs:1160-1213,2296-2301` | 未改成绩的轨被记为 Done |
| P1-16 | P1 | `ButtonHold` 无兜底取消 | `Flow/ButtonHold.cs:59-86,88-127` + `Flow/RebirthFlow.cs:2797` | 离开续关屏后 4 号仍按住 ~3.3s |
| P1-17 | P1 | 角色槽兜底改写账号数据且不过闸 | `Core/CrashFixes.cs:271-306` | 合成 charaId=1×5（待实机确认） |

（P2 见第 4 节，共 18 条，多数是性能/健壮性/可维护性。）

---

## 1. P0 级发现

### P0-1 续关屏注入路径包含 5 号键（=「否」），直接违背红线

**位置**
- `FakerSwap/Flow/Navigation.cs:70` `ConfirmButtonNameAlt = "Button05"`
- `FakerSwap/Flow/Navigation.cs:1143`（等待态）`bool keyOk = PressMenuButton(monitor);`
- `FakerSwap/Flow/Navigation.cs:1192`、`:1199`（step 5/6）`ok = Touch(monitor, area, 2) || PressMenuButton(monitor);`
- `FakerSwap/Flow/Navigation.cs:1785-1807` `PressMenuButton`：`:1796-1798`
  `_menuButtonToggle = !_menuButtonToggle; string name = _menuButtonToggle ? "Button04" : "Button05";`
- 调用方：`FakerSwap/Flow/RebirthFlow.cs:2804` `Navigation.PressContinueYes(...)`（每帧），
  同函数 `:2797` 正在 `ButtonHold.Press(0, Button04, 200)` 长按 4 号
- 同类隐患：`Flow/Navigation.cs:4288`、`:4532`、`:4568`（Entry YES/NO 屏交替按 04/05）

**证据/机制**
`PressContinueYes` 只有 `state <= 1` 和 `state ∈ {2,3}`（Disp / DispEnd = YES/NO 选择屏）两条分支会走注入；
`PressMenuButton` 的「防误触」白名单只排除 MODE SELECT / 选歌页 / 設定页（`Navigation.cs:1787-1795`），
**没有排除 `ContinueProcess`**。因此每两次调用就有一次按 `Button05`，恰好落在选择屏可见的时候；
同时 `ButtonHold` 又把 `Button04` 一直按着 —— 4 号与 5 号同时为按下状态，游戏读哪个取决于时序。

**影响**
- 命中「否」→ `ContinueProcess.NextGameOverProcess()` → 本 credit 提前结束：本批剩余 1~2 轨根本不会播放，
  但它们已在 `_credit` 里，`EndCurrentTrackHooks` 不会标记 Done → 计划项永久丢失，服务器少成绩，
  flow 继续推进并把整批当成功（与 P0-4 叠加）。
- 与用户红线「续关屏『否』= 5 号键，绝不能扫键」正面冲突（`docs/continue-crash.md`），
  也解释了「续关路径不稳定、偶尔直接回 Title」。
- 现成的验证手段：`Flow/ContinueDiag.cs:72-80` 已挂了 `NextGameOverProcess` 前缀，日志出现
  `[cdiag] 「否」被接受` 即坐实。**待实机确认**（跑一次 `ContinueRelogin=true` 即可）。

**建议修法（补丁级）**
1. `PressMenuButton` 开头加硬白名单而不是黑名单：
   ```csharp
   if (NavProcess.Any<Process.ContinueProcess>()) return false;       // 红线：续关屏绝不按面板键
   if (!NavProcess.Any<Process.PhotoEditProcess>() && !IsTailScreen()) return false;
   ```
2. `PressContinueYes` 的 waiting/step5/6 分支去掉 `PressMenuButton`，只保留 `Touch(ContinueButtonTouchAreas…)`；
   若必须用按键，只允许 `Button04`（写死，不接受 05/01）。
3. `PressSyntheticButton` 增加断言：`if (InsideYesNoScreen() && name != "Button04") { Log.Error(...); return false; }`
4. `PerformEntryAnswer` / `InjectConfirmPlayResponse` 删除 `ConfirmButtonNameAlt` 轮换，只按 4 号；
   或从 `StateEntry` 读真实「是」按钮名（`SetYesNoButton` 系列已有，说明能读）。

---

### P0-2 `CrashFixes` 直接 new `DataSaveProcess` + `ReleaseProcess`，绕过全部写入闸门

**位置** `FakerSwap/Core/CrashFixes.cs:73-91`（安装）、`:103-127`（实现，注入点 `:117-118`）

**证据**
```csharp
if (!ModConfig.Data.MapResultGuard) return true;
_mapResultSkipped = true;
___container.processManager.AddProcess(new Process.DataSaveProcess(___container), 50);
___container.processManager.ReleaseProcess(__instance);
```
对照 `Flow/LoginHooks.cs:975-1050`：正常出口 `ForceDataSave` 有三道门
（`:984` `HasSafeUserData` 非游客且成绩非空、`:992` `IsSafeStageToFinish` 当前屏可收尾、
`:1001-1008` 禁止叠加在 Result/MapResult/Continue/PhotoEdit 上），并强制 `:1039-1049` 写前备份 + 修剪。
`CrashFixes` 这条路径**一道都没走**：不查 `WriteGuard`、不查 `SessionHasPlaylog`、不备份、不查阶段。

**影响**
`incident-2026-11-14` 的触发链就是「从不可安全收尾的屏强行注入 `DataSaveProcess`」，
服务端 `userData` 整表覆盖 → 档案被刷空。只要有人把 UI 上的「地图结果兜底（老账号）」打开
（`UiActions.cs:217-220` 一键可开），就会重新拥有这条无保护写入路径。
即使当前 `MapResultGuard` 默认 false，这也是**防护体系的结构性缺口**（两套写入机制，只有一套有闸门）。

**建议修法**
1. 删除 `MapResultProcessOnStartPrefix` 的 `DataSaveProcess` 注入，改为只做「跳过渲染/超时让流程自己走」
   （`MapResultProcessOnUpdatePrefix` 保留即可）；确实需要提前收尾时调用
   `LoginHooks.ForceDataSave("mapresult-guard")`，让唯一出口统一把关。
2. 把闸门抽成可复用入口，例如 `WriteGuard.AllowForceDataSave(string why, out string reason)`（内部含
   `SafeNoWrite` + `HasSafeUserData` + `IsSafeStageToFinish` + 备份），**所有**注入 `DataSaveProcess` 的地方
   （`LoginHooks`、`CrashFixes`、未来任何代码）都必须过它；用 `grep "new Process.DataSaveProcess"` 做 CI 约束。
3. `MapResultGuard` 在 `SafeNoWrite=true` 时强制失效（现在 `ForceDataSave` 有这层，`CrashFixes` 没有）。

---

### P0-3 GameProcess 残留只「跳过」不「回收」→ 最长 8 分钟卡死 + 机台锁不放

**位置**
- `FakerSwap/Flow/Navigation.cs:606-640` `GameProcessOnUpdateGuardPatch`
  （`:616-620` 任一依赖为空即 `Skip(...)`，`Skip` 只限频打日志后 `return false`，**不调用 `SetRelease`、不 ReleaseProcess**）
- `FakerSwap/Flow/RebirthFlow.cs:2702-2713` `HasLiveGameProcess()` 依赖 `NavProcess.Any<GameProcess>()`
- `FakerSwap/Flow/RebirthFlow.cs:2650-2654`、`:2683-2687` 刷卡/继关前等待它退出
- `FakerSwap/Flow/RebirthFlow.cs:2665` 唯一超时：`StepAge > 4.0 * LoginWaitTimeoutSec`（默认 480s）

**证据/机制**
`OnUpdate` 被前缀永久跳过 ⇒ 该 `GameProcess` 永远不推进到 `Released` ⇒ 它留在 `_processList` 里 ⇒
`HasLiveGameProcess()` 恒 true ⇒ `TickWaitRelogin` 每帧走「刷卡前等待上一局 GameProcess 退出…」。
`docs/continue-crash.md` 已记录过这条死路（「旧 GameProcess 会永久留在进程栈上… 全卡死」），
现在的实现只把它从「崩溃」降级成「卡死」。

**影响**
单次任务最长 8 分钟无人值守卡死，`MachineLock` 全程占用（`SyncMachineLock` 在 Transferring/Stopping 都保持 ON），
操作员只能长按 End（`Core/EmergencyExit.cs`）或关游戏；关游戏又会走到 `OnApplicationQuit` 里尝试登出（见 P2-13）。

**建议修法**
1. `Skip(...)` 分支里补一次性回收：`if (!_reclaimTried) { _reclaimTried = true; NavReflect.Call(__instance, "SetRelease"); }`，
   再下一帧仍活着则 `NavProcess.Manager.ReleaseProcess(__instance)`（带 try/catch 与日志）。
2. 计数：`Skip` 连续 > 120 帧仍未释放 → `Log.Error` + 置 `RebirthFlow` 可见告警。
3. flow 侧把「等旧局退出」变成有界：新增 `const double StaleGameProcessTimeoutSec = 60`，
   超时后 `LoginHooks.ForceTimeUp()` + 直接 `BeginCredit()`（或 `Fail` 并释放机台锁）。
4. 同源问题：`CrashFixes.cs:196-204` Finalizer 里 `SetRelease` 失败时也只有 warn，同样要接上面这套回收。

---

### P0-4 没有发生 DataSave 也把 credit 记为「已上传」

**位置** `FakerSwap/Flow/RebirthFlow.cs:2399-2412`（`TickWaitUpload` 的 Entry/Title 分支）、`:2504`（`TickWaitLogout` 推进批次）

**证据**
```csharp
if (_sawDataSave) { ... SetCStep(Step.BatchAfterCredit, "本批已上传"); return; }   // 只认 stage==DataSave 出现过
...
if (stage == GameStage.Entry || stage == GameStage.Title) {                        // 没出现过 DataSave 也放行
    SetCStep(Step.BatchAfterCredit, "已回到登录界面");
```
`_sawDataSave` 仅在 `stage == DataSave` 时置位（`:2379-2388`）。走 `ForceTimeUp`（`LoginHooks.cs:704-711`）
或游戏自己超时回 Title 时，`_sawDataSave` 永远是 false，但流程照样 `BatchAfterCredit` → `BeginLogout` →
`TickWaitLogout` 里 `!IsLoggedIn()` 立即返回 true（`LoginHooks.cs:633-639`）→ `_batchIndex++`。
批次成功与否**没有任何断言**（`LoginHooks.DataSaveCount` 有现成计数器，但没被核对）。

**影响**
- 该 credit 的 3 条成绩实际上没上传（服务端没有 `playlog`，`userMusicDetail` 也没落库），
  但 UI/`Plan.Done`/最终 `Finished` 都当成功 → 目标账号缺成绩，且没有任何日志能事后区分。
- 与 `docs/upload-protocol.md` §3「任何结束/中止/异常路径都要走超时登出」不冲突，但与
  「一批 = 一次成功 UpsertUserAll」的核心契约冲突。

**建议修法**
1. `BeginCredit` 记录 `_dataSaveCountAtCreditStart = LoginHooks.DataSaveCount`；
   只有 `LoginHooks.DataSaveCount > 该值`（或明确 `_sawDataSave` + `PendingCount == 0`）才允许 `BatchAfterCredit` 推进；
   否则 `Log.Error("[flow] credit N 未上传（无 DataSave）")` + 把该批 `TransferItem.Done` 回滚 + 重新入队/重试一次，
   仍失败则 `Fail` 并保留可恢复信息（批次号、musicId 列表）。
2. 收尾时输出对账：`plan 条数 / Done / PayloadRewriter.PatchedRows / LoginHooks.DataSaveCount`，
   并在 `Finished` 状态里区分「全部成功」与「有 N 条未确认」。
3. 顺带把 `_sawDataSave` 从「stage 出现过」改成「`DataSaveProcess.OnStart` 回执」（
   `LoginHooks.cs:221-230` 已 `DataSaveCount++`，用它更可靠）。

---

## 2. P1 级发现

### P1-1 写前备份复用窗口与账号/monitor 无关
**位置** `Data/UserDataPatcher.cs:192`（`PreWriteBackupReuseSec = 120`）、`:219-225`（复用判定只比时间与文件存在）、
`:245-255`（`saved > 0` 即整体成功）、`:230-248`（循环 0/1 两个 monitor）
**现象** 「写前必备份」的实际语义是：**120 秒内任意一次成功备份都算数**。
- 换目标账号（中止后重来 / 双人机台第二个人刷卡）时，若距上次备份 < 120s，`Apply/Commit` 拿到的是**上一个账号**的备份路径；
- 只备份成功 1 个 monitor 也返回 true，而 `Apply(monitorIndex)` 可能写的是另一个 monitor。
**影响** 备份保护失效（事故恢复清单依赖 `pre-write-*.json`），且没有日志能看出「保护的是谁」。
**建议** `EnsurePreWriteBackup(bool force, out error)` 增加 `int monitor` 与 `long userId` 参数；
缓存键改为 `(userId, monitor)`；复用前校验 `GetLiveUserData(monitor).Detail.UserID == cachedUid`；
`saved > 0` 改为「目标 monitor 已被覆盖」。`ForcePreWriteBackup` 同样带上目标 monitor。

### P1-2 `PayloadRewriter.Pending` 全局、跨批/跨账号、永不清空
**位置** `Net/PayloadRewriter.cs:32`（`static readonly Dictionary Pending`）、`:170-171`（只删命中的 key）、
`:226`（`AppendMissingRows` 默认 false → 缺行永不命中）、`:302-305`（`ClearPending` **全仓库无调用者**）；
写入者 `Data/UserDataPatcher.cs:383`、`:436`
**现象** 任何一次「排队了但没写进任何报文」的记录会永久留在 Pending；下一次任意 `UpsertUserAll`（含下一个 credit、
甚至换账号后的上传）只要 `userMusicDetailList` 里有同 key 行就会被改写。
**影响** 「上传内容与源不一致」：旧账号/旧 credit 的成绩被注入新报文；`pendingLeft` 永远不为 0，
`_commitAborted` 判定与日志失去意义；`TryRewrite` 每包都会白白做一遍全量 key 匹配。
**建议** `RebirthFlow.StartTransfer()` 与 `BeginCredit()` 各调一次 `PayloadRewriter.ClearPending()`；
`Pending` 用 `userId` 前缀或改为 `Dictionary<(uid,key),rec>`；`TryRewrite` 对本次未命中的 key 记
`Warn` + 计数（`StalePending`），超过 1 个 credit 自动丢弃。

### P1-3 `Commit` 只排队报文就返回 true，没有任何「真的写进去了」的验证
**位置** `Data/UserDataPatcher.cs:354-395`，关键 `:386` `return touched > 0 || payload;`；
配合 `Net/PayloadRewriter.cs:108-184`（`SafeNoWrite`、对象类型不符、`upsertUserAll==null`、无匹配行 都只 `return false`）
**现象** `WriteMode=Payload`（或 `Both` 但内存写失败）时，`Commit` 返回 true ⇒ `ScoreHooks._committedThisTrack=true`
⇒ 本轨不再重试；即使 `TryRewrite` 后面拒绝/没命中，成绩既没进内存也没上传，flow 仍 `Done`。
**影响** 单轨静默丢失（与 P0-4 同源的「无对账」问题）。
**建议** `Commit` 的返回值语义拆开：`(bool memoryOk, bool payloadQueued)`；`ScoreHooks` 只在
`memoryOk || payloadQueuedConfirmed` 时置 Done；在 `TickWaitUpload` 里对
`PayloadRewriter.PatchedRows` 与本 credit 期望行数做对账，不齐则 Warn/重试。

### P1-4 playlog 没有被改写，detail 与 playlog 可能不一致（待实机确认）
**位置** `Net/PayloadRewriter.cs:187-239`（`RewriteDetailList` 只操作 `all.userMusicDetailList`）；
`Flow/ScoreHooks.cs:319-343`（补写判定表发生在 `ResultProcess.OnStart` 的 **postfix**）；
`Flow/LoginHooks.cs:221-230`（DataSave 只是观测）
**现象** `UpsertUserAll` 里 `userPlaylogList`（每轨游玩记录：achievement/dx/combo/sync）完全没有被改；
C 模式（AFK 自然结算）时若游戏在 `ResultProcess.OnStart` 元方法里就已经把 playlog 落进
`UserData.Update.UpdatePlayLogList`，我们 0.25s 后的改写只影响 detail/结算页，playlog 仍是原生判定（可能 0%）。
**影响** 服务端可能按 playlog 计算最近游玩/rating 贡献（AquaDX 一般按 detail 算，故影响有限），
但「报文与源逐条一致」的验收口径只覆盖 detail，容易误判通过。
**建议** 抓一次真实报文核对（`FakerSwap/Logs/net-*.log` 的 `UpsertUserAll` → 对比 `userPlaylogList` 与
`userMusicDetailList` 的 achievement）。若不一致：在 `TryRewrite` 里按 `(musicId,level)` 同步改写
`vo.upsertUserAll.userPlaylogList`；或把补写时机提前到 `GameOverMonitor.IsPlayEnd`
/ `GameProcess.UpdateSubbMonitorData` 首次调用（判定表生成完成、playlog 尚未构建）。

### P1-5 结算判定表「半改写」失败后回退原版
**位置** `Flow/ScoreHooks.cs:961-1000`（逐 note `SetResult` + 直接写 `<Achivement>/<CriticalNum>/…BackingField`）、
`:1452-1458`（`catch` 后 `return true` = 让原版再跑）
**现象** 若 `SetResult`/字段写在中途抛异常（字段名不匹配、`NotesManager` 半可用、反射被裁剪），
已经改了一半的 `GameScoreList` 会再被原版 `SetForceAchivement` 覆盖一次；两者混出的计数与
`<Achivement>` 缓存不一定自洽。
**影响** 结算页/playlog 出现自相矛盾数据（此前实机就出现过「95.02% vs 93.14%」一类偏差），
且只有一条 `Log.Error`，flow 不知情继续上传。
**建议** 二段式：先纯计算 `JudgePlan`（cp/perfect/great/good/miss/maxCombo/DX 全部算出并校验
`sum==n`、拟合误差 ≤ 1e-4）→ 校验通过才落地写字段；落地阶段任何异常都视为本轨失败：
恢复旧值（调用前先快照字段）、`Log.Error`、返回 false 让原版跑，并在 `ScoreHooks` 暴露
`LastRewriteOk` 供 flow 决定是否重试/标记失败。

### P1-6 写回 UserData 用的 monitor 不是 flow 当前 monitor
**位置** `Flow/ScoreHooks.cs:446-460`（`PickPlayerMonitor` 取第一个 `IsEntry` 的 monitor）、
`:349-379`（`ForceApplyToCurrentScores` 固定 `for (int i = 0; i < 2; i++)`）
**现象** flow 全程跟踪 `_monitor`（`RebirthFlow.cs:1270` 等），但写回路径不看它。
**影响** 双人机台/双登录（或 monitor 0 残留 Guest2 之外的一个 Entry 账号）时，可能把成绩写进另一个账号 ——
数据污染级；单人机台不受影响。同族问题：`RebirthFlow.cs:2797`
`ButtonHold.Press(0, Button04, 200)` 与 `RebirthFlow.cs:2930` `SetButtonDown(0, button)` 都把 monitor 写死为 0，
2P 机台上会「按键无效」；`InputLock.ShouldSwallow` 里的 monitor 相关判断也依赖 nav 的 `Any<>`。
**建议** 给 `ScoreHooks` 增加 `public static int TargetMonitor = -1;`，由 `RebirthFlow.BeginCredit`
（或 `MarkTrackStarted`）写入；`PickPlayerMonitor` 优先用它并校验 `IsEntry && !IsGuest`；
`ForceApplyToCurrentScores` 只处理该 monitor；`RebirthFlow.TryPressContinueYes`/`TrySyntheticButtonPress`
里的 `ButtonHold.Press`、`SetButtonDown` 也改用同一 monitor。

### P1-7 无差别吞掉 `GameProcess.OnStart` 的所有异常（含其它 mod 的）
**位置** `Core/CrashFixes.cs:179-207`（`[HarmonyFinalizer]`）
**现象** 任何 `GameProcess.OnStart` 抛出的异常都被吞掉并 `return null`，同时尝试 `SetRelease`。
既包括我们想救的续关 NRE，也包括 AquaMai 前缀/后置里抛出的异常、以及未来任何真实 bug。
**影响** ①掩盖其它 mod 的错误（排查困难）；②`SetRelease` 失败时进程滞留（见 P0-3）；
③`OnStartFailed` 这个标记没人读，等于静默。
**建议** 只吞「可识别」的异常：`__exception is NullReferenceException` 且
（`_monitors` 为空 或 栈上有 `Released` 的 `TrackStartProcess` 或 `RebirthFlow._resumeAfterRelogin`）；
其它异常 `return __exception`（继续抛）。吞掉时置 `RecoveredGameProcess = true` 并让 flow 感知
（例如 `RebirthFlow` 检测到后把本轨标记为「未写成绩」）。

### P1-8 机台锁被 AquaMai `TouchToButtonInput` 绕过（postfix 覆盖 `__result`）
**位置** 我方 `Flow/InputLock.cs:143-148`（`Swallow` 前缀 `__result=false; return false;`）；
对方 `AquaMai/AquaMai.Mods/GameSystem/TouchToButtonInput.cs:47-60`、`:93-105`（`GetButtonDown/Push/LongPush` 的 **postfix** 改写 `__result`）
**现象** Harmony 的 postfix 在前缀跳过原方法后仍会执行；AquaMai 的后置看到 `__result==false` 就用触摸区状态把它改成 true。
**影响** 任务运行中操作员触摸面板，等价于按下 `Button01..08`（含 5 号）；`InputLock` 的「占用机台」语义失效，
并与 P0-1 的 5 号键风险叠加。**待实机确认**（取决于 AquaMai 该功能是否启用；`_isPlaying` 只在 TestMode 下为 true）。
**建议** ①`InputLock` 增补对 `GetTouchPanelAreaDown/Push/LongPush`（菜单阶段）的吞读；
②或者在 UI/日志里显式检测并提示「AquaMai TouchToButtonInput 需关闭」；
③不要只靠「吞按钮读」来表达占用，增加一个「不接受任何物理输入」的统一门（例如同时吞
`InputManager.GetButtonDown/Push/LongPush` 与 `GetTouchPanelArea*`，把合成输入走 `AllowSyntheticInput` 白名单）。

### P1-9 MODE SELECT 上两个 bool-prefix 抢同一个方法
**位置** 我方 `Flow/Navigation.cs:534-546`（`Prefix` → `return !HandleModeSelect(...)`）；
对方 `AquaMai/AquaMai.Mods/GameSystem/DisableTimeout.cs:98-107`
（`ModeSelectProcessUpdateInput` 在 `Button05` 按下时 `TimeUp()` 后 `return false`）
**现象** 同一方法上两个 prefix 都可能返回 false；Harmony 按 patch 顺序执行，先返回 false 的赢，
后者与原始实现都被跳过。
**影响** 若 AquaMai 先赢：我方的 MODE SELECT 自动化（含 `TimeSkipStart`/`OKStart` 重试）不执行 →
5s 后走 `ForceMusicSelect` 强切（`Navigation.cs:3829`）→ 跳过选区，课题曲数据算不出来（已知问题，`docs/final-verification.md` §2）。
若我方先赢：AquaMai 的 timeskip 屏蔽失效。**待实机确认**（依赖 `instanceSkip` 配置与加载顺序）。
**建议** 我方改成「不抢原方法」：`Prefix` 只在**需要注入的那几帧**返回 false（现在默认状态也可能返回 false），
并把驱动改到 `ProcessManager.Update` 的后置（已 patch，`ProcessHooks.cs:125`）；
另在安装时 `Log.Info` 打印已检测到的同目标 patch 数量（`Harmony.GetPatchInfo`）便于现场判断。

### P1-10 计划构建在主线程做 `.ma2` 解析 + 判定表枚举
**位置** `Flow/RebirthFlow.cs:1552-1567`（`TickPlanning` 里同步 `EnsureJudges`）；
`Data/PlanBuilder.cs:127-141`（对每个 item 调 `ChartCatalog.GetStats` + `ScoreMath.BuildJudgeBreakdown`）；
`Data/ChartCatalog.cs:948-975`（`ReadChartStats` **每次调用都重新读+解析 `.ma2`，无缓存**）；
`Data/ScoreMath.cs:1123-1135`（`brk<=8` 时全枚举 `C(brk+5,5)`，最多 1287 组 × `SolveBaseCounts` 400 次迭代）
**现象/影响** 100 条计划 = 100 次文件 IO + XML/ma2 解析 + 数百次求解迭代，实测可能在主线程冻结数秒到数十秒；
期间游戏不 Update，机台表现为卡住，且 `RebirthFlow` 的 Phase 计时仍按 realtime 走（可能触发别处超时）。
**建议** ①`ChartCatalog` 增加 `Dictionary<int, ChartStats>` 缓存（`ResetCache` 时清）；
②`EnsureJudges` 改成 `Scheduler.Every(0.05, 每次处理 2 条)` 的顺序任务，并在 `StatusText` 显示进度；
③A 模式（不需要判定表）跳过；④`BreakCandidates` 对 `brk` 上限做更紧凑的族，或先粗解再局部微调。

### P1-11 等数据阶段每帧全量 `ReadScores`
**位置** `Flow/RebirthFlow.cs:1417-1423`（`WaitMusicSelect` 分支每帧 `UserDataPatcher.ReadScores`）
**现象** `ReadScores` 遍历 `ScoreDic[0..4]`（上千条）并 `new ScoreRecord`，只为取 `Count`。
**影响** 「等待账号数据」期间每帧上千对象分配 + 每次 5 条日志里的 `scoreCount` 变化，属于典型 GC 抖动源。
**建议** 用轻量计数（`ud.ScoreDic[i] != null ? ud.ScoreDic[i].Count : 0` 求和）替代完整快照，
把重读留给 `Step.ReadLive`；节流到 0.5s。

### P1-12 网络日志：主线程同步写盘 + 无上限 + 不可关
**位置** `Flow/NetworkHooks.cs:74`（`NetworkLog.Line("REQ", api, SafeRequest(query))`）、`:101`（RES）；
`Net/NetworkLog.cs:14`（`Enabled = true` 硬编码）、`:18-32`（`File.AppendAllText` 同步，单文件无轮转）
**现象** 每个请求都 `query.GetRequest()` 序列化一次（UpsertUserAll/GetUserData 体积最大），
响应再 `VOSerializer.Serialize()` 一次，然后同步落盘（截断上限 200k 字符）。
`net-*.log` 按天累积且**永不清理**（对比 pre-write 备份有 `PrunePreWrite(10)`）。
**影响** 登录/上传路径上的主线程 IO 尖峰（每 credit 两次登录 + 一次上传），长期运行占磁盘。
**建议** ①`Enabled` 绑定 `ModConfig.Data.Verbose`（或新增 `NetLog` 开关）；
②按 API 白名单记录（`UserLogin / UserLogout / UpsertUserAll / GetUserData`），其余只记 api 名+长度；
③异步落盘（`MainThread` 队列 + 独立写线程）或至少合并写（缓存到 StringBuilder，每 200ms flush）；
④保留最近 N 天/最大 M MB。

### P1-13 `TickTransfer` 等 ProcessManager 没有超时，也没有把失败上报 UI
**位置** `Flow/RebirthFlow.cs:1883-1887`
**现象** `if (!ProcessHooks.Ready) { StatusText = "等待游戏进程…"; return; }`
`ProcessHooks.Ready` 依赖 `Container`（`ProcessDataContainer..ctor` postfix）与 `Manager`；
若 Harmony 没装上/构造签名变化，Phase 永远停在 `Transferring`，机台锁一直 ON。
**影响** 无人值守卡死（比 P0-3 更早、更常见）。
**建议** 加 `if (UnityEngine.Time.realtimeSinceStartup - _phaseAt > 60) Fail("ProcessManager 未就绪")`；
并在 `ProcessHooks` 暴露 `FailureReason`，`SyncMachineLock` 在卡住 > 60s 时自动 `MachineLock.Off()` 并保留 Phase 供排查。

### P1-14 `CommandFile` 是「整文件重放」，编辑一次就重跑历史命令
**位置** `Core/CommandFile.cs:45-47`（`if (text == _lastSeen) return; _lastSeen = text;`）、`:55-75`（对全文每行 `Dispatch`）
**现象** 操作员用编辑器覆盖写入 `command.txt`（例如只剩 `abort`），以前写过的 `start`/`confirm`/`source-acquire`
会全部再执行一次；`unlock`（`:110-112`）还会在任务中直接 `MachineLock.Off()`，把 P0-1/P1-8 的输入风险放大。
**影响** 自动化脚本化操作时可能意外重启流程/重复中止/任务中解锁机台。
**建议** 记录已执行的行数或文件偏移（`_executedLineCount`/`_lastLength`），只执行新增行；
或要求每条命令带单调 `#id N`，小于等于已执行 id 的跳过。`unlock` 在 `RebirthFlow.IsBusy` 时拒绝并提示用 End 键。

### P1-15 重复 `Abort()` 会把中止槽位往后推，导致未改成绩的轨被记为 Done
**位置** `Flow/RebirthFlow.cs:1139-1219`（`Abort` 无幂等保护，`:1171 _abortSlot = _slot;`）、
`:2296-2301`（`EndCurrentTrackHooks` 用 `_slot >= _abortSlot` 判断「不改成绩」）
**现象** 第二次调用 `Abort()` 时 Phase 已是 `Stopping` 但仍 `IsBusy`，`_abortSlot` 被改成本轨（更大），
于是原本 `_slot < 新_abortSlot` 的那一轨在 `EndCurrentTrackHooks` 里被 `s.Item.Done = true`，
可它其实是被「跳过、不改成绩」的。
**影响** 计划计数虚高 → 最终报告「N 条完成」而服务器少 N 条（与 P0-4 叠加时更难发现）。
**建议** `if (_stopping || _abortSlot >= 0) return;` 放在 `Abort()` 开头（只允许第一次设置中止槽位）；
`EndCurrentTrackHooks` 的 Done 判定改用 `Item.SkipReason`/`Kind` 而不是槽位比较。

### P1-16 `ButtonHold` 没有兜底取消，离开续关屏后 4 号键还会「按住」约 3.3 秒
**位置** `Flow/ButtonHold.cs:59-69`（`Press` 每次重置 `FramesDone=0`）、`:88-127`（postfix 每帧写
`PlayerButtonPush=true`、`PlayerButtonPushTime = 累计`、`GameButtonPush=true`）、`:71-86`（`Cancel`）；
唯一调用处 `Flow/RebirthFlow.cs:2797` 每帧 `Press(0, Button04, 200)`，`Navigation.cs:3984` 是 `Select,4`
**现象** 续关屏期间 `Press` 每帧重置 → `FramesDone` 永远到不了 200 → 不会自动 `Cancel`；
离开续关屏后 `Press` 停止，`Active` 仍为 true，postfix 继续把 4 号写成按下，直到 200 帧后才 `Cancel`。
**影响** 紧接着的 MODE SELECT / PlInformation / 选歌页会看到「4 号键被按住 / 长按中」，
可能误触 `決定`/时间跳过（`TimeSkipButtonAnim(Button05)` 的触发条件里就有长按判定），
也会与 `InputLock` 的合成输入窗口互相干扰。
**建议** ①`ButtonHold` 增加时间兜底：`Press` 时记录 `Environment.TickCount`，
postfix 里 `if (elapsed > MaxHoldMs) { Cancel(); return; }`（例如 3500ms）；
②`RebirthFlow.TryPressContinueYes` 在 `NavProcess.Find<ContinueProcess>() == null` 时显式 `ButtonHold.Cancel()`；
③`BeginCredit`/`StartShutdown` 也各补一次 `Cancel()`。

### P1-17 角色槽兜底会改写账号数据，且不过 `WriteGuard`（待实机确认）
**位置** `Core/CrashFixes.cs:271-306`（`AutoComposeCharacterSlotPrefix` 生成 5 个 `new UserChara{ID=1}` 返回）
**现象** 该方法的返回值被游戏用于人物选择/展示；若它随后写回 `UserData.Detail`/角色槽并进入上传，
就等于在只读模式下改写了账号（无备份、无闸门）。
**影响** `SafeNoWrite=true` 时仍可能改账号；空账号会被人为塞入 5 个 charaId=1。
**建议** 先实机核对：只读模式下构造「无角色账号」登录 → 抓 `UpsertUserAll.userCharaList/isNewUserCharaList` 是否变化。
若确实上传：把该前缀也接到 `WriteGuard`（只读时返回 false + 让游戏走自己的空守卫？注意不能崩，
故建议只读时注入 0 个槽位并跳过写入，或直接 `Fail` 并提示「该账号需先人工进入一次游戏」）。

---

## 3. 与 AquaMai / Sinmai-Assist 的 Harmony 冲突矩阵

> 前提：`docs/architecture.md` §2 记录 **`Sinmai-Assist.dll` 因缺 `[assembly: MelonInfo]` 不会被加载**，
> 所以真正生效的第三方是 **AquaMai**。Sinmai-Assist 里同名目标仅作为「若将来修好加载」的隐患列出。

| 方法 | 我方 | AquaMai | Sinmai-Assist | 冲突性质 |
|---|---|---|---|---|
| `InputManager.GetButtonDown/Push/LongPush` | `InputLock` **prefix 跳过** | `TouchToButtonInput` **postfix 改 `__result`** | 同目标 | **P1-8 锁被绕过** |
| `InputManager.GetMonitorButtonDown(ref,int)` | `InputLock` prefix | `TouchToButtonInput` postfix | — | 同上 |
| `ModeSelect.ModeSelectProcess.UpdateInput` | `Navigation` **prefix 可 false** | `DisableTimeout` **prefix 可 false** | 同目标 | **P1-9 谁先谁赢** |
| `GameProcess.OnStart` | `CrashFixes` **Finalizer 吞异常** + probe | 多处 patch | — | **P1-7 吞别人异常** |
| `GameProcess.OnUpdate` | `Navigation` **prefix 可 false**（不释放） | 多处 patch | 同目标 | **P0-3** + 屏蔽对方 |
| `GameProcess.SetRelease` | 反射调用 | patch | — | 我方调用会触发对方逻辑（可接受） |
| `MapResultMonitor.Initialize` | `CrashFixes` prefix 可跳过 | patch | — | 双方都可能跳过 → 屏幕空白/时间到（低风险） |
| `PlInformationMonitor.IsPlayPlInfoEnd` | postfix 置 true | postfix 置 true | postfix | 都置 true，顺序无关（低风险） |
| `ResultProcess.OnStart` | `ScoreHooks` postfix 写 UserData | patch | patch | 顺序影响：若对方在我方之前改动判定，我方读到的是被改过的状态 |
| `GameScoreList.SetForceAchivement` | **prefix 重写并跳过原版** | — | **同目标** | 若 Sinmai-Assist 未来加载 → 互斥 |
| `GameScoreList.SetResult` | 我方逐 note 调用 | patch | — | 我方 N 次调用会触发对方 N 次（性能/副作用） |
| `GameManager.AutoJudge` / `set_AutoPlay` | 未 patch | patch | patch | **AquaMai AutoPlay 开启时会把判定覆盖回去** → 必须在 UI/文档里要求关闭 |
| `Packet.ProcImpl` / `set_State` | `PayloadRewriter`+`NetworkHooks` | patch | **同目标** | 改动出站报文，多个 mod 同时改 → 报文不确定（当前只有 AquaMai 日志类） |
| `VOExtensions.ExportUserAll` | 未 patch（读 `ScoreDic`） | **prefix/finalizer 改动导出列表** | — | **上传内容可能被第三方改写**（需实机核对报文） |
| `UserData.Initialize/UpdateScore` | 未 patch | patch | — | AquaMai 会在 UserData 生命周期插手，影响我方 `Apply` 的时机假设 |
| `ProcessDataContainer..ctor` / `ProcessBase..ctor` | postfix 捕获 | patch | — | 叠加 postfix，安全 |
| `ProcessManager.Update/AddProcess/ReleaseProcess/ForceTimeUp` | postfix 观测 | `SetMessageManager` 等 | — | 安全（若对方 prefix 跳过 Update，则我方 Update postfix 也不执行 → 证据/诊断缺失） |
| `Application.Quit` / `GameMain.OnApplicationQuit` | 我方 `OnApplicationQuit` 里尝试登出 | patch | — | 退出路径可能被打断（见 P2-13） |
| `TimerController.PrepareTimer` / `UpdateTimer` | 只读/写 `_countDownSecond` | patch | patch | 注销/倒计时归零行为可能被第三方改写（低风险） |
| `EntryProcess.OnUpdate` | postfix 驱动 Entry 自动化 | patch（`AutoEntry` 类） | — | **双方都会按「是」** → 建号/受付页行为叠加（需确认 AquaMai 的 Entry 自动化关闭） |

**结论**：真正危险的是**双方都返回 false 的 prefix**（`ModeSelect.UpdateInput`、`GameProcess.OnUpdate`）
和**我方 prefix + 对方 postfix 改写 `__result`**（`InputManager.Get*`）。这两类在任何一次 AquaMai 升级后都可能变。
建议在 `OnInitializeMelon` 里用 `Harmony.GetPatchInfo(method)` 打印同目标 patch 数量与 owner，
并在 UI 启动横幅里提示「检测到 AquaMai 输入/Entry 自动化，请关闭 XX」。

---

## 4. P2 级发现（健壮性 / 性能 / 可维护性）

| # | 位置 | 问题 | 建议 |
|---|---|---|---|
| P2-1 | `Core/Log.cs:34-43` | 每条日志 `File.AppendAllText`（open/close）在主线程，且 `MelonLogger` 也同步 | 改为批量缓冲 + 每 200ms flush；`Verbose=false` 时降级细节日志 |
| P2-2 | `UI/UIHost.cs:119-125`、`Data/BackupStore.cs:162-177` | 每 15s 在主线程 `Directory.GetFiles` + 解析 Backup/Source 下**所有** `*.json`（10 份 pre-write × ~100 条） | 只读文件名/时间做列表，选中时再解析；或异步加载 |
| P2-3 | `Data/ChartCatalog.cs:934-936` | `ResolveChartPath` 找不到对应难度文件时回退到目录里第一个 `*.ma2`（可能是别的难度） | 删除该回退，返回 null 走「粗模型」分支并 Warn |
| P2-4 | `Data/ChartCatalog.cs:948-975` | `ReadChartStats` 无缓存（见 P1-10） | 加缓存字典 |
| P2-5 | `Flow/GameStagePump.cs:157`、`Core/GameStage.cs:41` | 每帧对每个进程 `State.ToString()` 分配字符串；`SecondsInStage` 无锁读 `double`（x86 可能撕裂） | 缓存枚举名/用 `(int)State`；`_enteredAt` 用 `Interlocked`/加锁读 |
| P2-6 | `Core/ModConfig.cs:162-163,174,202-204` | 只 clamp 了 `BatchSize`；`UploadCooldownSec<50`、`TrackGapSec<15`、`MinLevel>MaxLevel`、`WriteMode/PlayMode/PlayRealism` 拼错都静默生效 | `Load()` 末尾集中校验 + 修正 + 一条 `Log.Warn`；枚举型字段白名单 |
| P2-7 | `Core/ModConfig.cs:222,265` | `AimeCode` 明文存 `config.json`，`Save()` 每次都写回 | 至少支持 `AimeCodeFile`/环境变量；文档标注该文件敏感 |
| P2-8 | `Flow/LoginHooks.cs:305-309,280,1332,1414`；`Core/CommandFile.cs:116` | 访问码脱敏只遮中间，日志留下**前 4 + 后 4 位**（8/20 位）；`uid` 明文 | 只记长度/`***`；提供「日志不含凭据」开关并默认开 |
| P2-9 | `Flow/LoginHooks.cs:456-458` | 原文件不存在时，刷完后**保留**我们写入的访问码（注释说明是有意） | 明确提示并在收尾时删除（或 `aime.txt.bak` 策略），避免机台继续用测试码自动登录 |
| P2-10 | `Data/BackupStore.cs:20-24` vs `:73-94` | 源备份文件名 = `用户名-时间-R.json`；若用户名以 `pre-write-` 开头，会被 pre-write 清理误删 | 源备份加 `source-` 前缀或独立目录 |
| P2-11 | `Data/UserDataPatcher.cs:101-119` | `ReadLive` 用 `ExportScoreDetailList`，只在结果为 0 时才回退 `ScoreDic`；部分失败无法察觉 | 交叉校验 `details.Length` 与 `ScoreDic` 计数，不一致则 Warn 并以多者为准 |
| P2-12 | `Core/Json.cs:108-120` | 递归下降解析无深度上限；恶意/损坏备份可 `StackOverflowException`（.NET 无法 catch，进程直接退出） | 加 `depth > 64` 抛 `FormatException` |
| P2-13 | `Flow/RebirthFlow.cs:251-258` | `OnApplicationQuit` 里 `BeginLogout` + `ForceDataSave`（有闸门），但退出流程本身可能被 AquaMai 的 `Application.Quit` patch 打断 | 退出时只做 `MachineLock.Off()` + 直接 `UserLogout` 报文（`TryDirectLogout`），不注入进程 |
| P2-14 | `Data/ScoreMath.cs` 与 `Flow/ScoreHooks.cs:474-1058` | 两套判定表生成实现（求解器 vs 结算页重写），逻辑重复且已出现过分歧 | 让 `ScoreHooks` 只消费 `JudgeBreakdown`（data 层唯一真源），删掉自带的 miss 分配/残差微调 |
| P2-15 | `Core/CrashFixes.cs:135-168` | `GameProcessOnStartGuardPatch` 从未被 `PatchAll`（死代码，带 5 个 Traverse 字段）；`:44-45` 日志先宣告成功再 patch | 删除死代码；日志改到 patch 成功之后 |
| P2-16 | `Core/CrashFixes.cs:24-97` + `Flow/HarmonyHooks.cs:27-38` | CrashFixes 整段在一个 try 里：`GameProcessIsTrackSkipGuard`/`MapResultMonitor.Initialize` 任一处签名不符 → 其余（MapResult 守卫、ContinueDiag）全部不装，且只有一条日志 | 复用 `HarmonyHooks.Step` 模式，逐组 try/catch；每组打印 `installed/missing` |
| P2-17 | `Flow/MapHooks.cs:226-239,623-702` | 选歌页每 20 帧对 3 个集合做全量反射扫描（千级条目） | 提到 1s 一次；只在存在课题分类/进入分类页时扫描 |
| P2-18 | `Core/CommandFile.cs:31-53` | 每 0.75s `File.Exists`+`ReadAllText`（主线程） | 计数不变则跳过；`FileSystemWatcher` 或 2s 轮询 |

---

## 5. 建议修复顺序（可执行路线）

**第一批（今晚，改动小、收益最大）**
1. P0-1：`PressMenuButton` 加 `ContinueProcess` 硬拒绝 + 删掉 `Button05` 轮换（`Navigation.cs`，约 10 行）。
2. P1-16：`ButtonHold` 加 3.5s 时间兜底 + `TryPressContinueYes` 里显式 `Cancel()`（约 15 行）。
3. P0-4：`BeginCredit` 记录 `DataSaveCount`，`BatchAfterCredit` 前核对（约 20 行）。
4. P1-2：`StartTransfer`/`BeginCredit` 调 `ClearPending()`，未命中 key 记 Warn（约 10 行）。
5. P1-15：`Abort()` 幂等保护（1 行）。

**第二批（明天，需要真机验证）**
6. P0-3：`GameProcess.OnUpdate` 守卫补回收 + flow 侧 60s 有界等待。
7. P0-2：删除 `CrashFixes` 的 `DataSaveProcess` 注入，统一走 `LoginHooks.ForceDataSave`。
8. P1-1：`EnsurePreWriteBackup` 绑定 `(uid, monitor)`。
9. P1-7：Finalizer 只吞可识别 NRE。
10. P1-8/P1-9：AquaMai 互斥检测 + 日志/UI 提示；`InputLock` 补吞触摸读。
11. P1-4：抓报文确认 playlog，不一致则在 `TryRewrite` 里同步改写。

**第三批（收尾/优化）**
12. P1-3、P1-5、P1-6、P1-10、P1-11、P1-12、P1-13、P1-14、P1-17。
13. 全部 P2（其中 P2-5/P2-12/P2-16 建议一并做，成本低）。

---

## 6. 实机回归验证清单（针对本报告）

| 验证项 | 方法 | 期望 |
|---|---|---|
| 5 号键不再被注入 | `cdiag` 开，`ContinueRelogin=true` 跑 2 个 credit，`grep "「否」被接受"` | 0 次 |
| 续关后不再卡死 | 跑 `ContinueRelogin=true`，关注 `[nav] GameProcess.OnUpdate 防御跳过` + 是否出现 `等待旧局 GameProcess 退出` | 出现后 ≤ 60s 恢复或明确失败 |
| 每批都有 DataSave | `grep "LoginHooks: DataSave started (#"` 与 `[flow] credit N upload started` 逐批对齐 | 每批恰好一条 |
| 备份归属 | 换账号后立刻写一轨，检查新生成的 `pre-write-*.json` 里的 `userName/userId` | 必须是当前目标账号 |
| 报文一致（detail + playlog） | 抓 `UpsertUserAll`，对比 `userMusicDetailList` 与 `userPlaylogList` 的 achievement | 两者一致（若不一致 → P1-4 成立） |
| 只读模式无任何写 | `SafeNoWrite=true` 跑源获取 + 中止，检查 `UpsertUserAll` 是否出现 | 不应出现（或仅空 playlog 被服务端丢弃） |
| 无角色账号（P1-17） | 只读模式登录空账号，抓报文 `userCharaList` | 不应变化 |

---

## 7. 未发现问题的地方（供 Lead 参考，避免重复排查）

- `Core/WriteGuard.cs` 的闸门语义正确，且 `SafeNoWrite=true` 时 `StartTransfer` 直接拒绝（`RebirthFlow.cs:1075`）。
- 窗口线程边界干净：UI 只通过 `MainThread.Post` 调 flow，只读 `WindowState.Current` 的不可变快照
  （`NativeWindowSnapshot.cs:210-220` 用 `volatile` 发布）；窗口线程无 Unity API 调用。
- 全程没有 `Process.Kill()`；结束一律走游戏自身登出/超时（`LoginHooks.cs` 注释与实现一致）。
- `MachineLock` 生命周期与 Phase 同步（`SyncMachineLock`），`CompleteShutdown` 必定 `Off()`。
- `Scheduler` 的「遍历拷贝 + 结束后清理」写法不会在回调里改集合导致 `InvalidOperationException`。
- `Fs.Safe`/`BackupStore` 的写前校验（`SavePreWrite` 验证文件存在且非空）是正确的「备份失败即拒绝写」实现。
- `SourceData` 的空文件/坏 JSON 处理不会抛到主循环；`PlanBuilder` 对难度窗口/缺失谱面有过滤。
