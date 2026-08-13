# WinARD MVP 功能收尾实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 完成百分比分辨率、稳定画质悬浮面板以及 MVP 规格中仍缺失的连接、设备、SSH、全屏和全局设置功能，并生成可验证的 Release x64 成品。

**架构：** 先把分辨率扩展为连接前不可变的启动计划，并复用现有“首选连接 + 单次安全连接”边界；会话 UI 将画质控件放入窗口视觉树中的锚定 overlay。其余功能按独立服务边界实现：重连编排、设备库交互、设置仓储和事务式凭据迁移，最后统一扩展严格白名单诊断和发布验证。

**技术栈：** C# 12、.NET 8、WinUI 3、Windows App SDK、SQLite、RFB/ARD 3.889、xUnit、PowerShell 打包脚本。

---

## 文件结构

### 新建

- `src/WinARD.Application/Sessions/AutomaticReconnectCoordinator.cs`：暂态错误分类、倒计时和可取消退避循环。
- `src/WinARD.Domain/Settings/AppSettings.cs`：全局设置闭集和值验证。
- `src/WinARD.Application/Ports/IAppSettingsRepository.cs`：设置持久化端口。
- `src/WinARD.Infrastructure/Settings/SqliteAppSettingsRepository.cs`：SQLite 设置仓储。
- `src/WinARD.Infrastructure/Database/Migrations/Migration004AppSettings.cs`：设置 schema/默认值迁移。
- `src/WinARD.Infrastructure/Database/Migrations/Migration004QualityScalePercent.cs`：扩展设备表的分辨率 CHECK；若迁移编号必须唯一，则与 AppSettings 合并为同一 Migration004，并保持两个职责各自有测试。
- `src/WinARD.Desktop/Views/SettingsDialog.xaml`、`.xaml.cs`：全局设置和保险库控制。
- `src/WinARD.Desktop/Services/CredentialBackendMigrationService.cs`：配置引用事务与迁移补偿。
- `src/WinARD.Desktop/ViewModels/QualityOverlayState.cs`：悬浮面板显隐、Esc 优先级和锚定状态。
- 对应测试文件放入现有 Domain/Application/Infrastructure/Desktop/Security 测试项目。

### 修改

- `src/WinARD.Domain/Connections/QualityProfile.cs`：增加 25%，明确 100% 语义。
- `src/WinARD.Application/Quality/QualityBootstrapModels.cs`、`QualityBootstrapPlanner.cs`：缩放比例和自动映射。
- `src/WinARD.Application/Sessions/ConnectDeviceHandler.cs`：启动比例配置和首像素验证结果。
- `src/WinARD.Application/Ports/*.cs`：启动、实际状态和重连接口。
- `src/WinARD.Remote.Protocol/Ard/ArdClientMessageWriter.cs`：沿用并验证缩放报文。
- `src/WinARD.Desktop/Services/RfbClientFactory.cs`：首帧前缩放、decoder/session 发布和兼容失败分类。
- `src/WinARD.Desktop/Services/ConnectionAttemptWorkflow.cs`：统一单次安全回退。
- `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`、`QualityPresentation.cs`：期望/实际百分比和待重连。
- `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`、`.xaml.cs`：锚定 overlay、立即重连和全屏工具栏。
- `src/WinARD.Desktop/MainWindow.xaml.cs`、`MainWindowViewModel.cs`：Bonjour、双击、详情和设置入口。
- `src/WinARD.Desktop/Views/ConnectionEditorDialog.*`、`ConnectionEditorViewModel.cs`：SSH 认证方式与独立目标。
- `src/WinARD.Desktop/App.xaml.cs`：注册设置、迁移和重连服务。
- `src/WinARD.Infrastructure/Diagnostics/*`、`DesktopDiagnosticContextFactory.cs`：严格白名单字段。
- `README.md`、`docs/testing/*`：行为、限制和真机验收。

## 任务 1：建立百分比分辨率和启动计划模型

- [ ] **步骤 1：写失败测试**

在 `QualityProfileTests` 和 `QualityBootstrapPlannerTests` 中断言五档闭集、旧 `Native` 兼容值、25% 映射，以及自动带宽边界：`1 MiB→25%`、`1 MiB+1→50%`、`2 MiB+1→75%`、`4 MiB+1/null→100%`。断言 fallback 永远为 `1d + BGRA32 + Zlib-first`。

- [ ] **步骤 2：验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~QualityProfileTests
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~QualityBootstrapPlannerTests
```

预期：缺少 `Percent25`/启动 `ScaleFactor` 而失败。

- [ ] **步骤 3：最少实现**

保持现有枚举数值兼容，为 100% 提供稳定显示语义，新增 `Percent25`。`QualityBootstrapSettings` 增加经验证的 `ScaleFactor`；planner 使用独立纯函数按已批准阈值解析自动档，fallback 固定比例 1。

同时升级 SQLite `quality_scale` CHECK 以允许数值 4；原数值 1 原位解释为 100%，不得改写现有行。将迁移注册到 `WinArdDatabase`，并增加 v3→v4 和新库 schema 测试。

- [ ] **步骤 4：验证绿灯并提交**

运行上述测试及 `dotnet test tests/WinARD.Application.Tests ... --filter FullyQualifiedName~QualityModelsTests`，全部通过后提交：

```powershell
git add src/WinARD.Domain src/WinARD.Application tests/WinARD.Domain.Tests tests/WinARD.Application.Tests
git commit -m "feat: plan bootstrap display scaling"
```

## 任务 2：在真实 ARD 启动路径应用缩放并统一回退

- [ ] **步骤 1：写失败的 wire 与集成测试**

在 ARD writer/initializer、`ConnectDeviceHandlerTests`、`FramePresentationTests` 中覆盖：100% 不写 type 8；75/50/25 写 `08 00 + IEEE-754 big-endian double`；已认证路径保持 1103 声明先于加密请求；缩放位于首 framebuffer request 前；cursor/空/CopyRect/DesktopSize 不验证成功；真实像素帧才发布实际比例；首选兼容失败后只创建一条 100% 安全连接；DNS/SSH/auth/cancel/普通 timeout 不 fallback。

- [ ] **步骤 2：验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Remote.Protocol.Tests/WinARD.Remote.Protocol.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~ArdClientMessageWriterTests|FullyQualifiedName~RfbSessionInitializerTests"
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ConnectDeviceHandlerTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FramePresentationTests|FullyQualifiedName~ConnectionAttemptWorkflowTests"
```

预期：bootstrap 未写缩放、实际状态无比例而失败。

- [ ] **步骤 3：最少实现协议与所有权**

`ConfigureBootstrapAsync` 在同一 scheduler background item 内按已认证测试固定顺序写缩放/PixelFormat/encodings/加密请求，wire 全部成功后原子发布 decoder 与 settings。扩展 `QualityBootstrapState` 保存实际比例；兼容失败复用现有 `QualityBootstrapCompatibilityException`，增加 `ScaleRejected`、`FramebufferSizeMismatch`、`RectangleOutOfBounds` 等闭集原因。分类只在“认证后、首个真实像素帧前”有效，运行期 RemoteClosed 不得进入画质 fallback。不得引入第三次尝试。

- [ ] **步骤 4：验证、审查和提交**

定向测试全部通过，检查每个失败路径释放 client/transport/preload/secret，再提交：

```powershell
git add src/WinARD.Application src/WinARD.Remote.Protocol src/WinARD.Desktop tests
git commit -m "feat: apply display scaling before the first frame"
```

## 任务 3：实现期望/实际分辨率状态与锚定画质面板

- [ ] **步骤 1：写失败测试**

扩展 `QualityPresentationTests` 验证设置/解析/实际/回退/待重连文案；为 `QualityOverlayState` 写显隐、再次点击、关闭、Esc 优先级和 dropdown-open 保持测试；扩展 `RemoteSessionQualityPanelTests` 断言画质控件不再位于 `Button.Flyout`，overlay 位于窗口视觉树、覆盖 viewport、具有关闭/立即重连按钮和 25% 选项。

- [ ] **步骤 2：验证红灯**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~QualityPresentationTests|FullyQualifiedName~RemoteSessionQualityPanelTests|FullyQualifiedName~QualityOverlayStateTests"
```

预期：overlay 状态类型和实际比例呈现缺失。

- [ ] **步骤 3：最少实现 overlay**

把画质内容移入 `RootGrid` 顶层 overlay，使用 `Canvas`/对齐容器锚定按钮下方并限制最大高度；控件 popup 不控制外层可见性。面板区域截获指针/键盘，不落入 `RemoteInputSurface`。修改 scale/color 只保存并设置 pending reconnect；刷新率继续在线应用。立即重连复用窗口生命周期的先释放输入、关闭旧会话、单次显式连接路径。

- [ ] **步骤 4：自动与人工验证并提交**

定向测试通过后运行应用进行一次：打开画质→打开每个下拉→选择→面板仍在→Esc 分层关闭→待重连提示/稍后。提交：

```powershell
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "fix: keep remote quality controls open"
```

## 任务 4：接入可取消的意外断线自动重连

- [ ] **步骤 1：写失败测试**

新增 `AutomaticReconnectCoordinatorTests` 和生命周期集成测试，覆盖暂态 EOF/网络错误退避、倒计时更新、取消、显式断开不重连、确定性 auth/permission/protocol/host-key 错误不重连、每次全新连接、自动分辨率重新解析、同一时间只有一个循环和一个会话租约。

- [ ] **步骤 2：验证红灯**

```powershell
dotnet test tests/WinARD.Application.Tests/WinARD.Application.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AutomaticReconnectCoordinatorTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~RemoteSessionWindowLifecycleTests|FullyQualifiedName~AutomaticReconnect"
```

- [ ] **步骤 3：最少实现**

协调器接收错误分类器、`ReconnectPolicy`、连接委托和倒计时 observer；不持有 UI 类型。会话窗口显示尝试次数、下一次倒计时和取消；用户“立即重连”与自动循环通过同一互斥门串行化。

- [ ] **步骤 4：验证并提交**

定向测试通过，确认取消/关闭不会遗留 timer/task，提交：

```powershell
git add src/WinARD.Application src/WinARD.Desktop tests
git commit -m "feat: reconnect transient remote sessions"
```

## 任务 5：完成 Bonjour、双击、详情和 SSH 编辑

- [ ] **步骤 1：写失败测试**

测试发现项生成预填草稿且保存前不持久化；双击保存项连接、单击只选择、活动会话阻断第二连接；详情含用户名/网络路径/无预览占位；SSH 认证方式闭集、私钥/密码字段验证、跳板主机与目标 Mac 独立保存/加载，旧配置按私钥路径迁移认证方式。

- [ ] **步骤 2：验证红灯**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~MainWindowViewModelTests|FullyQualifiedName~ConnectionEditorViewModelTests|FullyQualifiedName~ConnectionEditorDialogStatePresenterTests"
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~SshProfileTests
```

- [ ] **步骤 3：最少实现**

为发现项提供显式添加命令；ListView 双击只对保存项调用现有连接流程。编辑器增加认证方式和 TargetHost/TargetPort，构建 `SshProfile` 时不再复制顶层地址。详情只显示安全配置摘要，不生成远程截图。

- [ ] **步骤 4：验证并提交**

定向测试及连接编辑器现有测试通过后提交：

```powershell
git add src/WinARD.Domain src/WinARD.Desktop tests
git commit -m "feat: complete device and SSH workflows"
```

## 任务 6：完成全屏工具栏交互

- [ ] **步骤 1：写失败测试**

新增纯状态控制器测试：窗口模式常显；进入全屏隐藏；顶部热区显示；离开延迟隐藏且旧 timer 无效；Esc 优先级 dropdown→quality overlay→fullscreen；被本地消费的 Esc 不发送远端。

- [ ] **步骤 2：验证红灯**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~FullscreenToolbar|FullyQualifiedName~RemoteSessionWindowInputIntegrationTests"
```

- [ ] **步骤 3：最少实现**

给 CommandBar 命名并置于 overlay 层；以有界 dispatcher timer 控制全屏自动隐藏；顶部透明热区只在全屏启用。所有本地快捷键在调用远端键映射前决策并标记 handled。

- [ ] **步骤 4：验证并提交**

自动测试和窗口模式/全屏人工冒烟通过后提交：

```powershell
git add src/WinARD.Desktop tests/WinARD.Desktop.Tests
git commit -m "feat: add immersive session toolbar"
```

## 任务 7：实现 AppSettings、保险库控制和事务式凭据迁移

- [ ] **步骤 1：写失败的模型/仓储测试**

覆盖主题、日志等级、剪贴板默认值、默认凭据后端和锁定时间验证；SQLite 默认值、round-trip、并发更新和 Migration004；立即锁定和动态更新时间。

- [ ] **步骤 2：写失败的迁移事务测试**

覆盖目标验证失败、部分写入、数据库提交失败、取消、并发引用改变、提交成功后用户拒绝/接受源清理、目标补偿失败。断言失败时原引用和源秘密仍可读。现有单秘密 `CredentialStoreMigrator` 不负责提前删除源；批量服务必须采用 copy/verify→DB commit→可选 delete 顺序。

- [ ] **步骤 3：验证红灯**

```powershell
dotnet test tests/WinARD.Domain.Tests/WinARD.Domain.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~AppSettings
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~AppSettings|FullyQualifiedName~Migration004"
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Settings|FullyQualifiedName~CredentialBackendMigration"
```

- [ ] **步骤 4：最少实现设置边界**

实现有版本 AppSettings 和 repository；设置对话框即时应用主题/剪贴板默认值，更新 vault timeout，提供立即锁定。日志等级只能选择安全结构化等级，不能开启正文/像素采集。

- [ ] **步骤 5：最少实现迁移事务**

批量服务只枚举 repository 中受管引用；为目标生成对应 store 的新引用，copy/readback 全部成功后，在 SQLite transaction 中 CAS 更新全部 profile 和默认后端。提交后才根据用户决定删除不再引用的源。补偿和清理错误只输出稳定代码/计数。

- [ ] **步骤 6：验证并提交**

定向测试、Security 全项目和 Infrastructure 全项目通过后提交：

```powershell
git add src/WinARD.Domain src/WinARD.Application src/WinARD.Infrastructure src/WinARD.Security src/WinARD.Desktop tests
git commit -m "feat: add secure application settings"
```

## 任务 8：扩展严格诊断和文档

- [ ] **步骤 1：写失败诊断测试**

为期望/解析/实际比例、首帧尺寸、fallback 原因、重连尝试和迁移计数增加 allowlist 测试；注入 host/user/path/password/clipboard/raw exception 并断言 ZIP 不包含。所有数值验证范围、TTL 和条数上限。

- [ ] **步骤 2：验证红灯并最少实现**

```powershell
dotnet test tests/WinARD.Infrastructure.Tests/WinARD.Infrastructure.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~DiagnosticExporterTests
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~Diagnostic|FullyQualifiedName~Quality"
```

只增加规格允许的安全字段和稳定枚举；更新 README、真机手册、兼容矩阵说明和发布清单，未执行项保持 External gate。

- [ ] **步骤 3：验证并提交**

```powershell
git add src/WinARD.Infrastructure src/WinARD.Desktop tests docs README.md
git commit -m "feat: diagnose scale and reconnect state"
```

## 任务 9：整体审查、全量验证、编译与打包

- [ ] **步骤 1：规格合规审查**

逐条核对 `docs/superpowers/specs/2026-08-13-winard-mvp-completion-design.md`，Critical/Important 必须为 0；发现问题回到对应任务补失败测试并修复。

- [ ] **步骤 2：代码质量和安全审查**

重点审查 wire 顺序、重试上限、资源所有权、timer/取消竞态、UI 输入隔离、SQLite 事务、凭据删除顺序、诊断 allowlist。Critical/Important 必须为 0。

- [ ] **步骤 3：新鲜完整验证**

```powershell
dotnet restore WinARD.sln -p:Platform=x64
dotnet format WinARD.sln --verify-no-changes --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore -m:1
pwsh -File packaging/tests/check-licenses.Tests.ps1
pwsh -File packaging/check-licenses.ps1
pwsh -File packaging/check-vulnerabilities.ps1
git diff --check
```

预期：格式、构建和所有测试 0 失败；许可证/漏洞检查成功；无 whitespace 错误。

- [ ] **步骤 4：生成并验证成品**

```powershell
pwsh -File packaging/portable.ps1 -Version 0.1.0.0
pwsh -File packaging/verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
Get-FileHash artifacts/WinARD-portable-win-x64.zip, artifacts/WinARD.msix -Algorithm SHA256
```

预期：portable ZIP、未签名 MSIX 和 SHA256SUMS 存在，结构/版本/AMD64/managed metadata/hash/秘密文件名检查全部通过。

- [ ] **步骤 5：提交最终修正并推送**

仅在上述新鲜验证后提交审查修正；确认 `git status` 只含预期内容，再推送 `master` 到 `origin`。不把真机外部门禁描述为通过。
