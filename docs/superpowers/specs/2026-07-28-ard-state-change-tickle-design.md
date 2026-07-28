# ARD StateChange 与 Tickle 保活设计

## 背景

WinARD 已能在 macOS 26.5 上完成 Apple Remote Desktop `003.889` 协商，进入共享控制模式并显示远程画面。最新实机诊断表明，连接在空闲约三秒后中断：

- `2026-07-28T15:00:18.2569582Z`：会话完成协商，`FinalState=SharedControlNegotiated`。
- `2026-07-28T15:00:21.4449749Z`：运行期读取到服务端消息类型 `0x14`，被当前客户端判定为 `UnexpectedServerMessage`。

Apple Remote Desktop 将服务端消息 `0x14` 定义为 `StateChange`。当客户端到服务端方向约三秒没有消息时，服务端会发送状态码 `4`（`Tickle`）；客户端应回复 `AutoFBUpdate (0x09)`，表明客户端仍然存活并希望继续接收自动画面更新。

因此，断线根因不是认证、控制模式或输入控件，而是运行期消息分发器不支持 ARD `StateChange`，也没有响应 `Tickle`。

## 目标

1. 在 RFB `003.889` 会话中完整、安全地解析服务端 `StateChange (0x14)`。
2. 收到 `Tickle` 后立即发送精确的 `AutoFBUpdate (0x09)` 响应。
3. 完整消费其他已知和未知状态的载荷，避免流错位或无意义断线。
4. 保留当前显式 `FramebufferUpdateRequest` 流程，不引入定时心跳或无关协议行为。
5. 为解析失败和状态处理提供安全、结构化的诊断信息。

## 非目标

- 本轮不实现 ARD 专用剪贴板 `0x1f` 协议。
- 本轮不为睡眠、唤醒或光标可见性状态增加新的 UI 行为。
- 本轮不增加后台定时器、周期性心跳或指针探测。
- 本轮不宣称 macOS 控制连接已在实机上稳定，直到用户完成静置验证。

## 方案选择

采用专用协议组件：

- `ArdStateChangeReader` 负责读取和验证服务端 `StateChange` 消息体。
- `ArdClientMessageWriter` 负责生成客户端 `AutoFBUpdate` 消息。
- `RfbClient.ReceiveAsync` 只负责分发消息并执行状态对应的运行期动作。

不把全部解析逻辑内联到 `RfbClient.ReceiveAsync`，以避免消息循环继续承担字段解析、长度验证和状态解释等多种职责。也不使用周期性发送作为替代，因为周期性发送既不能消费 `0x14`，又会引入额外并发写入和生命周期管理。

## 线协议

### 服务端 StateChange

完整消息格式：

```text
[type:1][pad:1][size:u16be][flags:u16be][status:u16be][extra:size-4]
```

- `type` 固定为 `0x14`，由顶层消息分发器先消费。
- `size` 是其后 payload 的字节数，不包含 `type`、`pad` 和 `size` 字段。
- 合法 payload 至少包含 `flags` 和 `status`，因此 `size >= 4`。
- `extra` 可以为空；当 `size > 4` 时必须完整消费。
- padding 为兼容字段。读取但不要求为零，避免拒绝可兼容的服务端实现。

已知状态码：

| 状态 | 名称 | 本轮动作 |
|---:|---|---|
| 1 | LocalUserClosed | 识别为远端主动结束会话 |
| 2 | PasteboardChanged | 完整消费并记录 |
| 3 | PasteboardDataNeeded | 完整消费并记录 |
| 4 | Tickle | 回复 `AutoFBUpdate`，继续接收 |
| 5 | Sleep | 完整消费并记录 |
| 6 | Wake | 完整消费并记录 |
| 11 | CursorHidden | 完整消费并记录 |
| 12 | CursorVisible | 完整消费并记录 |

未知状态在消息结构合法时也必须完整消费、记录并继续。未知顶层消息类型仍保持现有失败策略。

### 客户端 AutoFBUpdate

完整消息固定为 16 字节：

```text
[type:1][pad:1][enabled:u16be][interval:u32be]
[x:u16be][y:u16be][width:u16be][height:u16be]
```

Tickle 响应字段：

| 字段 | 值 |
|---|---:|
| type | `0x09` |
| pad | `0` |
| enabled | `1` |
| interval | `0`，使用服务端默认值 |
| x | `0` |
| y | `0` |
| width | 当前 framebuffer 宽度 |
| height | 当前 framebuffer 高度 |

宽高来自已经初始化且经过协议限制验证的 framebuffer。若运行期宽高不能转换为非零 `ushort`，不得发送畸形响应，而应产生结构化协议错误。

## 组件设计

### ArdStateChangeReader

新增一个只解析已消费类型字节之后消息体的组件。输入为现有 `RfbReader`、协议限制和取消令牌，输出包含：

- padding 原始值；
- payload size；
- flags；
- status；
- extra payload 的长度。

解析顺序：

1. 读取 `pad` 和 `size`。
2. 验证 `size >= 4`，并验证总消费量不超过协议消息预算。
3. 读取 `flags` 和 `status`。
4. 有界跳过 `size - 4` 字节。
5. 返回结构化结果。

截断、非法长度和超过预算必须抛出 `RfbProtocolException`，并由调用方补充：

- `ProtocolFailureKind=MalformedArdStateChange`，追加到现有枚举末尾；
- `ProtocolReadStage=ArdStateChangeHeader` 或 `ArdStateChangePayload`，追加到现有枚举末尾；
- `ServerMessageType=0x14`。

### ArdClientMessageWriter

增加 `WriteAutoFramebufferUpdateAsync`。该方法：

- 验证宽高非零；
- 生成固定 16 字节消息；
- 对所有多字节字段使用大端编码；
- 通过现有 `RfbWriter.WriteMessageAsync` 完成单次写入；
- 不管理定时器，也不改变其他客户端消息。

### RfbClient 运行期分发

`RfbClient.ReceiveAsync` 在 `003.889` 会话中增加 `0x14` 分支：

1. 调用 `ArdStateChangeReader` 完整读取消息体。
2. 写入安全诊断事件，字段包含 `Status`、`Flags`、`PayloadSize` 和 `Action`，不得包含端点、凭据或剪贴板内容。
3. 根据状态执行动作：
   - `Tickle`：发送完整 framebuffer 区域的 `AutoFBUpdate`，随后继续当前接收循环。
   - `LocalUserClosed`：抛出带 `ProtocolFailureKind=RemoteSessionClosed`、`ProtocolReadStage=ArdStateChangePayload` 和 `ServerMessageType=0x14` 的结构化协议异常；该 failure kind 追加到现有枚举末尾，使诊断和 UI 能明确区分远端结束与未知消息。
   - 其他已知或未知状态：不向 UI 返回无意义消息，继续接收下一个顶层消息。

本次不在初始连接或每次显式 framebuffer 请求后额外发送 `AutoFBUpdate`。当前实机已经能获得初始画面，故最小根因修复是正确消费 Tickle 并按需响应；这样也避免同时改变现有拉取模型。

## 错误处理与边界

- `size < 4`：畸形消息，失败。
- 消息在 header、固定 payload 或 extra 中截断：失败，并保留 `0x14` 上下文。
- payload 超过配置的消息预算：在分配或跳过前失败。
- 未知 `status`：不是协议结构错误；完整消费并继续。
- 非 `003.889` 会话中的 `0x14`：仍按未知顶层服务端消息处理。
- Tickle 响应写入失败：保留原始 I/O 异常；顶层中断诊断额外写入 `ArdStateChangeStatus=4` 和 `ArdStateChangeAction=AutoFBUpdateFailed`，不把写失败误标为读取失败。
- 取消：遵循现有取消语义，不包装为协议失败。

## 并发与生命周期

Tickle 响应在当前 `ReceiveAsync` 调用内顺序写入，与当前输入、剪贴板或 framebuffer 请求共享底层流。每条协议消息先构造完整字节数组，再通过一次 `WriteMessageAsync` 写入。`RfbWriter` 已通过以 `Stream` 为键的共享 `SemaphoreSlim` 串行化同一连接上的所有写入，因此 Tickle 响应直接复用该机制；不得为本功能单独创建后台写线程。

## 测试策略

### ArdStateChangeReader 单元测试

- 精确解析最小 `size=4` 消息。
- 接受非零 padding。
- 完整跳过 extra payload，并保证下一个消息字节仍能正确读取。
- 覆盖所有已知状态码。
- 接受未知状态码。
- 拒绝 `size=0..3`。
- header、固定 payload 和 extra 分别截断时失败。
- 超过消息预算时在继续读取前失败。
- 取消令牌行为与其他读取器一致。

### ArdClientMessageWriter 单元测试

- `Tickle` 对应的精确 16 字节输出。
- 宽、高和所有字段采用大端编码。
- 零宽或零高在写入前失败，流保持未写入。
- 取消与写入异常按现有写入器语义传播。

### RfbClient 集成测试

- `StateChange(Tickle)` 后写出精确 `AutoFBUpdate`，随后能继续解析一个 framebuffer update。
- extra payload 不会污染后续消息边界。
- 其他已知状态和未知状态不会造成连接中断。
- 非 `003.889` 会话仍拒绝 `0x14`。
- 畸形 StateChange 的失败指纹包含 `ServerMessageType=0x14` 和对应读取阶段。
- Tickle 诊断字段不含敏感信息。

### 回归验证

- Release/x64 构建无错误。
- 运行全量自动化测试。
- 不改变标准 RFB 会话、现有 ACK/NOP、输入、剪贴板和 framebuffer 请求测试结果。

## 实机验收

使用新构建连接同一台 macOS 26.5 主机：

1. 等待远程画面出现。
2. 不进行鼠标、键盘或剪贴板操作，静置至少十秒。
3. 确认连接不再在约三秒处中断。
4. 确认画面仍能继续更新。
5. 若失败，只导出一次新诊断，检查 StateChange 状态、Tickle 响应动作以及新的失败指纹。

只有该实机验收通过后，才能宣称空闲连接问题已修复。
