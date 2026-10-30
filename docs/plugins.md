# 插件开发

LandBar 插件是本机独立程序，通过标准输入和标准输出与宿主交换 UTF-8 JSON Lines。插件提交标题、正文、图标、进度和操作按钮，LandBar 负责排版、并列、轮换及动画。PowerShell、Python、Node.js 和编译后的可执行程序均可接入。

## 使用插件

1. 右键灵动岛或点击托盘图标，进入“控制中心 → 插件管理”。
2. 安装计时器示例，或打开插件目录并复制自己的插件文件夹。
3. 刷新列表，查看插件名称、说明和状态，再手动启用。
4. 插件运行时，单击灵动岛并进入“插件”页，查看卡片及其操作。

插件目录为 `%LOCALAPPDATA%\LandBar\Plugins`。每个插件占用一个直接子目录，其中必须有 `plugin.json`；每次扫描最多读取 128 个子目录。新插件默认停用，已启用插件的选择保存在 LandBar 设置中。

修改清单或程序后，先停用再启用该插件。刷新列表会重新扫描清单，运行中的插件保持当前进程。

```text
Plugins/
└─ focus-timer/
   ├─ plugin.json
   └─ plugin.ps1
```

[专注计时示例](https://github.com/LeoAndJellyfish/LandBar/tree/main/samples/focus-timer)使用 Windows 自带的 PowerShell 5.1，提供暂停、继续、重置与 10 秒演示。倒计时常规更新保持同一张卡片，完成时另发一张保留 10 秒的提示卡片。发布包的 `app/PluginExamples/focus-timer` 目录随附示例源码，安装示例后仍需手动启用。

插件以当前 Windows 用户的权限运行，可以访问该用户允许访问的文件、网络和其他资源。请在审查来源及启动命令后启用。当前架构采用独立进程进行故障隔离；操作系统权限沿用当前用户，宿主未设置 AppContainer 或受限令牌。

## 清单

`plugin.json` 使用 UTF-8 编码，最大 16 KiB。以下为计时器清单：

```json
{
  "id": "focus-timer",
  "name": "专注计时",
  "version": "1.0.0",
  "description": "25 分钟专注计时，支持暂停、继续、重置和 10 秒演示。",
  "command": "powershell.exe",
  "arguments": [
    "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "plugin.ps1"
  ],
  "protocolVersion": 1
}
```

| 字段 | 类型 | 要求 |
| --- | --- | --- |
| `id` | string | 全局唯一，1–64 位；匹配 `[a-z0-9][a-z0-9._-]{0,63}` |
| `name` | string | 插件显示名称，1–64 个 UTF-16 代码单元 |
| `version` | string | 插件自报版本，1–64 个 UTF-16 代码单元 |
| `description` | string | 说明，最多 240 个 UTF-16 代码单元，允许空字符串 |
| `command` | string | 可执行程序名称或路径，1–1024 个 UTF-16 代码单元 |
| `arguments` | string[] | 最多 32 项，每项最多 1024 个 UTF-16 代码单元 |
| `protocolVersion` | integer | 当前固定为 `1` |

上述字段均为必填。清单和协议字段区分大小写，字符串禁止包含 NUL。宿主拒绝重复的顶层字段、重复插件 ID、符号链接及目录联接形式的插件目录和清单。

`command` 可指定 PATH 中的程序，例如 `powershell.exe`、`python.exe`、`node.exe`；也可指定绝对可执行文件路径，或插件目录内的相对可执行文件路径，例如 `bin/my-plugin.exe`。相对可执行文件路径限定在当前插件目录内。每个参数独立传递，工作目录为插件目录，宿主通过 `ProcessStartInfo` 启动程序。

PowerShell 示例中的 `-ExecutionPolicy Bypass` 作用于该子进程。开发者可按自己的运行环境选用参数。发布包提供 .NET 运行时供 LandBar 使用；Python、Node.js 等插件运行时由用户另行安装。

## 通信与握手

标准输入和标准输出使用 **UTF-8 无 BOM**，每条消息占一行，以 `\n` 结束。标准输出专用于协议，调试信息写入标准错误。每行必须是 JSON 对象，正文最大 **16 KiB（UTF-8 字节）**，最大嵌套深度为 12，每秒最多 60 条消息。

启动后，宿主首先发送：

```json
{"type":"hello","protocolVersion":1}
```

插件须在 5 秒内回复相同的协议版本，之后再提交卡片：

```json
{"type":"hello","protocolVersion":1}
```

宿主同时设置环境变量 `LANDBAR_PROTOCOL_VERSION=1`。插件运行期间应持续读取标准输入，及时处理操作和退出请求。

## 创建和更新卡片

插件发送 `upsert`：

```json
{
  "type": "upsert",
  "id": "focus",
  "title": "专注计时",
  "body": "24:30 · 进行中",
  "icon": "timer",
  "progress": 0.02,
  "actions": [
    {"id":"toggle","label":"暂停"},
    {"id":"reset","label":"重置 25 分钟"},
    {"id":"demo","label":"演示 10 秒"}
  ]
}
```

| 字段 | 类型 | 要求 |
| --- | --- | --- |
| `type` | string | 固定为 `upsert` |
| `id` | string | 插件内唯一，1–64 位；匹配 `[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}` |
| `title` | string | 必填，1–120 个 UTF-16 代码单元 |
| `body` | string | 必填，最多 600 个 UTF-16 代码单元，允许空字符串 |
| `icon` | string | 可选，默认 `activity` |
| `progress` | number / null | 可选，`0`–`1`；省略或 `null` 时隐藏进度条 |
| `ttlSeconds` | number / null | 可选，`1`–`86400` 秒；省略或 `null` 时持续保留 |
| `actions` | array | 可选，最多 3 个操作；每项含 `id` 和 `label` |

图标名称支持 `activity`、`timer`、`check`、`download`、`code`、`calendar`、`info` 和 `music`。操作 ID 沿用卡片 ID 的格式，同一卡片内各操作 ID 须唯一；标签长度为 1–24 个 UTF-16 代码单元。

相同 `id` 的 `upsert` 更新已有卡片，完整替换本次提供的显示字段。宿主按插件 ID 划分卡片空间，两个插件可使用相同的本地卡片 ID。每个插件最多保留 8 张卡片，宿主总计最多 32 张，同时运行最多 8 个插件。

`ttlSeconds` 从每次 `upsert` 到达宿主时重新计时。即使显示内容相同，也会续期；相同内容的续期无需重新绑定界面。无 TTL 的卡片持续保留，直到插件撤回、停止或退出。倒计时和设备状态适合更新同一张卡片；临时构建结果可设置 TTL。

插件主动撤回卡片：

```json
{"type":"remove","id":"focus"}
```

## 处理操作

用户点击操作按钮后，宿主向对应插件发送：

```json
{"type":"action","cardId":"focus","actionId":"toggle"}
```

插件执行该操作，再发送 `upsert` 更新内容和按钮。例如暂停计时后将按钮文字更新为“继续”。宿主仅发送当前卡片中已登记的操作。协议 1 没有同步返回值；执行结果通过后续卡片更新呈现。

## 退出与错误

停用插件、移除插件目录后刷新列表或退出 LandBar 时，宿主发送：

```json
{"type":"shutdown"}
```

插件收到后应释放自己的资源并退出。标准输入结束时也应退出。宿主等待 500 毫秒，随后终止仍在运行的插件进程树。Windows Job Object 管理已附加插件进程及其后续子进程的生命周期，插件主进程退出或宿主关闭会触发回收。插件正常退出、异常退出、协议错误和停用均会撤回该次运行拥有的全部卡片；其他插件和系统媒体继续运行。

握手超时、未知消息类型、无效字段、超长消息和超过速率或卡片数量限制会终止该插件会话。管理页提供错误状态。标准错误由宿主持续排空，当前版本不在岛上显示其内容。

插件重启后拥有独立的运行会话，旧会话结束后的消息失效。宿主管理卡片、操作和进程的生命周期。插件对外写入的文件、已发送的网络请求等外部操作由插件自身负责。

## 最小 Python 示例

本例需要已安装 Python 3.7 及以上版本。创建 `Plugins/python-clock`，保存以下清单：

```json
{
  "id":"python-clock",
  "name":"时间示例",
  "version":"1.0.0",
  "description":"点击刷新操作后显示当前时间。",
  "command":"python.exe",
  "arguments":["-u","plugin.py"],
  "protocolVersion":1
}
```

在同目录创建 UTF-8 编码的 `plugin.py`：

```python
import datetime
import json
import sys

sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", newline="\n", line_buffering=True)

def send(message):
    print(json.dumps(message, ensure_ascii=False), flush=True)

def publish():
    send({
        "type": "upsert",
        "id": "clock",
        "title": "当前时间",
        "body": datetime.datetime.now().strftime("%H:%M:%S"),
        "icon": "calendar",
        "actions": [{"id": "refresh", "label": "刷新"}],
    })

for line in sys.stdin:
    message = json.loads(line)
    if message["type"] == "hello":
        if message["protocolVersion"] != 1:
            break
        send({"type": "hello", "protocolVersion": 1})
        publish()
    elif message["type"] == "shutdown":
        break
    elif (message["type"] == "action"
          and message["cardId"] == "clock"
          and message["actionId"] == "refresh"):
        publish()
```

在管理页刷新并启用后，进入活动中的“插件”页即可查看时间。开发时优先验证握手、同 ID 更新、操作回调、停用撤回和异常退出，再接入实际应用或设备。

## 架构依据

Shi、Zhang 与 Cui 的 [A Programming Paradigm for Spatiotemporal Composability](https://arxiv.org/abs/2608.25512)讨论组件移除时的效果回收，以及依赖变化时的组件激活。其 Cordis 实现由上下文追踪效果，协调组件装载与卸载。[论文 §5.1，59–63 页](https://arxiv.org/pdf/2608.25512#page=59)

LandBar 本版采用其中的生命周期原则：插件对宿主的显示贡献归属于当前运行会话，停止时统一撤回；跨进程消息异步处理；更新已有卡片保留独立身份。宿主资源回收、外部操作与执行权限分别管理，这与论文对系统边界及沙箱的讨论相对应。[论文 §6.1–6.3，70–73 页](https://arxiv.org/pdf/2608.25512#page=70)

协议 1 提供独立内容源与卡片操作。插件间服务依赖、依赖图、任意界面注入和自动代码热替换尚未实现；本版也未引入 Cordis 运行时或实现论文的完整形式系统。
