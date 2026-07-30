# RFB 握手失败诊断实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 为 RFB 协商失败增加不含原始网络内容的握手阶段与字节计数诊断。

**架构：** 精确读取层产生安全计数，握手层补充协议阶段，现有异常上下文合并负责跨层保留字段，桌面诊断层显式导出允许字段。协议选择和连接行为保持不变。

**技术栈：** C# 12、.NET 8、xUnit、WinUI 3。

---

### 任务 1：握手失败上下文模型与协议测试

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Errors/RfbProtocolFailureInfo.cs`
- 修改：`src/WinARD.Remote.Protocol/IO/RfbReader.cs`
- 修改：`src/WinARD.Remote.Protocol/Handshake/RfbHandshake.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Handshake/RfbHandshakeTests.cs`
- 测试：`tests/WinARD.Remote.Protocol.Tests/Errors/RfbProtocolFailureInfoTests.cs`

- [ ] **步骤 1：编写失败测试**

为短 Banner、畸形 Banner、缺失安全类型数量和截断安全类型列表断言 `HandshakeStage`、`ExpectedByteCount`、`ActualByteCount`；为 `FillMissingFrom` 断言内层计数与外层阶段合并。

- [ ] **步骤 2：验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RfbHandshakeTests|FullyQualifiedName~RfbProtocolFailureInfoTests"
```

预期：测试因新枚举或字段不存在而失败。

- [ ] **步骤 3：实现最少协议代码**

增加：

```csharp
public enum RfbHandshakeStage
{
    VersionBanner,
    VersionParse,
    SecurityType33,
    SecurityTypeCount,
    SecurityTypes,
}
```

在精确读取提前结束时记录安全计数，并在握手边界通过 `WithContext` 附加阶段。完整畸形 Banner 转换为带 `VersionParse` 上下文的 `RfbProtocolException`。

- [ ] **步骤 4：验证绿灯**

重新运行任务 1 的定向命令，预期全部通过且无警告。

### 任务 2：桌面安全诊断导出

**文件：**
- 修改：`src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`
- 测试：`tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`

- [ ] **步骤 1：编写失败测试**

构造含秘密标记的异常消息，但只断言导出 `ProtocolFailureKind`、`RfbHandshakeStage`、`ExpectedByteCount`、`ActualByteCount`，并断言所有字段不含秘密标记或原始 Banner 字段名。

- [ ] **步骤 2：验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Handshake"
```

预期：新安全字段尚未导出，测试失败。

- [ ] **步骤 3：实现最少导出代码**

在 `GetProtocolFailureFields` 中按固定顺序导出三个新字段，并使用 invariant 整数格式；不导出异常消息或网络字节。

- [ ] **步骤 4：验证绿灯**

重新运行任务 2 的定向命令，预期全部通过。

### 任务 3：审查、验证和发布

**文件：**
- 验证：解决方案全部项目
- 生成：`F:/Documents/Windows ARD Client/artifacts/WinARD-<commit>-handshake-diagnostics-win-x64.zip`

- [ ] **步骤 1：检查差异和安全字段**

运行 `git diff --check`，确认没有任何原始 Banner、安全类型内容或异常原文进入诊断字段。

- [ ] **步骤 2：严格构建和全量测试**

```powershell
dotnet build WinARD.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test WinARD.sln -c Release --no-build
```

预期：0 warning、0 error，全部测试通过。

- [ ] **步骤 3：提交并发布**

提交诊断实现，发布 self-contained `win-x64`，压缩 ZIP，验证包含 `WinARD.Desktop.exe` 并输出 SHA-256。
