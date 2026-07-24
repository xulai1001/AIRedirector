# AIRedirector

`AIRedirector` 启动配置的 UmaAI 子进程，并在 `AIRedirector` workspace 显示 `UmaAI.exe` stdout 原始行。

配置 Dialog 使用 Host 传入的同一 `IApplication`。字段先写入 draft，只有选择“保存”才持久化到 `PluginData/AIRedirector/settings.json`；“取消”、Esc、关闭 Dialog 或 cancellation 均不写配置。

UmaAI 子进程 stdout 按 UTF-8 读取，对应 `chcp 65001` 输出。

UmaAI 子进程的工作目录设置为对应 exe 文件所在目录。
