# 持久压缩流跨 PixelFormat 切换实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 在线 Q0↔Q1 切换时保留连接级 Zlib/ZRLE inflater，并继续维持原子 repair 与 fail-closed 事务。

**架构：** 复用现有 `FramebufferUpdateSession`，在其 gate 内原子更新依赖 PixelFormat 的 decoder；Zlib/ZRLE inflater 不更换。`RfbClient` 在同一 scheduler work item 中完成 wire 配置、本地格式提交和一次 full repair。

**技术栈：** .NET 8、C# 12、RFB 3.8/Apple 3.889、System.IO.Compression、xUnit。

---

### 任务 1：为 session 增加持久 inflater 的格式重配置

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Encodings/ZlibEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/ZrleEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/RawEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Encodings/CursorEncoding.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateSession.cs`
- 修改：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Encodings/ZlibEncodingTests.cs`
- 修改：`tests/WinARD.Remote.Protocol.Tests/Encodings/ZrleEncodingTests.cs`

- [ ] **步骤 1：编写 Zlib 跨格式持续流失败测试**

测试使用一个 `ZLibStream` 依次写入 BGRA32 与 RGB565 raw bytes，每次 `Flush()` 后切分 chunk。使用同一 `FramebufferUpdateSession`：第一段按 BGRA32 解码；调用新的 session 重配置 API；第二段按 RGB565 解码并断言最终 BGRA 像素。

- [ ] **步骤 2：运行 Zlib 定向测试并确认 RED**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~ZlibEncodingTests"
```

预期：因 session 没有重配置 API，或第二段由新 inflater 解码失败。

- [ ] **步骤 3：编写 ZRLE 跨格式持续流失败测试**

同一 zlib stream 的第一段包含 BGRA32 raw tile，第二段包含 RGB565 raw tile；重配置后第二段必须正确展开。

- [ ] **步骤 4：实现最小可重配置 decoder 契约**

新增内部契约，分别提供验证与提交方法。`ZlibEncoding` 和 `ZrleEncoding` 的 inflater 字段不变，只更新 PixelFormat；Raw/Cursor 同样更新格式。所有提交均由 session gate 串行化。

- [ ] **步骤 5：实现 `FramebufferUpdateSession.ReconfigurePixelFormatAsync`**

在 session gate 内验证 Active、预验证全部格式 decoder，再一次性提交。异常前不修改任何 decoder；fault/dispose 状态拒绝调用。

- [ ] **步骤 6：运行协议定向测试并确认 GREEN**

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~ZlibEncodingTests|FullyQualifiedName~ZrleEncodingTests|FullyQualifiedName~FramebufferUpdate"
```

- [ ] **步骤 7：提交协议层实现**

```powershell
git add src/WinARD.Remote.Protocol tests/WinARD.Remote.Protocol.Tests
git commit -m "fix: preserve persistent compression across pixel changes"
```

### 任务 2：将 RfbClient 质量事务改为复用 session

**文件：**
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：编写真实 Q0→Q1 持久流集成测试**

脚本流先返回一个成功的 BGRA32 Zlib update；应用 RGB565 transition 后，repair response 返回同一 compressor 的下一段 RGB565 Zlib update。断言第二帧呈现成功、framebuffer 像素正确、session factory只调用一次、wire只包含一次非增量 repair。

- [ ] **步骤 2：运行测试并确认 RED**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FramePresentationTests.Rfb_client_quality_transition_preserves_persistent_zlib_stream"
```

预期：当前实现创建第二 session，第二 chunk因缺少前置字典而发生 DecoderFailure。

- [ ] **步骤 3：实现 session 复用事务**

删除 transition 中的新 session 创建/交换/旧 session dispose。wire 写入前预验证目标 PixelFormat；wire 写入后调用现有 session 的原子重配置 API；随后发送一次 full repair。任何 wire 后异常继续 fault连接。

- [ ] **步骤 4：补反向切换和失败不变量**

增加 RGB565→BGRA32 持久流回归，并确认 partial write、取消、scheduler fault、outstanding request 与 cleanup 测试保持原语义。

- [ ] **步骤 5：运行 Desktop 定向测试并确认 GREEN**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FramePresentationTests|FullyQualifiedName~AdaptiveQualitySessionIntegrationTests|FullyQualifiedName~QualityTransitionCoordinatorTests"
```

- [ ] **步骤 6：提交 Desktop 集成**

```powershell
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "fix: reuse framebuffer session for quality transitions"
```

### 任务 3：全量验证与重新打包

**文件：**
- 发布：`artifacts/WinARD-portable-win-x64.zip`

- [ ] **步骤 1：运行全仓测试与构建**

```powershell
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet format WinARD.sln --verify-no-changes --no-restore
```

- [ ] **步骤 2：重新打包和验证**

```powershell
.\packaging\portable.ps1 -Version 0.1.0.0
.\packaging\verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
```

- [ ] **步骤 3：检查研究和秘密类文件未进入 ZIP**

打开 `artifacts/WinARD-portable-win-x64.zip`，确认根目录存在一个 `WinARD.Desktop.exe`，且不存在 RDM、ProtocolProbe、protocol-research、credential、password、secret、capture、pcap 或嵌套归档。

- [ ] **步骤 4：记录实机边界**

报告自动化证据；macOS 26.5 实机复测未执行时只标记未验证，不声明故障已在实机消失。
