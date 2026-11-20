# 神秘菜单（关于面板 / 解锁 / 跑区改造）

> 状态：代码已完成并通过 `dotnet build`（0 警告 0 错误），**尚未实机验证**（开发时游戏在跑，
> 未覆盖已加载的 DLL）。本文记录实现方式、配置项与实测清单。

## 1. 关于面板 + 风车图标 + 360° 解锁

文件：`FakerSwap/UI/NativeWindow/AboutPanel.cs`（新）

- 主界面最下方新增可折叠「关于」区块：版本 / 作者 / MIT 协议 / 仓库 + 实时环境
  （游戏版本、Unity、MelonLoader、服务器地址、曲库数、区域数、账号 Rating/最高/游玩次数/MAP/POINT）。
- 右上角是 4 叶风车图标，用 `gdi32.Polygon`（`Win32.cs` 新增导入）+ `sin/cos` 顶点绘制，
  **不引入 GDI 世界变换**；每点一次 +90°，300ms ease-out 动画。
  动画期间 `NativeModWindow` 把定时器从 120ms 临时调到 16ms，结束后恢复。
- 累计 360°（4 下）→ `secretUnlocked=1`，关于面板出现「进入神秘菜单 →」。
- 状态持久化：`FakerSwap/ui-state.txt`（窗口线程自己写，**不碰 config.json**，避免和 Flow 抢写）。
  删除该文件即可重新拧 4 下。
- 神秘菜单是整页（替换主界面内容区），顶部标签：总览 / 曲库 / 区域 / 收藏 / 票券 / 工具。
  `ESC` 或「← 返回」退出；任务进行中/只读模式会在页顶显示警告条。

## 2. 解锁歌曲 / 解锁收藏品（P2）

文件：`FakerSwap/Data/UnlockService.cs`、`Data/GameCatalog.cs`、`UI/UiActions.cs`（新增段）

**原理：只写游戏自己的 `UserData`，不自己造包。** 游戏的 `VOExtensions.ExportUserAll`
会把下面这些列表并进 `UpsertUserAll.userItemList`，并且只发和服务端快照不同的行：

| UserData 列表 | 上报 itemKind | 含义 |
|---|---|---|
| `MusicUnlockList` | 5 | 歌曲本体 |
| `MusicUnlockMasterList` | 6 | MASTER（仅 `musicId >= 10000` 的 DX 曲需要） |
| `MusicUnlockReMasterList` | 7 | Re:MASTER（同上，还受机台活动开关限制） |
| `TitleList / IconList / PlateList / FrameList / PartnerList` | 收藏类 | 称号 / 头像 / 姓名框 / 背景 / 伙伴 |

真机抓包（`Sinmai-Assist/NetworkLogs/2026-11-21.log`）验证过：一局解锁两个收藏品时，
`userItemList` 恰好出现 2 行且 `isNewItemList = "11"` —— 所以「写内存 → 游戏自己差分上报」是可行的。

- 写入入口：`UserData.AddUnlockMusic(UserData.MusicUnlock.Base/Master/ReMaster, id)` / 收藏列表 `Add(new UserItem(id){stock=1,isValid=true})`。
- 去重：先查列表 `Contains`，已存在不重复写（重复 INSERT 会被服务端 500）。
- 分批：`ConfigData.UnlockBatchRows`（默认 200）。一次写太多行可能被服务端拒，UI 显示「剩余 N（再点一次继续）」。
- 上传：`UnlockService.Flush()` → `LoginHooks.ForceDataSave()`（游戏自己的 DataSave 链路）。
  只在安全阶段（选歌页/模式选择等）可用；否则提示「打一首 B·秒跳 结算后自动上传」。
- 只读闸门：`SafeNoWrite=true` 时全部写入被拒绝，UI 顶部红条提示先「解除只读」。

UI：
- **曲库**：id / 曲名 / 艺术家 / 5 个难度等级（B/A/E/M/R）/ 本体解锁项是否已写入；
  搜索框（`#11414` 按 id，其余按曲名/艺术家子串）、全部/未写入/已写入 过滤、单页 14 行分页；
  单行「写入」、本页批量、全部批量、含紫谱/含白谱开关、存档上传。
- **收藏**：称号/头像/姓名框/背景/伙伴 各自「持有 / 总数」+ 进度条 + 「补全」，以及「全部补全」。
- 数据来源 `GameCatalog`：主线程从 `Singleton<DataManager>`（`GetMusics`/`GetTitles`/…）
  和 `Singleton<UserDataManager>`（解锁列表）读一次，发布成普通数组，窗口线程只读。
  账号切换（snapshot 里 `AccountId` 变化）会自动标记过期。

## 3. 跑区改造（P3）

文件：`FakerSwap/Data/AreaCatalog.cs`（新）、`UI/UIHost.cs`、`UI/AreaBridge.cs`、
`NativeWindowSnapshot.cs`、`NativeModContent.cs`、`Flow/MapHooks.cs`、`Flow/RebirthFlow.cs`

**区域列表不再依赖选区页**，来源优先级：

1. `UserData.MapList`（登录后立刻可用：名称/进度/锁定/清关）
2. `DataManager.GetMapDatas()`（主数据：名称 / BonusMusicId / IsCollabo / IsInfinity / OpenEventId）
3. `FakerSwap/areas.json` 缓存（Title / 未登录也能显示）
4. `StreamingAssets/A*/map/Map*/Map.xml` 扫描（最后兜底）

缓存指纹 = 包目录集合 + map 目录数 + 最新修改时间；游戏更新后自动重建。
`UIHost.LoadAreas()` 在运行时列表为空时回落到 `AreaCatalog`，所以面板任何时候都有 175 个区；
离线行显示「离线」标签（锁定/进度未知），登录后自动补上。
「刷新列表」会同时让缓存失效重建。

**分数 / 跑区加成（按用户要求）：**

- `ConfigData.FillerAchievement`（默认 **1005000 = 100.5%**）+ `FillerScoreEnable`（默认 true）：
  填充轨（432 性隐 BASIC）不再「跳过且不改成绩」，而是写入满分/AP —— 绿谱没人看，
  地图距离加成拿满。
- `ConfigData.MissionAchievement`（默认 800000 = 80%）现在会持久化（之前只在内存里）。
  普通课题曲仍按 80% 通过；挑战曲（`StartLife > 0`）保持原有 NoMiss / AllowImperfect 规则。
- 区域课题曲（`QueueAreaBonusMissions`）难度选择改为：优先配置难度，
  该谱面不存在时退到这首曲子实际存在的最低难度（普通课题曲「不限难度」）。
- 跑区建议用 **C·真实时长**（`PlayRealism=Duration`，也是默认值）；区域面板顶部有提示。

## 4. 新增配置项（`FakerSwap/config.json`）

| 字段 | 默认 | 说明 |
|---|---|---|
| `MissionAchievement` | 800000 | 普通课题曲目标达成率（1/10000 %） |
| `FillerAchievement` | 1005000 | 填充轨目标达成率（100.5% = AP） |
| `FillerScoreEnable` | true | 填充轨是否写成绩 |
| `UnlockBatchRows` | 200 | 解锁每批最多写多少条 |

## 5. 实测清单（游戏关掉后部署 DLL 再验证）

1. `F8` 打开窗口 → 滚动到底部看到「关于」，右侧 4 叶风车；点 4 下看动画、转满后出现「进入神秘菜单」。
2. `ui-state.txt` 出现；重启游戏后仍是已解锁状态（删文件可重置）。
3. 打开神秘菜单 → 总览：账号名/Rating/服务器/曲库数正确；未登录时显示「未登录」。
4. 曲库：搜索 `#11414`、「未写入」过滤、翻页；单曲「写入」→ 日志有 `UnlockService` 行；
   点「存档上传」→ 看 `LoginHooks: forced DataSave`，之后落库回查该曲是否解锁。
5. 收藏：点「补全」→ 日志 `[unlock] 收藏品 …`，上传后重进收藏页应全亮。
6. 区域：Title 阶段打开面板应能看到 175 个区（离线标签）；登录后变进度/锁定；
   勾选后 `config.json` 的 `CrawlAreaIds` 立即变化。
7. 跑区：设置 C·真实时长，确认填充轨成绩是 100.5% / AP（日志 `filler` 轨 + `Result+0.25s` 提交）。

## 6. 风险 / 注意

- **国服是官方服务器**：写解锁类是真实改账号数据，有条款风险；建议先私服验证。
- 一次写入/上报太多 `userItemList` 行未经真机验证，保持分批（200）并逐批上传。
- `ForceDataSave` 只在安全阶段成功；标题页/登录页会拒绝（日志会写明原因）。
- 区域列表可能比运行时多（服务端没下发的区）：勾了但游戏列表里没有时，`SelectArea` 会记录
  `area-notfound` 警告并跳过，不会崩。
- 与 AquaMai 的 `[GameSystem.Unlock]` 互不影响：那是客户端显示层解锁（不上传），
  本功能是真正写账号（会上传）。两边同时开时，显示层被 AquaMai 抢先，但落库仍以 UserData 为准。
- `docs/handoff-2026-11-21.md` 里的老坑仍有效（PowerShell 多行合并、VO struct 写回、
  returnCode 1=成功、每 credit 新 loginId、50 秒上传下限、5 号键红线、选歌页 2 秒初始化）。
