# WinARD 会话输入边界诊断实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 为远程键盘和指针输入增加不含内容的分层聚合诊断，使下一次实机复现能确定输入链路的最后一个成功边界。

**架构：** 在 WinARD.Desktop 内新增一个小型线程安全 tracker，由远程窗口和协议客户端分别持有并写入同一个安全诊断 sink。UI 层记录捕获和稳定丢弃原因，协议层记录完整 RFB 输入消息写入的开始和完成；计数独立、有限采样且任何诊断异常都不影响输入。

**技术栈：** C# 12、.NET 8、WinUI 3、xUnit、`ISafeDiagnosticSink`、`Interlocked`

**约束：** 不记录按键、字符、坐标、按钮、凭据或加密材料；不修改 ARD 控制模式、RFB 输入布局、加密算法或发送顺序；根据用户要求不执行 git stage/commit。

---

## 文件结构

- 创建：`src/WinARD.Desktop/Input/RemoteInputDiagnosticTracker.cs` — 定义安全诊断枚举、线程安全计数和有限采样。
- 创建：`tests/WinARD.Desktop.Tests/Input/RemoteInputDiagnosticTrackerTests.cs` — 验证采样、字段隐私、并发和 sink 失败隔离。
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs` — 在 UI 输入入口记录捕获或稳定丢弃原因。
- 修改：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs` — 静态集成检查 UI 边界埋点和禁止字段。
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs` — 在 KeyEvent/PointerEvent 完整写入前后记录协议边界。
- 创建：`tests/WinARD.Desktop.Tests/Services/RemoteInputProtocolDiagnosticsTests.cs` — 使用真实 `RfbClient` 和内存流验证 Started/Completed 与失败行为。

### 任务 1：实现 tracker 的红—绿循环

**文件：**
- 创建：`tests/WinARD.Desktop.Tests/Input/RemoteInputDiagnosticTrackerTests.cs`
- 创建：`src/WinARD.Desktop/Input/RemoteInputDiagnosticTracker.cs`

- [x] **步骤 1：编写第一批失败测试**

测试应直接表达 API 和隐私边界：

```csharp
[Fact]
public void Activity_samples_first_and_every_eighth_count()
{
    var sink = new RecordingSink();
    var tracker = new RemoteInputDiagnosticTracker(sink);

    for (var index = 0; index < 17; index++)
    {
        tracker.Record(
            RemoteInputKind.Keyboard,
            RemoteInputBoundary.UiCaptured);
    }

    Assert.Equal([1L, 8L, 16L], Counts(sink.Events));
}

[Fact]
public void Dropped_activity_samples_first_and_every_fourth_count()
{
    var sink = new RecordingSink();
    var tracker = new RemoteInputDiagnosticTracker(sink);

    for (var index = 0; index < 9; index++)
    {
        tracker.RecordDropped(
            RemoteInputKind.Keyboard,
            RemoteInputDropReason.InvalidTransform);
    }

    Assert.Equal([1L, 4L, 8L], Counts(sink.Events));
}

[Fact]
public void Diagnostic_fields_never_include_input_content()
{
    var sink = new RecordingSink();
    var tracker = new RemoteInputDiagnosticTracker(sink);

    tracker.Record(
        RemoteInputKind.Keyboard,
        RemoteInputBoundary.ProtocolWriteCompleted,
        encrypted: true);

    var diagnostic = Assert.Single(sink.Events);
    Assert.Equal("REMOTE_INPUT_ACTIVITY", diagnostic.Code);
    Assert.Equal(
        ["Kind", "Boundary", "Count", "Encrypted", "Sampled"],
        diagnostic.Fields!.Select(field => field.Name));
    Assert.DoesNotContain(
        diagnostic.Fields!,
        field => field.Name is "Keysym" or "VirtualKey" or "ScanCode" or
            "Text" or "Coordinate" or "Buttons" or "Payload");
}

[Fact]
public void Throwing_sink_never_changes_input_diagnostic_control_flow()
{
    var tracker = new RemoteInputDiagnosticTracker(new ThrowingSink());

    var exception = Record.Exception(() => tracker.Record(
        RemoteInputKind.Pointer,
        RemoteInputBoundary.ProtocolWriteStarted,
        encrypted: true));

    Assert.Null(exception);
}
```

增加 Pointer 有界采样测试：调用 512 次后仅记录 1、8、64、256、512。并发调用 64 次后，采样出来的 `Count` 必须无重复、严格递增，并包含 1、8、64。

- [x] **步骤 2：运行测试并确认正确失败**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RemoteInputDiagnosticTrackerTests
```

预期：编译失败，报告 `RemoteInputDiagnosticTracker`、`RemoteInputKind`、`RemoteInputBoundary` 或 `RemoteInputDropReason` 未定义。

- [x] **步骤 3：编写最少 tracker 实现**

创建以下内部类型：

```csharp
internal enum RemoteInputKind
{
    Keyboard,
    Pointer,
}

internal enum RemoteInputBoundary
{
    UiCaptured,
    UiDropped,
    ProtocolWriteStarted,
    ProtocolWriteCompleted,
}

internal enum RemoteInputDropReason
{
    SessionClosing,
    InvalidTransform,
}

internal sealed class RemoteInputDiagnosticTracker(ISafeDiagnosticSink? diagnosticSink)
{
    private readonly long[] _counts = new long[8];

    public void Record(
        RemoteInputKind kind,
        RemoteInputBoundary boundary,
        bool? encrypted = null)
    {
        var count = Increment(kind, boundary);
        if (!ShouldSample(kind, boundary, count))
        {
            return;
        }

        Write(kind, boundary, count, reason: null, encrypted);
    }

    public void RecordDropped(RemoteInputKind kind, RemoteInputDropReason reason)
    {
        var count = Increment(kind, RemoteInputBoundary.UiDropped);
        if (count != 1 && count % 4 != 0)
        {
            return;
        }

        Write(kind, RemoteInputBoundary.UiDropped, count, reason, encrypted: null);
    }

    private static bool ShouldSample(
        RemoteInputKind kind,
        RemoteInputBoundary boundary,
        long count)
    {
        if (boundary == RemoteInputBoundary.UiDropped)
        {
            return count == 1 || count % 4 == 0;
        }

        if (kind == RemoteInputKind.Keyboard)
        {
            return count == 1 || count % 8 == 0;
        }

        return count is 1 or 8 or 64 || count % 256 == 0;
    }
}
```

使用 `(int)kind * 4 + (int)boundary` 选择计数槽位和对应锁。同一槽位在锁内完成 `Interlocked.Increment`、采样判定和 `TryWrite`，保证该 Kind/Boundary 的导出 Count 保持写入顺序；不同槽位互不阻塞。`Write` 构造 `SafeDiagnosticEventInput`，只加入规格允许字段，并使用 `_diagnosticSink.TryWrite(...)`；整个 tracker 不抛出 sink 异常。

增加可控阻塞 sink 测试：延迟 Count=1 的 sink 写入，同时从其他线程推进到 Count=8；修复前事件顺序为 `[8,1]`，修复后必须为 `[1,8]`。

- [x] **步骤 4：运行 tracker 测试验证通过**

运行与步骤 2 相同的命令。

预期：全部 `RemoteInputDiagnosticTrackerTests` 通过，0 failed。

### 任务 2：在 UI 输入入口加入捕获和丢弃诊断

**文件：**
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 修改：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`

- [x] **步骤 1：编写失败的 UI 集成测试**

在现有源码集成测试中断言：

```csharp
Assert.Contains(
    "new RemoteInputDiagnosticTracker(diagnosticSink)",
    source,
    StringComparison.Ordinal);
Assert.Contains(
    "RemoteInputDropReason.SessionClosing",
    source,
    StringComparison.Ordinal);
Assert.Contains(
    "RemoteInputDropReason.InvalidTransform",
    source,
    StringComparison.Ordinal);
Assert.Contains(
    "RemoteInputBoundary.UiCaptured",
    source,
    StringComparison.Ordinal);
Assert.DoesNotContain("keysym", source, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("Coordinate", source, StringComparison.Ordinal);
```

另行断言键盘和指针各自通过私有辅助方法记录，使事件处理器不直接构造诊断字段：

```csharp
Assert.Contains("RecordKeyboardCaptured()", source, StringComparison.Ordinal);
Assert.Contains("RecordPointerCaptured()", source, StringComparison.Ordinal);
Assert.Contains("RecordKeyboardDropped(", source, StringComparison.Ordinal);
Assert.Contains("RecordPointerDropped(", source, StringComparison.Ordinal);
```

- [x] **步骤 2：运行 UI 集成测试并确认失败**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RemoteSessionWindowInputIntegrationTests
```

预期：断言失败，因为窗口尚未创建 tracker 或记录输入边界。

- [x] **步骤 3：实现最少 UI 埋点**

在窗口中增加字段并从现有 sink 创建 tracker：

```csharp
private readonly RemoteInputDiagnosticTracker _inputDiagnostics;

_inputDiagnostics = new RemoteInputDiagnosticTracker(diagnosticSink);
```

增加无内容辅助方法：

```csharp
private void RecordKeyboardCaptured() => _inputDiagnostics.Record(
    RemoteInputKind.Keyboard,
    RemoteInputBoundary.UiCaptured);

private void RecordPointerCaptured() => _inputDiagnostics.Record(
    RemoteInputKind.Pointer,
    RemoteInputBoundary.UiCaptured);

private void RecordKeyboardDropped(RemoteInputDropReason reason) =>
    _inputDiagnostics.RecordDropped(RemoteInputKind.Keyboard, reason);

private void RecordPointerDropped(RemoteInputDropReason reason) =>
    _inputDiagnostics.RecordDropped(RemoteInputKind.Pointer, reason);
```

键盘处理顺序固定为：先调用统一的 `IsInputClosing()`（覆盖 closing 通知、会话停止和 lifetime 取消）并记录 `SessionClosing`；需要 transform 的 KeyDown 再判断 `CurrentTransform().IsValid` 并记录 `InvalidTransform`；确认进入处理路径后记录 `UiCaptured`。KeyUp 和 CharacterReceived 在关闭状态记录丢弃，否则记录捕获。

指针入口在 `QueuePointerSend` 和 `QueueWheelSend` 中先调用 `IsInputClosing()` 并记录 `SessionClosing`；调用 `TryGetRemotePoint` 失败时记录 `InvalidTransform`；成功后记录 `UiCaptured`，再保持现有本地状态更新和发送顺序。`BeginClosingDiagnostics()` 原子设置 `_closingStarted`，使关闭通知到会话 token 取消之间的窗口也不会被误记为捕获。

- [x] **步骤 4：运行 UI 集成测试验证通过**

运行与步骤 2 相同的命令。

预期：`RemoteSessionWindowInputIntegrationTests` 全部通过。

### 任务 3：在协议写入边界加入 Started/Completed 诊断

**文件：**
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 创建：`tests/WinARD.Desktop.Tests/Services/RemoteInputProtocolDiagnosticsTests.cs`

- [x] **步骤 1：编写失败的协议边界测试**

成功路径使用真实 `RfbClient` 和 `MemoryStream`：

```csharp
[Fact]
public async Task Pointer_write_records_started_and_completed_without_content()
{
    var sink = new RecordingSink();
    await using var client = new RfbClient(
        new MemoryStream(),
        diagnosticSink: sink);

    await client.SendPointerAsync(1, 12, 34, CancellationToken.None);

    AssertBoundaries(
        sink.Events,
        RemoteInputKind.Pointer,
        RemoteInputBoundary.ProtocolWriteStarted,
        RemoteInputBoundary.ProtocolWriteCompleted);
}

[Fact]
public async Task Key_write_records_started_and_completed_without_content()
{
    var sink = new RecordingSink();
    await using var client = new RfbClient(
        new MemoryStream(),
        diagnosticSink: sink);

    await client.SendKeyAsync(0xff0d, true, CancellationToken.None);

    AssertBoundaries(
        sink.Events,
        RemoteInputKind.Keyboard,
        RemoteInputBoundary.ProtocolWriteStarted,
        RemoteInputBoundary.ProtocolWriteCompleted);
}
```

失败流使用一个 `Stream`，其 `WriteAsync` 抛出 `IOException`；断言只出现 `ProtocolWriteStarted`，没有 `ProtocolWriteCompleted`，原异常仍向上传播。

- [x] **步骤 2：运行协议边界测试并确认失败**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RemoteInputProtocolDiagnosticsTests
```

预期：断言失败，因为 `RfbClient` 尚未记录输入协议边界。

- [x] **步骤 3：编写最少协议埋点**

在 `RfbClient` 构造函数中创建独立 tracker：

```csharp
private readonly RemoteInputDiagnosticTracker _inputDiagnostics;

_inputDiagnostics = new RemoteInputDiagnosticTracker(diagnosticSink);
```

在 `WaitUntilEncryptedAsync` 完成后、真实 writer 调用前记录 Started，在 writer 正常完成后记录 Completed：

```csharp
var encrypted = _transport.IsEncrypted;
_inputDiagnostics.Record(
    RemoteInputKind.Pointer,
    RemoteInputBoundary.ProtocolWriteStarted,
    encrypted);
await new PointerEventWriter(new RfbWriter(_transport)).WriteAsync(
    buttons,
    x,
    y,
    cancellationToken).ConfigureAwait(false);
_inputDiagnostics.Record(
    RemoteInputKind.Pointer,
    RemoteInputBoundary.ProtocolWriteCompleted,
    encrypted);
```

键盘路径使用相同结构并将 Kind 改为 `Keyboard`。不添加 catch，不改变现有异常传播和 `REMOTE_INPUT_FAILED` 处理。

- [x] **步骤 4：运行协议边界测试验证通过**

运行与步骤 2 相同的命令。

预期：成功和失败路径测试全部通过。

### 任务 4：回归验证和构建产物

**文件：**
- 检查全部本次修改文件
- 不 stage、不 commit

- [x] **步骤 1：运行 Desktop 全量测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64
```

预期：0 failed，0 errors。

- [x] **步骤 2：运行完整解决方案测试**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64
```

预期：0 failed，0 errors。

- [x] **步骤 3：运行格式和 diff 检查**

```powershell
dotnet format WinARD.sln --verify-no-changes
git diff --check
```

预期：两个命令退出码均为 0。

- [x] **步骤 4：编译 Release x64**

```powershell
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore
```

预期：Build succeeded，0 warnings，0 errors。

- [x] **步骤 5：生成便携测试包并校验哈希**

先检查仓库现有打包脚本和最近产物采用的命令，复用相同的 `dotnet publish` 与归档结构，不覆盖用户现有 zip。新包名称包含 `input-boundary-diagnostics` 和日期；完成后运行：

```powershell
Get-FileHash '<新 zip 的绝对路径>' -Algorithm SHA256
```

交付时明确说明：自动化验证只证明诊断埋点和构建正确，不能证明真实 Mac 输入已经恢复；下一步仍需按规格中的短复现步骤导出诊断。
