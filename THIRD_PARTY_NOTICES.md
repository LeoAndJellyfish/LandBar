# 第三方组件

## Noto Sans SC

LandBar 随应用嵌入 Noto Sans SC Regular（400）与 SemiBold（600）字体，许可为 SIL Open Font License 1.1。

字体版权声明：

> Copyright 2014-2021 Adobe (http://www.adobe.com/), with Reserved Font Name 'Source'

本项目使用由可变字体生成的静态字重实例，调整样式名称并移除残留的 `STAT` 元数据。字形集保留完整，字体家族名称为 Noto Sans SC。

- [完整 OFL 许可](https://github.com/LeoAndJellyfish/LandBar/blob/main/src/LandBar/Assets/Fonts/OFL.txt)
- [字体来源、修改与生成步骤](https://github.com/LeoAndJellyfish/LandBar/blob/main/src/LandBar/Assets/Fonts/README.md)
- 上游：[Google Fonts](https://github.com/google/fonts/tree/main/ofl/notosanssc)、[Noto CJK](https://github.com/notofonts/noto-cjk)

发布包在 `app/Assets/Fonts/` 保留字体许可和说明。

## .NET 与 Windows Desktop Runtime

Windows x64 自包含发布包携带 Microsoft .NET 与 Windows Desktop Runtime。运行时保留其上游许可与第三方通知，发布打包流程从实际恢复的运行时包收集相应文件。

- [.NET Runtime](https://github.com/dotnet/runtime)
- [WPF](https://github.com/dotnet/wpf)
- [Windows Forms](https://github.com/dotnet/winforms)

## Windows SDK for .NET

应用通过 `Microsoft.Windows.SDK.NET.Ref` 提供的投影程序集调用 Windows 通知与媒体 API。该 NuGet 包声明的许可地址为 [Windows SDK 许可](https://aka.ms/WinSDKLicenseURL)。

- [完整许可文件](https://github.com/LeoAndJellyfish/LandBar/blob/main/third_party/windows-sdk/sdk_license.rtf)
- [文件来源](https://github.com/LeoAndJellyfish/LandBar/blob/main/third_party/windows-sdk/README.md)

发布包在 `third_party/windows-sdk/` 附同一许可文件；恢复所得的实际包版本与元数据随发布清单保留。

各组件的权利与使用条件依据随附许可文件执行。本文件列明第三方组件及其来源，LandBar 项目自身的许可需单独声明。
