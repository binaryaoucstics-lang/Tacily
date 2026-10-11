# BrowserPageQa JSONL 主机

这是百站质量验证的后端基础设施；尚未执行真实网站测试，也没有准确率结论。
项目引用生产 App，目标为 .NET 8 Windows，不加入解决方案或新增 NuGet 包。

## 启动

```powershell
dotnet run --project eng/BrowserPageQa/BrowserPageQa.csproj -c Release -- --self-test
dotnet build eng/BrowserPageQa/BrowserPageQa.csproj -c Release
dotnet eng/BrowserPageQa/bin/Release/net8.0-windows10.0.19041.0/BrowserPageQa.dll
```

`--self-test` 独立执行合成 provider 验证，全部对外结果 `live=false`，不读个人设置、
凭据、不联网、不启动浏览器。主控提供的 `.parallel/Validate-Backend.ps1` 负责离线验收。
真实 JSONL 调用应启动已构建 DLL，避免构建日志混入 stdout；编码为 UTF-8，每行一个对象。

请求遵循只读公共接口 `eng/website-qa/CONTRACT.md`，要求 `schemaVersion=1`、
非空 `id`、`caseId` 和 `action`。一台主机内非取消请求 id 不可复用。

```json
{"schemaVersion":1,"id":"probe","caseId":"case-001","action":"capabilities"}
{"schemaVersion":1,"id":"assessment","caseId":"case-001","action":"assess","samples":[{"text":"The entrance is blue and the garden is quiet.","kind":"body"}],"statistics":{}}
{"schemaVersion":1,"id":"translation","caseId":"case-001","action":"message","message":{"type":"pageAutoCandidate","requestId":"r1","documentToken":"document","tabId":1,"browserWindowId":1,"frameId":0,"generation":1,"revision":1,"documentLanguage":"en","siteHost":"example.org","blocks":[{"id":"node","text":"The entrance is blue and the garden is quiet."}],"samples":[{"text":"The entrance is blue and the garden is quiet.","kind":"body"}],"pageSummary":{}}}
{"schemaVersion":1,"id":"translation","caseId":"case-001","action":"cancel"}
```

`cancel` 的 id 指向原 message 请求，caseId 必须一致；未知/已完成请求返回错误。
取消结果归属原请求，只在生产任务真正退出后输出一次失败 terminal。
最多同时接纳十个 message，与生产页面配置的十个并发 job 对齐；共享生产服务的十个 provider 执行槽，超限返回 `busy`。
每行最多 1 Mi 个 UTF-16 字符，每进程最多 100,000 个非取消 id；嵌套/重复字段及
生产候选边界无效时拒绝。输入 EOF 等待已接纳请求结束；Ctrl+C 取消并清理自己的任务。

## 输出与前端传输

- `capabilities` 只暴露 live、当前有效 provider 枚举、目标语言和生产 policyVersion。
- `assess` 使用生产样本/统计边界、站点策略和 PageTranslationDecision，返回 assessment，
  不创建 provider，不读秘密，不请求模型。
- `packet.packet` 是 BrowserPageAutoProtocol.Serialize 的 JSON 对象，原样传给生产
  `session.receive`。实际帧类型只有 `pageAutoAssessment` 和 `pageAutoTranslations`。
  第一个空 translations 帧的外层 `accepted=true` 用于桥接器解析 send 的
  `{accepted:true}` 应答；不能将 accepted/updates/complete 当作新的前端帧类型。
- 最终 translations 帧 `finished=true` 关闭前端 job；失败同时含净化 error。
  紧随其后的 terminal 才是审计证据，包含 success、requestCount、cacheHits、failedCount、
  elapsedMilliseconds。只有生产结果全部节点完成或生产决策跳过时 success=true；
  有 failed 节点、取消、认证错误、前缀中断都不能计为完成。
- error 仅输出固定错误码和说明，不输出异常文本或堆栈。传输写入失败会使进程非零退出，
  接收方不能把进程退出/EOF/缺失 terminal 当作成功。

主机只读正常当前用户的 LocalApplicationData/GlobalAITranslator/settings.json，
使用 Web JSON 选项反序列化为 AppSettings 并在内存 Migrate；不保存、不备份、不覆盖。
缺失/损坏配置报 `settings_unavailable`。首次加载后固定此设置快照，设置变更需重新启动主机。
缓存仅在进程内，不读写 App 的磁盘缓存。实际需要模型翻译才创建正常 router、
DpapiSecretStore 和 provider，参数与 ApplicationController.AutoPages 相同。
OAuth 仅使用正常账户服务的 token 获取/续期；正常续期可能更新受保护 OAuth token，
不调用登录或授权 UI，不切换账户、计费模式、provider 或模型，不提供模拟回退。

## 主控责任

worker 仅运行离线验收。主控负责在已授权环境准备构建资源、执行真实模式、Browser Use
真实 DOM 会话、截图和动态内容审核、独立真值标注、逐站 terminal/请求量检查及全仓门禁。
真实候选包含公开页面内容，只应在主控明确授权的真实运行中提交。
主控将原始 JSONL/stdout/stderr 和退出码保存到忽略证据目录，并核验身份匹配及 terminal 完整性。
模拟 self-test、可访问页面、零请求跳过不能替代需要翻译页面的真实模型与逐节点证据。

## 运行中版本与磁盘预设不一致

`--use-running-app-preset` 通过正常 BrowserBridgeClient 只读取得运行中应用的页面缓存
配置指纹，并调用生产 PageCacheScope 核对。磁盘配置一致时原样使用；用户已确认的官方
DeepSeek 配置仅允许匹配已保存预设标识，须唯一匹配全部翻译配置指纹。不会试用多个密钥、
更换模型/接口、写配置或重启应用。读取秘密仍延迟到真实请求。无法匹配、应用未运行或
版本不支持时失败；不退回旧默认密钥。网络配置沿用磁盘保存值，缓存指纹不验证代理选择。
该模式的首次真实诊断应核查应用版本和唯一区别，不能用于推断任意服务商的当前设置。
