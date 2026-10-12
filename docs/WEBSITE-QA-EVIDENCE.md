# 网页 QA：固定判断与动态、历史证据边界

本指南解释现有 [网页质量契约](../eng/website-qa/CONTRACT.md)、[Browser Use 采集](../eng/browser-use-qa/README.md) 与 [离线评分](../eng/website-qa/README.md) 的审核口径，不扩展结果字段或放宽完成门禁。[本轮回归约束](../eng/website-qa/REFLOW-REGRESSIONS-20261013.md) 是缺陷与保护条件的依据。

主控留存的公开开发证据已经包含真实浏览器访问期间的引用重排、导航显示变化和短暂可见源观察。这些历史观察应保留，不能再笼统称“从未访问真实站点”。本指南、诊断摘要和离线合成 fixtures 都不能证明真实翻译已完成，也不能认证百站或质量目标已达成。本任务不运行真实浏览器、网络或模型。

## fixed50 与可配置产品比例

正式页面判断采用 fixed50：中文正文占比阈值固定为 **50%**。采集端 session 的 `chineseProseShareThresholdPercent` 与 QA 主机规则判断均采用该默认基线；用户在产品中配置的比例不能改变正式判断口径。50% 是正文语言规则中的比例阈值，不是翻译覆盖率、节点识别准确率或页面准确率。

规则仍须遵守正文识别、语言证据、站点规则与手动模式等现有保护，不能把整页文字简单按字符数投票。独立标签来自源页面的可读性与翻译需要，不能根据 fixed50 的预测反推真值。保留用户设置、服务商与凭据、固定语料与 holdout、请求预算、超时及原始失败；不能通过改阈值、增加预算或放宽审批/沙盒使测试通过。

## 先冻结独立源标签，再看逐节点预测

1. 核对固定清单、源版本和证据身份。独立遍历实际可见原文，保留标签/角色、位置和全部历史 sourceNodes；不能只收集生产选中的块。无法读取的关闭 Shadow DOM、跨域 iframe 等范围须明确保留为证据限制。
2. 审核者先依据原文、角色、位置与源截图给出页面和每个历史源的 needTranslation，并在逐节点查看 predictions 前冻结标签与证据版本。无法判断的源保持 unknown。新出现或真正改变的源版本也须独立标注，不自动沿用预测或初始清单标签。
3. 随后逐项匹配生产块与源身份，检查 eligible、实际 completed/failed 和全部非空 atomic 成员。sourceIds 来自生产 atomicRecords，每个成员都需独立证据；不能因隐藏或译文为空删除成员。生产标志、缓存、审核票和 independent=true 声明本身都不能生成真值。
4. 审核截图、实际终止、失败与撤回，生成新审核结果，保留原始捕获、旧审核、哈希和全部 attempts。未知不能进入正确数；holdout 标签不能用于调参，也不能只挑 completed 案例计算漂亮指标。

## screenshotsCurrent：时间戳证明与真实最终 idle

截图是某时刻的视觉观察。命令可能触发滚动、重排、生产扫描或新请求，故截图之后、QA dispose 之前还要完成 `final-idle` 等待：实际 jobs/pending/scanning/rpcPending 均空，生产状态和 boolean pageDecision 有效，所有已发请求取得对应终止，无 failed；正例当前 eligible 节点须真实完成，负例须有明确零模型请求证据。先前 idle、accepted 或流结束不能代替这一最终状态。

现有 finalScreenshotProof 把最后 stable 图（启用完整页时还包括 stable-full）与最终状态关联。`screenshotsCurrent=true` 可表示内容、语义和请求身份数量相匹配；也可表示独立源文版本/可见性变化在连续时间戳观察中解释了差异。`timestampedDynamicContent`、`authoredSourceChanges` 与 `dynamicSkippedContent` 是既有诊断，须结合原始样本判断，不能只看一个 boolean。

**screenshotsCurrent 表示时间戳证据与真实最终 idle 的关联，不表示网页静止。** 动态中文 ticker 的 skipped 决策可稳定而正文继续变化；轮播可以在图片与最终采样之间更换源版本或隐藏/显示节点。原始源历史、每次采样、几何与截图仍须保留，人工检查布局、可读性、遮挡及覆盖范围。semanticStable/settled 也不认证几何稳定或质量达标，stable 只是截图 phase 名。

源文未变的显示变化或生产状态变化不能借“动态”豁免。相同 locator/原文下更换 bookkeeping ID，或隐藏后再显示同一源，也不能掩盖译文变化。不能解释的最终图差异仍按原规则刷新，最多 3 次，且使用同一 case 截止时间；新请求须实际完成终止。超过刷新上限、EOF、错误、缺失/失败终止、未解决任务、未知源或期限耗尽均保留失败，不伪造 idle 或补写未取得的截图。

## 所有唯一截图 phase 都要审核

- 基本 before、after、stable、scroll-before、scroll-after、return-after、reflow-after；存在 details 时还包括展开/还原图。
- 启用完整页时的 before-full、after-full、stable-full；检查图片的全部长页区域/拼接内容，不能只看首屏。一次滚动或完整页截图不证明无限滚动已加载全部内容。
- 每次独立 stable-refresh-N 与 stable-full-refresh-N。刷新保留原图、独立 phase、时间与哈希，不能覆盖失败图；basePhase 仅用于关联最后证明图片，不把不同 phase 合并成一次投票。

review 的 screenshots 投票须覆盖实际捕获的全部唯一 phase，逐张核对原始哈希、时间和源变化，失败图或未审图不能由最后一对的通过票代替。归一化保留所有 phase 与 provenance；screenshots.checks 的汇总也不能代替逐张审核。所有 full-page 区域及刷新图都应独立查看，不能以“后来恢复”抹去先前撤回。

## 历史生产观察不等于缺失或隐藏源的拒绝

| 证据 | 可以说明 | 不能推出 |
| --- | --- | --- |
| sourceNodes 中完整可见源及时间/位置 | 曾独立观察到此源；currentlyRendered 说明本次是否仍呈现 | 被选中、完成或需要翻译 |
| 当前 predictions | 当前生产块的成员与 eligible/completed/failed 状态 | 未列出的历史源必然被拒绝 |
| predictionHistory 与匹配的 sourceObservations | 在记录的时刻，源实际呈现/原子块代表时的生产分片与标志 | needTranslation 真值或后来 pending 已完成 |
| 隐藏源 currentlyRendered=false，当前 eligible=false 或没有 block | 当前不呈现/不在当前生产块中 | 历史上正确拒绝或漏译已消失 |
| 只在 mutation 中短暂可见，控制端无当时生产观察 | 有源历史，但历史生产选择仍缺证据 | 用随后空集合补成负预测 |

历史观察须匹配独立 sourceId/sourceText、原始分片历史和完整 atomic 成员。可信负观察还要求源当时真实 idle 与明确生产标志；没有观察、成员不全、尚在扫描/工作中或相互矛盾时保持 unknown。后来隐藏不能覆盖先前合法观察；只保留源文本也不能创造缺失的生产证据。现有基线在两次控制端采样之间的短暂可见源上可能缺少 sourceObservations，须明确该限制，不能在文档中声称已补齐。

## 保留显示失败与回归保护

公开开发诊断记录了引用标题与归档链接之间的独立横向分隔符变化，以及相同上下文刷新中已完成块被新 ID 替代、原文重新显示的现象。只有实际 DOM 所属链接和源文能证明它是链接间分隔变化；不能扩大为任意空白删除或把正文词间连接视为无意义格式。保留真实 offsets/hashes、日期/计数事实、完成内容与旧响应失效保护。

响应式导航与机构名称的短暂可见性也需按时间审核。源文、事实兄弟节点、选区或 slot 真的变化，部分/失败任务以及显式原文/暂停控制均保持保护；不能隐藏 observer 事件或将每个 mutation 当成格式变化。

源文未变且处于译文视图时，已显示译文回到原文/空白是真实撤回候选；确认的撤回继续失败，即便缓存随后恢复。显式原文/双语切换、真实源版本变化和原样名称前缀的单调补全依既有规则区分。采样只证明持续可观察的状态，不能证明从无帧级闪烁，视觉审核需补充。

## 完成与质量结论

visitSuccess 仅证明访问；evidenceReady 是完整证据待审核；completed 要求实际执行、独立节点证据、所有截图审核、终止、覆盖与错误门禁。正例必须有真实 live 模型请求、逐节点完成且成功终止；模拟、自测、纯缓存或离线套件不算实际完成翻译。负例保持可读原页与实际零不必要请求可以完成该负例验证，但不能记作翻译了页面。

质量验收仍需至少 100 个不同注册域的严格完成案例，节点 accuracy/precision 至少 95%、页面 accuracy 至少 97%，并满足原评分器的整体与 holdout 门禁。文档、访问数量、诊断记录和合成测试通过都不能替代这些实际证据；未达到条件就保持未达成，不在文档中填入推测的成绩。

离线验证分别运行 Browser Use 的全部 unittest 与 website evaluator/normalizer 的全部 unittest，命令见两份 README。通过仅证明当前实现的离线契约与回归保护；生产修复需主控审查真实差异，再以新采集和独立审核确认。各开发任务只读固定基线，依赖任务的验收不共享其代码；本指南不宣称其他任务计划中的观察已实现。
