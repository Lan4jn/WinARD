# 服务端分辨率缩放与低帧率修复计划

> 执行者按任务顺序实施；协议代码变更采用失败测试、最小实现、回归验证。可以使用 subagent-driven-development 分派独立工作；真机连接测试串行执行。

**目标：** 使 WinARD 准确呈现请求与实际传输分辨率，取得可复核的 Apple 缩放协议依据，并在真机支持范围内实现可靠的启动或在线缩放。

**架构：** 复用现有 ARD 认证、加密、消息调度和 framebuffer 解码链，先在独立 ScaleProbe 验证，再接入产品。实际像素尺寸决定能力状态；性能优化以分阶段测量结果为依据。

**技术栈：** .NET 8、C#、WinUI 3、RFB/ARD 003.889、现有 xUnit 与 PowerShell 工具。

**规格：** [服务端分辨率缩放与低帧率修复规格](../specs/2026-09-05-server-scale-repair-design.md)。功能行为、证据门槛与验收以该规格为准。

## 事实与约束

依据：`docs/testing/2026-09-05-online-scale-investigation.md` 和 `artifacts/WinARD-diagnostics-20260905-173809.zip`。

- 日常会话：3360 × 2100，目标 30 FPS，采样实际 11 FPS；响应 89 ms、呈现 1 ms。响应时间不是独立测得的网络 RTT。
- 本次启动解析比例为 100%，实际比例为 100%，没有发生兼容回退。该次日志不能证明非 100% 启动请求被拒绝。
- 独立探针同一连接发送现有 50% 请求后，六秒内收到 45 个真实像素更新，尺寸仍为 3360 × 2100；恢复请求后尺寸仍相同。
- 现有十字节浮点报文缺少成功 Apple 抓包依据。LibVNC 的四字节整数 type 8 属于不同扩展；禁止仅因消息编号相同就替换。
- 保持 Mac 零安装；本地显示缩放与传输分辨率分别标注。
- 已授权复用保存配置。地址、账号及秘密不进入报告或命令参数；凭据通过原有后端读取，保险库解锁或临时密码走交互输入。
- 不修改用户保存的比例、凭据或 Mac 系统显示模式来完成探测。测试连接独立且有超时，不发送键鼠；继续使用用户已指定的域名连接，若配置不能唯一识别则停止连接。

## 文件职责

| 文件 | 本次职责 |
|---|---|
| `src/WinARD.Desktop/ViewModels/QualityPresentation.cs` | 请求、实际、待重连、未确认的文案 |
| `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs` | 会话画质状态与分阶段测量 |
| `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml` | 画质面板状态呈现 |
| `src/WinARD.Application/Quality/QualityBootstrapModels.cs` | 启动确认与失败状态 |
| `src/WinARD.Application/Quality/QualityBootstrapPlanner.cs` | 保持既定自动映射，明确解析结果 |
| `src/WinARD.Desktop/Services/RfbClientFactory.cs` | 协议启动、在线切换、接收与所有权 |
| `src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs` | 经证据确认的请求编码 |
| `tools/WinARD.ScaleProbe/Program.cs` | 保存连接选择、阶段执行、匿名结果输出 |
| `tools/WinARD.ScaleProbe/RfbClient.ScaleProbe.cs` | 独立探测发送入口 |
| `docs/protocol/ard-zero-install-image-quality.md` | 协议依据及支持边界 |
| `docs/testing/2026-09-05-online-scale-investigation.md` | 真机观察与证据索引 |

现有消息调度器、压缩解码器和输入坐标转换复用。任务 4 开始前用符号搜索核对所有调用方，再确定确需修改的输入/解码文件；不创建第二套协议栈。

## 任务 1：修正产品状态与未验证能力说明

- [ ] 在 `tests/WinARD.Desktop.Tests/ViewModels/QualityPresentationTests.cs` 增加行为用例：请求 50%/实际 100%、请求未确认、已确认 50%、单次兼容回退、会话内修改待重连，沿用已有套件。
- [ ] 运行下方 QualityPresentation/QualityPanel 筛选，确认新增行为测试在修复前失败。
- [ ] 调整文案：同时显示请求比例、启动解析比例、实际像素尺寸；未确认不得显示“已生效”。请求失败时显示具体稳定状态。
- [ ] 明确自动档与带宽目标的映射；本次不擅自改变用户此前批准的阈值。
- [ ] 更正协议说明中将现有报文写为已验证能力的表述。保留请求入口时明确“实验性，尚未验证”，在线自动切换保持关闭。
- [ ] 回归测试通过后单独提交本任务；不得把界面修正标记为缩放协议已修复。

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~QualityPresentation|FullyQualifiedName~RemoteSessionQualityPanel" -m:1
```

## 任务 2：补齐 Apple 协议依据

- [ ] 查找 Apple 专用缩放实现或能实测降低接收像素尺寸的参考客户端。记录源码版本/提交、函数位置、许可证和对应服务端版本。
- [ ] 确认参考客户端改变的是传输像素尺寸，而非窗口缩放、裁剪或 Mac 系统显示模式；没有这种证据时不作为正例。
- [ ] 记录控制消息类型、总长度、字节序、比例含义、加密包内外位置，以及前后的显示选择、自动更新、framebuffer 请求和服务端确认消息。
- [ ] 若采用通信取证，先确认采集点能看到解密后的控制消息；普通加密 TCP 抓包不能证明消息布局。使用参考客户端公开调试接口或自有测试端，避免采集凭据和画面正文。
- [ ] 为经验证的消息制作只含控制字节与匿名尺寸元数据的最小 fixture，放入协议测试；不要复制参考客户端的整段实现。
- [ ] 把来源和对应 fixture 写入协议文档。未找到可信来源时，本任务结论为“证据不足”，不得通过排列字节试探替代依据。

**出口条件：** 能解释每个消息字段与必要时序，并能关联到一次真实尺寸变化。否则只继续任务 1 和任务 5，任务 3/4 的协议实现不启动。

## 任务 3：独立探针验证启动与在线缩放

- [ ] 根据任务 2 的 fixture 为 `ArdClientMessageWriterTests` 增加已知字节测试；先验证失败，再最小修改 writer。保留非法比例、取消和完整消息写入测试。
- [ ] 为探针阶段逻辑增加可重复的脚本流测试：迟到旧帧、仅 cursor/空帧、目标尺寸真实像素帧、服务端关闭、超时、取消。旧尺寸帧不能立即判为拒绝；写出请求不能判为成功。
- [ ] 启动验证用独立连接请求 50%，记录原始尺寸及后续真实像素尺寸。探测模式禁止自动 fallback，以免掩盖首选请求结果。
- [ ] 在线验证另开独立连接：先取得 100% 真实像素，再在同一会话请求 50%，确认后请求 100%。每阶段最多 25 秒、全程有界，失败后关闭测试连接。
- [ ] 保持连接、压缩流和加密序列连续；不得靠重建 decoder 或重新连接制造“在线切换成功”。
- [ ] 输出每阶段请求比例、观察到的尺寸序列、真实像素更新数、耗时、字节数和稳定失败阶段。全帧请求 FPS 标记为探测吞吐。
- [ ] 真机重复至少三次往返，保存匿名汇总。只有尺寸稳定变化且恢复成功才确认本机在线能力。

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ArdClientMessageWriterTests -m:1
dotnet build tools/WinARD.ScaleProbe/WinARD.ScaleProbe.csproj -c Release -p:Platform=x64
```

探针会话中断或未观察到新尺寸分别记录，不能泛化成“所有 macOS 不支持”。默认能力仍为 Unknown；仅根据有效实验结果决定下一任务分支。

## 任务 4：按验证结果接入产品

| 真机结果 | 产品行为 |
|---|---|
| 仅连接前请求生效 | 保留连接前比例；会话修改明确要求重连 |
| 同会话往返生效 | 接入受控在线切换，再开放动态调整 |
| 请求无效或证据不足 | 显示实际原尺寸及未验证状态，不宣称缩放成功 |

在线分支：

- [ ] 在 `tests/WinARD.Desktop.Tests/FramePresentationTests.cs` 增加接收与切换交错测试，以及尺寸更新、越界矩形、取消和超时用例；先失败再实现。
- [ ] 在现有调度边界完成正在处理的帧，串行发送经验证的控制序列；若服务端自动推帧，使用任务 2 证明的控制方式协调，不能假设本地停止请求就能停止来帧。
- [ ] 以完整解码的尺寸变更与目标真实像素帧确认状态，原子更新 framebuffer、呈现状态和输入转换；保留压缩流连续性。
- [ ] 切换过程中释放已按下的远端输入并暂缓坐标输入，确认新尺寸后恢复；验证四角、中心和光标坐标映射。
- [ ] 切换失败时仅在已证实的协议边界内恢复。无法继续可靠解码时关闭连接并交由既有重连流程，禁止带损坏流继续运行。
- [ ] 先开放手动在线切换。动态策略仅使用验证过的比例；不覆盖用户锁定的比例，并用有界冷却时间防止频繁往返。
- [ ] 回归启动单次 fallback、自动重连和输入生命周期，避免在线试验扩大现有兼容重试次数。

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FramePresentation|FullyQualifiedName~RemoteSessionWindowInputIntegration|FullyQualifiedName~AutomaticReconnect|FullyQualifiedName~ConnectionAttemptWorkflow" -m:1
```

## 任务 5：独立定位低帧率

- [ ] 核对现有指标边界，分别测量请求等待、接收/解码、排队和呈现。CPU 解码时间与网络等待不能用同一个计时值混称。
- [ ] 优先复用现有统计；无法分离的指标明确标为合计耗时。确需新增时只记录有界聚合数值，并扩展诊断白名单测试。
- [ ] 在相同测试内容、相同色深/编码、相同连接条件下比较 100% 与已确认的 50%；分别测静态、窗口拖动、滚动三类负载，每组至少 30 秒、三轮。
- [ ] 记录实际尺寸、真实更新 FPS、接收 MiB/s、响应和呈现耗时，说明测试内容由用户操作；探针不自动发送桌面输入。
- [ ] 依据证据只选择一个瓶颈做最小优化，例如冗余完整更新请求、串行等待或重复像素复制；先增加能复现该瓶颈的测试/基准再修改。
- [ ] 若 50% 不支持，继续对 100% 的请求/编码/解码链做测量，不将本地画面缩小当作传输量下降。

## 验收和交付

- [ ] 50% 支持的判据：本机实际像素帧为 1680 × 1050；恢复后为 3360 × 2100。其他原始尺寸按已验证比例和取整规则计算，不能硬编码本机尺寸。
- [ ] 在线支持还需满足同一连接至少三次往返、连续压缩流有效、坐标正确、没有粘键或未释放任务。
- [ ] 未支持/未验证时 UI 真实显示实际尺寸和限制，超时与失败有可诊断结果。
- [ ] 性能报告给出各负载三轮结果与变化，不承诺四分之一带宽或固定 30 FPS；像素数量下降不等于相同倍数的带宽改善。
- [ ] 运行相关测试、Release 构建及格式检查；新产物包含最终修改后再打包校验。记录确实执行过的真机项目，其余保留 External gate。
- [ ] 仅提交本次范围，保留当前工作树其他修改。推送沿用用户已有授权，在实际执行后报告提交与产物路径。

```powershell
dotnet build WinARD.sln -c Release -p:Platform=x64 -warnaserror
dotnet format WinARD.sln --verify-no-changes --no-restore
git diff --check
```

**当前状态：** 方案已编写；现有在线报文无效的真机观察已取得。正确 Apple 消息布局与在线时序仍需任务 2 的证据，不能据本计划标记协议修复完成。
