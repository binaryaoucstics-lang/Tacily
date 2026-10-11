# 真实网页质量验证接口 v1

目标保持为至少 100 个不同注册域名的公开真实网站；节点识别准确率和精确率均至少
95%，页面是否自动翻译的准确率至少 97%。记录召回率、遗漏和撤回，不能用全跳过
获得虚假的准确率。可访问网页不是完成的翻译测试，模拟模型结果不计入真实验证。

## 三个独立实现

- `eng/BrowserPageQa/`：.NET 8 Windows 标准输入/输出 JSONL 主机；调用现有
  `PageTranslationDecision`、`BrowserPageAutoProtocol` 和
  `BrowserPageTranslationService`。真实请求复用当前用户选择的服务商、预设、
  网络和正常凭据服务。不得替换模型、切换计费或写入用户设置。
- `eng/browser-use-qa/`：只经指定的 `run_browser_use.py` 包装器控制 Browser Use
  CLI。将生产 `page-auto-policy.js`、`page-auto-content.js`、
  `translation-motion.js` 注入公开真实 DOM，运行生产页面 session 和 MutationObserver。
  测试传输适配器可以替换扩展消息通道，识别、分组、样式和文本渲染不能重写。
- `eng/website-qa/`：预先固定站点清单、独立标注和计算指标；评分不读取模型预测
  来生成真值。截图、状态、事件和运行配置写入用户指定的忽略/外部证据目录。

## 主机 JSONL

每行请求是一个 JSON 对象：

```json
{"schemaVersion":1,"id":"case-001-probe","action":"message","caseId":"case-001","url":"https://en.wikipedia.org/wiki/Machine_translation","message":{"type":"pageAutoCandidate","requestId":"r1","documentToken":"opaque-document-token","tabId":1,"browserWindowId":1,"frameId":0,"generation":1,"revision":1,"documentLanguage":"en","siteHost":"en.wikipedia.org","blocks":[],"samples":[],"pageSummary":{}}}
```

`action` 为 `capabilities`、`assess`、`message` 或 `cancel`。message 是生产页面 session 发出的
原始 `pageAutoCandidate`；只添加传输要求的
tab/window/frame 信息。用现有协议读取和验证，不放宽实际限制。
`assess` 提供 `samples` 和 `statistics`，单独核对被前端本地跳过的页面；只调用生产
规则决策，不创建服务商、不读取凭据、不发起云请求。事件 type 为 `assessment`。

每行事件保留请求的 `id` 和 `caseId`，`type` 为：

- `capabilities`：`live`、`provider`（枚举标签）、`targetLanguage`、`policyVersion`；
  不返回账户、注册资料、API 地址、代理节点或凭据。真实主机 `live=true`。
- `packet`：`packet` 是生产 `BrowserPageAutoProtocol.Serialize` 生成的原始帧，
  如 accepted、assessment、updates 和 complete；前端原样交给生产 session 接收。
- `terminal`：`success`、`requestCount`、`cacheHits`、`failedCount` 和耗时；
  只有任务真正完成才发出，不用流结束或部分前缀冒充完成。
- `error`：固定/净化的错误代码和简短说明，绝不输出原始配置、密钥或异常数据。

主机不能打开登录窗口。OAuth 续期只能调用已有账户服务；缺配置、授权、模型或
额度必须报错并保留证据，不自动换成 API Key、模拟翻译或另一服务商。
`--self-test` 是独立离线测试方式，必须明确 `live=false`，不读取真实设置或凭据。

## 页面证据与逐站记录

站点清单字段：`id`、`registeredDomain`、`url`、`category`、`language`、
`expectedPageTranslation`、`split`（development/holdout）。注册域名按清单显式标注，
不得把同域子域重复计数；最终 URL 必须仍属于该域。清单先固定，访问失败保留原项，
替补也必须留下记录。不得绕过验证码、登录、付费墙、TLS 或浏览器安全拦截。

逐站结果 JSON 至少包括：

- 清单字段与实际 `finalUrl`、title、viewport、source HEAD、捕获时间；
- `visitSuccess`、`liveTranslation`、`completed`、`pageDecision`（实际 boolean）；
- `sourceNodes`：独立收集的可见文本候选，包含稳定 id、原文、标签/角色和位置；
- `predictions`：生产提取器/后端的 eligible 和实际 completed/failed；
- `annotations`：独立的人类审核标签 needTranslation；无标签记 unknown，不能算正确；
- `screenshots`：before、after、stable 和实际滚动/折叠/重排检查；
- `retractions`：已经显示且源文未变的译文回到原文/空白的事件；
- `errors`、`blockedReason`；状态不全或截图未审核不能记完成。

对已是目标语言的页面，expected=false 时，保持原页与零不必要翻译请求是成功。
对 expected=true 的页面必须有真实模型请求、完整终止、逐节点覆盖证据和翻译后截图。
至少核对首屏、主要正文和滚动后内容；动态站点需观察加载、重排和返回，不只检查标题。

页面正确率 = 正确页面决策 / 有独立真值的有效页面。
节点正确率 = (TP+TN)/(TP+TN+FP+FN)，精确率 = TP/(TP+FP)，召回率 = TP/(TP+FN)。
完成率、域名数、未知标签数和每站遗漏必须同时呈现。holdout 标签不用于调参，
最终指标必须同时报告全部集和 holdout 集；没有达到原任务条件不得判定目标完成。

## 验收边界

各 worker 只实现其 ownedPaths；不能执行真实浏览器、读取个人设置/密钥、发起云
翻译或网络请求。离线验收核对协议、取消、边界、计数和错误。主控负责 Browser Use
真实运行、截图审核、真实服务和全仓门禁。完整日志保留在忽略目录，源代码和小型
公开/合成回归夹具可提交，个人数据与生成截图不可提交。
