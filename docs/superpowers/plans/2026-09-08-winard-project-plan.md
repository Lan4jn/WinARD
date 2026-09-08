# WinARD 整项目后续开发与交付计划

> 交接执行者使用Superpowers系统化排查、TDD和验证后结论；按领域进行规格/质量审查。用户当前只要求完整文档，不在本轮执行代码、采集或发布。

**目标：** 从可运行但性能/协议与发布未收尾的工作树，完成可靠远控及经过证据验证的画质优化和交付。  
**架构：** 保留现有分层与协议链，按最小根因修复；MVS/缩放与基础功能分别验收。  
**技术栈：** .NET8、C#、WinUI3、D3D11、SQLite、Windows OpenSSH、RFB/ARD、xUnit/PowerShell。  
**依据：** [整项目报告](../../testing/2026-09-08-winard-project-handoff-report.md)、[整项目Spec](../specs/2026-09-08-winard-project-spec.md)。

## T0：冻结可信基线与交接资产

- [ ] 收集HEAD、git status、已跟踪diff及未跟踪源文件清单；保留用户变更，不reset/clean。
- [ ] 核对README、历史完成清单、当前代码和产物，建立实现/测试/真机/发布四列状态台账。以当前报告为起点，不继承2531数字为新鲜通过。
- [ ] 保存现有样本哈希与manifest，分清大图前缀、小记录和完整序列；不得把敏感artifacts加入Git。
- [ ] 执行离线基线构建和测试，失败分类为产品、测试、环境；复现后修复，不盲目修改断言。
- [ ] 记录工具链/平台/依赖锁定与输出路径；避免同时构建共享obj引发伪失败。

## T1：基础远控正确性回归

文件：`src/WinARD.Application/Sessions/ConnectDeviceHandler.cs`、`RemoteSession.cs`、`src/WinARD.Desktop/Services/RfbClientFactory.cs`、`ViewModels/RemoteSessionViewModel.cs`、`Input/`、`src/WinARD.Transport/`及对应测试。

- [ ] 对照A1/A2/A4验证完整认证、控制、加密、标准持续解码和输入路径，保留已成功行为。
- [ ] 新发现失败先加真实行为测试再最小修复；测试需覆盖实际生产入口，不能只读源码字符串。
- [ ] 与用户约定真机短回归，复用保存配置；不输出密码，不自行接受变化主机密钥。
- [ ] 断线、取消、失焦、关闭保持输入和transport/lease清理，验证无额外活动会话。

## T2：设备、设置、凭据与重连收敛

文件：`MainWindow.xaml.cs`、`ConnectionEditorViewModel.cs`、`ConnectionEditorService.cs`、`ConnectionSessionController.cs`、`AutomaticReconnectCoordinator.cs`、`src/WinARD.Infrastructure/Settings/`、`Devices/`、`src/WinARD.Security/`及对应测试（按仓库实际路径定位）。

- [ ] 执行A3/A5/A6：Bonjour草稿/双击目标、SSH字段隔离、主题/锁定、全屏弹层输入隔离。
- [ ] 重点回归凭据迁移源/目标解锁、copy/readback/CAS、退役引用、新generation key、Ask提示、崩溃文件锁恢复；补所缺生产行为测试。
- [ ] 运行期列级profile更新和重连reload不得恢复旧引用；用户未持久化画质的合并规则明确。
- [ ] 自动/手动/取消/关闭并发等待任务退出再释放租约，旧UI generation不回写。先定向，修改后再全量相关项目。

## T3：画质状态与性能测量先行

文件：`QualityPresentation.cs`、`ConnectionQualityTracker.cs`、`RemoteSessionViewModel.cs`、`RemoteSessionWindow.xaml`、`DiagnosticExporter.cs`。

- [ ] 测试请求50%但实际100%、安全回退32位、未确认和需重连文案；实际像素为唯一生效依据。
- [ ] 核对刷新率档位、目标/实际、自适应/手动与质量锁定；显示器上限无依据时不宣称自动识别。
- [ ] 明确定时边界并测请求等待、接收/解码、呈现排队；无法拆分的指标注明合计。
- [ ] 固定显示布局和负载，先获取标准编码基线；只根据瓶颈选择一个优化变量，补基准再修改。

## T4：传输分辨率专项

依据：`2026-09-05-server-scale-repair.md`及其Spec；文件：`ArdClientMessageWriter.cs`、`QualityBootstrapPlanner.cs`、`RfbClientFactory.cs`、`tools/WinARD.ScaleProbe/`。

- [ ] 找到可信Apple缩放消息/时序来源，不直接替换成UltraVNC type8，不再盲排字节。
- [ ] 先已知字节和状态测试，再独立启动50%及同会话100→50→100；禁止probe自动fallback掩盖失败。
- [ ] 每比例单独确认真实像素尺寸与坐标；未支持时保留清晰限制，不自动试探未知能力。
- [ ] 在线能力通过后再接入与接收流/输入/压缩状态同步的手动切换，之后才考虑自动策略。

## T5：MVS真实解码专项

文件：`Encodings/Mvs/MvsMacroblockParser.cs`、`AppleMvsDecoder.cs`、`AppleMvsDecoderTests.cs`、`AppleMvsFullFrameTests.cs`、研究采集/分析工具。

- [ ] 保持默认隔离，核对当前经验公式、AC规则和大矩形假设，逐项补来源/位级推导；无证据的路径明确拒绝。
- [ ] 建立与算法无关的整块/整图预期，覆盖中间色、棋盘、文字、渐变和彩色边缘；不能仅测输出变化。
- [ ] 复用已有样本，必要时补完整大切片及连续变化；读取全部记录并验证后继，区分prefix/header/body级证据。
- [ ] 确定真实语法/颜色/量化/预测/尾部后写准确算法子计划；不预设JPEG/Exp-Golomb，不按样本添加经验分支。
- [ ] 解析大矩形内部结构，验证末尾消费、checked长度、预算、取消和失败不发布半帧；不能用人工拼接替代真实大矩形。
- [ ] 独立验收集通过E2b、资源和真机输入通过E3后才加实验选项；E4前不默认自动优选。

历史 `2026-09-08-mvs-general-decoding-plan.md` 提供子任务细节，其旧问题表述应随当前实现更新，不重复已解决的“量化表完全未用”结论。新增运算本身仍需协议依据。

## T6：整合与最终真机矩阵

- [ ] 标准/MVS与不同支持比例分别同负载比较，不把分辨率/色彩变化全归因于编码。
- [ ] 执行Spec A1–A9，保存候选哈希、系统版本、显示布局、测试方法、每轮数据及限制。
- [ ] 执行两小时会话和重连/取消故障场景；失败修复后重验受影响项。
- [ ] 若某系统组合未测，标未验证；若缩放/MVS仍未完成，可提供标准候选但不得称整个优化目标完成。

## T7：发布与仓库收尾

文件：`packaging/`、`README.md`、`docs/testing/compatibility-matrix.md`、`release-checklist.md`、`THIRD-PARTY-NOTICES.md`。

- [ ] 对最终工作树运行全部工程检查，保留原始输出；处理许可证文件/SPDX问题必须有真实依据，不绕过规则。
- [ ] 生成唯一候选版本ZIP/MSIX/SHA256，校验包内无研究工具/样本/秘密/RDM DLL/测试程序集。
- [ ] 正式签名需用户掌管证书，签名后重算哈希再验证；未签名版本明确称候选。
- [ ] 在干净Windows10/11安装/运行/卸载，填写实际兼容和安全软件结果。
- [ ] 整理针对性提交，联网确认远端状态；按用户已有推送授权完成推送，实际成功后才报告。
- [ ] 发布报告列明版本/commit/差异/哈希、已验证范围、未完成项和升级影响。

## 工程验证命令

```powershell
git status --short --branch
git rev-parse HEAD
dotnet restore WinARD.sln -p:Platform=x64
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore -m:1
dotnet format WinARD.sln --verify-no-changes --no-restore
pwsh -File packaging/tests/check-licenses.Tests.ps1
pwsh -File packaging/check-licenses.ps1
pwsh -File packaging/check-vulnerabilities.ps1
git diff --check
pwsh -File packaging/portable.ps1 -Version 0.1.0.0
pwsh -File packaging/verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
```

0.1.0.0为当前候选验证值；正式发布前按既有版本规则递增并在构建、manifest、包校验中保持一致。命令成功与真实功能验收分别记录。

## 分工与用户下一步

执行者先做T0–T3离线/短回归，T4协议研究与T5语法研究可在无共享写冲突时并行，真机连接串行。T6必须针对同一整合候选，T7最后执行。

用户现在提供工作树和本三份文档即可；不需要重新发连接信息。执行者只有在样本/真机/签名需要具体输入时提出单次明确请求，先完成其他可独立工作。

每任务输出：改动范围、问题复现、测试证据、真机证据、未完成项、下一具体步骤。总目标未达成时，不能以“完成计划编写”或“完成差距审计”替代项目完成。
