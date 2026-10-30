# 专注计时示例

Windows 自带的 Windows PowerShell 5.1 即可运行。启用后显示 25 分钟倒计时、进度及三个操作：暂停／继续、重置 25 分钟、演示 10 秒。

在 LandBar 中进入“控制中心 → 插件管理”，安装计时器示例，再手动启用。也可将本目录完整复制到 `%LOCALAPPDATA%\LandBar\Plugins\focus-timer`，刷新列表后启用。

插件通过标准输入接收操作，通过标准输出发送 UTF-8 JSON Lines。卡片每秒更新，完成时另发一张保留 10 秒的提示卡片；重新开始或重置时撤回完成提示。停用插件后，计时进程和卡片一并结束。倒计时使用单调时钟，避免系统时间调整影响计时。

`plugin.ps1` 保留 UTF-8 BOM，确保 Windows PowerShell 5.1 正确解析中文源码；协议输出使用 UTF-8 无 BOM。清单中的 `-ExecutionPolicy Bypass` 仅作用于这个 PowerShell 子进程。

协议、字段限额和开发示例见 [插件开发文档](https://github.com/LeoAndJellyfish/LandBar/blob/main/docs/plugins.md)。
