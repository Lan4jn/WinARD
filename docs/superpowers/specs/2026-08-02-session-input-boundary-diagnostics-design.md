# WinARD 会话输入边界诊断设计

日期：2026-08-02

状态：方案 A 已确认，等待书面规格复核

## 1. 背景与证据

最新实机诊断显示 WinARD 已完成 ARD 003.889 共享控制协商，服务端授予控制能力，登录桌面后仍持续收到动态画面并正常处理 Tickle 保活。诊断中没有断线、加密完整性错误或输入发送异常。

现有诊断没有记录输入链路，因此无法区分以下边界：

1. WinUI 没有继续捕获键盘或指针事件；
2. 输入因无效 viewport transform、会话关闭状态等原因在 UI 层被丢弃；
3. 输入已进入 ViewModel 和输入映射器，但没有到达协议客户端；
4. 标准 RFB 输入报文已进入加密传输并完成底层写出，但 macOS 没有执行。

公开参考实现进入全会话加密后同样发送标准 RFB KeyEvent 和 PointerEvent。当前没有证据支持修改控制模式、输入报文类型、加密算法、IV、padding 或坐标布局。

## 2. 目标

- 用一次短时间实机复现确定输入链路的最后一个成功边界。
- 诊断只记录事件类别、阶段、累计数量和公开状态，不记录输入内容。
- 鼠标移动不会淹没现有协商、状态变化和错误事件。
- 诊断失败不得影响远程会话或输入发送。

## 3. 非目标

- 不修复尚未定位的输入问题。
- 不重发 `SetMode` 或重新协商 ARD 会话。
- 不增加 Pointer Smoke、坐标扫描或自动输入。
- 不记录 keysym、VirtualKey、扫描码、Unicode 字符、按键方向、鼠标坐标、按钮状态、密码、剪贴板、明文、密文、密钥、IV、摘要或原始报文。
- 不根据自动化测试宣称真实 Mac 已可控制。

## 4. 诊断阶段

每类输入分别维护单调递增计数。诊断事件使用统一代码 `REMOTE_INPUT_ACTIVITY`，字段如下：

- `Kind`：`Keyboard` 或 `Pointer`；
- `Boundary`：
  - `UiCaptured`：WinUI 事件处理器收到可处理事件；
  - `UiDropped`：事件未进入发送路径；
  - `ProtocolWriteStarted`：协议客户端准备写入一个完整输入消息；
  - `ProtocolWriteCompleted`：加密传输的写入调用正常完成；
- `Count`：该 Kind/Boundary 的累计数量；
- `Reason`：仅用于 `UiDropped`，取稳定枚举值，例如 `SessionClosing` 或 `InvalidTransform`；
- `Encrypted`：仅协议边界记录 `True` 或 `False`，不记录加密材料；
- `Sampled`：固定为 `True`，明确该事件是聚合快照而非逐条输入审计。

协议写入抛出异常时继续使用现有 `REMOTE_INPUT_FAILED` 错误诊断和关闭流程，不新增包含输入内容的异常字段。

## 5. 采样策略

为每个 Kind/Boundary 独立采样：

- 第 1 次必定记录；
- Keyboard 此后累计数达到 8 的倍数时记录；
- Pointer 记录第 8、64 次，此后累计数达到 256 的倍数时记录；
- `UiDropped` 第 1 次及每次达到 4 的倍数时记录，以便少量丢弃也能被看到；
- 写诊断使用 `ISafeDiagnosticSink.TryWrite`，失败时静默忽略，不改变输入行为。

该策略可在密码输入和桌面操作之间保留新的计数快照，同时限制高频 PointerMoved 产生的诊断量。

## 6. 组件边界

- `RemoteInputDiagnosticTracker`：线程安全地维护计数和采样判定，只接收 Kind、Boundary、Reason 和加密状态；同一 Kind/Boundary 的计数分配、采样和 sink 写入串行化，保证导出时间线中的 Count 单调递增。
- `RemoteSessionWindow`：在键盘、字符和指针入口记录 `UiCaptured`；使用统一的 closing 谓词覆盖窗口关闭通知、会话停止和 lifetime 取消，在 closing 或无效 transform 时记录 `UiDropped`。
- `RfbClient`：在 `SendKeyAsync` 和 `SendPointerAsync` 的完整报文写入前后记录 `ProtocolWriteStarted` 与 `ProtocolWriteCompleted`。
- `RfbClientFactory` 和会话窗口通过现有诊断 sink 创建各自的 tracker；边界事件依靠 Kind、Boundary、Count 和时间戳进行判断，不引入跨层共享可变状态。

因为 UI 与协议层计数分别独立，诊断分析比较的是各层在复现时间段内是否继续增长，而不是要求两个计数绝对相等。键盘映射可能将一次字符输入转换成多个协议事件，指针采样和合并也可能造成数量差异。

## 7. 根因判定规则

- 没有新的 `UiCaptured`：WinUI 焦点或命中测试边界故障。
- `UiCaptured` 增长且 `UiDropped/InvalidTransform` 增长，但协议边界不增长：viewport/尺寸状态故障。
- `UiCaptured` 增长、没有对应协议 `ProtocolWriteStarted`：输入映射或调度边界故障。
- `ProtocolWriteStarted` 增长但 `ProtocolWriteCompleted` 不增长：加密写出或连接 writer 故障，通常还应伴随 `REMOTE_INPUT_FAILED`。
- `ProtocolWriteCompleted` 在桌面操作阶段持续增长且没有输入错误：WinARD 已成功写出输入，后续调查限定在 macOS 对当前会话输入的解释或登录切换后的 ARD 状态，不再修改 WinUI 焦点或坐标代码。

## 8. 测试策略

实现遵循 TDD：

1. tracker 的 Keyboard 每 8 次、Pointer 有界高频采样和每 4 次丢弃测试先失败；
2. 并发计数保持单调且不重复采样，并用阻塞 sink 验证写入顺序不会出现 Count 倒退；
3. 诊断字段中不出现禁止的输入内容字段；
4. sink 抛出异常或为 null 时输入操作仍正常完成；
5. KeyEvent 和 PointerEvent 成功写入产生 Started/Completed 边界；
6. 协议写入失败只产生 Started，并保留现有输入失败处理；
7. UI 集成测试确认无效 transform 和关闭状态具有稳定丢弃原因；
8. 运行 Desktop 测试、协议测试、完整解决方案测试、格式检查和 Release x64 编译。

## 9. 实机复现步骤

1. 使用新构建连接 Mac；
2. 在登录界面输入密码并进入桌面；
3. 等待桌面动态画面稳定；
4. 在远程画面上点击、移动鼠标，并输入少量普通非敏感字符；
5. 立即从会话工具栏导出诊断；
6. 根据第 7 节只定位断裂边界，根因明确前不实施协议或 UI 修复。
