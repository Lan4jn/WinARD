# WinARD MVP 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 构建 Windows 10/11 x64 上可通过 TCP 或 SSH 连接 macOS 12+、使用 ARD 用户认证并进行画面、键鼠和纯文本剪贴板交互的 WinARD MVP。

**架构：** 采用 C#、.NET 8 和 WinUI 3。RFB/ARD 协议、传输、安全、持久化和 UI 通过小型接口隔离；先用模拟服务端和真实 Mac 验证协议，再接入设备库、Direct3D 渲染与发布打包。

**技术栈：** C# 12、.NET 8、WinUI 3 / Windows App SDK 1.6、Direct3D 11、Microsoft.Data.Sqlite、Windows OpenSSH、CommunityToolkit.Mvvm、xUnit、GitHub Actions。

---

## 文件结构

### 仓库根目录

- `.gitignore`：忽略构建、IDE、测试结果、凭据和视觉头脑风暴产物。
- `global.json`：请求 .NET 8 SDK，并允许在本机使用更高主版本 SDK。
- `Directory.Build.props`：统一目标框架、可空引用、分析器和警告策略。
- `Directory.Packages.props`：集中固定 NuGet 版本。
- `WinARD.sln`：解决方案入口。
- `LICENSE`：Apache-2.0 正文。
- `README.md`：开发环境、构建、测试和安全边界。

### 生产项目

- `src/WinARD.Domain/`：设备、连接配置、SSH 配置、凭据引用、会话状态和领域错误。
- `src/WinARD.Application/`：连接用例、会话状态机、设备库服务和端口接口。
- `src/WinARD.Remote.Protocol/`：RFB 字节读写、握手、ARD 认证、帧编码、输入和剪贴板。
- `src/WinARD.Transport/`：TCP、SSH channel、超时、主机密钥验证。
- `src/WinARD.Security/`：Windows 凭据管理器、Argon2id/AES-GCM 凭据库和敏感值包装。
- `src/WinARD.Infrastructure/`：SQLite、schema 迁移、Bonjour、日志遮盖和诊断导出。
- `src/WinARD.Desktop/`：WinUI 3 主窗口、连接编辑器、会话窗口、ViewModel 和 Direct3D 适配。
- `tools/WinARD.ProtocolProbe/`：无 UI 的协议探测工具，只用于开发期验证真实 Mac。

### 测试项目

- `tests/WinARD.Domain.Tests/`
- `tests/WinARD.Application.Tests/`
- `tests/WinARD.Remote.Protocol.Tests/`
- `tests/WinARD.Transport.Tests/`
- `tests/WinARD.Security.Tests/`
- `tests/WinARD.Infrastructure.Tests/`
- `tests/WinARD.Desktop.Tests/`
- `tests/WinARD.Testing/`：模拟 RFB/SSH 服务端、确定性时钟和脱敏测试夹具。

## 任务 1：建立可重复构建的解决方案骨架

**文件：**
- 创建：`.gitignore`
- 创建：`global.json`
- 创建：`Directory.Build.props`
- 创建：`Directory.Packages.props`
- 创建：`LICENSE`
- 创建：`README.md`
- 创建：`WinARD.sln`
- 创建：`src/*/*.csproj`
- 创建：`tests/*/*.csproj`
- 创建：`tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj`

- [ ] **步骤 1：编写仓库级构建配置**

```gitignore
bin/
obj/
.vs/
.idea/
TestResults/
artifacts/
.superpowers/
*.user
*.suo
*.pfx
*.snk
*.vault
```

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

```json
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestMajor"
  }
}
```

- [ ] **步骤 2：固定依赖版本**

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageVersion Include="Konscious.Security.Cryptography.Argon2" Version="1.3.1" />
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="8.0.25" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="8.0.1" />
    <PackageVersion Include="Microsoft.WindowsAppSDK" Version="1.6.250205002" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.2" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
  </ItemGroup>
</Project>
```

- [ ] **步骤 3：创建解决方案和项目引用**

运行：

```powershell
dotnet new sln -n WinARD
dotnet new classlib -n WinARD.Domain -o src/WinARD.Domain
dotnet new classlib -n WinARD.Application -o src/WinARD.Application
dotnet new classlib -n WinARD.Remote.Protocol -o src/WinARD.Remote.Protocol
dotnet new classlib -n WinARD.Transport -o src/WinARD.Transport
dotnet new classlib -n WinARD.Security -o src/WinARD.Security
dotnet new classlib -n WinARD.Infrastructure -o src/WinARD.Infrastructure
dotnet new console -n WinARD.ProtocolProbe -o tools/WinARD.ProtocolProbe
dotnet new xunit -n WinARD.Domain.Tests -o tests/WinARD.Domain.Tests
dotnet new xunit -n WinARD.Application.Tests -o tests/WinARD.Application.Tests
dotnet new xunit -n WinARD.Remote.Protocol.Tests -o tests/WinARD.Remote.Protocol.Tests
dotnet new xunit -n WinARD.Transport.Tests -o tests/WinARD.Transport.Tests
dotnet new xunit -n WinARD.Security.Tests -o tests/WinARD.Security.Tests
dotnet new xunit -n WinARD.Infrastructure.Tests -o tests/WinARD.Infrastructure.Tests
dotnet new xunit -n WinARD.Desktop.Tests -o tests/WinARD.Desktop.Tests
dotnet new classlib -n WinARD.Testing -o tests/WinARD.Testing
```

把全部项目加入 `WinARD.sln`，再按设计依赖添加 `ProjectReference`；任何生产项目不得引用 `WinARD.Desktop`。

```powershell
dotnet sln WinARD.sln add src/WinARD.Domain src/WinARD.Application src/WinARD.Remote.Protocol src/WinARD.Transport src/WinARD.Security src/WinARD.Infrastructure tools/WinARD.ProtocolProbe tests/WinARD.Domain.Tests tests/WinARD.Application.Tests tests/WinARD.Remote.Protocol.Tests tests/WinARD.Transport.Tests tests/WinARD.Security.Tests tests/WinARD.Infrastructure.Tests tests/WinARD.Desktop.Tests tests/WinARD.Testing
dotnet add src/WinARD.Application reference src/WinARD.Domain
dotnet add src/WinARD.Transport reference src/WinARD.Application src/WinARD.Domain
dotnet add src/WinARD.Security reference src/WinARD.Application src/WinARD.Domain
dotnet add src/WinARD.Infrastructure reference src/WinARD.Application src/WinARD.Domain
dotnet add tools/WinARD.ProtocolProbe reference src/WinARD.Remote.Protocol src/WinARD.Transport
dotnet add tests/WinARD.Domain.Tests reference src/WinARD.Domain
dotnet add tests/WinARD.Application.Tests reference src/WinARD.Application tests/WinARD.Testing
dotnet add tests/WinARD.Remote.Protocol.Tests reference src/WinARD.Remote.Protocol tests/WinARD.Testing
dotnet add tests/WinARD.Transport.Tests reference src/WinARD.Transport tests/WinARD.Testing
dotnet add tests/WinARD.Security.Tests reference src/WinARD.Security tests/WinARD.Testing
dotnet add tests/WinARD.Infrastructure.Tests reference src/WinARD.Infrastructure tests/WinARD.Testing
dotnet add tests/WinARD.Desktop.Tests reference src/WinARD.Application tests/WinARD.Testing
dotnet add src/WinARD.Security package Konscious.Security.Cryptography.Argon2
dotnet add src/WinARD.Infrastructure package Microsoft.Data.Sqlite
dotnet add src/WinARD.Infrastructure package Microsoft.Extensions.Logging
dotnet add tests/WinARD.Domain.Tests package coverlet.collector
dotnet add tests/WinARD.Application.Tests package coverlet.collector
dotnet add tests/WinARD.Remote.Protocol.Tests package coverlet.collector
dotnet add tests/WinARD.Transport.Tests package coverlet.collector
dotnet add tests/WinARD.Security.Tests package coverlet.collector
dotnet add tests/WinARD.Infrastructure.Tests package coverlet.collector
dotnet add tests/WinARD.Desktop.Tests package coverlet.collector
```

删除模板生成的 `Class1.cs` 和 `UnitTest1.cs`。测试项目的包引用使用 `Directory.Packages.props` 中的集中版本，不在单个 `.csproj` 重复版本号。

- [ ] **步骤 4：验证空骨架构建**

运行：`dotnet build WinARD.sln -warnaserror`

预期：`Build succeeded.`，0 warnings，0 errors。

- [ ] **步骤 5：提交骨架**

```powershell
git add .gitignore global.json Directory.Build.props Directory.Packages.props LICENSE README.md WinARD.sln src tests tools
git commit -m "build: bootstrap WinARD solution"
```

## 任务 2：定义领域模型和连接状态机

**文件：**
- 创建：`src/WinARD.Domain/Devices/Device.cs`
- 创建：`src/WinARD.Domain/Connections/ConnectionProfile.cs`
- 创建：`src/WinARD.Domain/Connections/SshProfile.cs`
- 创建：`src/WinARD.Domain/Security/CredentialReference.cs`
- 创建：`src/WinARD.Domain/Sessions/SessionState.cs`
- 创建：`src/WinARD.Domain/Errors/WinArdError.cs`
- 创建：`src/WinARD.Application/Sessions/SessionStateMachine.cs`
- 测试：`tests/WinARD.Domain.Tests/ConnectionProfileTests.cs`
- 测试：`tests/WinARD.Application.Tests/SessionStateMachineTests.cs`

- [ ] **步骤 1：先写无效配置与非法状态转换测试**

```csharp
[Fact]
public void Create_rejects_blank_host()
{
    Assert.Throws<ArgumentException>(() =>
        ConnectionProfile.Create(Guid.NewGuid(), "Mac", " ", 5900, "alex"));
}

[Fact]
public void Connected_cannot_transition_directly_to_authenticating()
{
    var machine = new SessionStateMachine();
    machine.MoveTo(SessionState.Resolving);
    machine.MoveTo(SessionState.Connecting);
    machine.MoveTo(SessionState.Negotiating);
    machine.MoveTo(SessionState.Authenticating);
    machine.MoveTo(SessionState.Initializing);
    machine.MoveTo(SessionState.Connected);

    Assert.Throws<InvalidOperationException>(() =>
        machine.MoveTo(SessionState.Authenticating));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj --filter SessionStateMachineTests`

预期：FAIL，类型 `SessionStateMachine` 不存在。

- [ ] **步骤 3：实现不可变配置和显式状态图**

```csharp
public enum SessionState
{
    Idle, Resolving, Connecting, Negotiating, Authenticating,
    Initializing, Connected, Reconnecting, Disconnecting, Failed
}

public sealed class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<SessionState, SessionState[]> Allowed =
        new Dictionary<SessionState, SessionState[]>
        {
            [SessionState.Idle] = [SessionState.Resolving],
            [SessionState.Resolving] = [SessionState.Connecting, SessionState.Failed, SessionState.Disconnecting],
            [SessionState.Connecting] = [SessionState.Negotiating, SessionState.Failed, SessionState.Disconnecting],
            [SessionState.Negotiating] = [SessionState.Authenticating, SessionState.Failed, SessionState.Disconnecting],
            [SessionState.Authenticating] = [SessionState.Initializing, SessionState.Failed, SessionState.Disconnecting],
            [SessionState.Initializing] = [SessionState.Connected, SessionState.Failed, SessionState.Disconnecting],
            [SessionState.Connected] = [SessionState.Reconnecting, SessionState.Disconnecting, SessionState.Failed],
            [SessionState.Reconnecting] = [SessionState.Connecting, SessionState.Disconnecting, SessionState.Failed],
            [SessionState.Disconnecting] = [SessionState.Idle],
            [SessionState.Failed] = [SessionState.Resolving, SessionState.Idle]
        };

    public SessionState Current { get; private set; } = SessionState.Idle;

    public void MoveTo(SessionState next)
    {
        if (!Allowed[Current].Contains(next))
            throw new InvalidOperationException($"Invalid session transition: {Current} -> {next}");
        Current = next;
    }
}
```

- [ ] **步骤 4：运行领域与应用测试**

运行：`dotnet test WinARD.sln --filter "FullyQualifiedName~WinARD.Domain.Tests|FullyQualifiedName~WinARD.Application.Tests"`

预期：全部 PASS。

- [ ] **步骤 5：提交领域基础**

```powershell
git add src/WinARD.Domain src/WinARD.Application tests/WinARD.Domain.Tests tests/WinARD.Application.Tests
git commit -m "feat: add connection domain and session state machine"
```

## 任务 3：实现安全的 RFB 字节读写基础

**文件：**
- 创建：`src/WinARD.Remote.Protocol/IO/RfbReader.cs`
- 创建：`src/WinARD.Remote.Protocol/IO/RfbWriter.cs`
- 创建：`src/WinARD.Remote.Protocol/IO/ProtocolLimits.cs`
- 创建：`src/WinARD.Remote.Protocol/Errors/RfbProtocolException.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/IO/RfbReaderTests.cs`

- [ ] **步骤 1：编写分片读取和恶意长度测试**

```csharp
[Fact]
public async Task ReadExactly_handles_one_byte_chunks()
{
    await using var stream = new ChunkedReadStream([0x00, 0x02, 0x12, 0x34], 1);
    var reader = new RfbReader(stream, ProtocolLimits.Default);
    Assert.Equal((ushort)2, await reader.ReadUInt16Async(CancellationToken.None));
    Assert.Equal((ushort)0x1234, await reader.ReadUInt16Async(CancellationToken.None));
}

[Fact]
public async Task ReadBytes_rejects_allocation_above_limit()
{
    var reader = new RfbReader(Stream.Null, new ProtocolLimits(1024, 1024 * 1024));
    await Assert.ThrowsAsync<RfbProtocolException>(() =>
        reader.ReadBytesAsync(1025, CancellationToken.None).AsTask());
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter RfbReaderTests`

预期：FAIL，`RfbReader` 不存在。

- [ ] **步骤 3：实现大端序读取器和上限**

```csharp
public sealed record ProtocolLimits(int MaxMessageBytes, int MaxFramebufferBytes)
{
    public static ProtocolLimits Default { get; } = new(16 * 1024 * 1024, 256 * 1024 * 1024);
}

public sealed class RfbReader(Stream stream, ProtocolLimits limits)
{
    public async ValueTask<ushort> ReadUInt16Async(CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(2, cancellationToken);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    public async ValueTask<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
    {
        if (count < 0 || count > limits.MaxMessageBytes)
            throw new RfbProtocolException($"RFB message length {count} exceeds limit.");
        var result = GC.AllocateUninitializedArray<byte>(count);
        await stream.ReadExactlyAsync(result, cancellationToken);
        return result;
    }
}
```

实现对应的 `RfbWriter`，只暴露明确的大端序整数和定长字节写入方法。

- [ ] **步骤 4：运行协议 IO 测试**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter "FullyQualifiedName~IO"`

预期：全部 PASS。

- [ ] **步骤 5：提交协议 IO**

```powershell
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests
git commit -m "feat: add bounded RFB protocol IO"
```

## 任务 4：实现 RFB 版本与安全类型协商

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Handshake/RfbVersion.cs`
- 创建：`src/WinARD.Remote.Protocol/Handshake/RfbHandshake.cs`
- 创建：`src/WinARD.Remote.Protocol/Handshake/RfbSecurityType.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Handshake/RfbHandshakeTests.cs`
- 创建：`tests/WinARD.Testing/Rfb/FakeRfbServer.cs`

- [ ] **步骤 1：编写 3.3、3.7、3.8 和缺少 ARD 类型的测试**

```csharp
[Theory]
[InlineData("RFB 003.003\n", "RFB 003.003\n")]
[InlineData("RFB 003.007\n", "RFB 003.007\n")]
[InlineData("RFB 003.008\n", "RFB 003.008\n")]
public async Task Negotiates_supported_versions(string serverBanner, string expectedClientBanner)
{
    await using var server = FakeRfbServer.ForVersion(serverBanner, [30]);
    var result = await RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None);
    Assert.Equal(expectedClientBanner, server.ReceivedVersion);
    Assert.Equal(RfbSecurityType.AppleRemoteDesktop, result.SecurityType);
}

[Fact]
public async Task Rejects_server_without_ard_security_type()
{
    await using var server = FakeRfbServer.ForVersion("RFB 003.008\n", [1, 2]);
    await Assert.ThrowsAsync<UnsupportedSecurityTypeException>(() =>
        RfbHandshake.NegotiateAsync(server.ClientStream, CancellationToken.None));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter RfbHandshakeTests`

预期：FAIL，握手类型不存在。

- [ ] **步骤 3：实现严格 banner 和 security type 协商**

```csharp
public enum RfbSecurityType : byte
{
    Invalid = 0,
    None = 1,
    VncAuthentication = 2,
    AppleRemoteDesktop = 30
}

public sealed record RfbHandshakeResult(RfbVersion Version, RfbSecurityType SecurityType);
```

实现必须拒绝非 12 字节 banner、未知版本、空安全类型列表和超过 255 项的输入；3.7/3.8 选择类型 30 并回写一个字节，3.3 按服务端指定类型验证。

- [ ] **步骤 4：运行握手和分片测试**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter "FullyQualifiedName~Handshake|FullyQualifiedName~RfbReader"`

预期：全部 PASS。

- [ ] **步骤 5：提交握手实现**

```powershell
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests tests/WinARD.Testing
git commit -m "feat: negotiate RFB and ARD security type"
```

## 任务 5：实现并实机验证 Apple Remote Desktop 认证

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Authentication/ArdAuthenticator.cs`
- 创建：`src/WinARD.Remote.Protocol/Authentication/ArdChallenge.cs`
- 创建：`src/WinARD.Remote.Protocol/Authentication/ISecretMaterial.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Authentication/ArdAuthenticatorTests.cs`
- 创建：`tools/WinARD.ProtocolProbe/Program.cs`
- 创建：`docs/protocol/ard-security-type-30.md`

- [ ] **步骤 1：编写确定性假服务端认证测试**

```csharp
[Fact]
public async Task Client_response_can_be_decrypted_by_ard_server_fixture()
{
    var fixture = ArdServerFixture.CreateDeterministic();
    var authenticator = new ArdAuthenticator(new DeterministicRandomSource(0x42));

    var response = await authenticator.CreateResponseAsync(
        fixture.Challenge,
        SecretMaterial.FromUtf8("alex"),
        SecretMaterial.FromUtf8("correct horse battery staple"),
        CancellationToken.None);

    var credentials = fixture.Decrypt(response);
    Assert.Equal("alex", credentials.Username);
    Assert.Equal("correct horse battery staple", credentials.Password);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter ArdAuthenticatorTests`

预期：FAIL，`ArdAuthenticator` 不存在。

- [ ] **步骤 3：实现 security type 30 挑战响应**

实现以下已定义 wire flow，并在 `docs/protocol/ard-security-type-30.md` 逐字段记录字节序和长度：读取 16 位 generator 与 16 位 key length；读取 modulus 和 server public key；生成客户端 Diffie-Hellman 密钥；从共享秘密派生 128 位 AES key；构造固定 128 字节凭据块（用户名 64 字节、密码 64 字节，均以 NUL 终止并保留随机填充）；AES 加密凭据块；发送加密凭据块与固定宽度 client public key；读取 RFB SecurityResult。

```csharp
public sealed record ArdChallenge(
    ushort Generator,
    ushort KeyLength,
    byte[] Modulus,
    byte[] ServerPublicKey);

public sealed record ArdResponse(byte[] EncryptedCredentials, byte[] ClientPublicKey);

public interface ISecretMaterial : IDisposable
{
    int Length { get; }
    void CopyTo(Span<byte> destination);
}
```

所有整数转换使用 unsigned big-endian；用户名和密码超过 63 个 UTF-8 字节时在发包前拒绝，不能静默截断。

- [ ] **步骤 4：用 ProtocolProbe 验证真实 Mac**

`ProtocolProbe` 从 `WINARD_HOST`、可选的 `WINARD_PORT` 和 `WINARD_USERNAME` 读取连接信息，未设置时在交互式控制台提示输入。密码始终通过隐藏的交互式控制台输入读取。任务 5 只验证 RFB 协商与 ARD 认证，不读取 `ServerInit` 或桌面尺寸。

运行：

```powershell
$env:WINARD_HOST=Read-Host 'Mac host or IP'
$env:WINARD_USERNAME=Read-Host 'macOS username'
# Optional: $env:WINARD_PORT=5900
dotnet run --project tools/WinARD.ProtocolProbe
```

预期：输出包含 `Security: AppleRemoteDesktop (30)` 和 `Authentication: success`；输出中不出现密码。任务 5 不读取 `ServerInit`，因此不输出桌面尺寸。

- [ ] **步骤 5：提交认证实现与协议记录**

```powershell
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests tools/WinARD.ProtocolProbe docs/protocol
git commit -m "feat: authenticate with Apple Remote Desktop"
```

## 任务 6：实现帧缓冲初始化和基础编码

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Framebuffer/PixelFormat.cs`
- 创建：`src/WinARD.Remote.Protocol/Framebuffer/Framebuffer.cs`
- 创建：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 创建：`src/WinARD.Remote.Protocol/Encodings/RawEncoding.cs`
- 创建：`src/WinARD.Remote.Protocol/Encodings/CopyRectEncoding.cs`
- 创建：`src/WinARD.Remote.Protocol/Encodings/DesktopSizeEncoding.cs`
- 创建：`src/WinARD.Remote.Protocol/Encodings/CursorEncoding.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`

- [ ] **步骤 1：编写像素、脏矩形、尺寸变化和越界测试**

```csharp
[Fact]
public async Task Raw_rectangle_updates_only_declared_region()
{
    var framebuffer = new Framebuffer(4, 4);
    var message = RfbMessages.RawRectangle(1, 1, 2, 2,
        [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255]);

    var dirty = await FramebufferUpdateReader.ApplyAsync(message, framebuffer, CancellationToken.None);

    Assert.Equal(new FramebufferRect(1, 1, 2, 2), Assert.Single(dirty));
    Assert.Equal(0xFFFF0000u, framebuffer.GetBgra32(1, 1));
}

[Fact]
public async Task Rectangle_outside_framebuffer_is_rejected()
{
    var framebuffer = new Framebuffer(4, 4);
    var message = RfbMessages.RawRectangle(3, 3, 2, 2, new byte[16]);
    await Assert.ThrowsAsync<RfbProtocolException>(() =>
        FramebufferUpdateReader.ApplyAsync(message, framebuffer, CancellationToken.None));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter FramebufferUpdateTests`

预期：FAIL，帧缓冲类型不存在。

- [ ] **步骤 3：实现 BGRA32 帧缓冲与基础编码注册表**

```csharp
public readonly record struct FramebufferRect(int X, int Y, int Width, int Height);

public interface IRfbEncodingDecoder
{
    int EncodingId { get; }
    ValueTask<IReadOnlyList<FramebufferRect>> DecodeAsync(
        RfbReader reader,
        Framebuffer framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken);
}
```

`Framebuffer` 在创建和尺寸变化前检查 `width * height * 4` 不超过 `ProtocolLimits.MaxFramebufferBytes`。Raw 转换服务端像素格式为 BGRA32；CopyRect 使用重叠安全拷贝；DesktopSize 原子替换缓冲区；Cursor 单独保存热点和像素，不写入桌面缓冲区。

- [ ] **步骤 4：运行帧缓冲测试并用 Probe 请求首帧**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter "FullyQualifiedName~Framebuffer"`

预期：全部 PASS。

运行：`dotnet run --project tools/WinARD.ProtocolProbe -- --capture-first-frame artifacts/first-frame.bgra`

预期：文件大小等于 `width * height * 4`，Probe 输出至少一个脏矩形。

- [ ] **步骤 5：提交基础画面协议**

```powershell
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests tools/WinARD.ProtocolProbe
git commit -m "feat: decode core RFB framebuffer updates"
```

## 任务 7：增加压缩帧、输入和纯文本剪贴板

**文件：**
- 创建：`src/WinARD.Remote.Protocol/Encodings/ZrleEncoding.cs`
- 创建：`src/WinARD.Remote.Protocol/Input/PointerEventWriter.cs`
- 创建：`src/WinARD.Remote.Protocol/Input/KeyEventWriter.cs`
- 创建：`src/WinARD.Remote.Protocol/Clipboard/ClipboardProtocol.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Encodings/ZrleEncodingTests.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Input/InputWriterTests.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Clipboard/ClipboardProtocolTests.cs`

- [ ] **步骤 1：编写 ZRLE tile、组合键和剪贴板上限测试**

```csharp
[Fact]
public async Task Zrle_solid_tile_fills_rectangle()
{
    var framebuffer = new Framebuffer(64, 64);
    var payload = ZrleFixtures.SolidTile(64, 64, bgra: 0xFF336699);
    await new ZrleEncoding().DecodeAsync(payload.Reader, framebuffer,
        new FramebufferRect(0, 0, 64, 64), CancellationToken.None);
    Assert.Equal(0xFF336699u, framebuffer.GetBgra32(63, 63));
}

[Fact]
public void Clipboard_rejects_text_above_one_megabyte()
{
    var protocol = new ClipboardProtocol(1_048_576);
    Assert.Throws<ClipboardTooLargeException>(() => protocol.Encode(new string('x', 1_048_577)));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests --filter "ZrleEncodingTests|InputWriterTests|ClipboardProtocolTests"`

预期：FAIL，相关类型不存在。

- [ ] **步骤 3：实现 ZRLE、RFB 输入消息和文本适配**

ZRLE 解码必须限制压缩与解压长度，逐 64×64 tile 处理 raw、solid、packed palette 和 plain RLE 子编码。键盘适配器维护已按下 keysym 集合，窗口失焦或断开时按相反顺序发送 key-up，避免远端修饰键卡住。

同时将 encoding decoder 的返回值扩展为结构化结果，直接携带 dirty rectangles 与 pixel-content rectangles，替换 Task 6 中 `PixelContentVersion` / `LastPixelContentRect` 的单矩形旁路状态，以支持 ZRLE 等单次解码产生多个像素内容矩形。

```csharp
public sealed class ClipboardProtocol(int maxUtf8Bytes)
{
    public byte[] Encode(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        if (bytes.Length > maxUtf8Bytes) throw new ClipboardTooLargeException(bytes.Length);
        return bytes;
    }
}
```

- [ ] **步骤 4：运行协议测试**

运行：`dotnet test tests/WinARD.Remote.Protocol.Tests`

预期：全部 PASS，测试进程无挂起。

- [ ] **步骤 5：提交会话交互协议**

```powershell
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests
git commit -m "feat: add compressed frames input and clipboard"
```

## 任务 8：实现 TCP 与 SSH 传输

**文件：**
- 创建：`src/WinARD.Application/Ports/IRemoteTransportFactory.cs`
- 创建：`src/WinARD.Transport/Tcp/TcpRemoteTransport.cs`
- 创建：`src/WinARD.Transport/Ssh/SshRemoteTransport.cs`
- 创建：`src/WinARD.Transport/Ssh/SshHostKeyVerifier.cs`
- 创建：`src/WinARD.Transport/Ssh/OpenSshCommand.cs`
- 创建：`src/WinARD.Transport/Ssh/OpenSshKnownHosts.cs`
- 创建：`src/WinARD.Transport/Ssh/OpenSshProcess.cs`
- 创建：`src/WinARD.Transport/TransportTimeouts.cs`
- 测试：`tests/WinARD.Transport.Tests/TcpRemoteTransportTests.cs`
- 测试：`tests/WinARD.Transport.Tests/SshHostKeyVerifierTests.cs`

- [ ] **步骤 1：编写取消、超时和主机密钥变化测试**

```csharp
[Fact]
public async Task Changed_host_key_is_blocked()
{
    var verifier = new SshHostKeyVerifier(new InMemoryHostKeyStore("server", "SHA256:old"));
    var result = await verifier.VerifyAsync("server", "ssh-ed25519", "SHA256:new", CancellationToken.None);
    Assert.Equal(HostKeyDecision.ChangedAndBlocked, result.Decision);
}

[Fact]
public async Task Tcp_connect_honors_timeout()
{
    var transport = new TcpRemoteTransport(TimeProvider.System, TimeSpan.FromMilliseconds(20));
    await Assert.ThrowsAsync<TransportTimeoutException>(() =>
        transport.ConnectAsync("192.0.2.1", 5900, CancellationToken.None));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Transport.Tests`

预期：FAIL，传输类型不存在。

- [ ] **步骤 3：实现统一 TransportConnection**

```csharp
public sealed class TransportConnection(Stream stream, EndPointDescription endpoint) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public EndPointDescription Endpoint { get; } = endpoint;
    public ValueTask DisposeAsync() => stream.DisposeAsync();
}

public interface IRemoteTransportFactory
{
    Task<TransportConnection> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken);
}
```

TCP 使用 `TcpClient.ConnectAsync(host, port, cancellationToken)`。SSH 使用受控的系统 OpenSSH 进程，通过 `ssh.exe -T -W <rfbHost>:<rfbPort>` 的 stdin/stdout 暴露双向流，不创建本地 TCP 监听端口。所有参数必须通过 `ProcessStartInfo.ArgumentList` 传入，并关闭 shell 执行。

生产环境只从 `%WINDIR%\System32\OpenSSH` 解析 `ssh.exe` 与 `ssh-keyscan.exe`，或使用管理员显式配置且已验证存在、文件名匹配的绝对路径；不得从当前目录或 `PATH` 搜索同名程序。Authenticode 签名验证不在 Task 8 范围内，作为发布前供应链门禁记录。

连接前运行有界的 `ssh-keyscan.exe`，独立解析原始公钥并计算 SHA-256 指纹；首次未知返回“需要用户确认”，已变化返回“阻断”。只有端点、算法和原始公钥均与固定值匹配，才为本次连接创建端点专用的临时 `known_hosts`，并使用 `StrictHostKeyChecking=yes`，禁止读取或更新全局及用户 known_hosts。

`ssh-keyscan` 的 stdout/stderr 必须分别按字节数限制并并发排空。超限、取消、读取失败或非零退出都要保留主异常，再使用独立短超时 best-effort 执行进程树终止、等待退出和释放；任何 cleanup 异常不得覆盖主异常。临时 `known_hosts` 的流关闭和显式删除必须分别尝试，`DeleteOnClose` 仅作为附加防线。

隧道就绪以 RFB 服务端会立即发送 banner 为前提：在同一个总连接截止时间内读取至少一个 stdout 字节，并通过前缀流把该字节回放给协议层。stderr 必须异步排空、脱敏并限制诊断摘要长度；取消、超时、远端拒绝和并发释放均须终止进程树并删除临时 known_hosts。

密码及加密私钥口令不得进入命令行、环境变量、stdin、日志或异常。Task 8 只定义 askpass broker 边界并在存在凭据引用时明确报“不支持”；安全 askpass 集成和真实 OpenSSH 互操作验证作为 Task 9 的外部门槛。

- [ ] **步骤 4：运行传输测试**

运行：`dotnet test tests/WinARD.Transport.Tests`

预期：全部 PASS。

- [ ] **步骤 5：提交传输层**

```powershell
git add src/WinARD.Application src/WinARD.Transport tests/WinARD.Transport.Tests
git commit -m "feat: add TCP and verified SSH transports"
```

- [ ] **步骤 6：将 SSH 传输重构为 OpenSSH stdio forwarding**

运行：`dotnet test tests/WinARD.Transport.Tests`

预期：OpenSSH 参数、host-key pin、首字节回放、共享截止时间、诊断脱敏和幂等清理测试全部 PASS，生产程序集不再引用 `Renci.SshNet`。

```powershell
git add Directory.Packages.props src/WinARD.Domain src/WinARD.Transport tests/WinARD.Transport.Tests docs/superpowers/plans/2026-07-25-winard-mvp.md
git commit -m "refactor: use OpenSSH stdio forwarding for SSH transport"
```

## 任务 9：实现两种凭据后端

**文件：**
- 创建：`src/WinARD.Application/Ports/ICredentialStore.cs`
- 创建：`src/WinARD.Security/Secrets/SecretBuffer.cs`
- 创建：`src/WinARD.Security/WindowsCredentials/WindowsCredentialStore.cs`
- 创建：`src/WinARD.Security/WindowsCredentials/CredentialNativeMethods.cs`
- 创建：`src/WinARD.Security/Vault/EncryptedCredentialVault.cs`
- 创建：`src/WinARD.Security/Vault/VaultFileFormat.cs`
- 创建：`src/WinARD.Security/Migration/CredentialStoreMigrator.cs`
- 测试：`tests/WinARD.Security.Tests/SecretBufferTests.cs`
- 测试：`tests/WinARD.Security.Tests/EncryptedCredentialVaultTests.cs`
- 测试：`tests/WinARD.Security.Tests/CredentialStoreMigratorTests.cs`

- [ ] **步骤 1：编写解锁、篡改、自动锁定和清理测试**

```csharp
[Fact]
public async Task Tampered_ciphertext_cannot_be_read()
{
    var storage = new InMemoryVaultStorage();
    await using var vault = await EncryptedCredentialVault.CreateAsync(storage, "master-password", TestTime.Provider);
    await vault.SaveAsync(new CredentialReference("mac", "1"), "secret", CancellationToken.None);
    storage.FlipLastByte();
    await Assert.ThrowsAsync<CryptographicException>(() =>
        vault.ReadAsync(new CredentialReference("mac", "1"), CancellationToken.None));
}

[Fact]
public void SecretBuffer_zeroes_memory_on_dispose()
{
    var bytes = Encoding.UTF8.GetBytes("secret");
    var secret = SecretBuffer.CopyFrom(bytes);
    secret.Dispose();
    Assert.True(secret.IsClearedForTesting);
}

[Fact]
public async Task Migration_deletes_source_only_after_target_verification()
{
    var source = InMemoryCredentialStore.With("mac/1", "secret");
    var target = new InMemoryCredentialStore();
    var migrator = new CredentialStoreMigrator();

    await migrator.MoveAsync(source, target, new CredentialReference("mac", "1"), CancellationToken.None);

    Assert.Null(await source.ReadTextForTestingAsync("mac/1"));
    Assert.Equal("secret", await target.ReadTextForTestingAsync("mac/1"));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Security.Tests`

预期：FAIL，凭据实现不存在。

- [ ] **步骤 3：实现凭据接口和加密格式**

```csharp
public interface ICredentialStore
{
    Task SaveAsync(CredentialReference reference, SecretBuffer secret, CancellationToken cancellationToken);
    Task<SecretBuffer?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken);
    Task DeleteAsync(CredentialReference reference, CancellationToken cancellationToken);
}
```

Vault 文件头固定包含 magic、format version、Argon2id 参数和 16 字节盐；每个条目包含引用、12 字节 nonce、ciphertext 和 16 字节 GCM tag。Argon2id 参数初始设为 64 MiB、3 iterations、2 lanes，并在文件中持久化以便未来升级。Windows 后端使用 `CredWriteW`、`CredReadW` 和 `CredDeleteW`，target name 前缀固定为 `WinARD/`。

`CredentialStoreMigrator` 逐项读取源秘密、写入目标、重新读取目标并做固定时间摘要比对，只有验证成功才删除源条目；任一步失败都保留源条目并清理临时缓冲区。

- [ ] **步骤 4：运行安全测试并扫描测试输出**

运行：`dotnet test tests/WinARD.Security.Tests --logger "console;verbosity=detailed"`

预期：全部 PASS；输出中不含测试字符串 `master-password` 或 `secret`。

- [ ] **步骤 5：提交安全存储**

```powershell
git add src/WinARD.Application src/WinARD.Security tests/WinARD.Security.Tests
git commit -m "feat: protect credentials with Windows and encrypted stores"
```

## 任务 10：实现 SQLite 设备库与 Bonjour 发现

**文件：**
- 创建：`src/WinARD.Application/Ports/IDeviceRepository.cs`
- 创建：`src/WinARD.Application/Ports/IDeviceDiscovery.cs`
- 创建：`src/WinARD.Infrastructure/Database/WinArdDatabase.cs`
- 创建：`src/WinARD.Infrastructure/Database/Migrations/Migration001Initial.cs`
- 创建：`src/WinARD.Infrastructure/Devices/SqliteDeviceRepository.cs`
- 创建：`src/WinARD.Infrastructure/Discovery/BonjourDeviceDiscovery.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/SqliteDeviceRepositoryTests.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/BonjourDeviceDiscoveryTests.cs`

- [ ] **步骤 1：编写往返、迁移回滚和发现去重测试**

```csharp
[Fact]
public async Task Saved_profile_round_trips_without_secret_value()
{
    await using var database = await TestDatabase.CreateAsync();
    var repository = new SqliteDeviceRepository(database.Connection);
    var profile = ConnectionProfile.Create(Guid.NewGuid(), "Studio Mac", "studio-mac.local", 5900, "alex")
        .WithCredential(new CredentialReference("mac", "device-1"));

    await repository.SaveAsync(profile, CancellationToken.None);
    var loaded = await repository.GetAsync(profile.Id, CancellationToken.None);

    Assert.Equal(profile, loaded);
    Assert.DoesNotContain("password", database.ReadRawText(), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Infrastructure.Tests`

预期：FAIL，仓储类型不存在。

- [ ] **步骤 3：实现事务迁移、仓储与 `_rfb._tcp` watcher**

```sql
CREATE TABLE devices (
    id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    host TEXT NOT NULL,
    port INTEGER NOT NULL,
    mac_username TEXT NOT NULL,
    transport_mode INTEGER NOT NULL,
    credential_reference TEXT NULL,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE UNIQUE INDEX ux_devices_host_port ON devices(host, port);

CREATE TABLE ssh_profiles (
    device_id TEXT PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
    ssh_host TEXT NOT NULL,
    ssh_port INTEGER NOT NULL,
    ssh_username TEXT NOT NULL,
    private_key_path TEXT NULL,
    pinned_host_key_algorithm TEXT NULL,
    pinned_host_key_sha256 TEXT NULL,
    credential_reference TEXT NULL
);

CREATE TABLE app_settings (
    setting_key TEXT PRIMARY KEY,
    setting_value TEXT NOT NULL
);

CREATE TABLE schema_version (
    version INTEGER NOT NULL
);
INSERT INTO schema_version(version) VALUES (1);
```

Bonjour 使用 Windows `DnssdServiceWatcher` 浏览 `_rfb._tcp`，把 add/update/remove 转换为不可变 `DiscoveredDevice`。仓储仅在用户点击添加后持久化发现项。

- [ ] **步骤 4：运行基础设施测试**

运行：`dotnet test tests/WinARD.Infrastructure.Tests`

预期：全部 PASS。

- [ ] **步骤 5：提交设备库和发现**

```powershell
git add src/WinARD.Application src/WinARD.Infrastructure tests/WinARD.Infrastructure.Tests
git commit -m "feat: persist devices and discover RFB services"
```

## 任务 11：实现连接编排、错误映射和重连

**文件：**
- 创建：`src/WinARD.Application/Sessions/RemoteSession.cs`
- 创建：`src/WinARD.Application/Sessions/ConnectDeviceHandler.cs`
- 创建：`src/WinARD.Application/Sessions/ActiveSessionCoordinator.cs`
- 创建：`src/WinARD.Application/Sessions/ReconnectPolicy.cs`
- 创建：`src/WinARD.Application/Ports/IConnectionSecretProvider.cs`
- 创建：`src/WinARD.Application/Errors/ErrorMapper.cs`
- 测试：`tests/WinARD.Application.Tests/ConnectDeviceHandlerTests.cs`
- 测试：`tests/WinARD.Application.Tests/ReconnectPolicyTests.cs`

- [ ] **步骤 1：编写阶段错误与可取消退避测试**

```csharp
[Fact]
public async Task Authentication_failure_reports_authenticating_stage()
{
    var transport = FakeTransport.Success();
    var protocol = FakeProtocol.AuthenticationFailure();
    var handler = TestHandlers.Connect(transport, protocol);

    var result = await handler.ExecuteAsync(TestProfiles.Direct, CancellationToken.None);

    Assert.Equal(SessionState.Failed, result.State);
    Assert.Equal(ConnectionStage.Authenticating, result.Error!.Stage);
    Assert.Equal("ARD_AUTH_REJECTED", result.Error.Code);
}

[Fact]
public void Backoff_is_capped_and_deterministic_with_test_random()
{
    var policy = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), new FixedRandom(0.5));
    Assert.Equal(TimeSpan.FromSeconds(30), policy.DelayForAttempt(10));
}

[Fact]
public async Task Second_active_session_is_rejected_by_mvp_coordinator()
{
    var coordinator = new ActiveSessionCoordinator();
    await using var first = await coordinator.AcquireAsync(Guid.NewGuid(), CancellationToken.None);
    await Assert.ThrowsAsync<SessionAlreadyActiveException>(() =>
        coordinator.AcquireAsync(Guid.NewGuid(), CancellationToken.None));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Application.Tests --filter "ConnectDeviceHandlerTests|ReconnectPolicyTests"`

预期：FAIL，连接处理器不存在。

- [ ] **步骤 3：实现单一会话编排器**

```csharp
public sealed record ConnectResult(SessionState State, RemoteSession? Session, WinArdError? Error);

public sealed class ConnectDeviceHandler(
    IRemoteTransportFactory transports,
    IConnectionSecretProvider secrets,
    IRfbClientFactory clients,
    IErrorMapper errors)
{
    public async Task<ConnectResult> ExecuteAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var machine = new SessionStateMachine();
        SecretBuffer? secret = null;
        TransportConnection? transport = null;
        IRfbClient? client = null;

        try
        {
            machine.MoveTo(SessionState.Resolving);
            secret = await secrets.GetMacPasswordAsync(profile, cancellationToken);

            machine.MoveTo(SessionState.Connecting);
            transport = await transports.ConnectAsync(profile, cancellationToken);

            machine.MoveTo(SessionState.Negotiating);
            client = clients.Create(transport.Stream);
            await client.NegotiateAsync(cancellationToken);

            machine.MoveTo(SessionState.Authenticating);
            await client.AuthenticateAsync(profile.MacUsername, secret, cancellationToken);
            secret.Dispose();
            secret = null;

            machine.MoveTo(SessionState.Initializing);
            await client.InitializeAsync(cancellationToken);
            machine.MoveTo(SessionState.Connected);

            return new ConnectResult(
                machine.Current,
                new RemoteSession(machine, transport, client),
                null);
        }
        catch (OperationCanceledException)
        {
            if (client is not null) await client.DisposeAsync();
            if (transport is not null) await transport.DisposeAsync();
            secret?.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            if (client is not null) await client.DisposeAsync();
            if (transport is not null) await transport.DisposeAsync();
            secret?.Dispose();
            var failedAt = machine.Current;
            machine.MoveTo(SessionState.Failed);
            return new ConnectResult(machine.Current, null, errors.Map(failedAt, exception));
        }
    }
}
```

`IConnectionSecretProvider` 根据配置从 Windows 凭据管理器、加密库或本地密码对话框取得 `SecretBuffer`，因此 `ConnectDeviceHandler` 不需要分支判断秘密来源。

`ActiveSessionCoordinator` 在 MVP 中只允许一个活动会话；会话释放后才允许下一次连接。主窗口在已有活动会话时禁用其他设备的连接按钮，并提供“切换到当前会话”。

`ErrorMapper` 把 DNS、SSH、主机密钥、RFB 协商、认证、协议和取消异常映射为稳定代码；未知异常生成安全摘要并保留仅供日志使用的 correlation ID。

- [ ] **步骤 4：运行应用测试**

运行：`dotnet test tests/WinARD.Application.Tests`

预期：全部 PASS。

- [ ] **步骤 5：提交连接用例**

```powershell
git add src/WinARD.Application tests/WinARD.Application.Tests
git commit -m "feat: orchestrate remote sessions and reconnects"
```

## 任务 12：建立 WinUI 3 应用壳与设备库

**文件：**
- 创建：`src/WinARD.Desktop/WinARD.Desktop.csproj`
- 创建：`src/WinARD.Desktop/App.xaml`
- 创建：`src/WinARD.Desktop/App.xaml.cs`
- 创建：`src/WinARD.Desktop/MainWindow.xaml`
- 创建：`src/WinARD.Desktop/MainWindow.xaml.cs`
- 创建：`src/WinARD.Desktop/ViewModels/MainWindowViewModel.cs`
- 创建：`src/WinARD.Desktop/ViewModels/DeviceItemViewModel.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/MainWindowViewModelTests.cs`

- [ ] **步骤 1：编写加载、搜索和选择设备测试**

```csharp
[Fact]
public async Task Search_filters_saved_devices_without_hiding_discovery_updates()
{
    var repository = FakeDeviceRepository.With("Studio Mac", "Office Mini");
    var discovery = FakeDeviceDiscovery.With("Nearby MacBook");
    var viewModel = new MainWindowViewModel(repository, discovery);
    await viewModel.InitializeAsync(CancellationToken.None);

    viewModel.SearchText = "studio";

    Assert.Equal("Studio Mac", Assert.Single(viewModel.FilteredDevices).DisplayName);
    Assert.Equal("Nearby MacBook", Assert.Single(viewModel.DiscoveredDevices).DisplayName);
}

[Fact]
public async Task Delete_device_uses_explicit_credential_choice()
{
    var repository = FakeDeviceRepository.With("Studio Mac");
    var credentials = new RecordingCredentialDeletion();
    var viewModel = TestViewModels.MainWindow(repository, credentials);
    await viewModel.InitializeAsync(CancellationToken.None);

    await viewModel.DeleteSelectedAsync(deleteCredential: false, CancellationToken.None);

    Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    Assert.False(credentials.WasDeleted);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Desktop.Tests --filter MainWindowViewModelTests`

预期：FAIL，ViewModel 不存在。

- [ ] **步骤 3：实现 WinUI 项目和设备库布局**

```xml
<!-- src/WinARD.Desktop/WinARD.Desktop.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.WindowsAppSDK" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WinARD.Application\WinARD.Application.csproj" />
    <ProjectReference Include="..\WinARD.Infrastructure\WinARD.Infrastructure.csproj" />
    <ProjectReference Include="..\WinARD.Remote.Protocol\WinARD.Remote.Protocol.csproj" />
    <ProjectReference Include="..\WinARD.Security\WinARD.Security.csproj" />
    <ProjectReference Include="..\WinARD.Transport\WinARD.Transport.csproj" />
  </ItemGroup>
</Project>
```

```xml
<Grid ColumnDefinitions="320,*">
  <Grid Grid.Column="0" RowDefinitions="Auto,Auto,*,Auto" Padding="16">
    <TextBlock Text="设备" Style="{StaticResource TitleTextBlockStyle}" />
    <TextBox Grid.Row="1" Text="{x:Bind ViewModel.SearchText, Mode=TwoWay}" PlaceholderText="搜索设备" />
    <ListView Grid.Row="2" ItemsSource="{x:Bind ViewModel.FilteredDevices}" SelectedItem="{x:Bind ViewModel.SelectedDevice, Mode=TwoWay}" />
    <ListView Grid.Row="3" ItemsSource="{x:Bind ViewModel.DiscoveredDevices}" />
  </Grid>
  <local:DeviceDetailsView Grid.Column="1" Device="{x:Bind ViewModel.SelectedDevice, Mode=OneWay}" />
</Grid>
```

使用依赖注入注册仓储、发现、连接处理器、凭据后端和 ViewModel。UI 线程外执行数据库和网络操作，集合更新通过 `DispatcherQueue` 回到 UI 线程。

```powershell
dotnet sln WinARD.sln add src/WinARD.Desktop/WinARD.Desktop.csproj
dotnet add tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj reference src/WinARD.Desktop/WinARD.Desktop.csproj
```

- [ ] **步骤 4：运行 ViewModel 测试并启动应用**

运行：`dotnet test tests/WinARD.Desktop.Tests`

预期：全部 PASS。

运行：`dotnet run --project src/WinARD.Desktop`

预期：主窗口显示空设备状态、添加按钮和附近发现区域，无未处理异常。

- [ ] **步骤 5：提交应用壳**

```powershell
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests WinARD.sln
git commit -m "feat: add WinUI device library shell"
```

## 任务 13：实现连接编辑器和凭据选择

**文件：**
- 创建：`src/WinARD.Desktop/Views/ConnectionEditorDialog.xaml`
- 创建：`src/WinARD.Desktop/Views/ConnectionEditorDialog.xaml.cs`
- 创建：`src/WinARD.Desktop/ViewModels/ConnectionEditorViewModel.cs`
- 创建：`src/WinARD.Desktop/ViewModels/CredentialPromptViewModel.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/ConnectionEditorViewModelTests.cs`

- [ ] **步骤 1：编写验证和测试连接阶段测试**

```csharp
[Fact]
public async Task Save_is_disabled_until_required_fields_are_valid()
{
    var viewModel = TestViewModels.ConnectionEditor();
    viewModel.Host = "";
    Assert.False(viewModel.SaveCommand.CanExecute(null));
    viewModel.Host = "studio-mac.local";
    viewModel.MacUsername = "alex";
    Assert.True(viewModel.SaveCommand.CanExecute(null));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Desktop.Tests --filter ConnectionEditorViewModelTests`

预期：FAIL，编辑器 ViewModel 不存在。

- [ ] **步骤 3：实现表单、SSH 展开区和阶段结果**

```csharp
public enum CredentialSaveMode
{
    WindowsCredentialManager,
    EncryptedVault,
    AskEveryTime
}

public sealed record ConnectionTestStageResult(
    ConnectionStage Stage,
    bool Succeeded,
    TimeSpan Duration,
    string Message);
```

“测试连接”调用与正式连接相同的编排组件，但在初始化成功后立即正常断开；成功不自动保存。密码输入使用 `PasswordBox`，不得绑定到长生命周期字符串属性，提交时立即复制到 `SecretBuffer` 并清空控件。

- [ ] **步骤 4：运行 UI 逻辑测试**

运行：`dotnet test tests/WinARD.Desktop.Tests --filter "ConnectionEditor|CredentialPrompt"`

预期：全部 PASS。

- [ ] **步骤 5：提交连接编辑器**

```powershell
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "feat: add connection editor and credential choices"
```

## 任务 14：实现远程会话窗口与 Direct3D 渲染

**文件：**
- 创建：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`
- 创建：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 创建：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 创建：`src/WinARD.Desktop/Rendering/IFramePresenter.cs`
- 创建：`src/WinARD.Desktop/Rendering/D3DFramePresenter.cs`
- 创建：`src/WinARD.Desktop/Input/WindowsInputMapper.cs`
- 创建：`src/WinARD.Desktop/Clipboard/WindowsClipboardBridge.cs`
- 测试：`tests/WinARD.Desktop.Tests/Rendering/FramePresentationTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/Input/WindowsInputMapperTests.cs`

- [ ] **步骤 1：编写缩放坐标、脏矩形和键释放测试**

```csharp
[Fact]
public void Pointer_coordinates_map_through_fit_to_window_transform()
{
    var transform = ViewportTransform.Fit(new SizeInt32(1920, 1080), new SizeInt32(1280, 800));
    Assert.Equal(new PointInt32(960, 540), transform.ToRemote(new PointInt32(640, 400)));
}

[Fact]
public void Losing_focus_releases_pressed_modifier_keys()
{
    var mapper = new WindowsInputMapper();
    mapper.KeyDown(VirtualKey.Control);
    var events = mapper.ReleaseAll();
    Assert.Equal(KeyEvent.Up(0xFFE3), Assert.Single(events));
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/WinARD.Desktop.Tests --filter "FramePresentationTests|WindowsInputMapperTests"`

预期：FAIL，渲染和输入类型不存在。

- [ ] **步骤 3：实现会话窗口和帧提交接口**

```csharp
public interface IFramePresenter : IAsyncDisposable
{
    void Resize(int width, int height);
    void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<FramebufferRect> dirtyRects);
}
```

`D3DFramePresenter` 创建 BGRA8 dynamic texture，只上传脏矩形并通过 WinUI `SwapChainPanel` 呈现。UI 线程只调度最新可用帧；协议线程不得等待显示器刷新。窗口工具栏提供适应窗口、100%、组合键、剪贴板、全屏和断开。

- [ ] **步骤 4：运行测试和本地实机会话**

运行：`dotnet test tests/WinARD.Desktop.Tests`

预期：全部 PASS。

运行：`dotnet run --project src/WinARD.Desktop`

预期：连接真实 Mac 后可连续操作鼠标键盘、切换全屏和同步双向纯文本；窗口失焦后远端无卡住的修饰键。

- [ ] **步骤 5：提交会话 UI**

```powershell
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "feat: render and control remote Mac sessions"
```

## 任务 15：完成日志遮盖、诊断导出和错误卡片

**文件：**
- 创建：`src/WinARD.Infrastructure/Diagnostics/SecretRedactor.cs`
- 创建：`src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs`
- 创建：`src/WinARD.Desktop/Views/ConnectionErrorCard.xaml`
- 创建：`src/WinARD.Desktop/ViewModels/ConnectionErrorViewModel.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/SecretRedactorTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/ConnectionErrorViewModelTests.cs`

- [ ] **步骤 1：编写秘密、剪贴板和指纹错误测试**

```csharp
[Fact]
public void Diagnostic_export_removes_registered_secrets_and_clipboard_text()
{
    var redactor = new SecretRedactor(["mac-password", "key-passphrase"]);
    var output = redactor.Redact("auth mac-password clipboard=private text key-passphrase");
    Assert.DoesNotContain("mac-password", output);
    Assert.DoesNotContain("key-passphrase", output);
    Assert.DoesNotContain("private text", output);
}
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test WinARD.sln --filter "FullyQualifiedName~SecretRedactor|FullyQualifiedName~ConnectionError"`

预期：FAIL，诊断类型不存在。

- [ ] **步骤 3：实现结构化诊断与操作按钮**

```csharp
public sealed record ConnectionErrorAction(string Label, ConnectionErrorActionKind Kind);

public sealed record ConnectionErrorViewModel(
    string Title,
    string Summary,
    string CorrelationId,
    IReadOnlyList<ConnectionErrorAction> Actions);
```

错误卡片按代码提供固定操作：权限错误打开 macOS 设置说明；SSH 指纹变化展示新旧指纹但默认只有取消；认证错误允许重新输入；协议不支持允许复制已遮盖诊断。

- [ ] **步骤 4：运行诊断测试**

运行：`dotnet test WinARD.sln --filter "FullyQualifiedName~WinARD.Infrastructure.Tests|FullyQualifiedName~WinARD.Desktop.Tests"`

预期：全部 PASS，测试日志不含注入的秘密。

- [ ] **步骤 5：提交诊断体验**

```powershell
git add src/WinARD.Infrastructure src/WinARD.Desktop tests/WinARD.Infrastructure.Tests tests/WinARD.Desktop.Tests
git commit -m "feat: add safe diagnostics and actionable errors"
```

## 任务 16：打包、CI、许可证和最终验收

**文件：**
- 创建：`src/WinARD.Desktop/Package.appxmanifest`
- 创建：`packaging/portable.ps1`
- 创建：`packaging/check-licenses.ps1`
- 创建：`packaging/verify-artifacts.ps1`
- 创建：`.github/workflows/ci.yml`
- 创建：`.github/workflows/package.yml`
- 创建：`THIRD-PARTY-NOTICES.md`
- 创建：`docs/testing/compatibility-matrix.md`
- 创建：`docs/testing/release-checklist.md`
- 修改：`README.md`

- [ ] **步骤 1：先创建自动验收脚本并验证其会因缺少产物失败**

```powershell
# packaging/verify-artifacts.ps1
$ErrorActionPreference = 'Stop'
$required = @('artifacts/WinARD.msix', 'artifacts/WinARD-portable-win-x64.zip')
foreach ($path in $required) {
    if (-not (Test-Path $path)) { throw "Missing release artifact: $path" }
}
```

运行：`pwsh -File packaging/verify-artifacts.ps1`

预期：FAIL，提示缺少两个发布产物。

- [ ] **步骤 2：实现 MSIX 与便携 ZIP 构建**

```xml
<!-- src/WinARD.Desktop/Package.appxmanifest 中的关键声明 -->
<Identity Name="WinARD" Publisher="CN=WinARD Development" Version="0.1.0.0" />
<Dependencies>
  <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
</Dependencies>
<Capabilities>
  <Capability Name="internetClient" />
  <Capability Name="privateNetworkClientServer" />
</Capabilities>
```

```powershell
dotnet publish src/WinARD.Desktop/WinARD.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/portable
Compress-Archive -Path artifacts/portable/* -DestinationPath artifacts/WinARD-portable-win-x64.zip -Force
dotnet publish src/WinARD.Desktop/WinARD.Desktop.csproj -c Release -r win-x64 --self-contained true -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxBundle=Never -p:AppxPackageDir="$PWD\artifacts\msix\"
$generatedMsix = Get-ChildItem artifacts/msix -Recurse -Filter *.msix | Select-Object -First 1
if ($null -eq $generatedMsix) { throw 'MSIX build did not produce an .msix file.' }
Copy-Item -LiteralPath $generatedMsix.FullName -Destination artifacts/WinARD.msix -Force
```

MSIX 使用同一 Release 输出，identity 名称固定为 `WinARD`，最低 Windows 版本为 `10.0.19041.0`。测试产物不签名；正式发布前在 checklist 中要求代码签名。

- [ ] **步骤 3：建立 CI 门禁**

```yaml
name: ci
on: [push, pull_request]
jobs:
  build-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - run: dotnet restore WinARD.sln
      - run: dotnet format WinARD.sln --verify-no-changes --no-restore
      - run: dotnet build WinARD.sln -c Release --no-restore -warnaserror
      - run: dotnet test WinARD.sln -c Release --no-build --collect:"XPlat Code Coverage"
```

`packaging/check-licenses.ps1` 读取所有 `obj/project.assets.json` 中的直接和传递 NuGet 包，再读取本机 NuGet cache 中对应 `.nuspec` 的 SPDX license expression。允许列表固定为 `MIT`、`Apache-2.0`、`BSD-2-Clause`、`BSD-3-Clause`、`ISC` 和 `MS-PL`；GPL、AGPL、缺失或不在列表内的表达式使脚本退出 1。脚本同时以包名、版本、许可证和项目 URL重建 `THIRD-PARTY-NOTICES.md`。

CI 在构建后运行：

```yaml
      - run: pwsh -File packaging/check-licenses.ps1
```

- [ ] **步骤 4：执行完整自动化验证**

运行：

```powershell
dotnet format WinARD.sln --verify-no-changes
dotnet build WinARD.sln -c Release -warnaserror
dotnet test WinARD.sln -c Release --collect:"XPlat Code Coverage"
pwsh -File packaging/portable.ps1
pwsh -File packaging/verify-artifacts.ps1
```

预期：格式检查、构建和测试全部通过；MSIX 与 ZIP 均存在。

- [ ] **步骤 5：执行兼容矩阵和两小时稳定性测试**

在 `docs/testing/compatibility-matrix.md` 记录 Windows 10 22H2、Windows 11，以及 macOS 12、13、14、15、26 的认证、画面、输入和剪贴板结果。macOS 12 与当前稳定版必须完成 TCP、SSH 密码、SSH 私钥、两种凭据后端及两小时连续会话；其他 macOS 版本完成冒烟测试。

预期：无崩溃；稳定画面下工作集在预热后不持续单调增长；日志、SQLite 和诊断包不包含测试密码或剪贴板正文。

- [ ] **步骤 6：请求最终代码审查并提交发布工程**

```powershell
git add .github packaging src/WinARD.Desktop/Package.appxmanifest THIRD-PARTY-NOTICES.md docs/testing README.md
git commit -m "build: package and verify WinARD MVP"
```

## 最终完成条件

- `dotnet format WinARD.sln --verify-no-changes` 退出码为 0。
- `dotnet build WinARD.sln -c Release -warnaserror` 退出码为 0。
- `dotnet test WinARD.sln -c Release` 全部通过。
- ProtocolProbe 在真实 macOS 12+ 上使用 ARD 用户认证成功。
- 主应用通过 TCP 与 SSH 两种路径完成画面、键鼠和纯文本剪贴板交互。
- Windows 凭据管理器与加密凭据库均通过篡改和泄露测试。
- MSIX 与便携 ZIP 在干净 Windows 10/11 x64 环境启动。
- 兼容矩阵、两小时稳定性记录和第三方许可证清单完整。
