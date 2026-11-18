# 续关后 `GameProcess.OnUpdate` 崩溃 —— 现场记录

> 采集自 AquaMai Crash Handler 截图（多次复现，栈稳定一致）
> 游戏版本 Sinmai 1.65.00 / AquaMai 1.9.24-g5e384e6

## 崩溃栈（完整）

```
Object reference not set to an instance of an object

ProcessManager Dump
Num  Pri  Name                        State
0    10   Process.CommonProcess       Update
1    20   Process.TrackStartProcess    Released      ← 还没释放完
2    20   Process.GameProcess          Update
3    50   Process.PleaseWaitProcess    Update
4    50   FadeProcess                  Released
5    50   FadeProcess                  Start
6    51   Process.GenericProcess       Update

System.NullReferenceException: Object reference not set to an instance of an object
  at Process.GameProcess.IsTrackSkip () [0x00023] in <b059da77c9ac42298694a8b0bbffbfc6>:0
  at Process.GameProcess.IsTrackSkipWaitEnd () [0x00000] in <b059da77c9ac42298694a8b0bbffbfc6>:0
  at (wrapper dynamic-method) Process.GameProcess.DMD<Process.GameProcess::OnUpdate>(Process.GameProcess)
  at (wrapper dynamic-method) Manager.ProcessManager.DMD<Manager.ProcessManager::Update>(Manager.ProcessManager)
```

## 关键判读

1. **崩溃点是 `GameProcess.OnUpdate`，不是 `OnStart`。**
   早期日志（`Package/Errorlog/*.log`）只显示 `DMD<GameProcess::OnStart>`，容易误判成 OnStart 的初始化问题。
   Crash Handler 的完整栈证明真正的 NRE 在 **`IsTrackSkip()` 内部 0x00023 偏移**。

2. **`IsTrackSkip()` 是方法体自己在读 null 字段**，不是参数问题。
   它读的是 `GamePlayManager` 的跳轨（track skip）数据。新 credit 开始时这份数据没有重建好。

3. **触发条件**：续关（CONTINUE → 是）之后进入下一批的**第一轨**。
   用户手动按 CONTINUE 的「是」不会崩；由插件注入按键通过续关则会崩 —— 但后续排查表明
   这更像是"续关后新 credit 的 GamePlayManager 状态未就绪"而非按键本身的问题。

4. `SetRelease()` 在同一时刻也会 NRE（日志：`[nav] SetRelease 抛异常: NullReferenceException`），
   所以崩溃一旦发生，旧 `GameProcess` 会**永久留在进程栈上**，导致
   `HasLiveGameProcess()` 恒为 true → 界面一直「等待旧局进程退出」→ 登录/刷卡自动化全卡死。

## 已经试过、但都没根治的方案

| 方案 | 结果 |
|---|---|
| 清理 `GamePlayManager` 残留（`ClearGamePlayManagerResidue`） | 无效 |
| `GameProcess.OnStart` 前置守卫（`_monitors`/`_message` 为空则跳过） | 无效（崩溃不在 OnStart） |
| `GameProcess.OnStart` 上挂 Harmony **Finalizer** 吞异常 | 能吞掉，但 `SetRelease()` 也 NRE，进程滞留 |
| 移除 `GamePlayManager.SetTrackSkipFlag(0, track)` 调用 | 无效（崩溃照旧） |
| 旧局进程滞留看门狗（超时强制回收） | 能恢复自动化，但不治崩溃 |
| 给 `IsTrackSkip()` 挂前缀钉死返回 `false` | **未完成验证**（已回滚，见下） |
| 续关按键从 Button01 改为 Button04 | ✅ 续关本身成功了，崩溃仍在 |

## 已确认有效的相关事实

- **续关屏的「是」= 4 号键**，且必须 **在 `ContinueProcess._state == 3` 时连续按住约 2.2 秒**
  才会被接受（实测 `PlayerButtonPushTime` 累计到 1879ms 后松开，0.75s 后 `ReLogin()` 触发）。
  点一下无效。
- **续关屏的「否」= 5 号键** —— 绝不能扫键，否则会误按 NO。
- `InputManager.ButtonSetting` 枚举 = `Button01..Button08` + **`Select`**（机台圆形決定按钮）。
  MODE SELECT 的「決定ボタンを押してください」页要的是 `Select`，不是 1~8 号面板键。

## 当前状态（2026-11-15 21:20）

已回滚到基线 `c3e47db`（判定修复完成、续关可用、MODE SELECT 强切仍在），
撤销了 5 个崩溃相关提交（`1137ea1`/`1b34520`/`028ff34`/`8a85584`/`1963e2c`），
崩溃在这个基线上**依然存在**。

## 下一步建议

最可能有效的是给 `Process.GameProcess.IsTrackSkip()` 挂 Harmony 前缀：

```csharp
[HarmonyPatch(typeof(Process.GameProcess), "IsTrackSkip")]
internal static class GameProcessIsTrackSkipGuard
{
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        __result = false;   // 我们完全不需要跳轨功能
        return false;       // 跳过原方法体 → NRE 从源头消失
    }
}
```

理由：`IsTrackSkip()` 只在 `IsTrackSkipWaitEnd()` 里被调用，而跳轨功能我们**完全用不到**
（所有 `SetTrackSkipFlag` 调用已移除）。钉死返回 false 后，`IsTrackSkipWaitEnd()` 恒为 false，
游戏走正常流程，NRE 不可能再发生。

上一次部署过但**未等到验证结果就回滚了**。重新启用时的验证点：

- 日志出现 `CrashFixes: GameProcess.IsTrackSkip 钉死 false (...)`（安装成功）
- 续关进入下一批第一轨时**不再弹出** AquaMai Crash Handler
- 若 Harmony 绑定失败（返回类型不是 `bool`），日志会报 patch 错误 —— 届时改用 Finalizer 形态

---

## ✅ 已修复（2026-11-15 23:30）

**根因：续关不会把 `GameManager.MusicTrackNumber` 重置回 1。**

- 续关进入下一 credit 的第一轨时，`GameProcess.OnStart` 里有：
  `GamePlayManager.GetGameScore(i, MusicTrackNumber - 1)`
  以及凭 `MusicTrackNumber` 索引的分数列表初始化。续关后 `MusicTrackNumber` 仍是**上一 credit 的 3**，
  于是索引 2 对应的分数列表不存在 → `GetGameScore` 返回 null → OnStart 自己 NRE（栈里没有内层帧，
  正是这个原因）；进程半初始化后 `ResultProcess.OnStart` 里的
  `GamePlayManager.GetAchivement` 再 NRE，整局崩溃（`Package/Errorlog/20261115230522.log`）。
- 之前所有"清 GamePlayManager 残留 / 钉死 IsTrackSkip / 前置守卫"都只是在处理症状。

**修复（`Flow/RebirthFlow.cs`）**：

```csharp
// 续关成功后、下一 credit 开始前：
Manager.GameManager.MusicTrackNumber = 1;
var ids = Manager.GameManager.SelectMusicID;
if (ids != null) for (int i = 0; i < ids.Length; i++) ids[i] = 0;
Log.Warn("[flow] 续关后重置游戏轨道计数: MusicTrackNumber=1, SelectMusicID 清零");
```

配套：

- `HasLiveGameProcess()` 只把**未 Released** 的 `GameProcess/TrackStartProcess` 算活（续关后游戏会把旧的
  留在栈上且状态为 Released）；60/90s 超时用 `Navigation.ReleaseGameProcesses` 走游戏自己的
  `ReleaseProcess` 强制回收。
- `GameProcess.OnStart` Finalizer 仍保留为兜底：只吞 NRE，日志打完整栈；失败实例标记为 broken，
  `OnUpdate` 跳过，下一帧 `ReleaseProcess` 回收（不再整局崩溃）。
- `ContinueRelogin=true`（国服只能续关，重新刷卡仅国际服可用）。

**实机验证（2026-11-15 23:25~23:37，连续 3 个 credit）**：

```
[flow] 续关成功（游戏自动继续）→ 进入下一批 stage=TrackStart
[flow] 续关后重置游戏轨道计数: MusicTrackNumber=1, SelectMusicID 清零
[crash] GameProcess.OnStart 现场: trackNo=1, musicIds=[11813,0,0,0]   ← 正常，无异常
credit 2 → DataSave#2 → 续关 → credit 3 → …
```

`OnStart 抛出 NullReferenceException` 不再出现。
