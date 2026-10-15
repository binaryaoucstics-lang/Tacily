# Browser Use CLI 真实网页采集基础设施

此目录实现采集驱动，不包含已认证的百站结果或准确率结论。主控留存的公开开发证据已包含真实浏览器访问及重排/隐藏源观察；这些观察不等于已完成翻译或质量审核。本任务的开发验收只运行合成 DOM、mock CLI 输出与离线 JSONL 主机；它们不计真实 case。统一口径见 [网页 QA 证据指南](../../docs/WEBSITE-QA-EVIDENCE.md)、[本轮回归约束](../website-qa/REFLOW-REGRESSIONS-20261013.md) 与 [真实零调用 assessment / 引用版本契约](../website-qa/ASSESSMENT-PROOF-20261013.md)。开发事实引用公开契约，不链接本机或私有日志/产物。

正式页面判断固定使用 **fixed50（中文正文占比阈值 50%）**：采集端的 session 配置与 QA 主机规则判断均使用该基线。产品可配置比例是用户偏好，不能据此调整正式评分的判断口径或更改用户设置。正文识别、站点规则、手动控制、模型/请求预算与期限仍按原接口执行。

## 捕获与覆盖口径分开记录

[覆盖与有界证据契约](../website-qa/COVERAGE-EVIDENCE-20261013.md) 发布 `coverageScope={"schemaVersion":1,"mode":"strict"}` / `{"schemaVersion":1,"mode":"automatic-page"}`，由离线归一化与评分明确选择。固定基线已有 `normalize_case`、`evaluate`、`_score_case` 的关键字 `coverage_scope="strict"`；两项离线 CLI 使用 `--coverage-scope strict|automatic-page`。此次按 [assessment 契约](../website-qa/ASSESSMENT-PROOF-20261013.md) 扩展既有 automatic-page 的证据识别，元数据仍是显式 v1；扩展实现仍待主控集成验收。capture 不新增覆盖开关，不更改生产识别、独立标签或原始捕获。

strict 是默认原覆盖义务，旧的无口径元数据案例只允许 strict。automatic-page 必须有匹配的版本/模式元数据，不能静默使用旧或 strict 案例；strict 也拒绝 automatic-page 案例。只有固定 `expectedPageTranslation=false`、独立已审核的页面 false 标签、实际 boolean `pageDecision=false`，且全部请求核算后证明有效整数 `requestCount=0` 的负页，才允许调整部分正节点的覆盖义务。必须有 `type=capabilities, live=true`，加以下一种证明：

- 原有至少一个成功零调用 terminal（`type=terminal, success=true, requestCount=0, failedCount=0, cacheHits>=0`）。事件须有非空 `id` 和匹配本案例的 `caseId`；terminal 身份唯一，全部 candidate 请求与 terminal 一一对应，计数是明确非负整数且总和与顶层计数一致。
- 实际 assess-only 主机事件：原始 `localAssessment` 为 `type=assessment, schemaVersion=1, live=true, needsTranslation=false`，非空 `id` 和匹配本案例的 `caseId`，且 requestIds/terminals 都是有效空列表。归一化保留原始事件到 `hostEvents`，不合成翻译 terminal。

版本必须是整数而非 boolean；计数不能是 null、boolean 或字符串。只有 capabilities、空列表本身、缺能力证明、缺失/损坏请求列表、非 live/正向/unknown 或无效身份/版本的 assessment 不构成证明。其他未知事件类型仍拒绝。正页误跳过、标签分歧、非零调用、失败/非零/损坏 terminal、已发工作缺成功 terminal、节点预测缺失/失败/矛盾/unknown 均不能取得 policy skip。默认 strict 的原规则与所有旧测试不变；截图、真实最终 idle、来源归属、错误、未知与撤回门禁仍适用。

正确跳过不会把英文改标为中文或已翻译。GMW 公开开发案例有 412 个独立审核节点，契约提及的九个英文正标签及 FOLLOW 原文仍须保留。实际生产 eligible 决定原混淆矩阵项；未翻译英文列入 `unTranslatedPositiveNodes`，满足全部 automatic-page 条件的子集另列 `policySkippedPositiveNodes`。`confirmedOmissions` 只在该显式口径下调整必须覆盖的遗漏，strict 保留原含义。TP/TN/FP/FN、accuracy/precision/recall、`annotatedPositiveNodes`、`translatedPositiveNodes` 与 `translationCoverage` 公式不变；policy skip 不产生 TP 或翻译完成信用，不从覆盖率分母删除。案例与 overall/development/holdout 均须报告新计数，并从证据重新计算。

保留同一原始 case、冻结 review 和全部历史尝试，分别生成新的 strict 与 automatic-page 归一化文件、案例数组及报告；CLI 示例和兼容性规则见 [离线评分 README](../website-qa/README.md)。评分 CLI 的可选 `--output <path>` 以独占创建方式写入报告，拒绝已存在路径；省略时仍输出 stdout。新配对报告使用 `--output`，不以重定向覆盖旧文件。`review.py` 的截图审核结果不自动选择新覆盖口径。两种报告不能互相冒充；95/97 质量、100 完成域、30 完成 holdout、两类页面、未知/错误/撤回与请求门禁均按原要求执行。正确零调用跳过可以验证负页决策，不表示翻译了英文节点。

当前 `predictions` 表示最后采样时仍在生产 session 中的块；全部观察历史保存在
`predictionHistory` 和原始 JSONL。重排后已被新块替代的旧 pending ID 不再冒充当前漏译，
历史中没有独立源证据的选择仍会阻止完成。sourceNodes 保留完整历史，新增
currentlyRendered 表示它在本次真实 DOM 采样中是否仍呈现；审核不能凭预测生成真值。
已跳过的动态页面以页面决策稳定为等待条件，持续变化的源文字与几何仍保留，不声称
网页内容静止。正例仍需内容与完成状态稳定、真实请求及成功终止。原样名称的源前缀
（如 FOI → FOIA）单调补全不算译文撤回；真正不同的译文恢复原文/空白仍记录。
sourceObservations 保留每个独立源被实际采样且呈现/原子块代表时的生产分片集合；隐藏后缺少 block 不会
覆盖该观测。这是生产状态观察，不是独立 needTranslation 真值。若 mutation 曾采到可见源，但在下一次控制端采样前隐藏，可能只有 sourceNodes 历史而没有可用 sourceObservations；后来 eligible=false、currentlyRendered=false 不能证明历史拒绝。负观察需要源当时有完整独立成员证据、真实 idle 和可信生产标志；未观察或尚未解决的源保持 unknown，最后 pending 集合不能借用此前成功。
atomic block 的 sourceIds 来自生产 atomicRecords，每个成员都必须有独立源证据；同一块
完成状态可对应这些成员。全部原始采样、历史预测、失败终止和撤回继续保留。

## 边界和入口

- Python 标准库，无新增依赖。离线页面适配器检查使用仓库现有 Node。
- 所有浏览器操作只有一个入口：已安装 browser-use-cli skill 的 `scripts/run_browser_use.py` 包装器。使用命名 session、`--json open/state/eval/screenshot`，命令始终为参数列表、`shell=false`。没有 Playwright、CDP、WebDriver、HTTP 或 MCP 备用通道。
- 真实运行仅由主控执行。驱动不读取用户配置或凭据，不换模型，不绕过访问控制。`--host-command-json` 指向主控提供的 live-backend JSONL 程序；该程序承担正常设置与服务商选择。
- 原样分块注入生产 `page-auto-policy.js`、`translation-motion.js`、`page-auto-content.js`，然后安装 `page-adapter.js`。生产识别、分组、样式、文本写入、session.configure/receive、开放 Shadow DOM MutationObserver 都由原模块执行。适配器只替换消息传输并采集证据。拒绝存在扩展 runtime 的页面，避免两套 session 同时运行。
- 每次 eval 参数限制在 20000 bytes UTF-16 内；生产源通常按 8000 字符拼接后执行。分块按 JSON 序列化后的完整表达式（包括前后缀、转义、中文与非 BMP 字符）计算实际预算，必要时自动缩小，避开 Windows 32k 命令行限制。`state` 的 `data._raw_text` 原样保留；eval 从 `data.result` 读取，不解析 state 文本来生成候选。
- **CLI eval 不等待 Promise**：`(async()=>...)()` 或 `extractPageSnapshot(...).then(...)` 的 `data.result` 可能只是 `{}`，不能视为提取结果。负例 probe 使用 QA 页内任务：同步 `startProbe` 返回 id/running，生产异步提取器写入 completed/result 或 failed；Python 单独同步 `probeStatus` 轮询同一 id。只有 completed 且 samples 为数组、statistics 为对象才转发 assess；空对象、字段缺失、失败和超时保存错误，不记成功。轮询最多 15 秒且受剩余 case 超时约束；dispose 后异步完成不能恢复已失效任务。状态与实际结果均保留在逐站 JSONL 中。

## 主控运行

预先创建契约清单（JSON 数组或 `{"sites":[...]}`），每项必须包含 `id`、`registeredDomain`、`url`、`category`、`language`、boolean `expectedPageTranslation` 和 `split`（development/holdout）。重复注册域、重复 ID、凭据 URL 和域外 URL 被拒绝。清单不得根据结果临时替换。

示例由主控在有浏览器/模型授权的环境执行：

```powershell
$hostArgs = @('C:/path/to/BrowserPageQa.exe') | ConvertTo-Json -Compress
python eng/browser-use-qa/capture.py `
  --manifest C:/qa-input/sites.json `
  --evidence-dir C:/qa-evidence `
  --run-id public-sites-001 --session website-qa-001 `
  --host-command-json $hostArgs --limit 3 --case-timeout 300 --full-screenshots
```

证据目录为必需外部参数，必须位于仓库外。不要传账户、密钥或 endpoint 作为 host 参数。建议由主控选择专用干净的 Browser Use session；不要选已加载翻译扩展的浏览器。首次运行必须使用全新的 run ID，驱动在任何浏览器动作前冻结 HEAD、生产/驱动源哈希、清单 SHA-256、host 命令、session 和超时配置，并保存清单副本。生产源在注入前再次核对哈希。

相同参数加 `--resume` 恢复。HEAD、源哈希、清单内容或运行配置变化都会拒绝恢复，需另建 run ID。`--limit` 是本次最多尝试的站点数，可以恢复时调整。已取得完整待审核证据的站点会跳过；访问/模型/协议失败以及中断的站点在新的 `attempt-NNNN` 中重试。绝不覆盖历史 attempt。状态每次采样、截图和协议处理后原子落盘；JSONL 每次追加均 flush/fsync。重试仍保留原清单项及全部失败记录。

原子保存使用独占创建的唯一临时文件，flush/fsync 并关闭写入句柄后再替换目标。离线 trace 已定位 Windows `os.replace` 的 WinError 5（stable 阶段保存）；Windows 替换中的 5/32/33 仅允许追加 3 次尝试，间隔 20/50/100ms，每次写入 `evidence_io_retry`（case JSONL，启动时写 stderr）。打开文件被拒绝、其它权限错误均不重试。持续拒绝仍抛错，保留旧目标与本次完整临时文件，不更改权限或删除目标；后续可保存的 case 错误包含 errno/winerror、文件路径与函数/行号堆栈。最后检查点也不能保存时整个运行失败，不能作为成功证据。

退出码：0 表示本轮取得完整待审核证据，1 表示至少一个站点证据不完整，2 表示启动/配置错误。**退出码 0 不表示人工审核完成，也不表示达到质量目标。**

## JSONL 桥接与取消

`JsonlHost` 使用独立进程标准输入输出，后台线程持续读取 stdout/stderr，日志保留请求及完整原始事件。每个站点握手传入清单中非空的 case ID，capabilities 请求 ID 每次使用新的 UUID，响应必须同时匹配请求 id/caseId 并声明 `live` 与目标语言；不会重复固定的 capabilities ID。`live=false` 明确标记 simulation，不能成为真实完成 case。

页面 `send()` 创建带 RPC ID 的可审计 outbound 队列。Python 转发生产 `pageAutoCandidate`，仅添加 tabId/browserWindowId/frameId/siteHost；不改 blocks、samples、识别规则或译文。host 的序列化 `packet` 原样交给生产 session.receive，传输接受响应只解开对应 Promise。候选终止必须由相同 host 请求 ID 的 `terminal` 证明；取消使用原候选的 host ID，保留生产取消帧。EOF、错误、额度/授权失败、缺失终止、超时、域外跳转都不能当成功。前端本地跳过时另调用生产提取器取得 samples/statistics，转发 `assess` 规则核对，不创建翻译请求、不将该结果当人类真值。

`requestIds` 保留全部生产 native candidate 的主机身份，不表示云模型调用次数。负页也可能发送 candidate 并收到生产 assessment 帧；每个候选仍必须有相同身份的成功 terminal，且 requestCount 必须是明确的非负整数。缺字段、null、boolean、字符串、失败或缺失 terminal 都不能补成 0。独立 `assess` 以实际 host assessment 事件结束，不能结束 candidate RPC。[公开契约](../website-qa/ASSESSMENT-PROOF-20261013.md) 记载 `3c3ec34` 的真实 GMW 开发捕获具有匹配的 live negative assessment 与空 requestIds/terminals：没有发起模型工作，所以没有翻译 terminal。这种事件可按上述完整条件证明 automatic-page 零调用；bare capabilities 或已发工作缺 terminal 仍拒绝，旧拒绝测试原样保留。GMW 的九个英文节点仍独立为 true、未翻译，旧 unknown 历史继续阻止完成。合成扩展回归须提供真实形状的 fixture assessment，不把旧无证明夹具改成成功。真实模型调用、failed 节点、未解决工作和撤回仍阻止完成。

### 帧批量传送与期限

每轮先核验并交付已经排队的 host 事件，再调用 CLI 做 outbound drain 或 DOM 采样；同轮发送请求后再检查 host 队列。首个候选先通过轻量 drain 接纳，后续语义等待将 sample 与 drain 合为一次同步 eval，保留原 `qa`、`sample`、`drain` 和批量交付接口。采样值反映当时的真实生产状态，之后到达的完成帧不会改写该快照；仍按下一轮实际状态和期限等待。两个 host 队列检查合计最多 1000 个事件，额度用尽时下一轮继续优先交付，不先做昂贵采样。

转发只合并已经到达的 host 帧，不额外等待凑批。每批最多 32 帧，且将 RPC ID、原始 packet、首次确认标志及同步 JavaScript 执行包装全部 JSON 编码后计算 UTF-16 字节数，完整 eval 表达式不得超过 20000 bytes。同一批保持原事件顺序，各 RPC 仅在首次帧之前调用一次 `acknowledge`，随后每个原始帧调用 `deliver`；跨批和交错 RPC 也不重复确认。执行是同步循环，不把 Promise 或 `{}` 作为传送成功。单个 packet 过大时，通过既有 `inject` 分块存入数据，再用小表达式一次交给 `deliver`；不会截断、重写或重新序列化该原始帧字符串，数据也不会作为 JavaScript 执行。存储的数据在交付表达式的 finally 中清除。

速度改善来自减少包装器/CLI 进程调用：以离线回归中的 102 个较小合成帧为例，旧逻辑需要 204 次确认/交付调用，现在为 4 次批量 eval。真实包大小、到达时序和大包分块会影响实际调用数；这不是实际网站、真实模型或语料验证成功的结论。stdout 原行及每个 `host_event` 仍分别记录、flush/fsync；关联 ID、case ID、packet 错误与 terminal 失败仍核验。遇到错误先交付此前有效的已排队帧，错误 packet 本身也保持原样交付，然后记录失败，不用批量传送掩盖错误或生成完成证据。

已知 backend packet/terminal 失败后，保留每个原始错误与原样、有序的后续帧/terminal，在原 case 截止时间内继续采集证据。此路径不新发候选或重试翻译；保存实际 `failureSnapshot`、逐次 `finalState`、最终生产 completed/failed 标志和来源历史，并尽量保存 after、stable 及启用时对应的完整页截图。截图失败单独记错，剩余时间内仍读取原请求 terminal。这里的 stable 只是截图 phase 名，`settled` 和 `semanticStable` 保持 false，截图审核仍为空，case 保持 nonready/noncomplete。缺终止时保留已经取得的快照/截图并记录超时；期限耗尽后不再启动采样或截图，不能虚构未取得的最终状态。滚动/重排截图不强求补齐，完整证据门槛不变。

每次 forward 最多读取 1000 个事件，并在 drain、outbound 转发、每次 poll 和批量交付前后检查同一 case 期限；持续有帧也不能无限占用循环。Browser Use 子进程 timeout 取 60 秒与 case 剩余时间的较小值，超过期限返回的结果仍判为超时；大包的每次分块 eval 同样受限。assessment 等待也不超过 case 期限。失败证据仍保存，历史 attempt 不修改；期限耗尽后清理调用可能无法执行，其失败会追加为 cleanup 错误。

## 页面证据

每个 attempt 包含 `case.json`、`events.jsonl` 和截图。日志/截图只有外部输出，不提交仓库。

- before：配置翻译前独立遍历实际渲染的文本节点，包含代码、日期、按钮、数字等，不以生产 eligible 过滤。采集标签、角色、Range 几何、文档坐标和首屏标记。开放 Shadow DOM、slot 可读；关闭 Shadow DOM、跨域 iframe 无法读取，人工审核需记录这些遗漏。
- sourceNodes：身份分配与可见证据独立；只有首次确认实际 rendered 的完整行（标签、角色、几何等）才加入历史，不收录 SCRIPT/STYLE/隐藏文本的占位项。已观察的译文消失为空白时保留此前合法位置；已在任一阶段显示过的节点后来隐藏或脱离仍保留最后完整行。保留原文；用 DOM locator、源版本和节点关联分配稳定 ID，相同位置与原文的节点替换可保持 ID。生产 record 的保留原文用于翻译后追踪，不能让译文替代原文。来源改变使用新 ID。ID 只在当前文档 attempt 内稳定，不能声称跨加载稳定。
- predictions：生产 blocks 的实际 eligible、completed、failed、local/fromCache 及 sourceId。生产预测不能创建可见源证据；未独立观察到的源标记 `sourceEvidence=unmatched` 和 `auditIssue=source_not_independently_visible`，不得记为完整待审核证据或完成。后来实际显示后可匹配，其先前 unmatched 状态保留在阶段日志中。`productionEligibility` 另列当前可见候选是否对应生产 block；原始阶段快照均在日志中，便于核对动态 eligibility。独立 annotations 初始全部为 unknown，不能算正确。
- 原子 block 的 `sourceIds` 只排除源文为空或全为 JavaScript trim 空白的成员，保留所有非空成员的身份。当前显示为空、隐藏或脱离不能作为过滤理由；未独立可见的非空成员仍使整个预测 unmatched。不会改写生产 atomicRecords、文本或 eligibility。
- screenshots：before、after、stable、scroll-before、scroll-after、return-after、reflow-after。实际滚动约一屏，再返回顶部；存在关闭 details 时额外展开/还原并截图。重排通过临时设置 body max-width 为 viewport 的 80% 触发真实换行，保存并恢复原 inline 值及 priority；同时派发 resize 检查生产监听器。没有改浏览器窗口尺寸的能力，因此这不是 viewport resize 测试。
- 可选 `--full-screenshots`（别名 `--full`）另外捕获 before-full、after-full、stable-full，通过同一包装器调用 `screenshot <path> --full`。保留原首屏/滚动截图，完整页截图有独立 phase、文件、时间、viewport、SHA-256 与 `fullPage=true`；启用时缺少任一完整页截图不能记 evidenceReady，审核须对所有 phase 单独投票且核对原始哈希。选项冻结在 run 配置中；改变选项或源文件须创建新 run ID，原失败 attempt 不修改。完整页截图仍需人工检查长页的正文、裁切、重叠和重排，不证明无限滚动内容已经加载完毕。
- 截图命令可能触发新的扫描或请求，故截图之后、dispose 之前仍须通过 `final-idle` 的真实语义等待并转发原始 host 事件。`finalScreenshotProof.screenshotsCurrent=true` 表示最后 stable（启用完整页时也包括 stable-full）图片与该次最终状态有可审核的时间关联；它既可来自内容/语义与请求身份数量匹配，也可来自独立源版本/可见性变化的时间戳解释。已观察的 skipped 动态内容也可保留时间戳证据。此字段不声称网站静止、几何不动或人工布局审核通过。源文未变的显示/预测变化不能用“动态网页”解释；仍须刷新，原上限 3 次和同一 case 期限耗尽后保留失败。新请求必须先取得真实终止，不能借用先前 idle。
- 刷新使用新的 `stable-refresh-N` / `stable-full-refresh-N` phase，保存原图而不覆盖；`basePhase` 仅关联最后一对证明图片。人工必须审核所有唯一 phase，包括初始图、每次刷新图、完整页的全部区域/拼接内容与 details 图；最后一对为 true 不能抹去较早的失败或撤回。
- 每个阶段等待生产无 jobs/pending/scanning/rpcPending、实际 skipped/completed 状态、boolean pageDecision、全部已发请求成功终止且无 failed，再要求语义连续不变达到 stable_seconds（默认 2 秒）。只有 `pageDecision=true` 才要求当前 eligible 预测均 completed；`pageDecision=false` 时允许保留 `eligible=true, completed=false, failed=false` 的未翻译候选，原标志不改写，候选识别准确率另行评分。负例要求实际零模型请求，任一 failed、未解决任务、缺失/失败 terminal 都不能通过。正例语义签名使用当前 `nodes` 的源文/显示文本/标签/角色多重集，以及当前生产预测的源文/文本、eligible/completed/failed 和源证据匹配标志、pageDecision；保留重复行数量，忽略顺序、时间戳、坐标、locator 和仅用于 bookkeeping 的 ID/groupId。实际文本或数量变化会重新计时。使用当前节点而非累计 sourceNodes 历史判定，以免相同内容的新身份不断替换阻止语义稳定。负例等待页面决策稳定，允许源 ticker 内容变化；追加的 decision/outbound、assessing/waiting_for_page 事件或新 candidate 请求会重启 quiet period，因此两次 idle 采样之间的决策重置和 assessment storm 不能被误当成完成。全部原事件、来源与几何采样继续保留。
- `settled` 仅表示上述语义等待通过，另写 `settlementKind=semantic`、`semanticStable`、`layoutReviewRequired=true`。每次 raw sample 的完整几何和阶段信息保持在 JSONL；另追加 `stability` 记录，并在 case 的 `stability[phase]` 保存最后一条：semanticUnchanged/semanticStable/stableForSeconds 与 geometryChanged/geometryVaried/geometryChanges 分开记录。几何变化单独比较 rects/可见状态多重集及 viewport，不用文本、locator 或行顺序冒充位移；这些诊断不认证布局稳定。CSS 轮播持续移动仍可能语义稳定，截图的人工布局检查始终必须完成。所有源身份继续累积到 sourceNodes 和 unknown annotations，不按语义签名去重或过滤；未匹配预测、历史未知证据和失败继续保留。
- 截图存路径、时间、viewport 与 SHA-256。必要时主控增加滚动范围/站点交互；默认一次滚动不能证明覆盖无限滚动站点的全部内容。本轮仅以离线夹具验证语义等待修复，没有重跑真实 People.com.cn 或宣称真实语料通过。
- retractions：观察已显示的文本回到原文/空白，要求生产保留原文未变、显示处于译文视图。主动原文/双语切换与已知真源变化不计撤回。生产观察器修复后采样，因此只记录持续可观察撤回，不把同一回调内已修复的瞬态写入当已显示。不能证明从未出现帧级闪烁；截图/视频审核应补充此项。

`visitSuccess` 与 `completed` 分离。外语页必须 live、真实 requestCount>0、每候选成功终止、逐节点完成、截图齐备与语义稳定；模拟、全缓存无实际模型调用、失败或缺终止均不满足。目标语言页要求实际 false 决策与有效零模型证据；已发 native 候选必须成功终止，没有候选的 assess-only 路径保留真实匹配的 live negative assessment。`evidenceReady=true` 是等待截图审核的完整证据，初始 `completed=false`。

## 原有历史观察上限与溢出失败

覆盖契约保留 main `3c3ec34` 上 Wiki 开发捕获的 `producer_observation_overflow` 失败。[assessment / 引用版本契约](../website-qa/ASSESSMENT-PROOF-20261013.md) 进一步记载 `8517302` 的 site-021：producer 值驻留避免了前一溢出，但 **98 次实际调用、四张图片**后仍因 `independent_source_observation_overflow` 失败；中断前 1844 个源行、零观察到的撤回不能证明完成。原失败和图片不修改，也不借用后来未取得的完成。

引用版本导出（ref-only）只在满足原 **2048 行、2,000,000 字符**边界需要时精简冗余且未引用的导出版本。必须保留 retained producer views 所需的精确 source 及 represented-owner 版本、实际成员/归属/时间证据；不能从当前位置伪造见证或合并不同源版本。完整 `sourceNodes` 历史不变，小页和旧 fixtures 行为保留，原 fixture1901 版本数仍须通过。如果必要见证集合本身超过任一原边界，仍明确溢出失败。

完整观察必须保留源版本、几何、成员归属、真实扫描 idle 与有界的时间戳证据，继续拒绝过期分片。真正溢出、丢失或不完整的历史保持 unknown 或明确失败：不能压掉 overflow、提高限额、把 dropped 当成功，也不能将后来隐藏或空集合解释为历史 eligible=false/completed=true。atomic 成员移动仍按当时实际身份与完整成员证据审核；当前完成或旧缓存不能补齐丢失历史。覆盖口径不会改变这些来源要求。

离线验收要求使用 >1800 独立源的现实规模合成页，在原上限内核对传输，并同时检查大页移动/ref-only、atomic 归属、必要见证真正溢出、伪造版本、损坏/过期证据与 unknown 丢失。不能重置 dropped、静默 overflow 或推断缺失的负 eligibility。新增回归由对应实现任务在其归属内添加；本任务保留全部原测试。通过离线回归不代表重新运行了 Wiki、原始失败已消除或百站/质量门槛已达到。

## 独立审核（离线）

人类先依据独立原文、角色、位置和源截图标注，并在逐节点查看 predictions 前冻结页面与全部历史源标签及其证据版本；新源或源版本也须独立标注。无法判断的保持 unknown，不根据 eligible/completed、缓存或审核票生成真值，也不使用 holdout 调参。准备外部 review JSON：

```json
{
  "caseSha256": "case.json 文件的 SHA-256",
  "screenshots": {
    "before": true, "after": true, "stable": true,
    "scroll-before": true, "scroll-after": true,
    "return-after": true, "reflow-after": true
  },
  "annotations": [{"sourceId":"source-1","needTranslation":true}]
}
```

示例只列基本 phase；必须按实际 case 审核全部唯一 screenshot phase（存在 details 时也包括额外两张，启用完整页截图时也包括 before-full/after-full/stable-full，以及所有 stable-refresh-N/stable-full-refresh-N）。每张原图均须核对哈希、时间与源变化；完整页须逐区域检查。运行：

```powershell
python eng/browser-use-qa/review.py `
  --case C:/qa-evidence/public-sites-001/site-001/attempt-0001/case.json `
  --review C:/qa-evidence/site-001-review.json `
  --output C:/qa-evidence/public-sites-001/site-001/attempt-0001/reviewed-001.json
```

审核核对 case 与截图哈希，生成全新结果，不覆盖 case、review 或历史结果。模拟、缺失 live 证明或已发工作缺 terminal 的证据即使所有票为 true 也无法变为 completed；无翻译请求的真实 assess-only 事件按上述条件核验。未标注节点继续 unknown。完成捕获不是准确率通过；统计应纳入有独立真值的有效决策错误和未知/遗漏，不可只筛 completed 来美化指标。全体/holdout 指标与至少 100 不同域的验收由 `eng/website-qa/` 主控流程负责。

## 离线验收

```powershell
pwsh -NoProfile -File eng/parallel/Test-OwnedFiles.ps1 -PlanPath .parallel/plan.json -TaskId assessment-docs
pwsh -NoProfile -File .parallel/Validate-AssessmentProofTask.ps1 -TaskId assessment-docs
python -m unittest discover -s eng/browser-use-qa/tests -p 'test_*.py'
python -m unittest discover -s eng/website-qa -p 'test_*.py'
```

测试包括 mock CLI JSON/失败/分块注入（转义、非 BMP、中文与 UTF-16 边界），批量交付的原始帧/交错 RPC/单次确认/顺序、完整包装预算边界、大包安全数据分块、错误与逐事件日志、case 期限与子进程超时，Promise 空对象及任务 completed/failed/timeout 回归，负例只有完整 probe 才能转发 assess，合成 DOM 可见候选（脚本/样式/隐藏节点不生成证据、后来显示/隐藏/脱离保留完整历史、预测 unmatched）/稳定 ID/撤回/RPC/监听器，独立 JSONL 严格 mock 进程（拒绝空身份/重复请求、核验响应关联、同一主机连续两个站点），实际 Windows 句柄占用与模拟替换冲突/耗尽/旧证据保留/失败堆栈，持续 rect/locator/顺序/身份运动的语义稳定、所有替换源身份/unknown 历史保留、13 个 eligible 未翻译候选的 skipped 负例零翻译请求且捕获全部截图、负例文本变化/failed/未解决任务仍阻止稳定、文本/数量变化和未解决任务阻止稳定、完整页独立截图/哈希/审核门槛，逐站截图阶段、失败恢复、冻结/历史保护、完成门槛与独立审核。测试临时目录在 owned tests 内并自动清理。实际验收日志和修复前 trace 留在忽略目录，原主控审查失败报告保持不动；本任务的这些离线测试不执行真实 Browser Use、网络或模型，不改变已有真实访问的历史记录。

capture-evidence 回归另外使用 fake wrapper runner 与假主机事件核对失败 packet 后的原始 failed terminal（包含真实形式的非零 requestCount/failedCount）、最终节点标志与截图哈希、截图失败不丢终止、缺失 terminal 和截止瞬间不启动额外采集。负页回归涵盖零模型 native assessment、实际模型调用拒绝、unknown/缺失 requestCount、采样间决策重置及持续零模型 assessment storm。全部仍是离线合成证据，不计真实网页完成率。

## 调度延迟与截图生命周期诊断

QA 的 `schedule(callback, delay)` 将生产请求的延迟原样交给 `setTimeout`；未传延迟时为 0。`scheduledScans` 包括尚未开始的实际定时扫描，执行或取消时移除，dispose 清理剩余计时器。原 producer work 证明以及 capture 的语义等待和最终未解决工作检查均保留此计数：已排队的延迟任务不能凭 completed/skipped 状态被视为 idle。扫描开始后的执行状态仍由生产 scanning/jobs/pending 证明。

每次截图继续使用原 phase 的命令前完整样本，额外只调用两次轻量 `screenshotBoundary`，不增加 DOM 遍历、轮询或等待。新增 `screenshotLifecycle` 按截图尝试记录；成功图片通过 `lifecycleIndex` 关联。它包含页面命令前后时间、最后实际 sampleSequence、原始事件索引、viewport/scroll 和 scheduledScans，以及控制端 `commandStartedAt`/`commandFinishedAt`、实际命令返回或错误。诊断数量受已有截图阶段、刷新次数和 case 期限约束，不保留额外的源/producer 快照，不改变 2048 行、2,000,000 字符证明边界或刷新上限。

JSONL 的 `screenshot_boundary` 同时保存该边界之前尚未导出的原始事件增量；索引对应原有 append-only `sample.events`，不是重新构造的 DOM 变化。命令前边界从该截图样本的事件数量开始，命令后边界从命令前边界继续。原历史不消费、不截断；撤回按原事件索引保留，完全相同的两条观察仍是两条。即使截图命令抛错，也在同一 case 期限内尝试结束边界并保存原始撤回与原错误；边界失败或期限耗尽明确记 unavailable，不延长期限或制造结束证据。较晚完整样本不能清除已保存的撤回。

页面边界覆盖的时间还包含 CLI 往返；控制端时间只包围实际 screenshot wrapper 命令，两个时钟不可直接当成同一时钟比较。命令后边界之前导出的候选可以与之后的 scroll/reflow 区分，但不是精确写入时刻或浏览器 paint 证据。`paintedTransition` 始终为 unknown；最终图片已经恢复译文也不能抹除中途 DOM 候选、证明没有绘制过原文或使撤回通过。旧 v1 夹具没有边界接口时保留 unavailable/unknown，原 phase、截图文件/哈希、未知标签与审核门槛不变。

新增 `test_capture_scheduler_parity.py` 和 `test_capture_screenshot_lifecycle.py` 及 append-only adapter 检查覆盖实际适配器的非零延迟/默认延迟、延迟期间 unknown、取消/后续调度/dispose、无额外遍历的边界、完整页截图前后撤回、截图与边界错误、截止时间和重复原始观察保留。它们仅提供开发期离线回归证据；真实页面/Browser Use/provider 重放由主控进行，不代表百站或 95/97 质量目标达成。
