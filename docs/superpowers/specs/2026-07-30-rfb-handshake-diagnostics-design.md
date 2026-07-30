# RFB 握手失败诊断设计

## 目标

在不记录主机、原始 RFB Banner、安全类型字节或认证数据的前提下，把当前仅显示为 `RFB_PROTOCOL_ERROR / Negotiating` 的失败定位到明确的 RFB 握手边界。

## 方案

为 `RfbProtocolFailureInfo` 增加独立的 `RfbHandshakeStage`、`ExpectedByteCount` 和 `ActualByteCount` 安全字段。`RfbReader` 在精确读取提前结束时记录期望与实际字节数；`RfbHandshake` 在读取或解析版本 Banner、读取 RFB 3.3 安全类型、读取安全类型数量及安全类型列表时附加对应阶段。

阶段枚举只描述协议位置：`VersionBanner`、`VersionParse`、`SecurityType33`、`SecurityTypeCount`、`SecurityTypes`。不保存原始内容，不自动重试，不改变版本或安全类型选择，也不改变明文/加密策略。

## 数据流

底层精确读取失败先产生 `TruncatedRead + ExpectedByteCount + ActualByteCount`；握手层使用现有 `WithContext` 补充 `HandshakeStage`。桌面诊断导出层仅把这些枚举和整数作为 Public 字段写入诊断 ZIP。

格式异常但读取完整时使用 `VersionParse`，字节计数为 12/12；不受支持的合法版本仍保持现有 `UnsupportedRfbVersionException`，不伪装成格式错误。

## 安全约束

- 不记录 Banner 文本或十六进制内容。
- 不记录安全类型列表内容。
- 不记录用户名、主机、密码、key、IV、明文或密文。
- 字节计数必须为非负且只来自本次有界读取。

## 测试

- 短 Banner：`VersionBanner`，期望 12，实际为输入长度。
- 完整但畸形 Banner：`VersionParse`，12/12。
- Security Type 数量缺失：`SecurityTypeCount`，1/0。
- Security Type 列表截断：`SecurityTypes`，期望为声明数量，实际为收到数量。
- 桌面诊断只导出阶段和计数，不包含原始报文标记。
- 严格 Release 构建和全量测试保持通过。
