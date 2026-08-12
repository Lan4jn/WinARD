# 持久压缩流跨 PixelFormat 切换设计

## 背景与根因

诊断 `WinARD-diagnostics-20260812-084246.zip` 显示：ARD 控制会话和输入写入均成功，自动画质从 Q0 切到 Q1 后，首个 Zlib rectangle 在 `FramebufferRectanglePayload` 阶段发生 `DecoderFailure`。

RFB Zlib 与 ZRLE 的 inflater 是连接级状态。当前质量切换会创建新的 `FramebufferUpdateSession`，从而同时创建新的 inflater；Mac 继续发送原连接压缩流的下一段时，新 inflater 缺少既有字典并失败。

## 目标

- Q0（BGRA32）与 Q1（RGB565）在线切换时保留同一连接的 Zlib/ZRLE inflater。
- 在安全 framebuffer 边界只切换像素解释规则，不重置压缩字典。
- 继续保持单 outstanding request、单 receive、一次 full repair 和 wire 写入后的 fail-closed 语义。
- 不加入“失败后猜测重置 inflater”或针对 Mac payload 的兼容分支。

## 方案选择

采用“复用现有 `FramebufferUpdateSession` 并原子重配置 PixelFormat”。不采用跨 session 共享 inflater lease，因为它会引入双重释放和所有权转移；也不采用关闭在线切换，因为那只能规避而不能修复根因。

## 组件设计

### 可重配置 decoder

为依赖 PixelFormat 的标准 decoder 建立内部可重配置契约。`Raw`、`Cursor`、`Zlib`、`ZRLE` 在 session gate 内接受新 PixelFormat。Zlib/ZRLE 只更新后续 rectangle 的预期 wire 长度和像素转换格式，其 `PersistentZlibInflater` 实例保持不变。

### FramebufferUpdateSession

新增异步 PixelFormat 重配置操作：

1. 获取 session gate，保证没有并发 decode；
2. 验证 session 仍为 Active；
3. 预先验证所有 decoder 能接受目标格式；
4. 在同一临界区提交所有格式更新；
5. 不更换 decoder dictionary，不释放持久 inflater。

如果验证失败，零 wire、零局部提交。session fault/dispose 后拒绝重配置。

### RfbClient 质量事务

`ApplyQualityTransitionAsync` 不再为标准 PixelFormat 变化创建新 framebuffer session。事务顺序为：

1. 在现有生命周期门下确认安全边界；
2. 预验证本地 session 重配置；
3. scheduler 中发送 `SetPixelFormat`，必要时发送 `SetEncodings`；
4. 在同一 scheduler work item 中提交本地 session PixelFormat；
5. 发送一次非增量 full repair；
6. 更新 current settings 与 outstanding 状态。

一旦 wire 输出开始，取消、本地提交失败或 repair 失败均 fault scheduler/连接；不得继续使用可能不一致的会话。

## 测试设计

- Zlib：同一 compressor 先生成 BGRA32 chunk，flush；再生成 RGB565 chunk，flush。一个 session 解码第一段，重配置后解码第二段并验证像素。
- ZRLE：使用同一 zlib stream 生成两个不同 PixelFormat 的合法 tile chunk，执行同样验证。
- session：重配置不替换持久 decoder/inflater；dispose、fault、取消和非法格式保持既有状态语义。
- RfbClient：先消费 Q0 持久 Zlib chunk，再应用 Q1 transition，repair response 使用同一 stream 的第二 chunk并成功呈现；只发送一次 full repair。
- 回归：部分 wire 写入、取消、cleanup、outstanding request 与 scheduler 公平性测试继续通过。

## 安全与诊断边界

- 保留压缩/解压、framebuffer work budget、精确 decompressed length 和原子 rectangle commit。
- 不记录压缩 payload、像素、主机或输入内容。
- Apple 1002/1001、服务端缩放和灰度门不受本修复影响。

## 成功标准

- 连续 Zlib/ZRLE 流跨 BGRA32→RGB565 和 RGB565→BGRA32 均可解码。
- 自动 Q0↔Q1 不因 inflater 重建断线。
- 全仓测试、warnings-as-errors build、format、便携版打包与产物验证通过。
- macOS 实机复测仍需用户执行；未执行时明确标为未验证。
