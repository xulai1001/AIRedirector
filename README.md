# AIRedirector

`AIRedirector` 是 URA 的 Windows 插件，启动为 UAF、Cook、Mecha 或 Legend 配置的 `UmaAI.exe`，并在 `AIRedirector` workspace 显示子进程的 UTF-8 stdout。每个进程以其 exe 所在目录作为工作目录。

## 配置

配置界面使用 Host 提供的 Terminal.Gui `IApplication`。场景开关控制对应路径项；文件选择器只接受现有 exe。只有“保存”会将草稿写入 `PluginData/AIRedirector/settings.json`，取消或关闭界面不会写入配置。启用场景的程序路径无效时，插件初始化失败并报告路径。

## Legend 联动

manifest 将 `LegendScenarioAnalyzer` 声明为软联动。共享插件上下文中存在 Legend 时，AIRedirector 将训练评分与可定位的训练建议追加到训练卡，将心得候选评分与可定位建议追加到心得卡；行动评分、汇总和无法定位到卡片的普通建议写入非空时标题为 `AI` 的 Extra section，重要行动建议写入 Important。解析到可展示输出后会刷新对应训练记录。缺少 Legend 时，配置、子进程和原始输出 workspace 可独立运行。

## 构建

仓库通过 Git submodule 固定 Host 与 Legend 源码。克隆后在仓库根执行：

```powershell
git -c core.longpaths=true submodule update --init --recursive
dotnet build .\AIRedirector.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
$uraHostProject = (Resolve-Path .\deps\UmamusumeResponseAnalyzer\UmamusumeResponseAnalyzer\UmamusumeResponseAnalyzer.csproj).Path
dotnet run --project .\tests\AIRedirectorSmoke\AIRedirectorSmoke.csproj -c Release -p:UraHostProjectPath="$uraHostProject" -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```
