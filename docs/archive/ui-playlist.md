# 控制台「待办曲目」播放列表面板

任务页（`NativeModContent.DrawTask`）里的可展开播放列表：默认只占一行摘要，点一下展开
「每一批（credit）× 每一首」的清单，实时显示每批进度、当前批、已完成批与课题曲（★）。

- 只读展示：不发送任何 `UiActions` 指令，不修改 Flow / 计划 / 账号数据。
- 数据全部来自不可变快照 `WindowSnapshot`；窗口线程不读 Flow、不碰 Unity。
- 展开状态是 `NativeModContent` 的 `static bool _queueExpanded`，默认 `false`（折叠），
  窗口关闭/重启时在 `Release()` 里复位。

## 数据链

```
Unity 主线程（UIHost.Tick → Publish → BuildSnapshot，每 0.25s 一次）
  RebirthFlow.GetQueueSnapshot()                 // 读 _batches 的可变 List，仅主线程
        │  QueueSnapshot / QueueBatchView / QueueSlotView  （Flow 侧对象，窗口线程不可见）
        ▼
  UIHost.ReadQueue(snap)                         // 逐字段深拷贝
        │  QueueEntry[] / QueueBatch[]             （UI 侧普通 POCO）
        ▼
  WindowState.Current = snap                     // volatile 引用发布，构建后不再修改
        ▼
窗口线程（NativeModWindow → NativeModContent.Render → DrawTask → DrawQueuePanel）
```

`GetQueueSnapshot()` 用 `try/catch` 包住，失败只在 Unity 日志里留一条 `flow: GetQueueSnapshot failed`；
`UIHost.ReadQueue` 同样有 `try/catch`，失败时只是本帧队列为空，不影响任务页其它控件。

## 快照契约（`FakerSwap/UI/NativeWindow/NativeWindowSnapshot.cs`）

```csharp
internal sealed class QueueEntry            // 一首待办曲目
{
    int Index;          // 批内 0-based 槽位
    int MusicId;
    int Level;
    int Kind;           // 0 = transfer, 1 = filler, 2 = mission（課題曲）
    bool IsMission;     // Kind == 2
    bool IsDone;
    string Label;       // "11813-3" / 课题曲带 ★："★11976-2"
}

internal sealed class QueueBatch            // 一批（一个 credit）
{
    int Index;                  // 0-based 批次号
    QueueEntry[] Slots;         // 该批全部曲目（通常 1~3 首）
    int Count;                  // Slots.Length
    bool IsMission;             // 该批含课题曲
    bool IsCurrent;             // 正在打的一批
    bool IsDone;                // 已完成（或在当前批之前）
    int CurrentSlot;            // 当前批正在打的槽位，其余 -1
    int DoneCount;              // 该批已完成的槽位数
}

// WindowSnapshot 新增字段（默认空，保证旧快照结构仍可渲染）
QueueBatch[] Queue;             // 默认 EmptyQueue
int QueueCurrentIndex = -1;     // 当前 credit 下标
int QueueCurrentSlot = -1;      // 当前 credit 内槽位
int QueueTotalSlots;            // 全部曲目数
int QueueDoneSlots;             // 已完成曲目数
int QueueMissionSlots;          // ★课题曲总数
int QueueMissionBatches;        // 含课题曲的批次数（数据保留，当前摘要未展示）
bool QueueRunning;              // Transferring / Stopping
```

约定：快照对象一旦发布就**只读**。`ReadQueue` 每次 `BuildSnapshot` 重新分配数组并逐字段拷贝，
窗口线程永远不会拿到 `RebirthFlow._batches`、`QueueBatchView` 或任何 Flow 可变对象。
`RebirthFlow.GetQueueSnapshot` 的 `IsDone` / `CurrentSlot` 语义原样保留，UI 不二次推断。

## 渲染规格（逻辑像素，实际按 DPI `c.S()` 缩放）

`DrawQueuePanel(c, s, x, y, width)` 插在任务页「当前曲目」卡片与「游戏阶段」行之间，
宽度与其它卡片一致（`c.Width - pad*2 - c.S(10)`）。

### 折叠（默认）

```
┌───────────────────────────────────────────────────────────────────────┐
│ 待办曲目   33 批 / 99 首（★课题 3） · 进行中 33/33                  ▸ │
└───────────────────────────────────────────────────────────────────────┘
```

| 元素 | 位置 / 颜色 |
|---|---|
| 标题「待办曲目」 | `x+8`，宽 `S(90)`，`Theme.Accent`，加粗，行高 `S(30)` 垂直居中 |
| 摘要「N 批 / M 首」 | `x+S(100)` 起，`Theme.Text`，小字号 |
| 「（★课题 K）」 | 紧接着摘要实测宽度之后；`K>0` 用 `Theme.Warn`，否则 `Theme.Muted` |
| 状态后缀 | 运行中「· 进行中 C/N」`Theme.Accent`；完成「· 已完成 D/M」；有进度「· 已玩 D/M」；右侧至少留 `S(60)` 才画，否则省略 |
| 展开箭头 | 右端 `x+width-S(24)`，折叠 `▸` / 展开 `▾`，`Theme.Muted`（悬停 `Theme.Text`），加粗 |
| 背景 / 边框 | 悬停 `Theme.Panel`，否则 `Theme.PanelAlt`；边框悬停或展开时 `Theme.AccentDark`，否则 `Theme.Border` |

整行（`x, y, width, S(30)`）都是命中区，`RowClick` 处理，行高与既有「区域（爬图）」「设置」
面板标题行一致。

### 展开

摘要行下方 4px 起，每批一行，行高 `S(24)`；行尾追加一行图例，最后返回 `y + S(22)`。

```
│ 待办曲目   33 批 / 99 首（★课题 3） · 进行中 3/33                  ▾ │
│ ✓ 1/33 [3/3] 11234-3 11813-3 12015-2                                │
│ ✓ 2/33 [3/3] 12015-4 10201-3 ★11976-2                               │
│ ▶ 3/33 [2/3] 11813-3 12015-4 ★11976-2                               │   ← 当前批：Theme.Selected + AccentDark 边框
│ · 4/33 [0/3] 11901-4 11263-3 11003-2                                │
```

| 列 | 内容 | 位置 / 宽度 | 颜色 |
|---|---|---|---|
| 状态标记 | `✓` 已完成 / `▶` 当前 / `·` 未开始 | `x+8`，宽 `S(14)` | `Theme.Good` / `Theme.Accent` / `Theme.Muted`；当前/完成加粗 |
| 批次号 | `(Index+1)/批总数`，如 `3/33` | `x+S(24)`，宽 `S(48)` | 完成批 `Theme.Good`，其它 `Theme.Text` |
| 批内进度 | `[DoneCount/Count]`，如 `[2/3]` | `x+S(76)`，宽 `S(46)` | 满进度 `Theme.Good`，否则 `Theme.Muted` |
| 曲目 | `musicId-level`，课题曲 `★musicId-level` | 从 `x+S(128)` 起逐个实测宽度 + `S(10)` 间距 | 课题曲 `Theme.Warn`（始终）；已完成批或本批已完成槽位 `Theme.Muted`；其余 `Theme.Text` |
| 行背景 | — | 当前批 `Theme.Selected`，悬停 `Theme.Panel`，其余奇偶 `Theme.PanelAlt` / `Theme.Background` | — |

曲目列最多画到 `x+width-S(8)`；万一槽位异常多（正常 `BatchSize≤3`，课题批最多 3 首），
超出部分画 `…` 截断，不越界、不覆盖其它控件。

### 高度与滚动

- 折叠：`S(30)+S(4)`；展开：`S(30)+S(4) + N*S(24) + S(22)`。
- 面板画在 `BeginContent/EndContent` 内，滚动/裁剪沿用窗口既有逻辑：
  `Fill/Text/RowClick` 内部都经过 `ScreenY()` 与内容区裁剪，滚轮改的是 `Canvas.ScrollY`。
- 队列很长（如 60+ 批）时页面整体变长，由既有滚动条滚动；不会把页面顶出可视区。
- 空队列（还没生成计划）：只画一行提示「尚未生成队列（进入转生阶段后显示每一批每一首）」，
  不显示箭头、不响应点击。

## 交互

1. 打开任务页（`FlowPhase.TargetLoginPending / TargetReading / Planning / Transferring / Stopping`
   任一阶段的 `DrawTask`）：默认折叠，一行摘要。
2. 鼠标移到摘要行：背景变亮、边框变蓝、箭头变亮（`HoverAt`）。
3. 在摘要行任意位置按下鼠标左键：`RowClick` 命中 → `_queueExpanded = !_queueExpanded`，
   同帧重绘展开/收起。点击被 `HitTest` 消费（`HasClick=false`），不会穿透到下方按钮。
4. 展开后逐批显示；滚动滚轮查看长队列；拖高窗口最小 900×700（`NativeModWindow.MinWidth/MinHeight`）。
5. 再次点摘要行收起；展开状态在窗口整个生命周期内保持（切页面、重绘都不丢），
   `NativeModWindow.Shutdown → NativeModContent.Release` 时复位为折叠。

## 改动清单

| 文件 | 关键改动 |
|---|---|
| `FakerSwap/UI/NativeWindow/NativeWindowSnapshot.cs` | 新增 `QueueEntry` / `QueueBatch`；`WindowSnapshot` 新增 `Queue` + `QueueCurrentIndex/QueueCurrentSlot/QueueTotalSlots/QueueDoneSlots/QueueMissionSlots/QueueMissionBatches/QueueRunning` |
| `FakerSwap/UI/UIHost.cs` | `BuildSnapshot` 末尾调用 `ReadQueue(snap)`；新增 `ReadQueue`，把 `RebirthFlow.GetQueueSnapshot()` 深拷贝成 UI 侧 POCO（Unity 主线程） |
| `FakerSwap/UI/NativeWindow/NativeModContent.cs` | 新增 `static bool _queueExpanded`（`Release` 复位）；`DrawTask` 插入 `DrawQueuePanel`；新增 `DrawQueuePanel`（摘要行 + 展开逐批列表） |
| `docs/ui-playlist.md` | 本文档 |

没有修改 `FakerSwap/Flow/**`、`FakerSwap/Data/**`、`FakerSwap/Core/**`、`Output/`，
也没有部署到游戏目录。`NativeModContent.Render` 的分发逻辑、其它页面（Home / Confirm / 设置 /
区域）的绘制与高度计算均未改动，只在任务页尾部（日志之前）插入了一个新面板。

## 构建验证

隔离输出命令（`..` 相对工程目录 `FakerSwap/`，即落在仓库根的 `.tmp-build-ui/`）：

```powershell
cd <仓库目录>
dotnet build FakerSwap\FakerSwap.csproj -c Release -t:Rebuild `
  -p:BaseIntermediateOutputPath=..\.tmp-build-ui\obj\ -p:OutputPath=..\.tmp-build-ui\out\
```

结果：**0 error**；1 条 warning 为既有基线
`FakerSwap/Flow/RebirthFlow.cs: warning CS0169: 从不使用字段 RebirthFlow._manualWaitLogged`，
不属于本次 UI 改动。

构建环境注意（与源码无关，写在这里供 Lead 复现）：如果此前用默认路径构建过（存在
`FakerSwap/obj/Release/.NETFramework,Version=v4.7.2.AssemblyAttributes.cs`），上面这条隔离命令会报
`CS0579 TargetFrameworkAttribute 特性重复` —— 原因是覆盖 `BaseIntermediateOutputPath` 后
默认 `obj/**` 不再被 `DefaultItemExcludes` 排除，旧的生成文件被当作源码二次编译。可任选其一：
清掉 `FakerSwap/obj`（构建中间目录，gitignore 内，随即自动重建），或临时注入
`$env:DefaultItemExcludes='obj\**;bin\**'` 再执行同一命令。本次已删除该过期生成文件后验证通过；
Lead 的标准构建（`build.ps1`）重新生成它后，再跑隔离命令需重复此清理。

## 无实机截图的渲染自检

本次按约束未构建进游戏、未部署 `Output/`，因此无法提供窗口实机截图。渲染逻辑按以下路径静态核对：

1. 数据：`WindowSnapshot.Queue` 为 `QueueBatch[]`，`Queue.Length=0` → 空态分支；否则摘要分支。
2. 折叠：只调用 `Fill/Border/Text/TextSmall/MeasureSmall/RowClick`，均以逻辑坐标传入，
   `NativeCanvas` 负责 DPI 缩放、`ScreenY` 滚动换算与内容区裁剪。
3. 展开：`for i in 0..Queue.Length-1`，每行固定 4 段（标记 / 批号 / 批内进度 / 曲目串），
   曲目串用 `MeasureSmall` 实测宽度累加，超界画 `…`；所有颜色来自 `Theme`，无硬编码色值。
4. 交互：`RowClick` 是 `NativeCanvas` 已有命中 API（`HitTest`：先判 `HasClick`，再按
   `ScreenY(y)` 换算滚动的屏幕坐标，再裁内容区，命中即消费点击）。整行命中区与绘制矩形完全一致。
5. 任务页之外：`Render` 的 phase 分发没变，`DrawHome/DrawConfirm` 等函数体未触碰。

实机验收建议（下次进游戏时，1 分钟）：
1. 开始一次转生，停在任务页 → 应看到一行「待办曲目 … ▸」。
2. 点该行 → 展开逐批列表，当前批应有 `▶` + 蓝色底 + 蓝色边框，已完成批 `✓` 绿色，
   课题曲 ★ 为橙色；批内进度 `[DoneCount/Count]` 随游玩推进。
3. 滚轮滚动 → 面板随页面滚动，点击摘要行仍能正确收/展（坐标经滚动换算）。
4. 收起后再展开、切到首页再回任务页 → 展开状态保持；关窗重开 → 回到默认折叠。

## 风险 / 已知限制

- 只能静态验证，无法实机截图；渲染风险集中在字体缺字（`▸`/`▾`/`✓`/`★`）。字体链是
  Microsoft YaHei UI → YaHei → SimSun → Segoe UI，且 GDI 会做字体链接回退；已有的 `▼`
  下拉箭头可作先例。若实机出现方框，把 `▸`/`▾` 换成 `▼` 即可（一行改动）。
- 队列在运行中会被追加课题曲批（`AppendMissionBatches`），摘要与行数最多滞后一个快照周期（0.25s）。
- 快照每 0.25s 深拷贝一次队列（约 `批数×3` 个小对象），分配量与既有 `Sources/Areas` 快照同量级；
  仅为显示用，不影响 Flow。
- 面板只读：点击批次行没有动作，避免误触中止/选曲等有副作用按钮。
- 任务页内容高度增加（折叠 +40px，展开 +N×24px + 62px，含 6px 间距），
  最小窗口 900×700 时任务页会出现滚动条，属预期；其它页面布局高度不变。
