# DesktopManager

Windows 桌面图标管理 + 动态壁纸结合体工具。接管 explorer 桌面图标显示，自绘图标层提供收纳盒/分组管理；壁纸层支持静态图与视频/GIF，支持多显示器与跨屏组合。目标以 MSIX + runFullTrust 形式上架微软商店。

**技术栈**：C# / WPF / .NET 10 / MSIX / xUnit / Win32 P/Invoke。

## 现状

| 里程碑 | 状态 | tag |
|---|---|---|
| M0 骨架 + 三件套 spike + 托盘常驻 | ✅ 完成 | `m0-skeleton` |
| M1 桌面接管 + 图标镜像核心 | ✅ 完成（待真机验收） | `m1-desktop-takeover` |
| M2 收纳盒/分组 + 双击隐藏 | 🚧 计划中 | — |
| M3 多屏 / M4 壁纸层 / M5 跨屏 / M6 商店化 | 📋 路线图 | — |

详见 `docs/superpowers/plans/`。

## 环境要求

- **.NET 10 SDK**（开发机已验证 10.0.302）
- **Windows 10 1809+**（主测 Windows 11）

## 构建与运行

```bash
# 在项目根目录执行
dotnet build DesktopManager.sln

# 运行（会先 build，再启动 app）
dotnet run --project src/DesktopManager.App
```

**用 Visual Studio 调试**：打开 `DesktopManager.sln`，把 `DesktopManager.App` 设为启动项目，F5 运行（可在 Output 窗口看 `Debug.WriteLine` 日志、设断点）。

## 测试

```bash
dotnet test DesktopManager.sln
```

## 项目结构

```
src/
├── DesktopManager.Core/    # 纯逻辑（可单测），net10.0：Models / Services（IconItem、DesktopSnapshot、DesktopDiff、DesktopSync、RecoveryStateDetector、ConfigStore）
├── DesktopManager.Native/  # Win32 P/Invoke 封装，net10.0-windows：DesktopIconVisibility、MonitorEnumerator、WindowInterop、IconExtractorNative
├── DesktopManager.App/     # WPF 主程序，net10.0-windows10.0.19041.0：App（托盘）、Windows（WallpaperWindow、IconLayerWindow）、Services（IconExtractor）、RecoveryGuard、ShellRestartWatcher
└── DesktopManager.Tests/   # xUnit 单测，net10.0
```

**三层架构**：壁纸播放窗口（最底）→ 图标层窗口（中间）→ 设置 UI（最上）。Core 可测、Native 仅封装、App 组合 UI，分层单向依赖。

## ⚠️ 真机验收注意（M1）

M1 启动后**会接管 explorer 桌面图标**（隐藏原生图标、由 app 的图标层接管显示），正常托盘退出会恢复。验收清单：

1. 启动后 explorer 原生桌面图标消失，图标层显示真实桌面图标
2. 双击图标 → 关联程序打开
3. 往桌面新建/删除文件 → ≤3s 图标层同步
4. 任务管理器重启 explorer.exe → 仍接管（原生图标不回来）
5. 托盘→退出 → 原生桌面图标恢复

**如果 app 异常退出导致桌面图标没恢复**，临时手动恢复（M2 会加自清理机制彻底解决）：

```bash
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideIcons /t REG_DWORD /d 0 /f
```

## 文档

- 总计划与里程碑路线图：`docs/superpowers/plans/2026-07-21-desktop-manager.md`
- M1 实现计划：`docs/superpowers/plans/2026-07-22-m1-desktop-takeover.md`
- M0 spike 结论与验收：`docs/superpowers/notes/m0-spike-results.md`
