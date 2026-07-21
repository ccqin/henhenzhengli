# T1 Review — 仓库与四项目骨架

审查对象：commit `c8ac081` + `6008f2e`（两次提交，共 10 个新文件 + sln 更新）。
独立验证：`git log --oneline` 一致；`dotnet build DesktopManager.sln` 实测 `已成功生成 / 0 警告 / 0 错误`；4 个输出 DLL 路径 TFM 段 `net10.0` / `net10.0-windows` / `net10.0` / `net10.0-windows10.0.19041.0` 与 brief Global Constraints 逐字对齐。

---

## 1. Spec compliance: ✅

**无偏差。** 逐项核对：

| Spec 项 | 要求 | 实际 | 结论 |
|---|---|---|---|
| 4 项目齐建 | Core/Native/App/Tests | 四个 csproj 均在 `src/` 下 | OK |
| Core TFM | `net10.0` | `net10.0` | OK |
| Native TFM | `net10.0-windows` | `net10.0-windows` | OK |
| App TFM | `net10.0-windows10.0.19041.0` | `net10.0-windows10.0.19041.0` | OK |
| Tests TFM | `net10.0` | `net10.0` | OK |
| 引用 Native→Core | 必须有 | `Native.csproj` 有 ProjectReference Core | OK |
| 引用 App→Core | 必须有 | `App.csproj` 有 ProjectReference Core | OK |
| 引用 App→Native | 必须有 | `App.csproj` 有 ProjectReference Native | OK |
| 引用 Tests→Core | 必须有 | `Tests.csproj` 有 ProjectReference Core | OK |
| build | 0 error | 实测 0 error 0 warning | OK |
| sln 格式 | `.sln`（非 `.slnx`） | `DesktopManager.sln`，文件头 `Microsoft Visual Studio Solution File, Format Version 12.00` | OK |
| 无业务代码 | 骨架仅 | 仅模板自带源文件，无任何业务类型/服务 | OK |
| 占位文件清理 | 删 Class1.cs/UnitTest1.cs | 四个项目目录中均无占位 cs 文件 | OK |
| .gitignore 含 bin//obj | 必须 | `[Bb]in/` `[Oo]bj/` 均在（line 63-64） | OK |

**关于 brief 命令字面值偏离（report 6.1–6.5）**：全部为 .NET 10 SDK 模板限制或环境因素所致，处理路径与 brief 总目标一致，不构成 spec 偏差：
- 6.2 `.slnx`→`--format sln`：对齐 brief Files 段。
- 6.3 `classlib -f net10.0-windows` 不支持→改 csproj：最终 TFM 精确达标。
- 6.4 `wpf -f net10.0-windows` 不支持→改 csproj：brief Step 1 末尾明确要求此处理。
- 6.1 git 仓库级占位身份、6.5 默认分支 `master`：brief 未指定，留给后续决定，合理。

**缺失项**：无。
**多余项**：无（App 的 5 个 WPF 模板文件为 WPF 项目骨架必需，非业务代码）。

---

## 2. Code quality: Approved（含若干 Minor）

### Critical
无。

### Important
无。

### Minor

1. **`src/DesktopManager.App/DesktopManager.App.csproj`（整个 App.csproj，line 1-16）** — `ItemGroup` 写在 `PropertyGroup` 之前。SDK 风格 csproj 虽允许任意顺序，但 MSBuild 惯例是 `PropertyGroup` 在前。**建议**：调整为 `PropertyGroup` 在上、`ItemGroup` 在下，与 Native.csproj（PropertyGroup 在上）保持一致。模板原样，非阻塞。

2. **`src/DesktopManager.App/App.xaml.cs:1-2`** — `using System.Configuration;` 与 `using System.Data;` 均未在文件内使用（仅 `System.Windows` 被引用）。**建议**：删除两行未用 using。WPF 模板原样。

3. **`src/DesktopManager.App/MainWindow.xaml.cs:1-10`** — 10 行 using 中实际只用到 `System.Windows`（基类 `Window`）。其余 9 行（`System.Text` / `System.Windows.Controls` 等）均未使用。**建议**：精简到 `using System.Windows;`。WPF 模板原样。

4. **`src/DesktopManager.App/MainWindow.xaml.cs:23`** — 文件末尾无换行（patch 标注 `\ No newline at end of file`）。**建议**：补一个尾换行。

5. **`src/DesktopManager.Tests/DesktopManager.Tests.csproj:25`** — 同样无尾换行（patch 标注 `\ No newline at end of file`）。**建议**：补尾换行。

6. **`src/DesktopManager.App/App.xaml:6-8`** — `<Application.Resources>` 内有一行无意义空白（带前导空格的空行）。**建议**：清成空标签 `<Application.Resources/>` 或空行干净。WPF 模板原样。

7. **`src/DesktopManager.App/App.xaml:4`** — `StartupUri="MainWindow.xaml"` 保留会使 App 启动时弹出 MainWindow。骨架阶段尚可接受，但与 M0.7「托盘常驻无主窗口」冲突，到 M0.7 必须改。**建议**：仅作为提醒，不在本任务改（YAGNI）。

### 关于 NuGet 包
- `Core.csproj`：无包。OK。
- `Native.csproj`：无包。OK。
- `App.csproj`：无包（仅 ProjectReference）。OK，符合 YAGNI（`H.NotifyIcon.Wpf` 等 M0.7 才引入）。
- `Tests.csproj`：`coverlet.collector` / `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio` 四个包，全部为 `dotnet new xunit` 模板默认产出，符合 brief 使用 xunit 模板的要求。OK。

### 关于 WPF 模板文件保留
`App.xaml` / `App.xaml.cs` / `MainWindow.xaml` / `MainWindow.xaml.cs` / `AssemblyInfo.cs` 五个文件是 `dotnet new wpf` 的默认产出，是 WPF 项目编译与运行的必需骨架（`AssemblyInfo.cs` 的 `ThemeInfo` 是 WPF 主题资源解析必需；`App.xaml` 是 Application 定义；`MainWindow` 是 `StartupUri` 指向的入口窗口）。**保留合理，不算业务代码，不算残留占位**。

### 关于 .gitignore
`dotnet new gitignore` 标准产出，含 `[Bb]in/` `[Oo]bj/` `*.user` `.vs/` `artifacts/` 等，覆盖 .NET/WPF/VS/Rider/VSCode/macOS/Windows 全栈。OK。

---

## 3. 总评

骨架搭建完全达标——4 项目/4 TFM/4 引用/build 0 error/sln 格式/无业务代码/占位清理全部精确对齐 brief，所有偏离命令字面值的处理均有据可查且符合 brief 总目标；代码层面仅余若干 WPF 模板自带的洁癖级 Minor，不影响功能与后续任务推进。
