# 区域课题曲（課題曲 / Area Bonus Music）

## 数据链（全部在客户端主数据里，不需要服务端）

```
Package/Sinmai_Data/StreamingAssets/<pack>/
  map/Map{区ID:000000}/Map.xml
      <BonusMusicId><id>X</id></BonusMusicId>          ← 区域的"课题曲组"编号
      <BonusMusicMagnification>2000</BonusMusicMagnification>   ← 加成倍率 2.0x

  mapBonusMusic/MapBonusMusic{X:000000}/MapBonusMusic.xml
      <MusicIds><list>
          <StringID><id>11976</id><str>…</str></StringID>   ← 真正的课题曲 musicId
          …
      </list></MusicIds>
```

**实测样本**

| 区域 | 区ID | 组 | 课题曲数 | 例子 |
|---|---|---|---|---|
| 涼宮ハルヒちほー | 650052 | 650052 | **81 首** | 11976 / 11977 / 11978 / 10201 … |
| はじまりのちほー | 000001 | 000002 | 60+ 首 | 11003 / 11006 / 11004 … |
| 某协作区 | 550057 | 550057 | 18 首 | 11856 / 11857 / 11263 … |

220 个区域**全部**都有课题曲表。

## 代码位置

`FakerSwap/Flow/MapHooks.cs`

```csharp
int  MapHooks.GetAreaBonusId(int areaId);         // Map.xml -> BonusMusicId
int[] MapHooks.GetAreaBonusMusicIds(int areaId);  // MapBonusMusic.xml -> 全部课题曲
int  MapHooks.QueueAreaBonusMissions(int areaId, int max);  // 入队到 PendingMissionCharts
```

两级缓存（`_areaBonusIdCache` / `_bonusMusicCache`），同一区域只读一次盘。

## 通过条件

- 成绩下限：`ScoreHooks.MinClearAchievement`（**80.0000%**，即"最低通关"）
- 难度：取 `ConfigData.MinLevel`（默认 2 = EXPERT）
- 判定表仍由 `ScoreMath.BuildJudgeBreakdown` 生成，结算/playlog 与其它轨一致

## 加入游玩列表的路径

```
TickAreaSelection() 选区成功
   └── QueueAreaBonusMissions(areaId, 3)
         └── PendingMissionCharts += MissionChart{ MusicId, Level, Achievement, Source="AreaBonus(区ID)" }

BeginCredit()
   └── AppendMissionBatches()
         └── MapHooks.TakeMissionCharts()          // 消费并清空
               └── SlotKind.Mission 的批次（每批最多 3 首；2 首时补 filler 或拆成两个单曲批）
                     └── 追加到 _batches，Plan.Credits 同步增加
```

日志样例：

```
[map] 区域课题曲: area=650052 组=650052 表中 81 首 → 加入 3 首 (level=2)
[flow] 选区: 区域 650052 加入课题曲 3 首
[map] 检测到课题曲 1 首 (MapTask)，将加入下一批游玩列表（最低通关 80.0000%）
[map]   课题曲: 11976-0@80.0000%(MapTask)
[flow] 区域循环: 新增课题曲 1 首 → 共 34 批
flow: credit 2/34 with 3 slot(s)
```

## 已修复

1. **两条检测路径并存** → 旧路径改为**合并**（`Clear()`+`AddRange` 已去掉），并按 musicId 去重
2. **批次无限增长（重大 bug）**：每批都会把同一区前 3 首重新入队，导致「一边跑一边加」，永远不结束（实测跑到 credit 65/65 仍在增长）。现在：
   - `MapHooks.PlayedMissionMusicIds` 记住已打过的课题曲，**同一首永不重复入队**
   - `ConfigData.MaxMissionCredits`（默认 3）限制每次任务追加的课题曲批次数，`AppendMissionBatches` 达到上限即停止
3. **中止后的占位曲**：`Abort()` 现在把当前轨之后的剩余轨**整条替换成 `FillerMusicId`（432 性隐）占位曲**，当前轨跳过且不改成绩

## 已知问题

1. 课题曲批是**额外追加**的批次（`33 → 33+N`），不占用原 98 条的转生批次
2. 续关后第一轨 `GameProcess.OnStart` 空引用（探针显示所有实例字段在正常轨和崩溃轨上完全相同，问题在方法体内部，暂未定位）
