# T1 报告 — 仓库与四项目骨架

## 1. Status

**DONE_WITH_CONCERNS**

核心目标全部达成：git 仓库初始化、解决方案 `DesktopManager.sln` 创建、4 个项目骨架生成、项目引用建立、整体编译 `Build succeeded` 且 0 error。但有若干与 brief 命令字面值偏离的处理决定（均为 .NET 10 SDK 模板/环境限制所致，处理路径明确且符合 brief 的总目标），详见第 6 节。

## 2. 所有 git commit 的 hash 与 message

| Hash (full) | Hash (short) | Message |
|---|---|---|
| `c8ac081d8018888db6762635fe4d1b7a2413f860` | `c8ac081` | `chore: init repo and solution skeleton` |
| `6008f2ecac520d85b035e7bc4850b0d779a3faa9` | `6008f2e` | `chore: scaffold Core/Native/App/Tests projects` |

## 3. `dotnet build DesktopManager.sln` 最终结果

```
DesktopManager.Core   -> ...\src\DesktopManager.Core\bin\Debug\net10.0\DesktopManager.Core.dll
DesktopManager.Native -> ...\src\DesktopManager.Native\bin\Debug\net10.0-windows\DesktopManager.Native.dll
DesktopManager.Tests  -> ...\src\DesktopManager.Tests\bin\Debug\net10.0\DesktopManager.Tests.dll
DesktopManager.App    -> ...\src\DesktopManager.App\bin\Debug\net10.0-windows10.0.19041.0\DesktopManager.App.dll

已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:07.63
```

**结论：Build succeeded，0 warning，0 error。** 四个项目的输出 DLL 路径中的 TFM 段与 brief 的 Global Constraints 完全一致。

## 4. `git log --oneline` 输出

```
6008f2e chore: scaffold Core/Native/App/Tests projects
c8ac081 chore: init repo and solution skeleton
```

## 5. 四个 csproj 的最终 TargetFramework 值

| 项目 | csproj | TargetFramework | 对齐 brief |
|---|---|---|---|
| Core | `src/DesktopManager.Core/DesktopManager.Core.csproj` | `net10.0` | OK |
| Native | `src/DesktopManager.Native/DesktopManager.Native.csproj` | `net10.0-windows` | OK |
| App | `src/DesktopManager.App/DesktopManager.App.csproj` | `net10.0-windows10.0.19041.0` | OK |
| Tests | `src/DesktopManager.Tests/DesktopManager.Tests.csproj` | `net10.0` | OK |

## 6. 偏离 brief 的决定及原因

### 6.1 Git 身份（本地仓库级占位身份，建议复核）
**现象**：本机 git 全局身份未配置，首次 commit 报 `Author identity unknown`。
**处理**：仅在当前仓库（非 `--global`）配置占位身份：
- name: `DesktopManager Dev`
- email: `dev@desktopmanager.local`

**原因**：brief 未指定 git 身份；用仓库级配置避免污染全局；选择明显是占位符的值便于后续识别和替换。
**建议**：后续若要推到远端，请用真实身份覆盖（`git config user.name ... && git config user.email ...`），或用 `git commit --amend --reset-author` 重写两个 commit 的作者。

### 6.2 `dotnet new sln` 默认生成 `.slnx`，显式指定 `--format sln`
**现象**：.NET 10 SDK（10.0.302）的 `dotnet new sln` 默认输出 `DesktopManager.slnx`（新 XML 解决方案格式），而非 brief 要求的 `DesktopManager.sln`。模板 `-f` 选项默认值为 `slnx`。
**处理**：删除 `.slnx`，改用 `dotnet new sln -n DesktopManager --format sln` 重新生成。
**原因**：brief Files 段明确列出 `DesktopManager.sln`，逐字遵守。

### 6.3 `dotnet new classlib` 不接受 `-f net10.0-windows`（Native 项目）
**现象**：brief Step 1 的命令 `dotnet new classlib -n DesktopManager.Native -o src/DesktopManager.Native -f net10.0-windows` 报错，`classlib` 模板 `-f` 仅接受 `net10.0 / net8.0 / netstandard2.0 / netstandard2.1`，不含平台后缀。
**处理**：改用 `dotnet new classlib -n DesktopManager.Native -o src/DesktopManager.Native -f net10.0` 生成，然后用 Edit 将 csproj 的 `<TargetFramework>` 从 `net10.0` 改为 `net10.0-windows`。
**原因**：与 brief 在 App 上的处理逻辑完全一致（"模板生成若不是目标值，改 csproj 对齐 Global Constraints"）。最终 csproj TFM 满足 brief。

### 6.4 `dotnet new wpf` 不接受 `-f net10.0-windows`（App 项目）
**现象**：brief Step 1 的命令 `dotnet new wpf -n DesktopManager.App -o src/DesktopManager.App -f net10.0-windows` 报错，`wpf` 模板 `-f` 的合法值最高到 `net10.0`（不含 `-windows` 后缀；列表为 net5.0/6/7/8/9/10、netcoreapp3.0/3.1）。
**处理**：改用 `dotnet new wpf -n DesktopManager.App -o src/DesktopManager.App -f net10.0` 生成（此时模板生成的 csproj TFM 为 `net10.0-windows`），然后按 brief 要求用 Edit 改为 `net10.0-windows10.0.19041.0`。
**原因**：brief Step 1 末尾明确要求"App 的 TargetFramework 必须是 net10.0-windows10.0.19041.0"。最终 csproj TFM 满足 brief。

### 6.5 默认 git 分支为 `master`（非 `main`）
**现象**：git 2.55 在本环境下 `git init` 默认分支为 `master`（未配置 `init.defaultBranch`）。
**处理**：未改动。
**原因**：brief 未指定分支名，未强制要求 `main`。如后续约定需要 `main`，可 `git branch -m master main`。

## 7. 其他说明

- 删除了模板生成的占位源文件：`src/DesktopManager.Core/Class1.cs`、`src/DesktopManager.Native/Class1.cs`、`src/DesktopManager.Tests/UnitTest1.cs`。
- App 的 WPF 必需文件（`App.xaml` / `App.xaml.cs` / `MainWindow.xaml` / `MainWindow.xaml.cs` / `AssemblyInfo.cs`）保留，属模板自带、非占位业务代码。
- `dotnet new gitignore` 生成的 `.gitignore` 含 `bin/` `obj/` 等标准忽略项，`git status` 确认构建产物未被跟踪。
- 仓库内还包含 `.superpowers/`（含 brief 与本报告）与 `docs/superpowers/plans/`，这两个目录是 SDD 工作流元数据，已在第一次 commit 一并纳入版本控制。
- 全程未写任何业务代码，仅骨架。
