# WinARD

WinARD 是面向 Windows 10/11 x64 的 Apple Remote Desktop（ARD）客户端。它使用 RFB/ARD Security Type 30 连接 macOS，提供设备库、TCP/SSH 连接、远程画面、键鼠、Unicode 文本、纯文本剪贴板和安全诊断导出。

## 当前状态

项目已具备可运行的 WinUI 3 桌面应用和自动化协议测试，主要能力包括：

- 保存、搜索、编辑和删除 Mac 连接；支持 Bonjour 发现。
- 直接 TCP（默认 5900）和 Windows OpenSSH 隧道。
- ARD 用户名/密码认证与 SSH 密码、私钥及首次主机密钥确认。
- Windows 凭据管理器、加密本地凭据库或每次询问三种凭据方式。
- D3D11 远程画面、脏矩形更新、远端光标、适应窗口与 100% 滚动。
- 鼠标、键盘、Unicode 输入、组合键释放和双向纯文本剪贴板。
- 结构化错误卡片、重试和经过脱敏/白名单过滤的诊断 ZIP 导出。

开发期 `ProtocolProbe` 曾对一台真实 Mac 完成 RFB 3.8、AppleRemoteDesktop（30）认证并抓取 3360×2100 首帧。主应用的完整实机交互、全部系统矩阵、两小时稳定性以及干净虚拟机安装仍是发布前外部门禁，不能据此视为已经完成兼容认证。

## 环境要求

- Windows 10 22H2 或 Windows 11，x64。
- .NET SDK 8.0 或更高版本；仓库通过 `global.json` 从 8.0.100 向前滚动。
- Visual Studio 2022 的 Windows App SDK/WinUI 3 支持，或相应 MSBuild 工具。
- 构建 MSIX 需要 Windows 10/11 SDK 的 `makeappx.exe`。
- SSH 路径需要 Windows OpenSSH Client（`ssh.exe`）。

## 在 Mac 上启用 ARD

1. 打开“系统设置”→“通用”→“共享”→“远程管理”。
2. 为将要登录的 macOS 用户授予所需的观察和控制权限。
3. 确认防火墙允许远程管理；直接 TCP 通常使用 5900 端口。
4. 如果使用 SSH 隧道，同时启用“远程登录”，并限制允许登录的用户。

不要把普通 VNC 密码当作 ARD 用户密码。WinARD 当前实现的是 AppleRemoteDesktop Security Type 30 的 macOS 用户认证。

## 构建和运行

```powershell
dotnet restore WinARD.sln -p:Platform=x64
dotnet format WinARD.sln --verify-no-changes --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore
dotnet run --project src/WinARD.Desktop/WinARD.Desktop.csproj -c Release -p:Platform=x64
```

真实 Mac 的开发期协议探测：

```powershell
dotnet run --project tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj -c Release -p:Platform=x64 -- --capture-first-frame artifacts\first-frame.bmp
dotnet run --project tools/WinARD.ProtocolProbe/WinARD.ProtocolProbe.csproj -c Release -p:Platform=x64 -- --pointer-smoke
```

探测器从 `WINARD_HOST` 和 `WINARD_USERNAME` 环境变量读取主机和用户名，未设置时会交互式读取；密码始终通过隐藏输入的交互提示读取。不要把密码写入命令行、脚本、日志或仓库。

`--pointer-smoke` 只发送一次按钮掩码为 0 的鼠标移动，将指针移动到远程画面中心，不会点击或拖动。运行时应观察 Mac 光标是否移动。成功输出只表示完整 RFB 报文已写入流；RFB 协议不会返回服务端已执行该输入的确认。

## 连接方式

直接 TCP 适合可信局域网中的 5900 端口。SSH 模式使用系统 `ssh.exe` 建立到远端回环地址的隧道，支持 SSH 密码或私钥；私钥口令只通过应用目录中固定文件名、固定绝对路径且与主程序同目录的 `WinARD.OpenSshAskPass.exe` 提供。发布签名、哈希与 ACL 校验仍属于外部发布门禁。首次主机密钥必须由用户确认，指纹变化默认拒绝。

ARD 密码可保存到：

- Windows 凭据管理器；
- 使用 Argon2id 和 AES-GCM 的本地加密凭据库；
- “每次询问”，不持久化秘密。

## 打包和验证

```powershell
pwsh -File packaging/tests/check-licenses.Tests.ps1
pwsh -File packaging/check-licenses.ps1
pwsh -File packaging/check-vulnerabilities.ps1
pwsh -File packaging/portable.ps1 -Version 0.1.0.0
pwsh -File packaging/verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
```

`portable.ps1` 会清理自身的暂存目录，然后生成：

- `artifacts/WinARD-portable-win-x64.zip`：self-contained portable 版本；
- `artifacts/WinARD.msix`：未签名的 MSIX 候选；
- `artifacts/SHA256SUMS.txt`：SHA-256 清单。

`verify-artifacts.ps1` 会先执行 ZIP 路径、重复项、大小和严格 SHA-256 清单检查，再使用 Windows SDK `makeappx unpack` 对 MSIX 的 BlockMap、Content Types 和包结构执行语义验证。关键二进制必须是可由 `PEReader` 解析的 AMD64 PE；主程序集还必须包含可读的 CLR metadata。脚本同时核对 manifest、EXE/DLL 文件版本、产品版本、程序集版本，以及 portable 与 MSIX 关键 payload 的 SHA-256 一致性。

未签名 MSIX 只用于工程验收；正式发布必须由受信任证书签名，并在签名后重建校验和与重新验证。

`-Version` 必须是每段 `0..65535`、无前导零的规范四段 MSIX 版本。发布工作流只接受严格的 `vMAJOR.MINOR.PATCH` tag，并映射为 `MAJOR.MINOR.PATCH.0`；手动运行则必须显式填写四段版本。版本会同时写入打包暂存 manifest、AssemblyVersion、FileVersion 和不带 git SHA 的 ProductVersion，不会修改源码 manifest。CI 使用的第三方 GitHub Actions 固定到已从官方仓库验证的完整 commit SHA。

许可证审计严格接受 `MIT`、`Apache-2.0`、`BSD-2-Clause`、`BSD-3-Clause`、`ISC`、`MS-PL`。缺少 SPDX expression、文件许可证、`LicenseRef`、未知标识或不在允许列表中的表达式都会阻止发布。

## 安全和诊断

- 不记录密码、私钥口令或剪贴板正文；注册秘密在进入诊断前遮盖。
- 诊断采用有界结构化事件和白名单 ZIP；默认不包含主机名/IP。
- SQLite 设备库不保存明文密码。
- SSH 主机密钥使用已知主机文件和每次连接的确认租约。
- 不要提交证书、私钥、vault、凭据、真实诊断包或测试画面。

## 已知限制

- 仅支持 Windows x64 和纯文本剪贴板；不支持文件传输、音频、打印和多显示器编排。
- 尚未在所有 macOS 12/13/14/15/26 与 Windows 10/11 组合上完成主应用实机验证。
- TCP 与 SSH、两种持久凭据后端、两小时连续会话和干净虚拟机安装仍需发布前执行。
- MSIX 产物默认未签名，不能视为可公开分发版本。
- 严格 NuGet SPDX 审计可能因上游包只声明许可证文件或缺少 expression 而失败；必须完成法律/依赖处置，不得绕过。

详见[兼容性矩阵](docs/testing/compatibility-matrix.md)和[发布检查清单](docs/testing/release-checklist.md)。

## 许可证

WinARD 使用 [Apache License 2.0](LICENSE)。第三方依赖见自动生成的 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
