# 实机验证计划（单账号开发环境）

> 环境：竖屏显示器 1080×1920 跑游戏；账号 uid <源uid>（当前为空档，用户会先打几首歌作为真实样本）。
> 原则：**先只读跑通，再单条写入，最后整批**；任何写入前都会强制落 `Backup\pre-write-*.json`。

## 第 0 步：用户先打几首歌

用户手动游玩 3~6 首（真实成绩），产生可用的样本数据。
这一步插件**不参与**（`SafeNoWrite=true`，`InputLock` 未激活）。

## 第 1 步：只读验收 —— 「登录 → 选歌页 → 读全量 → 备份 → 超时登出」

1. 重启游戏（加载最新 DLL）。
2. `FakerSwap\command.txt` 写 `source-acquire`（或点窗口「登录并读取源数据」）。
3. 期望日志：
   ```
   [nav] ConfirmPlay 注入「是」… try=1
   LoginHooks: UserLogin returnCode=1
   flow: source account logged in: <name> uid=<源uid>
   [nav] ModeSelect → TimeUp（结束受付）
   [nav] Fade -> MusicSelectProcess
   [flow] reading: step=WaitMusicSelect stage=MusicSelect atMusic=True
   UserDataPatcher.ReadLive: <name> R<rating> (N条)
   backup written: ...\Backup\<name>-<date>-R<rating>.json
   LoginHooks: 模拟超时 (无 playlog, ForceTimeUp)     ← 只超时登出，不写任何东西
   ```
4. 验收点：备份文件里 `records` 条数 = 用户实际打过的歌数（+历史记录），rating 与游戏内一致。

## 第 2 步：单条写入 —— `WriteMode=Memory`，`ForceOverwriteExisting=true`

1. 用第 1 步的备份当源，**把其中 1 条的 achievement 改成 100.5000%（`1005000`）**，另存为 `Source/test-1.json`。
2. `config.json`：
   ```json
   "SafeNoWrite": false,
   "WriteMode": "Memory",
   "AllowSelfTransfer": true,
   "ForceOverwriteExisting": true,
   "BatchSize": 1
   ```
3. `command.txt` 写 `source-file <path>\test-1.json` 然后 `start` → 确认 → 跑 1 首。
4. 期望日志：
   ```
   plan: items=1 ... (force overwrite existing enabled)
   [nav] 选中 <music>-<level> (cat=.. idx=..)
   [nav] 进入第 1 轨
   [nav] 跳过第 1 轨
   [score] 成绩写回 UserData(Result+0.25s): ... 内存touched=1
   pre-write backup written: ...\Backup\pre-write-*.json
   PayloadRewriter: patched 1 row(s) in UpsertUserAllApi
   ```
5. 验收点：游戏内该曲变成 100.5% / SSS+，`GetUserPreview` 的 rating 上升。

## 第 3 步：整批 + 中止语义

- 用第 1 步备份的完整集合当源（可整体 +0.5%），跑 2~3 个 credit。
- 验证：3 首/批、批间 50s 节流、15s 轨间隔、末批用幸隐(432 BASIC) 补齐。
- 分别在 1/3、2/3、3/3 中途中止，确认：当前轨不改成绩、剩余轨是 filler、不续关、最后回到 Entry 且 `loggedIn=False`。

## 回滚手段

| 场景 | 回滚 |
|---|---|
| 写坏单条 | 用 `Backup\pre-write-*.json` 作为源，`ForceOverwriteExisting=true` 再转生回去 |
| 写坏整批 | 同上，但源用第 1 步的全量备份 |
| 账号彻底空档 | 从服务端 DB 备份恢复，或重新打一遍 |
