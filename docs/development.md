# 开发与构建

## 环境

- Windows 10 2004（build 19041）及以上，或 Windows 11。
- [global.json](https://github.com/LeoAndJellyfish/LandBar/blob/main/global.json) 指定的 .NET 10 SDK（当前为 10.0.403）。
- Windows SDK，提供 `MakeAppx.exe`；签名构建还使用 `SignTool.exe`。
- PowerShell 7（`pwsh`），用于测试、构建和打包脚本。

发布目标为 `win-x64`，应用项目使用 `net10.0-windows10.0.19041.0`。发布包自带 .NET 运行时。Python 与 Pillow 用于重新生成图标，常规编译使用仓库中已生成的资源。

## 编译与预览

```powershell
git clone https://github.com/LeoAndJellyfish/LandBar.git
cd LandBar
dotnet build src/LandBar/LandBar.csproj -c Release
dotnet run --project src/LandBar -c Release -- --demo --preview
```

`--demo` 使用内置通知与媒体示例，停用系统来源；`--preview` 提供任务栏入口，便于桌面调试。日常模式保持托盘入口和无焦点置顶。

## 测试

### CI 检查

```powershell
./scripts/Test.ps1
```

稳定检查覆盖核心调度、媒体排序、动画数学、弹簧、配色及嵌入字体。CI 同时构建应用和发布包，验证版本、归档内容与校验记录。具体任务以 [工作流配置](https://github.com/LeoAndJellyfish/LandBar/blob/main/.github/workflows/ci.yml)为准。

### 交互桌面检查

```powershell
./scripts/Test.ps1 -IncludeDesktop
```

该选项额外运行预览、输入、自动收起、过渡与置顶检查。检查会创建隔离测试窗口，需要可交互的 Windows 桌面会话。系统媒体检查单独运行：

```powershell
dotnet run --project tests/LandBar.Media.Tests -c Release
```

媒体检查使用自建测试会话。实际应用来信、播放器切歌、授权状态与不同桌面环境应分别复核。

## 构建发布目录

```powershell
./scripts/Build.ps1
./scripts/Package.ps1
```

`Build.ps1` 先请求当前 `dist/app` 中的 LandBar 正常退出，再发布自包含程序，构建稀疏身份包并整理安装脚本。普通本地构建产物位于 `dist`。

`Package.ps1` 从独立构建目录按白名单整理发布文件，保留正在运行的 `dist` 实例。默认生成 `releases/LandBar-v<Version>-win-x64.zip` 与 `releases/SHA256SUMS.txt`。可使用 `-Version v0.3.10` 校验指定版本，或通过 `-OutputDirectory` 指定输出目录。

归档包含应用、身份文件、安装／启动／卸载脚本、使用文档、图片及第三方声明。本地通知、封面缓存、运行诊断和历史构建产物留在发布包之外。

在本机注册并运行普通构建：

```powershell
./dist/Install.ps1
```

未签名开发包的注册要求已启用 Windows 开发者模式。构建和安装脚本按现有状态执行检查。

## 签名构建

证书主题需与身份包发布者 `CN=LandBar` 一致，证书由目标电脑信任。

```powershell
./scripts/Build.ps1 -SigningCertificateThumbprint <证书指纹>
./dist/Install.ps1 -SignedPackagePath ./dist/LandBar.identity.msix
```

私钥与证书凭据留在构建环境中。公开工作流生成未签名开发包。

## 发布约定

1. 在 `src/LandBar/LandBar.csproj` 更新 `Version`，提交代码与相应说明。
2. 确认该提交的 Windows CI 通过。
3. 创建与版本一致的 `v<Version>` 标签，例如 `v0.3.10`，推送该标签。
4. [Release 工作流](https://github.com/LeoAndJellyfish/LandBar/blob/main/.github/workflows/release.yml)校验标签与项目版本，再构建并上传 ZIP 和校验记录。
5. 核对 Release 指向的提交，并下载验证归档及 SHA-256。

发布阶段以 GitHub Actions 生成的文件为准。身份注册、首次授权弹窗和实际桌面行为在交互环境中复核。

## 代码结构

| 路径 | 内容 |
| --- | --- |
| `src/LandBar.Core` | 有界提示队列、轮换、通知清理与无操作期限 |
| `src/LandBar/Services` | Windows 通知、系统媒体、设置与缓存 |
| `src/LandBar/Presentation` | 弹簧、内容过渡、圆角裁切、配色和置顶维护 |
| `src/LandBar/MainWindow.xaml` | 胶囊、活动页、控制中心与设置界面 |
| `packaging` | 应用身份清单与 Windows 图标资源 |
| `tests` | 核心测试及 Windows 集成／桌面检查 |
| `scripts` | 构建、测试、打包、安装与资源生成 |

动画采用固定透明原生宿主，内部外壳围绕顶部中心变化。内容保持固定排版，旧内容退场、新内容在裁切范围内淡入。置顶恢复按窗口事件检查，排序调整保留焦点与几何尺寸。

## 资源与平台接口

- 图标源：`src/LandBar/Assets/Brand/`；生成脚本：`scripts/Generate-BrandAssets.py`。
- 字体：应用内置 Noto Sans SC，字重为 400／600，许可与静态化说明见 [字体资源](https://github.com/LeoAndJellyfish/LandBar/blob/main/src/LandBar/Assets/Fonts/README.md)。
- 第三方分发说明：[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。
- 通知接口：[Microsoft UserNotificationListener](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/notification-listener)。
- 媒体接口：[Microsoft GSMTC](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)。
- 身份包：[Microsoft 桌面应用包身份](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)。
