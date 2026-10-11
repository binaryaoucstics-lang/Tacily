<p align="center">
  <img src="assets/branding/tacily.svg" width="144" height="144" alt="默译 · Tacily Logo" />
</p>

# 默译 · Tacily

**让理解自然发生。**

默译是一款 Windows 翻译工具。看外语网页、使用桌面软件或阅读漫画时，无需复制粘贴，就能翻译需要的内容。Chrome 网页还支持自动翻译，译文直接显示在原来的位置。

本项目以 [PolyForm Noncommercial 1.0.0](LICENSE) 公开源码，仅允许该协议规定的非商业用途。许可范围、第三方组件与历史版本授权见 [许可说明](docs/LICENSING.md)。

基于 [ScreenTranslator](https://github.com/NiniTakukon/ScreenTranslator)（MIT）扩展开发。

## 已有功能

- **快捷键翻译**：按 `Alt+Shift+T`，点击文字或拖框选择区域，译文会在鼠标附近显示。适用于浏览器、桌面软件和 PDF。
- **网页自动翻译**：在 Chrome 中自动判断是否需要翻译，翻译正文、标题、按钮和展开后的内容，译文边生成边显示。中文为主的段落会跳过。
- **图片和漫画翻译**：识别图片里的文字，将译文放回原图对应的位置，支持横排、竖排和字体颜色匹配。
- **按需翻译图片**：可以只自动检查大图和点开放大的图片，跳过小图；也可以关闭图片自动翻译，自己点击选择。
- **手动翻译与原文对照**：网页悬浮窗支持点击段落翻译、整页翻译，以及原文／译文／双语切换。图片可切换原文和译文。
- **保存翻译结果**：译文缓存在本机，刷新或返回网页时优先使用已有结果，减少重复翻译和等待。
- **轻巧的网页悬浮窗**：可折叠成小圆点，也能拖动位置；全屏看视频时自动隐藏。展开后可以快速调整设置，指定哪些网站自动翻译、哪些网站跳过。
- **自选翻译服务**：支持 DeepSeek、OpenAI、Claude 和自定义 API，可读取模型列表、保存多套配置。API Key 加密保存在本机。
- **独立代理与测速**：不同配置可以分别使用直连、系统代理或 Clash 节点，并通过实际翻译测试速度，方便选择合适的线路。

## 使用

### 使用 ChatGPT 订阅额度

在“翻译设置”选择 **OpenAI Responses**，将“使用方式”改为 **ChatGPT 登录（使用订阅额度）**，点击 **Continue with ChatGPT**，在系统浏览器中登录并授权。返回后从账户可用模型中选择模型，保存并运行“翻译测速”。无需填写 API Key，翻译使用官方 Responses API。

可添加不同账户或工作区，并为每个预设选择账户。登录凭据通过 Windows DPAPI 加密保存，令牌接近到期时自动续期；退出登录会撤销会话并清除本机令牌，保留账户连接供下次登录。点击“管理额度”打开 ChatGPT 用量设置；达到额度限制后，新翻译请求暂停，调整额度后点击“恢复翻译”。账户、工作区和模型的实际可用性由 OpenAI 决定。参考 [OpenAI 官方接入文档](https://developers.openai.com/siwc/token-sharing-open-source)。

### 使用 API Key

1. 解压 Windows 包，运行 `ScreenTranslator.exe`。
2. 在翻译设置中选择默认配置，或“新增”服务商预设；填写名称、接口协议、Base URL 和 API Key。点击“读取模型列表”选择模型，或手填完整模型 ID，再保存并点击“翻译测速”。图片翻译请选择支持图像输入的模型。“复制当前配置”会复制当前参数与已保存密钥，随后可独立修改。
3. 按快捷键开始翻译。Chrome 网页功能需在“常规”页按引导加载包内扩展并连接桌面程序。
4. 根据需要启用自动网页翻译。图片策略独立选择“默认不自动翻译”或“仅大图 / 点开放大后自动判断”；未启用时仍可使用快捷键手动翻译图片。
5. 如需代理，在翻译设置的“API 网络连接”选择方式并保存。Clash 指定节点先点击“读取 Clash 节点”，为每个预设选一个节点。未检测到 Clash Verge 时提供[官方 GitHub 下载入口](https://github.com/clash-verge-rev/clash-verge-rev/releases/latest)。节点配置在本机内存传递，按需复用已安装内核创建独立通道；不修改 Clash 当前节点、订阅或配置。直连关闭应用层代理，系统 TUN / VPN 路由仍生效。

升级时先退出旧程序，解压到新目录，再启动新版。API Key 和设置存放在用户目录，安装包不携带这些数据。Chrome 已加载旧扩展时，在扩展管理页重新加载或更换为新包内的 `browser-extension` 目录。

## 开发

需要 Windows 10 2004 或更新版本、.NET 8 SDK、PowerShell 7 和 Node.js 22 或更新版本。无需 Python、CUDA 或独立 GPU。

```powershell
./eng/fetch-assets.ps1
dotnet restore ScreenTranslator.sln
dotnet build ScreenTranslator.sln -c Release --no-restore
dotnet test ScreenTranslator.sln -c Release --no-build
node --test (Get-ChildItem ./browser-extension/tests -Filter *.test.js).FullName

# 生成 Windows 自包含包；输出目录须为空
./eng/build-release.ps1 -Version 0.14.5
```

大体积字体与 ONNX 权重不纳入 Git，来源、版本、哈希见 [资源清单](eng/assets.json)。网页语言识别使用固定的 Unicode、正文与常用词程序规则，不加载语言模型；图片判断使用轻量 PP-OCRv5。本地判断不发送网络请求。已移除旧 OvisOCR2/Python、Manga OCR、CRAFT/Comic Text Detector、D1、NTextCat/Core14 与双 VLM 定位实验链路。

## 隐私与贡献

网页只处理可读、非编辑的公开 DOM 文本；输入框、编辑区、代码与隐藏内容不进入自动翻译。手动选段只发送点击处的段落。图片自动翻译需独立开启；经本地判断确需翻译的大图，以及手动选择的图片，会将整图发给配置的服务商。磁盘译文缓存包含来源文本与译文，可在设置中关闭或清空。

请勿提交 API Key、用户配置、浏览器资料、日志、缓存或个人截图。见 [贡献说明](CONTRIBUTING.md)、[安全说明](SECURITY.md) 与 [第三方声明](docs/THIRD-PARTY-NOTICE.txt)。

## 许可

当前 Tacily 衍生项目及新增代码、文档和品牌素材采用 [PolyForm Noncommercial License 1.0.0](LICENSE)。允许按协议进行非商业使用、修改与分享；商业用途需要权利人的另行书面许可。允许用途以英文许可全文为准，包括其中的非商业组织条款。

这是提供源码的非商业许可，不属于 OSI 定义的开源许可。原项目 [MIT 声明](docs/licenses/ScreenTranslator-MIT.txt) 和第三方资源许可继续保留；此前已按 Apache 2.0 发布的版本仍受其原授权保护，详见 [许可说明](docs/LICENSING.md) 与 [NOTICE](NOTICE)。

## 后续方向

Android、同声传译和实时字幕为后续规划，尚未在当前版本发布。

标准 Logo 使用最新接缝对齐版的 [SVG 源文件](assets/branding/tacily.svg)，采用标准饱和度的蓝绿配色。
