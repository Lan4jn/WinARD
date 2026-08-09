# WinUI 指针路由事件修复实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让稳定覆盖远程显示内容的 `FrameSurface` 接收并发送远程鼠标输入。

**架构：** `InputSurface` 继续负责键盘焦点和主机光标；所有 Pointer 委托迁移到设置透明背景的 `FrameSurface`，用 `AddHandler(..., handledEventsToo: true)` 注册，并在关闭时对称解绑。该目标排除错误卡与滚动条，发送、映射和协议链路保持不变。

**技术栈：** C# 12、.NET 8、WinUI 3、xUnit

---

### 任务 1：锁定 Pointer 路由注册行为

**文件：**
- 修改：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`

- [ ] **步骤 1：编写失败的测试**

新增测试，读取 `RemoteSessionWindow.xaml.cs` 并断言五类 Pointer 事件均出现
`InputSurface.AddHandler(UIElement.Pointer*Event`、均使用 `handledEventsToo: true`、
不再出现普通 `InputSurface.Pointer* +=`，且五类事件均有 `RemoveHandler`。

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --filter FullyQualifiedName~Remote_window_captures_button_handled_pointer_events
```

预期：FAIL，因为当前 Pointer 事件仍使用普通 `+=` 注册。

- [ ] **步骤 3：编写最少实现代码**

在 `RemoteSessionWindow` 中保存五个 `PointerEventHandler` 字段，在构造函数中用：

```csharp
InputSurface.AddHandler(
    UIElement.PointerPressedEvent,
    _pointerPressedHandler,
    handledEventsToo: true);
```

相同模式注册 Moved、Released、Canceled 和 WheelChanged；在 `OnClosed` 中逐一调用
对应的 `RemoveHandler`。删除五个普通 `+=` 订阅。

- [ ] **步骤 4：运行测试验证通过**

运行相同定向命令，预期 PASS。

- [ ] **步骤 5：运行回归验证**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release
dotnet test WinARD.sln -c Release
dotnet format WinARD.sln --verify-no-changes
dotnet build WinARD.sln -c Release -p:Platform=x64
git diff --check
```

预期：全部通过、构建 0 error。

- [ ] **步骤 6：发布便携包**

发布 `src/WinARD.Desktop` 的 Release x64 便携输出，压缩为带有
`pointer-routed-event-fix` 标识的新 ZIP，并核验 SHA-256 和 `WinARD.Desktop.exe` 条目。

### 任务 2：将 Pointer 命中目标迁移到远程画面层

**文件：**
- 修改：`tests/WinARD.Desktop.Tests/Views/RemoteSessionWindowInputIntegrationTests.cs`
- 修改：`src/WinARD.Desktop/Views/RemoteSessionWindow.xaml.cs`

- [ ] **步骤 1：编写失败的测试**

将回归测试改为逐项断言五类 Pointer 事件的完整注册和解绑块以 `FrameSurface` 为目标，
并断言不存在 `InputSurface` 或 `ViewportHost` 的 Pointer 注册。另断言
`FrameSurface.CapturePointer(args.Pointer)`、`FrameSurface.ReleasePointerCapture(args.Pointer)`、
`args.GetCurrentPoint(FrameSurface).Properties`，以及 XAML 中透明背景命中层。

- [ ] **步骤 2：运行测试验证失败**

```powershell
dotnet test tests/WinARD.Desktop.Tests/WinARD.Desktop.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Remote_window_routes_pointer_events_through_frame_surface
```

预期：FAIL，因为 `1.0.1.0` 实现仍在 `InputSurface` 注册、捕获和释放 Pointer。

- [ ] **步骤 3：编写最少实现代码**

为 `FrameSurface` 设置透明背景，将五次 `AddHandler` 和五次 `RemoveHandler` 的接收者
替换为 `FrameSurface`。保留 `InputSurface.Focus(FocusState.Pointer)`，把 Pointer
捕获/释放和当前点属性读取替换为 `FrameSurface`。不修改 `TryGetRemotePoint`、
`WindowsInputMapper` 或 ViewModel 发送调用。

- [ ] **步骤 4：运行测试验证通过**

运行相同定向命令，预期 PASS。

- [ ] **步骤 5：运行完整验证并发布新包**

```powershell
dotnet test WinARD.sln -c Release --no-restore
dotnet format WinARD.sln --verify-no-changes --no-restore
dotnet build WinARD.sln -c Release -p:Platform=x64 --no-restore
git diff --check
```

预期：全部通过、构建 0 warning/0 error。发布版本号递增的 self-contained win-x64
便携 ZIP，并核验 SHA-256、版本号及必需文件条目。
