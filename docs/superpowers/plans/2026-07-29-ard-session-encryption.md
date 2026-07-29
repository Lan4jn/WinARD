# ARD 完整会话加密实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 为 WinARD 实现 macOS 26.5 ARD 3.889 的完整会话加密协商与传输，使鼠标、键盘和所有后续 RFB 消息进入同一条受完整性保护的加密通道。

**架构：** Type 30 认证返回一个可销毁的认证密钥所有者；`ArdSessionEncryption` 负责 1103 会话材料、SetEncryption 状态机和激活边界；`ArdEncryptedStream` 在激活前透明转发明文，激活后统一封装双向 AES-128-CBC 数据包。现有 RFB 读写器继续面向同一个 Stream，从而无需在各消息类型中重复实现加密。

**技术栈：** .NET 8、C#、`System.Security.Cryptography`（MD5/AES/SHA-1，均为 ARD 协议兼容要求）、xUnit、WinUI 3 桌面客户端。

---

## 文件结构

- 创建 `src/WinARD.Remote.Protocol/Authentication/ArdAuthenticationResult.cs`：拥有并按生命周期清零 Type 30 认证密钥。
- 修改 `src/WinARD.Remote.Protocol/Authentication/ArdAuthenticator.cs`：认证成功时转移认证密钥所有权。
- 创建 `src/WinARD.Remote.Protocol/Ard/ArdSessionCipherMaterial.cs`：拥有并清零 session key 与初始 IV。
- 创建 `src/WinARD.Remote.Protocol/Ard/ArdEncryptedPacketCodec.cs`：构造、解析和验证 ARD 加密数据包。
- 创建 `src/WinARD.Remote.Protocol/Ard/ArdEncryptedStream.cs`：维护双向序号、链式 IV、分片读取和串行加密写入。
- 创建 `src/WinARD.Remote.Protocol/Ard/ArdSessionEncryption.cs`：管理 Plaintext/Requested/PendingActivation/Encrypted 状态及切换边界。
- 创建 `src/WinARD.Remote.Protocol/Encodings/ArdSessionEncryptionEncoding.cs`：解析 pseudo-encoding 1103 并提交待激活会话材料。
- 修改 `src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs`：声明 SetEncryption 消息类型。
- 修改 `src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs`：写入 request/ack 两种 SetEncryption 消息。
- 修改 `src/WinARD.Remote.Protocol/Encodings/RfbEncodingType.cs`：声明 `ArdSessionEncryption = 1103`。
- 修改 `src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`：允许持久会话注入额外 decoder。
- 修改 `src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs`：声明 1103 并在首帧请求前发出 encryption request。
- 修改 `src/WinARD.Desktop/Services/RfbClientFactory.cs`：统一使用切换流，持有加密控制器，并在完整 FBU 后完成激活。
- 修改 `src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`：增加加密协商和传输失败分类。
- 创建对应测试文件，并扩展现有认证、初始化、帧解析及桌面客户端集成测试。

### 任务 1：保留并安全销毁 Type 30 认证密钥

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Authentication/ArdAuthenticationResult.cs`
- 修改：`src/WinARD.Remote.Protocol/Authentication/ArdAuthenticator.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Authentication/ArdAuthenticatorTests.cs`

- [ ] **步骤 1：编写失败测试，证明成功认证返回密钥所有者且销毁后清零**

在 `ArdAuthenticatorTests.cs` 增加使用固定 DH 随机源的测试，并通过内部测试钩子检查缓冲区：

```csharp
[Fact]
public void Authentication_result_uses_owned_key_and_dispose_clears_it()
{
    var owned = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
    var plaintext = new byte[16];
    using var expectedAes = Aes.Create();
    expectedAes.Key = owned.ToArray();
    var ciphertext = expectedAes.EncryptEcb(plaintext, PaddingMode.None);
    var decrypted = new byte[16];
    var result = new ArdAuthenticationResult(owned);

    result.DecryptEcb(ciphertext, decrypted);
    result.Dispose();

    Assert.Equal(plaintext, decrypted);
    Assert.All(owned, value => Assert.Equal(0, value));
    Assert.Throws<ObjectDisposedException>(() => result.DecryptEcb(ciphertext, decrypted));
}
```

- [ ] **步骤 2：运行认证测试，确认因返回类型不存在而失败**

运行：

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --filter FullyQualifiedName~ArdAuthenticatorTests
```

预期：FAIL，编译错误指出 `AuthenticateAsync` 没有返回 `ArdAuthenticationResult` 或测试钩子不存在。

- [ ] **步骤 3：实现最小认证结果所有者并转移密钥**

`ArdAuthenticationResult.cs` 的公开表面固定为：

```csharp
public sealed class ArdAuthenticationResult : IDisposable
{
    private byte[]? _key;

    internal ArdAuthenticationResult(byte[] key) { /* validate 16 bytes; take ownership */ }

    internal void DecryptEcb(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext) { /* AES-ECB, no padding */ }

    public void Dispose() { /* Interlocked.Exchange; CryptographicOperations.ZeroMemory */ }
}
```

将两个 `AuthenticateAsync` 重载的返回类型从 `Task` 改为 `Task<ArdAuthenticationResult>`。`CreateResponseMessage` 同时返回 wire response 与拥有的 16 字节 key；认证失败时清零 key，认证成功时只把 key 转入结果对象。不得从结果对象公开原始 key。

- [ ] **步骤 4：运行认证测试并确认通过**

运行同上。预期：PASS。

- [ ] **步骤 5：提交认证密钥生命周期变更**

```powershell
git add src/WinARD.Remote.Protocol/Authentication tests/WinARD.Remote.Protocol.Tests/Authentication/ArdAuthenticatorTests.cs
git commit -m "feat: retain ARD authentication key"
```

### 任务 2：实现可独立验证的 ARD 加密包 codec

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdSessionCipherMaterial.cs`
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdEncryptedPacketCodec.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedPacketCodecTests.cs`

- [ ] **步骤 1：编写固定向量和拒绝路径测试**

测试必须覆盖相同输入可复现的 wire bytes、摘要破坏、非法长度和材料销毁：

```csharp
[Fact]
public void Encrypt_then_decrypt_round_trips_and_advances_iv()
{
    using var material = new ArdSessionCipherMaterial(Key, InitialIv);
    var codec = new ArdEncryptedPacketCodec();

    var packet = codec.Encrypt(Key, InitialIv, sequence: 0, new byte[] { 5, 0, 0x12, 0x34, 0x56, 0x78 });
    var decoded = codec.Decrypt(Key, InitialIv, sequence: 0, packet.AsSpan(2));

    Assert.Equal(new byte[] { 5, 0, 0x12, 0x34, 0x56, 0x78 }, decoded.Payload);
    Assert.Equal(packet[^16..], decoded.NextIv);
}

[Fact]
public void Decrypt_rejects_sha1_mismatch()
{
    var packet = CreateValidPacket();
    packet[^1] ^= 0x01;

    var exception = Assert.Throws<RfbProtocolException>(() =>
        new ArdEncryptedPacketCodec().Decrypt(Key, InitialIv, 0, packet.AsSpan(2)));

    Assert.Equal(RfbProtocolFailureKind.ArdEncryptionIntegrity, exception.Failure?.Kind);
}
```

- [ ] **步骤 2：运行 codec 测试并确认失败**

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --filter FullyQualifiedName~ArdEncryptedPacketCodecTests
```

预期：FAIL，缺少 codec、cipher material 和错误分类。

- [ ] **步骤 3：实现 packet codec 和材料所有者**

固定接口：

```csharp
internal sealed record ArdDecryptedPacket(byte[] Payload, byte[] NextIv);

internal sealed class ArdEncryptedPacketCodec
{
    public byte[] Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        uint sequence,
        ReadOnlySpan<byte> payload);

    public ArdDecryptedPacket Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> iv,
        uint sequence,
        ReadOnlySpan<byte> ciphertext);
}
```

实现约束：payload 长度不超过 `ushort.MaxValue`；总密文长度必须能写入 `ushort`；密文必须非零且 16 字节对齐；使用 `CryptographicOperations.FixedTimeEquals` 比较 SHA-1；所有临时明文、hash input 和 key copy 在 `finally` 中清零。对协议规定的 AES-CBC、AES-ECB、MD5 和 SHA-1 添加精确的 CA 抑制注释，不做全局 suppress。

- [ ] **步骤 4：运行 codec 测试并确认通过**

运行同上。预期：PASS。

- [ ] **步骤 5：提交 codec**

```powershell
git add src/WinARD.Remote.Protocol/Ard/ArdSessionCipherMaterial.cs src/WinARD.Remote.Protocol/Ard/ArdEncryptedPacketCodec.cs src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedPacketCodecTests.cs
git commit -m "feat: encode ARD encrypted packets"
```

### 任务 3：实现可原子切换的加密 Stream

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdEncryptedStream.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedStreamTests.cs`

- [ ] **步骤 1：编写明文转发、切换、分片读取和并发写测试**

```csharp
[Fact]
public async Task Activate_makes_all_later_writes_encrypted_and_serialized()
{
    await using var inner = new RecordingDuplexStream();
    await using var stream = new ArdEncryptedStream(inner, ProtocolLimits.Default);
    using var material = new ArdSessionCipherMaterial(Key, InitialIv);

    await stream.WriteAsync(new byte[] { 0x12, 0, 0, 2, 0, 1, 0, 0 });
    stream.Activate(material);
    await Task.WhenAll(
        stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 2 }).AsTask(),
        stream.WriteAsync(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61 }).AsTask());

    Assert.StartsWith(new byte[] { 0x12, 0, 0, 2 }, inner.WrittenBytes);
    Assert.DoesNotContain(new byte[] { 5, 0, 0, 1, 0, 2 }, inner.WrittenBytes);
}

[Fact]
public async Task ReadAsync_reassembles_fragmented_encrypted_packets()
{
    var wire = CreateTwoEncryptedPackets();
    await using var inner = new FragmentingDuplexStream(wire, maxRead: 3);
    await using var stream = CreateActivatedStream(inner);
    var output = new byte[ExpectedPayload.Length];

    await ReadExactlyAsync(stream, output);

    Assert.Equal(ExpectedPayload, output);
}
```

- [ ] **步骤 2：运行 stream 测试并确认失败**

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --filter FullyQualifiedName~ArdEncryptedStreamTests
```

预期：FAIL，缺少 `ArdEncryptedStream`。

- [ ] **步骤 3：实现 Stream 状态、读缓存和写串行化**

固定公开表面：

```csharp
public sealed class ArdEncryptedStream : Stream
{
    public ArdEncryptedStream(Stream inner, ProtocolLimits limits);
    public bool IsEncrypted { get; }
    internal void Activate(ArdSessionCipherMaterial material);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
}
```

激活前直接转发；激活后读取 `u16be ciphertextLength` 并累积完整密文，codec 验证后把 payload 放入内部明文队列。写锁覆盖 sequence、IV、codec、inner write 和状态更新。单个加密包的最大 RFB payload 为 65498 字节；更大的 `WriteAsync` 必须按顺序拆成多个包。只有 inner write 成功后才提交对应包的 send sequence/IV；任何读写或 integrity 失败永久 fault stream。`Activate` 取得读写状态锁，只允许调用一次，并接管 cipher material 所有权。Dispose 清零内部材料和缓冲区，但保持传入的底层 stream 打开。

- [ ] **步骤 4：运行 stream 测试并确认通过**

运行同上。预期：PASS，包括并发循环至少 100 次不复用序号。

- [ ] **步骤 5：提交加密 Stream**

```powershell
git add src/WinARD.Remote.Protocol/Ard/ArdEncryptedStream.cs tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedStreamTests.cs
git commit -m "feat: add switchable ARD encrypted stream"
```

### 任务 4：实现 SetEncryption 与 1103 协商状态机

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Ard/ArdSessionEncryption.cs`
- 创建：`src/WinARD.Remote.Protocol/Encodings/ArdSessionEncryptionEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs`
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/RfbEncodingType.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdClientMessageWriterTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdSessionEncryptionTests.cs`

- [ ] **步骤 1：测试 request/ack 的精确 wire bytes**

```csharp
[Fact]
public async Task Writes_set_encryption_request_and_acknowledgement()
{
    var stream = new RecordingStream();
    var writer = new ArdClientMessageWriter(new RfbWriter(stream));

    await writer.WriteSetEncryptionRequestAsync(CancellationToken.None);
    await writer.WriteSetEncryptionAcknowledgementAsync(CancellationToken.None);

    Assert.Equal(
        new byte[]
        {
            0x12, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1,
            0x12, 0, 0, 2, 0, 1, 0, 0,
        },
        stream.ToArray());
}
```

- [ ] **步骤 2：测试 1103 解包和帧结束后才激活**

```csharp
[Fact]
public async Task Session_encryption_rectangle_becomes_pending_until_frame_completion()
{
    using var auth = CreateAuthenticationResult();
    await using var transport = new ArdEncryptedStream(new RecordingDuplexStream(), ProtocolLimits.Default);
    await using var encryption = new ArdSessionEncryption(transport, auth);
    var session = FramebufferUpdateReader.CreateSession(
        new Framebuffer(10, 10, ProtocolLimits.Default),
        PixelFormat.WinArdBgra32,
        encryption.CreateDecoder());

    await session.ApplyAsync(CreateUpdateWith1103AndRawRectangle(), CancellationToken.None);

    Assert.Equal(ArdSessionEncryptionState.PendingActivation, encryption.State);
    Assert.False(transport.IsEncrypted);
    await encryption.CompleteFramebufferUpdateAsync(CancellationToken.None);
    Assert.True(transport.IsEncrypted);
}
```

- [ ] **步骤 3：运行协商测试并确认失败**

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --filter "FullyQualifiedName~ArdSessionEncryptionTests|FullyQualifiedName~ArdClientMessageWriterTests|FullyQualifiedName~FramebufferUpdateTests"
```

预期：FAIL，缺少 1103 decoder、消息 writer 和状态机。

- [ ] **步骤 4：实现状态机与 decoder 注入**

固定状态与方法：

```csharp
public enum ArdSessionEncryptionState { Plaintext, Requested, PendingActivation, Encrypted }

public sealed class ArdSessionEncryption : IAsyncDisposable
{
    public ArdSessionEncryption(ArdEncryptedStream transport, ArdAuthenticationResult authentication);
    public ArdSessionEncryptionState State { get; }
    public IRfbEncodingDecoder CreateDecoder();
    public async ValueTask RequestAsync(CancellationToken cancellationToken);
    public async ValueTask CompleteFramebufferUpdateAsync(CancellationToken cancellationToken);
}
```

`ArdSessionEncryptionEncoding.DecodeAsync` 必须要求 rectangle 为 `0,0,0,0`，精确读取 36 字节，验证 version=1，并调用控制器的内部 `AcceptSessionMaterial`。`CompleteFramebufferUpdateAsync` 仅在 PendingActivation 状态写明文 ack，等待完成，然后调用 transport.Activate。重复材料、乱序调用和缺失 auth key 都抛出 `RfbProtocolException`。

给 `FramebufferUpdateReader.CreateSession` 增加 `params IRfbEncodingDecoder[] additionalDecoders`，合并时拒绝重复 encoding ID，保持现有调用方无需修改。

- [ ] **步骤 5：运行协商测试并确认通过**

运行同上。预期：PASS。

- [ ] **步骤 6：提交协商状态机**

```powershell
git add src/WinARD.Remote.Protocol/Ard src/WinARD.Remote.Protocol/Encodings src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs tests/WinARD.Remote.Protocol.Tests/Ard tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs
git commit -m "feat: negotiate ARD session encryption"
```

### 任务 5：把加密请求放入正确的 ARD 初始化顺序

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs`

- [ ] **步骤 1：修改初始化测试，要求 1103 和 request 位于首帧请求之前**

更新 `ArdSetEncodingsMessage()` 的预期列表，包含 `1103`；为 initializer 注入 `ArdSessionEncryption`，并断言最后一条初始化写入是 request：

```csharp
Assert.Contains(1103, DecodeSetEncodings(stream.Writes));
Assert.Equal(
    new byte[] { 0x12, 0, 0, 1, 0, 1, 0, 1, 0, 0, 0, 1 },
    stream.Writes[^1]);
```

- [ ] **步骤 2：运行初始化测试并确认失败**

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --filter FullyQualifiedName~RfbSessionInitializerTests
```

预期：FAIL，encodings 不含 1103，且没有 SetEncryption request。

- [ ] **步骤 3：实现初始化顺序**

将 `RfbSessionInitializer.InitializeAsync` 增加可空参数：

```csharp
public static Task<RfbServerInit> InitializeAsync(
    Stream stream,
    RfbHandshakeResult handshake,
    ProtocolLimits limits,
    ArdSessionEncryption? sessionEncryption,
    CancellationToken cancellationToken);
```

保留原重载的现有明文行为，避免协议探针和既有初始化单元测试被隐式改成无法完成的半协商状态。只有显式传入 controller 的加密感知重载才把 1103 加入最终 encoding list，并在所有 bootstrap/session-selection 完成、最终 encodings 写完后调用 `sessionEncryption.RequestAsync`。桌面 `RfbClient` 必须使用该重载；不要在 initializer 内发送 framebuffer request。

- [ ] **步骤 4：运行初始化测试并确认通过**

运行同上。预期：PASS，并保持非 ARD/旧重载测试不变。

- [ ] **步骤 5：提交初始化顺序**

```powershell
git add src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs
git commit -m "feat: request ARD encryption during initialization"
```

### 任务 6：集成桌面 RfbClient 的整个读写链

**文件：**
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 修改：`src/WinARD.Desktop/Input/WindowsInputMapper.cs`
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/WindowsInputMapperTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写端到端内存流测试，证明 ACK 后所有消息为密文**

测试服务器依次提供 handshake、Type 30 challenge、ServerInit 和带 1103 的 FBU；客户端处理后发送输入：

```csharp
[Fact]
public async Task RfbClient_encrypts_pointer_key_and_tickle_reply_after_1103_activation()
{
    await using var server = FakeArdServer.WithSessionEncryption();
    await using var client = new RfbClient(server.ClientStream);

    await client.NegotiateAsync(CancellationToken.None);
    await client.AuthenticateAsync("alice", server.Secret, CancellationToken.None);
    await client.InitializeAsync(CancellationToken.None);
    _ = await client.ReceiveAsync(CancellationToken.None);
    await client.SendPointerAsync(0, 100, 200, CancellationToken.None);
    await client.SendKeyAsync(0x61, true, CancellationToken.None);

    var postAckWire = server.BytesAfterPlaintextEncryptionAck;
    Assert.DoesNotContain(new byte[] { 5, 0, 0, 100, 0, 200 }, postAckWire);
    Assert.DoesNotContain(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61 }, postAckWire);
    Assert.Equal(new byte[] { 5, 0, 0, 100, 0, 200, 4, 1, 0, 0, 0, 0, 0, 0x61 },
        server.DecryptClientPackets());
}
```

- [ ] **步骤 2：运行桌面客户端测试并确认失败**

```powershell
dotnet test tests\WinARD.Desktop.Tests\WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~RfbClient
```

预期：FAIL，因为 `RfbClient` 仍直接使用原始 stream 且丢弃 auth result。

- [ ] **步骤 3：集成统一 transport 与激活边界**

`RfbClient` 构造时创建 `ArdEncryptedStream` 并让所有 `RfbReader`、`RfbWriter`、initializer、framebuffer session 和 input writer 只使用该 stream。`AuthenticateAsync` 保存 `ArdAuthenticationResult`；`InitializeAsync` 创建 `ArdSessionEncryption`、注入 decoder 并发起 request。

`ReceiveAsync` 的 FBU 分支必须先完整 `ApplyBodyAsync`，再调用：

```csharp
await _sessionEncryption.CompleteFramebufferUpdateAsync(cancellationToken).ConfigureAwait(false);
```

随后才能返回 snapshot。StateChange Tickle 回复也必须使用同一 transport。Dispose 顺序为 framebuffer decoder → session encryption → transport → auth result；即使前一项抛错，也要在 `finally` 中继续清理剩余 secret owners，但不负责销毁调用方传入的底层 network stream，保持当前所有权约定。

- [ ] **步骤 4：加入安全诊断映射**

新增 failure kind：`ArdEncryptionNegotiation`、`ArdEncryptionPacket`、`ArdEncryptionIntegrity`。`RemoteSessionViewModelTests.cs` 增加参数化测试，确认现有诊断字段只输出 `ProtocolFailureKind`、read stage、server message type、encoding ID 与 rectangle index；不得增加 key、IV、payload 或输入内容字段：

```csharp
[Theory]
[InlineData(RfbProtocolFailureKind.ArdEncryptionNegotiation)]
[InlineData(RfbProtocolFailureKind.ArdEncryptionPacket)]
[InlineData(RfbProtocolFailureKind.ArdEncryptionIntegrity)]
public async Task Encryption_failures_emit_only_safe_protocol_metadata(RfbProtocolFailureKind kind)
{
    var diagnostic = await RecordReceiveFailureAsync(
        RfbProtocolException.Create("ARD encryption failed.", new RfbProtocolFailureInfo(kind)));

    Assert.Contains(diagnostic.Fields, field =>
        field.Name == "ProtocolFailureKind" && field.Value == kind.ToString());
    Assert.DoesNotContain(diagnostic.Fields, field =>
        field.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
        field.Name.Contains("Iv", StringComparison.OrdinalIgnoreCase) ||
        field.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **步骤 5：修正 ARD 的右键与中键位顺序**

先在 `WindowsInputMapperTests.cs` 把三个按钮的期望值分别固定为 left=1、right=2、middle=4：

```csharp
[Theory]
[InlineData(RemotePointerButtons.Left, 1)]
[InlineData(RemotePointerButtons.Right, 2)]
[InlineData(RemotePointerButtons.Middle, 4)]
public void ToPointerMask_uses_ard_button_order(RemotePointerButtons buttons, byte expected) =>
    Assert.Equal(expected, WindowsInputMapper.ToPointerMask(buttons));
```

运行：

```powershell
dotnet test tests\WinARD.Desktop.Tests\WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~WindowsInputMapperTests
```

预期先 FAIL（right 实际为 4、middle 实际为 2）；随后将 `ToPointerMask` 映射改为 left bit0、right bit1、middle bit2，再运行并确认 PASS。移动和左键位保持不变。

- [ ] **步骤 6：运行桌面集成测试并确认通过**

```powershell
dotnet test tests\WinARD.Desktop.Tests\WinARD.Desktop.Tests.csproj -c Release --filter "FullyQualifiedName~RfbClient|FullyQualifiedName~WindowsInputMapperTests|FullyQualifiedName~RemoteSessionViewModelTests"
```

预期：全部 PASS。

- [ ] **步骤 7：提交运行期集成**

```powershell
git add src/WinARD.Desktop/Services/RfbClientFactory.cs src/WinARD.Desktop/Input/WindowsInputMapper.cs tests/WinARD.Desktop.Tests/FramePresentationTests.cs tests/WinARD.Desktop.Tests/WindowsInputMapperTests.cs tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs
git commit -m "feat: encrypt ARD runtime traffic"
```

### 任务 7：完成回归验证、Release 构建和真实 Mac 交付

**文件：**
- 修改：仅修复本任务验证暴露的直接相关测试或诊断文案。

- [ ] **步骤 1：运行协议测试项目**

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release
```

预期：全部 PASS，0 failed。

- [ ] **步骤 2：运行桌面测试项目**

```powershell
dotnet test tests\WinARD.Desktop.Tests\WinARD.Desktop.Tests.csproj -c Release
```

预期：全部 PASS，0 failed。

- [ ] **步骤 3：运行全量解决方案测试**

```powershell
dotnet test WinARD.sln -c Release --no-restore
```

预期：全部 PASS，0 warning，0 error；若 `--no-restore` 因首次环境缺包失败，先执行 `dotnet restore WinARD.sln` 后原命令重跑。

- [ ] **步骤 4：构建 Release x64 可执行文件**

```powershell
dotnet publish src\WinARD.Desktop\WinARD.Desktop.csproj -c Release -r win-x64 --self-contained false -p:Platform=x64
```

预期：0 warning，0 error，并生成：

```text
src/WinARD.Desktop/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/WinARD.Desktop.exe
```

- [ ] **步骤 5：记录产物哈希和干净状态**

```powershell
Get-FileHash src\WinARD.Desktop\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\WinARD.Desktop.exe -Algorithm SHA256
git status --short
```

预期：输出 SHA-256；工作树为空。

- [ ] **步骤 6：交付真实 Mac 验证说明**

向用户提供 exe 绝对路径、SHA-256 和自动化测试数量，并明确要求在 macOS 26.5 上验证移动、左键、拖动、右键、普通文字与修饰键。未获得用户实测成功前，只能表述为“完整会话加密协议已实现并通过自动化测试”，不得宣称远程控制已经稳定完成。
