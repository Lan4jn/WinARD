# Apple MVS 开发交接报告

> 当前状态见 [2026-09-08 核查报告](2026-09-08-mvs-review-report.md)：新增实现存在硬编码蓝色输出、面积驱动栈分配和后继消息证据失真问题。本文件保留前一阶段记录。

日期：2026-09-07。范围：离线核对现有证据与制定后续开发交接；本轮未连接 Mac、未新增采集、未实现解码器。

## 阅读顺序与目标

1. 本报告：确认事实、假设和当前代码风险。
2. [交接 Spec](../superpowers/specs/2026-09-07-apple-mvs-handoff-spec.md)：确定行为与验收契约。
3. [执行计划](../superpowers/plans/2026-09-07-apple-mvs-handoff-plan.md)：逐项实施并记录证据。

**当前目标：** 把已确认的 1011 服务端选择和有限样本，推进为完整、可重复验证的载荷边界与最小像素解码契约。

**最终目标：** WinARD 在 Mac 零安装前提下稳定解码 Apple MVS，正确呈现画面与输入，在同负载下验证画质、帧率和带宽取舍，提供真实实际编码状态与安全兼容回退。

完整采集、能解码首帧、连续解码、产品可用、性能达标是不同里程碑，不能相互替代。

## 1. 已核实证据

官方 RDM 文档将 Adaptive quality 映射为 Apple MVS codec；High 为 zlib 16-bit。Resolution quality 单独配置。

来源：https://docs.devolutions.net/rdm/knowledge-base/knowledge-base-articles/entry-settings/apple-remote-desktop-ard

本机此前核对 RDM 版本为 2026.2.17.0。回环探针观察到：

| 用户设置 | 初始 SetEncodings |
|---|---|
| High / Default | `1002,6,0,-239,1104,1100,1101,1105,-223` |
| Adaptive / Default（用户纠正设置后） | `1011,6,0,-239,1104,1100,1101,1105,-223` |

两组均完成认证、ClientInit、ServerInit、ViewerInfo、SetMode 和 SetEncodings，未到首个 framebuffer 请求。早期用户忘记切换 Performance 的样本已排除，不能用于对比。

独立探针在真实 Mac 的标准加密会话后声明 1011，生产 reader 返回 `UnsupportedEncoding / FramebufferRectangleHeader / EncodingId=1011`。这确认了真实服务端选择 1011，不证明载荷已正确解码。基线 framebuffer 为 9376 × 3384。

关联记录：[真实声明与服务器选择](2026-09-06-mvs-live-declarations.md)。其中后追加的算法结论须按本报告区分事实与假设。

## 2. 本地样本核验

根目录：`artifacts/protocol-research/samples/solid-blue/`。本轮读取 manifest、检查文件大小并重新计算 SHA-256，均与报告一致。

| 样本 | 文件 | 大小（含前四字节） | 头部按 uint32 大端解释 | 矩形 |
|---|---|---:|---:|---|
| Setup 候选 | `setup/payload-prefix.bin` | 133 | 129 | `(0,0) 0×0` |
| 图像候选前缀 | `payload-prefix.bin` | 65,536 | 364,783 | `(3360,0) 6016×3384` |

哈希：

```text
Setup: C02CECE7B757BC6AF2C5CA3A74A4C0D798B375A27EEC3CC2DB9BD9FAF3B7FBAD
Image prefix: 98E95F38EB7ACFA7725C6ACBA0D1BE14689A47FB9ABB464A56DC5D2AE1A76D18
```

若图像头确为“不含前缀的载荷长度”，完整文件应为 **364,787 字节**；现存文件缺 **299,251 字节**。现存图像内容为 65,532 字节加四字节头，不是完整图像帧。`manifest.json` 仍明确使用 `heuristicBigEndianLength` 和 `Unknown / Raw Payload Header`。

Setup 的长度与文件一致，且采集器继续读到了后续矩形，对该次长度解释提供初步支持。但工具本身按此假设消费字节，仍应通过完整图像后继边界及多次独立样本排除偶然对齐。

## 3. 结论可信度

| 结论 | 当前判定 | 还需要什么 |
|---|---|---|
| 1011 与 RDM Adaptive/MVS 关联，Mac 实际选择 | 已有交叉证据 | 保留版本/会话范围，不泛化全部系统 |
| 0×0 控制记录后有非零图像矩形 | 已观察 | 多次序列验证及其他控制模式 |
| 四字节大端长度 | 有力候选 | 完整消费图像并验证后继矩形/更新边界 |
| `0x02` 表示两张量化表 | 假设 | 字段来源、不同质量/图案样本、解码验证 |
| 两组 64 字节是 JPEG 亮度/色度表 | 假设 | 逐项比较、顺序/精度、实际像素验证 |
| 图像为标准 JPEG/DCT 流 | 未证实 | 完整图像语法、熵编码、块组织及重建结果 |
| YUV 4:2:0 或 4:2:2 | 未证实 | 彩色边缘测试与分量/抽样解析 |
| Setup 只发一次、跨帧引用、尺寸变化重置 | 未证实 | 连续变化、再次连接与尺寸变化序列 |
| 画质无损或带宽显著改善 | 未证实 | 正确解码及相同负载下的质量/性能比较 |

不得把“内容形似量化表”转换成对 JPEG、DCT、视频算法或无损性的确定声明。

## 4. 当前代码与已知检查点

HEAD：`3a810c59a447a109fde725143dfb616ac644396f`。工作树包含大量前序未提交修改和未跟踪文档/工具；提交号本身不包含全部交接工作。后续执行者必须同时接收工作树或针对性补丁。

- `tools/WinARD.ScaleProbe/Program.cs`：复用保存连接，支持选择探测和载荷前缀采集。不得在交接材料中输出真实地址、账号、数据库设备 GUID 或秘密。
- `tools/WinARD.ScaleProbe/RfbClient.ScaleProbe.cs`：通过反射访问 `FramebufferUpdateSession._decoders` 并原位注入采集 decoder，以保留旧 Zlib 字典。方向合理，但反射耦合不适合作为稳定产品接口。
- 同文件按启发式四字节长度消费 Setup；图像最多保存 64 KiB 并用专用异常结束连接。没有完整图像消费和后继边界验证。
- `MvsSampleCaptureReader.cs`：另一前缀读取路径不处理同样的 Setup 情况；与 live capture 行为不同，不能因该类单测通过就认为 live 路径全部覆盖。
- `WriteSampleAsync` 使用 `FileMode.Create` 分别写 payload/manifest，可能覆盖旧样本或留下不完整配对。下一采集前应补唯一批次、拒绝覆盖和事务式发布。
- `FramebufferUpdateReader.cs` 把全部 AppleMvs 当作 metadata，跳过普通非零检查并使用 metadata rectangle。需要区分“合法零尺寸控制记录”和“必须做像素边界验证的非零图像矩形”。
- `RfbEncodingType.AppleMvs = 1011` 已存在，不等于注册了产品解码器。生产 negotiation 与 decoder 注册都需核对。
- ARD 加密层可能缓冲整个包。“不解释/不保存未知载荷”比“socket 未读取载荷”准确。

## 5. 测试与构建声明

上游提供的报告声明 2470/2470、Release 零警告、format 通过。本轮未重新运行这些命令，也未核对同一工作树对应的完整测试日志，因此列为**报告方提供的结果**。本轮独立核实的是文件、哈希、manifest 和上述代码行为。

单元测试不能替代真实图像正确性或跨帧契约。后续验证必须覆盖实际采集路径和未来 decoder，不只验证人工构造的前缀模型。

## 6. 交接清单与用户配合

交付三份本文档、现有源码工作树及两份样本与 manifest。样本是合成画面报告下的本地研究材料，仍不放入公共仓库或安装包；若传给其他人，由用户选择安全传递方式。

现在用户无需补密码、地址或重复 RDM 声明测试。待采集工具通过离线测试后，后续开发者提供测试素材和明确采集指令；用户优先只启用一个显示器，或覆盖全部捕获区域，关闭通知预览，确认开始本次有界采集。

本报告不启动后续实现、网络测试或上传操作。后续实施按用户另行交给执行者的授权进行。
