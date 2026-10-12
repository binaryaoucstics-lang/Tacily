# 真实网站 QA：固定清单与离线评分

本目录实现 [公共契约](CONTRACT.md) 的清单与评分基础设施。主控留存的公开开发证据已包含真实站点访问中的重排与隐藏源观察；访问和诊断本身不等于实际完成翻译。本目录文档及合成 fixtures **不能认证百站测试或准确率目标已达到**。统一口径见 [网页 QA 证据指南](../../docs/WEBSITE-QA-EVIDENCE.md) 与 [本轮回归约束](REFLOW-REGRESSIONS-20261013.md)。

## 固定判断与产品设置

正式页面判断使用 **fixed50：中文正文占比阈值固定为 50%**，采集 session 与 QA 主机规则判断使用同一基线。产品中可配置的中文正文比例属于用户偏好，不能拿它替换正式判断阈值、追随预测修改独立标签或为评分改写用户设置。50% 是规则的正文占比阈值，并非节点识别准确率或页面质量目标。原正文/语言识别、手动与站点规则保护仍适用；语料、holdout、超时与请求预算不变。

## 固定语料

`sites.json` 包含 130 个显式注册域名、90 个外语正例、40 个中文/繁中已可读负例、44 个 holdout。覆盖新闻、知识、公共服务、技术、旅游、商品、多语言与非正文入口。目标读者语言为中文；繁中可读内容无需为字形转换发起翻译。

每项都有公共候选 URL、分类、语言、初始页面标签与固定 split。`provisional-unvisited` 表示离线策划的初始标签，**不是实际访问或审核结论**。新闻、商品、动态门户可能出现地区跳转、登录提示、付费墙或安全拦截，由主控实际访问后保留原项、失败原因与截图，再固定有版本记录的替补；这些阻塞不能充当负例。清单中的负例理由是中文已可读，不是不可访问。

注册域名由清单显式确定（包括 `co.uk`、`edu.cn`、`gov.cn`、`com.tw` 等多级公共后缀下的注册域名），评分器不猜测“最后两段域名”。重复域名或父子注册域声明直接拒绝；同一清单域的多个尝试全部排除，防止挑选最好一次或将子域重复计数。若需要重试，主控另存每次原始记录，在评分输入中固定单次正式尝试并保留选择依据。

holdout 标签由独立审核者保存，不用于调参。评分器只读取固定 split，不允许案例自行改变 split、标签或 URL。若独立截图审核否定初始标签，该案例排除，主控须明确修订清单并保存版本差异，不能以预测生成真值。

## 离线使用

只需 Python 标准库，不联网、不启动浏览器、不读取用户设置或凭据。所有输入路径由调用者提供，截图读取限于显式证据根目录内，拒绝通过 `..` 或符号链接逃出该目录。

```powershell
# 真实采集和独立审核由主控完成；证据放在忽略目录。
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.json --evidence-root .parallel/website-qa > .parallel/website-qa/report.json
python -m unittest discover -s eng/website-qa -p 'test_*.py'
```

默认清单为同目录 `sites.json`，可用 `--sites` 指定明确版本。案例输入为 JSON 数组，或 `{"schemaVersion":1,"cases":[...]}`。退出码：0 表示 `achieved=true`；1 表示已成功评分但门槛未满足；2 表示输入/文件错误。报告始终明确 `achieved`，不会把空数据或基础设施验收成功当作真实目标完成。

## 独立审核输入约定

契约中的字段均应保留。下面的字段形状是本评分器的本地输入约定，未更改只读公共契约；主控负责将真实采集记录映射到这些字段，原始 JSONL、截图、错误和版本信息同时保留。

| 字段 | 评分要求 |
| --- | --- |
| 清单七字段 | `id, registeredDomain, url, category, language, expectedPageTranslation, split` 与固定清单逐项一致 |
| 访问元数据 | `finalUrl` 为该注册域或点分隔子域的 http/https URL；`visitSuccess=true`；非空 `title`；`viewport.width/height` 正整数；`sourceHead` 为 40 位 Git SHA；`capturedAt` 为带时区 ISO 时间 |
| `pageAnnotation` | `{needTranslation: boolean, reviewed: true, independent: true}`，来自源页面与截图的独立审核，禁止从预测或初始清单标签复制 |
| `sourceNodes` | 非空数组；每节点 `{id, text, tag, role, position:{x,y,width,height}}`；稳定唯一 id，非空原文，可见位置和正面积。必须独立收集所有可见候选，不能只导出被模型选中的节点 |
| `annotations` | 每节点 `{nodeId, needTranslation: boolean, reviewed: true, independent: true}`。缺失、未审核、非独立或 `unknown` 都是未知标签 |
| `predictions` | 每节点 `{nodeId, eligible: boolean, completed: boolean, failed: boolean}`。`eligible` 来自生产识别器，`completed` 是真实节点完成状态，禁止将被选择视为已翻译 |
| `screenshots` | `before/after/stable` 各为 `{path, reviewed:true}`；必须是证据目录内存在的 PNG/JPEG/WebP 文件。检查非空图片签名；内容真实性与可读性依赖人工审核 |
| `screenshots.checks` | `firstViewport, mainContent, afterScroll, layout, dynamicReturn` 都为 true。分别审核首屏、主要正文、滚动后内容、折叠/重排与布局、动态加载并返回后的稳定性；静态页面也须确认适用检查 |
| `hostEvents` | 保存原始契约 JSONL 事件数组。每项必须有非空 `id` 与本案例 `caseId`。正例必须存在 `type=capabilities, live=true`，以及真正完成的 `type=terminal, success=true, failedCount=0, cacheHits>=0, requestCount>=0`；拒绝重复 terminal id |
| 模型与结束状态 | 正例 `liveTranslation=true`，顶层 `requestCount` 为正整数且等于全部 terminal 的 requestCount 之和。流关闭、accepted、部分 updates、缓存文字或 `completed=true` 本身都不能代替终止事件。负例允许本地规则跳过，`hostEvents=[]` 与 `requestCount=0`，不要求云调用 |
| `retractions` | 必须有观察数组；无事件用 `[]`。事件 `{nodeId, reviewed:true, confirmed:boolean, sourceUnchanged:boolean}`；确认且源文未变时计撤回，译文后来恢复也不能消除失败。源文变化不算撤回；未审核事件不算完成 |
| 完成与错误 | `completed=true`、`errors=[]`、`blockedReason` 缺省/null/空字符串；`synthetic=true`、`mock=true`、非 live capabilities 明确排除 |

所有节点记录必须对应独立 sourceNodes；重复 id 或凭空出现的预测/标签不接受。页面仍需逐节点审核；只查标题、只查翻译结果截图、只使用模型选择的节点，都不能产生合格记录。负例仍需源节点、独立标签和稳定截图，且必须零不必要翻译请求。

`normalize.py` 中的 `requestIds` 是原始 native candidate 身份，不是云模型调用次数。负例可以有 native assessment；所有请求必须与原始 terminal 身份一一对应且各自唯一，每个 terminal 都须成功、`failedCount=0`、`cacheHits` 和 `requestCount` 为明确的非负整数。主机允许并发请求，终态完成顺序可以不同于请求接纳顺序；保留原始顺序是保留原始事件序列和每个请求的 packet/terminal 身份，不要求全局终态顺序等于 candidate 顺序，也不重排终态或 provenance。模型调用次数只取原始 terminal 的 `requestCount` 总和，零调用可作为负例证据；缺失、额外、重复、身份不符、字段损坏或失败的 terminal 仍阻止完成，不补成成功或零调用。错误、未解决工作、未知节点、遗漏与撤回继续阻止完成，原始捕获、审核、哈希和生产预测保留。正例仍必须有真实模型调用。

native assessment 的归一化往返回归使用离线合成夹具，核对负例零调用、实际调用、缺失/无效/失败终止与失败证据保留，以及正负例并发 `r1/r2` 请求按 `r2/r1` 完成时的身份、计数和原始序列保留；通过不代表任何真实网站已完成或质量目标已达到。

人类应先基于原文、角色、位置和源截图标注，再与预测匹配。`independent=true` 是审核声明，不是自动产生真值的开关。offline 程序无法验证声明是否诚实、截图是否真实采集或模型是否确实被调用，主控必须审查原始运行证据；本脚本不会把这种本地一致性检查声称为密码学认证。

## 动态与历史证据审核

1. 在逐节点查看生产预测前冻结独立页面标签和全部 sourceNodes 历史源标签，保留证据版本；未知仍为 unknown。新源版本按独立原文重新审核，不能从 eligible/completed、初始清单标签或截图投票复制真值。holdout 标签不用于修复调参。
2. 核对每张截图的时间、原始哈希与实际源观察，审核全部唯一 phase。除基本图、details 和启用时的 before-full/after-full/stable-full 外，每次 stable-refresh-N/stable-full-refresh-N 也分别审核，完整页逐区域检查。`normalize.py` 保留这些 phase 与原始 provenance，不用最后刷新图的票替代前面的票。
3. `finalScreenshotProof.screenshotsCurrent` 是采集端关于时间戳证据与真实最终 idle 的关联证明，不是网站静止或布局正确的声明。独立源文版本/可见性变化可以解释图与最终采样的差异；源文未变的显示/生产预测变化仍需原有限次刷新，持续不匹配、失败、撤回或期限耗尽继续阻止完成。此字段不能替代逐张审核、终止事件或质量评分。
4. `predictions` 是当前生产块，`predictionHistory` 与 sourceObservations 是历史生产状态，`currentlyRendered=false` 是当前不呈现。完整的历史源观察可证明当时的生产选择/分片状态，不能充当 needTranslation 真值。隐藏后当前没有 block 或 eligible=false 不能证明该源历史上被拒绝；若源只在 mutation 中短暂可见、控制端没取得当时的生产观察，应保留 unknown。负观察必须有当时真实 idle、完整独立源成员和可信生产标志；缺失、未完成、相互矛盾的证据不能补成正确拒绝。
5. 把冻结的源标签与实际分片成员逐项匹配，再审核实际完成、错误和撤回。atomic block 的 sourceIds 来自生产成员记录，每个非空成员都要有独立源证据；当前隐藏/空白不能删除成员。旧完成状态不能代替新的 pending；后来缓存译文恢复也不能消除已确认的撤回。

本轮公开诊断显示引用链接之间的独立横向分隔符变化及相同上下文刷新可伴随已完成内容恢复原文；另有短暂显示的机构名称只留下源历史而缺少当时生产观察。它们是需保留的缺陷/未知证据，不能仅凭“动态”或文档更新判定修复成功。细节与保护条件见 [回归约束](REFLOW-REGRESSIONS-20261013.md)。原始失败、所有刷新图、真实 offsets/hashes、日期/计数事实、请求身份、EOF/错误/终止与期限门禁均须保留。

## 统计口径

报告同时包含 `overall / development / holdout`，每案例给出完成状态、排除/未完成原因及错误计数，未提交清单项列入 `missingCases`。案例有两层状态：

- `evidenceAccepted`：清单身份、真实域、访问/元数据、截图审核、live 声明和节点数据结构可信且完整，允许按独立标签计分。真实模型终止失败、未知标签、错误、遗漏和撤回仍会显示，不用排除失败案例来提高分数。
- `completed`：上述证据齐全，加上运行真正完成、所有节点有有效独立标签/预测、正例真实模型请求与成功终止、无遗漏/撤回/不必要请求、所有检查通过。完成域名与证据域名分别报告，访问成功不等于翻译完成。页面决策错误在 page accuracy 中计错；一次已审核运行可以结束而决策错误。

假模型、越域、缺截图等案例不贡献 TP/TN 或完成数，原因明确列出。未知标签不进入正确数或混淆矩阵，单独报告并阻止通过；缺失/损坏预测不能产生正确节点，已确认需要翻译却没有有效完成的节点仍记遗漏（缺失预测为 FN）。重复域名尝试全部排除。无节点候选证据的案例保守视为未完成。

节点以独立人工 `needTranslation` 为真值、生产 `eligible` 为预测：

- TP：需要翻译且 eligible；TN：不需要且不 eligible；FP：不需要却 eligible；FN：需要但未 eligible。
- accuracy = (TP+TN)/(TP+TN+FP+FN)，precision = TP/(TP+FP)，recall = TP/(TP+FN)。
- `translationCoverage` = 真实完成的已标注正节点 / 全部已标注正节点。即使 eligible 识别正确，完成失败仍计 `confirmedOmissions`；识别准确率不能替代翻译覆盖。
- 页面 accuracy = 实际 boolean `pageDecision` 与独立页面标签一致的次数 / 有独立页面真值和有效截图证据的页面数。
- `completionRate` = 严格完成域数 / 固定清单域数。未知标签、遗漏、撤回和不必要请求同时列出。

零分母指标输出 null，绝不输出 100%。所有正节点都跳过时产生 FN 和遗漏，precision 为 null，无法通过。95% 与 97% 门槛使用整数分数精确比较，避免浮点边界漂移。

## 可审计门禁

`achieved=true` 必须同时满足：

1. 固定清单至少 120 独立域、35 负例、30 holdout；严格完成至少 100 个独立域。
2. 整体节点 accuracy、precision 各 >=95%，页面 accuracy >=97%。
3. holdout 至少 30 个严格完成域，且独立满足相同质量门槛；整体及 holdout 都必须完成正、负两类页面。
4. 整体及 holdout 没有确认遗漏、确认撤回、不必要翻译请求、未知标签或无效预测。

holdout 覆盖和质量门禁是额外的保守约束，防止 100 个开发站遮蔽未测留出集。召回、翻译覆盖和逐站遗漏仍独立呈现。清单中剩余未访问/排除项不算完成，允许主控保留阻塞并使用固定替补，不自动删掉失败证据。

`test_evaluate.py` 仅使用保留 `.test` 域、合成节点和 1px 图片测试门禁逻辑（包括 97% 边界、重复子域、假模型、未知标签、全跳过、缺截图、不必要请求与撤回）。合成 fixtures 的评分通过只验证实现，**不代表真实质量结果**。测试临时文件位于本 checkout 的忽略目录 `.parallel/`；运行日志由主控或调用者保留在忽略目录。
