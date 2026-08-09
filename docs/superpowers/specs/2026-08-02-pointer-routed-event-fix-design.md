# WinUI 指针路由事件修复设计

## 问题与证据

实机诊断显示键盘输入的 `UiCaptured`、`ProtocolWriteStarted` 和
`ProtocolWriteCompleted` 同步增长，鼠标输入却没有产生任何 UI 边界事件。
第一版假设是 `RemoteInputSurface : Button` 的类处理器吞掉普通 Pointer 订阅，因此将
五类事件改为 `AddHandler(..., handledEventsToo: true)`。`1.0.1.0` 的两次独立实机
会话仍然没有任何 Pointer UI 边界事件，而键盘边界持续贯通。这否定了“事件已经命中
InputSurface、只是被 Button 标记为 handled”的假设：Pointer 根本没有路由到该覆盖层。

## 方案

保留 `RemoteInputSurface : Button`，但只让它负责键盘焦点和主机光标。将
`PointerPressed`、`PointerMoved`、`PointerReleased`、`PointerCanceled` 和
`PointerWheelChanged` 的 `AddHandler(..., handledEventsToo: true)` 注册迁移到
`FrameSurface`，并为该 Grid 设置透明背景，使它成为稳定命中层。`FrameSurface` 只包含
远程画面内容，不包含同级的 `SessionErrorCard` 或 `FrameScrollViewer` 自带滚动条，避免
祖先 handled-events 监听误捕获这些交互控件；坐标映射仍复用现有 ViewportHost 坐标系。

按下时先把键盘焦点交给 `InputSurface`，再由 `FrameSurface` 捕获 Pointer；释放时也由
`FrameSurface` 释放。按钮和滚轮属性从 `FrameSurface` 的当前点读取。窗口关闭时使用
相同委托从 `FrameSurface` 对称解绑。

不修改 ARD 握手、加密、输入报文、坐标映射或指针按钮映射。

## 验证

添加回归测试，要求五类事件只在 `FrameSurface` 注册/解绑，Pointer 捕获、释放及按钮
属性读取也统一使用 `FrameSurface`，并确认它在 `1.0.1.0` 实现上失败。完成最小实现后
运行定向测试、Desktop 全部测试、解决方案全部测试、格式检查和 Release x64 构建。
最终发布新的便携 ZIP，供实机验证 Pointer 的三个诊断边界是否开始同步增长。
