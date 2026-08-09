# ARD MVS 协议证据实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 为 RDM 的 ARD 画质、分辨率和 Apple MVS 路径建立可复现、脱敏且不依赖猜测的客户端报文与真实 Mac 载荷前缀证据。

**架构：** 在现有 `WinARD.ProtocolProbe` 中增加两个隔离研究模式：回环模拟 ARD 服务端捕获 RDM 声明，以及使用现有 ARD 加密会话连接真实 Mac 并截取候选编码的有界载荷前缀。原始研究产物写入已忽略的 `artifacts/protocol-research`；正式仓库只保存测试、运行手册和由证据支持的协议结论。本计划是综合画质规格的第一阶段，CodecHost、MVS 正式解码和 UI 在证据门通过后另行计划。

**技术栈：** .NET 8、C# 12、RFB 3.8/Apple 3.889、ARD security type 30、现有 `ArdEncryptedStream`/`ArdSessionEncryption`、`System.Text.Json`、xUnit、PowerShell。

---

## 范围和证据门

本计划只交付：

1. RDM 的 Full、High、Medium、Low、Adaptive 及分辨率 Default、Low、High 初始化声明；
2. Full 与 Adaptive 的有序编码差分和唯一候选 ID；
3. 真实 Mac 显示合成测试图时的候选编码有界载荷前缀；
4. 许可证兼容实现或公开协议资料的审计结论；
5. “证据门打开”或“证据门关闭”的唯一结论。

不在本计划实现 MVS decoder、CodecHost、正式产品 MVS 协商、综合画质 UI、自适应控制器或第三方库分发。无法得到唯一候选 ID、可靠载荷资料或合规后端时，证据门必须关闭，不得猜测继续。

## 文件结构

**修改：**

- `src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs`：公开有界、保序的任意 `SetEncodings` 写入入口。
- `tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs`：验证 signed ID、顺序、空列表、上限和取消。
- `tools/WinARD.ProtocolProbe/ProbeCommandLine.cs`：增加 RDM 监听、捕获比较和差分前缀研究命令。
- `tools/WinARD.ProtocolProbe/Program.cs`：按模式读取环境变量；监听模式不读取远端凭据。
- `tools/WinARD.ProtocolProbe/ProbeOutput.cs`：输出安全研究结果。
- `tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`：覆盖命令和模式分派。
- `docs/protocol/ard-mvs-evidence.md`：保存正式证据和门结论。

**创建：**

- `tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureReport.cs`：版本化捕获模型。
- `tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureFile.cs`：确定性 JSON 读写。
- `tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureComparer.cs`：计算唯一 Adaptive-only ID。
- `tools/WinARD.ProtocolProbe/RdmCapture/ArdProbeServerHandshake.cs`：loopback ARD 3.889/security-30 握手。
- `tools/WinARD.ProtocolProbe/RdmCapture/RfbClientDeclarationReader.cs`：有界读取客户端初始化声明。
- `tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureServer.cs`：编排监听、ServerInit、捕获和输出。
- `tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCapture.cs`：候选矩形与前缀模型。
- `tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixReader.cs`：读取一个候选矩形和一次前缀。
- `tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCaptureFile.cs`：写 manifest、payload-prefix 和哈希。
- `tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCaptureRunner.cs`：建立加密 ARD 研究会话。
- `tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs`：回环服务端、隐私和比较器测试。
- `tests/WinARD.Remote.Protocol.Tests/Tools/EncodingPrefixCaptureTests.cs`：安全硬门、矩形、输出上限测试。
- `docs/testing/ard-mvs-protocol-capture.md`：七组 RDM 和真实 Mac 捕获手册。

## 基线

计划编写前已在 `master` 运行：

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-restore
```

结果：1545 个测试通过，0 失败。用户明确要求直接在主工作区执行，不创建 worktree。执行者不得 reset、stash 或覆盖用户改动；每个任务开始前运行 `git status --short`。

### 任务 1：公开安全的 SetEncodings 写入入口

**文件：**

- 修改：`src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs:15-39,241-264`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs`

- [ ] **步骤 1：编写 signed ID、顺序和空列表测试**

```csharp
[Fact]
public async Task Set_encodings_writer_preserves_signed_ids_and_order()
{
    await using var stream = new MemoryStream();
    await RfbSessionInitializer.WriteSetEncodingsAsync(
        stream,
        [16, -223, 1103, int.MinValue],
        CancellationToken.None);

    Assert.Equal(
        Convert.FromHexString("0200000400000010FFFFFF210000044F80000000"),
        stream.ToArray());
}

[Fact]
public async Task Set_encodings_writer_allows_empty_declaration()
{
    await using var stream = new MemoryStream();
    await RfbSessionInitializer.WriteSetEncodingsAsync(
        stream,
        Array.Empty<int>(),
        CancellationToken.None);

    Assert.Equal(new byte[] { 2, 0, 0, 0 }, stream.ToArray());
}
```

- [ ] **步骤 2：运行测试并确认缺少公开 API**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~RfbSessionInitializerTests.Set_encodings_writer"
```

预期：编译失败，`RfbSessionInitializer` 不包含公开的 `WriteSetEncodingsAsync`。

- [ ] **步骤 3：实现公开入口并让现有初始化路径复用**

```csharp
public static async ValueTask WriteSetEncodingsAsync(
    Stream stream,
    IReadOnlyList<int> encodings,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(encodings);
    cancellationToken.ThrowIfCancellationRequested();
    if (encodings.Count > ushort.MaxValue)
    {
        throw new ArgumentOutOfRangeException(
            nameof(encodings),
            "An RFB SetEncodings message cannot contain more than 65535 entries.");
    }

    var message = new byte[checked(4 + (encodings.Count * sizeof(int)))];
    message[0] = 2;
    BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), checked((ushort)encodings.Count));
    for (var index = 0; index < encodings.Count; index++)
    {
        BinaryPrimitives.WriteInt32BigEndian(
            message.AsSpan(4 + (index * sizeof(int))),
            encodings[index]);
    }

    await new RfbWriter(stream).WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
}
```

删除私有方法中的重复序列化循环，现有数组通过这个入口写入。

- [ ] **步骤 4：增加 null、65536 项和预取消测试**

分别断言 `ArgumentNullException`、`ArgumentOutOfRangeException` 和 `OperationCanceledException`，并验证异常发生前 stream 没有写入字节。

- [ ] **步骤 5：运行测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RfbSessionInitializerTests
git add src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs
git commit -m "feat: expose bounded RFB encoding declarations"
```

预期：相关测试全部通过。

### 任务 2：增加研究模式命令模型

**文件：**

- 修改：`tools/WinARD.ProtocolProbe/ProbeCommandLine.cs`
- 修改：`tools/WinARD.ProtocolProbe/Program.cs`
- 修改：`tools/WinARD.ProtocolProbe/ProbeOutput.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`

- [ ] **步骤 1：编写三种命令的失败测试**

命令固定为：

```text
--listen-rdm adaptive-default artifacts/protocol-research/rdm/adaptive-default.json
--compare-rdm-captures artifacts/protocol-research/rdm/full-default.json artifacts/protocol-research/rdm/adaptive-default.json
--capture-differential-prefix artifacts/protocol-research/rdm/full-default.json artifacts/protocol-research/rdm/adaptive-default.json artifacts/protocol-research/mac/adaptive-default --confirm-synthetic-screen
```

核心测试：

```csharp
[Fact]
public void Probe_command_line_parses_rdm_listener()
{
    Assert.True(ProbeCommandLine.TryParse(
        ["--listen-rdm", "adaptive-default", "capture.json"],
        out var request));
    Assert.Equal(ProbeMode.ListenRdm, request.Mode);
    Assert.Equal("adaptive-default", request.ProfileName);
    Assert.Equal("capture.json", request.OutputPath);
}

[Fact]
public void Probe_command_line_requires_synthetic_screen_confirmation()
{
    Assert.False(ProbeCommandLine.TryParse(
        ["--capture-differential-prefix", "full.json", "adaptive.json", "out"],
        out _));
}
```

profile 只接受 1–32 个小写字母、数字和连字符。空路径、附加参数和重复确认标志均拒绝。

- [ ] **步骤 2：运行测试并确认新枚举不存在**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Probe_command_line
```

预期：编译失败。

- [ ] **步骤 3：实现请求模型**

```csharp
public enum ProbeMode
{
    Authentication,
    CaptureFirstFrame,
    PointerSmoke,
    ListenRdm,
    CompareRdmCaptures,
    CaptureDifferentialEncodingPrefix,
}

public sealed record ProbeRequest(
    ProbeMode Mode,
    string? OutputPath = null,
    string? ProfileName = null,
    string? BaselineCapturePath = null,
    string? AdaptiveCapturePath = null,
    bool SyntheticScreenConfirmed = false)
{
    public string? CaptureFirstFramePath =>
        Mode == ProbeMode.CaptureFirstFrame ? OutputPath : null;
}
```

旧的无参数、`--pointer-smoke` 和 `--capture-first-frame` 行为保持兼容。每种模式用独立、精确的参数数量分支，不实现通用可选参数解析器。

- [ ] **步骤 4：Program 在读取凭据前分派监听和比较模式**

```csharp
if (request.Mode == ProbeMode.ListenRdm)
{
    return await RunRdmListenerAsync(request, cancellation.Token);
}

if (request.Mode == ProbeMode.CompareRdmCaptures)
{
    return await RunRdmComparisonAsync(request, cancellation.Token);
}
```

监听端口只读 `WINARD_LISTEN_PORT`，默认 5901；监听和比较模式不得读取 `WINARD_HOST`、`WINARD_USERNAME` 或密码。差分前缀模式继续使用现有隐藏密码流程。

- [ ] **步骤 5：更新 usage、运行测试并提交**

usage 必须示范 PowerShell 调用运算符：

```text
& '.\WinARD.ProtocolProbe.exe' --listen-rdm adaptive-default '.\artifacts\protocol-research\rdm\adaptive-default.json'
```

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Probe_command_line|FullyQualifiedName~Probe_result"
git add tools/WinARD.ProtocolProbe/ProbeCommandLine.cs tools/WinARD.ProtocolProbe/Program.cs tools/WinARD.ProtocolProbe/ProbeOutput.cs tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs
git commit -m "feat: add ARD protocol research commands"
```

### 任务 3：实现捕获模型和唯一差分选择

**文件：**

- 创建：`tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureReport.cs`
- 创建：`tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureFile.cs`
- 创建：`tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureComparer.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs`

- [ ] **步骤 1：编写 JSON 往返和唯一候选测试**

```csharp
var full = new RdmCaptureReport(
    1,
    "full-default",
    "3.8",
    0xC1,
    new CapturedPixelFormat(32, 24, false, true, 255, 255, 255, 16, 8, 0),
    [16, 0, -223],
    [new CapturedClientMessage(2, "SetEncodings", 16, null, null)],
    true,
    null);
var adaptive = full with
{
    Profile = "adaptive-default",
    Encodings = [12345, 16, 0, -223],
};

Assert.Equal(12345, RdmCaptureComparer.FindSingleAdaptiveOnlyEncoding(full, adaptive));
```

零个差分、两个差分、schema 不同均抛 `InvalidDataException`；异常只能包含 schema 和数字 ID。

- [ ] **步骤 2：运行测试并确认类型不存在**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RdmCaptureTests
```

- [ ] **步骤 3：实现只包含协议数值的模型**

```csharp
public sealed record RdmCaptureReport(
    int SchemaVersion,
    string Profile,
    string ClientVersion,
    byte ClientInit,
    CapturedPixelFormat? PixelFormat,
    IReadOnlyList<int> Encodings,
    IReadOnlyList<CapturedClientMessage> Messages,
    bool ReachedFramebufferRequest,
    byte? StoppedAtUnknownMessageType);

public sealed record CapturedPixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColor,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift);

public sealed record CapturedClientMessage(
    byte Type,
    string Name,
    int WireLength,
    string? NumericPayloadHex,
    string? PayloadSha256);
```

`NumericPayloadHex` 只允许 SetMode、SetDisplay 和 SetEncryption；ViewerInfo 只记录长度和 SHA-256。

- [ ] **步骤 4：实现确定性 JSON 文件**

`WriteAsync` 使用同目录临时文件、Indented camelCase JSON、flush 和 `File.Move(temp, final, true)`；`finally` 删除临时文件。`ReadAsync` 验证 schema 为 1、profile 格式、列表不超过 4096、numeric payload 是偶数长度大写十六进制。

- [ ] **步骤 5：实现差分比较器**

```csharp
public static int FindSingleAdaptiveOnlyEncoding(
    RdmCaptureReport baseline,
    RdmCaptureReport adaptive)
{
    ValidateComparable(baseline, adaptive);
    var baselineIds = baseline.Encodings.ToHashSet();
    var candidates = adaptive.Encodings
        .Where(id => !baselineIds.Contains(id))
        .Distinct()
        .ToArray();
    if (candidates.Length != 1)
    {
        throw new InvalidDataException(
            $"Expected exactly one Adaptive-only encoding ID, observed {candidates.Length}: " +
            string.Join(", ", candidates));
    }

    return candidates[0];
}
```

比较器只能称为候选编码，不能直接命名为 MVS。

- [ ] **步骤 6：运行测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RdmCaptureTests
git add tools/WinARD.ProtocolProbe/RdmCapture tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs
git commit -m "feat: model safe RDM protocol captures"
```

### 任务 4：实现回环 ARD 服务端握手

**文件：**

- 创建：`tools/WinARD.ProtocolProbe/RdmCapture/ArdProbeServerHandshake.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs`

- [ ] **步骤 1：编写真实客户端实现对模拟服务端的握手测试**

测试启动 `TcpListener(IPAddress.Loopback, 0)`，服务端调用 `ArdProbeServerHandshake.AcceptAsync`，客户端使用现有 `RfbHandshake.NegotiateAsync` 和 `ArdAuthenticator.AuthenticateAsync`。使用合成凭据 `rdm-probe`/`synthetic-only`，断言：

```csharp
Assert.Equal(RfbVersion.V3_889, serverResult.Version);
Assert.Equal(0xC1, serverResult.ClientInit);
Assert.Equal(192, serverResult.DiscardedAuthenticationResponseBytes);
```

服务端结果不得具有 credential、host 或 raw authentication response 属性。

- [ ] **步骤 2：运行测试并确认类型不存在**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Ard_probe_server
```

- [ ] **步骤 3：实现固定、可审计的 security-30 挑战**

使用现有独立已知答案夹具相同的 64 字节 modulus、generator 5 和服务端私钥指数 3；运行时计算服务端 public key。服务端顺序固定为：

```text
写 RFB 003.889\n
读 RFB 003.008\n 或 RFB 003.889\n
写 [1, 30]
读 [30]
写 generator、keyLength、modulus、serverPublic
读 128 + keyLength 字节响应
清零响应
写 SecurityResult 0
读一个 ClientInit 字节
```

认证响应缓冲在 `finally` 使用 `CryptographicOperations.ZeroMemory`。模拟服务端不解密、不验证、不记录凭据，只观察认证完成后的声明。

- [ ] **步骤 4：增加错误客户端测试**

分别验证不支持的 client banner、security type 不是 30、认证响应截断、ClientInit 不是 `0x01`/`0xC1` 和取消。异常使用固定文本，不包含收到的认证字节。

- [ ] **步骤 5：运行测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RdmCaptureTests
git add tools/WinARD.ProtocolProbe/RdmCapture/ArdProbeServerHandshake.cs tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs
git commit -m "feat: add loopback ARD research handshake"
```

### 任务 5：捕获 RDM 初始化声明

**文件：**

- 创建：`tools/WinARD.ProtocolProbe/RdmCapture/RfbClientDeclarationReader.cs`
- 创建：`tools/WinARD.ProtocolProbe/RdmCapture/RdmCaptureServer.cs`
- 修改：`tools/WinARD.ProtocolProbe/Program.cs`
- 修改：`tools/WinARD.ProtocolProbe/ProbeOutput.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`

- [ ] **步骤 1：编写声明解析测试**

构造 ViewerInfo 66 字节、SetMode 4 字节、SetDisplay 8 字节、SetPixelFormat 20 字节、SetEncodings 和 FramebufferUpdateRequest 10 字节。断言：

```csharp
Assert.Equal(PixelFormat.WinArdBgra32, reportPixelFormat);
Assert.Equal(new[] { 12345, 16, 0, -223 }, report.Encodings);
Assert.True(report.ReachedFramebufferRequest);
Assert.Null(report.StoppedAtUnknownMessageType);
Assert.DoesNotContain("private-workstation", serializedReport, StringComparison.Ordinal);
```

ViewerInfo 中放入 `private-workstation`，输出只能保存长度和 SHA-256。

- [ ] **步骤 2：运行测试并确认 reader 不存在**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~Rfb_client_declaration
```

- [ ] **步骤 3：实现有界消息 reader**

| 类型 | 名称 | 长度规则 | 记录内容 |
|---|---|---|---|
| `0x00` | SetPixelFormat | 20 | 解析字段 |
| `0x02` | SetEncodings | `4 + count*4`，count ≤ 4096 | signed ID |
| `0x03` | FramebufferUpdateRequest | 10 | 成功停止 |
| `0x09` | AutoFramebufferUpdate | 16 | numeric payload |
| `0x0A` | SetMode | 4 | numeric payload |
| `0x0D` | SetDisplay | 8 | numeric payload |
| `0x12` | SetEncryption | opcode 1 为 12 字节，opcode 2 为 8 字节 | numeric payload |
| `0x21` | ViewerInfo | 66 | 长度和 SHA-256 |

KeyEvent、PointerEvent、ClientCutText 和其他未知类型立即停止并记录类型，不消费或保存 payload。总读取上限 64 KiB，总消息数上限 64。

- [ ] **步骤 4：实现并测试 ServerInit 和 listener**

`RdmCaptureServer` 只绑定 `IPAddress.Loopback`，只接受一个连接。握手后发送固定合成 ServerInit：1920×1080、`PixelFormat.WinArdBgra32`、`MayControl`、显示名 `WinARD Synthetic Probe`，不设置 session-selection。

端到端测试验证 schema 1 JSON、首个 FramebufferUpdateRequest 后退出、10 秒无进展超时、原子覆盖和无临时文件。

- [ ] **步骤 5：连接 Program 分派和安全输出**

使用以下签名，避免监听、文件写入和 CLI 互相持有隐式状态：

```csharp
public Task<RdmCaptureReport> CaptureOnceAsync(
    int port,
    string profile,
    CancellationToken cancellationToken);

private static async Task<int> RunRdmListenerAsync(
    ProbeRequest request,
    CancellationToken cancellationToken);

private static async Task<int> RunRdmComparisonAsync(
    ProbeRequest request,
    CancellationToken cancellationToken);
```

`CaptureOnceAsync` 只返回模型；`RunRdmListenerAsync` 使用 `RdmCaptureFile.WriteAsync` 保存到 `request.OutputPath`。`RunRdmComparisonAsync` 使用 `RdmCaptureFile.ReadAsync` 加载两个文件并调用 `RdmCaptureComparer`。

控制台只能输出：

```text
RDM capture listening on 127.0.0.1:5901 for profile adaptive-default.
RDM capture saved: artifacts\protocol-research\rdm\adaptive-default.json
```

不得输出用户名、密码、ViewerInfo 原始字节或本机名。`WINARD_LISTEN_PORT` 非数字或超范围时返回 exit code 2。

- [ ] **步骤 6：运行测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~RdmCaptureTests|FullyQualifiedName~ProtocolProbeTests"
git add tools/WinARD.ProtocolProbe/RdmCapture tools/WinARD.ProtocolProbe/Program.cs tools/WinARD.ProtocolProbe/ProbeOutput.cs tests/WinARD.Remote.Protocol.Tests/Tools/RdmCaptureTests.cs tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs
git commit -m "feat: capture RDM ARD declarations"
```

### 任务 6：实现真实 Mac 候选编码前缀捕获

**文件：**

- 创建：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCapture.cs`
- 创建：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixReader.cs`
- 创建：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCaptureFile.cs`
- 创建：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCaptureRunner.cs`
- 修改：`tools/WinARD.ProtocolProbe/Program.cs`
- 修改：`tools/WinARD.ProtocolProbe/ProbeOutput.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Tools/EncodingPrefixCaptureTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`

- [ ] **步骤 1：编写候选选择与合成画面硬门测试**

```csharp
await Assert.ThrowsAsync<InvalidOperationException>(() =>
    runner.RunAsync(
        host,
        port,
        username,
        password,
        baselinePath,
        adaptivePath,
        outputDirectory,
        syntheticScreenConfirmed: false,
        CancellationToken.None));
```

固定异常为 `Encoding payload capture requires an explicit synthetic-screen confirmation.`。另测零个或多个差分 ID 时在 DNS/TCP 前失败。

- [ ] **步骤 2：编写矩形和一次前缀读取测试**

输入为一个 FramebufferUpdate、一个矩形、candidate `12345` 和 32 字节 payload：

```csharp
var capture = await EncodingPrefixReader.ReadAsync(
    stream,
    12345,
    64 * 1024,
    CancellationToken.None);

Assert.Equal(12345, capture.EncodingId);
Assert.Equal(new CapturedRectangle(0, 0, 1920, 1080), capture.Rectangle);
Assert.Equal(payload, capture.PayloadPrefix);
```

再测空 update 最多 8 次、返回 Raw 标记候选被拒绝、rectangle count 超过 4096 和前缀上限。

- [ ] **步骤 3：运行测试并确认类型不存在**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~EncodingPrefixCaptureTests
```

- [ ] **步骤 4：实现结果模型和文件写入**

```csharp
public sealed record EncodingPrefixCapture(
    int SchemaVersion,
    int EncodingId,
    CapturedRectangle Rectangle,
    int PrefixLength,
    string PayloadSha256,
    byte[] PayloadPrefix);

public readonly record struct CapturedRectangle(
    ushort X,
    ushort Y,
    ushort Width,
    ushort Height);
```

输出目录只含 `manifest.json` 和 `payload-prefix.bin`。manifest 不含 host、port、username、路径或 payload，只含 schema、signed ID、rectangle、length、SHA-256 和 `syntheticScreenConfirmed: true`。已有文件时拒绝覆盖。

- [ ] **步骤 5：实现加密 ARD 会话编排**

runner 的公开入口固定为：

```csharp
public Task<EncodingPrefixCapture> RunAsync(
    string host,
    int port,
    ISecretMaterial username,
    ISecretMaterial password,
    string baselineCapturePath,
    string adaptiveCapturePath,
    string outputDirectory,
    bool syntheticScreenConfirmed,
    CancellationToken cancellationToken);
```

严格复用桌面客户端的已验证顺序：

```csharp
var handshake = await RfbHandshake.NegotiateAsync(networkStream, token);
var authentication = await new ArdAuthenticator().AuthenticateAsync(
    networkStream,
    handshake.Version,
    username,
    password,
    token);
await using var transport = new ArdEncryptedStream(networkStream, ProtocolLimits.Default);
await using var encryption = new ArdSessionEncryption(transport, authentication);
var server = await RfbSessionInitializer.InitializeAsync(
    transport,
    handshake,
    ProtocolLimits.Default,
    encryption,
    token);
```

创建带 `encryption.CreateDecoder()` 的临时 `FramebufferUpdateSession`，请求并消费最多 8 个初始更新，每次调用 `CompleteFramebufferUpdateAsync`，直到 `transport.IsEncrypted`。激活后发送：

```csharp
await RfbSessionInitializer.WriteSetEncodingsAsync(
    transport,
    [candidateEncodingId, (int)RfbEncodingType.Raw],
    token);
```

再发送一次 non-incremental 全屏请求，将 stream 交给 `EncodingPrefixReader`。不把候选 ID 加入正式 `RfbEncodingType`。

- [ ] **步骤 6：只读取一次有界前缀并立即关闭**

```csharp
var prefix = new byte[64 * 1024];
var count = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
if (count == 0)
{
    throw new EndOfStreamException(
        "The candidate encoding rectangle contained no observable payload prefix.");
}

Array.Resize(ref prefix, count);
```

这不是完整载荷解析器，不声称找到了矩形边界。输出名必须是 `payload-prefix`，不能使用 `frame` 或 `fixture`。读取后关闭连接，避免错位解析。

- [ ] **步骤 7：增加超时、隐私和冲突测试**

验证 30 秒总超时仍映射为 `ProbeTimeoutException`；manifest 和控制台不出现 host/username/password；控制台不打印 hex；输出冲突在连接前失败。

- [ ] **步骤 8：运行测试并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~EncodingPrefixCaptureTests|FullyQualifiedName~ProtocolProbeTests"
git add tools/WinARD.ProtocolProbe/EncodingResearch tools/WinARD.ProtocolProbe/Program.cs tools/WinARD.ProtocolProbe/ProbeOutput.cs tests/WinARD.Remote.Protocol.Tests/Tools/EncodingPrefixCaptureTests.cs tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs
git commit -m "feat: capture bounded ARD encoding prefixes"
```

### 任务 7：编写捕获运行手册

**文件：**

- 创建：`docs/testing/ard-mvs-protocol-capture.md`

- [ ] **步骤 1：写明构建和 PowerShell 调用方式**

```powershell
dotnet publish tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj -c Release -p:Platform=x64 --self-contained false
$probe = Resolve-Path 'tools\WinARD.ProtocolProbe\bin\x64\Release\net8.0-windows10.0.19041.0\WinARD.ProtocolProbe.exe'
```

所有 exe 调用使用 `& $probe`，不能把带引号路径直接当 PowerShell 表达式。

- [ ] **步骤 2：写明七组 RDM 捕获矩阵**

每组先运行 listener，再让 RDM 临时 Apple Remote Desktop entry 连接 `127.0.0.1:$env:WINARD_LISTEN_PORT`。用户名和密码使用合成值，服务端不解密。

```powershell
$env:WINARD_LISTEN_PORT = '5901'
& $probe --listen-rdm full-default 'artifacts\protocol-research\rdm\full-default.json'
& $probe --listen-rdm high-default 'artifacts\protocol-research\rdm\high-default.json'
& $probe --listen-rdm medium-default 'artifacts\protocol-research\rdm\medium-default.json'
& $probe --listen-rdm low-default 'artifacts\protocol-research\rdm\low-default.json'
& $probe --listen-rdm adaptive-default 'artifacts\protocol-research\rdm\adaptive-default.json'
& $probe --listen-rdm adaptive-low 'artifacts\protocol-research\rdm\adaptive-low.json'
& $probe --listen-rdm adaptive-high 'artifacts\protocol-research\rdm\adaptive-high.json'
```

每次只改变名称对应的一项设置。关闭剪贴板同步，不按键、不移动鼠标；首个 update request 后 mock server 关闭是预期行为。

- [ ] **步骤 3：写明比较和真实 Mac 安全步骤**

```powershell
& $probe --compare-rdm-captures `
  'artifacts\protocol-research\rdm\full-default.json' `
  'artifacts\protocol-research\rdm\adaptive-default.json'
```

真实 Mac 必须只保留一个显示器、显示无敏感测试图、关闭通知预览和私人文件名窗口。用户目视确认后才运行：

```powershell
& $probe --capture-differential-prefix `
  'artifacts\protocol-research\rdm\full-default.json' `
  'artifacts\protocol-research\rdm\adaptive-default.json' `
  'artifacts\protocol-research\mac\adaptive-default' `
  --confirm-synthetic-screen
```

- [ ] **步骤 4：写明清理和禁止提交规则**

明确 `artifacts/protocol-research` 已被 git 忽略，raw JSON 和 `.bin` 不得强制 add。证据提取后由用户决定是否删除 payload-prefix；普通 WinARD diagnostics 不包含它。

- [ ] **步骤 5：静态检查并提交**

```powershell
rg -n "& \$probe|--listen-rdm|--compare-rdm-captures|--capture-differential-prefix|confirm-synthetic-screen" docs/testing/ard-mvs-protocol-capture.md
git add docs/testing/ard-mvs-protocol-capture.md
git commit -m "docs: add ARD MVS capture runbook"
```

### 任务 8：执行捕获矩阵并记录证据

**文件：**

- 生成但不提交：`artifacts/protocol-research/rdm/*.json`
- 生成但不提交：`artifacts/protocol-research/mac/adaptive-default/*`
- 创建：`docs/protocol/ard-mvs-evidence.md`

- [ ] **步骤 1：确认代码与工作区状态**

```powershell
git status --short
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~RdmCaptureTests|FullyQualifiedName~EncodingPrefixCaptureTests|FullyQualifiedName~ProtocolProbeTests"
```

若有用户改动，保留并绕开。

- [ ] **步骤 2：按手册完成七组 RDM 捕获**

每完成一组读取 JSON，验证 `schemaVersion == 1`、profile 与文件名一致、`reachedFramebufferRequest == true`。出现未知消息时停止矩阵，先为该消息补有界 parser 和测试；不能用 TCP chunk 边界猜消息长度。

- [ ] **步骤 3：比较 Full 与 Adaptive**

运行比较命令。只有恰好一个 Adaptive-only signed ID 才进入真实 Mac 前缀步骤。零个或多个候选时跳过真实 Mac 捕获，并在证据文档记录门关闭和完整数字差分。

- [ ] **步骤 4：在合成画面硬门下捕获真实 Mac 前缀**

用户目视确认目标 Mac 只显示无敏感测试图后执行手册命令，并验证：

```powershell
Get-FileHash 'artifacts\protocol-research\mac\adaptive-default\payload-prefix.bin' -Algorithm SHA256
Get-Content 'artifacts\protocol-research\mac\adaptive-default\manifest.json' -Raw
```

manifest 哈希必须一致，且不含 host、username、password 或绝对路径。

- [ ] **步骤 5：调查许可证兼容实现和公开资料**

至少核对：Devolutions 官方 RDM ARD 文档、本机 `DevolutionsVnc.dll` 只读符号、候选开源仓库的精确 commit 和许可证、MVS 载荷长度/块布局/DCT-YCC/质量控制资料，以及许可证是否允许 WinARD 的分发方式。不能仅凭项目名、DLL 字符串或博客断言兼容。

- [ ] **步骤 6：编写无占位符证据文档**

`docs/protocol/ard-mvs-evidence.md` 必须包含：

1. 七组 profile 的 PixelFormat 和有序 signed ID；
2. Full 与 Adaptive 精确差分；
3. Adaptive Default/Low/High 精确差分；
4. payload-prefix 矩形、长度和 SHA-256，不嵌入 payload；
5. 公开资料与许可证表；
6. 已证明事实和未证明事实；
7. 单一“证据门打开”或“证据门关闭”结论。

打开门必须同时满足：唯一候选 ID、真实 Mac 返回该 ID、合规资料解释载荷边界、许可证允许采用。任一项不满足就关闭。

- [ ] **步骤 7：扫描敏感内容并提交**

```powershell
rg -n -i "password|username|private-host|payload-prefix\.bin|[A-Z]:\\" docs/protocol/ard-mvs-evidence.md
git status --short --ignored
git add docs/protocol/ard-mvs-evidence.md
git commit -m "docs: record ARD MVS interoperability evidence"
```

敏感扫描只能命中解释规则，不得命中真实值；`artifacts/protocol-research` 必须为 ignored，不能 staged。

### 任务 9：全量验证和第一阶段交接

**文件：**

- 修改：`docs/protocol/ard-mvs-evidence.md`（仅修复验证发现的问题）
- 修改：`docs/testing/ard-mvs-protocol-capture.md`（仅修复已执行命令与手册不一致）

- [ ] **步骤 1：运行格式和占位符检查**

```powershell
git diff --check
$forbidden = @(
  ('TO' + 'DO'),
  ('T' + 'BD'),
  ('待' + '定'),
  ('PLACE' + 'HOLDER'),
  '填入',
  ('<encoding' + '-id>'))
Select-String -Path `
  'docs/protocol/ard-mvs-evidence.md', `
  'docs/testing/ard-mvs-protocol-capture.md' `
  -Pattern $forbidden
```

预期：无输出。

- [ ] **步骤 2：运行协议工具测试**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64
```

预期：0 失败。

- [ ] **步骤 3：运行全量测试**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-restore
```

预期：0 失败，通过数不少于基线 1545。

- [ ] **步骤 4：验证研究产物未进入 Git**

```powershell
git ls-files artifacts/protocol-research
git status --short --branch
```

预期：第一条无输出；最终工作区干净。

- [ ] **步骤 5：根据证据门只创建下一阶段计划**

门打开时下一份计划命名为：

```text
docs/superpowers/plans/2026-08-09-ard-mvs-codec-host-and-negotiation.md
```

门关闭时下一份计划只能覆盖已证明的 ZRLE、色深和服务端缩放能力，不能包含 MVS decoder。本任务不实现下一阶段功能。

## 完成标准

- RDM 七组捕获可在 loopback 稳定复现；
- 捕获 JSON 不含认证响应、凭据、主机名或 ViewerInfo 原文；
- Full/Adaptive 差分由工具计算，不人工猜测；
- 真实 Mac 前缀只在合成画面显式确认后产生；
- payload-prefix 有界、哈希可验、不进入 Git；
- 证据文档区分已知与未知并给出唯一门结论；
- 全量测试通过，研究工具不改变正式 WinARD 编码声明。
