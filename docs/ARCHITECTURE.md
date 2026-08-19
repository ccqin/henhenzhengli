# DesktopManager 架构

> 更新：2026-08-19 · M6 子进程架构 + Owner=DefView 终态 · 死代码清理后

## 一、进程与项目总览

```
┌─────────────────────────────────────────────────────────────────────┐
│                     DesktopManager.App（主进程）                     │
│                                                                     │
│  托盘/设置窗口  配置管理  桌面文件监听(DesktopSync)  播放治理(Governor) │
│  子进程生命周期(ChildProcessManager)  跨屏中转  聚合持久化  日志数据库   │
└──────┬──────────────────┬─────────────────────┬─────────────────────┘
       │ stdin/stdout     │ stdin/stdout        │ stdin/stdout
       │ JSON 行协议      │ JSON 行协议         │ JSON 行协议
┌──────▼──────┐    ┌──────▼──────┐        ┌─────▼───────┐
│ Player.     │    │ Player.     │  ...   │ Player.*    │  每屏一组
│ Wallpaper   │    │ Icons       │        │（多屏扩展）   │
│ (壁纸渲染)   │    │ (图标层+交互)│        └─────────────┘
└─────────────┘    └─────────────┘
```

**7 个项目与依赖方向**（箭头 = 引用）：

```
Tests ──────► Core, Ipc
App ────────► Core, Native, Ipc, Player.Wallpaper*, Player.Icons*
Player.Icons ─► Core, Ipc, Native
Player.Wallpaper ─► Core, Ipc
Native ─────► Core
Core / Ipc ──► （无依赖，最底层）
```
\* App 对 Player.\* 的引用仅为把 exe 复制进输出目录（运行时按路径拉起）。

## 二、窗口形态（真机验证的终态）

所有渲染窗口 = **普通顶层窗口**，创建后由主进程执行 `AttachTopLevel`：

1. `SetWindowLongPtr(GWL_HWNDPARENT, hSHELLDLL_DefView)` —— **Owner 挂到桌面图标视图**（Fences/Layouter 同款）：
   - Win+D / 显示桌面免疫（owned 窗口跟随 shell，从不被最小化）
   - Z 序天然贴桌面层（永远在普通窗口之下）
2. `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`（不进任务栏、不抢焦点；壁纸另加 `WS_EX_TRANSPARENT` 点击穿透）
3. 壁纸窗高度 = 屏高 - 2px（破 shell 全屏检测，防任务栏被隐藏）

辅助机制：
- `WM_MOUSEACTIVATE → MA_NOACTIVATE`：点击不提升 Z 序（防看门狗时代误判；看门狗已删）
- 文本输入（重命名）：`EnableActivation/RestoreNonInteractive` 临时前台化
- 系统菜单弹出：临时前台化 + `RestoreNoActivateStyle`（只还原样式不动 Z 序）

> 已废弃路线（真机证伪，详见 `2026-08-18-M6子进程重构-真机复盘.md`）：
> SetParent 进 WorkerW/Progman（本机物理输出失效）、Z 看门狗、键盘钩子、图标层色键透明。

## 三、IPC 协议（stdin/stdout JSON 行，v1）

子进程启动 → stdout 上报 `Ready{hwnd}` → 主进程挂载窗口（AttachTopLevel）→ `Show` → 数据下发。
主进程死 → 子进程 stdin EOF → 自动退出（崩溃安全不变式）。

**消息一览**（27 个，全部在用）：

| 方向 | 消息 | 用途 |
|---|---|---|
| 子→主 | Ready | 窗口句柄上报 |
| 子→主 | LayoutChanged | 布局变更（防抖 500ms，主进程聚合落盘+审计） |
| 子→主 | IconOpened / IconAction / FenceAction | 操作审计（日志数据库 ops 表） |
| 子→主 | TransferLooseReq / TransferFenceReq | 跨屏拖拽请求（主进程中转） |
| 子→主 | ExportIconData / ExportFenceData | 跨屏迁移导出应答 |
| 子→主 | ClearSelectionExcept | 单选广播（除本屏） |
| 子→主 | Error | 错误上报（入库，不再静默） |
| 主→子 | SetWallpaper | 壁纸（path+kind+跨屏裁剪 canvas/crop） |
| 主→子 | SetIcons / ApplyDiff / SetFences | 图标全量/增量/收纳盒下发 |
| 主→子 | Pause / Resume | 播放治理（全屏/锁屏/电池） |
| 主→子 | Show / SetPosition / Shutdown | 生命周期 |
| 主→子 | SetAppearance / SetMenu | 外观与右键菜单配置下发 |
| 主→子 | ExportIcon/ImportIcon/ExportFence/ImportFence/ClearSelection | 跨屏迁移编排 |

## 四、数据与持久化

```
%AppData%\DesktopManager\
├── config.json    聚合布局（收纳盒/散落位置/壁纸/显示组/外观/菜单配置）
│                  主进程单一写者：子进程 LayoutChanged 上报 → 防抖 500ms 落盘
└── logs.db        SQLite（WAL）：logs 表（运行日志 INF+，Serilog sink）
                   ops 表（操作审计）；30 天/2 万条滚动清理；设置窗口可查/导出
```

桌面真相源：`DesktopSync`（FSW 监听用户+公共桌面）→ diff → 主进程按归属路由 `ApplyDiff` 到对应屏子进程。

## 五、关键流程

**启动**：枚举屏 → 读配置 → 每屏启动 Wallpaper+Icons 子进程 → Ready → AttachTopLevel（壁纸先挂、图标层后挂）→ Show → SetWallpaper/SetFences/SetIcons/SetAppearance/SetMenu → 桌面监听接线

**热插拔**：DisplayChangeWatcher → RebuildToMatchTopology（消失屏停子进程/孤儿配置保留、存活屏重定位、新屏启动恢复）

**explorer 重启**：ShellRestartWatcher(TaskbarCreated) → ReattachAll（Owner 失效 → 全部子进程重建重挂）

**退出**：托盘 → SaveAllNow → 子进程 Stop(Shutdown→关stdin→Kill兜底) → 恢复原生桌面图标；异常退出由 RunOnce 自清理兜底

## 六、分层职责

| 层 | 职责 | 约束 |
|---|---|---|
| Core | 纯逻辑（模型/对账/分配/决策），130 单测覆盖 | 不碰 Win32/UI |
| Native | Win32 封装（窗口样式/Owner 挂载/系统菜单/图标提取/监视器/图标显隐） | 无业务逻辑 |
| Ipc | 消息模型 + JSON 行读写 | 无状态 |
| App | 组合与生命周期：托盘/设置/治理/持久化/审计 | 不直接创建任何渲染窗口 |
| Player.* | 渲染与交互自闭环（子进程） | 交互本地处理，跨屏经主进程中转 |
