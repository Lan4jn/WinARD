using WinARD.Domain.Errors;

namespace WinARD.Desktop.ViewModels;

public enum ConnectionErrorActionKind
{
    Retry,
    ReenterCredentials,
    UnlockVault,
    OpenHelp,
    CopyCorrelationId,
    ExportDiagnostics,
    Cancel,
    Disconnect,
    ReplaceHostKey,
}

public sealed record ConnectionErrorAction
{
    public ConnectionErrorAction(string label, ConnectionErrorActionKind kind)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Action label cannot be blank.", nameof(label));
        }

        Label = label.Trim();
        Kind = kind;
    }

    public string Label { get; }

    public ConnectionErrorActionKind Kind { get; }
}

public sealed record ConnectionErrorViewModel(
    string Title,
    string Summary,
    string CorrelationId,
    IReadOnlyList<ConnectionErrorAction> Actions)
{
    public static ConnectionErrorViewModel FromError(
        WinArdError error,
        string? oldFingerprint = null,
        string? newFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var export = new ConnectionErrorAction("导出脱敏诊断", ConnectionErrorActionKind.ExportDiagnostics);
        return error.Code switch
        {
            "MACOS_SCREEN_RECORDING_DENIED" or "MACOS_ACCESSIBILITY_DENIED" => Create(
                "需要 macOS 权限",
                "请在 Mac 的“系统设置 > 隐私与安全性”中允许屏幕录制和辅助功能，然后重试。",
                error,
                new("打开设置说明", ConnectionErrorActionKind.OpenHelp),
                new("重试", ConnectionErrorActionKind.Retry),
                export),
            "SSH_HOST_KEY_CHANGED" => Create(
                "SSH 主机密钥已更改",
                HostKeySummary(oldFingerprint, newFingerprint),
                error,
                new("取消", ConnectionErrorActionKind.Cancel),
                new("显式替换主机密钥", ConnectionErrorActionKind.ReplaceHostKey),
                export),
            "ARD_AUTH_REJECTED" or "RFB_CONNECTION_REJECTED" => Create(
                "认证失败",
                "Mac 拒绝了凭据。请重新输入用户名和密码后重试。",
                error,
                new("重新输入凭据", ConnectionErrorActionKind.ReenterCredentials),
                export),
            "VAULT_LOCKED" => Create(
                "凭据库已锁定",
                "请解锁凭据库，再继续连接。",
                error,
                new("解锁", ConnectionErrorActionKind.UnlockVault),
                export),
            "RFB_VERSION_UNSUPPORTED" or "RFB_SECURITY_UNSUPPORTED" or "RFB_PROTOCOL_ERROR" => Create(
                "协议不受支持",
                "远程服务使用 WinARD 当前不支持的协议或安全类型。",
                error,
                new("复制关联 ID", ConnectionErrorActionKind.CopyCorrelationId),
                export),
            "DNS_RESOLUTION_FAILED" or "TCP_CONNECTION_FAILED" or "TRANSPORT_TIMEOUT" or
                "SSH_CONNECTION_FAILED" => Create(
                    "网络连接失败",
                    "无法连接到远程设备。请检查网络、地址和防火墙后重试。",
                    error,
                    new("重试", ConnectionErrorActionKind.Retry),
                    export),
            "REMOTE_SESSION_INTERRUPTED" or "REMOTE_PRESENTATION_FAILED" or "REMOTE_INPUT_FAILED" => Create(
                "远程会话已中断",
                "远程会话发生错误并已停止。可以断开窗口或导出脱敏诊断。",
                error,
                new("断开", ConnectionErrorActionKind.Disconnect),
                new("复制关联 ID", ConnectionErrorActionKind.CopyCorrelationId),
                export),
            _ => Create(
                "连接出现问题",
                "连接未能完成。可复制关联 ID，或导出不含秘密内容的诊断包。",
                error,
                new("复制关联 ID", ConnectionErrorActionKind.CopyCorrelationId),
                export),
        };
    }

    private static ConnectionErrorViewModel Create(
        string title,
        string summary,
        WinArdError error,
        params ConnectionErrorAction[] actions) =>
        new(title, summary, error.CorrelationId, actions);

    private static string HostKeySummary(string? oldFingerprint, string? newFingerprint) =>
        $"为防止中间人攻击，已取消连接。旧 SHA256：{SafeFingerprint(oldFingerprint)}；新 SHA256：{SafeFingerprint(newFingerprint)}。请独立核验后再显式替换。";

    private static string SafeFingerprint(string? value) => string.IsNullOrWhiteSpace(value)
        ? "不可用"
        : value.Trim();
}
