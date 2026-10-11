# ChatGPT OAuth 翻译排障

## 已确认的 JSON 参数错误

2026-10-11，主控使用相同 OAuth 连接和模型，向官方公开 `/v1/responses` 发起最小文本请求：

- 顶层 `instructions` 提及 JSON，`input` 未提及 JSON：HTTP **400**，`error.param=input`，`error.code=null`。服务端原句为：
  > Response input messages must contain the word 'json' in some form to use 'text.format' of type 'json_object'.
- 在 `input` 中提供 JSON 输出要求后：HTTP **200**，流终态为 `response.completed`；`Hello, world!` 得到译文 `你好，世界！`。
- 同次排查确认账户权限和模型列表读取成功。此例是 JSON 模式的参数前置条件未满足，不能据此判断额度耗尽或要求重新登录。仅在顶层 `instructions` 写 JSON 不够；文本、网页、图片请求都需要在 `input` 中提供可信的 JSON 要求。

以上是真实接口的最小复现，尚不等于修复已在用户 UI 中运行。主控须完成集成与完整验收，再以新构建检查各类翻译。**同机已经运行的旧应用不会自动热更新**；验证时退出旧进程，启动主控的新构建，确认实际启动版本。

## 先看状态、参数与错误码

| 诊断线索 | 含义与处理 |
| --- | --- |
| `status=400 param=input`，本例 `code=unknown` | 对照上面的 JSON 前置条件；检查新构建的输入构造。不要重复发送同一无效请求或自动重新授权。`unknown` 不能单独证明这是本例。 |
| `subscription_sharing_usage_limit_exceeded` | 标准订阅额度限制：暂停该账户的新翻译，点击“管理额度”，调整后由用户点击“恢复翻译”。不要通过自动重登、循环重试或换接口绕过限制。 |
| HTTP 429、`rate_limit_exceeded` / `insufficient_quota` | 区分请求频率与可用额度；频率限制稍后重试，额度问题检查对应付费路径。429 本身不能证明是订阅额度耗尽。API Key 付费与 OAuth 订阅额度是不同路径。 |
| `chatpass_v2_scope_not_authorized` / `chatpass_v2_invalid_authorization_context` | 检查所选账户的额度授权；需要时由用户明确重新授权。 |
| `subscription_sharing_user_not_eligible` / HTTP 403 | 检查账户、工作区、地区和可用权限；重新登录不保证解决资格问题。 |
| `invalid_grant`、`invalid_refresh_token`、`token_expired`、`refresh_token_expired`、`refresh_token_invalidated`、`refresh_token_reused` | 登录续期已失效，按应用提示由用户重新登录原账户。诊断本身不清除凭据，也不触发重新授权。 |
| `subscription_sharing_unsupported_capability`、`model_not_found`、`param=reasoning.effort` | 重新读取当前账户的模型列表，检查模型、输入类型和可用思考等级。 |
| HTTP 5xx，或初始化/登录阶段没有状态码 | 分别检查服务可用性、网络或本机凭据存储；没有状态码不代表没有请求，也不能推断额度或权限。 |

OAuth 翻译保持官方公开 `https://api.openai.com/v1/responses`、`stream=true`、`store=false`；**不得改为 `backend-api` 私有路径**。模型列表成功不代表每种输入、每个参数都获准。

## 模型和思考等级

按当前账户模型列表提供的能力选择等级；切换账户或模型后重新读取，不复用另一账户的能力。API Key 模型能力和 OAuth 账户能力可能不同。

- `low / medium / high / xhigh / max` 分别为低 / 中 / 高 / 超高 / 最高，`medium`、`xhigh`、`max` 必须分开。
- 已核实的 GPT-6.1 Sol / Astra 不支持关闭（`none`）或 `minimal`；部分 API 模型可关闭，但不能据此假设 OAuth 账户也支持。
- OAuth 模型列表里的 `ultra` 不属于本应用直接 Responses 请求使用的标准 effort，不能发送；本流程也不发送 `multi_agent`。
- 未知模型且没有已识别能力时采用服务端默认，不编造思考参数。缺少能力字段与明确没有可发送等级需要区分。

这些是本轮修复的接口约定；新 UI 的实际表现需主控在集成构建中验证。

## 安全日志

查找 `diagnostics.log` 中的 `chatgpt_failure` 行：

- 主位置：系统“文档”文件夹下 `GlobalAITranslator\logs\diagnostics.log`。
- 同时尝试写入 `%LOCALAPPDATA%\GlobalAITranslator\diagnostics.log`；目录不可写时日志可能缺失。轮转文件名为 `diagnostics.previous.log`。
- 阶段 `stage` 仅有 `translation`、`connection_test`、`initialize`、`sign_in`、`sign_out`、`refresh_state`，其他值为 `unknown`。
- 字段仅为阶段、数字 HTTP `status`、验证后的 `code`、`param`、结构标签 `shape` 和 `request_id`。例如：

```text
chatgpt_failure stage=translation status=400 code=unknown param=input shape=error request_id=req_0123456789abcdef0123456789abcdef
```

此行是合成示例。缺失或未通过验证的字段输出 `unknown`；本例服务端 `error.code=null` 因此显示 `code=unknown`。已知错误码和结构标签使用固定词表，参数仅接受有限的协议路径，如 `input`、`input[0].content[1].text`、`text.format`、`reasoning.effort`。请求 ID 仅接受 `req_` 后 1～16 位字母数字或 17～64 位十六进制标识，以及标准带连字符 UUID，并拒绝凭据形态；新格式可能显示为 `unknown`，需要审查后更新规则。

OAuth 控制器遇到未知异常类型时仅保留阶段，其余字段为 `unknown`；共享翻译和连接测试入口仍仅记录 `ChatGptException`，避免把其他提供商错误标成 OAuth 失败。日志不读取异常 `Message` / `ServerMessage`、堆栈或正文，不记录账户、邮箱、访问/刷新/身份令牌、输入图片或译文。登录和退出失败保留原有异常处理与凭据生命周期；日志写入失败不得改变翻译结果。排查时优先按时间、阶段、状态、参数和请求 ID 关联请求。

## 验证边界

- 主控真实复现证明该 OAuth/模型组合的 JSON 前置条件和一次成功终态，不证明所有账户、模型、额度、图片与网页 UI 都正常。
- 模拟 HTTP/SSE 测试可验证请求构造、错误解析与终态处理；不能证明实际账户资格、网络、额度或线上模型行为。
- 本任务的 `ChatGptFailureDiagnosticsTests` **只调用纯 `Format`**，覆盖安全元数据及伪凭据、JWT、换行、异常路径、超长字段；不调用 `Write`，不触发实际用户日志、配置、登录或网络。
- 控制器诊断路径需经源码审查和编译门禁；实际 UI、日志落盘及完整翻译流程仍由主控在集成后的新构建验收。

公开参考：[模型与推理](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)、[预览限制](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)、[错误恢复](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery)。
