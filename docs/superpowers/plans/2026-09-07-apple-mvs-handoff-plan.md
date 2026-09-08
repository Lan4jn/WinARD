# Apple MVS 后续开发执行计划

> 当前执行入口为 [2026-09-08 修正与开发计划](2026-09-08-mvs-recovery-plan.md)。先处理已发现的占位解码和边界证据问题，再推进真实解码。

**当前目标：** 从已有前缀样本取得完整边界、字段和最小解码证据。  
**最终目标：** 交付经过互操作、性能和稳定性验证的 MVS 产品路径。  
**依据：** [报告](../../testing/2026-09-07-apple-mvs-handoff-report.md)、[Spec](../specs/2026-09-07-apple-mvs-handoff-spec.md)。

本计划交给后续执行者实施，本轮只交付文档。按 Superpowers 系统化排查、TDD、验证后结论执行；不要重复已完成的 RDM 声明测试。未知协议通过明确实验验证，不填猜测算法代码。

## P0：冻结接手基线

- [ ] 保存 `git status --short`、HEAD 和针对性差异清单；当前 HEAD 为 `3a810c59a447a109fde725143dfb616ac644396f`，MVS 工作主要在未提交文件，不能只 checkout 该提交。
- [ ] 重新验证两个 manifest、文件长度、SHA-256，确认图像仍是前缀。仅输出匿名元数据。
- [ ] 运行现有捕获/声明测试并保存命令、退出码、通过/失败数量；不要继承“2470 全通过”作为新鲜结果。
- [ ] 阅读两个实际捕获路径及它们的调用方，记录反射注入、超时、写文件和矩形校验差异。

```powershell
git status --short
git rev-parse HEAD
Get-FileHash artifacts/protocol-research/samples/solid-blue/payload-prefix.bin, artifacts/protocol-research/samples/solid-blue/setup/payload-prefix.bin -Algorithm SHA256
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~MvsSampleCaptureTests|FullyQualifiedName~RdmCaptureTests" -m:1
```

## P1：可靠的完整记录采集工具

文件：`tools/WinARD.ProtocolProbe/EncodingResearch/MvsSampleCapture.cs`、`MvsSampleCaptureReader.cs`、`tools/WinARD.ScaleProbe/RfbClient.ScaleProbe.cs`、`Program.cs`；测试 `tests/WinARD.Remote.Protocol.Tests/Tools/MvsSampleCaptureTests.cs`。

- [ ] 先写失败测试：四字节候选长度 N、分段传输、恰好 N 字节后的后继头、截断、超预算、取消、已有输出目录和发布失败。
- [ ] 引入 Spec 定义的记录完整性字段，保留原前缀 manifest 的兼容读取但绝不升级其证据等级。
- [ ] 研究解析器按长度假设读取完整记录并继续下一头，独立标注该假设。避免用等待超时或 socket 分段判边界。
- [ ] 将样本写入唯一 staging 目录，验证文件/manifest/hash 后发布；拒绝覆盖和越出输出根目录，测试中断留下的状态。
- [ ] 共享一个真实采集实现，或让测试直接覆盖 live decoder 路径；避免只测另一套 reader。
- [ ] 替换私有字段反射注入：先检查现有可注入 decoder/session factory，使用最小显式研究入口，并保留原 Zlib 实例。注册必须在声明1011前完成且不与Receive并发。
- [ ] 在 `FramebufferUpdateReader.cs` 中区分合法零尺寸控制候选和普通图像矩形；新增单轴零、越界和合法非零测试，不让1011绕过像素边界。
- [ ] 定向测试、研究工具构建通过后审查差异；不对普通产品启用1011。

```powershell
dotnet build tools/WinARD.ScaleProbe/WinARD.ScaleProbe.csproj -c Release -p:Platform=x64
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~MvsSampleCapture|FullyQualifiedName~Framebuffer" -m:1
```

## P2：完整边界验证（需要用户现场配合）

- [ ] 工具就绪后向用户提供合成测试图和本次采集预算；确认单屏或全部捕获区域均为测试内容。不要沿用历史确认假定当前画面仍脱敏。
- [ ] 复用保存的域名配置和凭据，独立测试连接；不通过聊天传密码，不自动发送键鼠。用户按指引变换场景。
- [ ] 至少三个独立会话，依次采集控制候选、多个完整图像记录和后继矩形/更新头，记录解析位置与哈希。
- [ ] 验证每个 N 字节后都严格落在下一个合法头；若发生错位保留失败证据并停止，不扫描恢复掩盖错误。
- [ ] 把结果写入报告：长度包含范围、是否存在其他控制形式、是否依赖显示器布局。只有真实数据满足 Spec 才通过 E2a。

现有 `--capture-mvs` 是前缀命令，不能作为 P1 完成后的完整采集命令。P1 实现者必须在帮助文本和运行手册中提供准确的新命令及输出语义，运行前先用本地脚本流证明它能完整消费多记录。

## P3：Setup 与图像语法实验

文件：`docs/protocol/ard-mvs-evidence.md` 和新增研究分析脚本（位于 `tools/`，名称由实际分析职责确定）；已审核合成 fixture 放入测试研究目录，原始样本留 artifacts。

- [ ] 对比不同纯色、渐变和彩色边缘场景的完整控制/图像记录，形成字段偏移表，逐项区分观察值与解释。
- [ ] 检验 `0x02` 的含义、64 字节分组的顺序/精度和是否随质量变化。表内容相似只能作为线索。
- [ ] 确认图像内部封装、块/切片组织、熵编码、分量和抽样；如嵌套标准格式，再选择系统能力或许可明确的依赖。
- [ ] 编写最小离线重建实验，用已知合成图对比输出尺寸、颜色和空间位置。失败必须能定位语法/颜色误差。
- [ ] 验证两条以上连续依赖更新、缺Setup、重复Setup、重连和尺寸变化；明确哪些状态跨记录保存、何时重置。
- [ ] E2b 审查通过后，补一份代码级decoder子计划，包含准确结构、入口、已知答案和依赖选择。未通过则列出具体缺失字段和下一实验，不能写空实现。

## P4：隔离 decoder

候选接入点：`src/WinARD.Remote.Protocol/Encodings/`、`Framebuffer/FramebufferUpdateReader.cs`、现有 decoder registry。承载结构以 P3 为准，不预设它是标准JPEG矩形。

- [ ] 先建立完整已知答案和连续样本失败测试，再最小实现。
- [ ] 覆盖每个头/记录截断边界、非法模式、长度溢出、解压预算、缺失状态、矩形越界、取消与Dispose。
- [ ] 解码状态由单会话拥有，失败不发布像素；避免复用其他连接的表/字典/参考帧。
- [ ] 检查独立探针输出与参考合成画面，记录有损误差及可读性；不能宣称未经证明的无损。
- [ ] 规格、正确性、资源和许可证审查通过，才标记 E3。

## P5：产品接入

文件：`QualityBootstrapPlanner.cs`、`RfbClientFactory.cs`、`ConnectionAttemptWorkflow.cs`、`QualityPresentation.cs`、`DiagnosticExporter.cs`及对应测试。

- [ ] 实验性显式选项默认关闭，旧设置兼容；若新增持久化字段，正规迁移并测试回滚。
- [ ] 编码请求与实际结果分别呈现，真实1011解码成功才显示MVS。
- [ ] 首选失败共享原单次fallback预算；认证、权限、SSH、取消不作为codec兼容失败。
- [ ] 运行期失步后关闭连接并用安全配置恢复；确定失败不无限重试1011。
- [ ] 验证光标、键鼠坐标、组合键释放、剪贴板、尺寸变化、重连所有权。
- [ ] diagnostics只增加有界闭集状态和计数，敏感字段/载荷负向测试通过。

## P6：性能、稳定性与发布

- [ ] 按 Spec 对标准/MVS 三类负载各三轮30秒比较，记录实际尺寸与质量，解释计时口径。
- [ ] 两小时会话验证连续状态、内存趋势、取消/重连和输入；新增失败需修复并重验受影响项。
- [ ] 只有收益和质量均符合条件才开放自动优选；否则保留显式实验选择并报告取舍。
- [ ] 新鲜全量测试、format、Release x64、许可证和漏洞检查通过，生成并校验产物。
- [ ] 审查包内不含研究样本、探针、测试程序集或RDM组件；针对性提交和推送遵循用户交给执行者的权限。

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

## 每阶段交付格式

记录：实际改动文件、失败复现、验证命令与退出码、样本/fixture哈希、通过的阶段、未满足的条件、下一具体操作。不能将“已写证据不足报告”计为“完整协议已确定”。

给后续开发者的起始指令：先执行 P0/P1；不重复索取连接信息，不再次要求 High/Adaptive 声明；完整采集工具通过离线测试后再向用户约定合成画面采集，随后推进 P2/P3。
