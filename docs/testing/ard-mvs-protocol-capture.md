# ARD MVS 协议捕获运行手册

## 2026-09-07 交接状态说明

已取得真实 RDM 初始声明（High 首选 1002，Adaptive 首选 **`1011`**），且真实 Mac 会话已确认在线选择该编码并返回 `EncodingId=1011` 矩形头（详见 [真实声明与服务器选择记录](2026-09-06-mvs-live-declarations.md)）。
当前本轮研发暂停继续采集与开发，无需用户额外操作或提供密码。后续开发者进场时，应先准备专用的有界 1011 样本采集工具与合成画面素材，再在用户配合下进行脱敏取证。

本手册用于复现两类隔离研究证据：Remote Desktop Manager（RDM）连接本机回环模拟服务端时发送的 ARD 初始化声明，以及真实 Mac 返回的候选编码有界载荷前缀。它不是常规远程连接流程，也不证明候选编码就是 MVS。

所有命令都应从仓库根目录 `F:\Documents\Windows ARD Client` 的同一个 PowerShell 会话中执行。研究产物位于已被 Git 忽略的 `artifacts\protocol-research`；不得把其中的 JSON、manifest 或 `.bin` 文件强制加入 Git。

## 1. 构建并创建唯一批次

准备 Windows x64、.NET 8 SDK、已安装的 Remote Desktop Manager，以及一个未占用的本机回环端口。构建探针并把可执行文件保存为 PowerShell 路径对象：

```powershell
dotnet publish tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj -c Release -p:Platform=x64 --self-contained false
$probe = Resolve-Path 'tools\WinARD.ProtocolProbe\bin\x64\Release\net8.0-windows10.0.19041.0\WinARD.ProtocolProbe.exe'
```

后续所有探针调用都必须使用 PowerShell 调用运算符 `& $probe`。不要把带引号的 exe 路径直接写在命令开头，否则 PowerShell 会把参数解析成表达式并报 `Unexpected token`。

每次完整矩阵使用一个全新的时间戳批次。以下变量创建后不得在本批次中重新赋值：

```powershell
$batchRoot = Join-Path 'artifacts\protocol-research' (Get-Date -Format 'yyyyMMdd-HHmmss')
if (Test-Path -LiteralPath $batchRoot) { throw "Capture batch already exists: $batchRoot" }

$rdmRoot = Join-Path $batchRoot 'rdm'
$macRoot = Join-Path $batchRoot 'mac'
$macCaptureRoot = Join-Path $macRoot 'adaptive-default'
New-Item -ItemType Directory -Path $rdmRoot, $macRoot | Out-Null
```

监听、逐组验证、比较、真实 Mac 输出、manifest/hash 验证、清理和 Git 检查必须始终引用这三个根变量。不要改用另一个新路径后继续比较旧批次的固定路径。

先定义所有成功、失败或中止路径都要调用的环境清理动作：

```powershell
$clearCaptureEnvironment = {
  Remove-Item Env:WINARD_HOST, Env:WINARD_PORT, Env:WINARD_USERNAME -ErrorAction SilentlyContinue
  Remove-Item Env:WINARD_LISTEN_PORT -ErrorAction SilentlyContinue
  Write-Warning 'Close any active RDM probe connection and delete the seven temporary RDM entries.'
}
```

## 2. 记录实际 RDM 版本并冻结设置

捕获前先获取实际使用的 `RemoteDesktopManager.exe` ProductVersion：

```powershell
$rdmCommand = Get-Command 'RemoteDesktopManager.exe' -ErrorAction SilentlyContinue
$rdmCandidates = @(
  $rdmCommand.Source
  (Join-Path $env:ProgramFiles 'Devolutions\Remote Desktop Manager\RemoteDesktopManager.exe')
  $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Devolutions\Remote Desktop Manager\RemoteDesktopManager.exe' })
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$rdmExe = $rdmCandidates | Select-Object -First 1
if ($rdmExe) {
  $rdmProductVersion = (Get-Item -LiteralPath $rdmExe).VersionInfo.ProductVersion
} else {
  Write-Warning 'RemoteDesktopManager.exe was not found automatically. Open Help/About in RDM and record Product Version manually.'
  $rdmProductVersion = (Read-Host 'RDM Product Version shown in Help/About').Trim()
}
if ([string]::IsNullOrWhiteSpace($rdmProductVersion)) { throw 'RDM Product Version is required.' }
```

在 RDM 中新建一个仅用于本批次的 Apple Remote Desktop（ARD）模板条目。主机/IP 为 `127.0.0.1`，端口与稍后设置的 `$env:WINARD_LISTEN_PORT` 相同；用户名使用 `rdm-probe`，密码使用 `synthetic-only`。模拟服务端不会解密或验证认证响应，因此不得使用真实账户或复用密码。不要配置网关、SSH 隧道或端口转发。

复制模板形成七个临时条目。开始前逐项核对并记录 UI 中实际显示的标签和值：

| 设置类别 | 冻结要求 |
|---|---|
| 条目类型/协议 | Apple Remote Desktop（ARD），七组相同 |
| ARD/VNC engine 或实现 | 明确记录实际选择的 engine；七组相同，不允许某组自动切换实现 |
| 色深/Pixel format | 七组相同；记录 Auto 或明确位数的实际值 |
| 显示器选择 | 七组相同；记录主显示器、全部显示器或具体显示器的实际值 |
| 控制/观察模式 | 七组相同；明确记录 Control/Assist 或 View/Observe |
| 剪贴板 | 全部关闭同步 |
| 本地缩放/Fit/Stretch | 七组相同；不能把窗口缩放误当作远端 Resolution |
| 远端 Resolution | 仅 `adaptive-low` 和 `adaptive-high` 按矩阵改变 |
| Image quality | 仅 Full/High/Medium/Low/Adaptive 按矩阵改变 |
| 压缩、缓存、共享会话、自动重连 | 记录实际值并在七组间冻结 |
| 加密/安全模式 | 记录实际值并在七组间冻结 |
| 其他可能改变 encoding 声明的开关 | 全部记录并在七组间冻结 |

RDM 版本之间的设置页分组可能不同。每个泛化类别都必须分别记录当前 UI 中实际显示的原始标签和对应值，不能只记录类别名或留空；若该版本没有独立选项，记录其所在页面/分组的实际标签，并把值明确记为 `Not exposed`。捕获期间不要按键、点击或移动鼠标。

用以下脱敏记录保存版本和公共设置。它只保存在本批次 artifacts 中，不得提交；不要在回答中记录主机名、真实账户或路径：

```powershell
$commonSettings = [ordered]@{}
$settingCategories = @(
  'ARD/VNC engine',
  'Color depth or Pixel format',
  'Display selection',
  'Control or Observe mode',
  'Clipboard synchronization',
  'Local scaling or Fit mode',
  'Compression and cache options',
  'Shared session and reconnect options',
  'Encryption or security mode',
  'Other encoding-affecting switches'
)

foreach ($category in $settingCategories) {
  $uiLabel = ([string](Read-Host "Actual RDM UI label for category '$category'")).Trim()
  $value = ([string](Read-Host "Actual RDM value shown for '$uiLabel'")).Trim()
  if ([string]::IsNullOrWhiteSpace($uiLabel) -or [string]::IsNullOrWhiteSpace($value)) {
    & $clearCaptureEnvironment
    throw "RDM setting '$category' requires both an actual UI label and value."
  }

  $commonSettings[$category] = [ordered]@{
    uiLabel = $uiLabel
    value = $value
  }
}

# 第一阶段单变量对比矩阵（核心：High vs Adaptive，Resolution quality 固定 Default）
$matrix = @(
  [pscustomobject]@{ Profile = 'high-default';     Quality = 'High';     Resolution = 'Default' },
  [pscustomobject]@{ Profile = 'adaptive-default'; Quality = 'Adaptive'; Resolution = 'Default' }
)

# 扩展研究全矩阵（保留七组供横向深入排查）：
$matrixFull = @(
  [pscustomobject]@{ Profile = 'full-default';     Quality = 'Full';     Resolution = 'Default' },
  [pscustomobject]@{ Profile = 'high-default';     Quality = 'High';     Resolution = 'Default' },
  [pscustomobject]@{ Profile = 'medium-default';   Quality = 'Medium';   Resolution = 'Default' },
  [pscustomobject]@{ Profile = 'low-default';      Quality = 'Low';      Resolution = 'Default' },
  [pscustomobject]@{ Profile = 'adaptive-default'; Quality = 'Adaptive'; Resolution = 'Default' },
  [pscustomobject]@{ Profile = 'adaptive-low';     Quality = 'Adaptive'; Resolution = 'Low' },
  [pscustomobject]@{ Profile = 'adaptive-high';    Quality = 'Adaptive'; Resolution = 'High' }
)

[ordered]@{
  schemaVersion = 1
  rdmProductVersion = $rdmProductVersion
  commonSettings = $commonSettings
  matrix = $matrix
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $batchRoot 'rdm-context.json') -Encoding utf8
```

证据文档只能摘录非敏感的版本号和设置结论；`rdm-context.json` 与原始捕获一样不得进入 Git。

## 3. 捕获并硬校验 RDM 声明

监听器只绑定 `127.0.0.1`、只接受一个连接，并在收到首个 FramebufferUpdateRequest 后关闭连接。它完成固定的 ARD security type 30 握手，但不解密或验证认证响应。

设置端口后执行整个循环。每轮看到监听提示后，在 RDM 中连接提示所指的临时条目。连接被模拟服务端关闭，随后出现保存提示是预期行为。循环会在每组返回后立即验证 JSON；任一组失败都会停止后续矩阵。

```powershell
$env:WINARD_LISTEN_PORT = '5901'

try {
  foreach ($case in $matrix) {
    $capturePath = Join-Path $rdmRoot ($case.Profile + '.json')
    if (Test-Path -LiteralPath $capturePath) { throw "Capture already exists: $capturePath" }

    Write-Host "Set RDM entry '$($case.Profile)' to Quality=$($case.Quality), Resolution=$($case.Resolution), then connect it."
    & $probe --listen-rdm $case.Profile $capturePath
    if ($LASTEXITCODE -ne 0) { throw "Listener failed for profile $($case.Profile)." }

    $capture = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
    $propertyNames = @($capture.PSObject.Properties.Name)
    foreach ($requiredProperty in @('schemaVersion', 'profile', 'reachedFramebufferRequest', 'stoppedAtUnknownMessageType')) {
      if ($propertyNames -notcontains $requiredProperty) { throw "$($case.Profile): missing $requiredProperty." }
    }
    if ([int]$capture.schemaVersion -ne 1) { throw "$($case.Profile): schemaVersion is not 1." }
    if ([string]$capture.profile -cne [string]$case.Profile) { throw "$($case.Profile): profile does not match its file." }
    if ($capture.reachedFramebufferRequest -ne $true) { throw "$($case.Profile): first framebuffer request was not reached." }
    if ($null -ne $capture.stoppedAtUnknownMessageType) { throw "$($case.Profile): stopped at unknown message type $($capture.stoppedAtUnknownMessageType)." }
  }
} catch {
  & $clearCaptureEnvironment
  throw
}
```

出现未知消息类型时不要根据 TCP 分块猜测消息长度。保留本批次现场，先为该消息增加有界 parser 和测试。

## 4. 比较同一批次的 High 与 Adaptive（单变量对比）

只使用本批次变量构造比较输入：

```powershell
$highCapture = Join-Path $rdmRoot 'high-default.json'
$adaptiveCapture = Join-Path $rdmRoot 'adaptive-default.json'

try {
  & $probe --compare-rdm-captures $highCapture $adaptiveCapture
  if ($LASTEXITCODE -ne 0) { throw 'High/Adaptive comparison did not produce one candidate.' }
} catch {
  & $clearCaptureEnvironment
  throw
}
```

比较器不仅比较编码增减，还必须关注像素格式差异、声明顺序和消息序列。只有输出恰好一个 `RDM comparison candidate signed encoding ID: ...` 才能进入真实 Mac 捕获。若工具报告零个或多个差异 ID，停止；不要人工选择 ID。此时执行 `& $clearCaptureEnvironment`，处理临时 RDM 条目，并保留该批次供调查。严禁把唯一差异自动命名为已支持 MVS。

## 5. 真实 Mac 捕获安全硬门与交接指引

> [!NOTE]
> **2026-09-07 交接要点**：真实 macOS 服务端已确认在线选择并返回编码 `1011` 的矩形头。现有旧探针的 `--capture-known-encoding-prefix` 仅支持 1001/1002，后续开发者须先开发有界的 1011 样本工具（具备单次字节/时间上限、取消与资源释放，严禁从 TCP 分块推测长度）。当前暂停采集，后续开发者发起采集时再请用户配合。

候选载荷前缀可能包含可还原的屏幕图像数据。后续运行真实 Mac 采集前，必须亲眼逐项确认：

- 目标 Mac 优先只启用并显示一个显示器；若保留多个，所有会被捕获的显示区域均需全屏显示合成测试图；
- 唯一显示器全屏展示专用、无敏感信息的合成测试图；
- 已关闭通知预览、桌面小组件、菜单栏敏感内容，以及显示私人文件名、账户名、消息或浏览器内容的窗口；
- 没有其他用户或自动化会在捕获期间切换画面；
- 确认本次允许保存有界合成画面载荷（样本仅保存在本地 git 忽略的 artifacts 目录）；
- `$macCaptureRoot` 不存在，且本批次变量没有被重新赋值。

`--confirm-synthetic-screen` 只是操作者作出的明确声明，程序无法判断屏幕是否真的脱敏。任何一项不能确认时都不得运行；应立即调用 `& $clearCaptureEnvironment` 并处理临时 RDM 条目。

交互读取真实 Mac 的连接信息。密码不要写入环境变量或命令行；探针会在控制台中无回显读取：

```powershell
$env:WINARD_HOST = (Read-Host 'Authorized test Mac host or IP').Trim()
$env:WINARD_PORT = (Read-Host 'ARD port; press Enter only if you will then set 5900').Trim()
if ([string]::IsNullOrWhiteSpace($env:WINARD_PORT)) { $env:WINARD_PORT = '5900' }
$env:WINARD_USERNAME = (Read-Host 'Authorized test account').Trim()
$candidateEncodingId = (Read-Host 'Signed encoding ID from comparison (e.g. 1011)').Trim()
```

再次目视确认目标 Mac 仍是合成测试图，然后只用本批次路径运行：

```powershell
& $probe --capture-known-encoding-prefix `
  $candidateEncodingId `
  $macCaptureRoot `
  --confirm-synthetic-screen
if ($LASTEXITCODE -ne 0) { & $clearCaptureEnvironment; throw 'Encoding prefix capture failed.' }
```

成功输出只报告 signed encoding ID、矩形、前缀字节数，以及文件名 `manifest.json` 和 `payload-prefix.bin`；不会打印载荷十六进制。该文件只是最多 64 KiB 的有界前缀，不是完整帧或可分发测试夹具。

## 6. 验证同一批次的 manifest、长度和哈希

捕获后立即验证：

```powershell
try {
  $manifestPath = Join-Path $macCaptureRoot 'manifest.json'
  $payloadPath = Join-Path $macCaptureRoot 'payload-prefix.bin'
  $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  $payload = Get-Item -LiteralPath $payloadPath
  $hash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash

  if ($manifest.schemaVersion -ne 1) { throw 'Unexpected manifest schema.' }
  if ($manifest.syntheticScreenConfirmed -ne $true) { throw 'Synthetic-screen confirmation is absent.' }
  if ($payload.Length -ne $manifest.prefixLength) { throw 'Payload length does not match manifest.' }
  if ($hash -cne $manifest.payloadSha256) { throw 'Payload SHA-256 does not match manifest.' }

  $manifest | ConvertTo-Json -Depth 5
  $hash
} finally {
  & $clearCaptureEnvironment
}
```

manifest 应只含 schema、signed encoding ID、矩形、前缀长度、SHA-256 和合成画面确认，不得含主机、端口、用户名、密码、绝对路径或载荷内容。若长度或哈希不一致，不要继续提取证据；保留本批次现场并先排查文件是否被改动。

## 7. 所有退出路径的清理与禁止提交规则

无论捕获成功、命令失败、验证抛错，还是操作者用 Ctrl+C 中止，都必须执行以下动作：

1. 若 PowerShell 会话仍在，运行 `& $clearCaptureEnvironment`。若会话已经关闭，环境变量已随进程消失，但仍要完成下面的 RDM 清理。
2. 关闭 RDM 中仍活动的 probe 连接。成功结束或不再重试时删除七个临时条目；需要重试时将它们禁用并明确标为本批次研究专用，完成重试后删除。不得把普通生产条目改造成捕获条目。
3. `artifacts\protocol-research` 已由仓库根目录 `.gitignore` 中的 `artifacts/` 规则忽略。不得使用 `git add -f`、不得复制到受版本控制目录，也不得提交任何捕获 JSON、`rdm-context.json`、`manifest.json` 或 `payload-prefix.bin`。
4. 正式证据文档只能记录经核验的非敏感 RDM 版本/设置结论、协议数值、矩形、长度和 SHA-256，不得嵌入原始记录、二进制载荷、凭据、主机名或绝对路径。
5. 普通 WinARD diagnostics 不包含 `payload-prefix.bin`。证据提取后由用户决定是否删除该敏感前缀；不再需要时使用本批次变量删除：

   ```powershell
   $payloadPath = Join-Path $macCaptureRoot 'payload-prefix.bin'
   if (Test-Path -LiteralPath $payloadPath) { Remove-Item -LiteralPath $payloadPath }
   ```

最后确认同一批次研究产物仍未被 Git 跟踪或暂存：

```powershell
$tracked = @(git ls-files -- $batchRoot)
if ($tracked.Count -ne 0) { throw "Research artifacts are tracked: $($tracked -join ', ')" }

$batchStatus = @(git status --short --ignored -- $batchRoot)
if ($batchStatus | Where-Object { $_ -notmatch '^!! ' }) { throw 'Research artifacts are staged or unignored.' }
$batchStatus
```

`git ls-files` 必须无输出；状态输出只能以忽略标记 `!!` 开头，不能出现暂存状态。需要删除整个批次时，先人工确认 `$batchRoot` 正是本次时间戳目录，再使用文件管理器删除；不要对未经核对的计算路径执行递归删除。
