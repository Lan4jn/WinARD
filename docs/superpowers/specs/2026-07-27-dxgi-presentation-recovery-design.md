# WinARD DXGI 画面呈现恢复设计规格

日期：2026-07-27

状态：方案 A 已确认，等待书面规格审查

## 1. 问题与目标

WinARD 在本地控制台和 RDP 测试中都出现过 `REMOTE_PRESENTATION_FAILED`。两份诊断均记录
`SharpGenException` 和 HRESULT `0x887A0001`（`DXGI_ERROR_INVALID_CALL`），说明故障来自
Direct3D/DXGI 呈现链路的参数或对象状态，而不是 ARD 认证、网络传输或 RDP 本身。

现有安全诊断没有记录具体失败的 DXGI 操作，因此当前证据无法区分故障发生在
`CreateSwapChainForComposition`、`SetSwapChain`、`GetBuffer` 还是 `Present1`。本次修复的目标是：

1. 精确记录失败的呈现阶段和 HRESULT，同时继续遵守诊断脱敏规则。
2. 仅当 `Present1` 返回 `DXGI_ERROR_INVALID_CALL` 时执行一次有界恢复。
3. 恢复时重建交换链资源，上传完整画面，并改用普通全帧 `Present`。
4. 保留正常路径的脏矩形上传和 `Present1` 优化。
5. 保留现有设备移除恢复行为，不吞掉无法恢复的异常。

## 2. 范围

### 2.1 包含

- D3D11 纹理、DXGI 交换链和 `SwapChainPanel` 绑定阶段的操作级诊断。
- `Present1` 的 `DXGI_ERROR_INVALID_CALL` 专用恢复路径。
- 可在无真实 GPU、无 WinUI 窗口的单元测试中验证的恢复协调逻辑。
- 4K 本地冒烟测试和重复运行验证。

### 2.2 不包含

- 修改 ARD/RFB 协议、认证、网络或帧解码。
- 对所有 DXGI 错误进行通用重试。
- 永久关闭脏矩形上传。
- 默认切换到 WARP 软件渲染。
- 多次或无限次自动重试。

## 3. 设计

### 3.1 正常呈现路径

正常情况下继续执行现有流程：

1. 裁剪脏矩形。
2. 将变化区域上传到 D3D11 帧纹理。
3. 将变化区域复制到交换链后缓冲区。
4. 使用 `Present1` 和脏矩形列表提交画面。

空脏矩形列表仍然直接返回，不创建恢复动作。

### 3.2 操作级诊断边界

为可能抛出 `SharpGenException` 的关键操作设置稳定阶段名：

- `CreateDevice`
- `CreateTexture2D`
- `CreateSwapChainForComposition`
- `SetSwapChain`
- `GetBuffer`
- `UpdateSubresource`
- `CopySubresourceRegion`
- `Present1`
- `RecoveryDetachSwapChain`
- `RecoveryCreateSwapChain`
- `RecoverySetSwapChain`
- `RecoveryGetBuffer`
- `RecoveryPresent`

呈现层抛出的异常必须携带阶段名和 HRESULT，供现有
`REMOTE_PRESENTATION_FAILED` 安全诊断链记录。诊断不得包含像素内容、远程主机信息、用户名、
剪贴板正文或未经处理的任意原始文本。原异常作为内部异常保留，以便设备移除判断和开发调试。

### 3.3 有界恢复协调器

从 `D3DFramePresenter` 中提取一个小型、可注入的呈现恢复协调器。协调器只负责决策，不直接依赖
WinUI 或 Vortice 对象：

- 第一次执行优化呈现。
- 如果成功，立即结束。
- 如果失败不是 `Present1` 阶段的 `DXGI_ERROR_INVALID_CALL`，立即重新抛出。
- 如果命中目标错误，执行一次交换链重建，再执行一次普通全帧呈现。
- 重建或普通呈现再次失败时，立即重新抛出，不进入第二轮恢复。

每次 `Present` 调用最多发生一次恢复。下一帧仍从正常优化路径开始，避免一次偶发失败永久关闭
脏矩形优化。

### 3.4 交换链恢复

恢复过程保留 D3D11 设备和设备上下文，只重建与当前画面尺寸相关的资源：

1. 从 `SwapChainPanel` 分离旧交换链。
2. 释放旧交换链和帧纹理。
3. 按当前宽高创建新帧纹理和新交换链。
4. 将新交换链绑定到 `SwapChainPanel`。
5. 将当前 BGRA32 帧作为一个完整矩形上传并复制到后缓冲区。
6. 调用普通 `Present(0, PresentFlags.None)`，不传脏矩形参数。

资源字段只有在创建和绑定成功后才成为当前活动资源。失败路径释放本次创建的临时资源，避免重复
释放、悬空引用和部分初始化状态。

### 3.5 与设备移除恢复的关系

现有设备移除恢复保持更高优先级的独立路径：

- 任意 `SharpGenException` 若对应 `DeviceRemovedReason.Failure`，重建设备和全部帧资源，再呈现完整帧。
- `Present1` 的 `DXGI_ERROR_INVALID_CALL` 且设备未移除时，使用本规格的交换链级恢复。
- 如果设备移除发生在交换链级恢复过程中，异常向外传播，由最外层设备移除处理执行一次设备级恢复。
- 一次调用不得在设备级恢复后再次进入交换链级循环。

## 4. 错误传播

无法恢复的错误继续终止呈现循环，并由 `RemoteSessionViewModel` 映射为
`REMOTE_PRESENTATION_FAILED`。本次修复不会把真实失败伪装成成功。

错误对象至少提供：

- 稳定的呈现阶段名。
- 十六进制 HRESULT。
- 固定、无敏感信息的摘要。
- 原异常作为内部异常。

清理失败可以聚合报告，但不得覆盖最初的呈现失败；主失败必须保持为诊断中的首要原因。

## 5. 测试策略

### 5.1 单元测试

通过注入操作委托或窄接口验证恢复协调器：

1. 优化呈现成功时不重建交换链，也不调用普通 `Present`。
2. `Present1` 返回 `DXGI_ERROR_INVALID_CALL` 时，恰好重建一次并执行一次普通全帧呈现。
3. 相同 HRESULT 出现在非 `Present1` 阶段时不恢复。
4. 非目标 HRESULT 不恢复。
5. 重建失败时异常传播，且不再次重建。
6. 普通 `Present` 失败时异常传播，且不再次呈现。
7. 阶段名和 HRESULT 可被安全诊断提取。
8. 临时及活动资源在成功、失败和销毁路径中各释放一次。
9. 设备移除恢复仍只执行一次，并使用完整帧。

所有行为测试遵循红—绿—重构：先观察测试因缺少目标行为而失败，再编写最小实现使其通过。

### 5.2 集成与冒烟测试

- 运行 `WinARD.Desktop.Tests`。
- 运行完整 `WinARD.sln` Release 测试。
- 运行 Release 构建和项目现有格式、漏洞检查。
- 使用 `--remote-session-smoke` 在 3840×2160 下重复运行并启用
  `WINARD_REMOTE_SMOKE_ERROR_MARKER`。
- 每次运行持续足以覆盖窗口加载、交换链绑定和连续多帧呈现。
- 确认没有生成 `REMOTE_PRESENTATION_FAILED` 错误标记。

自动化验证只能证明测试环境中的回归被阻止。最终真实兼容性仍需在实际 Mac 会话中验证画面、输入和
持续运行稳定性。

## 6. 验收标准

1. 正常帧继续使用脏矩形 `Present1`。
2. `Present1` 的 `DXGI_ERROR_INVALID_CALL` 每帧最多触发一次交换链重建。
3. 恢复帧使用完整画面和普通 `Present`。
4. 重建或回退失败会终止呈现循环并产生包含阶段名和 HRESULT 的安全诊断。
5. 设备移除恢复没有回归。
6. 新增恢复测试、全部现有测试、Release 构建和 4K 冒烟测试通过。
7. 诊断包不包含远程画面、凭据、主机标识或剪贴板正文。

