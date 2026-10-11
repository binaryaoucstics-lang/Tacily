<p align="center">
  <img src="assets/branding/tacily.svg" width="144" height="144" alt="默译 · Tacily Logo" />
</p>

# 默译 · Tacily

**让理解自然发生。**

自动识别需要翻译的内容，在原位置流式呈现译文。面向全局、自动、无感的翻译体验，当前支持 Windows 桌面与 Chrome 网页。

本项目以 [PolyForm Noncommercial 1.0.0](LICENSE) 公开源码，仅允许该协议规定的非商业用途。许可范围、第三方组件与历史版本授权见 [许可说明](docs/LICENSING.md)。

基于 [ScreenTranslator](https://github.com/NiniTakukon/ScreenTranslator)（MIT）扩展开发。

## 功能

- `Alt+Shift+T` 后点击或拖框；也可在设置中选择直接翻译鼠标位置。
- 图片整图 VLM 翻译，流式擦字与原位嵌字；本地检测、颜色采样、横竖排与字体匹配并行运行。
- Chrome 自动判断网页语言，翻译正文、标题、导航、按钮与展开内容；中英混排页面保留原文，可手动翻译。
- 自动图片策略默认关闭，可选择仅检查大图和点开放大的图片；小图在读取像素之前跳过，大图经本机 OCR 判断需要翻译后才发送。按显示面积和视口占比筛选，持续观察放大与布局变化。
- 悬浮工具栏提供“翻译选中”“整页翻译”：点击“翻译选中”后点击段落即可翻译，无需手动高亮，Esc 退出选择。已有译文时显示原文／译文／双语切换，全屏视频时自动隐藏。图片不做双语。
- 点击悬浮工具栏旁的圆点折叠／展开，按住圆点可拖动位置；颜色区分准备、翻译、完成及异常状态，翻译中显示转圈动画，遵循动效开关与减少动画设置。
- 展开后点击小箭头打开常用设置：自动网页翻译、图片策略、目标语言、并发数与当前网站规则；保存至桌面设置，所有网页同步更新。
- 网站域名黑名单／白名单可在常规设置管理，支持从浮条直接设置当前域名。规则覆盖子域名，具体子域名优先，相同域名黑名单优先。黑名单阻止文字和图片自动翻译，仍可手动；白名单在自动模式开启时跳过整页语言判断，无需翻译的段落继续过滤。
- 默认翻译并发 10。网页译文与语言判断缓存支持刷新、返回与标签切换；默认本地磁盘缓存 256 MB。
- 多个自定义服务商预设，分别保存名称、模型、地址、思考等级与加密 API Key。支持 OpenAI 兼容 Chat Completions、OpenAI Responses、Anthropic Messages 与 DeepSeek 官方协议；可连接本机服务。网页文本翻译关闭思考以缩短等待。
- 自定义 API 可点击“读取模型列表”，按当前协议、地址及密钥查询并展开选择；保留手填 ID，支持取消及 Claude 分页。切换预设、地址或密钥会丢弃旧请求结果；本机服务可无密钥查询。
- 每个 API 预设可选择系统代理、直连、Clash 当前路由、Clash 指定节点或自定义 HTTP / HTTPS / SOCKS5 代理。翻译、连接测试和模型列表使用同一网络设置；切换线路保留译文缓存。
- Clash 节点支持选中节点测速、批量检测、停止、延迟排序；可用节点优先，排序保留当前选择。节点测速检测通用 HTTPS 连通性，当前 API 的可用性通过“翻译测速”验证。
- “翻译测速”按当前模型、节点和思考配置真实流式翻译一句英文，显示译文、首字延迟、总耗时与 TPS。token 用量取自服务商，不按字数估算；推理分项未知时显示含等待与思考的整体输出 TPS。
- API Key 在本机通过 Windows DPAPI 加密保存；自动翻译与历史记录默认关闭。

## 使用

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
