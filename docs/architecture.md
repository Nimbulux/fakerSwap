# FakerSwap 架构与协作约定

> 本文件是几个人（Lead 与各位 dev）共用的施工图。**改动前先读一遍。**

## 1. 这是个什么插件

给 Sinmai（maimai DX，SDEZ 1.65 / Unity 2018.4.7f1 / MelonLoader 0.6.4 / Mono net35 运行时）用的
MelonLoader Mod。核心功能：「账号转生」——把一份**源账号成绩快照**（Astral 抓包 JSON 或本插件备份）
里 **红谱以上**（每首歌取 EXPERT/MASTER/Re:MASTER 中的最高难度）的成绩，通过**游戏自身上传链路**
灌进当前登录的**目标账号**。

因为服务端 `UpsertUserAll` 一次最多带一个 credit 的 playlog（3 首），所以按 3 首一批循环：

```
登录目标账号 → 选歌页(读全量数据) → 一次确认(预估时间) → [ 选3首 → 结算 → 改成绩 → 上传 → 登出 → 续关登录 ] × N → 收尾登出
```

## 2. 已经踩好的坑（别重复踩）

| 坑 | 结论 |
|---|---|
| Sinmai-Assist.dll 在 Mods 里但不加载 | 它缺 `[assembly: MelonInfo]`。新插件必须有 `MelonInfo` + `MelonGame`。 |
| 编译 | `dotnet build` + net472 + `Libs/`（已从 AquaMai/Sinmai-Assist 复制好，含 MelonLoader/0Harmony/Assembly-CSharp/AMDaemon.NET/Unity 各模块）。**不要再引 Newtonsoft/YamlDotNet**，JSON 用 `Core/Json.cs`。 |
| 运行时 | MelonLoader 0.6.4，Mono，**不要用 async/await、Span、System.Text.Json、LINQ 之外的现代 API**。C# 9 语法可以，运行时 API 只能用 net35 有的。 |
| 游戏目录 | `<游戏目录>`，游戏 exe 在 `Package\Sinmai.exe`，Mods 在 `Package\Mods`。 |
| 私服 | `aime.my-aqua.net`（AquaDX 系）。上传 50s 冷却；异常退出有登录冷却。 |
| 现有 Mod | AquaMai.dll、MaiL2d.dll、Sinmai-Alpha.dll、XUnity.AutoTranslator。**不要和它们抢同一个 Harmony patch 目标导致崩溃**；能复用就复用（不要重复注册 AimeId 补丁等）。 |
| 参考代码 | `../Sinmai-Assist/`（最有用：GUI/NativeWindow、Common/DummyAimeLogin、Common/NetworkLogger、Common/InfinityTimer、Cheat/AutoPlay、Cheat/TargetPlay、Cheat/FastSkip、Cheat/ChartController、Utils/User）；`../AquaMai/`（Mods/Tweaks/TimeSaving/*、Mods/GameSystem/StartupDirectToGame、Mods/UX/AstralMenu*）；`..\..\lotsThings\_decomp\`（EntryProcess/MusicSelectProcess 反编译）。 |

## 3. 目录 / 文件所有权（避免互相覆盖）

| 目录 | 所有者 | 说明 |
|---|---|---|
| `FakerSwap/Core/`、`Main.cs`、`*.csproj`、`build.ps1`、`docs/` | **Lead** | 谁要改先跟 Lead 说 |
| `FakerSwap/Data/`、`FakerSwap/Net/` | **data-dev** | 快照/备份/成绩合成/内存写入/报文改写 |
| `FakerSwap/UI/` | **ui-dev** | 独立窗口 + 常驻占用横幅 |
| `FakerSwap/Flow/` | **flow-dev** | 登录/登出/进程跟踪/选歌/跳曲/结算/地图 |

只改自己的目录；需要别人改接口 → 发消息。

## 4. 已固定的公共契约（不要改签名，只许加）

```csharp
FakerSwap.Core.Log.Info/Warn/Error(string[, Exception])
FakerSwap.Core.Fs.Root / BackupDir / SourceDir / LogDir / ConfigPath / Safe(name)
FakerSwap.Core.ModConfig.Data  // ConfigData，字段见 Core/ModConfig.cs
FakerSwap.Core.MainThread.Post(Action)         // 窗口线程 → 游戏线程
FakerSwap.Core.Scheduler.After(sec, act, name) / Every(...) / Cancel / CancelNamed / Tick()
FakerSwap.Core.MachineLock.On(reason, detail) / Off() / Active / NoticeText
FakerSwap.Core.GameStageTracker.Set(stage, raw) / Current / SecondsInStage / Changed
FakerSwap.Core.JsonValue.Parse(string) / ToJson(bool) / this[string] / this[int] / AsInt/AsLong/AsDouble/AsString/AsBool

FakerSwap.Data.ScoreRecord / SourceSnapshot / ChartStats / TransferItem      // Data/Models.cs
FakerSwap.Data.SourceData.TryLoad(path, out snapshot, out error)
FakerSwap.Data.BackupStore.Save(snapshot) / List() / BuildFileName(name, rating, when)
FakerSwap.Data.UserDataPatcher.ReadLive(monitor) / Apply(monitor, records) / ReadRating(monitor)
FakerSwap.Data.ScoreMath.Normalize(record, stats) / RankOf(achievement) / SolveJudgeCounts(stats, achievement)
FakerSwap.Net.PayloadRewriter.Install(harmony) / Pending / Enabled
FakerSwap.Net.NetworkLog.Line(dir, api, body)

FakerSwap.Flow.RebirthFlow.Phase / StatusText / Source / Plan / IsBusy
    .Initialize() .Tick() .Shutdown(reason) .UseSourceFile(path,out err) .BeginSourceAcquire()
    .ConfirmStart() .CancelStart() .Abort() .SetPhase(phase,status) .Fail(err) .Finish()
FakerSwap.Flow.HarmonyHooks.Install(harmony)   // 各 hook 组的聚合点，新增组要在这里注册
FakerSwap.UI.UIHost.Initialize() / Tick() / OnGUI() / Shutdown()
```

## 5. 状态机（flow）

```
Idle
 └─ 用户点「选备份文件」→ SourceReady
 └─ 用户点「登录取源数据」→ SourceLoginPending → (登录成功) SourceReading → (读全量+备份) SourceReady
SourceReady ─ 用户点「开始转生」→ TargetLoginPending → (登录) TargetReading → (读全量) Planning
Planning → AwaitingConfirm（显示预估时间/条数，等二次确认）
AwaitingConfirm → Transferring
Transferring ── 每个 credit：选歌×3 → 结算 → 改成绩 → DataSave 上传 → 登出 → 续关登录
Transferring → (全部完成) Finished / (用户中止) Stopping → (收尾登出) Finished / (异常) Failed
任何阶段异常/退出：必须 MachineLock.Off() + 保证登出
```

## 6. 中止语义（用户明确要求）

- 未中止：正常走完，收藏品后续关照常。
- 界面点「中止任务」：**不续关**，打完当前 credit 剩下的 track（见下），登出，结束。
- 还没进游戏（还在选歌界面）：直接超时登出。
- 已经在游戏里：立刻跳过当前曲。
- 若当前 credit 还有剩余 track：用**性隐 BASIC**（`Config.FillerMusicId`，默认待填）填满并跳过，
  这些 filler **不修改成绩**。
- 当前 track 位置规则（1/3 = 本 credit 第 1 轨）：
  - 1/3 中止：track1 跳过(不改) + track2 性隐跳过 + track3 性隐跳过
  - 2/3 中止：track1 正常(改) + track2 跳过(不改) + track3 性隐跳过
  - 3/3 中止：track1 正常(改) + track2 正常(改) + track3 跳过(不改)

## 7. 地图 / 课题曲

- 区域选择界面出现时给 3 秒（`Config.AreaSelectTimeoutSec`），人工没选就选默认项。
- 如果当前区域有**课题曲**，把课题曲加进下一批游玩列表，以能通过的最低条件通过。
- 如果下次检测到人工走了另一个区域 → 取消课题曲任务。
- 实现建议：检测区域列表里是否存在「课题区」选项，存在就动态添加（用户明确推荐这个做法）。

## 8. 测试方式

游戏可执行：`<游戏目录>\启动.bat`
（会起 AMDaemon + inject + Sinmai）。日志：
- MelonLoader：`Package\MelonLoader\Latest.log`
- 本插件：`Package\FakerSwap\Logs\*.log`、`net-*.log`

`build.ps1` 会编译并把 `FakerSwap.dll` 拷进 `Package\Mods`。
