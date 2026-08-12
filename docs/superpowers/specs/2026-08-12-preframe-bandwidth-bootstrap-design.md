# WinARD 首帧前低带宽协商与单次安全回退设计

日期：2026-08-12
状态：方案 B 已批准，书面规格待审阅
前置提交：`90dc5e6` 关闭生产会话中的在线 PixelFormat 切换

## 1. 背景与证据

诊断 `WinARD-diagnostics-20260812-130151.zip` 证明当前版本已经能够稳定连接和控制，但没有真正应用界面显示的 Q1/Color16：

- 生产能力 `SafeOnlinePixelFormatSwitch=false`，会话开始传输后不会发送 `SetPixelFormat`；
- 会话仍使用初始化时的 BGRA32，编码为 Zlib；
- 实际约 2 FPS，最近 5 秒平均 4,270,031 B/s，峰值 6,219,354 B/s；
- 当前带宽目标为 `null`，即用户选择“不限制”，因此 `targetSatisfied=true` 只表示没有数值目标，并不表示带宽较低；
- 没有协议、解码或控制输入错误。

这说明当前瓶颈是单次 framebuffer 更新体积，而不是请求频率。Windows 客户端在收到画面后再压缩不能减少 Mac 已上传的字节；在“Mac 端零安装”约束下，降载必须在首帧前通过 Mac 原生 RFB 能力完成。

## 2. 目标与非目标

### 2.1 目标

1. 在 RFB 初始化完成后、任何 framebuffer 请求产生前，一次性配置初始 PixelFormat 和编码优先级。
2. 优选组合使用 RGB565 与 `ZRLE → Zlib → Raw`，降低单帧传输量。
3. 如果优选组合的首个 framebuffer 响应发生可归因于画质协商的协议/解码错误，销毁整个连接并以安全组合自动重试一次。
4. 安全组合固定为 BGRA32 与 `Zlib → ZRLE → Raw`；回退成功后不再尝试优选组合。
5. 会话传输开始后继续禁止在线 16/32 位切换。
6. UI 和诊断同时展示用户期望画质与实际已应用画质，不再把未应用的 Q1 当作真实线路状态。
7. 增加有界、脱敏的矩形传输统计，以区分“大脏区”和“压缩效率低”。

### 2.2 非目标

- 不安装 Mac 端代理、虚拟显示器或编码器；
- 不实现、猜测或分发 Apple MVS/RDM 私有编解码器；
- 不重新开启在线 PixelFormat 或服务端缩放切换；
- 不承诺固定达到 RDM 的 1–2 MiB/s；
- 不把刷新率当作本轮主要降载手段；
- 不在损坏的同一字节流上切换解码布局或重置 inflater 猜测恢复。

## 3. 方案选择

采用已批准的方案 B：“首帧前优选协商 + 新连接单次安全回退”。

不采用以下方案：

- 仅调整编码顺序：风险最低，但无法消除 BGRA32 的高像素字节量；
- 会话内多组合探测：会重复触碰已证伪的在线 PixelFormat 切换，并使持久压缩流边界不可信；
- 收到画面后本地再压缩：只减少本地内存或后续处理量，不减少 Mac 上传带宽。

## 4. 初始画质计划

新增不可变的连接启动计划，明确区分“用户期望”和“本次连接实际应用”：

```text
Preferred: PixelFormat + ordered encodings + source reason
Fallback:  PixelFormat + ordered encodings + fallback reason
Attempt:   Preferred | Fallback
```

### 4.1 PixelFormat 选择

优选连接按用户意图选择：

| 用户设置 | 优选 PixelFormat |
|---|---|
| 原画或显式锁定 32 位 | BGRA32 |
| 平衡、流畅、显式 16 位 | RGB565 |
| 自动或自定义“自动色彩” | RGB565 |
| 灰度请求但 Apple 灰度证据门未通过 | RGB565，并报告能力受限 |

自动模式选择 RGB565，是因为其默认目标为 2 MiB/s，而当前 BGRA32 实测已在 2 FPS 时达到约 4.27 MB/s。用户明确选择原画/32 位时不得覆盖。

安全回退始终使用 BGRA32。

### 4.2 编码顺序

优选组合：

```text
ZRLE(16), Zlib(6), Raw(0), CopyRect(1), Cursor(-239), DesktopSize(-223)
```

安全回退：

```text
Zlib(6), ZRLE(16), Raw(0), CopyRect(1), Cursor(-239), DesktopSize(-223)
```

服务器可以忽略顺序并选择任一已声明编码。客户端以首帧实际 `EncodingCounts` 作为实际编码证据，不从请求顺序推断。

## 5. 协议时序与原子性

单次连接尝试采用以下顺序：

1. 协议版本、安全类型 30、认证和 ARD 会话初始化完成；
2. 创建与启动计划 PixelFormat 一致的 framebuffer 和 decoder session；
3. 在任何 `FramebufferUpdateRequest` 之前写入一次 `SetPixelFormat`；
4. 写入一次 `SetEncodings`；
5. 发布不可变的已应用启动设置；
6. 写入一次全屏非增量 framebuffer 请求；
7. 读取并验证服务器消息，直到得到首个像素 framebuffer；
8. 将验证过的首批消息按原顺序交给正常会话消费，不丢弃首帧、光标或 ARD 元数据。

启动配置只允许执行一次。配置写入开始后的取消、异常或局部写入都使本连接不可信，必须销毁连接。启动成功后，现有 `SafeOnlinePixelFormatSwitch=false` 继续生效。

## 6. 首帧验证与单次回退

首帧验证在连接尝试层完成，因此失败时用户名/密码不会进入 UI 或诊断，第二次尝试仍通过现有安全凭据提供器重新获取秘密。

只有以下“启动画质兼容失败”允许触发安全回退：

- 首个像素 framebuffer 的 `DecoderFailure`；
- 首个像素 framebuffer 的 `UnsupportedEncoding`；
- 首个像素 framebuffer 的 `MalformedFramebufferUpdate`，且读取阶段位于 rectangle header/payload；
- 在首个像素 framebuffer 前，服务器明确关闭了已完成认证的会话。

DNS、TCP、SSH、认证、主机密钥、用户取消、普通超时和输入错误不触发画质回退，仍走现有错误处理。

回退规则：

1. 优选尝试失败后完整释放 client、decoder、加密会话和 transport；
2. 使用同一连接配置建立一个全新的 RFB/ARD 连接；
3. 使用 BGRA32 + `Zlib → ZRLE → Raw`；
4. 最多回退一次，不形成重连循环；
5. 回退也失败时返回第二次失败，并在脱敏字段中保留 `PreferredFailureReason` 和 `FallbackFailed=true` 的闭集状态，不包含原始异常消息。

## 7. 首帧缓冲所有权

连接处理器在返回 `RemoteSession` 前已经消费了首批服务器消息，因此 `RemoteSession` 持有一个有界的预加载队列：

- 队列按 wire 顺序保存首帧之前的光标、元数据和首个 framebuffer；
- 正常 `ReceiveAsync` 先排空预加载队列，再读取 client；
- 队列上限使用现有消息/内存预算；不建立第二套无限缓存；
- 连接失败、取消或 dispose 时必须释放所有像素 owner 和 cursor owner；
- framebuffer outstanding 状态在预取完成后已清除，正常循环在呈现首帧后才请求下一个增量帧。

## 8. 期望状态与实际状态

新增连接级实际画质快照，至少包含：

- 启动尝试：Preferred 或 Fallback；
- 实际 PixelFormat：BGRA32 或 RGB565；
- 请求的编码顺序；
- 首帧实际主编码：从 `EncodingCounts` 得到；
- 是否发生安全回退；
- 在线 PixelFormat 是否可切换（本阶段固定 false）。

`QualityDecision` 继续表达控制器期望，不再被用作实际线路状态。运行时展示示例：

```text
期望：16 位 · 100% · 30 FPS
实际：16 位 · ZRLE · 2 FPS · 1.8 MiB/s
```

回退时：

```text
期望：16 位 · 100% · 30 FPS
实际：32 位 · Zlib（已安全回退）· 2 FPS · 4.1 MiB/s
```

当期望与实际 PixelFormat 不一致时，状态为“本连接已安全回退”，而不是“已达到目标”。用户保存的 Color16 设置保持不变，下次连接仍可按优选组合尝试；单次会话内不自动再次尝试。

## 9. 矩形传输统计

协议解码阶段为 Raw、Zlib 和 ZRLE 产生仅含数值的瞬时记录：

- 编码稳定 ID；
- wire payload 字节数；
- 解压后的像素 wire 字节数；
- 像素面积；
- 是否产生像素内容。

不记录矩形坐标、压缩 payload、解压字节或像素。FramebufferUpdate 汇总并向上层传递：

- 矩形数量；
- 压缩/原始 payload 总字节；
- 像素面积总和；
- framebuffer 脏区比例；
- bytes per pixel，使用有界浮点值并在除数为零时省略；
- 每种编码的矩形数和字节数。

普通诊断只导出最近窗口的聚合值和闭集编码名，不导出逐矩形数组，避免通过尺寸序列侧信道推断具体屏幕布局。内存中逐矩形记录只存活到本次 update 汇总完成。

## 10. UI 与诊断

### 10.1 UI

- 画质摘要以“实际”状态为主，期望值在面板中单列；
- 32/16 位选项仍保存用户意图；如果当前会话已开始，则文字提示“下次连接生效”；
- 回退状态不弹阻塞对话框，不自动中断已经可用的安全连接；
- 当前编码显示首帧及后续观测到的实际编码。

### 10.2 诊断

新增严格 allowlist 字段：

- `PreferredPixelFormat`、`AppliedPixelFormat`；
- `BootstrapAttempt`、`BootstrapFallbackReason`；
- `PreferredEncodingOrder` 的稳定枚举 ID，不导出任意字符串；
- `RectangleCount`、`PixelArea`、`WirePayloadBytes`；
- `BytesPerPixelMilli`（每像素字节数乘 1000 的非负整数）；
- `DirtyCoveragePermille`；
- `DesiredColor`、`AppliedColor`。

现有 `color` 字段改为实际色彩；期望值使用独立字段。所有新字段同时经过字段名和字段值/数值范围双层 allowlist。ZIP 负向测试扫描所有 entry，禁止主机、用户名、密码、输入、坐标、路径、payload、像素和原始异常消息。

## 11. 带宽控制行为

- 用户选择“不限制”时继续不做带宽超限降档；UI 明确显示“不限制”，不再把“已达到目标”解释成低带宽；
- 用户设置 1/2/4/8/16 MiB/s 或自定义目标时，控制器只能调整当前连接仍安全的刷新率；
- 需要改变 PixelFormat 或缩放的决策返回“下次连接生效”，不会在当前连接写线；
- 本轮不自动替用户把“不限制”改成 1/2 MiB/s。

## 12. 测试策略

实现严格遵循 TDD，至少覆盖：

1. 启动计划从预设、显式色彩、锁定和灰度能力中得到确定 PixelFormat。
2. 首个 framebuffer 请求之前的 wire 顺序精确为 `SetPixelFormat → SetEncodings → non-incremental request`。
3. 优选编码顺序为 ZRLE-first，安全回退为 Zlib-first。
4. RGB565 首帧经 ZRLE、Zlib 和 Raw 均能正确解码。
5. 优选首帧协议/解码失败释放全部资源并只重试一次。
6. DNS/TCP/SSH/认证/取消失败不触发画质回退。
7. 回退连接使用新 transport、新加密会话和新 inflater，不复用损坏状态。
8. 首帧及前置 cursor/metadata 按序预加载且所有权、取消和 dispose 无泄漏。
9. 启动成功后任何在线 PixelFormat 变化仍返回 `ReconnectRequired` 且 wire 零变化。
10. 期望/实际/回退 UI 文案、无障碍名称和原子 snapshot 一致。
11. Raw/Zlib/ZRLE 矩形统计的字节、面积、聚合、零面积和溢出边界。
12. 诊断字段 allowlist、任意值拒绝和 ZIP 全 entry 隐私扫描。
13. 旧 `RemoteUpdateStatistics`、`RfbProtocolFailureInfo` 和公开构造/解构 API 保持兼容。

## 13. 验收标准

自动化交付标准：

- 全仓测试通过；
- Release x64 warnings-as-errors 为 0；
- `dotnet format --verify-no-changes` 通过；
- 便携版和 MSIX 重新生成并通过发布校验；
- 规格与质量审查 Critical/Important 清零。

macOS 26.5 实机验收：

1. 当前 Custom/Color16 配置首帧前实际应用 RGB565；
2. 首帧实际编码优先出现 ZRLE；若 Mac 选择 Zlib，诊断准确反映实际值；
3. 相同分辨率和操作脚本下，平均带宽低于本次基线 4,270,031 B/s；
4. 优选组合不兼容时只发生一次透明安全回退，随后连接、画面和控制可用；
5. 回退后的 UI 明确显示实际 32 位，不再显示为已应用 16 位；
6. 新诊断能用 wire bytes、pixel area、dirty coverage 和 bytes/pixel 区分瓶颈；
7. 未执行实机验收前不得宣称带宽目标已经达到。
