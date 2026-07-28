# ARD StateChange 与 Tickle 保活实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 WinARD 在 RFB `003.889` 会话中安全处理服务端 `StateChange (0x14)`，并在收到 `Tickle (status=4)` 时回复精确的 `AutoFBUpdate (0x09)`，消除 macOS 26.5 空闲约三秒自动断线。

**架构：** 在协议层新增独立的 `ArdStateChangeReader` 和结构化状态模型，并扩展现有 `ArdClientMessageWriter` 生成 AutoFBUpdate。桌面端 `RfbClient` 只负责顶层分发、状态动作和安全诊断；现有 `RfbWriter` 的按流共享写锁继续保证并发消息原子性。

**技术栈：** C# 12、.NET 8、xUnit、WinUI 3、现有 `RfbReader`/`RfbWriter`、现有安全诊断管线。

---

## 文件结构

- 创建：`src/WinARD.Remote.Protocol/Ard/ArdStateChange.cs` — ARD 状态码与解析结果模型。
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdStateChangeReader.cs` — `0x14` 消息体的有界大端解析。
- 创建：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdStateChangeReaderTests.cs` — StateChange 字节级和失败指纹测试。
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs` — 增加 StateChange 和 AutoFBUpdate 常量。
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs` — 增加固定 16 字节 AutoFBUpdate 写入。
- 修改：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdClientMessageWriterTests.cs` — 精确线字节和参数验证。
- 修改：`src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs` — 增加 ARD 状态失败种类和读取阶段。
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs` — 分发 `0x14`、回复 Tickle、记录诊断。
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs` — 运行期连续处理和版本隔离集成测试。
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs` — 明确显示远端主动关闭。
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs` — UI 和诊断回归测试。

## 任务 1：实现有界 StateChange 解析器

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdStateChange.cs`
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdStateChangeReader.cs`
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs`
- 修改：`src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdStateChangeReaderTests.cs`

- [ ] **步骤 1：编写最小合法消息和 extra 边界的失败测试**

```csharp
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;
using Xunit;

namespace WinARD.Remote.Protocol.Tests.Ard;

public sealed class ArdStateChangeReaderTests
{
    [Fact]
    public async Task Reads_minimum_payload_and_preserves_nonzero_padding()
    {
        await using var stream = new MemoryStream(
            [0xA5, 0x00, 0x04, 0x12, 0x34, 0x00, 0x04]);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        var result = await ArdStateChangeReader.ReadBodyAsync(
            reader, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal((byte)0xA5, result.Padding);
        Assert.Equal((ushort)4, result.PayloadSize);
        Assert.Equal((ushort)0x1234, result.Flags);
        Assert.Equal((ushort)ArdStateChangeStatus.Tickle, result.Status);
        Assert.Equal(0, result.ExtraPayloadLength);
    }

    [Fact]
    public async Task Consumes_extra_payload_without_stealing_the_next_message()
    {
        await using var stream = new MemoryStream(
            [0x00, 0x00, 0x07, 0x00, 0x02, 0x12, 0x34, 0xAA, 0xBB, 0xCC, 0x7E]);
        var reader = new RfbReader(stream, ProtocolLimits.Default);

        var result = await ArdStateChangeReader.ReadBodyAsync(
            reader, ProtocolLimits.Default, CancellationToken.None);

        Assert.Equal((ushort)0x1234, result.Status);
        Assert.Equal(3, result.ExtraPayloadLength);
        Assert.Equal((byte)0x7E, await reader.ReadByteAsync(CancellationToken.None));
    }
}
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArdStateChangeReaderTests
```

预期：编译失败，报告 `ArdStateChangeReader`、`ArdStateChangeStatus` 或 `ArdStateChange` 未定义。

- [ ] **步骤 3：追加失败枚举值和协议常量**

在已有枚举末尾追加，禁止重排旧值：

```csharp
MalformedArdStateChange = 6,
RemoteSessionClosed = 7,
```

```csharp
ArdStateChangeHeader = 6,
ArdStateChangePayload = 7,
```

在 `ArdProtocolConstants` 中增加：

```csharp
internal const byte AutoFramebufferUpdate = 0x09;
internal const byte StateChange = 0x14;
```

- [ ] **步骤 4：实现状态模型**

`ArdStateChange.cs`：

```csharp
namespace WinARD.Remote.Protocol.Ard;

public enum ArdStateChangeStatus : ushort
{
    LocalUserClosed = 1,
    PasteboardChanged = 2,
    PasteboardDataNeeded = 3,
    Tickle = 4,
    Sleep = 5,
    Wake = 6,
    CursorHidden = 11,
    CursorVisible = 12,
}

public sealed record ArdStateChange(
    byte Padding,
    ushort PayloadSize,
    ushort Flags,
    ushort Status,
    int ExtraPayloadLength);
```

- [ ] **步骤 5：实现最小解析器**

`ArdStateChangeReader.cs`：

```csharp
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.IO;

namespace WinARD.Remote.Protocol.Ard;

public static class ArdStateChangeReader
{
    private const int HeaderLength = 3;
    private const int FixedPayloadLength = 4;

    public static async ValueTask<ArdStateChange> ReadBodyAsync(
        RfbReader reader,
        ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();

        byte padding;
        ushort payloadSize;
        try
        {
            padding = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            payloadSize = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.MalformedArdStateChange,
                RfbProtocolReadStage.ArdStateChangeHeader,
                ArdProtocolConstants.StateChange));
        }

        if (payloadSize < FixedPayloadLength || HeaderLength + payloadSize > limits.MaxMessageBytes)
        {
            throw RfbProtocolException.Create(
                $"ARD StateChange payload length {payloadSize} is invalid.",
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.MalformedArdStateChange,
                    RfbProtocolReadStage.ArdStateChangeHeader,
                    ArdProtocolConstants.StateChange));
        }

        try
        {
            var flags = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var status = await reader.ReadUInt16Async(cancellationToken).ConfigureAwait(false);
            var extraPayloadLength = payloadSize - FixedPayloadLength;
            _ = await reader.ReadBytesAsync(extraPayloadLength, cancellationToken).ConfigureAwait(false);
            return new ArdStateChange(padding, payloadSize, flags, status, extraPayloadLength);
        }
        catch (RfbProtocolException exception)
        {
            throw exception.WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.MalformedArdStateChange,
                RfbProtocolReadStage.ArdStateChangePayload,
                ArdProtocolConstants.StateChange));
        }
    }
}
```

- [ ] **步骤 6：运行最小测试验证通过**

运行步骤 2 的命令。预期：2 个测试通过。

- [ ] **步骤 7：补齐非法长度、截断、预算和状态码测试**

```csharp
[Theory]
[InlineData(0)]
[InlineData(1)]
[InlineData(2)]
[InlineData(3)]
public async Task Rejects_payload_smaller_than_flags_and_status(ushort size)
{
    await using var stream = new MemoryStream([0, (byte)(size >> 8), (byte)size]);
    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        ArdStateChangeReader.ReadBodyAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            ProtocolLimits.Default,
            CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.MalformedArdStateChange, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangeHeader, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}

[Fact]
public async Task Payload_truncation_preserves_truncated_read()
{
    await using var stream = new MemoryStream([0, 0, 6, 0, 0, 0, 4, 0xAA]);
    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        ArdStateChangeReader.ReadBodyAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            ProtocolLimits.Default,
            CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}

[Fact]
public async Task Rejects_total_message_above_configured_budget()
{
    var limits = new ProtocolLimits(6, 1024);
    await using var stream = new MemoryStream([0, 0, 4, 0, 0, 0, 4]);
    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        ArdStateChangeReader.ReadBodyAsync(
            new RfbReader(stream, limits), limits, CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.MalformedArdStateChange, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangeHeader, exception.Failure?.ReadStage);
}
```

继续加入以下完整测试：

```csharp
[Theory]
[InlineData(new byte[] { })]
[InlineData(new byte[] { 0 })]
[InlineData(new byte[] { 0, 0 })]
public async Task Header_truncation_preserves_truncated_read(byte[] input)
{
    await using var stream = new MemoryStream(input);
    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        ArdStateChangeReader.ReadBodyAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            ProtocolLimits.Default,
            CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangeHeader, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}

[Theory]
[InlineData((ushort)1)]
[InlineData((ushort)2)]
[InlineData((ushort)3)]
[InlineData((ushort)4)]
[InlineData((ushort)5)]
[InlineData((ushort)6)]
[InlineData((ushort)11)]
[InlineData((ushort)12)]
[InlineData((ushort)0x7FFF)]
public async Task Accepts_known_and_unknown_status_values(ushort status)
{
    await using var stream = new MemoryStream(
        [0, 0, 4, 0, 0, (byte)(status >> 8), (byte)status]);

    var result = await ArdStateChangeReader.ReadBodyAsync(
        new RfbReader(stream, ProtocolLimits.Default),
        ProtocolLimits.Default,
        CancellationToken.None);

    Assert.Equal(status, result.Status);
}

[Fact]
public async Task Pre_cancelled_read_is_not_wrapped_as_protocol_failure()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await using var stream = new MemoryStream([0, 0, 4, 0, 0, 0, 4]);

    var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        ArdStateChangeReader.ReadBodyAsync(
            new RfbReader(stream, ProtocolLimits.Default),
            ProtocolLimits.Default,
            cancellation.Token).AsTask());

    Assert.Equal(cancellation.Token, exception.CancellationToken);
}
```

- [ ] **步骤 8：运行全部解析器测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArdStateChangeReaderTests
git add src/WinARD.Remote.Protocol/Ard/ArdStateChange.cs src/WinARD.Remote.Protocol/Ard/ArdStateChangeReader.cs src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs tests/WinARD.Remote.Protocol.Tests/Ard/ArdStateChangeReaderTests.cs
git commit -m "feat: parse ARD state change messages"
```

预期：解析器测试全部通过，提交成功。

## 任务 2：实现精确 AutoFBUpdate 写入

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdClientMessageWriterTests.cs`

- [ ] **步骤 1：编写精确线字节失败测试**

```csharp
[Fact]
public async Task Auto_framebuffer_update_writes_exact_full_region_message()
{
    await using var stream = new TrackingMemoryStream();
    var writer = new ArdClientMessageWriter(new RfbWriter(stream));

    await writer.WriteAutoFramebufferUpdateAsync(0x1234, 0x5678, CancellationToken.None);

    Assert.Equal(
        [0x09, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0x12, 0x34, 0x56, 0x78],
        stream.ToArray());
    Assert.Equal(1, stream.WriteCount);
}

[Theory]
[InlineData((ushort)0, (ushort)1)]
[InlineData((ushort)1, (ushort)0)]
public async Task Auto_framebuffer_update_rejects_zero_dimensions_before_writing(
    ushort width, ushort height)
{
    await using var stream = new TrackingMemoryStream();
    var writer = new ArdClientMessageWriter(new RfbWriter(stream));

    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        writer.WriteAutoFramebufferUpdateAsync(width, height, CancellationToken.None).AsTask());

    Assert.Empty(stream.ToArray());
    Assert.Equal(0, stream.WriteCount);
}
```

- [ ] **步骤 2：运行测试验证方法缺失**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArdClientMessageWriterTests
```

预期：编译失败，报告 `WriteAutoFramebufferUpdateAsync` 未定义。

- [ ] **步骤 3：实现固定 16 字节写入**

```csharp
public ValueTask WriteAutoFramebufferUpdateAsync(
    ushort width,
    ushort height,
    CancellationToken cancellationToken)
{
    ArgumentOutOfRangeException.ThrowIfZero(width);
    ArgumentOutOfRangeException.ThrowIfZero(height);

    var message = new byte[16];
    message[0] = ArdProtocolConstants.AutoFramebufferUpdate;
    BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), 1);
    BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), 0);
    BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(8), 0);
    BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(10), 0);
    BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(12), width);
    BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(14), height);
    return _writer.WriteMessageAsync(message, cancellationToken);
}
```

- [ ] **步骤 4：运行 writer 测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArdClientMessageWriterTests
git add src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs tests/WinARD.Remote.Protocol.Tests/Ard/ArdClientMessageWriterTests.cs
git commit -m "feat: write ARD auto framebuffer updates"
```

预期：writer 测试全部通过，提交成功。

## 任务 3：接入运行期分发和安全诊断

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdServerMessage.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：编写 Tickle 连续处理失败测试**

在测试类增加消息 helper：

```csharp
private static byte[] ArdStateChange(
    ushort status,
    ushort flags = 0,
    byte padding = 0,
    byte[]? extra = null)
{
    extra ??= [];
    var payloadSize = checked((ushort)(4 + extra.Length));
    var bytes = new byte[4 + payloadSize];
    bytes[0] = 0x14;
    bytes[1] = padding;
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), payloadSize);
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), flags);
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), status);
    extra.CopyTo(bytes, 8);
    return bytes;
}
```

```csharp
[Fact]
public async Task Rfb_client_replies_to_ARD_tickle_and_continues_to_framebuffer_update()
{
    var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
    await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.889\n"),
            .. ArdServerInit(2, 1),
            .. ArdStateChange((ushort)ArdStateChangeStatus.Tickle, 0x1234, extra: [0xAA]),
            .. CursorOnlyUpdate(),
        ]);
    await using var client = new RfbClient(stream, diagnosticSink: sink);

    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);
    var writtenBeforeReceive = stream.WrittenBytes.Length;
    using var message = Assert.IsType<RemoteCursorMessage>(
        await client.ReceiveAsync(CancellationToken.None));

    Assert.Equal(
        [0x09, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1],
        stream.WrittenBytes[writtenBeforeReceive..]);
    var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
    Assert.Contains(diagnostic.Fields, field => field.Name == "Action" && field.Value == "AutoFBUpdateSent");
}
```

- [ ] **步骤 2：运行测试确认当前 `0x14` 被拒绝**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Rfb_client_replies_to_ARD_tickle
```

预期：FAIL，失败指纹为 `UnexpectedServerMessage/ServerMessageType/0x14`。

- [ ] **步骤 3：公开单一 StateChange 类型值**

不要把整个内部常量类公开。在 `ArdServerMessage` 增加：

```csharp
public const byte StateChangeType = 0x14;
```

并让 `ArdProtocolConstants.StateChange` 引用它：

```csharp
internal const byte StateChange = ArdServerMessage.StateChangeType;
```

- [ ] **步骤 4：实现 `0x14` 分发、动作和诊断**

在 `RfbClient.ReceiveAsync` switch 中加入：

```csharp
case ArdServerMessage.StateChangeType when handshake.Version == RfbVersion.V3_889:
    var stateChange = await ArdStateChangeReader.ReadBodyAsync(
        reader, ProtocolLimits.Default, cancellationToken).ConfigureAwait(false);
    var action = GetStateChangeAction(stateChange.Status);

    if (stateChange.Status == (ushort)ArdStateChangeStatus.LocalUserClosed)
    {
        WriteStateChangeDiagnostic(stateChange, action);
        throw RfbProtocolException.Create(
            "The remote Apple Remote Desktop user closed the session.",
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.RemoteSessionClosed,
                RfbProtocolReadStage.ArdStateChangePayload,
                ArdServerMessage.StateChangeType));
    }

    if (stateChange.Status == (ushort)ArdStateChangeStatus.Tickle)
    {
        try
        {
            await new ArdClientMessageWriter(new RfbWriter(_stream))
                .WriteAutoFramebufferUpdateAsync(
                    checked((ushort)framebuffer.Width),
                    checked((ushort)framebuffer.Height),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WriteStateChangeDiagnostic(stateChange, "AutoFBUpdateFailed", exception);
            throw;
        }
    }

    WriteStateChangeDiagnostic(stateChange, action);
    continue;
```

增加 helper：

```csharp
private static string GetStateChangeAction(ushort status) => status switch
{
    (ushort)ArdStateChangeStatus.LocalUserClosed => "RemoteSessionClosed",
    (ushort)ArdStateChangeStatus.Tickle => "AutoFBUpdateSent",
    (ushort)ArdStateChangeStatus.PasteboardChanged => "Consumed",
    (ushort)ArdStateChangeStatus.PasteboardDataNeeded => "Consumed",
    (ushort)ArdStateChangeStatus.Sleep => "Consumed",
    (ushort)ArdStateChangeStatus.Wake => "Consumed",
    (ushort)ArdStateChangeStatus.CursorHidden => "Consumed",
    (ushort)ArdStateChangeStatus.CursorVisible => "Consumed",
    _ => "UnknownConsumed",
};

private void WriteStateChangeDiagnostic(
    ArdStateChange stateChange,
    string action,
    Exception? exception = null)
{
    _diagnosticSink.TryWrite(new SafeDiagnosticEventInput(
        "ARD_STATE_CHANGE",
        Guid.NewGuid().ToString("N"),
        "Apple Remote Desktop state change processed.",
        [
            new("Status", stateChange.Status.ToString(CultureInfo.InvariantCulture)),
            new("Flags", $"0x{stateChange.Flags.ToString("X4", CultureInfo.InvariantCulture)}"),
            new("PayloadSize", stateChange.PayloadSize.ToString(CultureInfo.InvariantCulture)),
            new("Action", action),
        ],
        exception));
}
```

- [ ] **步骤 5：运行 Tickle 测试验证通过**

运行步骤 2 的命令。预期：PASS，响应恰好 16 字节，后续帧仍被解析。

- [ ] **步骤 6：补齐状态、版本和失败诊断测试**

加入非终止状态连续处理测试：

```csharp
[Theory]
[InlineData((ushort)2, "Consumed")]
[InlineData((ushort)3, "Consumed")]
[InlineData((ushort)5, "Consumed")]
[InlineData((ushort)6, "Consumed")]
[InlineData((ushort)11, "Consumed")]
[InlineData((ushort)12, "Consumed")]
[InlineData((ushort)0x7FFF, "UnknownConsumed")]
public async Task Rfb_client_consumes_nonterminal_ARD_state_before_next_message(
    ushort status,
    string expectedAction)
{
    var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
    await using var stream = new ScriptedDuplexStream(
        [.. Handshake("RFB 003.889\n"), .. ArdServerInit(2, 1), .. ArdStateChange(status), .. CursorOnlyUpdate()]);
    await using var client = new RfbClient(stream, diagnosticSink: sink);

    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);
    using var message = Assert.IsType<RemoteCursorMessage>(
        await client.ReceiveAsync(CancellationToken.None));

    var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
    Assert.Contains(
        diagnostic.Fields,
        field => field.Name == "Action" && field.Value == expectedAction);
}
```

加入版本隔离和失败指纹测试：

```csharp
[Fact]
public async Task Standard_RFB_rejects_ARD_state_change()
{
    await using var stream = new ScriptedDuplexStream(
        [.. Handshake("RFB 003.008\n"), .. ServerInit(1, 1), .. ArdStateChange(4)]);
    await using var client = new RfbClient(stream);
    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);

    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        client.ReceiveAsync(CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.UnexpectedServerMessage, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}

[Fact]
public async Task Malformed_ARD_state_change_reports_header_context()
{
    await using var stream = new ScriptedDuplexStream(
        [.. Handshake("RFB 003.889\n"), .. ArdServerInit(1, 1), 0x14, 0, 0, 3]);
    await using var client = new RfbClient(stream);
    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);

    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        client.ReceiveAsync(CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.MalformedArdStateChange, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangeHeader, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}

[Fact]
public async Task Truncated_ARD_state_payload_preserves_truncated_read()
{
    await using var stream = new ScriptedDuplexStream(
        [.. Handshake("RFB 003.889\n"), .. ArdServerInit(1, 1), 0x14, 0, 0, 6, 0, 0, 0, 4, 0xAA]);
    await using var client = new RfbClient(stream);
    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);

    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        client.ReceiveAsync(CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}

[Fact]
public async Task Local_user_closed_reports_remote_session_closed()
{
    await using var stream = new ScriptedDuplexStream(
        [.. Handshake("RFB 003.889\n"), .. ArdServerInit(1, 1), .. ArdStateChange(1)]);
    await using var client = new RfbClient(stream);
    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);

    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        client.ReceiveAsync(CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.RemoteSessionClosed, exception.Failure?.Kind);
    Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, exception.Failure?.ReadStage);
    Assert.Equal((byte)0x14, exception.Failure?.ServerMessageType);
}
```

为现有 `ScriptedDuplexStream` 增加下一次写入注入：

```csharp
private IOException? _nextWriteException;

public void FailNextWrite(IOException exception) =>
    _nextWriteException = exception ?? throw new ArgumentNullException(nameof(exception));

public override ValueTask WriteAsync(
    ReadOnlyMemory<byte> buffer,
    CancellationToken cancellationToken = default)
{
    if (Interlocked.Exchange(ref _nextWriteException, null) is { } exception)
    {
        return ValueTask.FromException(exception);
    }

    return _output.WriteAsync(buffer, cancellationToken);
}
```

用它验证写失败诊断：

```csharp
[Fact]
public async Task Tickle_write_failure_is_recorded_without_wrapping_the_io_exception()
{
    const string privateExtra = "private-extra-marker";
    var sink = new InMemorySafeDiagnosticSink(new SecretRedactor());
    await using var stream = new ScriptedDuplexStream(
        [
            .. Handshake("RFB 003.889\n"),
            .. ArdServerInit(1, 1),
            .. ArdStateChange(4, extra: System.Text.Encoding.ASCII.GetBytes(privateExtra)),
        ]);
    await using var client = new RfbClient(stream, diagnosticSink: sink);
    await client.NegotiateAsync(CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);
    var expected = new IOException("injected write failure");
    stream.FailNextWrite(expected);

    var actual = await Assert.ThrowsAsync<IOException>(() =>
        client.ReceiveAsync(CancellationToken.None).AsTask());

    Assert.Same(expected, actual);
    var diagnostic = Assert.Single(sink.Snapshot(), item => item.Code == "ARD_STATE_CHANGE");
    Assert.Contains(
        diagnostic.Fields,
        field => field.Name == "Action" && field.Value == "AutoFBUpdateFailed");
    Assert.DoesNotContain(privateExtra, System.Text.Json.JsonSerializer.Serialize(diagnostic), StringComparison.Ordinal);
}
```

- [ ] **步骤 7：运行桌面协议集成测试并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Rfb_client_"
git add src/WinARD.Remote.Protocol/Ard/ArdServerMessage.cs src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs src/WinARD.Desktop/Services/RfbClientFactory.cs tests/WinARD.Desktop.Tests/FramePresentationTests.cs
git commit -m "fix: answer ARD state change tickles"
```

预期：全部 `Rfb_client_` 测试通过，提交成功。

## 任务 4：明确显示远端主动关闭

**文件：**
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写远端关闭 UI 失败测试**

```csharp
[Fact]
public async Task Remote_session_closed_failure_uses_explicit_status_and_diagnostic_kind()
{
    var diagnosticSink = new RecordingDiagnosticSink();
    var exception = RfbProtocolException.Create(
        "remote closed",
        new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.RemoteSessionClosed,
            RfbProtocolReadStage.ArdStateChangePayload,
            0x14));
    await using var viewModel = new RemoteSessionViewModel(
        new FailingWithExceptionRuntime(exception),
        new TrackingLifetime(),
        new TrackingPresenter(),
        new InlineDispatcher(),
        clipboardBridge: null,
        diagnosticSink);

    await viewModel.StartAsync(CancellationToken.None);
    await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

    Assert.Equal("远程主机已结束会话。", viewModel.StatusMessage);
    Assert.Equal("远程主机已结束共享会话。", viewModel.Error?.Message);
    var diagnostic = Assert.Single(
        diagnosticSink.Events,
        item => item.Code == "REMOTE_SESSION_INTERRUPTED");
    Assert.Contains(
        diagnostic.Fields!,
        field => field.Name == "ProtocolFailureKind" && field.Value == "RemoteSessionClosed");
    Assert.Contains(
        diagnostic.Fields!,
        field => field.Name == "ServerMessageType" && field.Value == "0x14");
}
```

- [ ] **步骤 2：运行测试确认仍为通用中断文案**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Remote_session_closed_failure
```

预期：FAIL，实际状态仍为“连接已中断。”。

- [ ] **步骤 3：选择明确 UI 文案**

在 `MonitorLoopsAsync` 获取 `protocolException` 后加入：

```csharp
var remoteSessionClosed =
    protocolException?.Failure?.Kind == RfbProtocolFailureKind.RemoteSessionClosed;
```

将错误消息和状态改为：

```csharp
var error = WinArdError.Create(
    ConnectionStage.Connected,
    ReferenceEquals(completed, present)
        ? "REMOTE_PRESENTATION_FAILED"
        : "REMOTE_SESSION_INTERRUPTED",
    remoteSessionClosed
        ? "远程主机已结束共享会话。"
        : "远程会话已中断。",
    Guid.NewGuid().ToString("N"));
var status = remoteSessionClosed
    ? "远程主机已结束会话。"
    : ReferenceEquals(completed, present)
        ? "画面呈现失败，会话正在关闭。"
        : "连接已中断。";
```

- [ ] **步骤 4：运行目标和现有诊断测试并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Remote_session_closed_failure|FullyQualifiedName~Receive_protocol_failure"
git add src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs
git commit -m "fix: report remote ARD session closure"
```

预期：目标测试和现有失败诊断测试全部通过，普通失败仍显示原文案。

## 任务 5：全量验证、Release/x64 构建与交付

**文件：**
- 验证：`WinARD.sln`
- 产物：`src/WinARD.Desktop/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/WinARD.Desktop.exe`

- [ ] **步骤 1：检查工作树和补丁格式**

```powershell
git status --short
git diff --check cd1e336..HEAD
```

预期：工作树无未提交文件；`git diff --check` 无输出。

- [ ] **步骤 2：还原并执行警告即错误构建**

```powershell
dotnet restore WinARD.sln -p:Platform=x64
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
```

预期：Build succeeded，0 warnings，0 errors。

- [ ] **步骤 3：运行全量测试**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore
```

预期：所有测试通过，0 failed；记录实际测试总数。

- [ ] **步骤 4：确认 EXE 并计算 SHA-256**

```powershell
$exe = Resolve-Path 'src/WinARD.Desktop/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/WinARD.Desktop.exe'
Get-Item $exe | Select-Object FullName, Length, LastWriteTime
Get-FileHash $exe -Algorithm SHA256
```

预期：EXE 存在；记录绝对路径、长度、时间和 SHA-256。

- [ ] **步骤 5：执行最终代码审查**

使用 `superpowers:requesting-code-review` 审查从 `cd1e336` 到当前 HEAD 的差异，确认：

- `0x14` 只在 `003.889` 被接受；
- size-prefixed payload 始终完整且有界消费；
- 截断保留 `TruncatedRead`；
- Tickle 只回复一次精确 16 字节消息；
- 未知状态不会导致流错位；
- 诊断不包含 extra payload、凭据、端点或剪贴板内容；
- 没有定时器、Pointer Smoke 或无关重构。

预期：没有 Critical、Important 或 Must-fix 问题；若有，修复后重新运行步骤 2–4。

- [ ] **步骤 6：交付一次实机静置验证说明**

提供新 EXE 路径和哈希，只要求用户：连接同一台 macOS 26.5 主机，画面出现后不输入并静置至少 10 秒，确认不再在约 3 秒处断线且画面仍更新；若失败，仅导出一次新诊断。

在实机验证完成前，交付措辞必须是“已实现并通过自动化验证，等待实机确认”，不得宣称控制连接已稳定修复。
