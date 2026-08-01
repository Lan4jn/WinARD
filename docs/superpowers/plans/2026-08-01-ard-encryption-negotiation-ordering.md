# ARD 加密协商异步顺序修复实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 允许 macOS 在 1103 会话材料之前发送普通 framebuffer update，同时保证鼠标、键盘和剪贴板在加密激活前不会写入明文传输。

**架构：** `ArdSessionEncryption` 保持 `Requested` 状态跨越普通更新，并通过单一异步激活信号协调等待中的敏感写入。`RfbClient` 的 pointer/key/clipboard 方法先等待该信号，再调用现有 writer；FramebufferUpdateRequest 和 ARD liveness 回复不受阻塞，以便服务器继续推进到 1103。

**技术栈：** C# 12、.NET 8、xUnit、`TaskCompletionSource<bool>`、现有 ARD 3.889/RFB 测试夹具。

---

## 文件结构

- 修改 `src/WinARD.Remote.Protocol/Ard/ArdSessionEncryption.cs`：容忍 1103 晚于首帧到达，并提供无明文降级的激活等待器。
- 修改 `src/WinARD.Desktop/Services/RfbClientFactory.cs`：在 pointer/key/clipboard 写入前等待 ARD 加密激活。
- 修改 `tests/WinARD.Remote.Protocol.Tests/Ard/ArdSessionEncryptionTests.cs`：锁定普通首帧不再致命，以及激活等待器的成功、取消和销毁行为。
- 修改 `tests/WinARD.Desktop.Tests/FramePresentationTests.cs`：覆盖普通首帧、后续 1103、激活前敏感输入零字节和激活后密文发送。
- 修改 `docs/superpowers/specs/2026-07-29-ard-session-encryption-design.md`：纠正“首个 update 必须携带 1103”的隐含顺序假设。

### 任务 1：用失败测试锁定服务器异步顺序

**文件：**
- 修改：`tests/WinARD.Remote.Protocol.Tests/Ard/ArdSessionEncryptionTests.cs`
- 修改：`tests/WinARD.Desktop.Tests/FramePresentationTests.cs`

- [ ] **步骤 1：把首帧缺少 1103 的测试改为保持 Requested**

将 `First_completed_framebuffer_update_without_1103_fails_closed` 改为：

```csharp
[Fact]
public async Task Completed_framebuffer_update_without_1103_remains_requested()
{
    await using var inner = new ScriptedDuplexStream([]);
    await using var transport = new ArdEncryptedStream(inner, ProtocolLimits.Default);
    await using var encryption = new ArdSessionEncryption(
        transport,
        new ArdAuthenticationResult(AuthenticationKey.ToArray()));
    await encryption.RequestAsync(CancellationToken.None);

    await encryption.CompleteFramebufferUpdateAsync(CancellationToken.None);

    Assert.Equal(ArdSessionEncryptionState.Requested, encryption.State);
    Assert.False(transport.IsEncrypted);
}
```

- [ ] **步骤 2：增加桌面集成测试**

构造输入 `Handshake + ArdServerInit + EmptyFramebufferUpdate + ArdSessionEncryptionUpdate`。先读取空 update，然后同时启动 pointer、key 和 clipboard 写入，断言任务未完成且底层流没有新增字节；读取后续 1103 update 后等待三个任务完成，并按链式 IV 解密三个数据包，断言负载分别是标准 RFB PointerEvent、KeyEvent 和 ClientCutText。

- [ ] **步骤 3：运行测试确认红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ArdSessionEncryptionTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Rfb_client_waits_for_later_1103_before_sending_sensitive_messages
```

预期：协议测试因首帧仍抛 `ArdEncryptionNegotiation` 失败；桌面集成测试同样在首个普通 update 失败。

### 任务 2：实现激活等待器和输入门控

**文件：**
- 修改：`src/WinARD.Remote.Protocol/Ard/ArdSessionEncryption.cs`
- 修改：`src/WinARD.Desktop/Services/RfbClientFactory.cs`

- [ ] **步骤 1：在控制器中增加激活完成信号**

新增：

```csharp
private readonly TaskCompletionSource<bool> _activationCompletion =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

public async ValueTask WaitUntilEncryptedAsync(CancellationToken cancellationToken)
{
    Task<bool> activationTask;
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        ThrowIfDisposed();
        if (_state == ArdSessionEncryptionState.Encrypted)
        {
            return;
        }

        activationTask = _activationCompletion.Task;
    }
    finally
    {
        _gate.Release();
    }

    if (!await activationTask.WaitAsync(cancellationToken).ConfigureAwait(false))
    {
        throw new ObjectDisposedException(nameof(ArdSessionEncryption));
    }
}
```

- [ ] **步骤 2：纠正 framebuffer 完成状态转移**

删除 `Requested` 时的首帧 fatal 分支。仅当状态为 `PendingActivation` 时写 ACK、激活 transport、设置 `Encrypted` 并执行：

```csharp
_activationCompletion.TrySetResult(true);
```

`DisposeAsync` 在清理密钥后执行 `_activationCompletion.TrySetResult(false)`，释放尚在等待的调用方。

- [ ] **步骤 3：门控敏感客户端写入**

将 `RfbClient.SendPointerAsync`、`SendKeyAsync` 和 `SendClipboardTextAsync` 改为 `async ValueTask`。当 `_sessionEncryption` 非空时先：

```csharp
await _sessionEncryption.WaitUntilEncryptedAsync(cancellationToken).ConfigureAwait(false);
```

随后调用原 writer。不要门控 `RequestFramebufferUpdateAsync` 或 Tickle 的 AutoFBUpdate 回复。

- [ ] **步骤 4：运行定向测试确认绿灯**

运行任务 1 的两个命令，预期全部通过。

### 任务 3：文档、完整验证与发布

**文件：**
- 修改：`docs/superpowers/specs/2026-07-29-ard-session-encryption-design.md`

- [ ] **步骤 1：更新协议设计**

明确 1103 可以出现在 SetEncryption request 之后的任意有效 framebuffer update；普通 update 保持 `Requested`，敏感写入等待激活，不允许明文降级。

- [ ] **步骤 2：运行差异检查和完整验证**

```powershell
git diff --check
dotnet build WinARD.sln -c Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test WinARD.sln -c Release --no-build
```

预期：构建 0 warning、0 error；所有测试通过。

- [ ] **步骤 3：提交实现**

```powershell
git add src/WinARD.Remote.Protocol/Ard/ArdSessionEncryption.cs src/WinARD.Desktop/Services/RfbClientFactory.cs tests/WinARD.Remote.Protocol.Tests/Ard/ArdSessionEncryptionTests.cs tests/WinARD.Desktop.Tests/FramePresentationTests.cs docs/superpowers/specs/2026-07-29-ard-session-encryption-design.md docs/superpowers/plans/2026-08-01-ard-encryption-negotiation-ordering.md
git commit -m "fix: tolerate delayed ARD encryption material"
```

- [ ] **步骤 4：发布并校验便携 ZIP**

使用新短提交号发布 `WindowsPackageType=None`、self-contained `win-x64`，验证 ZIP 包含 `WinARD.Desktop.exe`、`WinARD.OpenSshAskPass.exe`、`Microsoft.UI.dll` 和 `e_sqlite3.dll`，输出 SHA-256。实机控制仍必须由 macOS 26.5 验证。
