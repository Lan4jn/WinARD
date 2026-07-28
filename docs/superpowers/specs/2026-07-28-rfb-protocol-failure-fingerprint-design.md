# WinARD RFB 运行期协议失败指纹设计

## 背景

macOS 26.5 实机已经证明 WinARD 能完成 `003.889`、`ClientInit=0xC1`、`mayControl` 和共享控制协商，画面与键盘输入也能工作。但连接在空闲数秒后稳定中断，诊断仅保留 `RfbProtocolException` 类型，无法判断失败发生在顶层消息、Framebuffer 矩形编码、剪贴板还是截断读取。

本设计只增强脱敏诊断，不猜测或放宽未知协议消息，也不抓取原始网络载荷。

## 目标

- 下一次自动断线后，仅凭诊断文件即可定位到稳定协议分支。
- 至少记录读取阶段、顶层服务端消息类型、Framebuffer 编码 ID、矩形序号和稳定失败类别中实际可用的字段。
- 保持未知消息和未知编码立即失败，不根据未知载荷猜测长度。
- 不记录画面、剪贴板、键盘输入、用户名、主机、显示名称、凭据或原始异常文本。
- 标准 RFB 与 ARD `003.889` 使用同一套失败指纹模型。

## 非目标

- 本轮不实现尚未识别的 Apple 消息或编码。
- 不记录 PCAP、原始字节、压缩数据或协议载荷摘要。
- 不改变现有错误卡片、重试策略或会话关闭语义。
- 不用 Pointer Smoke、坐标扫描或额外输入测试定位问题。

## 方案

### 结构化异常元数据

为 `RfbProtocolException` 增加可选、不可变的结构化失败信息。字段均为固定枚举或受界整数：

- `FailureKind`：稳定类别，例如 `UnexpectedServerMessage`、`UnsupportedEncoding`、`TruncatedRead`、`MalformedFramebufferUpdate`、`MalformedClipboard`、`DecoderFailure`。
- `ReadStage`：例如 `ServerMessageType`、`FramebufferHeader`、`FramebufferRectangleHeader`、`FramebufferRectanglePayload`、`ClipboardHeader`、`ClipboardPayload`。
- `ServerMessageType`：已读取时记录 `0..255`。
- `EncodingId`：进入某个矩形解码器后记录有符号 32 位编码 ID。
- `RectangleIndex`：已知时记录当前矩形的零基序号。

结构化信息不替代现有异常消息；消息继续用于开发测试，但诊断导出器不得导出消息文本。

### 边界包装

仅在能够准确知道上下文的协议边界补充信息：

1. `RfbClient.ReceiveAsync` 读取顶层消息类型后，为未知消息记录 `UnexpectedServerMessage` 和具体类型。
2. `FramebufferUpdateReader` 在读取更新头、矩形头以及调用解码器时补充当前消息类型 `0`、矩形序号与编码 ID。内部 `RfbProtocolException` 若已有更具体信息则保留，不重复覆盖。
3. `ClipboardProtocol` 在头部、长度、UTF-8 解码失败时记录剪贴板阶段和固定类别，但不记录文本或长度之外的载荷信息。
4. `RfbReader` 的意外 EOF 记录 `TruncatedRead`；上层边界负责补充更具体的读取阶段。

取消异常、网络 I/O 异常和呈现异常不转换成协议失败指纹。

### 诊断接线

`RemoteSessionViewModel` 记录 `REMOTE_SESSION_INTERRUPTED` 时，如果根异常是带结构化信息的 `RfbProtocolException`，追加固定 Public 字段：

- `ProtocolFailureKind`
- `ProtocolReadStage`
- `ServerMessageType`，格式为 `0xNN`
- `EncodingId`，十进制有符号数
- `RectangleIndex`

缺失字段不写入。原始异常仍交给现有安全诊断管线，以保留异常类型与 HResult；异常消息继续被排除。

## 数据流

```text
macOS 服务端字节
  -> RfbClient 顶层消息边界
  -> Framebuffer / Clipboard 子解析器
  -> RfbProtocolException + 结构化失败信息
  -> RemoteSessionViewModel
  -> ISafeDiagnosticSink
  -> 脱敏 diagnostics.json
```

## 错误与兼容性

- 未知顶层消息或编码仍然是致命协议错误；本设计不吞消息。
- 已存在的 `RfbProtocolException(string)` 和 `(string, Exception)` 构造方式保持可用，避免破坏公共调用点。
- 重复包装时采用“最具体信息优先”：内层字段已存在则保留，外层只补空缺字段。
- 所有整数在进入诊断前使用固定格式，不经过异常消息解析或正则提取。

## 测试

必须先看到以下测试失败，再实现：

1. ARD 未知顶层消息导出 `UnexpectedServerMessage`、`ServerMessageType=0x08` 和 `ServerMessageType` 阶段。
2. 未知 framebuffer 编码导出 `UnsupportedEncoding`、编码 ID 和矩形序号。
3. 已知编码载荷截断导出 `TruncatedRead`，同时保留当前编码 ID、矩形序号与 payload 阶段。
4. 非法 ServerCutText 导出 `MalformedClipboard` 和剪贴板阶段，不包含剪贴板内容。
5. 取消和普通 I/O 异常不伪装成协议失败。
6. 诊断 JSON 不包含原始异常消息、载荷样本、显示名称或测试注入的秘密标记。
7. 现有协议、桌面、诊断和 Release/x64 全量测试保持通过。

## 实机成功标准

使用新构建连接 macOS 26.5，不进行任何输入并等待自动中断。新诊断必须在 `REMOTE_SESSION_INTERRUPTED` 中包含足以唯一定位下一步实现分支的结构化字段，同时继续证明协商为 `003.889 / 0xC1 / mayControl / Shared`。
