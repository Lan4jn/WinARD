# Apple MVS 真实通用解码开发计划

> 后续执行者按Superpowers的系统化排查、TDD、验证后结论执行。每任务先验证问题，再最小修改，保留其他工作树变更。本轮仅文档交接。

**当前目标：** 建立可信语法和独立像素验收，替换样本颜色映射。  
**最终目标：** Mac零安装、正确稳定并通过性能/发布验收的MVS支持。  
**架构：** 复用现有连接、加密、研究采集、framebuffer与输入管线；默认生产隔离保持到E3。  
**技术栈：** C#/.NET8、WinUI3、现有xUnit/PowerShell；不因MVS名称新增媒体依赖。

依据：[报告](../../testing/2026-09-08-mvs-completion-audit.md)、[Spec](../specs/2026-09-08-mvs-general-decoding-spec.md)。

## G0 接手与隔离检查

文件：`Framebuffer/FramebufferUpdateReader.cs`、`Encodings/AppleMvsDecoder.cs`、`Encodings/Mvs/MvsMacroblockParser.cs`（均在 `src/WinARD.Remote.Protocol/`）。

- [ ] 保存HEAD、git status和相关diff，确认默认工厂仍不注册1011；已有正确测试无需重复改写。
- [ ] 核实样本目录、manifest、哈希、采集版本、场景和原图；不要只交付旧提交，未跟踪文件也需移交。
- [ ] 重跑当前MVS定向测试，保存真实输出。绿灯只作基线，不作协议证明。
- [ ] 将完成报告中的不成立结论标注为被本次审计更新，保留历史记录与样本。

## G1 阻止猜测输出并建立反例

文件：`MvsMacroblockParser.cs`、`tests/WinARD.Remote.Protocol.Tests/Encodings/AppleMvsDecoderTests.cs`、`EncodingResearch/MvsSyntaxAnalysisTests.cs`。

- [ ] 新增失败测试：有效六字节头但无熵数据，应明确拒绝；当前返回灰色会暴露问题。
- [ ] 新增失败测试：未被证实的纹理模式不得进入genericY成功路径。只断言明确拒绝，不猜预期图像。
- [ ] 最小移除未知模式的估值成功返回。纯色特征匹配移出可用decoder，仅作研究假设记录；不得再添加新颜色特判。
- [ ] 测试通过并确认普通路径继续隔离；这一阶段可能减少可解码样本数量，不能通过保留假输出维持通过率。

下面的用例可直接加入测试类，用现有公开入口验证空熵拒绝（先红后绿）：

```csharp
[Fact]
public void Header_without_entropy_is_not_a_decoded_macroblock()
{
    byte[] payload = [0x00, 0x0F, 0x19, 0x00, 0x00, 0x0A];
    byte[] table = Enumerable.Repeat((byte)1, 64).ToArray();
    byte[] output = new byte[16 * 16 * 4];
    Assert.Throws<FormatException>(() =>
        WinARD.Remote.Protocol.Encodings.Mvs.MvsMacroblockParser.DecodeMacroblock(
            payload, table, table, output));
}
```

## G2 独立图像预期与样本覆盖

文件：`tools/Generate-SyntheticPatterns.ps1`、`tools/Analyze-MvsSamples.ps1`、`AppleMvsDecoderTests.cs`、`AppleMvsFullFrameTests.cs`、`docs/testing/2026-09-08-mvs-sample-analysis-report.md`。

- [ ] 先复用已有样本，核对它们对应的完整原图、采样坐标和矩形范围；仅看样本名字不能证明左上角应为何色。
- [ ] 为8×8棋盘、文字和彩色边缘建立整块预期，而非像素(0,0)判据；确认当前常量输出不通过。
- [ ] 将人工四块拼接测试标为“写入位置/组装测试”，不作为真实全帧语法证明。
- [ ] 原图缺失或覆盖不足时才准备新素材/采集；先提供工具和范围，再请用户确认无敏感内容。不要重复索取地址密码。
- [ ] 保留至少一组不用于语法推导的复杂样本作为验收集；事先定义误差与颜色范围。

## G3 位流语法研究与最小真实解码

文件：`MvsMacroblockParser.cs`、`tools/WinARD.ProtocolProbe/EncodingResearch/MvsSyntaxAnalyzer.cs`、协议证据文档。

- [ ] 对字段逐项建立“偏移/位序/含义/证据/适用模式”表；区分固定值观察与真实magic/QP等语义。
- [ ] 确定真实熵编码、系数/预测、块扫描、颜色分量、量化和padding。已有IDCT/Exp-Golomb工具只在证据匹配时使用。
- [ ] 完整解析一组支持模式的载荷；输出消费位数与合法尾部，未知模式明确失败。
- [ ] 从解析数据重建像素，移除固定颜色与经验估值；若量化表有实际作用，验证其正确进入重建。不能强迫量化表敏感性测试代替语义证据。
- [ ] 对独立复杂样本比较完整像素及空间结构。若结果不符，定位语法/颜色/状态问题，禁止按样本打补丁。
- [ ] E2b评审后写准确代码级算法子计划；仍缺字段则列出下一实验，不能将缺口报告标为实现完成。

## G4 大矩形与连续状态

文件：`AppleMvsDecoder.cs`、`AppleMvsFullFrameTests.cs`、`FramebufferUpdateSession.cs`、`tools/WinARD.ScaleProbe/RfbClient.ScaleProbe.cs`。

- [ ] 获取/复用完整真实大矩形，确认其内部块组织。此前6016×3384前缀不能作为完整fixture。
- [ ] 实现内部多块解析与非整块边缘处理；若不支持则安全拒绝并明确产品限制，不能重用同一块填全屏。
- [ ] 验证真实连续变化、Setup更新、缺失Setup、连接重置和尺寸变化；保存状态必须由会话独占。
- [ ] 在分配前验证输出/工作预算，固定小栈缓冲，长循环取消测试；失步不发布半帧。
- [ ] 后继验证记录真实对象/wire边界层次，cursor/空更新只证明相应事件，不计图像连续成功。关联修正版本的新鲜证据。
- [ ] 正确性、资源与真机输入审查通过才认定E3。

## G5 产品接入和性能验收

文件：`src/WinARD.Application/Quality/QualityBootstrapPlanner.cs`、`src/WinARD.Desktop/Services/RfbClientFactory.cs`、`ConnectionAttemptWorkflow.cs`、`ViewModels/QualityPresentation.cs`、`src/WinARD.Infrastructure/Diagnostics/DiagnosticExporter.cs`。

- [ ] E3后才加入显式实验选择，保存设置兼容，实际解码成功才显示MVS。
- [ ] 测试首选加一次安全连接的总预算、运行期失步、取消和安全重连；认证等确定错误不误回退。
- [ ] 验证真实大桌面、光标、四角/中心、组合键、剪贴板及尺寸变化。
- [ ] 按Spec完成同负载三轮数据和两小时稳定性，记录实际尺寸和画质变化，不能用工具全帧FPS代替日常增量FPS。
- [ ] 满足E4才自动优选；无收益或存在限制时保留实验性，不宣称全量支持。

## G6 最终验证和交接

- [ ] 全量测试和Release构建必须对应最终工作树；记录日志、退出码及失败处理，不能继承旧2521统计。
- [ ] 核对许可证、漏洞、包内容；不得包含研究样本、RDM DLL、探针或测试程序集。
- [ ] 更新完成报告，逐项对应Spec，列明未支持模式、真机矩阵与剩余工作。
- [ ] 仅在实际成功后报告提交/推送/产物。保留用户其他修改和样本，不执行批量清理。

定向命令：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~AppleMvs|FullyQualifiedName~MvsSyntaxAnalysis|FullyQualifiedName~MvsSampleCapture" -m:1
dotnet build tools/WinARD.ScaleProbe/WinARD.ScaleProbe.csproj -c Release -p:Platform=x64
```

最终命令：

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

当前用户只需把本三份文档、工作树和合成样本交给执行者。先做G0–G2离线工作；确有样本缺口时再安排用户配合。此计划不授权本轮继续实现或连接。
