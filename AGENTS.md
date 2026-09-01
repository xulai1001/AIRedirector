# AIRedirector Repository Guidelines

## 仓库与结构

- 本目录是独立 Git 根；Host API 与构建契约来自 `UmamusumeResponseAnalyzer` NuGet 包，`deps/LegendScenarioAnalyzer` 固定联动源码。Host-dependent smoke 位于 `URA-Plugins.Integration/tests/AIRedirectorSmoke`。
- `Class1.cs` 负责生命周期、配置界面、进程输出路由和 Legend 联动。
- `AIRedirectorConfig.cs` 负责 `PluginData/AIRedirector/settings.json`；`UmaAiProcessStartInfo.cs` 与 `ChildProcessManager.cs` 负责 Windows 子进程。
- `UmaAiRawOutputWorkspace.cs` 负责原始输出，`LegendAiOutputBuffer.cs` 负责解析并修改 Legend display。

## 构建与测试

克隆后初始化源码依赖；Release 构建生成插件包但不部署到本机：

```powershell
git -c core.longpaths=true submodule update --init --recursive
dotnet build .\AIRedirector.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

行为改动运行 Integration 中同时引用 Host、AIR 与 Legend 的 `AIRedirectorSmoke`。

## 代码与安全边界

- AIRedirector 仅支持 Windows。用户配置的 exe 路径属于外部输入；启用时必须验证并清晰失败，不猜测路径或静默降级。
- Legend 类型只能在确认 `IsPluginAvailable("LegendScenarioAnalyzer")` 后创建的桥接中使用；不得复制依赖或增加备用加载路径。
- 原始 stdout 先于 Legend 修改发布；保留单消费者顺序、异常传播、有限等待和完整资源清理。
- 不提交运行配置、插件包、捕获数据、凭据或本机路径。
