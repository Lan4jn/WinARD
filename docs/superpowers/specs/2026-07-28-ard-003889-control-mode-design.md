# WinARD ARD 003.889 控制模式设计规格

日期：2026-07-28

状态：方案 A 已确认，等待书面规格审查

## 1. 问题与根因

WinARD 当前可以通过 Apple Remote Desktop Security Type 30 完成认证并接收画面，但 macOS 26.5
将连接显示为观察模式，标准 RFB PointerEvent 也不生效。

根因位于认证后的 ARD 扩展协商，而不是 WinUI 输入控件：

1. macOS 发送 `RFB 003.889\n` 后，`RfbVersion.Parse` 将其映射为普通 `RfbVersion.V3_8`；
2. `RfbHandshake` 因而回写 `RFB 003.008\n`，丢失 Apple ARD 协议标识；
3. `RfbSessionInitializer` 固定发送普通 `ClientInit=0x01`；
4. WinARD 不解析 Apple Extended ServerInit，也不发送 `ViewerInfo`、`SetMode(control)` 和
   `SetDisplay`；
5. macOS 因此把会话当作普通 VNC 观察连接，而不是 ARD 控制连接。

公开互操作实现 `peetinc/noVNC-ARD` 在提交
`928fabc011ceb86dad1adf3a01d7bbeaf3fd20cf` 中明确区分 `003.889`，原样回显版本，发送
`ClientInit=0xC1`，并通过 Extended ServerInit 与 `SetMode` 选择控制模式。该行为与 WinARD 的实机
症状及本地数据流一致。本规格以线格式作为互操作证据；最终结论仍须由 macOS 26.5 实机验证。

## 2. 目标与范围

### 2.1 目标

- 在 TCP 5900 上进入 Apple ARD 003.889 扩展会话。
- 请求共享控制模式，而不是观察或独占模式。
- 当服务端要求 Session Select 时，选择当前控制台会话。
- 明确验证服务端是否授予控制能力，禁止静默退回观察模式。
- 保留对标准 RFB 3.3、3.7 和 3.8 服务端的现有行为。
- 让桌面客户端和协议探针共用同一套协商实现。

### 2.2 本次包含

- `003.889` 的独立协议身份和原样版本回显。
- Apple ClientInit 标志 `0xC1`（Shared、Select、Enhanced）。
- Extended ServerInit 的标志、能力位图和显示名称解析。
- `mayControl` 权限检查。
- 控制台 Session Select 状态机。
- `ViewerInfo`、`SetMode(1)`、`SetDisplay(all)` 的报文编码与初始化顺序。
- ARD `Ack`、`NOP` 等零载荷控制消息的安全消费，避免进入已连接状态后被误判为未知 RFB 消息。
- 脱敏诊断字段和精确线格式测试。

### 2.3 本次不包含

- TCP/UDP 3283 管理、资产收集、软件分发或批量管理通道。
- Security Type 33 RSATunnel。
- 全流量加密、加密按键事件、剪贴板扩展、幕帘锁定和虚拟显示 UI。
- ARD 专用图像编码和画质选择。
- 坐标扫描、随机输入或继续扩展 Pointer Smoke 探针。

这些能力可以在控制模式经真实 Mac 验证后分别设计，不能与本次根因修复捆绑。

## 3. 协议模型

### 3.1 保留 ARD 协议身份

`RfbVersion` 增加独立的 `V3_889` 值，其认证结果语义仍兼容 RFB 3.8，但 `Banner` 必须保持
`RFB 003.889\n`。握手结果同时承担两个职责：

- 决定 Security Type 30 的认证结果读取规则；
- 告知初始化器应走标准 RFB 还是 Apple ARD 扩展路径。

不得再通过把 889 直接折叠成 `V3_8` 来隐式选择行为。`RfbClient` 和 `ProbeRunner` 必须把握手结果
传给会话初始化器，避免初始化器根据后续字节猜测协议。

### 3.2 Apple ClientInit

仅当握手版本为 `V3_889` 时发送一个字节 `0xC1`：

- `0x01`：Shared；
- `0x40`：Select；
- `0x80`：Enhanced。

标准 RFB 连接继续发送 `0x01`。

### 3.3 Extended ServerInit

先按标准 ServerInit 读取宽、高、16 字节像素格式和名称字段长度。对于 `V3_889`，名称字段满足
长度至少 22 字节且首字节为 `0x00` 时，按以下结构解析：

- 字节 0–1：扩展标记；
- 字节 2–5：大端 32 位服务端标志；
- 字节 6–21：16 字节能力位图；
- 最后一个 NUL 后的剩余内容：UTF-8 显示名称。

服务端标志至少识别：

- `0x01 observe`；
- `0x02 mayControl`；
- `0x04 sessionSelect`；
- `0x08 noVirtualDisplay`。

若 `V3_889` 返回的不是 Extended ServerInit，或没有设置 `mayControl`，初始化立即以稳定的协议错误
终止。WinARD 不得显示“连接成功”后继续发送无效输入，也不得静默降级为观察模式。

## 4. 初始化状态机

### 4.1 无 Session Select

Extended ServerInit 未设置 `sessionSelect` 时，按固定顺序发送：

1. `ViewerInfo`；
2. `SetMode(1)`，请求共享控制；
3. `SetDisplay(combineAll=1, displayId=0)`；
4. 现有 `SetPixelFormat`；
5. 扩展后的 `SetEncodings`；
6. 尺寸非零时发送首次完整 FramebufferUpdateRequest。

`SetMode` 固定为 1。本次不引入观察、共享、独占的 UI 选择，避免把协议修复扩大为产品设置功能。

### 4.2 控制台 Session Select

服务端设置 `sessionSelect` 时，在发送常规初始化报文前执行有界状态机：

1. 读取带 16 位大端长度的 SessionInfo；
2. 校验版本、允许命令位图、保留字段和 NUL 结尾的控制台用户名；
3. 若允许 `ConnectToConsole`，发送 74 字节 SessionCommand，命令为 1；
4. 若只允许 `RequestConsole`，发送命令 0；
5. 读取一个或多个 SessionResult；状态 2 或 3 表示等待，状态 0 或 4 表示获准；
6. 获准后执行与无 Session Select 相同的初始化序列；其他状态明确报“会话选择被拒绝”。

本次始终选择当前控制台，不自动创建虚拟显示。所有长度受 `ProtocolLimits` 约束，等待过程服从现有
连接取消和超时，禁止无限分配或忙循环。

### 4.3 ARD 控制消息

进入正常收包循环后，接收器应识别并消费至少以下 Apple 服务端消息：

- `0x04 Ack`：零载荷；
- `0x07 NOP`：零载荷。

消费这些消息后继续读取下一条业务消息，不向上层伪造帧，也不使连接失败。其他尚未支持的 ARD
消息仍应产生包含消息类型的稳定协议错误，避免无界跳过未知载荷造成流错位。

## 5. 报文编码

新增专注于 ARD 客户端控制消息的编码器，每个方法只负责编码并写入一个完整报文：

- `ViewerInfo`：66 字节，包含应用类别、应用标识、版本字段和 32 字节命令能力位图；
- `SetMode`：`0A 00 00 01`；
- `SetDisplay`：`0D 01 00 00 00 00 00 00`；
- `SessionCommand`：大端长度 72、版本 1、命令、填充和 64 字节 NUL 填充用户名。

所有多字节整数使用大端编码。用户名按 UTF-8 编码、截断在完整字符边界并保证 NUL 终止。报文编码
与状态机分离，以便使用固定字节向量独立测试。

`SetEncodings` 增加控制协商需要的 Apple 伪编码时，应只加入当前读取器能够安全处理或明确忽略的
项目。本次不声明尚未实现的加密、剪贴板或专用像素能力。

## 6. 组件边界

- `RfbHandshake`：识别并原样协商 `003.889`，返回明确协议身份。
- `RfbSessionInitializer`：根据握手结果选择标准或 ARD 初始化策略。
- `ArdServerInitParser`：解析并校验扩展字段，不负责写消息。
- `ArdSessionSelector`：完成控制台 Session Select 状态机。
- `ArdClientMessageWriter`：生成 ViewerInfo、SetMode、SetDisplay 和 SessionCommand。
- `RfbClient` 与 `ProbeRunner`：只负责传递握手上下文，不各自复制 ARD 逻辑。
- 正常收包层：消费已知零载荷 ARD 控制消息，现有帧缓冲区和输入写入接口保持不变。

这些边界让标准 RFB 路径不依赖 ARD 细节，也允许对每段线协议分别做内存流测试。

## 7. 错误与诊断

新增稳定错误场景：

- `ARD_EXTENDED_INIT_REQUIRED`：889 会话未返回扩展初始化；
- `ARD_CONTROL_NOT_ALLOWED`：服务端未设置 `mayControl`；
- `ARD_SESSION_COMMAND_UNAVAILABLE`：无法选择或请求控制台；
- `ARD_SESSION_DENIED`：服务端拒绝会话选择；
- `ARD_SESSION_MALFORMED`：长度、版本或状态报文非法。

诊断可记录：原始协议版本、发送的 ClientInit 标志、服务端标志的十六进制值、是否要求 Session
Select、请求的控制模式和最终会话状态。诊断不得记录用户名、凭据、远程显示名称、画面、剪贴板或
原始未知载荷。

## 8. 测试策略

实现遵循红—绿—重构，先增加当前实现必然失败的测试：

1. `003.889` 被解析为独立版本并原样回显；
2. 标准 3.8 仍回显 `003.008`；
3. ARD 初始化发送 `0xC1`，标准初始化仍发送 `0x01`；
4. Extended ServerInit 正确解析每个标志、16 字节能力位图和显示名称；
5. 缺少扩展头或 `mayControl` 时返回对应错误；
6. ViewerInfo、SetMode、SetDisplay 和 SessionCommand 与固定字节向量完全一致；
7. Session Select 覆盖直接获准、等待后获准、请求控制台、拒绝、截断和超限长度；
8. 初始化报文顺序严格为 ViewerInfo、SetMode、SetDisplay、像素格式、编码和首次刷新请求；
9. Ack 与 NOP 被消费后，下一条 framebuffer 消息仍能正常读取；
10. `RfbClient` 和 `ProbeRunner` 均把 `V3_889` 上下文传入同一初始化器；
11. 现有 3.3、3.7、3.8、认证、抓帧、输入和桌面测试无回归。

完成实现后运行完整解决方案测试、Release/x64 编译和协议探针编译。自动化测试只证明报文字节和
状态机符合设计，不能代替真实 macOS 26.5 控制验证。

## 9. 实机验收

在不经过 RDP 的 Windows 本地会话连接目标 macOS 26.5，满足以下条件才算解决：

1. WinARD 诊断显示协商版本为 `003.889`、ClientInit 为 `0xC1`，且服务端包含 `mayControl`；
2. macOS 将连接显示为控制/辅助模式，而不是观察模式；
3. 鼠标移动、点击和键盘输入能够操作当前控制台；
4. 多显示器组合画面保持可见，输入坐标不需要扫描或试点；
5. 断开重连后结果一致，标准 RFB 回归测试仍通过。

如果第 1 条成功而第 2 条失败，应保存新的脱敏诊断并回到协议证据调查，不直接转向坐标或 UI 修补。

## 10. 互操作与许可证

`noVNC-ARD` 使用 MPL-2.0。本项目仅依据公开线格式、行为描述和测试向量进行独立 C# 实现，不复制
其 JavaScript 源码结构或表达。若后续必须复制或改编其代码，应先评估并履行 MPL-2.0 的文件级源码
公开和版权声明义务，同时更新 `THIRD-PARTY-NOTICES.md`。
