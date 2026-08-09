# 会话输入、诊断与连接质量实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 修复 Enter 未被远程会话转发的问题，增加当前会话诊断入口，并用真实画面请求—响应样本显示连接质量。

**架构：** 输入层使用 `handledEventsToo` 捕获被 WinUI `Button` 消费的键盘路由事件，保留现有映射和协议发送链。质量计算放入独立、可测试的纯 C# 跟踪器，由 `RemoteSessionViewModel` 在发送画面请求和收到画面响应时驱动；窗口仅负责显示状态和调用现有诊断服务。

**技术栈：** .NET 8、C#、WinUI 3、CommunityToolkit.Mvvm、xUnit、`TimeProvider`。

**仓库约束：** 当前工作树已有 ARD padding 修复，必须保留；按用户要求不执行 `git add` 或 `git commit`。

---

## 文件结构

- 创建 `src/WinARD.Desktop/ViewModels/ConnectionQualityTracker.cs`：纯逻辑质量样本、EMA 和分级。
- 创建 `tests/WinARD.Desktop.Tests/ViewModels/ConnectionQualityTrackerTests.cs`：阈值、EMA、断开和无样本测试。
- 修改 `src/WinARD.Desktop/ViewModels/RemoteSessionViewModel.cs`：记录画面请求与响应，公开质量状态。
- 修改 `tests/WinARD.Desktop.Tests/ViewModels/RemoteSessionViewModelTests.cs`：验证请求—响应数据流与断开状态。
- 修改 `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`：以 `handledEventsToo` 注册键盘事件、诊断按钮事件和质量视觉刷新。
- 修改 `src/WinARD.Desktop/Views/RemoteSessionWindow.xaml`：增加诊断按钮、质量圆点和可访问文本。
- 创建 `tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`：锁定 Enter 路由注册和窗口清理。
- 修改 `tests/WinARD.Desktop.Tests/Views/ConnectionErrorCardIntegrationTests.cs`：锁定常驻诊断按钮与质量指示灯。

### 任务 1：锁定并修复 Enter 路由

- [ ] **步骤 1：编写失败的窗口输入集成测试**

在 `RemoteSessionWindowInputIntegrationTests.cs` 读取窗口源码并断言键盘事件使用 handled-events-too 注册：

```csharp
[Fact]
public void Remote_window_captures_button_handled_key_down_and_key_up()
{
    var source = File.ReadAllText(RepositoryFile(
        "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

    Assert.Contains("InputSurface.AddHandler(UIElement.KeyDownEvent", source, StringComparison.Ordinal);
    Assert.Contains("InputSurface.AddHandler(UIElement.KeyUpEvent", source, StringComparison.Ordinal);
    Assert.Contains("handledEventsToo: true", source, StringComparison.Ordinal);
    Assert.Contains("InputSurface.RemoveHandler(UIElement.KeyDownEvent", source, StringComparison.Ordinal);
    Assert.Contains("InputSurface.RemoveHandler(UIElement.KeyUpEvent", source, StringComparison.Ordinal);
}
```

- [ ] **步骤 2：运行测试验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RemoteSessionWindowInputIntegrationTests
```

预期：FAIL，因为当前窗口只使用 `KeyDown +=` 和 `KeyUp +=`。

- [ ] **步骤 3：实现最小路由修复**

在窗口构造时保存委托并注册：

```csharp
_keyDownHandler = OnKeyDown;
_keyUpHandler = OnKeyUp;
InputSurface.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, handledEventsToo: true);
InputSurface.AddHandler(UIElement.KeyUpEvent, _keyUpHandler, handledEventsToo: true);
```

在窗口关闭清理中使用相同委托调用 `RemoveHandler`。删除旧的 `InputSurface.KeyDown +=` 与 `InputSurface.KeyUp +=`，CharacterReceived 和指针事件保持不变。

- [ ] **步骤 4：验证 Enter 映射和窗口测试绿灯**

在 `WindowsInputMapperTests` 增加 Enter down/up 断言 `[(0xff0d, true), (0xff0d, false)]`，运行两个定向测试类，预期全部 PASS。

### 任务 2：增加当前会话诊断入口

- [ ] **步骤 1：编写失败的 XAML/接线测试**

在 `ConnectionErrorCardIntegrationTests` 断言：

```csharp
Assert.Contains("RemoteExportDiagnosticsButton", xaml, StringComparison.Ordinal);
Assert.Contains("Click=\"OnExportDiagnosticsClicked\"", xaml, StringComparison.Ordinal);
Assert.Contains("ExportDiagnosticsAsync", window, StringComparison.Ordinal);
```

- [ ] **步骤 2：运行定向测试验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ConnectionErrorCardIntegrationTests
```

预期：FAIL，因为工具栏没有诊断按钮。

- [ ] **步骤 3：实现诊断按钮**

在命令栏添加：

```xml
<AppBarButton
    x:Name="ExportDiagnosticsButton"
    AutomationProperties.AutomationId="RemoteExportDiagnosticsButton"
    Click="OnExportDiagnosticsClicked"
    Label="导出诊断" />
```

窗口构造后设置 `ExportDiagnosticsButton.IsEnabled = _diagnosticExportService is not null;`。点击处理程序使用 `_inputOperations` 之外的独立异步观察路径调用现有 `ExportDiagnosticsAsync`，用户取消保存时不报错；窗口关闭后按钮禁用。

- [ ] **步骤 4：运行诊断入口测试验证绿灯**

运行同一测试命令，预期 PASS。

### 任务 3：用 TDD 实现连接质量跟踪器

- [ ] **步骤 1：编写失败的纯逻辑测试**

创建可控 `FakeTimeProvider`，覆盖：初始灰色、149 ms 绿色、150/500 ms 黄色、501 ms 红色、EMA `0.25 * sample + 0.75 * previous`、无待处理请求时忽略响应、断开后红色。

期望 API：

```csharp
var tracker = new ConnectionQualityTracker(timeProvider);
tracker.BeginRequest();
timeProvider.Advance(TimeSpan.FromMilliseconds(149));
var snapshot = tracker.CompleteResponse();
Assert.Equal(ConnectionQualityLevel.Good, snapshot.Level);
Assert.Equal(149, snapshot.ResponseMilliseconds);
```

- [ ] **步骤 2：运行测试验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~ConnectionQualityTrackerTests
```

预期：编译失败，因为跟踪器类型尚不存在。

- [ ] **步骤 3：实现最小跟踪器**

定义：

```csharp
public enum ConnectionQualityLevel { Unmeasured, Good, Fair, Poor, Disconnected }
public sealed record ConnectionQualitySnapshot(
    ConnectionQualityLevel Level,
    int? ResponseMilliseconds,
    string DisplayText);
```

`BeginRequest` 保存 `TimeProvider.GetTimestamp()`；`CompleteResponse` 在有待处理时间戳时计算毫秒、更新 EMA、清除待处理并按阈值生成快照；`Disconnect` 清除待处理并返回“已断开”。毫秒显示使用四舍五入后的非负整数。

- [ ] **步骤 4：运行跟踪器测试验证绿灯**

运行同一定向测试，预期全部 PASS。

### 任务 4：把质量样本接入会话数据流

- [ ] **步骤 1：编写失败的 ViewModel 测试**

使用可控时间和脚本化 runtime：首次 `RequestFramebufferUpdateAsync` 后推进 80 ms，返回一个 `RemoteFramebufferMessage`，断言 `ConnectionQuality.Level == Good`、文本为“良好 · 80 ms”；随后推进第二个样本验证 EMA。让 runtime 失败，断言最终为 `Disconnected`。

- [ ] **步骤 2：运行 ViewModel 定向测试验证红灯**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64 --filter FullyQualifiedName~RemoteSessionViewModelTests
```

预期：FAIL，因为 ViewModel 尚未公开或更新质量状态。

- [ ] **步骤 3：实现请求—响应接线**

构造函数接受可选 `TimeProvider? timeProvider = null` 并创建跟踪器。增加：

```csharp
public ConnectionQualitySnapshot ConnectionQuality
{
    get => _connectionQuality;
    private set => SetProperty(ref _connectionQuality, value);
}
```

提取 `RequestFramebufferUpdateTrackedAsync`：请求成功后调用 `BeginRequest()`。收到 `RemoteFramebufferMessage` 或 `RemoteCursorMessage` 后调用 `CompleteResponse()`，通过 dispatcher 更新属性。剪贴板、Bell 和控制消息不完成样本。`MonitorLoopsAsync` 的终止路径和显式 Dispose 路径更新为 `Disconnect()`。

- [ ] **步骤 4：运行 ViewModel 测试验证绿灯**

运行同一测试命令，预期全部 PASS。

### 任务 5：显示质量圆点和文本

- [ ] **步骤 1：编写失败的 UI 合约测试**

断言 XAML 包含 `RemoteConnectionQualityIndicator`、`Ellipse`、质量文本以及不只依赖颜色的 Automation 名称；断言窗口的属性变更处理会调用 `UpdateConnectionQualityVisual()`。

- [ ] **步骤 2：运行 UI 合约测试验证红灯**

运行 ConnectionErrorCardIntegrationTests，预期 FAIL。

- [ ] **步骤 3：实现 UI**

将状态栏右侧改为水平 StackPanel：8px 圆点 + 文本。窗口按枚举选择灰、绿、黄、红 brush，并同步设置：

```csharp
QualityText.Text = ViewModel.ConnectionQuality.DisplayText;
AutomationProperties.SetName(
    QualityIndicator,
    $"连接质量：{ViewModel.ConnectionQuality.DisplayText}");
```

在 `OnViewModelPropertyChanged` 中只在质量属性变化时刷新，UI 更新必须留在 dispatcher/UI 线程。

- [ ] **步骤 4：运行桌面全量测试**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release -p:Platform=x64
```

预期：全部 PASS，0 失败。

### 任务 6：完整验证和打包

- [ ] **步骤 1：格式和差异检查**

```powershell
dotnet format WinARD.sln --verify-no-changes --no-restore
git diff --check
```

预期：退出码 0。

- [ ] **步骤 2：Release x64 构建和全量测试**

```powershell
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore -warnaserror
dotnet test WinARD.sln -c Release -p:Platform=x64 --no-build --no-restore
```

预期：0 警告、0 错误、0 测试失败。

- [ ] **步骤 3：打包和验证**

```powershell
.\packaging\portable.ps1 -Version 0.1.0.0
.\packaging\verify-artifacts.ps1 -ExpectedVersion 0.1.0.0
```

将便携 ZIP 复制为带日期和功能名的新文件，确认包含 `WinARD.Desktop.exe`、`WinARD.OpenSshAskPass.exe`、`Microsoft.UI.dll`、`e_sqlite3.dll`，输出完整 SHA-256。

- [ ] **步骤 4：交付说明**

明确区分自动验证与 macOS 26.5 实机验证；不宣称 Enter 登录或连接质量显示已在真实 Mac 上成功，直到用户验证。
