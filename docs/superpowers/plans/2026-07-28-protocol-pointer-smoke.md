# 协议层鼠标输入探针实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 为 `WinARD.ProtocolProbe` 增加 `--pointer-smoke`，绕过 GUI 向真实 Mac 发送一次无按钮鼠标移动，以区分 GUI 输入故障和 ARD/macOS 输入链路故障。

**架构：** 命令行解析产生一个明确的探针请求；`ProbeRunner` 复用现有认证与 RFB 初始化，再调用独立的 `PointerSmokeProbe` 写出标准六字节 PointerEvent。结果对象只返回远程尺寸和发送坐标，输出层明确说明“写入成功不等于服务端执行”。

**技术栈：** .NET 8、C#、xUnit、RFB 3.8、AppleRemoteDesktop Security Type 30。

---

## 文件结构

- 创建 `tools/WinARD.ProtocolProbe/ProbeCommandLine.cs`：解析认证、抓帧和鼠标探针三种互斥运行模式。
- 创建 `tools/WinARD.ProtocolProbe/PointerSmokeProbe.cs`：计算中心坐标并通过现有 `PointerEventWriter` 写出无按钮移动。
- 修改 `tools/WinARD.ProtocolProbe/Program.cs`：使用新解析结果调用 runner，并输出鼠标探针结果。
- 修改 `tools/WinARD.ProtocolProbe/ProbeRunner.cs`：在认证后按请求选择不初始化、抓帧或鼠标探针路径。
- 修改 `tools/WinARD.ProtocolProbe/ProbeResult.cs`：携带可选的鼠标探针结果。
- 修改 `tools/WinARD.ProtocolProbe/ProbeOutput.cs`：格式化不含敏感信息的鼠标探针输出。
- 修改 `tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`：覆盖参数解析、完整协议顺序、线格式和写入失败。

### 任务 1：锁定命令行模式

**文件：**
- 创建：`tools/WinARD.ProtocolProbe/ProbeCommandLine.cs`
- 修改：`tools/WinARD.ProtocolProbe/Program.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`

- [ ] **步骤 1：编写失败的参数解析测试**

在 `ProtocolProbeTests` 中加入：

```csharp
[Theory]
[InlineData("--pointer-smoke", ProbeMode.PointerSmoke)]
public void Command_line_accepts_pointer_smoke(string argument, ProbeMode expected)
{
    Assert.True(ProbeCommandLine.TryParse([argument], out var request));
    Assert.Equal(expected, request.Mode);
    Assert.Null(request.CaptureFirstFramePath);
}

[Theory]
[InlineData("--pointer-smoke", "extra")]
[InlineData("--pointer-smoke", "--capture-first-frame", "frame.bmp")]
public void Command_line_rejects_invalid_pointer_smoke_combinations(params string[] arguments)
{
    Assert.False(ProbeCommandLine.TryParse(arguments, out _));
}
```

- [ ] **步骤 2：运行测试并确认红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "Command_line_"
```

预期：编译失败，提示 `ProbeCommandLine`、`ProbeMode` 尚不存在。

- [ ] **步骤 3：实现最小命令行模型和解析器**

创建 `ProbeCommandLine.cs`：

```csharp
namespace WinARD.ProtocolProbe;

public enum ProbeMode
{
    Authentication,
    CaptureFirstFrame,
    PointerSmoke,
}

public sealed record ProbeRequest(ProbeMode Mode, string? CaptureFirstFramePath = null);

public static class ProbeCommandLine
{
    public static bool TryParse(string[] args, out ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = new ProbeRequest(ProbeMode.Authentication);
        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "--pointer-smoke", StringComparison.Ordinal))
        {
            request = new ProbeRequest(ProbeMode.PointerSmoke);
            return true;
        }

        if (args.Length == 2 &&
            string.Equals(args[0], "--capture-first-frame", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(args[1]))
        {
            request = new ProbeRequest(ProbeMode.CaptureFirstFrame, args[1]);
            return true;
        }

        return false;
    }
}
```

在 `Program.cs` 中用 `ProbeCommandLine.TryParse` 替换 `TryReadCapturePath`，并更新 usage：

```text
Usage: WinARD.ProtocolProbe [--capture-first-frame <path.bgra|path.bmp> | --pointer-smoke].
```

- [ ] **步骤 4：运行参数解析测试并确认绿灯**

运行同一步骤 2。预期：所有 `Command_line_` 测试通过。

- [ ] **步骤 5：提交命令行模式**

```powershell
git add -- tools/WinARD.ProtocolProbe/ProbeCommandLine.cs tools/WinARD.ProtocolProbe/Program.cs tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs
git commit -m "feat: parse protocol pointer smoke mode"
```

### 任务 2：发送标准无按钮 PointerEvent

**文件：**
- 创建：`tools/WinARD.ProtocolProbe/PointerSmokeProbe.cs`
- 修改：`tools/WinARD.ProtocolProbe/ProbeRunner.cs`
- 修改：`tools/WinARD.ProtocolProbe/ProbeResult.cs`
- 修改：`tools/WinARD.ProtocolProbe/ProbeOutput.cs`
- 修改：`tools/WinARD.ProtocolProbe/Program.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`

- [ ] **步骤 1：编写失败的线格式单元测试**

加入一个可写内存流测试：

```csharp
[Fact]
public async Task Pointer_smoke_writes_no_button_event_at_framebuffer_center()
{
    await using var stream = new MemoryStream();

    var result = await PointerSmokeProbe.SendAsync(
        stream,
        width: 3360,
        height: 2100,
        CancellationToken.None);

    Assert.Equal(new ProbePointerSmoke(3360, 2100, 1680, 1050), result);
    Assert.Equal([5, 0, 0x06, 0x90, 0x04, 0x1A], stream.ToArray());
}
```

- [ ] **步骤 2：运行单元测试并确认红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "Pointer_smoke_writes_no_button_event_at_framebuffer_center"
```

预期：编译失败，提示 `PointerSmokeProbe` 和 `ProbePointerSmoke` 尚不存在。

- [ ] **步骤 3：实现最小鼠标探针**

在 `ProbeResult.cs` 增加：

```csharp
public sealed record ProbePointerSmoke(int Width, int Height, int X, int Y);
```

创建 `PointerSmokeProbe.cs`：

```csharp
using WinARD.Remote.Protocol.Input;
using WinARD.Remote.Protocol.IO;

namespace WinARD.ProtocolProbe;

public static class PointerSmokeProbe
{
    public static async ValueTask<ProbePointerSmoke> SendAsync(
        Stream stream,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var x = width / 2;
        var y = height / 2;
        await new PointerEventWriter(new RfbWriter(stream)).WriteAsync(
            buttons: 0,
            x,
            y,
            cancellationToken);
        return new ProbePointerSmoke(width, height, x, y);
    }
}
```

- [ ] **步骤 4：运行线格式测试并确认绿灯**

运行同一步骤 2。预期：测试通过，写出 `05 00 06 90 04 1A`。

- [ ] **步骤 5：编写失败的完整协议顺序测试**

扩展模拟服务器：完成 ARD 认证后发送 `ServerInit(4, 2)`，读取既有 44 字节初始化声明，然后断言最后六字节为：

```csharp
Assert.Equal([5, 0, 0, 2, 0, 1], await ReadExactlyAsync(stream, 6));
```

客户端测试调用：

```csharp
var result = await runner.RunAsync(
    host,
    port,
    username,
    password,
    new ProbeRequest(ProbeMode.PointerSmoke),
    CancellationToken.None);

Assert.Equal(new ProbePointerSmoke(4, 2, 2, 1), result.PointerSmoke);
Assert.Null(result.Capture);
```

- [ ] **步骤 6：运行完整协议测试并确认红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "Pointer_smoke_initializes_then_sends_center_move"
```

预期：编译失败，因为 `ProbeRunner` 尚不接受 `ProbeRequest`，`ProbeResult` 尚无 `PointerSmoke`。

- [ ] **步骤 7：把鼠标模式接入 runner 和输出**

为 `ProbeRunner` 增加接收 `ProbeRequest` 的重载。认证模式保持认证后返回；其他模式先调用 `RfbSessionInitializer.InitializeAsync`。鼠标模式调用：

```csharp
var pointerSmoke = await PointerSmokeProbe.SendAsync(
    stream,
    server.Width,
    server.Height,
    operationCancellation.Token);
return new ProbeResult(handshake.Version, handshake.SecurityType, PointerSmoke: pointerSmoke);
```

将 `ProbeResult` 扩展为：

```csharp
public sealed record ProbeResult(
    RfbVersion Version,
    RfbSecurityType SecurityType,
    ProbeCapture? Capture = null,
    ProbePointerSmoke? PointerSmoke = null);
```

在 `ProbeOutput` 增加：

```csharp
public static string FormatPointerSmoke(ProbePointerSmoke pointer) =>
    $"Pointer event written: {pointer.X},{pointer.Y} within {pointer.Width}x{pointer.Height}; server execution is not acknowledged by RFB.";
```

`Program.cs` 在结果非空时打印该行。

- [ ] **步骤 8：运行完整协议测试并确认绿灯**

运行同一步骤 6。预期：测试通过。

- [ ] **步骤 9：验证输入写入失败不会返回成功**

加入一个 `Stream`，在 `WriteAsync` 收到 PointerEvent 时抛出 `IOException`，调用 `PointerSmokeProbe.SendAsync` 并断言：

```csharp
await Assert.ThrowsAsync<IOException>(() =>
    PointerSmokeProbe.SendAsync(stream, 4, 2, CancellationToken.None).AsTask());
```

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "Pointer_smoke_propagates_write_failure"
```

预期：测试通过，失败不会被转换为 `ProbePointerSmoke`。

- [ ] **步骤 10：提交协议鼠标探针**

```powershell
git add -- tools/WinARD.ProtocolProbe/PointerSmokeProbe.cs tools/WinARD.ProtocolProbe/ProbeRunner.cs tools/WinARD.ProtocolProbe/ProbeResult.cs tools/WinARD.ProtocolProbe/ProbeOutput.cs tools/WinARD.ProtocolProbe/Program.cs tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs
git commit -m "feat: add protocol pointer smoke probe"
```

### 任务 3：回归验证和 Release 编译

**文件：**
- 修改：`README.md`
- 不修改：`docs/testing/compatibility-matrix.md`（真实 Mac 结果返回后才填写；本次编译不伪造 Pass）

- [ ] **步骤 1：补充 README 使用说明**

在协议探针示例中加入：

```powershell
dotnet run --project tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj -c Release -- --pointer-smoke
```

说明该命令只发送无按钮鼠标移动，并要求观察 Mac 光标是否移动；“written”不表示服务端确认执行。

- [ ] **步骤 2：运行格式和定向测试**

```powershell
dotnet format WinARD.sln --verify-no-changes --no-restore
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore
```

预期：退出码均为 0，无失败测试。

- [ ] **步骤 3：运行完整解决方案测试**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-restore
```

预期：所有测试通过；不得用已有旧结果代替本次输出。

- [ ] **步骤 4：编译 Release 探针及解决方案**

```powershell
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
```

预期：0 警告、0 错误，并生成：

```text
tools/WinARD.ProtocolProbe/bin/x64/Release/net8.0-windows10.0.19041.0/WinARD.ProtocolProbe.exe
```

- [ ] **步骤 5：提交文档并确认仓库状态**

```powershell
git add -- README.md
git commit -m "docs: document protocol pointer smoke probe"
git status --short
```

预期：提交成功，`git status --short` 无输出。真实 Mac 测试完成前，不把兼容性矩阵标记为 Pass。
