# WinARD RFB 运行期协议失败指纹实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 在不导出协议载荷或原始异常文本的前提下，让下一份自动断线诊断明确指出 RFB 失败类别、读取阶段、顶层消息类型、编码 ID 和矩形序号。

**架构：** `WinARD.Remote.Protocol` 在准确的读取边界上给 `RfbProtocolException` 附加不可变结构化元数据；已有异常构造方式保持兼容。Desktop 会话监控只把允许的枚举和整数投影成 Public 诊断字段，现有安全诊断管线继续负责持久化和导出。

**技术栈：** .NET 8、C#、xUnit、现有 `RfbReader`/`FramebufferUpdateReader`/`ClipboardProtocol`、`ISafeDiagnosticSink`、WinUI 3 桌面宿主。

---

## 文件结构

### 创建

- `src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`：稳定失败类别、读取阶段以及不可变上下文合并模型。
- `tests/WinARD.Remote.Protocol.Tests/Errors/RfbProtocolFailureInfoTests.cs`：验证结构化上下文合并时保留更具体的内层信息。

### 修改

- `src/WinARD.Remote.Protocol/Errors/RfbProtocolException.cs`：保留旧构造器并支持附加/补全失败信息。
- `src/WinARD.Remote.Protocol/IO/RfbReader.cs`：把意外 EOF 标记为 `TruncatedRead`。
- `src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`：标注更新头、矩形头、编码 ID、矩形序号和 payload 阶段。
- `src/WinARD.Remote.Protocol/Clipboard/ClipboardProtocol.cs`：标注剪贴板头、长度、payload 和 UTF-8 错误。
- `src/WinARD.Desktop/Services/RfbClientFactory.cs`：标注未知顶层消息类型及已知消息分支上下文。
- `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`：把结构化失败信息投影为固定诊断字段。
- `tests/WinARD.Remote.Protocol.Tests/IO/RfbReaderTests.cs`：覆盖 EOF 类别。
- `tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`：覆盖未知编码和截断 payload 上下文。
- `tests/WinARD.Remote.Protocol.Tests/Clipboard/ClipboardProtocolTests.cs`：覆盖剪贴板阶段和脱敏边界。
- `tests/WinARD.Desktop.Tests/FramePresentationTests.cs`：覆盖未知顶层 ARD 消息的结构化信息。
- `tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`：覆盖诊断字段和秘密不泄漏。

## 任务 1：增加不可变协议失败模型

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`
- 修改：`src/WinARD.Remote.Protocol/Errors/RfbProtocolException.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Errors/RfbProtocolFailureInfoTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/IO/RfbReaderTests.cs`
- 修改：`src/WinARD.Remote.Protocol/IO/RfbReader.cs`

- [ ] **步骤 1：编写失败信息合并与 EOF 红灯测试**

新增测试，固定公共 API 和“内层优先、外层补空”的语义：

```csharp
[Fact]
public void WithContext_preserves_specific_inner_fields_and_fills_missing_outer_fields()
{
    var exception = new RfbProtocolException(
        "payload secret",
        new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.TruncatedRead,
            ReadStage: null,
            ServerMessageType: null,
            EncodingId: 1105,
            RectangleIndex: 2));

    var wrapped = exception.WithContext(new RfbProtocolFailureInfo(
        RfbProtocolFailureKind.DecoderFailure,
        RfbProtocolReadStage.FramebufferRectanglePayload,
        ServerMessageType: 0,
        EncodingId: 999,
        RectangleIndex: 7));

    Assert.Equal(RfbProtocolFailureKind.TruncatedRead, wrapped.Failure!.Kind);
    Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, wrapped.Failure.ReadStage);
    Assert.Equal((byte)0, wrapped.Failure.ServerMessageType);
    Assert.Equal(1105, wrapped.Failure.EncodingId);
    Assert.Equal(2, wrapped.Failure.RectangleIndex);
}

[Fact]
public async Task Unexpected_end_of_stream_has_truncated_read_failure_kind()
{
    var exception = await Assert.ThrowsAsync<RfbProtocolException>(() =>
        new RfbReader(new MemoryStream([0x01]), ProtocolLimits.Default)
            .ReadBytesAsync(2, CancellationToken.None).AsTask());

    Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
    Assert.Null(exception.Failure?.ReadStage);
    Assert.IsType<EndOfStreamException>(exception.InnerException);
}
```

- [ ] **步骤 2：运行定向测试并确认红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RfbProtocolFailureInfoTests|FullyQualifiedName~Unexpected_end_of_stream_has_truncated_read_failure_kind"
```

预期：编译失败，因为失败枚举、信息模型、`Failure` 和 `WithContext` 尚不存在。

- [ ] **步骤 3：实现最小失败模型与兼容异常 API**

`RfbProtocolFailureInfo.cs` 定义：

```csharp
namespace WinARD.Remote.Protocol.Errors;

public enum RfbProtocolFailureKind
{
    UnexpectedServerMessage,
    UnsupportedEncoding,
    TruncatedRead,
    MalformedFramebufferUpdate,
    MalformedClipboard,
    DecoderFailure,
}

public enum RfbProtocolReadStage
{
    ServerMessageType,
    FramebufferHeader,
    FramebufferRectangleHeader,
    FramebufferRectanglePayload,
    ClipboardHeader,
    ClipboardPayload,
}

public sealed record RfbProtocolFailureInfo(
    RfbProtocolFailureKind Kind,
    RfbProtocolReadStage? ReadStage = null,
    byte? ServerMessageType = null,
    int? EncodingId = null,
    int? RectangleIndex = null)
{
    internal RfbProtocolFailureInfo FillMissingFrom(RfbProtocolFailureInfo outer) => new(
        Kind,
        ReadStage ?? outer.ReadStage,
        ServerMessageType ?? outer.ServerMessageType,
        EncodingId ?? outer.EncodingId,
        RectangleIndex ?? outer.RectangleIndex);
}
```

在 `RfbProtocolException` 保留两个现有构造器，增加结构化构造器和上下文补全：

```csharp
public RfbProtocolException(string message, RfbProtocolFailureInfo failure)
    : base(message)
{
    Failure = failure ?? throw new ArgumentNullException(nameof(failure));
}

internal RfbProtocolException(
    string message,
    Exception innerException,
    RfbProtocolFailureInfo failure)
    : base(message, innerException) => Failure = failure;

public RfbProtocolFailureInfo? Failure { get; }

public RfbProtocolException WithContext(RfbProtocolFailureInfo context)
{
    ArgumentNullException.ThrowIfNull(context);
    return new RfbProtocolException(
        Message,
        this,
        Failure?.FillMissingFrom(context) ?? context);
}
```

在 `RfbReader.ReadExactlyAsync` 的 `EndOfStreamException` catch 中构造：

```csharp
throw new RfbProtocolException(
    $"Unexpected end of stream while reading {expectedByteCount} bytes.",
    exception,
    new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead));
```

三参数构造器保持 `internal`，供同一协议程序集的读取器附加 inner exception 与结构化信息；不要删除旧构造器。

- [ ] **步骤 4：运行定向与协议错误测试确认绿灯**

运行步骤 2 命令，再运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RfbReaderTests|FullyQualifiedName~RfbProtocolFailureInfoTests"
```

预期：全部通过；原有 EOF 消息和 inner exception 断言保持不变。

- [ ] **步骤 5：提交任务 1**

```powershell
git add src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs src/WinARD.Remote.Protocol/Errors/RfbProtocolException.cs src/WinARD.Remote.Protocol/IO/RfbReader.cs tests/WinARD.Remote.Protocol.Tests/Errors/RfbProtocolFailureInfoTests.cs tests/WinARD.Remote.Protocol.Tests/IO/RfbReaderTests.cs
git commit -m "feat: model structured RFB protocol failures"
```

## 任务 2：在协议读取边界补充精确上下文

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 修改：`src/WinARD.Remote.Protocol/Clipboard/ClipboardProtocol.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Clipboard/ClipboardProtocolTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：为未知顶层消息、未知编码和截断 payload 编写红灯测试**

扩展现有测试断言：

```csharp
// FramePresentationTests：003.889 收到 0x08
Assert.Equal(RfbProtocolFailureKind.UnexpectedServerMessage, exception.Failure?.Kind);
Assert.Equal(RfbProtocolReadStage.ServerMessageType, exception.Failure?.ReadStage);
Assert.Equal((byte)0x08, exception.Failure?.ServerMessageType);

// FramebufferUpdateTests：encoding = -777，rectangle index = 0
Assert.Equal(RfbProtocolFailureKind.UnsupportedEncoding, exception.Failure?.Kind);
Assert.Equal(RfbProtocolReadStage.FramebufferRectangleHeader, exception.Failure?.ReadStage);
Assert.Equal((byte)0, exception.Failure?.ServerMessageType);
Assert.Equal(-777, exception.Failure?.EncodingId);
Assert.Equal(0, exception.Failure?.RectangleIndex);

// 截断 ArdDisplayInfo2 payload
Assert.Equal(RfbProtocolFailureKind.TruncatedRead, exception.Failure?.Kind);
Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, exception.Failure?.ReadStage);
Assert.Equal((int)RfbEncodingType.ArdDisplayInfo2, exception.Failure?.EncodingId);
Assert.Equal(0, exception.Failure?.RectangleIndex);
```

剪贴板增加 padding 非零和非法 UTF-8 测试，断言 `MalformedClipboard`，阶段分别为 `ClipboardHeader` 和 `ClipboardPayload`，并使用 `"clipboard-secret"` 验证失败信息中无载荷字段。

- [ ] **步骤 2：运行三组测试确认因缺少上下文而失败**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FramebufferUpdateTests|FullyQualifiedName~ClipboardProtocolTests"
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Rfb_client_rejects_unknown_ard_message
```

预期：现有异常仍被抛出，但新增 `Failure` 字段断言得到 null 或缺少阶段。

- [ ] **步骤 3：在 FramebufferUpdateReader 标注头部和矩形 payload**

对更新头读取和每个矩形循环使用窄 try/catch。未知编码直接构造 `UnsupportedEncoding`；解码器异常补充当前上下文：

```csharp
var rectangleContext = new RfbProtocolFailureInfo(
    RfbProtocolFailureKind.DecoderFailure,
    RfbProtocolReadStage.FramebufferRectanglePayload,
    ServerMessageType: 0,
    EncodingId: encodingId,
    RectangleIndex: index);

try
{
    var decodeResult = await decoder.DecodeAsync(
        reader,
        framebuffer,
        rectangle,
        cancellationToken).ConfigureAwait(false);
    // 保留现有结果处理
}
catch (RfbProtocolException exception)
{
    throw exception.WithContext(rectangleContext);
}
```

矩形头读取失败使用 `MalformedFramebufferUpdate` + `FramebufferRectangleHeader` + 当前 index；更新 padding/rectangle count 读取失败使用 `FramebufferHeader`。不得捕获 `OperationCanceledException` 或普通 `IOException`。

- [ ] **步骤 4：在 ClipboardProtocol 和 RfbClient 标注边界**

`RfbClient.ReceiveAsync` default 分支改为：

```csharp
throw new RfbProtocolException(
    $"Unsupported RFB server message type {type}.",
    new RfbProtocolFailureInfo(
        RfbProtocolFailureKind.UnexpectedServerMessage,
        RfbProtocolReadStage.ServerMessageType,
        ServerMessageType: type));
```

Framebuffer 和 Clipboard 已知分支捕获 `RfbProtocolException` 时只补 `ServerMessageType`，保留内层更具体类别。`ClipboardProtocol` 对非零 padding、超限长度和非法 UTF-8 使用 `MalformedClipboard`；底层 EOF 的 `TruncatedRead` 保留，并补 `ClipboardHeader` 或 `ClipboardPayload`。

- [ ] **步骤 5：运行边界测试确认绿灯并检查取消语义**

运行步骤 2 的命令，再运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Rfb_client_"
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FramebufferUpdateTests|FullyQualifiedName~ClipboardProtocolTests|FullyQualifiedName~RfbReaderTests"
```

预期：全部通过；既有取消测试仍得到 `OperationCanceledException`，普通流 fault 不被转换为 `RfbProtocolException`。

- [ ] **步骤 6：提交任务 2**

```powershell
git add src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs src/WinARD.Remote.Protocol/Clipboard/ClipboardProtocol.cs src/WinARD.Desktop/Services/RfbClientFactory.cs tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs tests/WinARD.Remote.Protocol.Tests/Clipboard/ClipboardProtocolTests.cs tests/WinARD.Desktop.Tests/FramePresentationTests.cs
git commit -m "feat: fingerprint RFB protocol read failures"
```

## 任务 3：把失败指纹安全写入诊断并生成测试构建

**文件：**
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/DiagnosticExporterTests.cs`

- [ ] **步骤 1：编写诊断投影红灯测试**

在 `RemoteSessionViewModelTests` 使用一个启动后抛出以下异常的 runtime：

```csharp
var failure = new RfbProtocolException(
    "decoder leaked-secret-payload",
    new RfbProtocolFailureInfo(
        RfbProtocolFailureKind.TruncatedRead,
        RfbProtocolReadStage.FramebufferRectanglePayload,
        ServerMessageType: 0,
        EncodingId: 1105,
        RectangleIndex: 3));
```

等待 `Completion` 后断言 `REMOTE_SESSION_INTERRUPTED` 的字段精确为：

```csharp
AssertField(diagnostic, "ProtocolFailureKind", "TruncatedRead");
AssertField(diagnostic, "ProtocolReadStage", "FramebufferRectanglePayload");
AssertField(diagnostic, "ServerMessageType", "0x00");
AssertField(diagnostic, "EncodingId", "1105");
AssertField(diagnostic, "RectangleIndex", "3");
```

序列化 `safeDiagnosticSink.Snapshot()`，断言不包含 `decoder leaked-secret-payload`、测试画面字节、clipboard 或 host 标记。再增加普通 `IOException` 测试，断言没有任何 `Protocol*`、`EncodingId` 或 `RectangleIndex` 字段。

- [ ] **步骤 2：运行 ViewModel 测试确认红灯**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Protocol_failure_diagnostic|FullyQualifiedName~Io_failure_does_not_emit_protocol_fingerprint"
```

预期：协议异常事件存在，但 `Fields` 为空或缺少新字段。

- [ ] **步骤 3：实现固定诊断字段投影**

在 `RemoteSessionViewModel` 增加纯函数：

```csharp
private static IReadOnlyList<DiagnosticField>? GetProtocolFailureFields(Exception exception)
{
    if (exception is not RfbProtocolException { Failure: { } failure })
    {
        return null;
    }

    var fields = new List<DiagnosticField>
    {
        new("ProtocolFailureKind", failure.Kind.ToString()),
    };
    if (failure.ReadStage is { } stage)
    {
        fields.Add(new("ProtocolReadStage", stage.ToString()));
    }
    if (failure.ServerMessageType is { } type)
    {
        fields.Add(new("ServerMessageType", $"0x{type:X2}"));
    }
    if (failure.EncodingId is { } encodingId)
    {
        fields.Add(new("EncodingId", encodingId.ToString(CultureInfo.InvariantCulture)));
    }
    if (failure.RectangleIndex is { } rectangleIndex)
    {
        fields.Add(new("RectangleIndex", rectangleIndex.ToString(CultureInfo.InvariantCulture)));
    }

    return fields;
}
```

在 `MonitorLoopsAsync` 中，present failure 继续使用 `GetPresentationFailureFields`；receive failure 使用 `GetProtocolFailureFields`。所有字段使用默认 `DiagnosticFieldCategory.Public`，不读取 `exception.Message`。

- [ ] **步骤 4：验证诊断 JSON 脱敏与既有导出行为**

运行步骤 2 命令，再运行：

```powershell
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DiagnosticExporterTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RemoteSessionViewModelTests|FullyQualifiedName~ConnectionFailureDiagnosticsTests"
```

预期：新字段保留，原始异常消息仍未进入安全事件或导出的 JSON，现有呈现失败字段不回归。

- [ ] **步骤 5：运行格式、全量 Release/x64 构建与测试**

```powershell
dotnet format WinARD.sln --verify-no-changes --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore
git diff --check
```

预期：格式无差异；构建 0 warning、0 error；所有测试 0 failed；工作树只含本任务预期改动。

- [ ] **步骤 6：确认实机测试产物并提交**

确认存在：

```text
src/WinARD.Desktop/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/WinARD.Desktop.exe
```

然后提交：

```powershell
git add src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs tests/WinARD.Infrastructure.Tests/DiagnosticExporterTests.cs
git commit -m "feat: export safe RFB failure fingerprints"
```

## 任务 4：macOS 26.5 单次空闲复现

**文件：**
- 不修改源码；用户主动导出的诊断保存在 `artifacts/` 或 Release 产物目录。

- [ ] **步骤 1：启动新 Release 客户端并连接 Mac**

不运行 Pointer Smoke，不发送测试输入。连接后保持空闲，等待已知的数秒自动中断。

- [ ] **步骤 2：导出并检查诊断**

诊断必须同时包含：

```text
RFB_SESSION_NEGOTIATED: 003.889 / 0xC1 / MayControl=True / Shared
REMOTE_SESSION_INTERRUPTED: ProtocolFailureKind + 可用的 ReadStage/MessageType/EncodingId/RectangleIndex
```

如果失败指纹为未知顶层消息，下一轮只实现该消息的已知、安全长度规则；如果是未知编码或 payload 错误，则以记录的编码 ID 和读取阶段建立独立根因假设。不得根据单字节类型猜测未知载荷长度。
