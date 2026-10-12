# Wine 本地初步验收（2026-10-12）

结论：当前发布包在本机 Wine 10.0 下未通过启动冒烟测试，无法继续界面交互验收。此结果不能直接归因于应用代码，也不能替代 Windows 11 原生验收。

## 环境与执行

- 使用 Ubuntu 软件源的 Wine `10.0~repack-12ubuntu1`，无需 sudo，解压至 `artifacts/wine-smoke/runtime`。
- 独立 WINEPREFIX：`artifacts/wine-smoke/prefix`；未使用用户默认 Wine 环境。
- 测试对象：`artifacts/FontLite-win-x64/FontLite.App.exe` 自包含发布包，运行日志报告 .NET 10.0.12。
- 修正解压环境的 Wine NLS 查找路径，补齐 x64/x86 zlib1.dll；保留 Wine mscoree 以支持托管程序集加载。
- 首次有效运行后，再复制本机 TTF/OTF/TTC 与 Wine 自带 TTF 到隔离前缀的 Windows Fonts 目录复测。

## 观察结果

| 项目 | 结果 |
|---|---|
| Wine 启动与 .NET 托管代码加载 | 已进入应用启动及 WPF 执行路径 |
| SQLite 初始化 | 独立前缀内生成 library.db、WAL 与 SHM |
| WPF 窗口稳定显示 | 未通过，文本排版阶段进程 FailFast |
| 补充本机字体后复测 | 同一崩溃再次出现 |
| 导航、布局、主题、扫描、预览与字体操作 | 未验收 |
| Word 兼容与 Windows 性能 | 未验收 |

关键堆栈：`MS.Internal.Invariant.FailFast` → `MS.Internal.Shaping.TypefaceMap.MapUnresolvedCharacters`。日志同时出现 Wine DirectWrite 与 Windows UISettings 未实现接口提示；这些提示与崩溃的因果关系尚未确认。

证据文件：

- `artifacts/wine-smoke/logs/startup-managed.log`：修正运行时后的启动日志。
- `artifacts/wine-smoke/logs/startup-fonts.log`：补充字体后的复测日志。
- `artifacts/wine-smoke/logs/paths.log`：Wine NLS 路径排查。

复现：在项目目录运行 `bash tools/run-wine-smoke.sh`。脚本依赖本次解压的本地 Wine 环境，不自动下载依赖。

下一步应在 Windows 11 x64 上按 `docs/VALIDATION.zh-CN.md` 执行原生验收，判断同一发布包是否能正常显示窗口；若继续研究 Wine 兼容，应单独定位最小 WPF 文本排版复现，不能把 Wine 下失败直接当作 Windows 产品缺陷。

此记录对应旧版 FontLite。运行脚本现已更新为 FontCrate 发布包；本文不代表新版已经通过 Wine 或 Windows 原生验收。
