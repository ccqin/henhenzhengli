# DesktopManager

Windows 桌面图标管理 + 动态壁纸结合体工具。接管 explorer 桌面图标显示，自绘图标层提供收纳盒/分组管理；壁纸层支持静态图与视频/GIF，支持多显示器与跨屏组合。目标以 MSIX + runFullTrust 形式上架微软商店。

**技术栈**：C# / WPF / .NET 10 / MSIX / xUnit / Win32 P/Invoke。

## 现状

| 里程碑 | 状态 | tag |
|---|---|---|
| M0 骨架 + 三件套 spike + 托盘常驻 | ✅ 完成 | `m0-skeleton` |
| M1 桌面接管 + 图标镜像核心 | ✅ 完成（真机验收通过） | `m1-desktop-takeover` |
| M2 收纳盒/分组 + 双击隐藏 | ✅ 完成（真机验收通过） | `m2-fences` |
| M3 多屏（图标层） | ✅ 完成（双屏真机验收通过） | `m3-multimon` |
| M4 壁纸层（单屏×每屏） | ✅ 完成（双屏真机验收通过） | `m4-wallpaper` |
| M5 跨屏壁纸 / M6 商店化 | 📋 路线图 | — |

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

## ⚠️ 真机验收注意

启动后**会接管 explorer 桌面图标**（隐藏原生图标、由 app 的图标层接管显示），正常托盘退出会恢复。
Win11 真机验收结论（已通过）：原生图标立即隐藏（直接隐藏 SysListView32）、图标层在文件夹窗口下层（桌面层 Z-order）、重启 explorer 只重启一次且继续接管（单实例 Mutex 断环）。

**如果 app 异常退出导致桌面图标没恢复**（RunOnce 自清理会在下次登录自动修复，也可手动）：

```bash
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce" /v DM_RestoreIcons /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideIcons /t REG_DWORD /d 0 /f
```

**如果 app 异常退出导致桌面图标没恢复**，临时手动恢复（M2 会加自清理机制彻底解决）：

```bash
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideIcons /t REG_DWORD /d 0 /f
```

## 文档

- 总计划与里程碑路线图：`docs/superpowers/plans/2026-07-21-desktop-manager.md`
- M1 实现计划：`docs/superpowers/plans/2026-07-22-m1-desktop-takeover.md`
- M2 实现计划：`docs/superpowers/plans/2026-07-22-m2-fences-hideicons.md`
- M3 多屏实现计划：`docs/superpowers/plans/2026-08-04-m3-multi-monitor.md`
- M4 壁纸层实现计划：`docs/superpowers/plans/2026-08-04-m4-wallpaper.md`
- M5 跨屏壁纸+设置窗口计划：`docs/superpowers/plans/2026-08-05-m5-crossscreen.md`
- M0 spike 结论与验收：`docs/superpowers/notes/m0-spike-results.md`
