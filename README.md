# AIRedirector

`AIRedirector` 启动配置的 UmaAI 子进程，并在 `AIRedirector` workspace 显示 `UmaAI.exe` stdout 原始行。

配置 Dialog 使用 Host 传入的同一 `IApplication`。纵向 `Menu` 使用 `CheckBox` 开关各场景；只有启用的场景显示路径项，路径通过 Terminal.Gui `OpenDialog` FilePicker 选择。字段先写入 draft，只有选择“保存”才持久化到 `PluginData/AIRedirector/settings.json`；“取消”、Esc、关闭 Dialog 或 cancellation 均不写配置。

UmaAI 子进程 stdout 按 UTF-8 读取，对应 `chcp 65001` 输出。

UmaAI 子进程的工作目录设置为对应 exe 文件所在目录。

manifest 中的 `LegendScenarioAnalyzer` 是软联动声明。Legend 在本轮共享插件上下文可用时，AIRedirector 注册常驻 display 修改器并在 AI 输出变化后原位刷新 Legend 面板；Legend 缺失时，配置、子进程和 AIRedirector 原始输出 workspace 仍可独立工作。
