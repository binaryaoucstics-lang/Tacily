# 参与贡献

感谢你愿意改进屏幕翻译。欢迎提交错误报告、功能建议和代码贡献。

## 提交问题

- 先搜索现有 Issue，避免重复提交。
- 错误报告请附上 Windows 版本、复现步骤、预期结果和实际结果。
- 涉及界面或译文错位时，建议附上截图，并说明显示缩放比例、浏览器和页面类型。
- 请在提交前去除 API Key、私人浏览内容、用户配置与截图中的个人信息。

## 本地开发

项目需要 Windows 10/11、.NET 8 SDK 和 Node.js 22+。资源下载脚本验证固定版本及 SHA-256：

```powershell
./eng/fetch-assets.ps1
dotnet restore ScreenTranslator.sln --configfile NuGet.Config
dotnet build ScreenTranslator.sln -c Release --no-restore
dotnet test ScreenTranslator.sln -c Release --no-build
```

浏览器扩展的测试：

```powershell
node --test (Get-ChildItem ./browser-extension/tests -Filter *.test.js).FullName
```

## 提交代码

1. 从 `main` 创建一个主题分支。
2. 保持改动聚焦，并为行为变化补充测试。
3. 提交前运行完整构建和测试。
4. 在 Pull Request 中说明问题、解决方式和验证结果。

提交贡献即表示你同意贡献内容按仓库的 Apache License 2.0 发布。第三方代码和资源需保留原有版权及许可声明。
