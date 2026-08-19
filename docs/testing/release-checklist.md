# WinARD 发布检查清单

所有项目都必须针对同一候选 SHA-256 完成。`External gate` 不得被自动化测试或模拟服务端结果替代。

## 自动化工程门禁

- [ ] `dotnet format WinARD.sln --verify-no-changes --no-restore`
- [ ] Release x64 `dotnet build` 使用 `-warnaserror`，0 warning / 0 error
- [ ] 全量 `dotnet test`，0 failed
- [ ] `packaging/tests/check-licenses.Tests.ps1`
- [ ] `packaging/check-licenses.ps1`，所有直接/传递 NuGet 包均有允许的 SPDX expression
- [ ] `packaging/check-vulnerabilities.ps1`，无已知漏洞
- [ ] `packaging/portable.ps1`
- [ ] `packaging/verify-artifacts.ps1`
- [ ] tag 或手动输入版本与 MSIX manifest、验证参数和发布记录完全一致
- [ ] portable/MSIX 的 EXE FileVersion、ProductVersion 和 DLL AssemblyVersion 均等于四段发布版本
- [ ] `makeappx unpack` 默认验证成功，portable 与 MSIX 的关键 payload SHA-256 一致
- [ ] 关键 EXE/DLL 通过 PEReader 的 AMD64、section、optional header 和 managed metadata 检查
- [ ] GitHub Actions 的所有第三方 `uses:` 均固定到经过官方仓库验证的 40 位 commit SHA
- [ ] 秘密扫描覆盖仓库 diff、构建日志、测试日志、诊断 ZIP 和发布产物清单

## 包与签名

- [ ] MSIX identity 为 `WinARD`，publisher 与生产证书 subject 完全一致
- [ ] 生产代码签名证书有效、私钥受保护且未写入仓库/日志
- [ ] 证书 Subject 严格匹配 manifest Publisher `CN=WinARD Development`
- [ ] 使用时间戳服务签名 `WinARD.msix`
- [ ] `signtool verify /pa /all /v /tw` 成功，包内存在非空 `AppxSignature.p7x`
- [ ] 签名后重新生成 `SHA256SUMS.txt` 并重新运行产物验证
- [ ] 在全新 Windows 10 22H2 x64 虚拟机安装、启动、卸载 MSIX
- [ ] 在全新 Windows 11 x64 虚拟机安装、启动、卸载 MSIX
- [ ] 在两个干净系统解压 portable ZIP 并启动
- [ ] SmartScreen/Defender 结果已记录；无临时例外或测试证书残留

## 真实 Mac 兼容门禁

- [ ] Windows 10 22H2 × macOS 12/13/14/15/26 矩阵已填写
- [ ] Windows 11 × macOS 12/13/14/15/26 矩阵已填写
- [ ] macOS 12 和当前稳定版完成直接 TCP
- [ ] macOS 12 和当前稳定版完成 SSH 密码
- [ ] macOS 12 和当前稳定版完成 SSH 私钥及私钥口令
- [ ] Windows 凭据管理器完成保存、重启后读取、更新和删除
- [ ] 加密凭据库完成创建、解锁、重启后读取、错误口令和篡改拒绝
- [ ] 画面、脏矩形、缩放、鼠标、键盘、Unicode、组合键释放和双向纯文本剪贴板均有证据
- [ ] 首次 SSH 主机密钥确认和指纹变化拒绝均有证据
- [ ] 两小时连续会话完成；无崩溃、无卡住按键、无持续单调内存增长
- [ ] 自动、100%、75%、50%、25% 均以连接前配置验证实际首帧尺寸；会话中修改显示重新连接提示
- [ ] 首选配置不兼容时最多一次全新 `100% + BGRA32 + Zlib-first` 回退，且 UI/诊断一致
- [ ] 暂态断线显示重连 attempt/倒计时并可取消；认证、权限、协议和主机密钥错误不自动重连
- [ ] 全屏顶部热区、`Ctrl+Alt+T` 与 `Esc` 本地优先级完成真机交互验证
- [ ] Mac 端未安装 Helper、代理或虚拟显示器；只使用系统远程管理/远程登录
- [ ] 相同候选、显示器、链路和操作下记录各比例的 FPS/吞吐/响应；未达到门限不得宣称带宽改善

## 安全与诊断

- [ ] 日志、SQLite、凭据后端、诊断 ZIP 不含测试密码、私钥口令或剪贴板正文
- [ ] 默认诊断导出不包含主机名/IP；用户选择包含主机时仍无原始异常消息或秘密
- [ ] 导出白名单和大小上限经过恶意输入复测
- [ ] 凭据删除策略和“每次询问”不持久化经过复测
- [ ] 设置立即应用与保险库立即/自动锁定经过复测；凭据迁移失败保留原引用和源秘密
- [ ] 诊断包含期望/解析/实际比例、首帧尺寸、稳定 fallback 原因、重连状态和迁移计数
- [ ] 诊断恶意注入 host/user/path/password/clipboard/raw exception/credential key/像素后 ZIP 完全不含对应标记
- [ ] `THIRD-PARTY-NOTICES.md` 与候选 restore 结果一致并经人工复核

## 发布记录

- [ ] 版本、commit、候选 SHA-256、签名证书 thumbprint、测试人员和日期已记录
- [ ] 已知限制与未完成项写入发布说明
- [ ] 所有失败/跳过项有负责人和书面风险接受；否则停止发布
