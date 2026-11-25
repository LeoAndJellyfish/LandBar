# Gemini 额度

显示本机 Antigravity 登录账号的 Gemini 套餐剩余额度、重置时间和额外 AI 积分。点击岛上的圆环进入 **Gemini** 标签。

## 安装

1. 安装 Python 3.10 或更高版本，确认 `py -3 --version` 可用。
2. 安装 Antigravity CLI 1.1.11 或更高版本，用 Google 账号登录。已有 Antigravity 登录可由官方 CLI 复用。 [安装与登录](https://antigravity.google/docs/cli/getting-started/)
3. 在 LandBar 0.8.0 的 **控制中心 → 插件管理** 中安装 **Gemini 额度示例**，启用插件。使用已有插件协议的版本时，也可将本目录三个文件复制到 `%LOCALAPPDATA%\LandBar\Plugins\gemini-balance`，再刷新列表并启用。
4. 为圆环选择左侧或右侧。在 **设置 → 收缩模式** 中可单独保留 **Gemini 额度**。

插件依次检查 `LANDBAR_AGY_PATH`、`%LOCALAPPDATA%\agy\bin\agy.exe` 和 PATH 中的 `agy.exe`。自定义路径需为原生可执行文件的完整路径。修改环境变量后重启 LandBar。

## 额度与积分

- 圆环表示 Gemini 已知额度窗口中最低的剩余比例。详情仅列出 CLI 提供的窗口和重置时间，时间使用本机时区。
- 每 60 秒查询一次。也可在标签页点击 **刷新额度**，两次查询至少间隔 5 秒。更新额度时保持岛当前形态。
- 登录失效后，插件暂停自动查询并在标签页提示。需要时在终端完成 Antigravity 登录，再点击 **刷新额度**。后台查询使用 CLI 的非交互模式。
- 积分余额与套餐分别显示。返回 `0` 时显示 `0`，缺少数值时显示 **未知**。查询暂时失败时保留上次额度，并将圆环置灰、标注已过期。明确登出时清除旧快照。
- 数据范围是 Antigravity 编程套餐中的 Gemini 模型组。 [额度说明](https://antigravity.google/docs/cli/commands/usage/) · [AI 积分说明](https://antigravity.google/docs/cli/commands/credits/)

## 数据来源

插件通过 `agy --print /usage --output-format json` 和 `agy --print /credits --output-format json` 读取结构化的零轮次响应。认证由官方 CLI 管理。插件仅在内存保存显示数据，发送给 LandBar 的内容限于额度视图。 [只读命令的版本说明](https://antigravity.google/docs/changelog?tab=cli#v1111)

查询进程沿用终端代理变量。未设置这些变量时，使用 Windows 中配置的代理。该设置的作用范围是查询进程。
