# 文档索引

| 文档 | 内容 | 状态 |
|---|---|---|
| [architecture.md](architecture.md) | 代码结构、模块职责 | 现行 |
| [game-flow.md](game-flow.md) | 游戏流程与插件介入点 | 现行 |
| [official-server.md](official-server.md) | **国服（SDGB）实测约束与续关协议** | 现行，重点 |
| [upload-protocol.md](upload-protocol.md) | UpsertUserAll / UploadUserPlaylog 报文说明 | 现行 |
| [judge-matrix.md](judge-matrix.md) | 音符类型 × 判定等级约束 | 现行 |
| [area-mission.md](area-mission.md) | 区域课题曲流程 | 现行 |
| [navigation.md](navigation.md) | 界面导航/反射调用参考（较长） | 参考 |
| `archive/` | 旧版开发记录、排查日志、历史方案 | 仅存档 |

## 快速结论（2026-11-21）

1. 插件走游戏原版流程，只改上传报文（`WriteMode = Payload`）。
2. 国服续关必须让游戏原生 `ContinueProcess.ReLogin()` 完成 `UserLoginApi(isContinue=true)`；
   每个 credit 的 `UpsertUserAll.playlogId` 必须等于该次登录的 `loginId`。
3. 续关后 `lastPlayCredit` / `playCredit` 保持 1，不要强行改成 2。
4. 服务器返回 `returnCode`：**1 成功，0 失败**；2 首包被拒；登录后 ≥50 秒才允许上传。
