# M8 插件系统 — 架构与技术路线

> 目标：在既有"主进程 + 桌面层子进程"架构上开放**插件位**，支撑桌面宠物、桌面小组件、
> 壁纸源、自动化规则四类插件；首发官方插件 = 桌面宠物（同时作为架构试金石）。
> 日期：2026-09-04　状态：设计稿（待用户确认后实施）

## 1. 设计原则

1. **复用而非新建**：插件的进程模型、IPC 协议、桌面层挂载、崩溃安全——全部复用 M6 已验证的机制
   （壁纸/图标层就是这么跑的，真机稳定性已验证）。
2. **进程隔离**：插件 = 独立 exe 子进程。插件崩≠应用崩；技术栈自由（WPF/Avalonia/WinForms）。
3. **渐进契约**：插件 API（宿主↔插件消息）从最小集起步，按插件实际需要逐步丰富，不预先设计大而全。
4. **内置与第三方同构**：官方插件（桌面宠物）与未来第三方插件走完全相同的加载路径，避免两套逻辑。

## 2. 总体架构

### 2.1 进程模型（M6 扩展）

```
DesktopManager.App（主进程）
 ├─ 托盘 / 设置窗口（新增"插件"管理页）/ 配置 / DesktopSync / PlaybackGovernor
 ├─ PluginManager（新增）：扫描清单 → 生命周期 → Z 序编排 → 配置代存
 ├─ 每屏 1 个 Player.Wallpaper.exe —— 壁纸渲染（最底层）
 ├─ 每屏 1 个 Player.Icons.exe —— 图标层（最顶层）
 └─ 每个启用的插件 1 个进程（跨屏全局，一个实例覆盖虚拟桌面）
```

### 2.2 Z 序三明治（关键设计）

```
（低）壁纸窗口 → 插件窗口（可多个，按启用序叠放） → 图标层窗口 →（高）普通窗口
```

- 挂载：与壁纸/图标层同机制 `AttachTopLevel`（owner=SHELLDLL_DefView + TOOLWINDOW/NOACTIVATE）。
- 编排：BottomPair 扩展为 **BottomStack**——图标层贴底后，插件窗口依次 `PlaceBelow(插件, 上一个)`，
  壁纸插最下。插件重启/启停时全栈重排（幂等，复用现有 RequestReorder 链路）。
- 点击穿透：按清单声明（宠物=可点击交互；氛围特效=WS_EX_TRANSPARENT 全穿透）。
- **图标之上模式**（清单可选 `zOrder: "above-icons"`）：极少数插件需要（如全局 HUD），
  T1 阶段先不做，仅在契约中预留字段。

### 2.3 插件契约

**清单 plugin.json**（插件目录根）：

```json
{
  "id": "com.desktopmanager.pet",
  "name": "桌面宠物",
  "version": "1.0.0",
  "author": "official",
  "entry": "DesktopManager.Plugin.Pet.exe",
  "zOrder": "above-wallpaper",          // above-wallpaper（默认）| above-icons（预留）
  "clickThrough": false,                 // true = 全窗口点击穿透
  "supportsPause": true                  // 全屏/锁屏时宿主发 Pause，插件自行藏匿
}
```

**目录约定**：

```
内置插件：MSIX 包内 <安装目录>\plugins\<id>\        （随主包分发，只读）
第三方：  %APPDATA%\DesktopManager\plugins\<id>\    （用户安装，可写）
```

扫描顺序：先内置后第三方；同 id 内置优先（防伪造官方插件）。

**生命周期**：随主进程启动（enabled 的插件）→ `ready{hwnd}` 上报 → 宿主挂桌面层 →
主进程退出时 stdin EOF 自然退出（与壁纸/图标层同一崩溃安全不变式）→ 插件异常退出不自动重启
（记录 + 设置页标红，用户手动再启，避免坏插件风暴重启）。

### 2.4 IPC 消息（插件 API 最小集，T1 实现）

复用 `DesktopManager.Ipc` JSON 行协议（含行级容错——插件 stdout 打日志不再致命）：

| 消息 | 方向 | 用途 |
|---|---|---|
| `PluginHello { PluginId, Name, Version }` | 插件→主 | ready 后第一条，主进程校验清单一致性 |
| `MonitorsReq {}` | 插件→主 | 请求屏幕拓扑 |
| `MonitorsInfo { Monitors[] }` | 主→插件 | 屏幕矩形/主屏标记（宠物多屏走动、小组件定位用） |
| `PluginConfigGet { Key }` / `PluginConfigSet { Key, Value }` | 双向 | 配置由宿主代存（config.json 的 `Plugins` 节），插件无状态可随时重启 |
| `Pause` / `Resume` | 主→插件 | 全屏/锁屏/电池治理（`supportsPause: true` 才发） |
| `PluginError { Message }` | 插件→主 | 可见错误（进日志库 ops） |
| `Shutdown` | 主→插件 | 停用插件（设置页关闭开关） |

壁纸源/自动化类插件（无窗口）同样跑此协议——`ready` 时 hwnd 可为 0，宿主跳过挂载，仅做生命周期。

## 3. 宿主框架（M8-T1）

### 3.1 新增 `PluginManager`（主进程）

- `Discover()`：扫描两处插件目录，解析/校验 plugin.json（坏清单跳过并记日志）
- `StartEnabled()`：启动后按 config 的 `Plugins.Enabled[]` 拉起；`ready{hwnd}` → `AttachTopLevel`
  → BottomStack 重排
- `Stop(id)` / `Start(id)`：设置页操作
- `SavePluginConfig(id, key, value)`：写 config.json `Plugins.Configs[id][key]`（立即落盘——关键操作路径）
- 崩溃处理：`Exited(code≠0)` → 日志 + 设置页状态"已停止"，不自动重启

### 3.2 设置窗口「插件」页

- 列表：名称/版本/来源（内置|用户）/状态（运行中|已停止|损坏）/开关
- 点开详情：清单信息 + 插件自渲染配置区（T3 再做插件自定义配置 UI；T1 仅开关）
- 底部链接："插件开发文档"（T3 产出）

### 3.3 config.json 扩展

```json
"Plugins": {
  "Enabled": ["com.desktopmanager.pet"],
  "Configs": { "com.desktopmanager.pet": { "character": "cat" } }
}
```

## 4. 首发插件：桌面宠物（M8-T2）

`src/DesktopManager.Plugin.Pet/`（独立 WPF exe，发布到包内 `plugins\com.desktopmanager.pet\`）。

### 4.1 能力清单（T2 范围）

- **角色渲染**：Sprite 帧序列（PNG 图集，随包内置 1 个默认角色 + 素材目录可扩展）
- **行为状态机**：idle（原地小动作）→ walk（沿屏底行走）→ climb（贴屏幕左右边缘攀爬）→
  fall（从顶部落下，简单重力）→ drag（被鼠标拖动，跟随+摆动）→ interact（点击反应：表情/动作）
- **多屏**：可走到相邻屏（GetMonitors 的虚拟桌面坐标系）；默认只在主屏，配置项开启多屏
- **全屏礼让**：收到 Pause → 走到屏幕底边并趴下/淡出；Resume → 复活（不依赖 supportsPause 的
  宿主侧隐藏，宠物自己演"睡觉"更有味道）
- **托盘不出**：宠物交互全在桌面层（点击/拖动）；设置走宿主插件页

### 4.2 明确不做（T2 砍掉，防蔓延）

喂食/成长系统、语音、网络素材商店、多宠物同屏、托盘菜单——留 T3+。

### 4.3 素材

默认角色：开源 CC0 猫咪 Sprite（如 itch.io 的 CC0 pet pack，确认许可后入库）；
素材格式：`frames/<动作>/<帧序号>.png` + `pet.json`（帧率/锚点/动作定义）。

## 5. 技术路线（执行单元）

| 阶段 | 内容 | 验收 | 预估 |
|---|---|---|---|
| **M8-T1 宿主框架** | PluginManager + plugin.json 解析 + BottomStack Z 序 + IPC 新消息 + 设置插件页 + 空壳测试插件（随机漂移的方块） | 空壳插件挂桌面层（壁纸上图标下）、可点击/可穿透两模式、启停/重启 explorer/拔屏全存活、坏插件不拖垮主应用 | 1-2 天 |
| **M8-T2 桌面宠物** | Pet 插件（状态机/素材/拖动/多屏/全屏礼让）+ 打包进 MSIX plugins 目录 | 真机：宠物在双屏走动攀爬、拖动流畅、全屏游戏时藏匿、杀宠物进程桌面无恙 | 2-3 天 |
| **M8-T3 SDK 开放** | 插件模板工程（dotnet new）+ 开发文档 + 第三方目录加载验证 | 按文档从零做出一个第三方插件并被加载 | 远期 |

**版本节奏**：按流程约定，T1/T2 完成后并入待发布清单，用户说"更新版本"时打包为 1.1.0.0
（新大功能跳 minor）。

## 6. 风险与对策

| 风险 | 概率 | 对策 |
|---|---|---|
| 插件窗口与图标层 Z 序互抢（BottomStack 复杂化） | 中 | 全栈重排幂等 + RequestReorder 复用；真机重点回归"改名回车/壁纸重启"老场景 |
| 宠物拖动与图标拖拽手势冲突（都在桌面层） | 中 | 宠物窗口区域小且声明 clickThrough=false；命中优先给最上层（图标层在上，天然分流）；真机验证 |
| 第三方插件质量拖累口碑（商店评分） | 低（T3 前） | T3 才开放；内置插件官方维护；清单带 author 签名字段预留 |
| MSIX 包体积再涨（宠物素材） | 低 | 素材控制在 <5MB；角色扩展走"用户目录素材"不进包 |
| 插件 stdout 污染 IPC | 已根治 | IpcReader 行级容错（2026-08-31 已落地，宠物日志随便打） |

## 7. 决策点（实施前需拍板）

1. 宠物默认角色选型（猫咪/小狗/其他）与素材来源——T2 开始前定
2. `above-icons` 模式是否进 T1（当前建议：不进，预留字段即可）
3. 1.1.0.0 是否随 T1+T2 一起发（建议：是，宠物是商店更新的亮点）
