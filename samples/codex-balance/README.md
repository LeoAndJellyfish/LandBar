# Codex 额度

显示本机 Codex 登录账号的套餐剩余额度、窗口重置时间和额外积分。圆环位于收起态左侧或右侧，点击后进入独立的“Codex”标签。插件更新额度时保持灵动岛当前形态。

## 安装

1. 使用 LandBar 0.6.0 及以上版本，在“控制中心 → 插件管理”点击“Codex 额度示例”。
2. 安装 **Python 3.10 及以上版本**，确认终端中的 `py -3 --version` 可运行。安装器需包含 Windows Python Launcher。
3. 安装或更新 [Codex CLI](https://developers.openai.com/codex/cli)，在 CLI 中完成 **ChatGPT 账号登录**。
4. 在 LandBar 插件管理中启用“Codex 额度”，为额度圆环选择“左侧”或“右侧”。

插件默认停用。若本机只有 `python.exe`，可将插件目录中 `plugin.json` 的 `command` 改为实际 Python 可执行文件，`arguments` 改为 `["-u","plugin.py"]`。修改后停用、重新启用插件。

## Codex 路径

程序优先读取可选环境变量 `LANDBAR_CODEX_PATH`，其值须为本机 `codex.exe` 的完整路径；随后检查 `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`、PATH 中的原生程序，以及 npm 安装目录内的 Windows 平台程序。插件直接启动原生程序，路径搜索失败时会在标签页提示。

设置环境变量或安装 CLI 后，退出并重新启动 LandBar，再启用插件。不同 Windows 用户或独立 `CODEX_HOME` 环境中的登录状态分别使用。

## 显示规则

- 圆环显示已知套餐窗口中最低的剩余百分比；标签页列出各窗口与重置时间。时间使用本机时区。
- 每 60 秒查询一次，同时接收 `account/rateLimits/updated` 推送。手动刷新最短间隔为 5 秒。
- 积分为 `0` 时显示 `0`；服务返回不限额时显示“不限额”，缺少字段时显示“未知”。超长数值使用带“≈”的科学记数法显示。套餐剩余比例与额外积分分别显示。
- 读取失败时保留上次数据并标注过期，圆环转灰。成功数据超过 150 秒仍未更新时也标注过期。账号变化时清空上个账号的显示并重新读取。
- API Key 登录及尚未提供套餐额度的账号会显示相应提示。此示例使用 Codex 套餐额度接口；API 账单余额请通过对应账单页面查看。

## 数据与进程

插件通过本机 [`codex app-server --stdio`](https://developers.openai.com/codex/app-server) 查询 `account/read`（`refreshToken:false`）和 `account/rateLimits/read`，接收额度与账号变化通知。程序不读取或复制凭据文件，不保存账号快照，不输出邮箱、账号标识或原始 RPC 错误；发送给 LandBar 的内容为额度视图。

插件不创建聊天、启动模型任务、兑换积分或发起登录操作。Codex app-server 自行管理其账号连接和内部认证行为。停用插件后关闭自有子进程，LandBar 的 Windows Job 同时管理附加进程。

手动安装时，将此目录中的三个文件复制到个人插件目录。位置声明、节点和交互协议见 [LandBar 插件开发文档](https://github.com/LeoAndJellyfish/LandBar/blob/main/docs/plugins.md)。
