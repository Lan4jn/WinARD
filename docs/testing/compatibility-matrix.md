# WinARD 兼容性矩阵

状态定义：

- `Pass`：有本次发布对应的可复核证据。
- `Not run / External gate`：尚未执行，发布前必须由真实设备或干净虚拟机完成。
- `Partial evidence`：只有协议探测证据，不能推断主应用完整功能。

## 已知真实证据

| Windows | macOS | 路径 | 认证 | 画面 | 输入 | 剪贴板 | 时长 | 状态 | 证据 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Windows x64（版本未记录） | 版本未记录 | ProtocolProbe / TCP | RFB 3.8；AppleRemoteDesktop（30）；成功 | 抓取 3360×2100 首帧 | 未测试 | 未测试 | 单次探测 | Partial evidence | 用户提供控制台输出和 `artifacts/first-frame.bmp`；不是主应用实机会话 |

百分比分辨率（自动/100%/75%/50%/25%）、首帧安全回退、意外断线自动重连、全屏快捷键、设置和凭据迁移尚无同一候选的真机证据，状态均为 `Not run / External gate`。百分比分辨率仅在连接前应用；会话中修改需要重新连接。Mac 端保持零安装，不部署 Helper 或代理。不得从自动化测试推断带宽改善。

## 发布目标矩阵

下列每一行均需要在对应系统上使用正式候选产物重新执行，不得用模拟服务端或其他版本结果代替。

| Windows | macOS | TCP ARD 认证 | SSH 密码 | SSH 私钥 | 画面 | 键鼠/Unicode | 双向纯文本剪贴板 | Windows 凭据管理器 | 加密凭据库 | 连续时长 | 状态 | 证据 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Windows 10 22H2 x64 | 12 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 2 小时未运行 | Not run / External gate | 待附日志、截图、版本、候选 SHA-256 |
| Windows 10 22H2 x64 | 13 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 10 22H2 x64 | 14 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 10 22H2 x64 | 15 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 10 22H2 x64 | 26 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 11 x64 | 12 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 2 小时未运行 | Not run / External gate | 待附日志、截图、版本、候选 SHA-256 |
| Windows 11 x64 | 13 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 11 x64 | 14 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 11 x64 | 15 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |
| Windows 11 x64 | 26 | Not run | Not run | Not run | Not run | Not run | Not run | Not run | Not run | 冒烟未运行 | Not run / External gate | 待补 |

## 证据要求

每次测试记录 Windows build、macOS build、连接路径、ARD/SSH 配置、候选文件 SHA-256、开始/结束时间、结果、脱敏日志位置和至少一张不含敏感信息的截图。两小时测试还需记录预热后工作集趋势、重连/断开情况以及诊断包秘密扫描结果。
