# GPU 第三梯队 · LibVLCSharp 视频渲染替换计划

> 目标：视频壁纸 GPU 从 ~18% 降到 3-5%（跳过 WPF 合成器，DirectX 直渲染）
> 前置：第一二梯队已落地（`28e6c15`/`c09fb16`）；LibVLCSharp NuGet 已添加但代码未写

## 实施结果（2026-08-25 落地）

- ✅ T1/T2/T3 全部完成，IPC 协议不变（App.xaml.cs 与主进程零改动——现有架构把 IPC 全转发到
  WallpaperWindow 公共方法，方法签名未变）
- **跨屏裁剪实际方案**：弃用 `Crop` 几何字符串（格式不可靠），改为 **WS_CHILD 子 HWND**——
  在壁纸窗口内创建子窗口作 LibVLC 渲染表面，按旧 MediaElement 的 Canvas 布局 1:1 负偏移放置，
  超出父客户区被 Win32 天然裁剪；父窗口加 WS_CLIPCHILDREN 防闪
- **包坑**：`VideoLAN.LibVLC.Windows` 3.0.22+ 布局改为 `build/{x64,x86,arm64}/` + MSBuild targets，
  默认 AnyCPU 三架构复制 ~300MB 到 `libvlc\` 子目录（LibVLC 探测不到）→ csproj 用包属性
  `VlcWindowsX*Enabled=false` / `VlcWindowsX64TargetDir=.` 仅 x64 落应用根（~90MB），裁掉 lua/hrtfs
- **API 坑**：LibVLCSharp 3.10.1 命名空间是 `LibVLCSharp.Shared`（非官方文档的 `LibVLCSharp`）；
  `MediaPlayer.Size(uint, ref uint, ref uint)` 是 ref 签名；`Core.Initialize(null)` 须在 new LibVLC 前调
- **Win32 坑**：`GetModuleHandleW` 在 kernel32 非 user32
- 冒烟测试（真机）：D3D11VA 硬解（Intel UHD 770）、positionMs 上报/暂停/恢复/Seek/循环、
  3840x1080 超屏检测全通过；GPU 占用对比需任务管理器观察（预期 ~18% → 3-5%）

## 背景

**为什么 20% GPU 降不下来**：当前视频路径是 `解码器 → WPF 合成器 → DWM`，
中间的 **WPF 合成器是最大开销**（~8-10%）。帧率限制、GIF 停定时器等只削边缘。

**真机教训**：
- `Timeline.DesiredFrameRate=30` 会连带限制 `MediaElement` 内的 `MediaTimeline` → 视频强降 30fps = 明显卡顿（`c09fb16` 已撤销）
- `DisableHWAcceleration` 更糟（软渲染 x64 掉到 9fps，dotnet/wpf#11029）

**LibVLC 方案原理**：LibVLC 内部用 **DirectX 直接渲染到 HWND**——视频帧路径变成 `解码器 → DirectX → 窗口表面`，完全跳过 WPF 合成器。

**参考**：
- [LibVLCSharp 官方文档](https://github.com/videolan/libvlcsharp/blob/3.x/docs/getting_started.md)
- [LibVLCSharp.WPF NuGet](https://www.nuget.org/packages/LibVLCSharp.WPF)（我们不用 WPF 版的 VideoView，直接用核心 API + Hwnd）
- Lively 壁纸最终也走了 mpv/原生渲染路线（[Reddit 讨论](https://www.reddit.com/r/csharp/comments/ir2ts6/)）

## 当前状态

```
✅ NuGet 已添加：
   src/DesktopManager.Player.Wallpaper/DesktopManager.Player.Wallpaper.csproj
   - LibVLCSharp 3.10.1
   - VideoLAN.LibVLC.Windows（原生 DLL ~70MB）

❌ 代码未写（csproj 改动未提交）
```

## 实施步骤（3 个执行单元）

### Tier3-T1 — VlcVideoController + 集成

- [ ] 新建 `src/DesktopManager.Player.Wallpaper/VlcVideoController.cs`：
  ```csharp
  // 核心类：封装 LibVLC 的生命周期 + Hwnd 直渲染 + IPC 接口
  internal sealed class VlcVideoController : IDisposable
  {
      private LibVLC? _libVLC;
      private MediaPlayer? _player;

      // 初始化（传入壁纸窗口的 HWND，LibVLC 用 DirectX 直渲染到这个窗口）
      public void Initialize(IntPtr hwnd);

      // 播放（替代 MediaElement.Source + Play）
      public void Play(string path);

      // IPC 对应
      public void Pause();
      public void Resume();
      public void Seek(double positionMs);
      public double Position { get; }           // VideoPositionReport 用

      // 循环播放（替代 MediaEnded → Position=0）
      public void EnableLoop();

      // 分辨率（替代 NaturalVideoWidth/Height，VideoOversized 检测用）
      public (int W, int H) VideoSize { get; }

      public void Dispose();
  }
  ```

- [ ] `WallpaperWindow.xaml.cs` 改造：
  - 视频分支（`case WallpaperKind.Video`）改用 `VlcVideoController`
  - `_video`（MediaElement）仅保留给… 不，**完全移除 MediaElement**
  - 图/GIF 仍用 WPF `Image`（静态内容 GPU 开销可忽略）
  - 窗口仍保持 WPF Window（Owner=DefView 需要它）
  - **关键**：视频播放时把 WPF 根 Canvas 设为不渲染（`Visibility=Hidden` 或空），让 LibVLC 的 DirectX 表面直接可见

- [ ] 跨屏裁剪：LibVLC 不走 WPF 布局，裁剪偏移需要改为：
  - 方案 A：`MediaPlayer.Crop` API（LibVLC 内置裁剪过滤器，接受 "x:y:w:h" 几何字符串）
  - 方案 B：多个窗口各渲染全视频 + 裁剪（当前方式）
  - **推荐 A**：`_player.SetAdjust(VideoAdjustOption.CropGeometry, ...)`

### Tier3-T2 — IPC 接口对齐

- [ ] `App.xaml.cs`（Player.Wallpaper）：
  - `case SetWallpaper w:` → 视频 kind 时调 `_vlc.Play(w.Path)`
  - `case Pause:` → `_vlc.Pause()`
  - `case Resume:` → `_vlc.Resume()`
  - `case SetVideoPosition vp:` → `_vlc.Seek(vp.PositionMs)`
  - `VideoPositionChanged` 事件 → `_vlc.Position` 轮询（2s DispatcherTimer，同现有 StartVideoReport）

- [ ] 主进程无需改动（IPC 协议不变）

### Tier3-T3 — 构建 + 测试 + 打包 + 提交

- [ ] 构建（注意：`VideoLAN.LibVLC.Windows` 会带 ~70MB 原生 DLL，确认复制到输出）
- [ ] 真机测试：
  1. 视频壁纸正常显示
  2. GPU 使用率对比（任务管理器 → 预期从 ~18% 降到 3-5%）
  3. 暂停/恢复（PlaybackGovernor 全屏暂停）
  4. 循环播放（视频结尾自动重播）
  5. 跨屏拼接（组壁纸裁剪）
  6. 静态图/GIF 不受影响
- [ ] MSIX 打包验证（原生 DLL 是否正确打入）
- [ ] 提交 + git commit

## 关键注意事项

| 事项 | 说明 |
|---|---|
| ** airspace 限制** | LibVLC 的 DirectX 表面无法被 WPF 控件覆盖——我们的图标层在**另一个进程**，不受影响 |
| **包体积** | `VideoLAN.LibVLC.Windows` 带 ~70MB 原生 DLL（libvlc.dll + 插件），MSIX 会从 25MB 涨到 ~95MB |
| **裁剪偏移** | WPF `Canvas.SetLeft(image, offset)` 方式不适用于 LibVLC，改用 `Crop` 过滤器 |
| **DesiredFrameRate** | 不要再设（`c09fb16` 教训），LibVLC 自管帧率 |
| **句柄传递** | `MediaPlayer.Hwnd = new WindowInteropHelper(window).Handle` 在 `SourceInitialized` 之后 |
| **多实例** | 每个壁纸子进程各自创建 `LibVLC` 实例（内存独立），无需共享 |
| **`--no-osd`** | LibVLC 默认可能显示 OSD（进度条等），需 `MediaPlayer.EnableHardwareDecoding = true` + 禁用 OSD |
| **音量** | `_player.Volume = 0`（壁纸静音，同现有 `MediaElement.Volume = 0`） |

## 风险与对策

| 风险 | 概率 | 对策 |
|---|---|---|
| 跨屏裁剪不精确 | 中 | 先验证 `Crop` 几何字符串；不行则回退为"每屏独立视频窗口" |
| 原生 DLL 打包问题 | 中 | `VideoLAN.LibVLC.Windows` 包的 `runtimes\win-x64\native\` 路径会自动复制 |
| LibVLC 内存泄漏（多实例） | 低 | 每个子进程一个实例，进程退出自动回收 |
| 视频格式兼容 | 低 | LibVLC 支持几乎所有格式，比 MediaElement 更广 |

## 预期效果

| 指标 | 当前（MediaElement） | 预期（LibVLC） |
|---|---|---|
| GPU 使用率 | ~18% | **3-5%** |
| 包体积 | 25MB | ~95MB |
| 视频格式支持 | WMP 子集 | 几乎全部 |
| 帧率 | 原生 | 原生 |
