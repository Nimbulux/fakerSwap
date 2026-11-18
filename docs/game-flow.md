# 实机观察记录（SDEZ 1.65 + AquaNet 私服）

> 环境：`Package\Sinmai.exe`，MelonLoader 0.6.4，窗口模式（AquaMai `GameSystem.Window` Width=1080 Height=1920，竖屏）。
> 服：`http://aime.my-aqua.cn/Maimai2Servlet/1.60/A61E01D3538/...`
> 测试账号：`<源账号>`，uid **<源uid>**，lastGameId=SDEZ，lastDataVersion=1.65.00，lastRomVersion=1.60.00。

## 登录链路（实测 2026-11-14 20:54）

1. 标题/招揽画面上出现「かざしてね」提示（AimeGen 虚拟卡常驻）。
2. 触发读卡后 → `GetUserPreviewApi`：
   ```
   POST .../GetUserPreviewApi   {"userId":<源uid>,"segaIdAuthKey":""}
   响应 {"userId":<源uid>,"userName":"<源账号>","isLogin":false,"lastGameId":"SDEZ",...}
   ```
   注意 `isLogin:false` —— 说明上次是正常登出的（没有卡 15 分钟）。
3. 然后进入 **INFORMATION 确认页**（不是直接进模式选择）：
   - 文案：「このAimeでプレイを開始しますか？」「**ボタンをおしてください**」
   - 卡片显示玩家名 / RANK / TOTAL STAR / プレイデート
   - 此时还没有 `UserLoginApi`，需要按一次按键/触摸才继续。
4. 按确认后才 `UserLoginApi` → 模式选择 → 信息（活动/区域）→ 选歌。

**对插件的含义**：
- 导航里必须有「确认页 → 按一下」这一步（`Navigation.GoToMusicSelect` 要能处理）。
- 判断"已登录"最可靠的信号是 `GetUserPreview` 的响应（`isLogin`）或 `NetDataManager.SetLoginVO`，不是画面特征。
- 登录 >50s / 每曲 >15s 的节流要从 `SetLoginVO` 的 `loginDateTime` 起算。

## 启动期间会打的接口（可用来判断"到标题了"）

`GetGameSettingApi` / `GetGameEventApi` / `GetGameChargeApi` / `GetGameNgMusicIdApi` /
`GetGameNationalDataApi`(多次) / `GetGameWeeklyDataApi` / `GetGameFestaApi` /
`GetPlaceCircleDataApi` / `GetGameRankingApi` / `GetGameTournamentInfoApi`

`GetUserPreviewApi` 只在读卡后出现 —— 适合作为"刷卡被识别"的信号。

## 环境坑

1. **进程被内核驱动 `odd.sys` 保护**：`taskkill /f`、`Stop-Process` 全部 `Access denied`。
   要重启游戏必须先由管理员卸载该驱动（`先管理员启动我.bat` 按任意键，或 `sc stop odd`）。
   → 插件里**绝对不要**用 `Process.Kill()` 做兜底退出。
2. 用 `SetWindowPos` 改窗口大小/最大化会让渲染错位（黑边），别去动游戏窗口。
3. MelonLoader 的 Mods 目录是 `Package\Mods`，运行中 **DLL 文件不被锁**（可以覆盖），但**必须重启进程才会生效**。
4. Sinmai-Assist 的 NetworkLogger 是开的：`Package\Sinmai-Assist\NetworkLogs\yyyy-MM-dd.log`，
   含完整请求/响应体（本次抓到 64×GetGameNationalDataApi 等），抓协议直接看它。
