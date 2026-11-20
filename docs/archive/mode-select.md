# MODE SELECT：根因与正确按法（运行时 IL 实证）

> 采集：2026-11-15 22:14，Sinmai SDEZ 1.65 / AquaMai 1.9.24。  
> 手段：`FakerSwap/Core/IlDump.cs`（`ildump <Type> [Method]` 命令）。  
> **磁盘上的 `Assembly-CSharp.dll` 方法体是加密/剥离的（静态反编译全是 `Invalid MethodBodyBlock`），
> 但 Mono 内存里的方法体是可读的** —— IlDump 通过反射 `GetMethodBody()` 反汇编，能拿到带真实
> token 名称的 IL。这是这次能定位根因的关键工具。

## 结论（一句话）

**MODE SELECT 的「決定」是 4 号键（`ButtonSetting.Button04`），吃的是 `InputManager.GetButtonDown`
的真实按下边沿；屏幕上要按两次。** 旧实现按的是 `ButtonSetting.Select`（8 号键）、直写
`_isOK/_isDecidedOK`、连点 `OKButtonAnim(Button05)`、扫 1~8 号键 —— 全都走不到游戏的状态机。

## 证据链

### 1) `Process.ModeSelect.ModeSelectProcess.UpdateInput(int monitorId)` 第一行

```
IL_0000: ldarg.1
IL_0001: ldc.i4.3                                  // ButtonSetting.Button04 == 3
IL_0002: call InputManager.GetButtonDown
IL_0007: brfalse IL_0441                           // 没按 4 号键 → 直接返回
```

即：**不是 Select，是 Button04；而且是 Down（边沿），不是 Push/长按。**

### 2) 按下之后

- `_active_mode == Common(0)`：`_monitors[m].OKButtonAnim(Button04)`（入场/2P 受付分支）
- `_active_mode == Individual(1)`（本机实机走这条）：
  `if (_isCallCreditMain && !_isBlockingDecideCredit) → _monitors[m].OKButtonAnim(Button04)`
  然后 `if (_monitors[m]._isOK && _monitors[m]._state >= 33) → 扣 credit、推进状态机`

`ModeSelectProcess.OKButtonAnim` 只是把各 `ModeSelectMonitor.OKButtonAnim(button)` 的返回值或起来，
写进程自己的 `_isOK`；**真正被状态机读的是 Monitor 的 `_isOK`**（`SyncedParam` 只在 Common 模式
把进程字段回灌给 Monitor）。所以直写 `process._isOK/_isDecidedOK` 永远不会推进画面。

### 3) Monitor 状态机（`Monitor.ModeSelect.ModeSelectMonitor.ViewUpdate`）

`MonitorState` 枚举 0..58，关键节点：

| 状态 | 含义 | 何时离开 |
|---|---|---|
| 8 `SelectCardWait` | 「決定ボタンを押してください」页 | `_isOK` → 24 `CreditJudgeInit`，并把中心卡片的 `_decidedSelectModeType` 落定 |
| 24/25 `CreditJudge*` | 判定 credit，`_isOK=0`、`_timer=60` | `IsEnoughCreditNum()` → 33，否则 28（投币） |
| 33/34 `DecideCreditFadeIn*` | 淡入确认 credit 页 | 动画结束 |
| 35 `DecideCreditWait` | credit 确认页（「決定」） | `_isOK` → `_isDecidedEntry=true`、存设置 → 36/37 淡出 |
| 57 `Finish` → 58 `End` | 结束 | `IsEnd()` 为真后进程 `_state` → Released(2) |

所以**两次按 4 号键**：第一次从「決定ボタンを押してください」进入 credit 判定，第二次在 credit 确认页。
中间（状态 24→35）完全自动。

### 4) 为什么写 `PlayerButtonPush/Down` 没用

```
InputManager.GetButtonDown(m,b) = IsButtonDown(m) && PlayerButtonDown[m,b]
```

`UpdateAmInput()` 每帧从 JVS 原始状态重建 `AnyButtonDown`/`DownButtonList`/`PlayerButtonDown`（并在
读取后把 Raw 清零）。`ButtonHold` 在老实现里只在 `UpdateAmInput` 的 Postfix 里写
`PlayerButtonPush/Down`，**没有**维护 `AnyButtonDown`/`DownButtonList`，所以 `IsButtonDown(monitor)`
恒 false → `GetButtonDown` 恒 false。这就是「字段都写对了、画面就是不动」的原因。

## 现在的实现

`Flow/InputInject.cs`：Harmony **Postfix** on `Manager.InputManager.GetButtonDown`。
只对已 arm 的 `(monitor, button)` 返回一次 `true`（真实边沿语义），读一次即消费、3 帧内未消费自动过期，
不可能把按键卡住。

`Flow/Navigation.cs · TryPressOkForCredit`：
- 只在 `Phase == Transferring`（写入阶段）且 `ModeSelectProcess._state == Disp(1)` 时工作；
- 0.4s settle 后每 0.7s arm 一次 `Button04`，最多 14 次 / 15s；
- 实时打印 `monitor0` 状态名（见 `MonitorStateName`），状态一变就记录；
- 真实按键 5 次无效 → 直调 `monitor.OKButtonAnim(Button04)` 兜底（与 UpdateInput 内部同一入口）；
- `_isDecidedEntry`/`_isCanceledEntry` 一旦为真立即停手，把淡出/换页交给游戏；
- 不再强切 Fade（`ForceJumpAreaSelect=false` 默认），不再直写 `_state=Released`。

预期日志序列：

```
[nav] MODE SELECT 決定准备: process{state=Disp(1), ...} monitor0{state=SelectCardWait(8), checkOK=True, ...}
[nav] MODE SELECT 注入 Button04(決定) #1 monitor0=SelectCardWait(8), ...
[input] 注入按下 Button04 monitor=0 frame=...
[nav] MODE SELECT monitor0 状态 → CreditJudgeInit(24) ...
[nav] MODE SELECT monitor0 状态 → DecideCreditFadeIn(33) ...
[nav] MODE SELECT monitor0 状态 → DecideCreditWait(35) ...
[nav] MODE SELECT 注入 Button04(決定) #2 ...
[nav] MODE SELECT monitor0 状态 → DecidedEntryFadeIn(39) ...
→ 下一屏 RegionalSelect（区域选择）
```

## 复现 / 自检命令

```
# 任意时刻（游戏已启动）
ildump ModeSelectProcess              → Logs/ildump-HHmmss.txt：UpdateInput/OnUpdate/OKButtonAnim…
ildump Monitor.ModeSelect.ModeSelectMonitor
ildump Manager.InputManager GetButtonDown
ildump GameProcess OnStart

modediag                              → 每帧记录 MODE SELECT 进程状态
mmselect                              → 人工接管（nav 不再注入）
```

## 相关坑（已修）

- **`PressMenuButton` 曾 04/05 交替**：续关屏（是=4/否=5）上会按到 5 号键 =「否」。
  现在只按 4 号键，且在 `ContinueProcess`/`ModeSelectProcess`/`MusicSelectProcess`/設定页上直接拒绝。
- **`ButtonHold` 没有松开兜底**：调用方停止调用 300ms 后自动 `Cancel()`，不会把 4 号键带进 MODE SELECT。
