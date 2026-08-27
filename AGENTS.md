# AIRedirector Repository Guidelines

## 仓库与结构

- 本目录是独立 Git 根。父目录提供共享 build targets 和 smoke 项目；`LegendScenarioAnalyzer` 是独立 sibling 仓库，未经明确授权不得修改。
- `Class1.cs` 负责生命周期、配置界面、进程输出路由和 Legend 联动。
- `AIRedirectorConfig.cs` 负责 `PluginData/AIRedirector/settings.json`；`UmaAiProcessStartInfo.cs` 与 `ChildProcessManager.cs` 负责 Windows 子进程。
- `UmaAiRawOutputWorkspace.cs` 负责原始输出，`LegendAiOutputBuffer.cs` 负责解析并修改 Legend display。

## 构建与测试

构建时关闭 manifest、打包和本机部署副作用，并显式传入 Host 项目：

```powershell
dotnet build .\AIRedirector.csproj -c Release -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false -p:UraHostProjectPath=<ura-host-project>
```

行为改动使用父目录中的现有 smoke 入口：

```powershell
dotnet run --project ..\tests\AIRedirectorSmoke\AIRedirectorSmoke.csproj -c Release -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false -p:UraHostProjectPath=<ura-host-project>
dotnet run --project ..\tests\PluginRuntimeSmoke\PluginRuntimeSmoke.csproj -c Release -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false -p:UraHostProjectPath=<ura-host-project> -- AIRedirector
```

## 代码与安全边界

- AIRedirector 仅支持 Windows。用户配置的 exe 路径属于外部输入；启用时必须验证并清晰失败，不猜测路径或静默降级。
- Legend 类型只能在确认 `IsPluginAvailable("LegendScenarioAnalyzer")` 后创建的桥接中使用；不得复制依赖或增加备用加载路径。
- 原始 stdout 先于 Legend 修改发布；保留单消费者顺序、异常传播、有限等待和完整资源清理。
- 不提交运行配置、插件包、捕获数据、凭据或本机路径。
