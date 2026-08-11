# ARD MVS 协议捕获运行手册

本手册用于复现两类隔离研究证据：Remote Desktop Manager（RDM）连接本机回环模拟服务端时发送的 ARD 初始化声明，以及真实 Mac 返回的候选编码有界载荷前缀。它不是常规远程连接流程，也不证明候选编码就是 MVS。

所有命令都应从仓库根目录 `F:\Documents\Windows ARD Client` 的 PowerShell 中执行。研究产物位于已被 Git 忽略的 `artifacts\protocol-research`；不得把其中的 JSON、manifest 或 `.bin` 文件强制加入 Git。

## 1. 前置条件与构建

准备以下环境：

- Windows x64、.NET 8 SDK；
- 已安装 Remote Desktop Manager，并能新建 Apple Remote Desktop（ARD）连接；
- 本机回环端口（默认 5901）未被占用；
- 进行真实 Mac 捕获时，目标 Mac 已启用远程管理，且使用专用测试画面和获授权的测试账户。

构建探针并把可执行文件保存为 PowerShell 路径对象：

```powershell
dotnet publish tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj -c Release -p:Platform=x64 --self-contained false
$probe = Resolve-Path 'tools\WinARD.ProtocolProbe\bin\x64\Release\net8.0-windows10.0.19041.0\WinARD.ProtocolProbe.exe'
```

后续所有探针调用都必须使用 PowerShell 调用运算符 `& $probe`。不要把带引号的 exe 路径直接写在命令开头，否则 PowerShell 会把参数解析成表达式并报 `Unexpected token`。

## 2. 配置临时 RDM 条目

本阶段的监听器只绑定 `127.0.0.1`、只接受一个连接，并在收到首个 FramebufferUpdateRequest 后关闭连接。它会完成固定的 ARD security type 30 握手，但不会解密或验证认证响应。

在 RDM 中新建一个仅用于本次研究的临时 Apple Remote Desktop（ARD）条目，并设置：

1. 主机/IP 为 `127.0.0.1`，端口与 `$env:WINARD_LISTEN_PORT` 相同；不要配置网关、SSH 隧道或端口转发。
2. 用户名使用 `rdm-probe`，密码使用 `synthetic-only`。这些是合成值，不得使用任何真实账户或复用密码。
3. 关闭剪贴板同步。捕获期间不要按键、点击或移动鼠标。
4. 保存一个干净模板，再复制出七个临时条目。除矩阵中指定的“图像质量”和“分辨率”外，所有设置必须完全相同。

RDM 版本之间的设置页分组可能不同；应按字段含义选择 Apple Remote Desktop 条目中的 Image quality（Full、High、Medium、Low、Adaptive）和 Resolution（Default、Low、High），不要用显示窗口缩放代替远端 Resolution 设置。

## 3. 捕获七组 RDM 声明

使用两个窗口：在 PowerShell 窗口 A 启动一次监听器；看到 `RDM capture listening on 127.0.0.1:5901 ...` 后，在 RDM 中连接对应的临时条目。连接在数秒内被服务端关闭、随后出现 `RDM capture saved: ...` 是预期行为。一次捕获结束后再开始下一组，不要并行运行监听器。

先设置端口：

```powershell
$env:WINARD_LISTEN_PORT = '5901'
```

严格按下表逐组操作：

| Profile/文件 | RDM 图像质量 | RDM 分辨率 | 本组唯一允许改变的设置 |
|---|---|---|---|
| `full-default` | Full | Default | 基线 |
| `high-default` | High | Default | 图像质量 |
| `medium-default` | Medium | Default | 图像质量 |
| `low-default` | Low | Default | 图像质量 |
| `adaptive-default` | Adaptive | Default | 图像质量 |
| `adaptive-low` | Adaptive | Low | 分辨率 |
| `adaptive-high` | Adaptive | High | 分辨率 |

每组先在窗口 A 运行相应命令，再在 RDM 中连接名称相同的条目：

```powershell
& $probe --listen-rdm full-default 'artifacts\protocol-research\rdm\full-default.json'
& $probe --listen-rdm high-default 'artifacts\protocol-research\rdm\high-default.json'
& $probe --listen-rdm medium-default 'artifacts\protocol-research\rdm\medium-default.json'
& $probe --listen-rdm low-default 'artifacts\protocol-research\rdm\low-default.json'
& $probe --listen-rdm adaptive-default 'artifacts\protocol-research\rdm\adaptive-default.json'
& $probe --listen-rdm adaptive-low 'artifacts\protocol-research\rdm\adaptive-low.json'
& $probe --listen-rdm adaptive-high 'artifacts\protocol-research\rdm\adaptive-high.json'
```

这些命令是七次独立运行，不是一次性粘贴后等待七个连接。每个目标文件必须与命令中的 profile 同名；若目标文件已经存在，先确认它是否应保留，再使用新的空输出路径，避免混淆不同批次证据。

每组结束后检查 JSON 的结构和终止点：

```powershell
$capture = Get-Content 'artifacts\protocol-research\rdm\full-default.json' -Raw | ConvertFrom-Json
$capture | Select-Object schemaVersion, profile, reachedFramebufferRequest, stoppedAtUnknownMessageType
```

期望 `schemaVersion` 为 `1`、`profile` 与文件名一致、`reachedFramebufferRequest` 为 `True`、`stoppedAtUnknownMessageType` 为空。若出现未知消息类型，停止余下矩阵，不要根据 TCP 分块猜测消息长度。

## 4. 比较 Full 与 Adaptive

完成并验证 `full-default` 和 `adaptive-default` 后运行：

```powershell
& $probe --compare-rdm-captures `
  'artifacts\protocol-research\rdm\full-default.json' `
  'artifacts\protocol-research\rdm\adaptive-default.json'
```

只有输出恰好一个 `RDM comparison candidate signed encoding ID: ...` 才能进入真实 Mac 捕获。若工具报告零个或多个 Adaptive-only ID，停止；不要人工选择 ID，也不要运行下一节命令。

## 5. 真实 Mac 捕获安全硬门

候选载荷前缀可能包含可还原的屏幕图像数据。运行真实 Mac 命令前，操作者必须亲眼逐项确认：

- 目标 Mac 只启用并显示一个显示器；额外物理或虚拟显示器已断开或在系统中停用，而不只是被客户端隐藏；
- 唯一显示器全屏展示专用、无敏感信息的合成测试图；
- 已关闭通知预览、桌面小组件、菜单栏敏感内容，以及显示私人文件名、账户名、消息或浏览器内容的窗口；
- 没有其他用户或自动化会在捕获期间切换画面；
- Full/Adaptive 比较已产生唯一候选 ID；
- 输出目录 `artifacts\protocol-research\mac\adaptive-default` 不存在或为空，且其中没有需要保留的旧证据。

`--confirm-synthetic-screen` 只是操作者作出的明确声明，程序无法判断屏幕是否真的脱敏。任何一项不能确认时都不得运行。

设置真实 Mac 的连接信息。密码不要写入环境变量或命令行；探针会在交互式控制台中无回显读取：

```powershell
$env:WINARD_HOST = '测试 Mac 的主机名或 IP'
$env:WINARD_PORT = '5900'
$env:WINARD_USERNAME = '获授权的测试账户'
```

再次目视确认目标 Mac 仍是合成测试图，然后运行：

```powershell
& $probe --capture-differential-prefix `
  'artifacts\protocol-research\rdm\full-default.json' `
  'artifacts\protocol-research\rdm\adaptive-default.json' `
  'artifacts\protocol-research\mac\adaptive-default' `
  --confirm-synthetic-screen
```

成功输出只报告 signed encoding ID、矩形、前缀字节数，以及文件名 `manifest.json` 和 `payload-prefix.bin`；不会打印载荷十六进制。该文件只是最多 64 KiB 的有界前缀，不是完整帧或可分发测试夹具。

## 6. 验证 manifest、长度和哈希

捕获后立即验证 manifest 与二进制文件一致：

```powershell
$manifestPath = 'artifacts\protocol-research\mac\adaptive-default\manifest.json'
$payloadPath = 'artifacts\protocol-research\mac\adaptive-default\payload-prefix.bin'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$payload = Get-Item $payloadPath
$hash = (Get-FileHash $payloadPath -Algorithm SHA256).Hash

if ($manifest.schemaVersion -ne 1) { throw 'Unexpected manifest schema.' }
if ($manifest.syntheticScreenConfirmed -ne $true) { throw 'Synthetic-screen confirmation is absent.' }
if ($payload.Length -ne $manifest.prefixLength) { throw 'Payload length does not match manifest.' }
if ($hash -cne $manifest.payloadSha256) { throw 'Payload SHA-256 does not match manifest.' }

$manifest | ConvertTo-Json -Depth 5
$hash
```

manifest 应只含 schema、signed encoding ID、矩形、前缀长度、SHA-256 和合成画面确认，不得含主机、端口、用户名、密码、绝对路径或载荷内容。若长度或哈希不一致，不要继续提取证据；保留现场并先排查写入或文件被改动的问题。

## 7. 清理与禁止提交规则

完成证据提取后：

1. 清除当前 PowerShell 会话中的真实 Mac 连接变量：

   ```powershell
   Remove-Item Env:WINARD_HOST, Env:WINARD_PORT, Env:WINARD_USERNAME -ErrorAction SilentlyContinue
   Remove-Item Env:WINARD_LISTEN_PORT -ErrorAction SilentlyContinue
   ```

2. 删除 RDM 中的七个临时条目，确保合成凭据没有被保留到日常连接配置。
3. `artifacts\protocol-research` 已由仓库根目录 `.gitignore` 中的 `artifacts/` 规则忽略。不得使用 `git add -f`、不得复制到受版本控制目录，也不得提交任何捕获 JSON、`manifest.json` 或 `payload-prefix.bin`。
4. 正式证据文档只能记录经核验的协议数值、矩形、长度和 SHA-256，不得嵌入二进制载荷、凭据、主机名或绝对路径。
5. 普通 WinARD diagnostics 不包含 `payload-prefix.bin`。完成证据提取后，由用户决定保留还是删除该敏感前缀；不再需要时可单独删除：

   ```powershell
   Remove-Item -LiteralPath 'artifacts\protocol-research\mac\adaptive-default\payload-prefix.bin'
   ```

提交文档前必须确认研究产物仍未被 Git 跟踪或暂存：

```powershell
git ls-files artifacts/protocol-research
git status --short --ignored artifacts/protocol-research
```

第一条命令必须无输出；第二条只能显示忽略状态 `!!`，不能显示已暂存状态。
