# T1 Brief — 仓库与四项目骨架（合并 M0.1 + M0.2）

你在 `DesktopManager` 项目（一个 Windows 桌面图标管理+壁纸工具，WPF/.NET 10/MSIX）的 M0 阶段。这是整个项目的第一个实现单元：初始化 git 仓库、建解决方案、用 dotnet 模板生成 4 个项目、建立项目引用、整体编译通过。本任务**只搭骨架**，不写业务代码。

## Global Constraints（必须遵守）
- .NET 10 SDK（已装 10.0.302）。
- 框架：Core/Tests 用 `net10.0`；Native 用 `net10.0-windows`；App 用 `net10.0-windows10.0.19041.0`（WPF）。
- 解决方案名 `DesktopManager`；项目 `DesktopManager.Core` / `.Native` / `.App` / `.Tests`。
- 工作目录：`d:\15.ai\狠狠整理`（当前为空目录，尚非 git 仓库）。

## Task M0.1 — Git 初始化与解决方案骨架

Files:
- Create: `d:\15.ai\狠狠整理\.gitignore`
- Create: `d:\15.ai\狠狠整理\DesktopManager.sln`

Step 1: 初始化仓库与 .gitignore
```bash
git init
dotnet new gitignore
```
Expected: `.gitignore` 生成（含 bin/obj 等）。

Step 2: 创建解决方案
```bash
dotnet new sln -n DesktopManager
```
Expected: `DesktopManager.sln` 生成。

Step 3: 提交基线
```bash
git add -A
git commit -m "chore: init repo and solution skeleton"
```

## Task M0.2 — 四个项目骨架与引用关系

Files:
- Create: `src/DesktopManager.Core/DesktopManager.Core.csproj`
- Create: `src/DesktopManager.Native/DesktopManager.Native.csproj`
- Create: `src/DesktopManager.App/DesktopManager.App.csproj`
- Create: `src/DesktopManager.Tests/DesktopManager.Tests.csproj`

Step 1: 用模板生成四个项目（注意 App 的框架对齐 Global Constraints）
```bash
dotnet new classlib -n DesktopManager.Core   -o src/DesktopManager.Core   -f net10.0
dotnet new classlib -n DesktopManager.Native -o src/DesktopManager.Native -f net10.0-windows
dotnet new wpf     -n DesktopManager.App    -o src/DesktopManager.App    -f net10.0-windows
dotnet new xunit   -n DesktopManager.Tests  -o src/DesktopManager.Tests  -f net10.0
```
- 删除各项目模板生成的默认占位文件（Class1.cs / UnitTest1.cs 等）。
- 模板生成的 App.csproj 的 TargetFramework 若不是 `net10.0-windows10.0.19041.0`，改为此值（对齐 Global Constraints）。
- 若 `dotnet new wpf` 别名不可用，先 `dotnet new list wpf` 确认。

Step 2: 加入解决方案
```bash
dotnet sln add src/DesktopManager.Core/DesktopManager.Core.csproj
dotnet sln add src/DesktopManager.Native/DesktopManager.Native.csproj
dotnet sln add src/DesktopManager.App/DesktopManager.App.csproj
dotnet sln add src/DesktopManager.Tests/DesktopManager.Tests.csproj
```

Step 3: 建立项目引用
```bash
dotnet add src/DesktopManager.Native/DesktopManager.Native.csproj reference src/DesktopManager.Core/DesktopManager.Core.csproj
dotnet add src/DesktopManager.App/DesktopManager.App.csproj reference src/DesktopManager.Core/DesktopManager.Core.csproj
dotnet add src/DesktopManager.App/DesktopManager.App.csproj reference src/DesktopManager.Native/DesktopManager.Native.csproj
dotnet add src/DesktopManager.Tests/DesktopManager.Tests.csproj reference src/DesktopManager.Core/DesktopManager.Core.csproj
```

Step 4: 还原并整体编译
```bash
dotnet build DesktopManager.sln
```
Expected: `Build succeeded`，0 error。

Step 5: 提交
```bash
git add -A
git commit -m "chore: scaffold Core/Native/App/Tests projects"
```

## 完成后报告要求
把完整报告写到 `d:\15.ai\狠狠整理\.superpowers\sdd\task-t1-report.md`，包含：
1. Status: DONE / DONE_WITH_CONCERNS / NEEDS_CONTEXT / BLOCKED
2. 所有 git commit 的 hash 与 message
3. `dotnet build` 的最终结果（succeeded + 0 error 的证据）
4. `git log --oneline` 输出
5. 四个 csproj 的最终 TargetFramework 值
6. 任何偏离 brief 的决定及原因
返回给我的消息只需：status、commit 列表、一句话编译结果、concerns（如有）。
