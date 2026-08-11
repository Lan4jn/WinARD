# WinARD 零安装自适应画质实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 在目标 Mac 零安装的前提下，以公开 Zlib/ZRLE/Raw、标准 PixelFormat、已确认的 ARD 缩放报文和现有刷新节奏构建 WinARD 自己的内容感知多维自适应画质系统。

**架构：** 用户意图以设备级 `QualityProfile` 保存，纯 Application 控制器根据 5 秒性能窗口输出不可变 `QualityDecision`。Desktop 在唯一 outstanding framebuffer 请求完成的安全边界协调变化，Remote.Protocol/RfbClient 将配置报文、解码器替换和一次非增量修复帧作为原子事务应用。Apple 1002/1001 只进入独立证据门，证据不足时标准能力照常交付。

**技术栈：** .NET 8、C# 12、WinUI 3、SQLite、RFB 3.8/Apple 3.889、ARD security type 30、xUnit、现有 `ClientMessageScheduler`/`FramebufferUpdateSession`/`SessionPerformanceTracker`。

---

## 约束和不变量

- 用户已明确要求直接在 `master` 主工作区开发，不创建 worktree。
- 不实现、声明或猜测 MVS；不复制、链接或分发 RDM DLL。
- 不自动使用 Apple 1000 黑白模式。
- 1002/1001 的前缀不是正式解码证据；没有完整边界和像素语义就关闭候选。
- PixelFormat、decoder session 和编码声明必须原子切换；部分写入后不得继续使用当前连接。
- 缩放后的 framebuffer 尺寸时序没有实机证据前返回 `ReconnectRequired`，不得用预计尺寸请求。
- 每个任务先写测试、看到预期红灯，再写最少实现。
- 每项实现后依次进行规格审查和代码质量审查；Critical/Important 全部关闭后才能进入下一任务。
- `artifacts/protocol-research` 保持 ignored，不进入 Git。

## 文件结构

### 新建

- `src/WinARD.Domain/Connections/QualityProfile.cs`：持久化用户画质意图和锁定项。
- `src/WinARD.Application/Quality/QualityModels.cs`：观测、决策、能力、状态和原因枚举。
- `src/WinARD.Application/Quality/AdaptiveQualityController.cs`：内容状态机和多维滞回控制器。
- `src/WinARD.Remote.Protocol/Encodings/PersistentZlibInflater.cs`：ZRLE 与 Zlib 共用的每连接持久 inflater、边界和故障状态机。
- `src/WinARD.Remote.Protocol/Encodings/ZlibEncoding.cs`：encoding 6 的 raw-pixel payload 解码器。
- `src/WinARD.Desktop/ViewModels/QualityTransitionCoordinator.cs`：安全边界和修复帧协调。
- `src/WinARD.Desktop/ViewModels/QualityPresentation.cs`：工具栏摘要、选项和无障碍文字。
- `src/WinARD.Infrastructure/Database/Migrations/Migration003QualityProfile.cs`：v2 到 v3 数据库迁移。
- `tests/WinARD.Domain.Tests/Connections/QualityProfileTests.cs`
- `tests/WinARD.Application.Tests/Quality/AdaptiveQualityControllerTests.cs`
- `tests/WinARD.Remote.Protocol.Tests/Encodings/ZlibEncodingTests.cs`
- `tests/WinARD.Desktop.Tests/ViewModels/QualityTransitionCoordinatorTests.cs`
- `tests/WinARD.Desktop.Tests/ViewModels/QualityPresentationTests.cs`
- `docs/protocol/ard-zero-install-image-quality.md`
- `docs/testing/zero-install-adaptive-quality-macos.md`

### 修改

- `src/WinARD.Domain/Connections/ConnectionProfile.cs`
- `src/WinARD.Remote.Protocol/Framebuffer/PixelFormat.cs`
- `src/WinARD.Remote.Protocol/Encodings/RfbEncodingType.cs`
- `src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- `src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateSession.cs`
- `src/WinARD.Remote.Protocol/IO/ProtocolLimits.cs`
- `src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs`
- `src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs`
- `src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs`
- `src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- `src/WinARD.Application/Sessions/RemoteSession.cs`
- `src/WinARD.Desktop/Services/RfbClientFactory.cs`
- `src/WinARD.Desktop/ViewModels/SessionPerformanceTracker.cs`
- `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`
- `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- `src/WinARD.Desktop/Services/ConnectionSessionController.cs`
- `src/WinARD.Infrastructure/Database/WinArdDatabase.cs`
- `src/WinARD.Infrastructure/Devices/SqliteDeviceRepository.cs`
- `src/WinARD.Desktop/Services/DesktopDiagnosticContextFactory.cs`
- `src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs`
- `tools/WinARD.ProtocolProbe/ProbeCommandLine.cs`
- `tools/WinARD.ProtocolProbe/Program.cs`
- `tools/WinARD.ProtocolProbe/EncodingResearch/*`
- 对应现有测试文件。

## 任务 1：关闭 MVS 交付门并建立标准能力基线

**文件：**

- 创建：`docs/protocol/ard-zero-install-image-quality.md`
- 修改：`docs/protocol/ard-mvs-evidence.md`（文件不存在时创建）
- 测试：文档静态检查

- [ ] **步骤 1：写明唯一门结论**

`ard-mvs-evidence.md` 必须包含以下不可含糊的结论：

```markdown
## 证据门结论

证据门关闭。WinARD 不在正式 SetEncodings 中声明 MVS，也不实现或分发 MVS decoder。
关闭原因是缺少许可证兼容的完整载荷实现与可重复完整边界证据；已观察到候选名称或前缀不能替代这些条件。
```

`ard-zero-install-image-quality.md` 记录可交付能力：Zlib 6、ZRLE 16、Raw 0、PixelFormat、`08 00 + binary64_be` 缩放，以及 1002/1001 的独立证据门。

- [ ] **步骤 2：运行占位符和敏感内容检查**

```powershell
$forbidden = @(('TO'+'DO'), ('T'+'BD'), ('待'+'定'), ('PLACE'+'HOLDER'), ('填'+'入'))
Select-String -Path `
  'docs/protocol/ard-mvs-evidence.md', `
  'docs/protocol/ard-zero-install-image-quality.md' `
  -Pattern $forbidden
rg -n -i "password|username|payload-prefix\.bin|[A-Z]:\\" `
  docs/protocol/ard-mvs-evidence.md `
  docs/protocol/ard-zero-install-image-quality.md
```

预期：占位符无输出；敏感扫描只允许命中解释禁令，不出现真实值或绝对路径。

- [ ] **步骤 3：提交**

```powershell
git add docs/protocol/ard-mvs-evidence.md docs/protocol/ard-zero-install-image-quality.md
git commit -m "docs: close MVS delivery gate"
```

## 任务 2：定义设备级画质意图并迁移数据库

**文件：**

- 创建：`src/WinARD.Domain/Connections/QualityProfile.cs`
- 修改：`src/WinARD.Domain/Connections/ConnectionProfile.cs`
- 创建：`src/WinARD.Infrastructure/Database/Migrations/Migration003QualityProfile.cs`
- 修改：`src/WinARD.Infrastructure/Database/WinArdDatabase.cs`
- 修改：`src/WinARD.Infrastructure/Devices/SqliteDeviceRepository.cs`
- 创建：`tests/WinARD.Domain.Tests/Connections/QualityProfileTests.cs`
- 修改：`tests/WinARD.Infrastructure.Tests/WinArdDatabaseTests.cs`
- 修改：`tests/WinARD.Infrastructure.Tests/SqliteDeviceRepositoryTests.cs`

- [ ] **步骤 1：编写失败的领域测试**

```csharp
[Fact]
public void Automatic_profile_defaults_to_two_mib_and_allows_gray()
{
    var profile = QualityProfile.Automatic;

    Assert.Equal(QualityPreset.Automatic, profile.Preset);
    Assert.Equal(2L * 1024 * 1024, profile.TargetBytesPerSecond);
    Assert.True(profile.AllowAutomaticGrayscale);
    Assert.False(profile.ColorLocked);
    Assert.False(profile.ScaleLocked);
    Assert.False(profile.RefreshLocked);
}

[Fact]
public void Quality_color_has_no_black_and_white_value()
{
    Assert.DoesNotContain("BlackAndWhite", Enum.GetNames<QualityColor>());
}

[Fact]
public void Profile_rejects_non_positive_bandwidth()
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        QualityProfile.CreateCustom(
            targetBytesPerSecond: 0,
            color: QualityColor.Automatic,
            scale: QualityScale.Automatic,
            refresh: FrameRefreshPolicy.Automatic));
}
```

- [ ] **步骤 2：运行测试并确认红灯**

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~QualityProfileTests
```

预期：编译失败，`QualityProfile` 不存在。

- [ ] **步骤 3：实现最小领域模型**

```csharp
public enum QualityPreset { Automatic, Original, Balanced, Smooth, Custom }
public enum QualityColor { Automatic, Full32, Color16, Grayscale }
public enum QualityScale { Automatic, Native, Percent75, Percent50 }

public sealed record QualityProfile
{
    public static QualityProfile Automatic { get; } = new(
        QualityPreset.Automatic,
        2L * 1024 * 1024,
        QualityColor.Automatic,
        QualityScale.Automatic,
        FrameRefreshPolicy.Automatic,
        true,
        false,
        false,
        false);

    // 构造函数验证正带宽或“不限制”哨兵、合法枚举和锁定组合。
}
```

`ConnectionProfile` 增加 `QualityProfile Quality`，并用 `WithQualityProfile` 保持其他字段不变。现有 `WithFrameRefreshPolicy` 更新 `Quality.Refresh`，避免两份刷新意图分叉。

- [ ] **步骤 4：先写迁移失败测试**

从 v2 fixture 打开数据库，断言：

```csharp
Assert.Equal(3, await ReadSchemaVersionAsync(connection));
Assert.Equal(FrameRefreshMode.Fixed, restored.FrameRefreshPolicy.Mode);
Assert.Equal(60, restored.FrameRefreshPolicy.FixedFramesPerSecond);
Assert.Equal(QualityPreset.Custom, restored.Quality.Preset);
Assert.Equal(QualityColor.Automatic, restored.Quality.Color);
Assert.True(restored.Quality.AllowAutomaticGrayscale);
```

再测试 Automatic 和 Unlimited 旧值不丢失、Direct/SSH 字段不移位、非法数据库组合在仓储边界报错。

- [ ] **步骤 5：实现 migration 003 和仓储读写**

新增显式列：`quality_preset`、`quality_bandwidth_bps`、`quality_color`、`quality_scale`、`quality_allow_gray`、三个 lock 位。使用 CHECK 约束枚举整数、带宽范围和 bool；SELECT/INSERT ordinal 统一定义常量，避免硬编码错位。

- [ ] **步骤 6：验证并提交**

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~QualityProfile
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Migration|FullyQualifiedName~SqliteDeviceRepository"
git add src/WinARD.Domain src/WinARD.Infrastructure tests/WinARD.Domain.Tests tests/WinARD.Infrastructure.Tests
git commit -m "feat: persist adaptive quality profiles"
```

## 任务 3：使 PixelFormat 和初始化声明可配置

**文件：**

- 修改：`src/WinARD.Remote.Protocol/Framebuffer/PixelFormat.cs`
- 修改：`src/WinARD.Remote.Protocol/Initialization/RfbSessionInitializer.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Encodings/ZrleEncodingTests.cs`

- [ ] **步骤 1：编写 16 位和 wire 失败测试**

```csharp
[Fact]
public async Task Set_pixel_format_writer_emits_exact_rgb565_wire_message()
{
    await using var stream = new MemoryStream();
    await RfbSessionInitializer.WriteSetPixelFormatAsync(
        stream,
        PixelFormat.WinArdRgb565,
        CancellationToken.None);

    Assert.Equal(
        Convert.FromHexString("0000000010100001001F003F001F0B0500000000"),
        stream.ToArray());
}
```

另测 BGRA32、null/invalid format、预取消零写入和 `SessionDeclaration` 保序声明 `[6,16,0,1,-239,-223]`。

- [ ] **步骤 2：运行测试确认红灯**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Set_pixel_format_writer|FullyQualifiedName~Session_declaration"
```

预期：缺少 public writer、RGB565 和 declaration 模型。

- [ ] **步骤 3：实现最小声明模型**

```csharp
public sealed record RfbSessionDeclaration(
    PixelFormat PixelFormat,
    IReadOnlyList<int> Encodings)
{
    public static RfbSessionDeclaration Default { get; } = new(
        PixelFormat.WinArdBgra32,
        RfbSessionInitializer.DefaultEncodings);
}
```

`InitializeAsync` 接受 declaration（默认保持兼容），并复用公开 `WriteSetPixelFormatAsync` 与现有 `WriteSetEncodingsAsync`。RGB565 固定 little-endian true-color，实际启用仍由后续 Mac 互操作门控制。

- [ ] **步骤 4：验证已有 decoder 支持**

扩展 Raw/ZRLE 测试，用同一 RGB565 fixture 断言 BGRA 输出；不复制 `PixelConverter`。验证 big-endian 16 位现有测试保持通过。

- [ ] **步骤 5：验证并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~RfbSessionInitializerTests|FullyQualifiedName~FramebufferUpdateTests|FullyQualifiedName~ZrleEncodingTests"
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests
git commit -m "feat: configure RFB pixel formats and declarations"
```

## 任务 4：实现公开 RFB Zlib encoding 6

**文件：**

- 创建：`src/WinARD.Remote.Protocol/Encodings/ZlibEncoding.cs`
- 创建：`src/WinARD.Remote.Protocol/Encodings/PersistentZlibInflater.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/ZrleEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/RfbEncodingType.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 修改：`src/WinARD.Remote.Protocol/IO/ProtocolLimits.cs`
- 创建：`tests/WinARD.Remote.Protocol.Tests/Encodings/ZlibEncodingTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Encodings/ZrleEncodingTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Initialization/RfbSessionInitializerTests.cs`

- [ ] **步骤 1：编写基本解码红灯测试**

```csharp
[Fact]
public async Task Zlib_decodes_big_endian_length_and_raw_pixels()
{
    var update = ZlibUpdate(
        width: 2,
        height: 1,
        pixels: Convert.FromHexString("112233FF445566FF"));

    var result = await DecodeWithPersistentSessionAsync(update, PixelFormat.WinArdBgra32);

    Assert.Equal(Convert.FromHexString("112233FF445566FF"), result.FramebufferBytes);
    Assert.Equal((int)RfbEncodingType.Zlib, result.EncodingId);
}
```

- [ ] **步骤 2：运行测试确认类型不存在**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ZlibEncodingTests
```

- [ ] **步骤 3：先提取并验证共用持久 inflater**

用现有 ZRLE 连续矩形、sync-flush、截断、取消、reset 和 dispose 测试刻画当前行为，再把 `ZrleEncoding` 内的 `SegmentedReadStream`、持久 `ZLibStream`、边界校验和 fault/dispose/reset 状态提取到 internal `PersistentZlibInflater`。先运行 `ZrleEncodingTests`，预期提取前后全部通过；新增测试证明两个 decoder 实例绝不共享 dictionary/context。

- [ ] **步骤 4：实现 connection-level Zlib decoder**

`ZlibEncoding` 读取 `uint32_be compressedLength`，使用每连接持久 inflater，将输出严格限制为 `width * height * bytesPerPixel`，再通过 `PixelConverter` 原子写入 framebuffer。不得复用 ZRLE tile parser。

`ProtocolLimits` 增加：

```csharp
public int MaxZlibCompressedBytes { get; init; }
public int MaxZlibDecompressedBytes { get; init; }
```

`FramebufferUpdateReader.CreateSession` 注册 Zlib；one-shot API 在消费压缩长度前拒绝并提示 `CreateSession`。

- [ ] **步骤 5：补齐恶意和状态测试**

先红后绿逐个覆盖：

- 分片长度和分片 payload；
- 连续矩形和跨 update dictionary 复用；
- 16/32 位转换；
- 截断、超长、解压超限、尺寸乘法溢出；
- 少于/多于精确像素长度；
- completed stream 和尾随压缩数据；
- 取消后 context fault，故障 session 不可继续；
- dispose 幂等且后续调用失败；
- framebuffer 只在完整验证后提交。

- [ ] **步骤 6：把 Zlib 放到默认声明首位**

仅在 decoder 测试全部通过后更新默认顺序：

```text
6, 16, 0, 1, -239, -223, ARD metadata...
```

精确 wire 测试必须更新，服务器忽略 Zlib 时仍可选择 ZRLE/Raw。

- [ ] **步骤 7：验证并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~ZlibEncodingTests|FullyQualifiedName~FramebufferUpdateTests|FullyQualifiedName~RfbSessionInitializerTests"
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests
git commit -m "feat: decode persistent RFB Zlib updates"
```

## 任务 5：实现 ARD 服务端缩放 writer 和能力模型

**文件：**

- 修改：`src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs`
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdProtocolConstants.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdClientMessageWriterTests.cs`
- 创建：`src/WinARD.Application/Quality/QualityModels.cs`
- 创建：`tests/WinARD.Application.Tests/Quality/QualityModelsTests.cs`

- [ ] **步骤 1：编写 exact wire 红灯测试**

```csharp
[Theory]
[InlineData(1.0, "08003FF0000000000000")]
[InlineData(0.75, "08003FE8000000000000")]
[InlineData(0.5, "08003FE0000000000000")]
public async Task Scaling_writer_emits_network_order_binary64(double factor, string expectedHex)
{
    await using var stream = new MemoryStream();
    await ArdClientMessageWriter.WriteScalingFactorAsync(stream, factor, CancellationToken.None);
    Assert.Equal(Convert.FromHexString(expectedHex), stream.ToArray());
}
```

另测 NaN、Infinity、`<= 0`、`> 1` 和预取消均在写入前失败。

- [ ] **步骤 2：运行红灯并实现 writer**

使用 `BitConverter.DoubleToInt64Bits` 和 `BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(2), bits)` 生成固定 10 字节报文。不得依赖本机端序或 culture。

- [ ] **步骤 3：编写并实现分级证据能力模型**

```csharp
public enum CapabilitySupport { Unknown, Unsupported, Advertised, Observed }

public sealed record ArdDisplayCapabilities(
    CapabilitySupport Zlib,
    CapabilitySupport Rgb565,
    CapabilitySupport ServerScaling,
    CapabilitySupport AppleThousands,
    CapabilitySupport AppleGrayscale,
    bool SafeOnlinePixelFormatSwitch,
    bool SafeOnlineScaleSwitch,
    int? MaximumRefreshRate);
```

测试保证 Unknown 不会被当作 true，最大刷新率只接受 30..240，1000 黑白不出现在模型中。

- [ ] **步骤 4：验证并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArdClientMessageWriterTests
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~QualityModelsTests
git add src/WinARD.Remote.Protocol src/WinARD.Application tests/WinARD.Remote.Protocol.Tests tests/WinARD.Application.Tests
git commit -m "feat: model and write ARD display quality capabilities"
```

## 任务 6：实现内容状态机和多维自适应控制器

**文件：**

- 修改：`src/WinARD.Application/Quality/QualityModels.cs`
- 创建：`src/WinARD.Application/Quality/AdaptiveQualityController.cs`
- 创建：`tests/WinARD.Application.Tests/Quality/AdaptiveQualityControllerTests.cs`

- [ ] **步骤 1：定义测试使用的不可变模型**

```csharp
public enum QualityContentState { Idle, Interactive, Motion, Recovery }
public enum QualityLevel { Q0, Q1, Q2, Q3, Q4 }
public enum QualityDecisionReason
{
    Initial,
    MotionDetected,
    SustainedOverTarget,
    SevereOverTarget,
    StableRecovery,
    UserConstraint,
    CapabilityLimited,
    TargetUnsatisfied,
}

public sealed record QualityObservation(
    DateTimeOffset Timestamp,
    long AverageBytesPerSecond5s,
    long PeakBytesPerSecond5s,
    double ActualFramesPerSecond,
    TimeSpan ResponseTime,
    TimeSpan DecodeTime,
    TimeSpan PresentationTime,
    double DirtyCoverage,
    TimeSpan SinceLastInput,
    bool PointerDragActive,
    bool ScrollActive,
    int PendingInputCount);
```

控制器只使用以下固定阶梯，不合成表外组合：

| 等级 | 色彩 | 服务端缩放 | Motion 目标帧率 |
|---|---|---:|---:|
| Q0 | 32 位全彩 | 100% | 60 FPS |
| Q1 | 16 位彩色 | 100% | 60 FPS |
| Q2 | 16 位彩色 | 75% | 60 FPS |
| Q3 | 16 位彩色 | 50% | 45 FPS |
| Q4 | 灰度 | 50% | 30 FPS |

- [ ] **步骤 2：先写状态转换红灯**

覆盖：

```csharp
[Fact]
public void Continuous_scroll_enters_motion_and_targets_sixty_fps()
{
    var controller = CreateController(QualityProfile.Automatic, FullCapabilities);
    var decision = controller.Observe(MotionObservation(scrollActive: true));
    Assert.Equal(QualityContentState.Motion, decision.ContentState);
    Assert.Equal(60, decision.TargetFramesPerSecond);
}

[Fact]
public void Six_hundred_stable_milliseconds_enters_recovery_without_jumping_to_q0()
{
    // 先把控制器降到 Q3，再输入稳定样本；只允许恢复到 Q2。
}
```

再测 25% dirty 阈值、2% idle 阈值、普通输入进入 Interactive、无输入内容记录。
同时断言 Idle 的目标上限为 30 FPS，Interactive 只选择 30 或 45 FPS，Motion 按 Q 等级选择 60/45/30 FPS，Recovery 每次最多恢复一个等级。

- [ ] **步骤 3：实现状态机最小逻辑**

状态和时间只由注入的 `TimeProvider`/观测时间驱动，不使用 `DateTime.UtcNow`。测试假时钟不得假设 timestamp frequency 等于 ticks。

- [ ] **步骤 4：先写阶梯和滞回红灯**

逐个覆盖：

- 两个独立 5 秒窗口超过 110% 降一级；
- 150% 或严重响应立即降一级；
- 低于 75% 且稳定 15 秒升一级；
- 降档冷却 2 秒、升档冷却 5 秒；
- 每次只变一个维度；
- 能力裁剪后单调升降；
- 灰度许可关闭时最低 Q3；
- 用户锁定不被覆盖；
- 最低可用等级仍超限返回 `TargetUnsatisfied`；
- Original 永不自动降档；Balanced 不低于 Q2；Smooth 不进入灰度。

- [ ] **步骤 5：实现纯控制器**

控制器不得引用 Desktop、协议流、仓储或 UI 类型。`QualityDecision` 只包含目标值、变化集合和稳定 reason enum，不执行副作用。

- [ ] **步骤 6：验证并提交**

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AdaptiveQualityControllerTests
git add src/WinARD.Application/Quality tests/WinARD.Application.Tests/Quality
git commit -m "feat: add content-aware adaptive quality controller"
```

## 任务 7：扩展 5 秒性能观测

**文件：**

- 修改：`src/WinARD.Desktop/ViewModels/SessionPerformanceTracker.cs`
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/SessionPerformanceTrackerTests.cs`

- [ ] **步骤 1：编写真实窗口红灯测试**

```csharp
[Fact]
public void Tracker_reports_five_second_average_peak_and_dirty_coverage()
{
    var tracker = CreateTrackerWithFakeClock();
    // 每秒写入 1,2,3,4,5 MiB，并提交 10%,20%,30%,40%,50% dirty。
    var observation = tracker.CreateQualityObservation();

    Assert.Equal(3L * MiB, observation.AverageBytesPerSecond5s);
    Assert.Equal(5L * MiB, observation.PeakBytesPerSecond5s);
    Assert.Equal(0.5, observation.DirtyCoverage);
}
```

再测窗口滚出第六秒、重叠 dirty 保守相加后 clamp 到 1、零 framebuffer 尺寸、安全饱和和输入活动只保留聚合类别/时间。

- [ ] **步骤 2：确认当前 1 秒 EMA 不满足测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~SessionPerformanceTrackerTests
```

预期：缺少 5 秒观测 API 或数值不匹配。

- [ ] **步骤 3：实现有界滚动样本**

保留现有 UI snapshot 兼容字段，新增最多 5 个完整一秒 bucket 和 `CreateQualityObservation`。`ObserveFrame` 真正使用 dirty/size 计算 coverage；不得保存 rectangles、像素或输入内容。

- [ ] **步骤 4：验证并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~SessionPerformanceTrackerTests
git add src/WinARD.Desktop/ViewModels/SessionPerformanceTracker.cs tests/WinARD.Desktop.Tests/ViewModels/SessionPerformanceTrackerTests.cs
git commit -m "feat: publish bounded quality observations"
```

## 任务 8：实现原子质量切换事务

**文件：**

- 修改：`src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- 修改：`src/WinARD.Application/Sessions/RemoteSession.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateSession.cs`
- 创建：`src/WinARD.Desktop/ViewModels/QualityTransitionCoordinator.cs`
- 创建：`tests/WinARD.Desktop.Tests/ViewModels/QualityTransitionCoordinatorTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：定义事务契约红灯测试**

```csharp
public sealed record RemoteQualitySettings(
    PixelFormat PixelFormat,
    IReadOnlyList<int> Encodings,
    double ScaleFactor);

public enum QualityTransitionStatus
{
    Applied,
    NoChange,
    ReconnectRequired,
    CapabilityUnavailable,
}
```

`IRemoteSessionRuntime.ApplyQualityTransitionAsync` 接收 settings 和 cancellation，返回稳定 status；调用者不能传 raw message bytes。

- [ ] **步骤 2：编写安全边界失败测试**

覆盖：

- outstanding 请求未完成时 coordinator 不调用 runtime；
- active receive 时不切换；
- Unknown/Unsupported 能力返回限制或重连；
- 未证明在线 PixelFormat/scale 切换时零 wire 写并返回 `ReconnectRequired`；
- 同一 decision generation 只应用一次。

- [ ] **步骤 3：实现 Desktop coordinator**

`RemoteSessionViewModel` 仍拥有“响应已呈现、下一请求未产生”的唯一切换时机。Coordinator 只接收已完成的 decision，不自行轮询网络。

- [ ] **步骤 4：编写 RfbClient 原子事务测试**

成功顺序必须为：

```text
SetPixelFormat → SetEncodings → ARD Scale → swap decoder session → one non-incremental full request
```

若某项未变化则省略对应 writer。三个配置报文和 repair 作为一个 `ClientMessageScheduler` background work item，内部不能被其他 background 请求穿插；输入仍按现有最高优先级规则获得服务。

失败覆盖：部分写入、旧 decoder dispose、新 decoder 创建失败、repair 写失败、取消和缩放后尺寸未知。任何部分写入后的失败都标记连接故障，不回到旧 decoder 继续消费。

- [ ] **步骤 5：实现可交换 session**

预构造使用新 PixelFormat/encodings 的 `FramebufferUpdateSession`；配置报文全部成功后原子交换引用并异步 dispose 旧 session。缩放若没有主动 resize/尺寸时序证据，事务返回 `ReconnectRequired`，不发送报文。

- [ ] **步骤 6：验证并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~QualityTransitionCoordinatorTests|FullyQualifiedName~FramePresentationTests"
git add src/WinARD.Application src/WinARD.Desktop src/WinARD.Remote.Protocol tests/WinARD.Desktop.Tests
git commit -m "feat: apply quality transitions at safe frame boundaries"
```

## 任务 9：集成控制器、会话循环和输入活动

**文件：**

- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 修改：`src/WinARD.Desktop/ViewModels/AutomaticFrameRateController.cs`
- 修改：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写 VM 集成红灯测试**

```csharp
[Fact]
public async Task Quality_decision_is_applied_after_presentation_before_next_request()
{
    // Arrange 一次完整 response/presentation 和一个待应用 decision。
    // Assert 调用顺序：Present -> ApplyQualityTransition -> RequestFramebuffer。
}

[Fact]
public async Task Applied_transition_forces_exactly_one_nonincremental_repair()
{
    // 下一次为 incremental:false，随后恢复 incremental:true。
}
```

另测非帧消息不触发、重复 generation 不触发、关闭期间取消、输入时间戳不包含坐标/键值、用户保存变更立即重建控制器约束。

- [ ] **步骤 2：实现最小集成**

在 `RemoteSessionViewModel` 当前呈现完成和下一请求之间调用 coordinator。Pointer move/barrier、滚轮和键盘入口只更新无内容活动聚合器。`FramebufferRequestPacer` 继续执行 decision 的目标 FPS。

旧 `AutomaticFrameRateController` 不得与新控制器同时调整 FPS：迁移所有调用后删除，或变为只供旧测试/兼容适配且不再被生产路径实例化。

- [ ] **步骤 3：验证输入优先和请求公平性**

扩展 scheduler/VM 测试证明画质事务不会饿死输入，持续输入时仍在既有 32 条公平批次内产生画面请求；画质切换不得绕过 `RemoteInputScheduler`。

- [ ] **步骤 4：验证并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~RemoteSessionViewModelTests|FullyQualifiedName~ClientMessageSchedulerTests|FullyQualifiedName~RemoteInputSchedulerTests"
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "feat: integrate adaptive quality with remote sessions"
```

## 任务 10：实现工具栏、设置保存和无障碍状态

**文件：**

- 创建：`src/WinARD.Desktop/ViewModels/QualityPresentation.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 修改：`src/WinARD.Desktop/Services/ConnectionSessionController.cs`
- 修改：`src/WinARD.Desktop/MainWindow.xaml.cs`
- 创建：`tests/WinARD.Desktop.Tests/ViewModels/QualityPresentationTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/Services/ConnectionSessionControllerTests.cs`

- [ ] **步骤 1：编写 presentation 红灯测试**

```csharp
[Fact]
public void Automatic_summary_contains_active_dimensions_and_evidence()
{
    var text = QualityPresentation.FormatSummary(
        preset: QualityPreset.Automatic,
        color: QualityColor.Color16,
        scale: QualityScale.Percent75,
        targetFps: 45,
        bytesPerSecond: 3.1 * MiB);

    Assert.Equal("自动 · 16 位 · 75% · 45 FPS · 3.1 MiB/s", text);
}
```

覆盖六种状态文字、未知能力、重新连接后生效和无障碍完整名称；颜色不是唯一状态信号。

- [ ] **步骤 2：实现 UI 模型和 XAML**

工具栏增加“画质”摘要按钮和 Flyout。面板包含五预设；1/2/4/8/16 MiB/s、不限制和自定义带宽；自动/32 位/16 位/灰度；自动/100%/75%/50% 缩放；自动/30/45/60/75/90/105/120/不限制刷新率；灰度许可；锁定项；当前编码稳定名称。远端刷新率上限有可靠证据时禁用更高固定档，未知时保留完整列表。为关键控件设置稳定 `AutomationProperties.Name` 和 `AutomationId`。

保留现有连接质量灯和性能栏；不显示 raw encoding ID、主机或本机路径。

- [ ] **步骤 3：推广保存协调模式**

`ConnectionSessionController.UpdateConnectedQualityProfileAsync` 复用现有刷新设置的串行保存、transferred ownership 更新和发布语义。Window 先立即应用当前会话，再异步保存；失败只显示独立非阻塞提示，关闭后不回写 UI。

- [ ] **步骤 4：验证详细行为**

测试：修改详细项切 Custom、预设映射、锁定项、灰度开关、保存失败、重复选择不保存、关闭期间取消、旧刷新选择迁移后显示一致、当前能力禁用但保留已保存值。

- [ ] **步骤 5：验证并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~QualityPresentationTests|FullyQualifiedName~RemoteSessionWindowInputIntegrationTests|FullyQualifiedName~ConnectionSessionControllerTests"
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "feat: add adaptive quality controls and status"
```

## 任务 11：扩展脱敏诊断

**文件：**

- 修改：`src/WinARD.Desktop/Services/DesktopDiagnosticContextFactory.cs`
- 修改：`src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs`
- 修改：`tests/WinARD.Desktop.Tests/Views/RemoteSessionDiagnosticExportStateTests.cs`
- 修改：`tests/WinARD.Infrastructure.Tests/DiagnosticExporterTests.cs`

- [ ] **步骤 1：先写 allowlist 红灯测试**

允许字段固定为稳定数值/枚举：preset、targetBps、qualityLevel、contentState、color、scalePercent、encodingName、target/actualFps、average/peakBps、responseMs、capability flags、reason code、targetSatisfied。

```csharp
Assert.Equal("Motion", quality.GetProperty("contentState").GetString());
Assert.Equal("SevereOverTarget", quality.GetProperty("reason").GetString());
Assert.False(quality.GetProperty("targetSatisfied").GetBoolean());
```

- [ ] **步骤 2：编写隐私负测**

导出 zip 全文不得包含：host、username、password、鼠标坐标、按键、文本、clipboard、pixel、payload、ciphertext、key、IV、sequence、绝对路径。reason 只接受 enum 映射，不透传任意字符串。

- [ ] **步骤 3：实现 factory 和严格 allowlist**

为 Zlib 6 增加稳定名称，同时更新 tracker、VM formatting 和 exporter 的 encoding allowlist。未知编码使用现有安全分类，不输出未经允许的名称。

- [ ] **步骤 4：验证并提交**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RemoteSessionDiagnosticExportStateTests
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DiagnosticExporterTests
git add src/WinARD.Desktop src/WinARD.Infrastructure tests/WinARD.Desktop.Tests tests/WinARD.Infrastructure.Tests
git commit -m "feat: export safe adaptive quality diagnostics"
```

## 任务 12：建立 Apple 1002/1001 直接证据门

**文件：**

- 修改：`tools/WinARD.ProtocolProbe/ProbeCommandLine.cs`
- 修改：`tools/WinARD.ProtocolProbe/Program.cs`
- 修改：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCaptureRunner.cs`
- 修改：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixReader.cs`
- 修改：`tools/WinARD.ProtocolProbe/EncodingResearch/EncodingPrefixCaptureFile.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/ProtocolProbeTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Tools/EncodingPrefixCaptureTests.cs`
- 修改：`docs/protocol/ard-zero-install-image-quality.md`

- [ ] **步骤 1：编写 CLI 红灯测试**

命令固定为：

```text
--capture-known-encoding-prefix 1002 artifacts/protocol-research/mac/apple-thousands --confirm-synthetic-screen
--capture-known-encoding-prefix 1001 artifacts/protocol-research/mac/apple-gray --confirm-synthetic-screen
```

测试拒绝其他 ID、重复确认、空路径和附加参数；确认标志缺失时在 DNS/TCP/文件前失败。

- [ ] **步骤 2：实现 allowlist 和声明顺序**

只允许 1002/1001，连接真实 Mac 后声明：

```csharp
new[] { candidateId, 6, 16, 0 }
```

不再要求 RDM baseline/adaptive 文件。候选未命中但服务器返回 fallback 时，报告稳定类别和 observed encoding，不把 fallback 当候选成功。

- [ ] **步骤 3：扩展多样本前缀研究但不冒充 decoder 证据**

每个候选至少在合成纯色、渐变、文字图和多个矩形尺寸下产生独立目录；保持 payload 有界、哈希、不可覆盖和普通诊断隔离。现有 prefix reader 只证明外层前缀时，文档明确门关闭。

- [ ] **步骤 4：运行实机硬门**

执行前用户目视确认：单显示器、无敏感合成图、通知和私人文件名关闭。验证 manifest 哈希、不含 host/user/password/path，研究目录被 Git 忽略。

- [ ] **步骤 5：写唯一候选结论**

1002 和 1001 分别给出 `打开` 或 `关闭`。打开必须具备完整 payload 边界、像素语义、连续状态、恶意输入测试和 macOS 26.5 一致性；只有 prefix 时必须关闭。若任一候选打开，为它另写独立 decoder 设计/计划，不在本任务猜实现。

- [ ] **步骤 6：验证并提交**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~EncodingPrefixCaptureTests|FullyQualifiedName~ProtocolProbeTests"
git ls-files artifacts/protocol-research
git add tools/WinARD.ProtocolProbe tests/WinARD.Remote.Protocol.Tests docs/protocol/ard-zero-install-image-quality.md
git commit -m "feat: probe Apple quality encodings directly"
```

预期：`git ls-files` 无输出。

## 任务 13：全量验证、实机手册和便携版

**文件：**

- 创建：`docs/testing/zero-install-adaptive-quality-macos.md`
- 修改：验证发现的目标文件
- 发布：`artifacts/WinARD-portable-win-x64.zip`

- [ ] **步骤 1：编写实机矩阵**

手册固定记录：macOS 26.5、单显示器、同一分辨率、同一网络、相同办公操作脚本；分别测试 Automatic、Original、Balanced、Smooth、Custom，1/2/4/8 MiB/s 和 Automatic/30/45/60/120/Unlimited。

每组记录：实际 encoding、平均/峰值 MiB/s、实际 FPS、response、输入写延迟、内容状态、Q 等级变化、是否目标满足。不得记录画面或输入内容。

- [ ] **步骤 2：运行格式与占位符检查**

```powershell
git diff --check
$forbidden = @(('TO'+'DO'), ('T'+'BD'), ('待'+'定'), ('PLACE'+'HOLDER'), ('填'+'入'))
Select-String -Path `
  'docs/protocol/ard-zero-install-image-quality.md', `
  'docs/testing/zero-install-adaptive-quality-macos.md' `
  -Pattern $forbidden
```

- [ ] **步骤 3：运行分层测试**

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release -p:Platform=x64
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release -p:Platform=x64
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release -p:Platform=x64
```

- [ ] **步骤 4：运行全量测试和 Release 构建**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
```

预期：0 失败、0 warning、0 error，通过数不少于开始本计划时的基线。

- [ ] **步骤 5：编译便携版并做结构检查**

```powershell
.\packaging\portable.ps1 -Version 0.1.0.0
.\packaging\verify-artifacts.ps1 -ExpectedVersion 0.1.0.0

$zip = 'artifacts\WinARD-portable-win-x64.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $zip))
try {
    $names = @($archive.Entries.FullName)
    if ('WinARD.Desktop.exe' -notin $names) { throw 'WinARD.Desktop.exe is missing.' }
    if ($names | Where-Object { $_ -match '(?i)RDM|protocol-research|credential|password' }) {
        throw 'Portable archive contains forbidden research or secret-like entries.'
    }
} finally {
    $archive.Dispose()
}
```

预期：两个脚本成功，版本/架构/关键 payload/哈希验证通过，ZIP 含主 EXE，且不含 RDM DLL、协议研究 payload 或凭据类文件名。主 EXE 启动和诊断导出在下一步人工验收。

- [ ] **步骤 6：执行 WinUI 和 Mac 手工验收**

确认：运动阶段不再长期 3–4 FPS；网络允许时接近 60，受限时稳定 45/30；不进入黑白；停止后逐级恢复；输入队列有界；每个自动变化可解释。未执行项明确标为未验证，不得描述为通过。

- [ ] **步骤 7：提交手册和最终验证修复**

```powershell
git add docs/testing/zero-install-adaptive-quality-macos.md
git commit -m "docs: add adaptive quality acceptance runbook"
git status --short --branch
```

最终工作区应干净；研究 artifacts 必须 ignored 且 unstaged。

## 完成标准

- 标准 Zlib/ZRLE/Raw、16/32 位 PixelFormat 和能力模型有完整边界测试。
- 控制器能在 Idle/Interactive/Motion/Recovery 间稳定切换，并遵守 Q0–Q4、灰度许可和用户锁定。
- 在线变化只在安全边界原子应用；未证明能力返回重新连接或关闭，不猜测。
- UI、设备级持久化、状态和诊断完整且脱敏。
- Apple 1002/1001 分别有唯一证据门结论；关闭不阻塞标准方案。
- 全量测试、Release x64 构建和便携版结构验证通过。
- macOS 26.5 未执行的手工项不会被误报为完成。
