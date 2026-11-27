# SLPerformance

SLPerformance 是面向 **SCP: Secret Laboratory（SCP:SL）专用服务器**的 LabAPI 性能优化插件。它提供 22 个可独立启停的优化模块，用于降低主线程负载、减少托管内存分配，并缓解批量切换角色、波次增援和真人加入时的帧时间尖峰。

## 功能

- 优化 Mirror 和 LiteNetLib 的网络处理、移动同步、语音批处理，以及地图实体、动画和状态效果的更新。
- 为批量角色切换和波次增援预热角色模型和物品。将部分玩家加入时的认证计算移到后台。
- 提供 TPS、帧时间、CPU、GC 和网络统计，可在运行时查看模块状态并单独切换模块。
- 默认检测其他插件的 Harmony 补丁，并跳过检测到冲突的模块。两个 Tier 3 实验模块默认关闭。

## TPS 与性能实测

**48/48 稳态压测达到 60.00 TPS，主线程平均工作耗时降低 39.4%。批量切角色时，角色初始化耗时降至原来的约 1/5.4。**

| 实服场景 | 指标 | 实测结果 |
| --- | --- | --- |
| 48/48 稳态负载，目标 60 TPS | 平均 TPS | **~30 → 60.00 TPS（+50+%）** |
| 48/48 稳态负载 | 主线程平均工作耗时 | **11.77 → 7.13 ms（−39.4%）** |
| 48/48 稳态负载 | 每帧 Mono 内存分配 | **108.6 → 20.0 KB（−81.6%）** |
| 48/48 冷开局，关闭 OPT-21 与启用 v3 对比 | 最长单帧耗时 | **365–427 → 224.60 ms（约 −38% 至 −47%）** |
| 48 次同模板角色切换 | 角色初始化耗时 | **61.65 → 11.42 ms（−81.5%，约 5.4 倍加速）** |
| 预热后的真人加入，目标 120 TPS | 加入期间观测 TPS | **119.8–119.9 TPS**，最大帧 10.28–10.55 ms |

48/48 稳态数据比较了两个已启用 SLPerformance 的配置，压测包含 47 个模拟玩家。在 60 TPS 目标下，主线程耗时的下降为突发负载留出了更多帧内处理时间。120 TPS 场景记录了优化启用后的读数，尚缺同场景关闭优化的 TPS 对照。测试条件与逐轮数据见 [性能基准](BENCHMARKS.md)和[实服验证记录](PROGRESS.md)。

离线微基准中，移动增量同步快照构建加速 **5.02 倍**，Mirror 脏广播扫描加速 **17.24 倍**。这些倍数按单项操作耗时计算，详见 [离线微基准](BENCHMARKS.md)。

## 安装

已验证环境为 **SCP:SL 14.2.9、LabAPI 1.1.7.0 和 Harmony 2.4.2.0**。其他版本的兼容情况见 [兼容性说明](COMPATIBILITY.md)。

1. 将 [SLPerformance.dll](outputs/SLPerformance.dll) 放入 `LabAPI/plugins/global/`，或放入目标端口对应的插件目录。
2. 确认 `LabAPI/dependencies/global/` 或目标端口的依赖目录中已有 `0Harmony.dll`。
3. 启动服务器，在服务器控制台运行 `slperf status` 确认模块状态。首次启动会生成配置，可对照 [配置示例](outputs/config.example.yml) 调整模块开关。

## 管理命令

| 命令 | 用途 |
| --- | --- |
| `slperf status` | 查看版本兼容情况和各模块状态 |
| `slperf stats` | 查看 TPS、帧时间、CPU、GC 和网络统计 |
| `slperf roleburst` | 查看增援和批量角色切换耗时 |
| `slperf joinstats` | 查看真人玩家加入和认证耗时 |
| `slperf toggle <moduleId>` | 在运行时启用或停用指定模块 |

直接运行 `slperf` 可查看完整命令列表。

## 从源码构建

插件目标为 `.NET Framework 4.8`（x64），测试和微基准项目目标为 `.NET 8`。在 Windows 上安装 .NET 8 或更新的 SDK，以及 .NET Framework 4.8 Developer Pack。将以下两个参数分别指向本机 SCP:SL 14.2.9 专用服务器的 `SCPSL_Data\Managed` 目录和 Harmony DLL，然后在仓库根目录运行：

```powershell
dotnet build src/SLPerformance.sln -c Release `
  '-p:ScpslManagedDir=C:\path\to\SCPSL_Data\Managed' `
  '-p:HarmonyDllPath=C:\path\to\0Harmony.dll'
```

插件构建产物位于 `src/SLPerformance/bin/Release/net48/SLPerformance.dll`。模块设计和实现细节见 [系统架构](ARCHITECTURE.md)和[优化方案](OPTIMIZATIONS.md)。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
