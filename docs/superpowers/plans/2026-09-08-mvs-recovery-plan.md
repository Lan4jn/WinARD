# Apple MVS 后续修正与开发计划

> 完成声明复核后的当前执行入口：[真实通用解码计划](2026-09-08-mvs-general-decoding-plan.md)。本文件保留上一轮问题背景。

日期：2026-09-08。当前执行入口；配套[报告](../../testing/2026-09-08-mvs-review-report.md)、[Spec](../specs/2026-09-08-mvs-recovery-spec.md)。旧计划保留背景，不继承其P3/P4完成标记。

**当前目标：** 隔离占位decoder、修正证据记录、恢复真正的位流到像素验证。  
**最终目标：** Mac零安装、正确稳定且性能经实测的MVS产品支持。

本轮只交接文档。后续执行使用Superpowers系统化排查、TDD和新鲜验证；保留其他未提交工作，不做无关重构。

## R0：确认基线与限制影响

- [ ] 保存HEAD、工作树差异与当前测试命令。先读报告列出的真实入口，不以类名或注释判断功能完成。
- [ ] 针对普通工厂默认注册1011写失败测试，期望在真实decoder就绪前结构化拒绝。
- [ ] 取消普通 `FramebufferUpdateReader.CreateDecoders` 中当前AppleMvsDecoder注册；研究入口显式注入。保留源代码供追溯，但移除“生产级已完成”的声明。
- [ ] 验证标准Zlib/ZRLE、未知编码拒绝和研究采集仍按预期工作。不运行大尺寸旧decoder来证明栈溢出。

文件：`src/WinARD.Remote.Protocol/Framebuffer/FramebufferUpdateReader.cs`、`Encodings/AppleMvsDecoder.cs`、`tests/WinARD.Remote.Protocol.Tests/Encodings/AppleMvsDecoderTests.cs`、`Framebuffer/AppleMvsRectangleValidationTests.cs`。

## R1：建立能识别占位输出的测试

- [ ] 在隔离测试中向当前decoder提供有效Setup与仅六字节任意头，期望拒绝；先确认当前错误地生成像素。
- [ ] 改变magic、尾随熵数据、量化表，检查当前实现忽略哪些输入。测试目的为暴露未实现语义，不假定任意bit变化都必须改变像素。
- [ ] 将“输出主导蓝色即成功”测试降为旧原型回归记录，不作为MVS解码验收；新增真实非蓝与复杂图案fixture的独立预期。
- [ ] 在协议尚未解释前，让未实现路径明确拒绝，不用另一套硬编码值让测试变绿。

文件：`AppleMvsDecoderTests.cs`、`EncodingResearch/MvsSyntaxAnalysisTests.cs`、`tools/WinARD.ProtocolProbe/EncodingResearch/MvsPrototypeDecoder.cs`。

## R2：修正后继边界证据与研究注册

- [ ] 为live采集路径增加失败测试：Receive返回Bell、Clipboard、Cursor、空更新、完整图像更新、截断与超时。检查manifest不将前四种一律写成消息0图像。
- [ ] 将 `successorValidated=true; nextMsgType=0` 替换为实际类型与解析级别记录；若高层返回对象不足以确定wire类型，用现有reader边界的最小结构化观察，不推断或硬编码。
- [ ] 两条采集路径共享验证规则，保留长度读完/头验证/body验证的不同级别。
- [ ] 修复 `RegisterResearchDecoder` 与Apply/Dispose竞争：优先会话创建时传入研究decoder，避免在线字典变更；若保留在线注册，则共享同一同步gate并明确替换资源所有权。
- [ ] 错误日志输出稳定码而非ex.Message；原始样本和旧manifest只读保留，新批次新目录。
- [ ] 离线回归通过后，按预算重新采集三个会话的多条连续记录，更新E2a状态。实际消息0必须有完整头/body解析证据。

文件：`tools/WinARD.ScaleProbe/RfbClient.ScaleProbe.cs`、`MvsSampleCaptureReader.cs`、`MvsSampleCapture.cs`、`FramebufferUpdateSession.cs`、`MvsSampleCaptureTests.cs`。

## R3：真实语法与像素重建契约

- [ ] 验证六字节头每个字段的意义，`0x1900`与9仅为观察值，直到来源/差分实验支持解释。
- [ ] 分别采纯色RGB/灰阶、渐变、彩色细线、文字、局部变化；预期图由生成器或独立参考确定。保留未用于推导的样本。
- [ ] 解析真实系数/预测/颜色数据，确定实际熵编码及尾部规则；若不是DCT路线，淘汰相关假设，不强行套JPEG。
- [ ] 建立从完整真实载荷到独立预期像素的测试，验证16×16、多块、非整块边缘与连续变化。不能调用填蓝函数制造答案。
- [ ] 只有表、字段、像素和连续状态全部可解释才通过E2b；记录不支持的模式与明确拒绝行为。

文件：`tools/WinARD.ProtocolProbe/EncodingResearch/MvsSyntaxAnalyzer.cs`、`MvsPrototypeDecoder.cs`、`MvsSyntaxAnalysisTests.cs`、`docs/protocol/ard-mvs-evidence.md`。E2b后补准确decoder算法子计划，不提前写未知语法。

## R4：有界隔离decoder

- [ ] 用R3已知答案先建立失败测试，再实现真实decoder。
- [ ] 删除面积驱动stackalloc，固定小块处理；checked验证输出与工作预算后分配。对大矩形做资源预算测试，先拒绝再分配，不测试不可恢复栈崩溃。
- [ ] 验证缺Setup、重复/错误Setup、未知头、截断、尾随非法数据、坏状态、取消、超配额、Dispose及会话隔离。
- [ ] 校验表所有权，避免公开可变内部数组；按用途清零与释放，长循环响应取消。
- [ ] 真实图像、连续流、边缘尺寸和输入验收通过，再审查E3；在此之前不恢复默认注册或产品声明。

## R5：产品接入与最终验收

- [ ] 加显式实验性选项，状态展示基于真实解码；旧设置与标准路径兼容。
- [ ] 验证统一fallback预算及运行期失步恢复，避免无限重连1011。
- [ ] 验证光标、四角/中心点击、组合键释放、剪贴板、尺寸变化与取消。
- [ ] 执行三类负载三轮30秒比较与两小时稳定性；无明确收益时保留显式选项，不默认自动优选。
- [ ] 全量测试、format、Release、许可证/漏洞及包内容验证；正式产物不含探针/样本/RDM DLL。
- [ ] 报告各阶段实际结果，附提交和工作树状态；不覆盖用户其他未提交变更。

## 验证命令

每次修复先运行对应定向测试；失败归因后修复，再扩大到相关回归。以下名称来自当前测试文件，不使用测试总数作为协议证据。

```powershell
git status --short
git rev-parse HEAD
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~AppleMvs|FullyQualifiedName~MvsSyntaxAnalysis|FullyQualifiedName~MvsSampleCapture|FullyQualifiedName~RdmCapture" -m:1
dotnet build tools/WinARD.ScaleProbe/WinARD.ScaleProbe.csproj -c Release -p:Platform=x64
```

最终交付：

```powershell
dotnet restore WinARD.sln -p:Platform=x64
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore -m:1
dotnet format WinARD.sln --verify-no-changes --no-restore
pwsh -File packaging/check-licenses.ps1
pwsh -File packaging/check-vulnerabilities.ps1
git diff --check
pwsh -File packaging/portable.ps1 -Version 0.1.0.0
pwsh -File packaging/verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
```

## 给用户与后续执行者的交接

当前用户无需额外采样或提供密码。执行者先做R0–R2离线工作，工具验证通过后再预约合成画面采集。用户提供当前工作树、样本目录与本文档三件套，不只交付HEAD；样本保持本地受控传递。

每阶段交付“改动、失败测试、修复、验证输出、证据范围、剩余问题”。R0生产隔离不等于MVS已修复，R2边界成功不等于R3像素契约成功。
