# Subagent-Driven Development 进度账本

计划：docs/superpowers/plans/2026-07-21-desktop-manager.md
仓库：d:\15.ai\狠狠整理（默认分支 master）

## M0 任务单元
- [x] T1 (M0.1+M0.2): complete (commits c8ac081..6008f2e, review clean, Spec ✅ / Quality Approved)
- [ ] T2 (M0.3): ConfigStore 配置模型 TDD — 执行中
- [ ] T3 (M0.4+M0.5+M0.6): spike 三件套
- [ ] T4 (M0.7): 托盘常驻空壳
- [ ] T5 (M0.8): 冒烟总验 + spike 验收清单交付（需用户真机确认）

## Minor findings 滚存（留给最终 whole-branch review triage）
- T1: 7 条，均 WPF 模板洁癖级（未用 using / 文件无尾换行 / App.xaml 空 Resources / ItemGroup 顺序等），不影响功能。详见 t1-review.md。

## 计划修正（对后续 brief 生效）
- **.NET 10 SDK 的 `dotnet new` 模板 `-f` 不吃平台后缀**（net10.0-windows 报错）。后续生成 Windows 项目一律：`dotnet new <tpl> -f net10.0` 生成，再 Edit csproj 改 TargetFramework。T3/T4 brief 据此调整。

## 待处理（非阻塞）
- git 身份是仓库级占位 `dev@desktopmanager.local`，推远端前用真实身份覆盖。
- 默认分支 master，如需 main：`git branch -m master main`。

## 验收归人（无法自动化）
- M0.4: 运行后观察桌面图标隐藏/恢复
- M0.5: 多屏机上打印显示器枚举结果
- M0.6: 运行 WallpaperWindow 观察置底+点击穿透
- M0.7: 运行 app 观察托盘出现、退出正常
