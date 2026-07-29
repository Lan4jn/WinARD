# ARD 加密包诊断细分实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 将当前笼统的 `ArdEncryptionPacket` 断线细分为安全、可导出的加密包阶段、方向、序号和密文长度，并发布一次正常连接即可采集证据的 win-x64 构建。

**架构：** 扩展不可变的 `RfbProtocolFailureInfo`，让协议层携带 ARD 加密包上下文；`ArdEncryptedPacketCodec` 标注解密验证阶段，`ArdEncryptedStream` 补充外层长度、截断和接收序号；桌面诊断层只导出白名单中的标量字段。所有错误继续 fail closed，不记录任何 key、IV、密文、明文或摘要。

**技术栈：** C# 12、.NET 8、xUnit、Windows App SDK、AES-CBC/SHA-1 ARD 协议实现、结构化安全诊断。

---

## 文件结构

- 修改 `src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`：定义 ARD 包方向/阶段枚举和可合并的非秘密上下文字段。
- 修改 `src/WinARD.Remote.Protocol/Ard/ArdEncryptedPacketCodec.cs`：为每个解密拒绝点附加精确阶段、接收序号和密文长度。
- 修改 `src/WinARD.Remote.Protocol/Ard/ArdEncryptedStream.cs`：为外层长度、截断读取和状态提交失败补充方向与包位置。
- 修改 `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`：将新增字段加入安全诊断白名单。
- 修改 `tests/WinARD.Remote.Protocol.Tests/Errors/RfbProtocolFailureInfoTests.cs`：覆盖默认值、上下文合并和不可变性。
- 修改 `tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedPacketCodecTests.cs`：覆盖阶段分类。
- 修改 `tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedStreamTests.cs`：覆盖外层长度与截断上下文。
- 修改 `tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`：覆盖导出字段及秘密字段排除。

### 任务 1：扩展结构化协议失败上下文

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Errors/RfbProtocolFailureInfoTests.cs`

- [ ] **步骤 1：编写失败的默认值与合并测试**

在 `RfbProtocolFailureInfoTests` 增加测试，要求新字段默认为空，并由内部失败优先、外部上下文补空值：

```csharp
var inner = new RfbProtocolFailureInfo(
    RfbProtocolFailureKind.ArdEncryptionPacket,
    ArdEncryptionStage: ArdEncryptedPacketFailureStage.Padding,
    ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
    ArdEncryptionSequence: 1,
    ArdCiphertextLength: 48);
var outer = new RfbProtocolFailureInfo(
    RfbProtocolFailureKind.UnexpectedServerMessage,
    RfbProtocolReadStage.ServerMessageType,
    ArdEncryptionStage: ArdEncryptedPacketFailureStage.OuterLength,
    ArdEncryptionDirection: ArdEncryptedPacketDirection.Send,
    ArdEncryptionSequence: 9,
    ArdCiphertextLength: 64);

var result = inner.FillMissingFrom(outer);

Assert.Equal(ArdEncryptedPacketFailureStage.Padding, result.ArdEncryptionStage);
Assert.Equal(ArdEncryptedPacketDirection.Receive, result.ArdEncryptionDirection);
Assert.Equal(1u, result.ArdEncryptionSequence);
Assert.Equal(48, result.ArdCiphertextLength);
Assert.Equal(RfbProtocolReadStage.ServerMessageType, result.ReadStage);
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RfbProtocolFailureInfoTests"
```

预期：FAIL，编译器报告缺少 `ArdEncryptedPacketFailureStage`、`ArdEncryptedPacketDirection` 或新增命名参数。

- [ ] **步骤 3：实现最少的不可变上下文模型**

在 `RfbProtocolFailureInfo.cs` 增加：

```csharp
public enum ArdEncryptedPacketDirection
{
    Send,
    Receive,
}

public enum ArdEncryptedPacketFailureStage
{
    OuterLength,
    TruncatedCiphertext,
    CbcDecrypt,
    PlaintextTooShort,
    PayloadLength,
    Padding,
    Integrity,
    StateCommit,
}
```

将 record 末尾扩展为：

```csharp
ArdEncryptedPacketFailureStage? ArdEncryptionStage = null,
ArdEncryptedPacketDirection? ArdEncryptionDirection = null,
uint? ArdEncryptionSequence = null,
int? ArdCiphertextLength = null
```

并在 `FillMissingFrom` 中分别使用 `当前值 ?? outer.值` 合并四个字段。

- [ ] **步骤 4：运行测试验证通过**

运行任务 1 步骤 2 的命令。

预期：PASS，`RfbProtocolFailureInfoTests` 全部通过。

- [ ] **步骤 5：Commit**

```powershell
git add src\WinARD.Remote.Protocol\Errors\RfbProtocolFailureInfo.cs tests\WinARD.Remote.Protocol.Tests\Errors\RfbProtocolFailureInfoTests.cs
git commit -m "feat: describe ARD packet failures"
```

### 任务 2：细分加密包 codec 的解密失败阶段

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdEncryptedPacketCodec.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedPacketCodecTests.cs`

- [ ] **步骤 1：编写失败的阶段分类测试**

扩展已有非法密文、SHA-1 损坏测试，并用 AES-CBC 固定夹具构造长度、payload 和 padding 错误。断言示例：

```csharp
var exception = Assert.Throws<RfbProtocolException>(() =>
    ArdEncryptedPacketCodec.Decrypt(Key, InitialIv, 3, ciphertext));

Assert.Equal(RfbProtocolFailureKind.ArdEncryptionPacket, exception.Failure?.Kind);
Assert.Equal(ArdEncryptedPacketFailureStage.Padding, exception.Failure?.ArdEncryptionStage);
Assert.Equal(ArdEncryptedPacketDirection.Receive, exception.Failure?.ArdEncryptionDirection);
Assert.Equal(3u, exception.Failure?.ArdEncryptionSequence);
Assert.Equal(ciphertext.Length, exception.Failure?.ArdCiphertextLength);
```

分别覆盖 `OuterLength`、`PlaintextTooShort`、`PayloadLength`、`Padding` 和 `Integrity`；完整性失败仍断言 kind 为 `ArdEncryptionIntegrity`。

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ArdEncryptedPacketCodecTests"
```

预期：FAIL，现有异常没有 `ArdEncryptionStage` 等上下文。

- [ ] **步骤 3：让 codec 在拒绝点创建精确失败**

将 `PacketFailure` 改为接收阶段、序号和长度：

```csharp
private static RfbProtocolException PacketFailure(
    string message,
    RfbProtocolFailureKind kind,
    ArdEncryptedPacketFailureStage stage,
    uint sequence,
    int ciphertextLength) =>
    RfbProtocolException.Create(
        message,
        new RfbProtocolFailureInfo(
            kind,
            ArdEncryptionStage: stage,
            ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
            ArdEncryptionSequence: sequence,
            ArdCiphertextLength: ciphertextLength));
```

每个分支传入对应阶段；`CryptographicException` 使用 `CbcDecrypt`，摘要不匹配使用 `Integrity` 和 `ArdEncryptionIntegrity`。不得把 key、IV、ciphertext、plaintext 或 expected digest 放入异常或 failure info。

- [ ] **步骤 4：运行测试验证通过**

运行任务 2 步骤 2 的命令。

预期：PASS，所有 codec 测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add src\WinARD.Remote.Protocol\Ard\ArdEncryptedPacketCodec.cs tests\WinARD.Remote.Protocol.Tests\Ard\ArdEncryptedPacketCodecTests.cs
git commit -m "feat: classify ARD decrypt failures"
```

### 任务 3：补充加密 Stream 的外层包位置

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdEncryptedStream.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdEncryptedStreamTests.cs`

- [ ] **步骤 1：编写失败的外层长度与截断测试**

增加两个测试：非法二字节长度在 sequence 0 报 `OuterLength`；声明 32 字节但只提供 16 字节时报 `TruncatedCiphertext`。公共断言：

```csharp
Assert.Equal(ArdEncryptedPacketDirection.Receive, exception.Failure?.ArdEncryptionDirection);
Assert.Equal(0u, exception.Failure?.ArdEncryptionSequence);
Assert.Equal(32, exception.Failure?.ArdCiphertextLength);
```

另增加 `WithContext(ServerMessageType)` 后这些字段仍保留的断言。

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
dotnet test tests\WinARD.Remote.Protocol.Tests\WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ArdEncryptedStreamTests"
```

预期：FAIL，外层读取异常缺少 ARD 包上下文。

- [ ] **步骤 3：在读取边界附加上下文**

读取 header 前在 `_stateSync` 下取得当前 `_receiveSequence`。外层长度失败创建：

```csharp
new RfbProtocolFailureInfo(
    RfbProtocolFailureKind.ArdEncryptionPacket,
    ArdEncryptionStage: ArdEncryptedPacketFailureStage.OuterLength,
    ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
    ArdEncryptionSequence: sequence,
    ArdCiphertextLength: ciphertextLength)
```

读取 ciphertext 截断时将现有 `TruncatedRead` 异常通过 `WithContext` 补为 `TruncatedCiphertext`，同时保留 kind。codec 返回后若状态提交检查失败，将上下文补为 `StateCommit`。不要改变 fail-closed、IV、序号或清零行为。

- [ ] **步骤 4：运行测试验证通过**

运行任务 3 步骤 2 的命令。

预期：PASS，所有 stream 测试通过。

- [ ] **步骤 5：Commit**

```powershell
git add src\WinARD.Remote.Protocol\Ard\ArdEncryptedStream.cs tests\WinARD.Remote.Protocol.Tests\Ard\ArdEncryptedStreamTests.cs
git commit -m "feat: trace ARD receive packet position"
```

### 任务 4：将安全字段加入桌面诊断白名单

**文件：**
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写失败的安全导出测试**

构造带完整 ARD 上下文的 `RfbProtocolException`，要求事件字段精确为：

```csharp
[
    ("ProtocolFailureKind", "ArdEncryptionPacket"),
    ("ProtocolReadStage", "ServerMessageType"),
    ("ArdEncryptionStage", "Padding"),
    ("ArdEncryptionDirection", "Receive"),
    ("ArdEncryptionSequence", "1"),
    ("ArdCiphertextLength", "48"),
]
```

同时断言不存在名为或前缀为 `Key`、`Iv`、`Payload`、`CiphertextBytes`、`Plaintext`、`Digest`、`Hash` 的字段；允许精确的 `ArdCiphertextLength` 标量字段。所有字段值不得包含原始异常消息或任何测试用秘密字节表示。

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
dotnet test tests\WinARD.Desktop.Tests\WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Encryption_failures"
```

预期：FAIL，诊断仅含现有 `ProtocolFailureKind` 和读取上下文。

- [ ] **步骤 3：扩展诊断字段白名单**

将 `GetProtocolFailureFields` 初始容量从 5 调整为 9，并在现有字段后加入：

```csharp
if (failure.ArdEncryptionStage is { } stage)
    fields.Add(new("ArdEncryptionStage", stage.ToString(), DiagnosticFieldCategory.Public));
if (failure.ArdEncryptionDirection is { } direction)
    fields.Add(new("ArdEncryptionDirection", direction.ToString(), DiagnosticFieldCategory.Public));
if (failure.ArdEncryptionSequence is { } sequence)
    fields.Add(new("ArdEncryptionSequence", sequence.ToString(CultureInfo.InvariantCulture), DiagnosticFieldCategory.Public));
if (failure.ArdCiphertextLength is { } length)
    fields.Add(new("ArdCiphertextLength", length.ToString(CultureInfo.InvariantCulture), DiagnosticFieldCategory.Public));
```

不要导出异常 message 或任何 byte buffer。

- [ ] **步骤 4：运行测试验证通过**

运行任务 4 步骤 2 的命令，并运行：

```powershell
dotnet test tests\WinARD.Desktop.Tests\WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RemoteSessionViewModelTests"
```

预期：PASS，诊断相关测试全部通过。

- [ ] **步骤 5：Commit**

```powershell
git add src\WinARD.Desktop\ViewModels\RemoteSessionViewModel.cs tests\WinARD.Desktop.Tests\ViewModels\RemoteSessionViewModelTests.cs
git commit -m "feat: export safe ARD packet context"
```

### 任务 5：全量验证与发布诊断构建

**文件：**
- 不修改生产源文件。
- 产物：`F:\Documents\Windows ARD Client\artifacts\WinARD-<commit>-ard-packet-diagnostics-win-x64.zip`

- [ ] **步骤 1：运行格式和工作树检查**

```powershell
git diff --check
git status --short --branch
```

预期：无 diff 错误；源代码提交完成后工作树干净。

- [ ] **步骤 2：运行严格 Release 构建**

```powershell
dotnet build WinARD.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
```

预期：exit 0，0 warnings，0 errors。

- [ ] **步骤 3：运行全量测试**

```powershell
dotnet test WinARD.sln -c Release --no-build
```

预期：exit 0，全部测试通过，0 failed。

- [ ] **步骤 4：发布 win-x64**

```powershell
dotnet publish src\WinARD.Desktop\WinARD.Desktop.csproj -c Release -r win-x64 --self-contained false -p:Platform=x64
```

预期：发布目录为 `src\WinARD.Desktop\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish`。

- [ ] **步骤 5：打包并核验产物**

使用当前短提交号组成唯一 ZIP 名；`Compress-Archive` 打包整个 publish 目录。运行 `Get-FileHash -Algorithm SHA256`，并用 `tar -tf` 确认 ZIP 包含 `WinARD.Desktop.exe`。

预期：输出 ZIP 绝对路径、字节大小和 SHA-256；工作树仍干净。

- [ ] **步骤 6：实机验证交接**

要求用户只进行一次普通连接，等待当前断线出现并导出诊断。明确说明此构建用于定位连续加密包规则，不宣称鼠标或键盘控制已经修复。
