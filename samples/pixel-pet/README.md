# 像素伙伴示例

Windows 自带的 PowerShell 5.1 即可运行。插件提供两个视图：可选左右侧的收起态像素动画，以及名为“像素伙伴”的独立标签页。标签页包含同一动画、配色序号圆环和“切换配色”按钮。

在“控制中心 → 插件管理”安装示例并手动启用，也可将本目录完整复制到 `%LOCALAPPDATA%\LandBar\Plugins\pixel-pet` 后刷新、启用。“像素伙伴”标签自动出现；收起态动画需要在插件管理中选择“左侧”或“右侧”。点击“还原”可取消侧边显示，独立标签继续保留。

动画使用 `idle.widget`，声明默认左侧并允许左右两侧：

```json
"placement": {"defaultSide":"left","allowedSides":["left","right"]}
```

位置由用户选择并由 LandBar 保存；切换配色后位置保持不变。开发侧边组件时必须提供该字段；需要固定位置的组件可将 `allowedSides` 限定为一侧。

像素图为原创 16 × 16 网格，包含 4 帧、4 种颜色，以每秒 4 帧播放。LandBar 负责帧切换，视图隐藏后停止动画计时。插件进程阻塞读取标准输入，仅在启用或切换配色时提交视图。圆环表示当前配色在三种配色中的序号。

`plugin.ps1` 保留 UTF-8 BOM，协议使用 UTF-8 无 BOM。像素帧和配色均内置于脚本。停用后，两个视图撤回；所选侧边组件移除，独立标签消失。

字段、插槽与操作协议见 [插件开发文档](https://github.com/LeoAndJellyfish/LandBar/blob/main/docs/plugins.md)。
