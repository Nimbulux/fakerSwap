# FakerSwap UI — 独立控制窗口

目标：给玩家一个**独立 Win32 控制窗口**（不画在游戏画面上），运行时窗口标题/游戏内横幅都提示
「FakerSwap 占用机台」，并把「选源数据 → 二次确认 → 转生进度 / 中止」这条操作链做完整。

移植自 `Sinmai-Assist/GUI/NativeWindow`（同类环境验证过的「独立线程 + Win32 建窗 + GDI 绘制」实现），
命名空间改为 `FakerSwap.UI.NativeWindow`，内容按 FakerSwap 的流程重写。

## 文件

| 文件 | 作用 |
|---|---|
| `UIHost.cs` | 生命周期（Initialize/Tick/OnGUI/Shutdown）、把 Flow/锁状态镜像成快照、F8 显示/隐藏、游戏内占用横幅 |
| `UiActions.cs` | 窗口按钮回调统一入口：**全部 `MainThread.Post` 回游戏线程** |
| `LogTail.cs` | 日志尾巴（只读最新日志文件，窗口线程可用） |
| `AreaBridge.cs` | 区域爬图：`Navigation.ListAreas(0)` → UI 快照；勾选经 `MainThread.Post` 写回 `ConfigData.CrawlAreaIds` / `CrawlAllAreasFallback` 并保存 config.json |
| `GameFallbackUi.cs` | 独立窗口失败时的极简 IMGUI 兜底面板（阶段/批次/当前曲/6 行日志 + 按钮） |
| `NativeWindow/Win32.cs` | P/Invoke（建窗、GDI、SetWindowText、SetWindowPos 置顶…） |
| `NativeWindow/Theme.cs` | 深色主题色 |
| `NativeWindow/NativeCanvas.cs` | GDI 画布（文本/按钮/列表行/进度条/滚动/中文字体） |
| `NativeWindow/NativeWindowSnapshot.cs` | 游戏线程 → 窗口线程的**不可变快照** + `WindowState` |
| `NativeWindow/WindowRequests.cs` | 窗口线程 → 游戏线程的请求/提示小邮箱 |
| `NativeWindow/NativeModWindow.cs` | 窗口线程、消息循环、绘制、窗口位置持久化、标题、置顶、可重开 |
| `NativeWindow/NativeModContent.cs` | 4 个页面的绘制 |
| `docs/window-*.png` | 自检截图（真实窗口代码渲染，非设计稿） |

## 页面 ↔ 状态机

| `RebirthFlow.Phase` | 页面 |
|---|---|
| `Idle` / `NeedSource` / `SourceReady` / `Finished` / `Failed` | 首页：状态卡 + 「当前不在任务中」+ 机台锁状态 + 源数据二选一（① 备份列表 ② 登录获取） + 开始转生 |
| `SourceLoginPending` / `SourceReading` | 首页 + 顶部「请刷入源账号卡」提示（可取消） |
| `AwaitingConfirm` | 二次确认页：源快照 / 待转生条数 / 批次数 / 预估总时长 / 难度范围 / 模式 + 取消·确认 |
| `TargetLoginPending` / `TargetReading` / `Planning` | 任务页（准备中）：阶段、提示、中止 |
| `Transferring` / `Stopping` | 运行页：进度条、当前批次 / 总批次、当前轨 /3、当前曲目、已用时 / 剩余估算、中止、最近 10 行日志 |

窗口标题：`MachineLock.Active` 时为 **「FakerSwap · 占用机台中」**，否则「FakerSwap · 账号转生控制台」。
游戏内顶部横幅在 `MachineLock.Active` 时常驻（`UIHost.OnGUI`，保留 `ConfigData.ShowOverlayBanner` 开关）。

## 线程模型（重点）

- **窗口线程**（`FakerSwap.Window`，STA）只做：读 `WindowState.Current` 快照、读日志文件、画 GDI。
  **绝不调用 Unity / 游戏对象 / Flow 命令。**
- **游戏线程**：`UIHost.Tick()` 每 250ms 生成新快照（phase/plan/lock/进度/配置…）；每 15s 或按请求
  刷新 `BackupStore.List()`；`UIHost.OnGUI()` 画横幅/兜底面板。
- **窗口 → 游戏**：`UiActions.*` 一律 `MainThread.Post(...)`，执行失败会回一条提示到窗口 footer。
- 窗口隐藏/显示/置顶是窗口本地操作（`PostMessage`），不经过游戏线程。

## 交互 / 尺寸 / DPI

- 启动时自动显示窗口（`SW_SHOWNOACTIVATE`，不抢游戏焦点）。
- `F8`（主线程轮询 `Input.GetKeyDown`）显示/隐藏窗口。
- 点窗口 X 只隐藏，不销毁；F8 重新打开。
- 「置顶：开/关」写 `ModConfig.Data.WindowTopMost` 并即时应用（`SetWindowPos(HWND_TOPMOST)`）。
- 初始尺寸 = `ConfigData.WindowW/H`（默认 1000×780），**最小 900×700**（`WM_GETMINMAXINFO` 同时限制
  用户拖拽和 `SetWindowPos`，所以 760 这类请求会被夹到 900）。
- 布局**每帧按实际客户区宽度重算**（`WM_SIZE → InvalidateRect` 触发重绘）：
  - 客户区 ≥ 960px：左右两列（① 备份列表 62% + ② 登录获取 38%）；
  - 客户区 < 960px：单列上下堆叠（① 在上 ② 在下），900×700 下两个入口都完整可见；
    单列时隐藏「当前设置」卡片，改为 footer 上方一行摘要，避免把 ② 挤出首屏。
- 窗口线程固定 **DPI-unaware**（`SetThreadDpiAwarenessContext`）：这样桌面 150% 缩放时，
  DPI-unaware 的测试/截图工具（`tools/capture.ps1` 的 `GetWindowRect + PrintWindow`）看到的
  逻辑尺寸与窗口一致，不会出现"内容按 1500 宽绘制、截图只到 1060"的裁切。
  代价是高缩放桌面下 GDI 文字会被系统整体放大（略糊），换成 `DpiMode=1`（PMv2）可清晰但截图会裁。
- 窗口位置/大小保存在 `FakerSwap/window.txt`（不写 `config.json`，避免和 Flow 抢写）：
  **每 ~2 秒检测变化自动落盘** + `WM_EXITSIZEMOVE`/关闭/退出时落盘，路径会写日志；
  启动时读取失败/尺寸过小/不在屏幕内 → 回退到 `ConfigData.WindowX/Y/W/H`。
- `WM_PRINT`/`WM_PRINTCLIENT` 已实现，`PrintWindow`（含 `PW_RENDERFULLCONTENT`）能抓到完整客户区。
- 中文绘制：优先 `Microsoft YaHei UI`，依次回退 `Microsoft YaHei` → `SimSun` → `Segoe UI`；
  字体探测走 `System.Drawing.FontFamily`（游戏 `Package/Sinmai_Data/Managed/System.Drawing.dll` 可用），
  全程 try/catch，失败时由 GDI 自己回退。

## 只读模式（SafeNoWrite，2026-11-14 事故后默认开启）

- 只读状态 = `ModConfig.Data.SafeNoWrite`；UI **只读**它，不主动修改运行时开关。
- 顶栏琥珀徽章：`● 只读模式（不写账号）`；窗口标题**保持**「FakerSwap · 账号转生控制台」，
  避免破坏 `tools/capture.ps1 -Title` 的窗口匹配。
- 空闲页琥珀说明卡：`当前 SafeNoWrite=true：只能获取源数据、不能执行转生。` +
  改 `FakerSwap/config.json` 的 `SafeNoWrite` 为 false 并重启游戏的说明。
- 「开始转生」置灰为「开始转生（只读）」；点击会在 footer 给一行提示（不会静默）。
- 运行页标题右侧与进度条下方都有 `只读（不写账号）` 标记；源数据卡片提示文案变琥珀。
- 可选「解除只读…」footer 按钮（仅 SafeNoWrite=true 时出现）→ 二次确认弹窗（说明恢复写入能力 +
  2026-11-14 事故风险）→ 确认后**只把 `config.json` 的 `SafeNoWrite` 写成 false**，
  **不修改运行时 `ConfigData.SafeNoWrite`**，所以本次会话 `Core.WriteGuard` 仍然拦截一切写入；
  提示"需重启游戏生效"，重启后徽章才消失。
- 只读下仍然可以：登录源账号、读取/备份源数据、浏览备份列表；不能开始转生 / 上传成绩。

## 设置面板

首页「区域（爬图）」上方的可折叠 **设置** 区块（默认展开，两列紧凑布局），全部读写 `ConfigData`，
每次改动 `MainThread.Post` → 改字段 → `ModConfig.Save()`，并弹 `WindowRequests.Notice("已保存：…")`：

| 设置 | 字段 | 控件 |
|---|---|---|
| 游玩模式 | `PlayRealism` | 下拉：`C·真实时长` / `B·秒跳`（最常改） |
| 写入方式 | `WriteMode` | 下拉：Both / Payload / Memory |
| 批大小 | `BatchSize` | 按钮 1 首 / 3 首（2 会被服务器拒，故意不提供） |
| 难度下限/上限 | `MinLevel` / `MaxLevel` | 下拉 0~4（BASIC…Re:MASTER） |
| 填充曲 | `FillerMusicId` | 数字输入（默认 432） |
| 选区等待 | `AreaSelectTimeoutSec` | 数字输入 1~10 秒 |
| 二次确认 | `ConfirmBeforeStart` | 开关 |
| 续关优先（实验性） | `ContinueRelogin` | 开关 |
| 机台锁 | `LockInput` | 开关 |
| 只读模式 | `SafeNoWrite` | 开关（关闭走原有二次确认弹窗，重启生效） |
| 爬完再来一轮 | `CrawlAllAreasFallback` | 开关 |
| 自动建号（DEV） | `AllowConfirmNewUser` | 开关 |
| 强制覆盖已有成绩 | `ForceOverwriteExisting` | 开关 |
| 地图结果兜底（老账号） | `MapResultGuard` | 开关 |

- 下拉是自绘 popup（页面内遮罩点击，点外面关闭）；数字输入在失焦或停止输入 ~0.8s 后提交，避免每个按键都写盘。
- 底部脚注：`* 改动立即写入 FakerSwap/config.json；多数项下一批/下次开始任务生效。Dev/实验项仅测试用。`
- 截图：`FakerSwap/docs/window-settings.png`（`window-settings-dropdown.png` 为下拉展开态）。

## 区域（爬图）面板

首页「当前设置」上方新增可折叠区块 **区域（爬图）**（默认展开）：

- 数据：`Navigation.ListAreas(0)`（nav 的 `Navigation.AreaInfo { Id/Name/IsClear/IsComplete/IsLock/IsInfinity/IsEvent/Distance/IsCurrent }`）
  + `MapHooks.SelectedAreaId/SelectedAreaName` + `ConfigData.CrawlAreaIds/CrawlAllAreasFallback`。
- 每行 `[✓] 区域名` + 右侧状态（已完成 / 已通关 / 活动 / 无限 / 锁定 / 当前）+ 进度
  （nav 的 `AreaInfo` 目前没有 Progress/Total 计数，先显示 `进度 <Distance>`；nav 以后加 Progress/Total 字段
  时改 `AreaEntry.ProgressText` 即可显示 `3 / 5`）。
- 操作：`全选`、`全不选`、`仅未完成`（排除 已完成/锁定/无限）、`刷新列表`；
  勾选写回 `ConfigData.CrawlAreaIds`（`List<int>`）并 `ModConfig.Save()`（**经 `MainThread.Post`**）。
- 兜底复选框：`爬完所有勾选区后，从头再来继续爬` → `ConfigData.CrawlAllAreasFallback`。
- 状态行：`当前区域：… · 本轮已爬 N 个 · 勾选区完成 x/y · 正在区域选择页`
  （本轮计数按本次任务中 `MapHooks.SelectedAreaId` 的变化次数统计；运行页也会显示区域 + 本轮数）。
- 注意：`Navigation.ListAreas()` 只在游戏进入**区域选择页**时返回列表；UI 会缓存上次抓到的列表，
  没有缓存时显示"当前不在区域选择页…"，此时可用「刷新列表」重试。预选可以在一次选区页出现后继续使用缓存。
- 截图：`FakerSwap/docs/area-panel.png`。

## 依赖的 Flow 契约

已使用（`docs/architecture.md` 第 4 节 + flow-dev 已落地的命令）：`Phase`、`StatusText`、`LastError`、
`Source`、`Plan`、`CurrentCredit`、`CurrentTrack`、`CurrentItem`、`IsBusy`、`UseSourceFile`、
`BeginSourceAcquire`、`StartTransfer(out string error)`、`ConfirmStart`、`CancelStart`、`Abort`、`ClearSource`。

UI **只读** `MachineLock.Active/NoticeText/Since`；不开 / 不关锁，锁由 Flow 独占。

## 自测

```powershell
# 1) 编译（在仓库根目录）
dotnet build FakerSwap\FakerSwap.csproj -c Release

# 2) 实机
#    启动游戏后：F8 显示窗口；点「登录并读取源数据」→ 刷源账号卡；窗口应显示"等待源账号刷卡"、标题占用；
#    读完后列表出现新备份；选中 → 使用此文件 → 开始转生 → 确认页 → 确认开始；
#    运行页看批次/轨/进度/日志；点「中止任务」应进 Stopping 并收尾登出；结束后窗口标题恢复空闲。
#    日志：Package\FakerSwap\Logs\yyyyMMdd.log；窗口消息异常会写 standalone window: ... 
```

截图自检：临时 net472 harness（不在仓库里）把 `NativeWindow/*.cs` + `LogTail.cs` 原样编译，喂假快照，
再用**和 `tools/capture.ps1` 完全相同的路径**（`GetWindowRect` + `PrintWindow(hwnd,hdc,2)`）截图：

- 900×700、1200×900、1000×780、760（被最小尺寸夹到 900×760）四档：内容均完整、无横向裁切；
- 900 以下单列时 ① ② 都完整可见；
- 点 X 隐藏 / F8 重开、`Start→Shutdown→Start` 连续多轮不炸；
- `window.txt` 自动落盘、重启后位置/尺寸恢复，读不到时回退 config。

截图（`docs/`）：

| 文件 | 内容 |
|---|---|
| `window-idle.png` | 空闲 + 无备份（单列 900 宽） |
| `window-home-source.png` | 已载入源数据（单列） |
| `window-source-login.png` | 等待源账号刷卡（占用锁 + 中止入口） |
| `window-confirm.png` | 二次确认 |
| `window-running.png` | 运行页（进度/批次/轨/曲目/中止/日志） |
| `window-failed.png` | 失败态 |
| `window-printwindow-900x700.png` | **验收：PrintWindow 900×700**（① ② 完整可见） |
| `window-printwindow-1200x900.png` | **验收：PrintWindow 1200×900**（两列） |
| `window-printwindow-1000x780.png` | **验收：PrintWindow 1000×780 默认尺寸**（两列） |
| `window-printwindow-requested-760-clamped-900x760.png` | 请求 760×760 被最小尺寸夹到 900×760（单列，内容完整） |
| `window-readonly-1000x780.png` | **只读模式（1000×780）**：徽章 + 说明卡 + 置灰的开始按钮 + 「解除只读…」 |
| `window-readonly-900x700.png` | 只读模式（900×700，单列） |
| `window-readonly-unlock-dialog.png` | 「解除只读」二次确认弹窗 |
| `window-readonly-running.png` | 只读模式下的运行页（进度区 `只读 · 不写账号` 标记） |
