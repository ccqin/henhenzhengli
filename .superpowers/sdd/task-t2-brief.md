# T2 Brief — ConfigStore 配置模型 + xUnit TDD（M0.3）

你在 `DesktopManager` 项目（WPF/.NET 10/MSIX 桌面图标管理+壁纸工具）的 M0 阶段。仓库与四项目骨架已由 T1 建好（`d:\15.ai\狠狠整理`，4 项目+引用+build 0 error 已就绪）。本任务实现 **Core 项目的配置模型与 JSON 存储**，用 **xUnit TDD**（先写失败测试→跑→实现→跑过→提交）。这是纯逻辑，无 Win32/UI 依赖。

## Global Constraints
- Core 框架 `net10.0`；Tests 框架 `net10.0`，已引用 Core。
- 命名空间：`DesktopManager.Core.Models` / `DesktopManager.Core.Services`。
- 用 `System.Text.Json`；nullable enable（项目已开 `ImplicitUsings`+`Nullable`）。
- 代码逐字按下方实现（计划已定稿），你的工作是转录 + 跑测试，不要自由发挥（YAGNI）。

## Task M0.3 — Core 配置模型与存储

Files:
- Create: `src/DesktopManager.Core/Models/AppConfig.cs`
- Create: `src/DesktopManager.Core/Services/IConfigStore.cs`
- Create: `src/DesktopManager.Core/Services/ConfigStore.cs`
- Test: `src/DesktopManager.Tests/ConfigStoreTests.cs`

Produces: `AppConfig`、`FenceConfig`、`IConfigStore.Load()` / `.Save(AppConfig)`。

### Step 1: 写失败测试
`src/DesktopManager.Tests/ConfigStoreTests.cs`:
```csharp
using DesktopManager.Core.Models;
using DesktopManager.Core.Services;

namespace DesktopManager.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Save_Load_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var store = new ConfigStore(path);
            var config = new AppConfig(
                HideExplorerIcons: true,
                AutoStart: true,
                Fences: new[] { new FenceConfig("f1", "Work", 10, 20, 300, 400) });

            store.Save(config);
            var loaded = store.Load();

            Assert.True(loaded.HideExplorerIcons);
            Assert.Single(loaded.Fences);
            Assert.Equal("Work", loaded.Fences[0].Title);
            Assert.Equal(300, loaded.Fences[0].W);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        var store = new ConfigStore(path);
        var loaded = store.Load();
        Assert.False(loaded.HideExplorerIcons); // 默认不接管，安全
        Assert.Empty(loaded.Fences);
    }
}
```

### Step 2: 跑测试确认失败
```bash
dotnet test src/DesktopManager.Tests/DesktopManager.Tests.csproj --filter "FullyQualifiedName~ConfigStoreTests"
```
Expected: FAIL（`ConfigStore`/`AppConfig` 未定义，编译错误）。

### Step 3: 写最小实现

`src/DesktopManager.Core/Models/AppConfig.cs`:
```csharp
namespace DesktopManager.Core.Models;

public record AppConfig(
    bool HideExplorerIcons = false,
    bool AutoStart = true,
    IReadOnlyList<FenceConfig> Fences = null!);

public record FenceConfig(string Id, string Title, int X, int Y, int W, int H);
```
注：`Fences` 默认 `null!`，`Load` 会替换为空列表。

`src/DesktopManager.Core/Services/IConfigStore.cs`:
```csharp
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public interface IConfigStore
{
    AppConfig Load();
    void Save(AppConfig config);
}
```

`src/DesktopManager.Core/Services/ConfigStore.cs`:
```csharp
using System.IO;
using System.Text.Json;
using DesktopManager.Core.Models;
namespace DesktopManager.Core.Services;

public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;

    public ConfigStore(string path) => _path = path;

    public AppConfig Load()
    {
        if (!File.Exists(_path))
            return new AppConfig(HideExplorerIcons: false, AutoStart: true, Fences: Array.Empty<FenceConfig>());
        var json = File.ReadAllText(_path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options)
                  ?? new AppConfig(Fences: Array.Empty<FenceConfig>());
        return cfg with { Fences = cfg.Fences ?? Array.Empty<FenceConfig>() };
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Options));
}
```

### Step 4: 跑测试确认通过
```bash
dotnet test src/DesktopManager.Tests/DesktopManager.Tests.csproj --filter "FullyQualifiedName~ConfigStoreTests"
```
Expected: PASS，2 个测试通过。

### Step 5: 提交
```bash
git add -A
git commit -m "feat(core): AppConfig model and JSON ConfigStore with round-trip tests"
```

## 完成后报告要求
把完整报告写到 `d:\15.ai\狠狠整理\.superpowers\sdd\task-t2-report.md`，包含：
1. Status: DONE / DONE_WITH_CONCERNS / NEEDS_CONTEXT / BLOCKED
2. commit 的 hash 与 message
3. `dotnet test` 两次的结果（失败→通过的证据，贴关键输出）
4. 任何偏离 brief 的决定及原因
返回给我的消息只含：status、commit、一句话测试结果、concerns（如有）。

遇到歧义（如测试不通过、JSON 序列化异常）先问我再继续。
