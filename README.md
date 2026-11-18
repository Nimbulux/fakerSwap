# FakerSwap · maimai DX 账号成绩转生工具

[![build](https://img.shields.io/badge/.NET-net472-blue)]() [![MelonLoader](https://img.shields.io/badge/MelonLoader-0.6.4-green)]()

> 把 A 账号的「红谱以上」成绩，通过**游戏自己的上传链路**灌进 B 账号。
> 面向 **Sinmai（maimai DX / SDEZ 1.65）+ 私服（AquaDX / 兼容 API）**。
>
> 名字来自 **Fake**（谱面是假打的）+ **Swap**（成绩数据是搬的）。
>
> **仅限你有权限的账号 / 自己的测试服数据迁移使用。** 不用于绕过付费、他人账号或商业用途。

---

## English summary

MelonLoader mod for Sinmai (maimai DX SDEZ 1.65) that migrates EXPERT+ scores from one
account snapshot to another through the game's normal DataSave/upload path, with a standalone
Win32 control window, real-input screen automation (MODE SELECT / area select / ticket select /
photo confirm), in-credit area-mission songs, continue-based credit chaining, upload
verification, and a read-only safety gate. For private servers and your own accounts only.

---

## 功能

| 能力 | 说明 |
|---|---|
| **源数据获取** | ① 选本地备份 / Astral 抓包 JSON；② 让源账号刷卡登录一次，插件在选歌页抓全量成绩并自动备份到 `Package\FakerSwap\Backup\` |
| **自动转生** | 按难度窗口（默认 EXPERT~Re:MASTER）把源成绩按每 credit 3 首分批，逐曲写入并上传 |
| **全程真实操作** | MODE SELECT、写真确认（选「不拍」）、登录奖励、区域选择、选歌、结算都是走游戏自己的输入/进程链路，不硬跳屏 |
| **区域选择加速** | 选区页直接改写光标下标 + 一次真实 4 号键決定（30 秒倒计时内完成）；也可 `ManualAreaSelect` 交给人工 |
| **课题曲** | 选区确认后从区域主数据排 1 首课题曲，塞进当前 credit 的第 3 轨（不打乱成绩、不堆 filler）；`MaxMissionCredits` 控制总数 |
| **选票** | 默认**不选票**（测试服安全）；设置里「下次用票」/「选票保持」可控 |
| **续关** | 国服唯一可行的跨 credit 方式；续关后自动重置 `MusicTrackNumber`（修复 GameProcess.OnStart 崩溃） |
| **上传校验** | 每个 credit 等 DataSave 真正发生才计数；`Payload` 改写 + `Memory` 回写可选，结束回读核对 |
| **安全闸门** | `SafeNoWrite=true` 时只读不写；从不按 5 号键（Continue「否」）；异常/中止/关游戏都会走登出 |
| **独立控制台** | 独立 Win32 窗口（不进游戏画面），置顶、F8 显示/隐藏；待办曲目播放列表可展开；任务进度/当前曲目/剩余时间 |

---

## 环境要求

- Windows，**Sinmai（maimai DX SDEZ 1.65）**
- [MelonLoader 0.6.4](https://github.com/LavaGang/MelonLoader)（.NET Framework 4.7.2）
- .NET SDK（编译用）
- 私服 API（AquaDX 或兼容 `UpsertUserAll / UploadUserPlaylog` 的服务端）

## 构建

```powershell
# 1) 指向你的游戏目录（只需设置一次，或每次传 -Game）
$env:MAI_GAME_DIR = "D:\maimai\SDEZ_1.65.00"

# 2) 从游戏安装里复制编译所需的引用 DLL（本仓库不发布游戏 DLL）
pwsh .\tools\fetch-libs.ps1

# 3) 构建（输出 Output\FakerSwap.dll；设置了 MAI_GAME_DIR 时自动部署到 Package\Mods）
pwsh .\build.ps1
```

> `FakerSwap/Libs/` 里的 `Assembly-CSharp.dll`、`UnityEngine*.dll` 等属于游戏本体，
> **不随仓库发布**；`fetch-libs.ps1` 会从 `Package\Sinmai_Data\Managed` 和
> `Package\MelonLoader\net35` 复制。

## 安装

1. 游戏目录已装好 **MelonLoader 0.6.4**。
2. 把 `Output\FakerSwap.dll` 放进 `Package\Mods\`。
3. 启动游戏，控制窗口会自动出现（F8 显示/隐藏，X 隐藏）。

## 使用

1. **源数据**：窗口「源数据」里选备份文件，或点「登录并读取源数据」刷源账号卡（读完自动登出）。
   > 自动刷卡默认关闭；`AutoSwipe=false` 时停在登录页等你刷卡。
2. **开始转生**：选源数据 → 点「开始转生」→ 刷目标账号卡 → 读取数据 → 二次确认（条数/批次/预计时长/写入方式）。
3. **跑批**：插件逐 credit 跑（真实按键 + 真实结算 + DataSave 上传），窗口显示进度、当前曲目、待办队列。
4. **收尾**：全部完成自动登出；随时可点「中止任务」（会让当前 credit 用填充曲跑完，不改成绩，再登出）。

## 配置（`Package\FakerSwap\config.json`）

| 键 | 默认 | 说明 |
|---|---|---|
| `AimeCode` | `""` | 模拟刷卡用访问码；留空则读 `AMDaemon\DEVICE\aime.txt` |
| `AutoSwipe` | `false` | 是否允许插件模拟刷卡（测试/自动跑图用；默认等人工刷卡） |
| `MinLevel` / `MaxLevel` | `2` / `4` | 难度窗口（2=EXPERT，3=MASTER，4=Re:MASTER） |
| `BatchSize` | `3` | 每 credit 首数（**2 会被服务端拒收**，只能 1 或 3） |
| `WriteMode` | `Both` | `Memory` 直接改内存 / `Payload` 改上传报文 / `Both` |
| `PlayRealism` | `Judge` | `Judge`=秒跳过场（B 模式）/ `Duration`=挂机到自然结束（C 模式） |
| `ContinueRelogin` | `true` | 用续关跨 credit（国服必须开；重刷卡仅国际服可用） |
| `FillerMusicId` | `432` | 填充曲（性隐 BASIC），用于补齐 3 轨 |
| `PadToFullCredit` | `true` | 用填充曲把每批补满 3 首，避免 2 首报文被拒 |
| `AreaSelectTimeoutSec` | `8` | 选区页等待秒数（导航会直接跳光标并決定，不用等它跑满） |
| `ManualAreaSelect` | `false` | true = 选区完全交给人工，确认后发 `areaok` |
| `AutoAddMissionSongs` | `true` | 自动检测并排入区域课题曲 |
| `MaxMissionCredits` | `3` | 本次运行最多打几首课题曲（每首占当前 credit 一轨；调大 = 多跑区，批次数按需增长） |
| `TicketUseNext` / `TicketUseKeep` | `false` | 选票：下一次手选 / 保持每次用上次手选的票（默认不选票） |
| `SafeNoWrite` | **`true`** | 只读模式：导航/读取/规划都跑，但任何写账号操作一律拒绝 |
| `LockInput` | `true` | 任务期间锁机台输入 |
| `WindowTopMost` | `true` | 独立窗口置顶 |

## 控制台 / 命令

窗口下方的 `command.txt` 支持（写一行命令，文件变化即执行）：

| 命令 | 作用 |
|---|---|
| `source-file <path>` / `source-acquire` / `source-clear` | 选择/读取/清除源数据 |
| `start` / `confirm` / `cancel` / `abort` / `unlock` | 开始 / 二次确认 / 取消 / 中止 / 解锁 |
| `queue` | 打印当前待办队列（按批） |
| `areaok` | 配合 `ManualAreaSelect`：人工选区完成后放行 |
| `mmselect` | MODE SELECT 交给人工（C 键=4 号键） |
| `state` / `stack` / `cdiag` / `modediag` | 状态/进程栈/倒计时/模式诊断 |
| `missiondump` | 打印选歌页课题曲分类的扫描结果 |
| `ildump <type> [method]` | 运行时 IL dump（本仓库开发时用来破 MODE SELECT / 教程等） |

## 工作流程（简图）

```
登录/受付 → MODE SELECT（真实 4 号键 ×2）
→ 写真确认（存在才按「不拍」）→ 登录奖励（让它自己结束 → 选区）
→ 区域选择（跳光标 + 4 号键決定）→ 选歌页
→ [本 credit] 2 首成绩曲 + 1 首课题曲（3 轨）
→ 结算 → DataSave 上传 → CONTINUE（国服续关，重置轨道计数）
→ 下一 credit …→ 全部完成 → 登出
```

## 项目结构

```
FakerSwap/              插件源码（net472 / C# 9）
  Core/                 配置、日志、Harmony 安装、崩溃兜底、IL dump
  Flow/                 导航（真实输入/行屏处理）、登录、地图、分数、流程状态机
  UI/                   独立 Win32 控制台（GDI 自绘）
  Libs/                 游戏引用 DLL（不发布，fetch-libs.ps1 生成）
docs/                   架构/流程/协议/事故记录/审计
tools/                  构建辅助（fetch-libs、apidump、截图、关游戏等）
build.ps1               一键构建 + 部署
```

## 已知限制

- 只支持 SDEZ 1.65（方法签名/枚举按该版本校准；其它版本需要重新适配）。
- 私服必须支持游戏原生的上传链路；服务端拒收 2-playlog 报文，所以填充曲补齐不可关。
- 音游自动化本身有风险；`SafeNoWrite` 先跑一遍只读确认流程，再关闭写入。

## 免责声明

本项目仅供**自己账号之间 / 自己的测试服**做数据迁移与自动化研究。请勿用于：
绕过付费、修改他人账号、商业用途，或任何违反游戏/服务端条款的行为。
使用本项目造成的一切后果（账号封禁、数据损坏等）由使用者自行承担。

## License

MIT
