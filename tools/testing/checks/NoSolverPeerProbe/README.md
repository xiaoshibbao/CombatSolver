# 无求解器对端

独立 headless 测试 Mod，只负责原版 ENet 建局、脚本动作与状态对账，不包含生产求解器。运行时断言已加载 Mod 中没有 CombatSolver。输入由 `tools/testing/run-multiplayer-p0-enet.ps1` 生成，输出为 peer-1 的 result.json 和 environment.json。

```powershell
dotnet build tools/testing/checks/NoSolverPeerProbe/NoSolverPeerProbe.csproj -c Release -p:SteamRoot=<Steam库> -p:OutputPath=<准备目录>/
```

准备目录中把 `NoSolverPeerProbe.dll` 和 `NoSolverPeerProbe.json` 分别复制为 `CombatSolver.dll`、`CombatSolver.json`（仅测试启动器的输入命名），并放入主项目同次构建的 `CombatSolver.MemoryCleaner.exe`。启动器将其安装到独立的 NoSolverPeerProbe Mod 目录。

传入 `-NoSolverPeerBuildDir <准备目录>`，双人模式可验证普通原生动作、房主当前回合自动执行及队友 RNG 交错。所有调用使用 `-CleanupInstanceOnExit`；测试不连接 Steam 房间。完整状态检查不等于玩家可见 UI 或公网延迟验收。
