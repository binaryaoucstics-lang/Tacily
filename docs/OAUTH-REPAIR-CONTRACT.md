# OAuth 翻译修复与 OpenAI 思考等级接口

## 已确认的原因与验收

2026-10-11，主控使用应用保存的账户连接与公开 `/v1/responses` 做最小文本复现：
JSON 模式只在顶层 `instructions` 提及 JSON、`input` 未提及 JSON 时返回 HTTP 400，
`error.param=input`；将 JSON 要求放入 `input` 后返回 HTTP 200 与 `response.completed`。
账户权限和模型列表读取成功。测试不记录或共享凭据，不使用用户的图片与翻译文本。

修复必须保持官方 endpoint、OAuth 独立付费路径、stream=true、store=false，
确保文本、网页与图片请求均满足 JSON 模式前置条件，不靠重复调用或更改账户绕过错误。
错误应保留 HTTP 状态、参数、错误码、返回结构与请求 ID；诊断日志不包含请求或响应正文。

## 共享接口（主控先提交）

- `ThinkingLevel` 保留 None=0、Low=1、High=2、Max=3，追加 Medium=4、XHigh=5、Minimal=6。
- `ThinkingLevelNames` 统一标签/effort 映射；ultra 不是该直接 Responses 接口的标准 effort。
- `TranslationThinkingCatalog.SupportedLevels(provider,model,useChatGptPlan)` 是已核实模型能力的回退。
  未核实模型返回空列表，使用服务端默认。`Normalize` 将旧设置/自动翻译覆盖值映射到可用等级。
- `TranslationModelChoice.SupportedReasoningLevels` 可空；null 是能力未提供，空列表是未识别到可发送等级。
  `DefaultReasoningLevel` 可空。模型列表解析忽略非法值和 ultra，保留已支持等级的顺序。
- `IChatGptTokenProvider.UpdateModelCapabilities` 保存账户模型快照；`GetSupportedThinkingLevels`
  返回当前账户/模型的临时能力；能力不写入凭据、不会跨账户复用。
- `TranslationProviderConfiguration.Thinking` 增加可选 `useChatGptPlan` 与 `supportedLevels` 参数；
  实际发送 effort 与 UI 选择一致，None 不受支持时归一为最低合法等级。
- 主控把全局设置标签转换接入共享映射，并在翻译/连接测试失败处接入
  `ChatGptFailureDiagnostics.Write(stage,error)`；该辅助类只输出安全元数据。

## 四个独立 Worktree 任务

| 任务 | 独占责任 | 验收 |
| --- | --- | --- |
| responses | Responses 请求和错误解析、对应 Core 回归测试 | JSON 模式全部输入类型；HTTP/SSE 错误与 request ID；保留终态检查 |
| thinking-core | 模型等级映射、OAuth catalog 解析与能力缓存接入、对应 Core 测试 | medium/xhigh 精确发送；API 与 OAuth 能力区分；未知模型与旧值兼容 |
| thinking-ui | 翻译设置 VM、模型加载 VM、翻译页面与设置界面测试 | 提供商/模型/账户切换刷新等级；预设/保存/测速不丢值；描述随模型改变 |
| diagnostics | OAuth 控制器、诊断辅助类、诊断测试与排障说明 | 安全错误元数据可追踪；不写正文/凭据；准确解释原因与验证边界 |

四个任务从本共享提交出发，不读取其他任务工作区，不更改本接口文件与共享入口。
使用仓库调度器独立启动 `codex exec`，统一 gpt-6.1-sol、high、fast。
主控重跑验收、保存提交、逐个集成，并执行完整仓库门禁后快进 main。

参考：
[模型与推理](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)、
[预览限制](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)、
[错误恢复](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery)、
[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol)。
