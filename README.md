# FakerSwap — maimai DX 账号成绩转移

MelonLoader 插件：把 A 账号的成绩通过**游戏自己的正常流程**（选歌 → 游玩 → 结算 → 存档 → 续关）
转移到 B 账号，插件只改写**上传报文**里的成绩数据。

- 已实测：**SDGB 1.55 国服**（`maimai-gm.wahlap.com`）连续 2 个 credit 存档成功
- 同时兼容：SDEZ 1.65 私服（AquaDX 等）

> 仅限**自己有权使用的账号 / 自己的测试服数据**。不要用于绕过付费、他人账号或商业用途。

---

## 工作原理（重要）

插件**不再伪造游戏流程**：

| 环节 | 谁来做 |
|---|---|
| 选歌 / 难度 / 设置 / 开始 | 插件驱动真实按键（4 号键 = 決定） |
| 游玩 / 结算 / 存档 | 游戏原版 |
| **续关** | 游戏原版：插件只点按一次 4 号键，游戏自己调 `ContinueProcess.ReLogin()` 发 `UserLoginApi(isContinue=true)`，拿到新的 `loginId` |
| 成绩写入 | **只改上传报文**（`WriteMode = Payload`）：`UpsertUserAll` 的 `userMusicDetailList`、`isNewMusicDetailList` 等 |

国服的关键约束（实测）：
- `UpsertUserAll.playlogId` 必须等于**本次登录**的 `loginId`；每个 credit 都需要一次 `isContinue` 登录换新的 `loginId`；
- `lastPlayCredit` / `playCredit` 续关后仍是 **1**（不要改）；
- 每包只能 **1 或 3 首**（2 首包会被拒）；
- 登录后至少 **50 秒**才允许上传，否则整包被丢弃；
- 服务器 `returnCode`：**1 = 成功，0 = 失败**。

详见 [docs/official-server.md](docs/official-server.md)。

---

## 界面

独立 Win32 窗口（不占游戏画面），F8 显示/隐藏。布局自下而上：

1. **状态栏**：阶段 / 进度 / 当前曲目 / 机台锁计时；
2. **待办列表**（可展开）：每批 3 首，显示曲名、难度、目标达成率、完成状态；
3. **开始 / 中止 / 确认**按钮；
4. **设置**（可收起）：
   - 左列：游玩模式（C·真实时长 / B·秒跳）、难度下限、难度上限、长版歌曲（正常跑 / 跳过 / 直写）
   - 右列：机台锁、开始前二次确认、选票保持、下次用票

已移除的无用/实验项（仍保留在 config.json，但不再出现在界面）：
写入方式、每批数量、填充曲、选区等待、续关优先、安全空写、区域兜底、自动建号、强制覆盖、地图兜底等。

---

## 快速开始

```powershell
# 1. 取回游戏 DLL（不可再分发）
powershell -File tools/fetch-libs.ps1 -GameDir "C:\...\SDEZ_1.65.00_..._0"
#    或手动把 Package\Sinmai_Data\Managed 的 DLL 放进 FakerSwap\Libs\

# 2. 构建
dotnet build FakerSwap\FakerSwap.csproj -c Release -t:Rebuild

# 3. 部署（游戏需关闭）
powershell -File tools/close-game.ps1
copy Output\FakerSwap.dll "<游戏>\Package\Mods\FakerSwap.dll"

# 4. 启动游戏（各整合包启动器不同，例如 Package\原神启动.bat）
```

使用：
1. 刷卡登录目标账号（国服需要物理卡/AimeGen）；
2. 在窗口里选源数据快照（`source-file` 或界面选择）；
3. 点「开始转生」，按提示确认；
4. 完成后自动登出。

---

## 目录

```
FakerSwap/            插件源码（Flow=流程，Net=报文改写，UI=窗口）
tools/                构建/部署/抓包辅助脚本
docs/                 文档（见 docs/README.md；旧文档在 docs/archive/）
Output/               构建产物
```

## 常见问题

- **存档弹「数据保存失败」**：多数是续关没有真正完成 `isContinue` 登录（playlogId 仍旧）。
  看日志有没有 `[trace] ContinueProcess.ReLogin` 和新的 `UserLogin ok loginId=`。
- **续关屏卡住或秒过**：插件只点按一次 4 号键；如果游戏没反应会每 4 秒重按一次。
- **第二局被跳过旅行伙伴选择**：已修复——该页面会等待约 1.5 秒展示后再按決定。
- **国服长曲**：長尺曲占 2 轨、会形成 2 首包，官方服拒收；建议设置「长版歌曲 = 跳过」。

## 免责声明

本项目仅供学习与个人数据备份/迁移测试。使用本工具产生的任何后果（包括账号风险、数据损坏）
由使用者自行承担。
