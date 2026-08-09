# WinARD 无损自适应会话性能实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 在不改变分辨率、BGRA32 色彩或静态清晰度的前提下，增加按设备保存的自动/手动刷新率、鼠标移动合并、输入优先调度，以及可证明瓶颈的会话传输与编码统计。

**架构：** 领域与 SQLite 保存 `FrameRefreshPolicy`；桌面会话使用纯逻辑 `AutomaticFrameRateController` 和 `FramebufferRequestPacer` 控制下一次画面请求；PointerMoved 通过 latest-wins 合并器消除过期轨迹；RFB 客户端通过有公平性的优先消息调度器串行写入。协议解码结果携带编码计数，ARD 加密层下方的计数流提供会话字节差，ViewModel 汇总并向 UI 与脱敏诊断发布快照。

**技术栈：** .NET 8、C# 12、WinUI 3、SQLite、RFB/ARD 3.889、xUnit、`TimeProvider`、现有安全诊断基础设施。

---

## 执行前提

当前 `codex/session-input-quality` 工作区包含未提交的会话输入修复，不能 reset、stash、checkout 或覆盖。执行者必须先运行：

```powershell
git branch --show-current
git status --short
dotnet test WinARD.sln -c Release --no-restore
```

预期基线：分支仍为 `codex/session-input-quality`，测试 1305 个通过。若使用新 worktree，必须先确保该 worktree 包含当前 Pointer/ARD 前置修复；不能从缺少这些未提交变更的旧 HEAD 开始。只有在隔离且干净的 worktree 中才执行本计划的 commit 步骤；否则仅运行对应验证并保留未暂存状态。

## 文件结构

### 新建文件

- `src/WinARD.Domain/Connections/FrameRefreshPolicy.cs`：刷新模式、合法固定档位和运行时上限计算。
- `src/WinARD.Infrastructure/Database/Migrations/Migration002FrameRefreshPolicy.cs`：为设备表增加刷新策略列。
- `src/WinARD.Desktop/ViewModels/FramebufferRequestPacer.cs`：固定、自动和无限模式的单调时钟限帧。
- `src/WinARD.Desktop/ViewModels/AutomaticFrameRateController.cs`：EMA、迟滞和逐档升降纯逻辑。
- `src/WinARD.Desktop/ViewModels/SessionPerformanceTracker.cs`：会话 FPS、接收速率、编码、耗时快照。
- `src/WinARD.Desktop/ViewModels/FrameRefreshOption.cs`：工具栏使用的强类型刷新选项和远端上限状态。
- `src/WinARD.Desktop/Input/RemotePointerWriteCoalescer.cs`：latest-wins 移动与屏障顺序。
- `src/WinARD.Desktop/Services/SessionTrafficCountingStream.cs`：不复制内容的读写字节计数。
- `src/WinARD.Desktop/Services/ClientMessageScheduler.cs`：输入优先、后台公平的客户端消息调度器。
- `tests/WinARD.Desktop.Tests/ViewModels/FramebufferRequestPacerTests.cs`
- `tests/WinARD.Desktop.Tests/ViewModels/AutomaticFrameRateControllerTests.cs`
- `tests/WinARD.Desktop.Tests/ViewModels/SessionPerformanceTrackerTests.cs`
- `tests/WinARD.Desktop.Tests/Input/RemotePointerWriteCoalescerTests.cs`
- `tests/WinARD.Desktop.Tests/Services/SessionTrafficCountingStreamTests.cs`
- `tests/WinARD.Desktop.Tests/Services/ClientMessageSchedulerTests.cs`

### 修改文件

- `src/WinARD.Domain/Connections/ConnectionProfile.cs`：持有并复制刷新策略。
- `src/WinARD.Infrastructure/Database/WinArdDatabase.cs`：注册 migration 002。
- `src/WinARD.Infrastructure/Devices/SqliteDeviceRepository.cs`：策略读写。
- `src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateResult.cs`：每次更新的编码计数。
- `src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`：在矩形循环中聚合编码 ID。
- `src/WinARD.Application/Ports/RemoteSessionContracts.cs`：画面统计与运行时性能快照契约。
- `src/WinARD.Application/Ports/IRfbClientFactory.cs`：暴露性能快照。
- `src/WinARD.Application/Sessions/RemoteSession.cs`：转发性能快照。
- `src/WinARD.Desktop/Services/RfbClientFactory.cs`：计数流、编码统计和消息调度集成。
- `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`：限帧、自动控制、合并输入与性能发布。
- `src/WinARD.Desktop/Services/ConnectionSessionController.cs`：保存当前设备策略并更新 ownership profile。
- `src/WinARD.Desktop/MainWindow.xaml.cs`：向远程窗口传入当前 profile 和保存回调。
- `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`：刷新率选择和性能文本。
- `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`：选择器绑定、即时应用、保存错误和诊断上下文。
- 相关现有测试文件：领域、数据库、协议、ViewModel、控制器、窗口集成和诊断导出测试。

## 任务 1：刷新策略领域模型

**文件：**
- 创建：`src/WinARD.Domain/Connections/FrameRefreshPolicy.cs`
- 修改：`src/WinARD.Domain/Connections/ConnectionProfile.cs`
- 测试：`tests/WinARD.Domain.Tests/ConnectionProfileTests.cs`

- [ ] **步骤 1：编写失败的策略和值复制测试**

在 `ConnectionProfileTests.cs` 增加：

```csharp
[Fact]
public void New_profile_defaults_to_automatic_refresh()
{
    var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user");

    Assert.Equal(FrameRefreshPolicy.Automatic, profile.FrameRefreshPolicy);
}

[Theory]
[InlineData(30)]
[InlineData(45)]
[InlineData(60)]
[InlineData(75)]
[InlineData(90)]
[InlineData(105)]
[InlineData(120)]
public void Fixed_refresh_accepts_only_supported_steps(int fps)
{
    Assert.Equal(fps, FrameRefreshPolicy.Fixed(fps).FixedFramesPerSecond);
}

[Theory]
[InlineData(0)]
[InlineData(15)]
[InlineData(31)]
[InlineData(121)]
public void Fixed_refresh_rejects_unsupported_values(int fps) =>
    Assert.Throws<ArgumentOutOfRangeException>(() => FrameRefreshPolicy.Fixed(fps));

[Fact]
public void Updating_refresh_preserves_connection_and_credentials()
{
    var credential = CredentialReference.Create("windows", "mac-device-1");
    var original = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
        .WithCredential(credential);

    var updated = original.WithFrameRefreshPolicy(FrameRefreshPolicy.Unlimited);

    Assert.Equal(FrameRefreshPolicy.Unlimited, updated.FrameRefreshPolicy);
    Assert.Equal(original.CredentialReference, updated.CredentialReference);
    Assert.Equal(original.Host, updated.Host);
}
```

- [ ] **步骤 2：运行测试验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ConnectionProfileTests
```

预期：编译失败，缺少 `FrameRefreshPolicy`、`FrameRefreshPolicy` 属性和 `WithFrameRefreshPolicy`。

- [ ] **步骤 3：实现最小领域模型**

`FrameRefreshPolicy.cs` 使用以下公开形状：

```csharp
namespace WinARD.Domain.Connections;

public enum FrameRefreshMode
{
    Automatic = 0,
    Fixed = 1,
    Unlimited = 2,
}

public readonly record struct FrameRefreshPolicy
{
    private static readonly int[] SupportedFixedValues = [30, 45, 60, 75, 90, 105, 120];

    private FrameRefreshPolicy(FrameRefreshMode mode, int? fixedFramesPerSecond)
    {
        Mode = mode;
        FixedFramesPerSecond = fixedFramesPerSecond;
    }

    public FrameRefreshMode Mode { get; }
    public int? FixedFramesPerSecond { get; }
    public static FrameRefreshPolicy Automatic { get; } = new(FrameRefreshMode.Automatic, null);
    public static FrameRefreshPolicy Unlimited { get; } = new(FrameRefreshMode.Unlimited, null);
    public static IReadOnlyList<int> SupportedFixedFramesPerSecond => SupportedFixedValues;

    public static FrameRefreshPolicy Fixed(int framesPerSecond)
    {
        if (Array.IndexOf(SupportedFixedValues, framesPerSecond) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        return new FrameRefreshPolicy(FrameRefreshMode.Fixed, framesPerSecond);
    }

    public int? EffectiveMaximum(int? remoteMaximum) => Mode switch
    {
        FrameRefreshMode.Unlimited => null,
        FrameRefreshMode.Automatic => remoteMaximum,
        _ => remoteMaximum is { } maximum
            ? Math.Min(FixedFramesPerSecond!.Value, maximum)
            : FixedFramesPerSecond,
    };
}
```

把 `frameRefreshPolicy` 加入 `ConnectionProfile` 私有构造函数，`Create` 默认 Automatic，所有 `With...` 方法原样传递它，并新增：

```csharp
public FrameRefreshPolicy FrameRefreshPolicy { get; }

public ConnectionProfile WithFrameRefreshPolicy(FrameRefreshPolicy policy) =>
    new(Id, DisplayName, Host, Port, MacUsername, TransportMode,
        CredentialReference, SshProfile, policy);
```

- [ ] **步骤 4：运行领域测试验证绿灯**

运行同一步骤 2；预期全部通过。

- [ ] **步骤 5：检查点**

```powershell
git diff --check -- src/WinARD.Domain/Connections tests/WinARD.Domain.Tests/ConnectionProfileTests.cs
```

隔离干净 worktree 中提交：

```powershell
git add src/WinARD.Domain/Connections/FrameRefreshPolicy.cs src/WinARD.Domain/Connections/ConnectionProfile.cs tests/WinARD.Domain.Tests/ConnectionProfileTests.cs
git commit -m "feat: add per-device frame refresh policy"
```

## 任务 2：SQLite 迁移与策略持久化

**文件：**
- 创建：`src/WinARD.Infrastructure/Database/Migrations/Migration002FrameRefreshPolicy.cs`
- 修改：`src/WinARD.Infrastructure/Database/WinArdDatabase.cs`
- 修改：`src/WinARD.Infrastructure/Devices/SqliteDeviceRepository.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/WinArdDatabaseTests.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/SqliteDeviceRepositoryTests.cs`

- [ ] **步骤 1：编写迁移和往返失败测试**

增加断言：migration 001 数据库升级后 `schema_version=2`，已有设备策略为 Automatic；保存 Fixed(90) 和 Unlimited 后读取值相同。核心测试形状：

```csharp
[Fact]
public async Task Existing_device_migrates_to_automatic_refresh()
{
    await using var fixture = await DatabaseFixture.CreateAtVersionOneAsync();
    await fixture.Database.InitializeAsync(CancellationToken.None);
    await using var repository = new SqliteDeviceRepository(fixture.Database);

    var profile = Assert.Single(await repository.GetAllAsync(CancellationToken.None));

    Assert.Equal(FrameRefreshPolicy.Automatic, profile.FrameRefreshPolicy);
    Assert.Equal(2, await fixture.ReadSchemaVersionAsync());
}

[Theory]
[MemberData(nameof(RefreshPolicies))]
public async Task Repository_round_trips_refresh_policy(FrameRefreshPolicy policy)
{
    await using var fixture = await DatabaseFixture.CreateAsync();
    await using var repository = new SqliteDeviceRepository(fixture.Database);
    var profile = ConnectionProfile.Create(Guid.NewGuid(), "Mac", "host", 5900, "user")
        .WithFrameRefreshPolicy(policy);

    await repository.SaveAsync(profile, CancellationToken.None);

    Assert.Equal(policy, (await repository.GetAsync(profile.Id, CancellationToken.None))!.FrameRefreshPolicy);
}
```

`RefreshPolicies` 返回 Automatic、Fixed(30)、Fixed(120)、Unlimited。

在 `WinArdDatabaseTests` 的现有临时数据库 fixture 中增加 `CreateAtVersionOneAsync`：显式只运行 `Migration001Initial`，插入一条 devices 行后关闭连接；`ReadSchemaVersionAsync` 使用新连接读取唯一版本值。不要用手写不完整表结构模拟 v1。

- [ ] **步骤 2：运行基础设施测试验证红灯**

```powershell
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WinArdDatabaseTests|FullyQualifiedName~SqliteDeviceRepositoryTests"
```

预期：版本仍为 1，仓储不读写刷新列。

- [ ] **步骤 3：实现 migration 002**

```csharp
internal sealed class Migration002FrameRefreshPolicy : IDatabaseMigration
{
    public int FromVersion => 1;
    public int ToVersion => 2;

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE devices ADD COLUMN refresh_mode INTEGER NOT NULL DEFAULT 0
                CHECK(refresh_mode IN (0, 1, 2));
            ALTER TABLE devices ADD COLUMN refresh_fps INTEGER NULL
                CHECK(refresh_fps IS NULL OR refresh_fps IN (30, 45, 60, 75, 90, 105, 120));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

把默认 migration 链改为 `[new Migration001Initial(), new Migration002FrameRefreshPolicy()]`。

- [ ] **步骤 4：实现仓储读写与组合校验**

INSERT/UPDATE 增加 `refresh_mode`、`refresh_fps`。SELECT 在 credential 字段后增加两列，并统一调整 SSH ordinal。增加：

```csharp
private static FrameRefreshPolicy ReadRefreshPolicy(SqliteDataReader reader, int modeOrdinal, int fpsOrdinal)
{
    var mode = (FrameRefreshMode)reader.GetInt32(modeOrdinal);
    return mode switch
    {
        FrameRefreshMode.Automatic when reader.IsDBNull(fpsOrdinal) => FrameRefreshPolicy.Automatic,
        FrameRefreshMode.Unlimited when reader.IsDBNull(fpsOrdinal) => FrameRefreshPolicy.Unlimited,
        FrameRefreshMode.Fixed when !reader.IsDBNull(fpsOrdinal) =>
            FrameRefreshPolicy.Fixed(reader.GetInt32(fpsOrdinal)),
        _ => throw new InvalidDataException("Persisted frame refresh policy is invalid."),
    };
}
```

读取基础 profile 后立即调用 `.WithFrameRefreshPolicy(...)`。

- [ ] **步骤 5：运行基础设施测试验证绿灯**

运行步骤 2；预期全部通过。

- [ ] **步骤 6：检查点**

运行 `git diff --check`。隔离 worktree 中提交：

```powershell
git add src/WinARD.Infrastructure/Database src/WinARD.Infrastructure/Devices/SqliteDeviceRepository.cs tests/WinARD.Infrastructure.Tests
git commit -m "feat: persist frame refresh policy"
```

## 任务 3：纯逻辑限帧器与自动控制器

**文件：**
- 创建：`src/WinARD.Desktop/ViewModels/FramebufferRequestPacer.cs`
- 创建：`src/WinARD.Desktop/ViewModels/AutomaticFrameRateController.cs`
- 创建：`tests/WinARD.Desktop.Tests/ViewModels/FramebufferRequestPacerTests.cs`
- 创建：`tests/WinARD.Desktop.Tests/ViewModels/AutomaticFrameRateControllerTests.cs`

- [ ] **步骤 1：编写限帧红灯测试**

测试通过注入的 delay 委托捕获等待时长，不进行真实 sleep：

```csharp
[Fact]
public async Task Fixed_sixty_waits_for_remaining_frame_period()
{
    var time = new ManualTimestampProvider();
    TimeSpan? waited = null;
    var pacer = new FramebufferRequestPacer(
        time,
        (delay, _) => { waited = delay; return Task.CompletedTask; });
    pacer.SetPolicy(FrameRefreshPolicy.Fixed(60), automaticFramesPerSecond: 60);
    pacer.MarkRequestStarted();
    time.Advance(TimeSpan.FromMilliseconds(5));

    await pacer.WaitForNextRequestAsync(CancellationToken.None);

    Assert.InRange(waited!.Value.TotalMilliseconds, 11.5, 12.0);
}

[Fact]
public async Task Unlimited_does_not_delay()
{
    var delayed = false;
    var pacer = new FramebufferRequestPacer(
        TimeProvider.System,
        (_, _) => { delayed = true; return Task.CompletedTask; });
    pacer.SetPolicy(FrameRefreshPolicy.Unlimited, 60);

    await pacer.WaitForNextRequestAsync(CancellationToken.None);

    Assert.False(delayed);
}
```

- [ ] **步骤 2：编写自动控制器红灯测试**

覆盖：初始 60、远端上限 45、连续三个坏样本降一级、五秒且 30 个好样本升一级、中性样本重置连续计数、最低 30/最高 120。

```csharp
[Fact]
public void Three_bad_samples_drop_one_step()
{
    var time = new ManualTimestampProvider();
    var controller = new AutomaticFrameRateController(time, remoteMaximum: null);
    var bad = new FrameRateLoadSample(
        Response: TimeSpan.FromMilliseconds(30),
        Presentation: TimeSpan.FromMilliseconds(14),
        ChangedAreaRatio: 0.75,
        InputWriteLatency: TimeSpan.FromMilliseconds(60));

    controller.Observe(bad);
    controller.Observe(bad);
    time.Advance(TimeSpan.FromSeconds(1));
    controller.Observe(bad);

    Assert.Equal(45, controller.CurrentFramesPerSecond);
}
```

- [ ] **步骤 3：运行定向测试验证红灯**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~FramebufferRequestPacerTests|FullyQualifiedName~AutomaticFrameRateControllerTests"
```

预期：缺少所有新类型。

- [ ] **步骤 4：实现 `FramebufferRequestPacer`**

公开形状：

```csharp
internal sealed class FramebufferRequestPacer(
    TimeProvider timeProvider,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public void SetPolicy(FrameRefreshPolicy policy, int automaticFramesPerSecond);
    public void MarkRequestStarted();
    public Task WaitForNextRequestAsync(CancellationToken cancellationToken);
}
```

使用 `TimeProvider.GetTimestamp/GetElapsedTime`；默认 delay 为 `Task.Delay(value, timeProvider, token)`；超时已耗尽时返回 CompletedTask，不补偿遗漏周期。

- [ ] **步骤 5：实现 `AutomaticFrameRateController`**

定义：

```csharp
internal readonly record struct FrameRateLoadSample(
    TimeSpan Response,
    TimeSpan Presentation,
    double ChangedAreaRatio,
    TimeSpan InputWriteLatency);

internal sealed class AutomaticFrameRateController
{
    public AutomaticFrameRateController(TimeProvider timeProvider, int? remoteMaximum);
    public int CurrentFramesPerSecond { get; }
    public bool Observe(FrameRateLoadSample sample);
}
```

严格实现规格中的 0.25 EMA、3 个坏样本、1 秒降档冷却、5 秒且 30 个好样本升档、单步变化及中性重置。

- [ ] **步骤 6：运行定向测试验证绿灯**

运行步骤 3；预期全部通过。

- [ ] **步骤 7：检查点并提交**

```powershell
git diff --check -- src/WinARD.Desktop/ViewModels tests/WinARD.Desktop.Tests/ViewModels
```

隔离 worktree 中提交：`feat: add adaptive framebuffer pacing`。

## 任务 4：协议编码计数与会话字节统计

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateResult.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 创建：`src/WinARD.Desktop/Services/SessionTrafficCountingStream.cs`
- 修改：`src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`
- 创建：`tests/WinARD.Desktop.Tests/Services/SessionTrafficCountingStreamTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：编写编码计数红灯测试**

构造含 Raw、CopyRect 和 Cursor 的一条更新，断言：

```csharp
Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.Raw]);
Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.CopyRect]);
Assert.Equal(1, result.EncodingCounts[(int)RfbEncodingType.Cursor]);
```

另加重复两个 Raw 矩形计数为 2 的测试。

- [ ] **步骤 2：编写计数流红灯测试**

验证同步/异步 read、write 都准确累计，且 wrapper 不拥有 inner：

```csharp
[Fact]
public async Task Counts_read_and_written_bytes_without_copying_payload()
{
    await using var inner = new MemoryStream([1, 2, 3, 4]);
    await using var stream = new SessionTrafficCountingStream(inner, leaveOpen: true);
    var buffer = new byte[3];

    Assert.Equal(3, await stream.ReadAsync(buffer));
    await stream.WriteAsync(new byte[] { 5, 6 });

    Assert.Equal(3, stream.BytesRead);
    Assert.Equal(2, stream.BytesWritten);
}
```

- [ ] **步骤 3：运行红灯测试**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FramebufferUpdateTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~SessionTrafficCountingStreamTests|FullyQualifiedName~FramePresentationTests"
```

预期：缺少 `EncodingCounts` 和 `SessionTrafficCountingStream`。

- [ ] **步骤 4：聚合编码计数**

`FramebufferUpdateReader` 在读取每个矩形的 `encodingId` 后增加计数；`FramebufferUpdateResult` 接收只读字典并防御性复制。保留兼容构造函数，把未提供字典视为空。

- [ ] **步骤 5：实现计数流和消息统计契约**

`SessionTrafficCountingStream` 代理所有 Stream 能力，只在成功返回后用 `Interlocked.Add` 累计长度。新增契约：

```csharp
public sealed record RemoteUpdateStatistics(
    long ReceivedSessionBytes,
    IReadOnlyDictionary<int, int> EncodingCounts)
{
    public static RemoteUpdateStatistics Empty { get; } = new(0, new Dictionary<int, int>());
}
```

给 `RemoteFramebufferMessage` 和 `RemoteCursorMessage` 增加可选 `Statistics`，旧构造函数默认 Empty。

- [ ] **步骤 6：在 RfbClient 生成每次响应统计**

构造 RfbClient 时用计数流包裹原 stream，再交给 `ArdEncryptedStream`。`ReceiveAsync` 进入前读取 `BytesRead`，完成 framebuffer 解码后计算差值，并把 `update.EncodingCounts` 传入快照工厂。ARD 元数据和 Cursor 保留在原始计数中，后续 tracker 再区分画面编码。

- [ ] **步骤 7：运行绿灯测试并检查无回归**

运行步骤 3；预期全部通过。

- [ ] **步骤 8：检查点并提交**

隔离 worktree 中提交：`feat: collect framebuffer encoding and traffic metrics`。

## 任务 5：PointerMoved latest-wins 合并

**文件：**
- 创建：`src/WinARD.Desktop/Input/RemotePointerWriteCoalescer.cs`
- 创建：`tests/WinARD.Desktop.Tests/Input/RemotePointerWriteCoalescerTests.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 测试：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`

- [ ] **步骤 1：编写合并与屏障红灯测试**

使用阻塞 sender 验证：首个移动正在发送时再提交 100 个移动，最终只发送首个和最新；按下/释放/滚轮屏障前先发送最新移动；取消后不发送新值；sender 失败时故障回调只触发一次。

```csharp
[Fact]
public async Task Blocked_move_keeps_only_latest_pending_position()
{
    var writes = new List<PointerWrite>();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var coalescer = new RemotePointerWriteCoalescer(async (write, token) =>
    {
        writes.Add(write);
        if (writes.Count == 1) await gate.Task.WaitAsync(token);
    });

    coalescer.QueueMove(new PointerWrite(0, new RemotePoint(1, 1)));
    for (var i = 2; i <= 101; i++)
    {
        coalescer.QueueMove(new PointerWrite(0, new RemotePoint(i, i)));
    }
    gate.SetResult();
    await coalescer.WhenIdleAsync();

    Assert.Equal([new(0, new(1, 1)), new(0, new(101, 101))], writes);
    Assert.Equal(99, coalescer.Snapshot.CoalescedMoves);
}
```

- [ ] **步骤 2：运行红灯测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~RemotePointerWriteCoalescerTests
```

预期：缺少合并器类型。

- [ ] **步骤 3：实现合并器**

定义：

```csharp
internal readonly record struct PointerWrite(byte Buttons, RemotePoint Point);
internal readonly record struct RemotePointerCoalescerSnapshot(
    long CoalescedMoves,
    int PendingDepth);
```

合并器提供同步无阻塞的 `QueueMove`、`BarrierAsync(IReadOnlyList<PointerWrite>)`、`WhenIdleAsync`、`Snapshot` 和 `DisposeAsync`。单 worker 按以下规则取队列：

1. pending move 可被新 move 原子替换；`QueueMove` 只完成入队，不为每个移动创建等待发送的 Task；
2. barrier 入队前把当前 pending move 固化到 barrier 前；
3. barrier 内的写入不可合并；
4. 构造函数接收 `Func<Exception, Task> faultHandler`；sender 异常 fault 合并器，使当前/后续 barrier 与 `WhenIdleAsync` 以同一异常失败，并只调用一次 faultHandler；ViewModel 把它连接到现有 `ObserveInputFailure`、`ReportInputFailureAsync` 和停止会话路径。

- [ ] **步骤 4：集成窗口事件**

ViewModel 新增：

```csharp
public void QueuePointerMove(byte buttons, RemotePoint point);
public ValueTask SendPointerBarrierAsync(
    IReadOnlyList<PointerWrite> writes,
    CancellationToken token);
```

`OnPointerMoved` 调用 move；Pressed、Released、Canceled、Wheel 和 release-input 使用 barrier。Wheel 一次 barrier 内发送 `[wheelMask, baseMask]`，禁止其他移动插入两者之间。本地 `UpdateLocalPointerState` 仍在入队前同步执行。

ViewModel Dispose 先停止接收新输入，再用现有 250 ms 释放窗口完成必要的按键/按钮释放屏障，随后 Dispose coalescer；不得在传输已经销毁后继续运行 pointer worker。

- [ ] **步骤 5：运行合并器和窗口输入测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~RemotePointerWriteCoalescerTests|FullyQualifiedName~RemoteSessionWindowInputIntegrationTests"
```

预期全部通过。

- [ ] **步骤 6：检查点并提交**

隔离 worktree 中提交：`perf: coalesce stale pointer moves`。

## 任务 6：客户端消息优先级与公平发送

**文件：**
- 创建：`src/WinARD.Desktop/Services/ClientMessageScheduler.cs`
- 创建：`tests/WinARD.Desktop.Tests/Services/ClientMessageSchedulerTests.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 测试：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/Services/RemoteInputProtocolDiagnosticsTests.cs`

- [ ] **步骤 1：编写优先级、公平性和故障红灯测试**

覆盖：高优先级先于尚未开始的 background；持续输入时第 32 条后必须运行一个 background；同优先级 FIFO；一个 write 失败后所有待处理任务失败且不再执行委托。

```csharp
[Fact]
public async Task Background_is_served_after_thirty_two_input_writes()
{
    var order = new List<string>();
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var scheduler = new ClientMessageScheduler(maximumInputBatch: 32);
    var first = scheduler.EnqueueInputAsync(async token =>
    {
        order.Add("i0");
        firstStarted.SetResult();
        await releaseFirst.Task.WaitAsync(token);
    }, CancellationToken.None);
    await firstStarted.Task;
    var input = Enumerable.Range(1, 39)
        .Select(i => scheduler.EnqueueInputAsync(_ => { order.Add($"i{i}"); return ValueTask.CompletedTask; }, CancellationToken.None))
        .ToArray();
    var background = scheduler.EnqueueBackgroundAsync(
        _ => { order.Add("frame"); return ValueTask.CompletedTask; }, CancellationToken.None);
    releaseFirst.SetResult();

    await Task.WhenAll(
        input.Select(value => value.AsTask())
            .Append(first.AsTask())
            .Append(background.AsTask()));

    Assert.Equal(32, order.IndexOf("frame"));
}
```

- [ ] **步骤 2：运行红灯测试**

运行 Desktop 定向测试；预期缺少 scheduler。

- [ ] **步骤 3：实现 scheduler**

使用 lock 保护的 input/background FIFO 与一个异步 worker。`EnqueueInputAsync`、`EnqueueBackgroundAsync` 返回对应请求完成任务。调度规则：有 input 时最多连续发送 32 条；若 background 非空则发送一条并重置批次；无 input 时立即发送 background。Dispose 停止接收并等待 worker；fault 后缓存 `ExceptionDispatchInfo`。

- [ ] **步骤 4：把 RfbClient 的运行期写入全部接入 scheduler**

- Pointer、Key 使用 input。
- Clipboard 和普通 FramebufferUpdateRequest 使用 background。
- ARD Tickle 触发的 AutoFramebufferUpdate 使用 background。
- 初始化完成前的握手、认证、SetEncodings 和加密激活保持现有直接顺序；初始化完成后才允许并发运行期消息。
- 输入诊断的 `ProtocolWriteStarted/Completed` 包围实际 write 委托，而不是入队动作。
- RfbClient Dispose 先禁止新调度、等待或取消 worker，再释放 framebuffer decoder、session encryption 和 transport，防止 worker 写入已释放流。

- [ ] **步骤 5：运行定向测试验证绿灯**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~ClientMessageSchedulerTests|FullyQualifiedName~FramePresentationTests|FullyQualifiedName~RemoteInputProtocolDiagnosticsTests"
```

预期全部通过，现有输入边界计数语义保持。

- [ ] **步骤 6：检查点并提交**

隔离 worktree 中提交：`perf: prioritize remote input writes`。

## 任务 7：会话性能快照与自动限帧接入

**文件：**
- 创建：`src/WinARD.Desktop/ViewModels/SessionPerformanceTracker.cs`
- 创建：`tests/WinARD.Desktop.Tests/ViewModels/SessionPerformanceTrackerTests.cs`
- 修改：`src/WinARD.Application/Ports/IRfbClientFactory.cs`
- 修改：`src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- 修改：`src/WinARD.Application/Sessions/RemoteSession.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 测试：`tests/WinARD.Application.Tests/RemoteSessionRuntimeTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写 tracker 红灯测试**

定义 ViewModel 发布给 UI/诊断的快照：

```csharp
public sealed record SessionPerformanceSnapshot(
    FrameRefreshMode Mode,
    int? TargetFramesPerSecond,
    int ActualFramesPerSecond,
    long ReceiveBytesPerSecond,
    int? PrimaryFramebufferEncoding,
    int ResponseMilliseconds,
    int InputWriteMilliseconds,
    int InputQueueDepth,
    long CoalescedPointerMoves,
    long SampleSequence);
```

测试一秒窗口内 60 帧、6 MiB 字节差得到 60 FPS 和对应 B/s；元数据、Cursor 和 CopyRect 不参与 PrimaryFramebufferEncoding，Raw/ZRLE 按像素矩形计数选主编码；无样本保持 0。

- [ ] **步骤 2：编写 ViewModel 限帧红灯测试**

脚本 runtime 返回一帧后，断言下一次请求在 pacer delay 完成前未发生；选择 Fixed(30) 后 delay 约 33 ms；Unlimited 不 delay；Automatic 收到坏样本后目标档位下降并更新属性。

- [ ] **步骤 3：运行红灯测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~SessionPerformanceTrackerTests|FullyQualifiedName~RemoteSessionViewModelTests"
```

预期：缺少 tracker、policy constructor 参数和性能属性。

- [ ] **步骤 4：暴露 runtime 性能快照**

在 Application contracts 定义运行时快照：

```csharp
public sealed record RemoteDisplayCapabilities(int? MaximumRefreshRate)
{
    public static RemoteDisplayCapabilities Unknown { get; } = new(null);
}

public sealed record RemoteRuntimePerformanceSnapshot(
    int InputWriteMilliseconds,
    int InputQueueDepth,
    long SampleSequence)
{
    public static RemoteRuntimePerformanceSnapshot Empty { get; } = new(0, 0, 0);
}
```

`IRfbClient` 和 `IRemoteSessionRuntime` 增加只读 `DisplayCapabilities` 与 `PerformanceSnapshot`，默认分别返回 Unknown 和 Empty；`RemoteSession` 原样转发。RfbClient 在 A 阶段返回 Unknown，不解析当前被跳过的 ARD 28 字节显示记录。测试替身可提供已知 60/120 上限，以验证 UI 和控制器行为。RfbClient 的 performance snapshot 只由 ClientMessageScheduler 的有界输入统计生成。每次画面接收字节已经位于 `RemoteUpdateStatistics`，Pointer 合并数由 ViewModel 持有的 coalescer 提供，避免不同层重复计算。

- [ ] **步骤 5：实现 `SessionPerformanceTracker`**

使用单调时间窗口、累计编码计数和 0.25 EMA；提供：

```csharp
public SessionPerformanceSnapshot ObserveFrame(
    RemoteUpdateStatistics update,
    IReadOnlyList<RemoteRectangle> dirty,
    RemoteFramebufferSize size,
    TimeSpan response,
    TimeSpan presentation,
    RemoteRuntimePerformanceSnapshot runtime,
    RemotePointerCoalescerSnapshot pointer);
```

`SessionPerformanceSnapshot` 同时包含 UI 文本需要的模式、目标 FPS、实际 FPS、接收速率、主编码和响应时间。

- [ ] **步骤 6：接入 RemoteSessionViewModel**

构造函数增加 `FrameRefreshPolicy initialRefreshPolicy`、`TimeProvider`、pacer/controller/tracker 测试替身；remote maximum 从 `_session.DisplayCapabilities.MaximumRefreshRate` 获取。已知值必须在 30 到 240 之间，否则按 Unknown 处理并写入不含原始元数据的安全类别诊断。接收循环改为：

```csharp
await _pacer.WaitForNextRequestAsync(token);
_pacer.MarkRequestStarted();
_connectionQualityTracker.BeginRequest();
await _session.RequestFramebufferUpdateAsync(incremental, token);
```

呈现前后用单调时钟测量，完成 frame 后更新 tracker；Automatic 把样本交给 controller，档位变化时更新 `TargetFramesPerSecond` 和 pacer。新增 `SetFrameRefreshPolicy` 立即更新策略。

- [ ] **步骤 7：运行 Application 与 Desktop 测试**

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RemoteSessionRuntimeTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter "FullyQualifiedName~SessionPerformanceTrackerTests|FullyQualifiedName~RemoteSessionViewModelTests"
```

预期全部通过。

- [ ] **步骤 8：检查点并提交**

隔离 worktree 中提交：`feat: publish adaptive session performance`。

## 任务 8：按设备即时保存刷新策略

**文件：**
- 修改：`src/WinARD.Desktop/Services/ConnectionSessionController.cs`
- 修改：`src/WinARD.Desktop/MainWindow.xaml.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 测试：`tests/WinARD.Desktop.Tests/Services/ConnectionSessionControllerTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`

- [ ] **步骤 1：编写 controller 红灯测试**

```csharp
[Fact]
public async Task Updating_connected_refresh_saves_profile_and_updates_ownership()
{
    var fixture = await ConnectedControllerFixture.CreateAsync();
    var ownership = fixture.Controller.TransferConnectedSession();

    var updated = await fixture.Controller.UpdateConnectedFrameRefreshPolicyAsync(
        FrameRefreshPolicy.Fixed(90), CancellationToken.None);

    Assert.Equal(FrameRefreshPolicy.Fixed(90), updated.FrameRefreshPolicy);
    Assert.Equal(updated, ownership.Profile);
    Assert.Equal(updated, fixture.Repository.Saved.Single());
}
```

另测仓储抛错时 ownership 保持旧 profile。

- [ ] **步骤 2：运行红灯测试**

运行 `ConnectionSessionControllerTests`；预期缺少更新方法，ownership Profile 不可更新。

- [ ] **步骤 3：实现原子保存与 ownership 更新**

`ConnectedSessionOwnership.Profile` 改为锁保护的可更新引用，并提供 internal `UpdateProfile`。controller 新增：

```csharp
public async Task<ConnectionProfile> UpdateConnectedFrameRefreshPolicyAsync(
    FrameRefreshPolicy policy,
    CancellationToken cancellationToken)
```

在 `_gate` 内读取 ownership 当前 profile、创建 updated、先 `repository.SaveAsync`，成功后更新 ownership 并触发 `ProfileUpdated`；失败不修改内存。

- [ ] **步骤 4：把保存回调传入远程窗口**

MainWindow 创建窗口时传入 `ownership.Profile` 和 controller 方法。retry closure 每次读取 `ownership.Profile`，不再捕获旧 `effectiveProfile`。RemoteSessionWindow 在用户选择后先调用 `ViewModel.SetFrameRefreshPolicy`，再异步保存；保存失败显示“刷新设置未保存，本次会话仍已应用”。

- [ ] **步骤 5：运行 controller 与窗口集成测试**

预期全部通过。

- [ ] **步骤 6：检查点并提交**

隔离 worktree 中提交：`feat: remember refresh policy per device`。

## 任务 9：工具栏与性能状态 UI

**文件：**
- 创建：`src/WinARD.Desktop/ViewModels/FrameRefreshOption.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 测试：`tests/WinARD.Desktop.Tests/Views/ConnectionErrorCardIntegrationTests.cs`

- [ ] **步骤 1：编写 XAML/绑定红灯测试**

源码集成测试断言：

```csharp
Assert.Contains("x:Name=\"FrameRateComboBox\"", xaml, StringComparison.Ordinal);
Assert.Contains("x:Name=\"PerformanceText\"", xaml, StringComparison.Ordinal);
Assert.Contains("OnFrameRateSelectionChanged", codeBehind, StringComparison.Ordinal);
Assert.Contains("nameof(RemoteSessionViewModel.SessionPerformance)", codeBehind, StringComparison.Ordinal);
Assert.Contains("\"自动\"", optionSource, StringComparison.Ordinal);
Assert.Contains("\"120\"", optionSource, StringComparison.Ordinal);
Assert.Contains("\"无限\"", optionSource, StringComparison.Ordinal);
```

- [ ] **步骤 2：运行红灯测试**

运行 `ConnectionErrorCardIntegrationTests`；预期缺少 UI 元素。

- [ ] **步骤 3：实现工具栏 UI**

在 `FrameRefreshOption.cs` 定义：

```csharp
public sealed record FrameRefreshOption(
    FrameRefreshPolicy Policy,
    string DisplayName,
    bool IsEnabled,
    string? ConstraintText);
```

在 CommandBar.Content 中加入可访问的 `ComboBox`，ItemsSource 使用 ViewModel 暴露的不可变选项对象，不把字符串解析作为领域输入。远端上限未知时全部显示；已知时高档禁用；保存值高于上限时保留显示并增加“当前上限 X，有效 X”。

状态栏 `PerformanceText` 格式：

```text
自动 45 FPS · 实际 38 FPS · 12.4 MiB/s · ZRLE · 86 ms
```

未测值显示短横线，不伪造 0 ms 或 Raw。

- [ ] **步骤 4：实现属性变化刷新和无障碍文本**

质量圆点继续表达现有连接级别；Automation Name 同时包含模式、实际 FPS、速率、编码和响应时间，不只依赖颜色。

- [ ] **步骤 5：运行 UI 集成与 ViewModel 测试**

预期全部通过。

- [ ] **步骤 6：检查点并提交**

隔离 worktree 中提交：`feat: add session refresh and performance controls`。

## 任务 10：脱敏诊断与 C 阶段证据门

**文件：**
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 修改：`src/WinARD.Desktop/Services/DesktopDiagnosticContextFactory.cs`
- 修改：`src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs`（仅在现有上限不足时）
- 测试：`tests/WinARD.Desktop.Tests/Views/RemoteSessionDiagnosticExportStateTests.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/DiagnosticExporterTests.cs`

- [ ] **步骤 1：编写诊断红灯测试**

创建包含性能快照的 context，导出并解析 `diagnostics.json`，断言：

```csharp
Assert.Equal(90, counters["Session.TargetFps"]);
Assert.Equal(64, counters["Session.ActualFps"]);
Assert.Equal(1234567, counters["Session.ReceiveBytesPerSecond"]);
Assert.Equal(42, counters["Session.PointerMovesCoalesced"]);
Assert.Equal(18, profile.EncodingStatistics!["ZRLE"]);
```

同时断言 JSON 不含 `Coordinate`、`PointerX`、`PointerY`、`Keysym`、`Pixel`、`Ciphertext`、`Sequence`、实际按键或剪贴板测试标记。

- [ ] **步骤 2：运行红灯测试**

运行 Desktop 诊断状态和 Infrastructure DiagnosticExporter 测试；预期当前 session export context 为空。

- [ ] **步骤 3：构建会话诊断 context**

RemoteSessionWindow 使用当前 profile 创建一个 `DiagnosticProfileSummary`，`EncodingStatistics` 只包含稳定编码名称与矩形累计计数。`PerformanceCounters` 只包含规格允许的 long 值：模式枚举、目标/实际 FPS、接收速率、响应/呈现/输入耗时、队列深度、合并数和是否 SSH 隧道内统计。

未知编码使用 `Encoding.<signed-id>`，不把 payload 或 rectangle 坐标写入字段。

- [ ] **步骤 4：明确 C 阶段准入输出**

诊断必须足以回答：Mac 是否实际发送 ZRLE/Raw、各编码矩形数量、会话字节速率和自动档位变化。此任务不添加任何新编码 ID，也不修改 SetEncodings 顺序。

- [ ] **步骤 5：运行诊断测试验证绿灯**

```powershell
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DiagnosticExporterTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore --filter FullyQualifiedName~RemoteSessionDiagnosticExportStateTests
```

预期全部通过。

- [ ] **步骤 6：检查点并提交**

隔离 worktree 中提交：`feat: export safe session performance evidence`。

## 任务 11：完整回归、发布与实机清单

**文件：**
- 修改：`docs/superpowers/specs/2026-08-02-lossless-adaptive-session-performance-design.md`（只记录最终实现偏差；没有偏差则不改）
- 发布：`artifacts/WinARD-20260802-adaptive-performance-v1.1.0.0-portable-win-x64.zip`

- [ ] **步骤 1：运行定向测试集合**

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-restore
```

预期：所有测试 0 失败。

- [ ] **步骤 2：运行完整验证**

```powershell
dotnet test WinARD.sln -c Release --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore
dotnet format WinARD.sln --verify-no-changes --no-restore
git diff --check
```

预期：全量测试通过，构建 0 warning/0 error，格式和 diff 检查退出码 0。

- [ ] **步骤 3：发布自包含 x64 包**

使用现有 `packaging/portable.ps1` 参数模式发布 `1.1.0.0`。发布后校验：

- `WinARD.Desktop.exe`、`WinARD.Desktop.dll`、`WinARD.OpenSshAskPass.exe`、`Microsoft.UI.dll`、`e_sqlite3.dll` 存在；
- exe/dll 的 FileVersion、ProductVersion、AssemblyVersion 一致；
- ZIP 可读、必需条目在根目录、计算 SHA-256。

- [ ] **步骤 4：实机验证**

在同一目标 Mac 和网络依次验证 Automatic、30、60、120、Unlimited：

1. 静态文字和细线清晰度与 1.0.2.0 一致；
2. 连续快速移动鼠标不回放旧轨迹；
3. 点击、拖动、滚轮和键盘顺序正确；
4. 固定档位实际请求率不超过选择值；
5. Automatic 在大面积滚动时可降档，稳定后不频繁抖动；
6. UI 显示实际 FPS、会话接收速率、编码和响应时间；
7. 诊断中 Pointer 合并数可增长，但坐标和按键内容不存在；
8. 根据编码统计决定是否存在 C 阶段候选，不在本任务中添加编码。

- [ ] **步骤 5：最终检查点**

若在隔离 worktree 中执行，提交最终文档/验证调整：

```powershell
git status --short
git diff --check
git commit -am "feat: add lossless adaptive session performance"
```

不要使用 `git add -A`；只暂存本计划列出的文件，避免包含用户的其他改动。
