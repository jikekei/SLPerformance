# 兼容性

- 已验证的专用服务器环境为 SCP:SL 14.2.9、LabAPI 1.1.7.0 和 Harmony 2.4.2.0。
- 插件目标为 .NET Framework 4.8 和 x64，通过 LabAPI 加载。构建时请使用目标服务器版本的程序集。
- 默认检测影响目标方法的 Harmony 补丁，并跳过发生冲突的优化或使用可共存的优化路径。可运行 `slperf status` 查看实际加载情况。
- 两个 Tier 3 实验模块默认关闭。各模块的开关见 [配置示例](outputs/config.example.yml)。
