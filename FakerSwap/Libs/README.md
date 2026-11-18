# Libs（不随仓库发布）

这里需要放**编译引用 DLL**：

- `Package\Sinmai_Data\Managed\*.dll`（Assembly-CSharp、UnityEngine 等）
- `Package\MelonLoader\net35\MelonLoader.dll`、`0Harmony.dll`

这些文件属于游戏本体 / MelonLoader，版权不属于本仓库，已在 `.gitignore` 中排除。

本地准备：

```powershell
$env:MAI_GAME_DIR = "D:\maimai\SDEZ_1.65.00"
pwsh .\tools\fetch-libs.ps1
```

然后 `pwsh .\build.ps1`。
