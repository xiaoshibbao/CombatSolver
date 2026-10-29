# 本地多人适配版

基线：主线 `5c773caa`（manifest 0.50.1）与上游多人实现 `14b36a28`。适用游戏 0.111.0，依赖 STS2-RitsuLib。这里的 0.50.2 是本地构建，不表示作者发布了新版本。

## 使用

支持 2～4 人，房主和加入者均可在自己的出牌阶段计算。打开求解器，按需要选择输出、防守或启动方案，再执行当前回合。多人设置独立控制搜索回合数和时间；每个新本地回合建立新根，不把旧计划跨回合自动执行。

队友无需安装求解器。所有真实动作走游戏原版动作队列和同步器；不会替队友出牌、用药或选牌。需要队友选择的原生动作等待对方完成。

预测假定队友不再主动出牌或用药，但完整模拟已存在的队友能力、遗物、球、伙伴及回合效果。它无法预知队友接下来的人类决策。队友改变战场后，从新状态重评估剩余原动作；仍合法则继续，失效目标暂停。共享 RNG 偏差会提示，不能把旧数值继续当成精确预测。

## 实现范围

- 主线程捕获所有玩家的根状态，后台分支维护各自资源、有序牌堆、能力、球、伙伴、历史和九条 RNG；Fork 重映射跨玩家引用。
- 多人专用卡和相关能力使用模拟引擎中的真实语义，合法队友目标绑定稳定生物身份。生成牌、转移牌与跨玩家触发保留所有权。
- 搜索只枚举本地动作，最多提供三类有实际差异的方案。纯支援在本地方案余费中考虑；群体或本地收益正常搜索。
- 敌方群体行动逐玩家结算。额外回合按当前参与者分别判定；只有队友获得额外回合时推进队友被动与原生回合流程，返回本地回合后才枚举本地动作。
- 保留主线的额外回合镜像登记、回合末时序及后续搜索修复，不回退到多人分支的旧整套 DLL。

## 可重复验证

所有命令在源码根运行。Windows 使用 PowerShell 7.4 以上及 .NET 9，先用 `local.props` 配置 `SteamRoot`。不使用个人目录作为测试实例根；所有游戏副本位于 `.local/headless-instances/`，命令负责退出与清理。

```powershell
dotnet build -c Release -p:CopyModOnBuild=false
pwsh -NoProfile -File tools/testing/run-multiplayer-p0-enet.ps1 -PlayerCount 4 -Port 33774 -Sts2GameRoot <游戏目录> -RitsuWorkshopRoot <Ritsu目录>
pwsh -NoProfile -File tools/testing/run-multiplayer-p0-enet.ps1 -VerifyPaelsEyeExtraTurn -VerifyRemoteExtraTurn -Sts2GameRoot <游戏目录> -RitsuWorkshopRoot <Ritsu目录>
```

无求解器对端使用 `tools/testing/checks/NoSolverPeerProbe/` 中的独立测试 Mod；它不包含生产求解代码，并断言 CombatSolver 没有加载。将其构建目录按该工具 README 准备后，传入 `-NoSolverPeerBuildDir`，可配合 `-VerifyControllerFullAuto` 或 `-VerifyControllerRngDrift`。

Linux 对应入口为 `tools/testing/run-multiplayer-p0-enet.sh`，第三个参数可选 `scripted`、`full-auto`、`rng-drift`、`paels-eye`、`paels-eye-both`、`remote-extra`。本轮没有 Linux 游戏运行证据。

## 验证记录

本轮原始结果保存于 `.local/multiplayer-validation/` 和 `.local/multiplayer-p0/`。历史内容清单见 [原版多人内容](MULTIPLAYER_CONTENT_INVENTORY.md)，其历史 Passed 不能代替本轮结果。

2026-10-29 本轮实际结果：

| 场景 | 结果与证据目录（相对 `.local/`） |
| --- | --- |
| 双人根、Fork、双方普通牌、原生选牌与跨回合完整差分 | Passed；`multiplayer-validation/virtual/peer-0` |
| 四人 ENet：房主和三个加入者，各座位动作及全玩家/RNG 对账 | 四端 Passed；`multiplayer-p0/enet-4-3aa0f3a967e348558fed5d83d728ed94` |
| 队友独占额外回合、本地行动门禁与返回本地回合的完整差分 | 双端 Passed；`multiplayer-p0/enet-2-f209e44fc78d4f5dadc5264d3276ea07` |
| 队友未加载 CombatSolver，房主通过生产控制器自动执行当前回合 | 双端 Passed；`multiplayer-p0/enet-2-b1d3289d1ff140598d2649a6ee363337`；对端 `environment.json` 明确 `solverLoaded=false` |
| 房主和加入者分别使用生产求解与自动执行 | 双端 Passed；`multiplayer-p0/enet-2-8e96de68a2d749208b9af9ed9e12cba6`；双方均执行本地方案并在第二回合全状态/RNG 对齐 |
| 无求解器队友在本地两次攻击之间打出 Largesse，改变共享 RNG | 双端 Passed；`multiplayer-p0/enet-2-16b8686429a74f9e94016c9827aee39a`；生成牌归属、RNG 提示、剩余动作重评估与继续执行，不增加完整搜索 |
| 四人跨玩家内容：BLAZE、RALLY、LARGESSE、IMITATION_LEARNING、OUTRAGE，目标为第4座位 | 基础/升级均 Passed；`multiplayer-validation/cards4`、`multiplayer-validation/cards4-upgraded`；逐动作全玩家/RNG 对账 |
| 搜索保留本地攻击，再用余费执行两张支援牌 | Passed；`multiplayer-validation/support-search`；真实搜索、固定合法目标及原生路线完整差分 |
| 单人回合末自动出牌先于额外回合资格判定 | Passed；`multiplayer-validation/singleplayer-order`；`PAELS-EYE-AUTOPOST-ORDER` 原生/Fork 全状态哨兵 |

Release 构建零警告、零错误；PowerShell/Bash 结构门禁通过。CoverageCatalog 的有效实现、战斗选择与阵容来源专项验证通过，并更新生成目录。目录仍保留一项历史非通过证据和 InfusedCore 的运行证据缺口；未把专项通过写成全内容运行验收。

失败尝试同样保留：一次额外回合测试曾错误等待客户端请求对象而超时，修正为观察原生回合后双端通过；四进程曾因1536 MiB预约及并行批次导致准入互等，按实测约1240 MiB工作集改用每端1408 MiB并独立运行后四端通过，未延长120秒请求超时。早期双端战斗通过但清理遇DLL映射释放延迟，启动器现保留所有权校验并有限重试。旧虚拟RNG干扰夹具先被单敌输入拒绝，改为双敌后仍因路线未满足“两次攻击同一敌人”的夹具前置条件退出；该夹具未通过，不视作续行行为的验证结果。

Steam 房间、公网延迟、所有角色与卡牌组合及可见界面布局需额外实机验收。本机 ENet 证明原生网络动作和同步，不等于 Steam 公网已验收。
