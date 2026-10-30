# Windows 包身份

`AppxManifest.xml` 声明通知访问、系统媒体控制，以及外部程序目录。应用文件保留在 `dist/app`，`LandBar.identity.msix` 仅携带元数据。

构建：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build.ps1`。

本地开发安装：确认 Windows 开发者模式已启用，运行 `dist/安装并启动.cmd`，随后在应用设置中申请通知访问。脚本通过 `Add-AppxPackage -Register` 注册 `dist/identity/AppxManifest.xml`，保持开发者模式、证书信任设置原状。开发者模式关闭时，脚本显示明确错误。

签名部署：同步修改包清单与 `src/LandBar/app.manifest` 中的 Publisher，确保其等于签名证书的 Subject。运行 `scripts/Build.ps1 -SigningCertificateThumbprint <证书指纹>`，然后运行 `dist/Install.ps1 -SignedPackagePath ./dist/LandBar.identity.msix`。Windows 验证签名和信任链；脚本保持系统证书设置原状。

外部位置包最低支持 Windows 10 2004。微软提供的 `-AllowUnsigned` 测试包路径仍限制可执行激活；本项目采用开发者注册或可信签名部署。

官方依据：

- [外部位置包身份](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)
- [通知访问能力](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap3-capability-manual)
