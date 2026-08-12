# 首帧前低带宽协商与单次安全回退实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 在 Mac 零安装条件下，于首个 framebuffer 请求前应用 RGB565 与 ZRLE-first 协商；优选首帧不兼容时用全新连接仅回退一次，并让 UI、诊断和带宽统计准确反映实际线路状态。

**架构：** Domain/Application 生成不可变启动计划，RFB client 原子执行一次启动配置，连接处理器验证并预加载首帧，连接尝试工作流拥有唯一的优选→安全回退循环。协议解码器只产生有界数值统计，上层把用户期望与连接实际状态分离；会话开始后继续禁止在线 PixelFormat 切换。

**技术栈：** C# 12、.NET 8、WinUI 3、xUnit、RFB/ARD 003.889、System.IO.Compression、现有安全诊断与便携打包脚本。

---

## 文件结构

- 创建 `src/WinARD.Application/Quality/QualityBootstrapModels.cs`：启动尝试、实际画质、回退原因与启动计划闭集模型。
- 创建 `src/WinARD.Application/Quality/QualityBootstrapPlanner.cs`：纯函数地把 `QualityProfile` 映射为优选/安全启动计划。
- 修改 `src/WinARD.Application/Ports/IRfbClientFactory.cs`：增加兼容默认实现的启动配置、实际状态和首帧验证契约。
- 修改 `src/WinARD.Application/Ports/RemoteSessionContracts.cs`：扩展更新统计和运行时实际画质，同时保留旧构造 API。
- 修改 `src/WinARD.Remote.Protocol/Encodings/EncodingDecodeResult.cs`：携带单矩形安全数值统计。
- 修改 `src/WinARD.Remote.Protocol/Encodings/{Raw,Zlib,Zrle}Encoding.cs`：产生 wire bytes、pixel bytes 和 pixel area。
- 修改 `src/WinARD.Remote.Protocol/Framebuffer/{FramebufferUpdateResult,FramebufferUpdateReader}.cs`：聚合矩形传输统计。
- 修改 `src/WinARD.Desktop/Services/RfbClientFactory.cs`：执行一次首帧前配置，切换初始 decoder PixelFormat，预取首批消息并暴露实际状态。
- 修改 `src/WinARD.Application/Sessions/{ConnectDeviceHandler,RemoteSession}.cs`：传入启动计划、验证首帧并按序交付预加载消息。
- 修改 `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`：存在预加载首帧时跳过重复的初始 full request。
- 修改 `src/WinARD.Desktop/Services/ConnectionAttemptWorkflow.cs`：仅对闭集启动兼容失败建立全新连接回退一次。
- 修改 `src/WinARD.Desktop/ViewModels/{QualityPresentation,RemoteSessionViewModel,SessionPerformanceTracker}.cs`：实际/期望画质分离和统计窗口。
- 修改 `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml` 与 `.xaml.cs`：展示期望、实际和安全回退状态。
- 修改 `src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs` 与 `src/WinARD.Desktop/Services/DesktopDiagnosticContextFactory.cs`：新增双层 allowlist 的聚合字段。
- 修改对应 Application、Protocol、Desktop、Infrastructure 测试：覆盖 TDD RED/GREEN、公开 API 兼容、隐私和真实 wire 顺序。

## 任务 1：建立启动画质闭集模型与确定性计划器

**文件：**
- 创建：`src/WinARD.Application/Quality/QualityBootstrapModels.cs`
- 创建：`src/WinARD.Application/Quality/QualityBootstrapPlanner.cs`
- 测试：`tests/WinARD.Application.Tests/Quality/QualityBootstrapPlannerTests.cs`

- [ ] **步骤 1：编写计划器失败测试**

测试必须逐一断言：Original/锁定 Full32 得到 BGRA32；Balanced、Smooth、显式 Color16 和 Automatic 得到 RGB565；无 Apple 灰度能力的 Grayscale 得到 RGB565 且原因 `CapabilityLimited`；所有安全回退为 BGRA32。精确断言编码数组：

```csharp
Assert.Equal([16, 6, 0, 1, -239, -223], plan.Preferred.Encodings);
Assert.Equal([6, 16, 0, 1, -239, -223], plan.Fallback.Encodings);
Assert.Equal(RemotePixelFormatKind.Rgb565, plan.Preferred.PixelFormat);
Assert.Equal(RemotePixelFormatKind.Bgra32, plan.Fallback.PixelFormat);
```

- [ ] **步骤 2：运行 RED**

运行：

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~QualityBootstrapPlannerTests
```

预期：FAIL，`QualityBootstrapPlanner`、`QualityBootstrapPlan`、`QualityBootstrapAttempt` 或相关 enum 不存在。

- [ ] **步骤 3：实现最小闭集模型与纯计划器**

模型使用 enum，不允许任意 reason 字符串：

```csharp
public enum QualityBootstrapAttempt { Preferred, Fallback }
public enum QualityBootstrapReason { UserFull32, UserColor16, AutomaticBandwidth, CapabilityLimited, SafeFallback }
public sealed record QualityBootstrapSettings(
    RemotePixelFormatKind PixelFormat,
    IReadOnlyList<int> Encodings,
    QualityBootstrapReason Reason);
public sealed record QualityBootstrapPlan(
    QualityBootstrapSettings Preferred,
    QualityBootstrapSettings Fallback);
```

构造器复制编码数组为只读快照并验证只含 `[16,6,0,1,-239,-223]` 的无重复子集，计划器不读取实时 FPS 或主机信息。

- [ ] **步骤 4：运行 GREEN 与 Application 全量**

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release --no-restore
```

预期：全部通过。

- [ ] **步骤 5：提交**

```powershell
git add src/WinARD.Application/Quality tests/WinARD.Application.Tests/Quality/QualityBootstrapPlannerTests.cs
git commit -m "feat: plan preframe quality bootstrap"
```

## 任务 2：增加有界矩形传输统计且保持公开 API 兼容

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Encodings/EncodingDecodeResult.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/RawEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/ZlibEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/ZrleEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateResult.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 修改：`src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Framebuffer/FramebufferUpdateTests.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Encodings/{Raw,Zlib,Zrle}EncodingTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：编写单矩形与聚合统计失败测试**

Raw 断言 `WirePayloadBytes == width * height * bytesPerPixel`；Zlib/ZRLE 断言 wire bytes 包含 4 字节 compressed length 与压缩 payload，pixel wire bytes 使用实际 PixelFormat；聚合断言饱和相加、零像素不除零、编码 count/bytes 分离。公开 API 测试必须以反射锁定旧构造器：

```csharp
Assert.NotNull(typeof(RemoteUpdateStatistics).GetConstructor(
    [typeof(long), typeof(IReadOnlyDictionary<int, int>)]));
Assert.NotNull(typeof(FramebufferUpdateResult).GetConstructor(
    [typeof(IEnumerable<FramebufferRect>), typeof(IEnumerable<FramebufferRect>),
     typeof(RemoteCursor), typeof(bool), typeof(IReadOnlyDictionary<int, int>)]));
```

- [ ] **步骤 2：运行 RED**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~EncodingTests|FullyQualifiedName~FramebufferUpdateTests"
```

预期：FAIL，新统计属性/类型不存在；旧 API 测试在生产实现前保持通过。

- [ ] **步骤 3：实现解码结果与 update 聚合**

新增不可变数值结构：

```csharp
public readonly record struct RectangleTransferStatistics(
    int EncodingId,
    long WirePayloadBytes,
    long PixelWireBytes,
    long PixelArea,
    bool HasPixelContent);
```

`EncodingDecodeResult` 可选携带一条统计；`FramebufferUpdateReader` 汇总为 `FramebufferTransferStatistics`。所有乘法使用 `checked`，跨矩形求和使用饱和相加；不记录 x/y、payload 或 pixel。

- [ ] **步骤 4：兼容扩展 `RemoteUpdateStatistics`**

保留旧二参数 positional 构造器和二元 `Deconstruct`，新增非 positional 属性 `Transfer` 或显式新构造器；不得直接给 public positional record 追加参数。为新集合复制只读快照。

- [ ] **步骤 5：运行 GREEN 与 Protocol/Desktop 定向**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~FramePresentationTests
```

预期：全部通过。

- [ ] **步骤 6：提交**

```powershell
git add src/WinARD.Remote.Protocol src/WinARD.Application/Ports/RemoteSessionContracts.cs tests/WinARD.Remote.Protocol.Tests tests/WinARD.Desktop.Tests/FramePresentationTests.cs
git commit -m "feat: report bounded framebuffer transfer statistics"
```

## 任务 3：在真实 RFB client 中原子执行一次首帧前配置

**文件：**
- 修改：`src/WinARD.Application/Ports/IRfbClientFactory.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 测试：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：编写真实 wire 顺序失败测试**

使用现有 `ScriptedDuplexStream` 完成 RFB 3.8 或 003.889 初始化，在任何 request 前调用新契约：

```csharp
await client.ConfigureBootstrapAsync(preferred, default);
await client.RequestFramebufferUpdateAsync(false, default);
Assert.Equal(
    [.. ExpectedSetPixelFormat(PixelFormat.Rgb565),
     .. ExpectedSetEncodings([16, 6, 0, 1, -239, -223]),
     .. ExpectedFramebufferRequest(incremental: false, 2, 1)],
    stream.WrittenBytes[initializationOffset..]);
```

另测第二次配置拒绝、首个 request 后配置拒绝且 wire 零变化、配置写入中取消使 client fault、BGRA32 fallback 顺序、`ActualQuality` 精确反映 attempt/pixel format/requested encodings。

- [ ] **步骤 2：运行 RED**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FramePresentationTests&Name~Bootstrap"
```

预期：FAIL，`ConfigureBootstrapAsync` 和实际画质模型不存在。

- [ ] **步骤 3：实现兼容契约与 RfbClient 启动事务**

`IRfbClient` 新成员必须有默认实现以免破坏测试 fake 和外部实现：

```csharp
QualityBootstrapState BootstrapState => QualityBootstrapState.LegacyBgra32;
ValueTask ConfigureBootstrapAsync(QualityBootstrapSettings settings, QualityBootstrapAttempt attempt,
    CancellationToken cancellationToken) => ValueTask.FromResult();
```

真实 `RfbClient` 在初始化后创建目标 PixelFormat decoder session，scheduler 中写 `SetPixelFormat` 后写 `SetEncodings`，只在两者成功后发布状态。任何 wire 尝试后的失败使用现有 fault/cleanup 语义；不得复用在线 transition 路径发送 repair request。

- [ ] **步骤 4：保持在线安全门测试**

现有 `Production_coordinator_rejects_q1_without_writing_set_pixel_format` 必须继续断言会话开始后的 Q0↔Q1 返回 `ReconnectRequired` 且输出长度不变。

- [ ] **步骤 5：运行 GREEN 与 Desktop 全量**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore
```

预期：全部通过。

- [ ] **步骤 6：提交**

```powershell
git add src/WinARD.Application/Ports/IRfbClientFactory.cs src/WinARD.Desktop/Services/RfbClientFactory.cs tests/WinARD.Desktop.Tests/FramePresentationTests.cs
git commit -m "feat: configure quality before the first frame"
```

## 任务 4：验证并预加载首帧，保证消息与资源所有权

**文件：**
- 修改：`src/WinARD.Application/Sessions/ConnectDeviceHandler.cs`
- 修改：`src/WinARD.Application/Sessions/RemoteSession.cs`
- 修改：`src/WinARD.Application/Ports/IRfbClientFactory.cs`
- 修改：`src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 测试：`tests/WinARD.Application.Tests/ConnectDeviceHandlerTests.cs`
- 测试：`tests/WinARD.Application.Tests/RemoteSessionRuntimeTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写预加载和失败分类 RED**

测试顺序为：Initialize → ConfigureBootstrap → non-incremental request → Receive cursor/metadata/frame；`HandleAsync` 返回后，`RemoteSession.ReceiveAsync` 必须按序返回预加载消息且不再次调用 client receive。ViewModel 发现 `HasPreloadedFramebuffer=true` 时不得再发送启动 full request，呈现预加载帧后只发送下一个 incremental request。dispose 前未消费的 framebuffer/cursor owner 必须只释放一次。

定义闭集异常包装：

```csharp
public enum QualityBootstrapFailureReason {
    DecoderFailure, UnsupportedEncoding, MalformedFramebufferUpdate, RemoteSessionClosed
}
public sealed class QualityBootstrapCompatibilityException : Exception {
    public QualityBootstrapFailureReason Reason { get; }
}
```

认证、取消、一般 timeout 不得被包装。

- [ ] **步骤 2：运行 RED**

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ConnectDeviceHandlerTests|FullyQualifiedName~RemoteSessionRuntimeTests"
```

预期：FAIL，handler 不接受 bootstrap plan、不预取首帧、session 无预加载队列。

- [ ] **步骤 3：实现有界预加载队列**

`RemoteSession` 构造器接收 `IReadOnlyList<RemoteServerMessage>` 的所有权快照；`ReceiveAsync` 在 `_receiveGate` 内先 dequeue。只允许 cursor 和首个 framebuffer，ARD metadata 继续由 client 内部消费而不进入公共消息；队列上限为 8 条且最多 1 个 framebuffer。异常或取消用 `DisposeMessage` 释放所有 `IDisposable`。`IRemoteSessionRuntime` 增加默认 `HasPreloadedFramebuffer=false`，真实 session 在首帧尚未 dequeue 时返回 true；ViewModel 仅在 false 时发送现有启动 full request。

- [ ] **步骤 4：实现 handler 首帧验证**

handler 从 profile 创建 `QualityBootstrapPlan`，使用调用方指定 attempt 配置 client，发一次 full request并读到首个 framebuffer。只把规格列出的 `RfbProtocolFailureKind` 包装为 compatibility exception；保留结构化 failure reason，不保留任意异常消息字段。

- [ ] **步骤 5：运行 GREEN 与资源测试**

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RemoteSessionViewModelTests&Name~Preloaded"
```

预期：全部通过，owner dispose 计数均为 1。

- [ ] **步骤 6：提交**

```powershell
git add src/WinARD.Application/Sessions src/WinARD.Application/Ports src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs tests/WinARD.Application.Tests tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs
git commit -m "feat: validate and preload the bootstrap frame"
```

## 任务 5：在连接工作流中建立全新连接的单次安全回退

**文件：**
- 修改：`src/WinARD.Desktop/Services/ConnectionAttemptWorkflow.cs`
- 修改：`src/WinARD.Application/Sessions/ConnectDeviceHandler.cs`
- 测试：`tests/WinARD.Desktop.Tests/Services/ConnectionSessionControllerTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/Services/ConnectionFailureDiagnosticsTests.cs`

- [ ] **步骤 1：编写回退矩阵 RED**

以顺序记录器断言 compatibility exception 时恰好两次 transport/client 创建：第一次 Preferred/RGB565，资源完全 dispose 后第二次 Fallback/BGRA32。第二次成功返回 `BootstrapFallbackUsed=true`。第二次失败不产生第三次调用。

理论测试覆盖：DNS、TCP、SSH、host key、认证、用户取消、一般 timeout 均只有一次尝试；既有 host-key 单次确认重试与 bootstrap 回退组合总数有明确上限，不能形成嵌套无限循环。

- [ ] **步骤 2：运行 RED**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ConnectionSessionControllerTests|FullyQualifiedName~ConnectionFailureDiagnosticsTests"
```

预期：FAIL，工作流没有 bootstrap attempt 状态或回退逻辑。

- [ ] **步骤 3：实现唯一外层回退循环**

`ConnectionAttemptWorkflow` 的 bootstrap attempt 最多两次；host-key 确认仍由既有逻辑处理，但接受后重新开始当前 bootstrap attempt，不增加 bootstrap 预算。优选 compatibility 失败写安全事件：

```text
code=QUALITY_BOOTSTRAP_FALLBACK
BootstrapFallbackReason=<closed enum>
BootstrapAttempt=Preferred
```

第二次失败使用现有 error mapping，并附 `FallbackFailed=True`；不输出异常 message、host、pixel 或 payload。

- [ ] **步骤 4：运行 GREEN 与 Desktop 服务测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Services
```

预期：全部通过。

- [ ] **步骤 5：提交**

```powershell
git add src/WinARD.Desktop/Services/ConnectionAttemptWorkflow.cs src/WinARD.Application/Sessions/ConnectDeviceHandler.cs tests/WinARD.Desktop.Tests/Services
git commit -m "feat: retry bootstrap once with safe quality"
```

## 任务 6：把期望画质与实际已应用画质分离

**文件：**
- 修改：`src/WinARD.Application/Ports/RemoteSessionContracts.cs`
- 修改：`src/WinARD.Application/Sessions/RemoteSession.cs`
- 修改：`src/WinARD.Desktop/ViewModels/QualityPresentation.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/QualityPresentationTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/AdaptiveQualitySessionIntegrationTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/Views/RemoteSessionQualityPanelTests.cs`

- [ ] **步骤 1：编写 presentation RED**

新增测试断言：期望 Color16/实际 Rgb565 显示“实际：16 位”；期望 Color16/实际 Bgra32 fallback 显示“实际：32 位（已安全回退）”；Unlimited 状态显示“不限制”，不显示“已达到低带宽目标”；会话内改变色深提示“下次连接生效”。

`QualityPresentationSnapshot` 是 public positional record，不能直接追加 positional 参数；以显式兼容构造器或新的嵌套实际状态属性扩展，并用 reflection/source deconstruct 测试锁定旧 API。

- [ ] **步骤 2：运行 RED**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~QualityPresentationTests|FullyQualifiedName~RemoteSessionQualityPanelTests|FullyQualifiedName~AdaptiveQualitySessionIntegrationTests"
```

预期：FAIL，没有实际启动画质字段和期望/实际文案。

- [ ] **步骤 3：贯通实际状态快照**

`IRemoteSessionRuntime` 默认暴露 `BootstrapState`；`RemoteSession` 转发 client 状态；ViewModel 构建原子 snapshot 时同时快照 decision 与 bootstrap state。实际 color 从 applied PixelFormat 得到，encoding 从观测统计得到，fallback 状态来自连接启动状态，不从异常文本推断。

- [ ] **步骤 4：更新 XAML 和无障碍名称**

在画质 flyout 添加稳定 ID：`RemoteQualityDesiredText`、`RemoteQualityAppliedText`。摘要按钮以实际 color/encoding 为主；保存选项仍显示用户期望。AutomationProperties.Name 同时包含期望、实际、回退状态。

- [ ] **步骤 5：运行 GREEN 与 Desktop 全量**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore
```

预期：全部通过。

- [ ] **步骤 6：提交**

```powershell
git add src/WinARD.Application/Ports src/WinARD.Application/Sessions/RemoteSession.cs src/WinARD.Desktop/ViewModels src/WinARD.Desktop/Views tests/WinARD.Desktop.Tests
git commit -m "feat: show desired and applied remote quality"
```

## 任务 7：聚合带宽效率并扩展严格脱敏诊断

**文件：**
- 修改：`src/WinARD.Desktop/ViewModels/SessionPerformanceTracker.cs`
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 修改：`src/WinARD.Desktop/Services/DesktopDiagnosticContextFactory.cs`
- 修改：`src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/SessionPerformanceTrackerTests.cs`
- 测试：`tests/WinARD.Desktop.Tests/Views/RemoteSessionDiagnosticExportStateTests.cs`
- 测试：`tests/WinARD.Infrastructure.Tests/DiagnosticExporterTests.cs`

- [ ] **步骤 1：编写统计与隐私 RED**

测试用两帧已知 wire bytes/pixel area/dirty coverage，断言窗口聚合：`RectangleCount`、`PixelArea`、`WirePayloadBytes`、`BytesPerPixelMilli`、`DirtyCoveragePermille` 和每编码 bytes。覆盖零面积、省略 ratio、long 溢出饱和、未知 encoding 合并 Other。

Exporter 测试同时加入合法字段和恶意任意值；生成 ZIP 后 `ReadAllAsync` 扫描所有 entry，断言 host、username、coordinates、payload marker、pixel marker、exception marker 均不存在。

- [ ] **步骤 2：运行 RED**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~SessionPerformanceTrackerTests|FullyQualifiedName~RemoteSessionDiagnosticExportStateTests"
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DiagnosticExporterTests
```

预期：FAIL，新聚合和 allowlist 字段不存在。

- [ ] **步骤 3：实现窗口聚合与诊断模型**

只保存按秒 bucket 和累计数值，不保存 rectangle 列表。`BytesPerPixelMilli = round(1000 * wirePayloadBytes / pixelArea)`，用 decimal/checked 或先约分避免乘法溢出；`DirtyCoveragePermille` clamp 到 0..1000。

`DiagnosticQualitySummary` 是 public positional record；不得直接替换旧 21 参数构造器/Deconstruct。新增属性采用兼容显式构造器或独立 `DiagnosticTransferSummary`。

- [ ] **步骤 4：实现双层 allowlist**

字段名门只接受规格中的新字段；枚举值门只接受 `Preferred/Fallback`、`Bgra32/Rgb565` 和闭集 fallback reason；数值门分别限制非负 int64、0..1000 permille。未知/任意字符串返回 null。

- [ ] **步骤 5：运行 GREEN**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release --no-restore
```

预期：全部通过。

- [ ] **步骤 6：提交**

```powershell
git add src/WinARD.Desktop/ViewModels src/WinARD.Desktop/Services/DesktopDiagnosticContextFactory.cs src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs tests/WinARD.Desktop.Tests tests/WinARD.Infrastructure.Tests
git commit -m "feat: diagnose framebuffer bandwidth efficiency"
```

## 任务 8：完整审查、验证和发布

**文件：**
- 检查：规格中列出的全部生产与测试文件
- 生成：`artifacts/WinARD-portable-win-x64.zip`
- 生成：`artifacts/WinARD.msix`
- 生成：`artifacts/SHA256SUMS.txt`

- [ ] **步骤 1：逐项规格审查**

独立规格审查代理核对：首帧前 RGB565、ZRLE-first、新连接单次 fallback、非兼容错误不 retry、预加载所有权、在线 gate 保持关闭、期望/实际 UI、聚合诊断和隐私。Critical/Important 必须为 0；发现问题由对应实现代理修复并重新审查。

- [ ] **步骤 2：代码质量审查**

独立质量审查代理重点检查：public API 构造/Deconstruct 兼容、transport/client/secret/owner 释放、取消身份、scheduler fault、persistent inflater 不跨连接复用、整数溢出、诊断 allowlist 和测试是否真实走 wire。Critical/Important 必须为 0。

- [ ] **步骤 3：运行新鲜全仓验证**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet format WinARD.sln --verify-no-changes --no-restore
git diff --check
```

预期：测试 0 失败；构建 0 warning/0 error；格式和 diff check exit 0。

- [ ] **步骤 4：重新打包与校验**

```powershell
.\packaging\portable.ps1 -Version 0.1.0.0
.\packaging\verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
```

预期：Artifact verification passed。

- [ ] **步骤 5：扫描发布包**

打开 ZIP 清单并断言：无 `ProtocolProbe`、RDM、protocol-research、pcap/capture、credential/password/secret 命名文件和嵌套归档；必需载荷由 `verify-artifacts.ps1` 证明。计算 SHA-256 并与 `SHA256SUMS.txt` 一致。

- [ ] **步骤 6：最终提交与实机交接**

```powershell
git status --short
git log -10 --oneline
```

工作树必须干净。交付 ZIP 路径、SHA-256、自动化证据和 macOS 26.5 复测清单；明确“带宽降低”只有实机同脚本对照后才能宣布通过。
