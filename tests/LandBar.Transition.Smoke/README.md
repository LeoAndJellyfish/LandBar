# 展开与收起的逐帧回归

```powershell
dotnet run --project tests/LandBar.Transition.Smoke -c Release -- artifacts/transition-final
dotnet run --project tests/LandBar.Transition.Smoke -c Release -- artifacts/transition-constrained --constrained-settings
```

测试引用生产 `LandBar.MainWindow`，从生产 `App.xaml` 读取资源，使用隔离的示例媒体与通知。测试宿主跳过真实应用启动流程、单实例互斥和系统来源。测试不控制用户播放器、不请求通知权限、不修改系统或应用显示设置。

几何探针在测试实例内选择“永不”自动收起，避免反射切换页面和逐帧截图期间触发用户设置的无操作期限。该设置保存在测试实例内存，用户配置文件保持原样。实际五秒自动收起由 `LandBar.Interaction.Smoke` 独立验证，其测试实例明确使用五秒期限。

覆盖首轮展开、收起、单媒体、双媒体、通知、控制中心、设置、分页尺寸变化和快速反向切换，共 11 个阶段。

`--constrained-settings` 独立检查 448×250 的设置视口，要求真实滚动条出现后内容仍完成入场。该模式停止窗口的来源刷新计时器，在测试宿主内设置受限布局，保留系统工作区配置。

检查内容：

- 每个合成时间采样一次生产窗口的 WPF 输出。四角远离抗锯齿边缘的圆角外像素必须透明。
- 可见的实绘按钮、图标、文字与封面边界应完整位于当前窗口。透明点击热区以实际子元素绘制范围检查。
- 窗口形变期间，旧内容快照必须完成退场。
- 每阶段结束时，内容应恢复为不透明、可交互状态，内容过渡应结束，避免隐藏全部内容获得误通过。
- 动态岛的顶部保持为宿主坐标 0，水平中心保持宿主中心；每个采样帧分别记录可见岛尺寸与坐标。
- 初始化完成后，记录原生 `WM_WINDOWPOSCHANGED` 并核对 `GetWindowRect`，宿主边界应保持不变。
- 宿主内位于岛外的空白区域抽样检查 alpha=0。测试另创建自身的灰色下层窗口，通过 `WindowFromPoint` 核对空白处命中下层窗口、岛内命中 LandBar；不发送鼠标或键盘输入。

`report.json` 保留每帧尺寸、透明度、泄露与裁切指标，以及所有阶段的终态。`frame-*.png` 和 `contact-sheet.png` 用于检查中间过程。

生产结构使用固定原生宿主，`Island` 背景与 `IslandViewport` 内容为同级元素。因此图像从 `HoverSurface` 全宿主视觉树捕获，再按可见岛的当前边界裁出；控件范围也先映射至宿主坐标后减去岛原点。测试同时覆盖背景与内容，避免仅截取背景获得误通过。

这些图像由 `RenderTargetBitmap` 捕获生产视觉树，用于验证 WPF 裁切与内容时序。逐帧离屏截图会增加渲染负载，报告帧数用于几何覆盖，实际呈现帧率须使用独立的无截图性能探针和原生桌面录像测量。不要与原生性能采样并行运行本检查。运行需要交互式 Windows 桌面；灰色下层测试窗口会临时出现在岛的宿主区域。

`--diagnostic` 会保留缺陷结果并返回 0，便于采集旧实现；报告中的 `passed` 仍表示真实验收状态，正式验收应使用默认严格模式。

独立的受限高度检查：

```powershell
dotnet run --project tests/LandBar.Transition.Smoke -c Release -- artifacts/transition-constrained-settings --constrained-settings
```

该模式停止示例窗口刷新计时器，通过生产内容过渡控制器缩至 448×250，确认设置页出现可滚动内容后仍恢复可见、交互与结束状态。ScrollViewer 内部内容按其视口正常裁切，因此该受限阶段不执行未考虑滚动语义的原始子元素超界断言。圆角透明检查、新旧快照时序和终态断言仍启用。结果保存在独立目录，默认 11 阶段严格矩阵保持原样。
