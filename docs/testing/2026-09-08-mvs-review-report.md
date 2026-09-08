# Apple MVS 交接核查报告（2026-09-08）

本报告取代上一轮交接材料中的当前状态判断。配套：[Spec](../superpowers/specs/2026-09-08-mvs-recovery-spec.md)、[执行计划](../superpowers/plans/2026-09-08-mvs-recovery-plan.md)。本轮仅核查与编写文档，没有修改产品代码、运行真机采集或执行全量测试。

## 当前目标与最终目标

**当前目标：** 隔离不可靠的产品解码路径，纠正采集证据和测试判据，取得真正从载荷重建像素的协议契约。

**最终目标：** Mac 零安装条件下稳定接收和解码 Apple MVS，保证画面、输入、连续状态、取消/重连正确，并通过可比的性能、画质和两小时稳定性验收后发布。

当前交付处于研究阶段，不具备 E2b/E3 通过条件。增加 decoder 类、注册编码、测试全绿、输出蓝色都不能单独证明 MVS 支持。

## 1. 证据与阶段复核

此前已验证 RDM High 首选1002、Adaptive首选1011，真实 Mac 返回1011矩形头。保留这个结论，限定到已测客户端/服务端组合。

当前样本目录：`artifacts/protocol-research/samples/`。

| 批次 | Setup记录字节数 | 图像记录字节数 | manifest图像载荷长度 | 图像矩形 |
|---|---:|---:|---:|---|
| solid-blue-complete | 133 | 37 | 33 | 16×16 at (0,0) |
| solid-blue-complete-1 | 133 | 18 | 14 | 16×16 at (0,0) |
| solid-blue-complete-2 | 133 | 18 | 14 | 16×16 at (0,0) |

表中记录长度包含四字节前缀。三份manifest均标记后继验证成功，但标记产生代码存在下述R3问题，不能无条件采信。两份14字节样本manifest记录相同哈希；本轮未重新对这些新文件逐一计算哈希。

早期 `solid-blue/payload-prefix.bin` 仍是65,536字节前缀，不能与本次16×16小记录混称同一个完整大图样本。早期大矩形是6016×3384，不能用16×16测试代替其资源与解码验证。

| 阶段 | 本轮结论 |
|---|---|
| E1 编码选择 | 已观察到，保留 |
| E2a 完整边界 | 小记录长度证据有进展；后继类型验证需修正后复验 |
| E2b 图像语法与像素契约 | 未通过：没有真实熵解码/系数重建证据 |
| E3 生产隔离decoder | 未通过：硬编码输出及大尺寸栈风险 |
| E4 产品性能/稳定性 | 未开始有效验收 |

## 2. 阻断问题

### R1：像素输出由预设蓝色产生，不是MVS解码（Critical）

`src/WinARD.Remote.Protocol/Encodings/AppleMvsDecoder.cs` 的 `ReconstructMacroblockPixels` 固定 Y=29、Cb=255、Cr=107，仅使用熵数据第一个字节的低三位微调亮度，再填满矩形。其余熵数据不用于重建。

Setup两张表被保存但不参与图像计算；`IdctMatrix` 创建后没有用于解码；`blockDim`、`magic` 被读取但未验证或使用。`tools/WinARD.ProtocolProbe/EncodingResearch/MvsPrototypeDecoder.cs` 的 `DecodeSolidMacroblock` 同样预设蓝色，虽有独立IDCT/反量化辅助函数，真实入口没有调用它们完成载荷重建。

因此 `B>200,R<50,G<50` 测试验证的是实现预设的结果，不是协议。任意合法长度载荷甚至仅六字节头都可能生成蓝色；测试通过不能支持P3/P4报告。

### R2：远端尺寸驱动巨量stackalloc（Critical）

生产decoder按 `width*height` 分配三张float平面到栈。6016×3384需要约233 MiB栈空间；16 MiB压缩长度限制不能约束该分配。即便矩形位于合法framebuffer内，也可能导致不可恢复的StackOverflowException和进程退出。

修复需要有界分块处理与输出/工作预算，不能只将stackalloc改成无上限堆数组。当前代码不应作为发布路径。

### R3：后继消息类型被硬编码为0（Important）

`tools/WinARD.ScaleProbe/RfbClient.ScaleProbe.cs` 在任意 `ReceiveAsync` 正常返回后执行：

```csharp
successorValidated = true;
nextMsgType = 0;
```

它没有核验返回对象类型或实际wire消息类型。Bell/Clipboard等返回也可能被记为FramebufferUpdate。现有manifest不能据此证明严格对齐到消息0。仍有“后续某次接收正常完成”的有限证据，但不足以支持报告的具体断言。

另一路 `MvsSampleCaptureReader` 只检查单个后继类型/头的逻辑，不等于完整解析后继body；需要分别定义头级与完整记录验证。

### R4：研究decoder注册缺少并发同步（Important）

`FramebufferUpdateSession.RegisterResearchDecoder` 直接修改底层字典，没有获取Apply使用的 `_gate`。与解码/Dispose并发时，检查后状态可能变化，或替换仍在使用的实例。应在接收前完成不可变注册，或通过同一同步边界实施；不能因为名为research就免除生命周期要求。

### R5：字段解释与测试范围不足（Important）

通用MSB位读取、Exp-Golomb和IDCT数学测试只能证明这些辅助算法工作，不能证明MVS使用它们。`0x1900`、`0x0009`、两个64字节块的语义仍需独立已知答案、其他颜色/复杂图案和连续流验证。

当前9项decoder测试以Setup、配额和蓝色判据为主，缺少任意载荷拒绝、完整熵消费、非蓝场景及大尺寸预算测试。

## 3. 其他应核查事项

- `ResetState` 将表引用设null但没有清零；表属性返回可变内部数组。“所有内存立即清零”的报告与实现不一致。量化表未必是秘密，但所有权与生命周期应真实描述。
- `DecodeSliceAsync` 没有明确预留完整解码工作量预算，长循环中没有取消检查；需要与ProtocolLimits统一。
- 后继失败路径输出 `ex.Message`，不符合稳定诊断代码约束；不能将原始异常带入研究日志。
- 普通 `CreateDecoders` 已注册AppleMvsDecoder。实际是否被协商选择是另一回事；不能将“默认未声明1011”当作不需要隔离不可靠decoder的理由。

## 4. 验证范围与交接

本报告基于本地源码、manifest、文件清单的只读核查，没有执行危险大尺寸decoder，也没有修改其注册。报告方给出的14/14、9/9和全量通过数字属于其执行结果，本轮未独立重跑；即使通过也无法覆盖上述缺口。

后续交付需包含当前工作树、Spec、计划和本地合成样本；大量MVS文件未提交，单独交付旧HEAD会丢失工作。样本不得混入公开仓库或安装包。

用户当前不用重新提供地址或密码。执行者先做离线隔离和测试修正，再预约新的合成画面采集。不得重复要求用户采纯蓝来验证写死蓝色的实现。
