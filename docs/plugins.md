# 插件开发

LandBar 插件是本机独立程序，通过标准输入和标准输出交换 UTF-8 JSON Lines。插件可以提交提示卡片、独立标签页、页面替换和收起态组件；LandBar 负责布局、裁切、动画与操作分发。PowerShell、Python、Node.js 和编译后的可执行程序均可接入。

## 使用插件

1. 右键灵动岛或点击托盘图标，进入“控制中心 → 插件管理”。
2. 安装随附示例，或打开插件目录并复制自己的插件文件夹。
3. 刷新列表，查看插件名称、说明和状态，再手动启用。
4. 插件发布的独立标签自动出现在活动页，标题由插件指定。旧卡片插件以自己的显示名称形成单独标签。
5. 收起态组件在管理页选择“左侧”或“右侧”；固定位置的组件显示其允许位置。页面替换点击“使用”，点击“还原”取消该位置的选择。

插件目录为 `%LOCALAPPDATA%\LandBar\Plugins`。每个插件占用一个直接子目录，其中必须有 `plugin.json`；每次扫描最多读取 128 个子目录。新插件默认停用，已启用插件的选择保存在 LandBar 设置中。

修改清单或程序后，先停用再启用该插件。刷新列表会重新扫描清单，运行中的插件保持当前进程。

```text
Plugins/
├─ focus-timer/
│  ├─ plugin.json
│  └─ plugin.ps1
└─ pixel-pet/
   ├─ plugin.json
   └─ plugin.ps1
```

三个公开示例的源码随发布包位于 `app/PluginExamples`。计时器和像素伙伴使用 Windows 自带的 PowerShell 5.1；Codex 额度示例需要 Python 3.10+ 和已通过 ChatGPT 登录的 Codex CLI：

| 示例 | 功能 |
| --- | --- |
| [专注计时](https://github.com/LeoAndJellyfish/LandBar/tree/main/samples/focus-timer) | 25 分钟倒计时、暂停／继续、重置与 10 秒演示，使用卡片协议 |
| [像素伙伴](https://github.com/LeoAndJellyfish/LandBar/tree/main/samples/pixel-pet) | 可选左右侧的收起态像素动画、独立标签和配色切换，使用声明式视图协议 |
| [Codex 额度](https://github.com/LeoAndJellyfish/LandBar/tree/main/samples/codex-balance) | 套餐剩余额度、重置时间、额外积分、独立标签及左右侧圆环，使用本机 Codex app-server |

安装示例后仍需手动启用。像素伙伴的独立标签自动出现，收起态动画需单独选择“左侧”或“右侧”。

Codex 示例的依赖、登录检查和字段说明见 [示例 README](https://github.com/LeoAndJellyfish/LandBar/blob/main/samples/codex-balance/README.md)。额度来自安装者自己的 Codex CLI；发布文件包含通用程序与清单。额度快照保存在插件进程内存中，退出后清除。

插件停止或撤回视图后，对应页面恢复内置显示，独立标签消失。插件管理的“停用插件界面”可统一隐藏声明式视图；插件程序和原有卡片继续运行。托盘菜单始终提供“恢复内置界面”和内置插件管理入口，便于撤销控制中心、设置或插件管理页面的替换。

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

标准输入和标准输出使用 **UTF-8 无 BOM**，每条消息占一行，以 `\n` 结束。标准输出专用于协议，调试信息写入标准错误。每行必须是 JSON 对象，正文最大 **16 KiB（UTF-8 字节）**，JSON 嵌套深度最多 24，插件每秒最多发送 60 条消息。

启动后，宿主首先发送：

```json
{"type":"hello","protocolVersion":1,"features":["cards","views","contexts","tabs","sprites","widgetPlacement"]}
```

插件须在 5 秒内回复相同的协议版本，之后再提交卡片或视图：

```json
{"type":"hello","protocolVersion":1}
```

宿主同时设置环境变量 `LANDBAR_PROTOCOL_VERSION=1`。插件运行期间应持续读取标准输入，及时处理操作和退出请求。宿主 `features` 列出可用扩展；使用声明式界面的插件可先检查 `views` 和 `tabs`，侧边位置声明对应 `widgetPlacement`。较早宿主省略该字段时，插件可回退至卡片接口。

## 声明式视图与独立标签

使用 `view` 创建或完整更新一棵视图树：

```json
{
  "type": "view",
  "id": "dashboard",
  "slot": "activity.tab",
  "title": "构建状态",
  "root": {
    "type": "column",
    "width": 320,
    "spacing": 12,
    "padding": 16,
    "children": [
      {"type":"text","text":"构建完成","fontSize":20,"weight":"semibold"},
      {"type":"ring","value":1,"text":"完成","width":64,"height":64,"color":"#BEDDC9"},
      {"type":"button","text":"重新检查","actionId":"refresh"}
    ]
  }
}
```

`id` 在插件内唯一，匹配 `[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}`。视图与卡片分别保存，同名 ID 可同时用于一张卡片和一个视图。`root` 为根节点，`slot` 选择呈现位置。

| `slot` | 位置与启用方式 |
| --- | --- |
| `activity.tab` | 独立活动标签；每个视图各有一个标签，启用插件界面后自动出现 |
| `idle.widget` | 收起态侧边组件；必须声明 `placement`，用户在管理页选择允许的侧边 |
| `idle.left`、`idle.right` | 旧侧边名称；仍必须声明 `placement`，且默认侧与插槽名称一致 |
| `idle.content` | 收起态主体，替换默认主体内容；在管理页选择“使用” |
| `media.page` | 媒体页面替换；在管理页选择“使用” |
| `notifications.page` | 通知页面替换；在管理页选择“使用” |
| `plugins.page` | 旧协议卡片标签的页面替换；在管理页选择“使用” |
| `controlCenter.page` | 控制中心替换；在管理页选择“使用” |
| `settings.page` | 设置页面替换；在管理页选择“使用” |
| `pluginManagement.page` | 插件管理页面替换；在管理页选择“使用” |

`activity.tab` 必须提供 `title`；其他位置可选。标题为 1–24 个 UTF-16 代码单元，支持中文和 emoji，禁止控制字符及换行，首尾空白会移除。一个插件可发布多个不同 ID、不同标题的标签。标签分别显示，更多标签可横向滚动。

收起态插槽也适用于媒体播放时的紧凑形态。左右组件位于默认主体两侧；`idle.content` 可替换主体。用户点击收起态组件，可进入同插件的活动标签。

### 侧边位置与宿主布局

侧边组件必须在每次 `view` 消息中提供 `placement`。开发者需确定默认侧及支持的侧边，宿主据此提供位置选择：

```json
{
  "type": "view",
  "id": "status",
  "slot": "idle.widget",
  "placement": {
    "defaultSide": "left",
    "allowedSides": ["left", "right"]
  },
  "root": {
    "type": "ring", "width": 28, "height": 28,
    "value": 0.75, "text": "75", "fontSize": 12
  }
}
```

| 字段 | 要求 |
| --- | --- |
| `placement.defaultSide` | 必填，`left` 或 `right`，必须包含在 `allowedSides` 中 |
| `placement.allowedSides` | 必填，包含 1–2 项 `left`、`right`，禁止重复 |

支持左右切换时填写两侧；固定左侧使用 `{"defaultSide":"left","allowedSides":["left"]}`，固定右侧使用 `{"defaultSide":"right","allowedSides":["right"]}`。固定组件的另一侧不提供选择。`placement` 对象拒绝重复或未知字段，页面和 `idle.content` 禁止提供该字段。

旧侧边视图应迁移至 `idle.widget` 并补齐 `placement`。继续使用 `idle.left` 或 `idle.right` 时，`defaultSide` 须分别为 `left`、`right`；缺少声明的侧边视图会触发协议错误。卡片、独立标签和其他页面的消息格式保持原有约定。

每侧同时选择一个组件。同一组件更换侧边时，宿主撤销旧位置的绑定，避免左右重复显示；目标位置已有组件时，宿主在双方位置约束允许的情况下交换位置，否则替换目标侧组件。点击已选中的侧别可移除该组件。选择保存在本机设置中，视图更新不会重置用户选择，插件停用时对应组件撤回。`defaultSide` 用于恢复重复绑定等异常状态；首次显示仍需用户在管理页选择位置。

宿主分别按左右组件的实际尺寸分配空间，关闭或撤回的组件立即释放对应侧的宽度和间距。时钟、媒体或自定义主体在剩余区域内布局，整个岛继续围绕屏幕顶部中心伸缩。组件同时存在于时钟和媒体紧凑形态中；可用宽度不足时，宿主先简化内置媒体内容，再按空间隐藏侧边组件，避免组件重叠或局部裁断。

### 旧版侧边插件迁移

v0.6.0 要求侧边视图显式声明位置。旧消息缺少 `placement` 时，宿主报告协议错误并撤回该插件的视图。

自行开发的组件应在每次 `view` 消息中增加上述声明，建议使用 `idle.widget`。保持插件 ID 和视图 ID，原有有效侧边绑定即可继续使用。

已安装的示例源码位于用户插件目录，LandBar 更新保留该目录。更新像素伙伴等示例时：

1. 在插件管理中停用示例；自定义过源码时先保留副本。
2. 从新版 `app/PluginExamples/<示例名>` 复制相应源码与清单至 `%LOCALAPPDATA%\LandBar\Plugins\<示例名>`。
3. 刷新插件列表并重新启用，在管理页选择允许的左侧或右侧。

相同 ID 的 `view` 完整替换原视图；节点、标题和位置声明相同时，宿主保留已有呈现。视图更新本身不触发自动弹出。每插件最多保留 12 个视图，宿主总计最多 48 个。视图无 TTL，持续存在至主动撤回或该次运行结束：

```json
{"type":"removeView","id":"dashboard"}
```

### 节点

宿主支持七种节点，并按区域可用空间约束布局和裁切：

| `type` | 内容 |
| --- | --- |
| `column` | 垂直排列 `children` |
| `row` | 水平排列 `children` |
| `text` | 文本 |
| `button` | 操作按钮，必须提供有效 `actionId`；`text` 指定按钮文字 |
| `ring` | 圆环，必须提供 `0`–`1` 的 `value`；`text` 可显示环心文字 |
| `sprite` | 内嵌像素帧，字段见下节 |
| `spacer` | 按 `width`、`height` 留出间距 |

每个视图最多包含 **128 个节点、8 层节点深度**，根节点计为第 1 层。仅 `row` 和 `column` 支持 `children` 数组。节点拒绝重复或未定义字段。

| 字段 | 类型与范围 | 默认值 |
| --- | --- | --- |
| `id` | 可选，沿用视图 ID 格式，同一视图内唯一 | 无 |
| `text` | 最多 512 个 UTF-16 代码单元 | 空字符串 |
| `color`、`background`、`trackColor` | `#RRGGBB` 或 `#AARRGGBB` | 使用宿主样式 |
| `width`、`height` | `0`–`720`、`0`–`420` DIP | 自动布局 |
| `fontSize` | `12`–`32` DIP | `15` |
| `weight` | `normal`、`semibold` | `normal` |
| `align` | `start`、`center`、`end`、`stretch` | `start` |
| `spacing`、`padding` | `0`–`32` DIP | `0` |
| `cornerRadius` | `0`–`64` DIP | `0` |
| `value` | 有限数值 `0`–`1` | 无 |
| `thickness` | `1`–`16` DIP | `3` |
| `actionId` | 仅 `button` 使用，沿用视图 ID 格式 | 无 |
| `children` | 仅 `row`、`column` 使用 | 空数组 |

收起态根节点另有显示限额：`idle.widget`、`idle.left`、`idle.right` 最大 **64 × 36 DIP**，`idle.content` 最大 **400 × 36 DIP**。省略尺寸时由宿主安排可用空间，显式尺寸超过限额会被拒绝。建议圆环与像素动画明确指定尺寸，并分别检查左右侧、单组件、双组件及媒体播放时的显示。

### 像素动画

`sprite` 直接携带调色板与帧数据，插件无需持续发送动画消息：

```json
{
  "type": "view",
  "id": "pixels",
  "slot": "idle.widget",
  "placement": {"defaultSide":"left","allowedSides":["left","right"]},
  "root": {
    "type": "sprite",
    "width": 28,
    "height": 28,
    "pixelWidth": 2,
    "pixelHeight": 2,
    "palette": ["#00000000", "#BEDDC9"],
    "frames": ["0110", "1001"],
    "fps": 4
  }
}
```

| 字段 | 要求 |
| --- | --- |
| `pixelWidth`、`pixelHeight` | 必填整数，分别为 `1`–`32` |
| `palette` | 必填，1–16 项颜色，使用 `#RRGGBB` 或 `#AARRGGBB` |
| `frames` | 必填，1–16 帧；每帧为 `pixelWidth × pixelHeight` 个十六进制索引字符 |
| `fps` | `1`–`12`，默认 `6` |

每帧按从左到右、从上到下排列像素，字符 `0`–`F` 对应调色板位置，索引须落在调色板范围内。透明颜色可使用 `#00000000`。`width`、`height` 指定显示尺寸，与像素网格尺寸分别设置。

LandBar 在视图可见时播放帧，隐藏或卸载后停止动画计时。16 × 16、2–4 帧通常适合小组件；所有帧、节点和字段仍共享单条消息的 16 KiB 上限。

### 视图操作

点击声明式按钮时，宿主向所属插件发送：

```json
{"type":"viewAction","viewId":"dashboard","actionId":"refresh"}
```

宿主校验该操作仍存在于当前视图树中。插件执行后可重新发送同 ID 的 `view`，更新文字、圆环、调色板或按钮。结果通过后续视图更新呈现。

### 页面上下文

用户选中的页面显示时，宿主可向该提供方发送上下文：

```json
{
  "type": "context",
  "slot": "media.page",
  "data": {
    "sessions": [
      {"SessionId":"example","AppName":"播放器","Title":"歌曲","Artist":"演唱者","IsPlaying":true,
       "CanTogglePlayPause":true,"CanGoNext":true,"CanGoPrevious":true}
    ]
  }
}
```

上下文按当前选定的提供方发送，内容变化后再更新。插件须已发布对应插槽的视图；宿主不向所有插件广播通知或媒体信息，插件没有任意请求宿主数据的协议入口。

| 页面 | `data` 内容 |
| --- | --- |
| `media.page` | `sessions`，最多 8 个媒体会话，字段如上例 |
| `notifications.page` | `notifications`，最多 8 条，字段为 `Id`、`AppName`、`Title`、`Body`、`CreatedAt`；正文截至 240 个 UTF-16 代码单元 |
| 其他显示中的页面、独立标签 | `quiet`、`hideContent`、`autoCollapseSeconds` |

开启“隐藏消息内容”后，通知上下文的 `Title`、`Body` 使用遮蔽文本。上下文整行受 16 KiB 限制，超长或无法序列化的数据会被拒绝。媒体上下文提供只读会话快照，媒体操作由插件自身实现。

## 兼容卡片协议

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

`ttlSeconds` 从每次 `upsert` 到达宿主时重新计时。即使显示内容相同，也会续期；相同内容的续期无需重新绑定界面。无 TTL 的卡片保留在提供方内容中，直到插件撤回、停止或退出；自动提示仍遵守显示预算和无操作收起设置。倒计时和设备状态适合更新同一张卡片；临时构建结果可设置 TTL。

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

插件收到后应释放自己的资源并退出。标准输入结束时也应退出。宿主等待 500 毫秒，随后终止仍在运行的插件进程树。Windows Job Object 管理已附加插件进程及其后续子进程的生命周期，插件主进程退出或宿主关闭会触发回收。正常退出、异常退出、协议错误和停用均撤回该次运行拥有的全部卡片与视图；独立标签消失，选定页面恢复内置显示。其他插件和系统媒体继续运行。

握手超时、未知消息类型、无效字段、超长消息和超过速率、卡片或视图数量限制会终止该插件会话。管理页提供错误状态。标准错误由宿主持续排空，当前版本不在岛上显示其内容。

插件重启后拥有独立的运行会话，旧会话结束后的消息失效。宿主管理卡片、视图、操作和进程的生命周期。插件对外写入的文件、已发送的网络请求等外部操作由插件自身负责。

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

在管理页刷新并启用后，进入活动中的“时间示例”标签即可查看卡片。独立声明式标签可采用上文的 `activity.tab` 和 `viewAction` 接口。开发时优先验证握手、同 ID 更新、操作回调、停用撤回和异常退出，再接入实际应用或设备。

## 架构依据

Shi、Zhang 与 Cui 的 [A Programming Paradigm for Spatiotemporal Composability](https://arxiv.org/abs/2608.25512)讨论组件移除时的效果回收，以及依赖变化时的组件激活。其 Cordis 实现由上下文追踪效果，协调组件装载与卸载。[论文 §5.1，59–63 页](https://arxiv.org/pdf/2608.25512#page=59)

LandBar 本版采用其中的生命周期原则：插件对宿主的显示贡献归属于当前运行会话，停止时统一撤回；跨进程消息异步处理；更新已有卡片保留独立身份。宿主资源回收、外部操作与执行权限分别管理，这与论文对系统边界及沙箱的讨论相对应。[论文 §6.1–6.3，70–73 页](https://arxiv.org/pdf/2608.25512#page=70)

协议 1 保留卡片接口，并增加受约束的声明式视图、独立标签和选定页面上下文。插件间服务依赖、依赖图、任意界面注入和自动代码热替换尚未实现；项目未引入 Cordis 运行时或实现论文的完整形式系统。
