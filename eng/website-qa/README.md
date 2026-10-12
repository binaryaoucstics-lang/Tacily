# 真实网站 QA：固定清单与离线评分

本目录实现 [公共契约](CONTRACT.md) 的清单与评分基础设施。主控留存的公开开发证据已包含真实站点访问中的重排与隐藏源观察；访问和诊断本身不等于实际完成翻译。本目录文档及合成 fixtures **不能认证百站测试或准确率目标已达到**。统一口径见 [网页 QA 证据指南](../../docs/WEBSITE-QA-EVIDENCE.md) 与 [本轮回归约束](REFLOW-REGRESSIONS-20261013.md)。

## 覆盖口径接口 v1

[覆盖与有界证据契约](COVERAGE-EVIDENCE-20261013.md) 发布两种明确口径。此节描述公共接口与集成验收要求；本任务只修改文档，固定基线尚无新增参数，不能据此宣称其他任务的实现已经通过验收。主控集成并验证实现后使用下列新参数。

- `normalize_case`、`evaluate`、`_score_case` 增加关键字 `coverage_scope="strict"`，保留现有位置参数调用。CLI 增加 `--coverage-scope strict|automatic-page`，非法模式必须拒绝。
- 归一化案例与评分报告记录 `coverageScope={"schemaVersion":1,"mode":"strict"}` 或 `{"schemaVersion":1,"mode":"automatic-page"}`。覆盖口径版本与原有案例 envelope 的 `schemaVersion` 分别保留。
- 默认 `strict` 保持原测试与覆盖义务。无 coverageScope 的旧案例只允许 strict；strict 拒绝 automatic-page 案例。automatic-page 必须取得版本、模式均匹配的案例元数据，不能静默转换旧案例或 strict 案例。

| 口径 | 正节点覆盖义务 | 其他完成条件 |
| --- | --- | --- |
| `strict`（默认） | 所有独立已知正标签缺少真实完整翻译均保留原遗漏含义 | 原捕获、审核、预测、请求、截图、未知、错误与撤回门禁 |
| `automatic-page`（显式） | 仅在下述正确跳过负页中，经有效生产预测证明的正节点可以列为 policy skip；其他正节点仍须真实完整翻译 | 同样保留所有其他门禁，policy skip 不产生翻译完成或 TP 奖励 |

正确跳过必须同时满足：固定清单 `expectedPageTranslation=false`、独立且已审核的 `pageAnnotation.needTranslation=false`、实际 boolean `pageDecision=false`、有效整数 `requestCount=0`，以及有效的主机事件/terminal 身份与计数证据。automatic-page 还必须有 `type=capabilities, live=true` 和至少一个成功的零模型调用本地/native assessment terminal：`type=terminal, success=true, requestCount=0, failedCount=0, cacheHits>=0`。每个事件的非空 `id` 与本案例 `caseId` 必须有效，terminal 身份唯一，全部请求与 terminal 一一对应；计数字段必须是明确的非负整数，不能是 null、boolean 或字符串。先核对全部请求与原始终止事件及计数总和，再判断覆盖义务。native assessment 身份不是模型调用数；空请求/terminal 列表、只有 capabilities 或独立 `assessment` 事件不能证明 automatic-page policy skip。非零调用、损坏计数/事件、标签分歧、正页误跳过均不能取得 policy skip。缺失、失败、矛盾或 unknown 的节点预测不能取得豁免；原始错误与未知仍阻止完成。初始清单标签或预测不能代替独立页面/节点标签。strict 的原零调用负例规则与全部原测试保持不变。

两种口径都保留所有 `needTranslation=true`、TP/TN/FP/FN、accuracy/precision/recall，以及原有 `annotatedPositiveNodes`、`translatedPositiveNodes`、`translationCoverage` 公式。案例及 `overall / development / holdout` 聚合须额外呈现：

| 计数 | 含义 |
| --- | --- |
| `unTranslatedPositiveNodes` | 所有已知正标签中，缺少已证明完整翻译的节点数（整数） |
| `policySkippedPositiveNodes` | 上述未翻译节点中，automatic-page 下满足全部正确跳过条件的子集数（整数）；strict 无此豁免 |
| `confirmedOmissions` | 当前显式口径下必须翻译却未完成的节点数；strict 保留原含义 |

这些计数必须从证据重新计算，不信任输入摘要；policy skip 仍留在翻译覆盖率分母，不能冒充 `translatedPositiveNodes`。GMW 公开开发案例的 412 个独立审核节点、契约提及的九个英文正标签及 FOLLOW 原文均须保留。中文主导页面的独立 false 标签与实际零调用跳过可以解释自动页覆盖义务；英文节点仍标 true、保留其实际 eligible 对应的混淆矩阵项，并明确列为未翻译及符合条件时的 policy skip。这些事实不是完成率或质量达标结果。

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

以下是覆盖契约集成后的离线 CLI 用法，同一原始捕获及冻结审核分别生成新文件；不覆盖原 case、review、截图或旧报告。两份 `cases.*.json` 由对应口径的归一化案例组成数组或上述 envelope，不能将单个案例对象当作数组，也不能混用口径。

```powershell
python eng/website-qa/normalize.py --case .parallel/website-qa/case.json --review .parallel/website-qa/review.json --evidence-root .parallel/website-qa --coverage-scope strict --output .parallel/website-qa/normalized.strict.json
python eng/website-qa/normalize.py --case .parallel/website-qa/case.json --review .parallel/website-qa/review.json --evidence-root .parallel/website-qa --coverage-scope automatic-page --output .parallel/website-qa/normalized.automatic-page.json
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.strict.json --evidence-root .parallel/website-qa --coverage-scope strict --output .parallel/website-qa/report.strict.json
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.automatic-page.json --evidence-root .parallel/website-qa --coverage-scope automatic-page --output .parallel/website-qa/report.automatic-page.json
```

评分 CLI 新增可选 `--output <path>`，指定时以独占创建方式写入新报告，已存在的路径必须拒绝；省略时保留原 stdout 输出。配对报告使用 `--output` 并选择未使用的新名字，不用 PowerShell 重定向覆盖旧证据。两份报告并列审核；automatic-page 的完成数不能记作 strict 完成数。退出码与 `achieved` 含义不变，输出文件存在不表示质量目标已达到。

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
| 模型与结束状态 | 正例 `liveTranslation=true`，顶层 `requestCount` 为正整数且等于全部 terminal 的 requestCount 之和。流关闭、accepted、部分 updates、缓存文字或 `completed=true` 本身都不能代替终止事件。strict 保留原负例本地规则跳过约定，允许 `hostEvents=[]` 与 `requestCount=0`，不要求云调用；automatic-page policy skip 必须有 live capabilities 和至少一个身份/计数有效的成功零模型调用 terminal，空事件不构成该证明 |
| `retractions` | 必须有观察数组；无事件用 `[]`。事件 `{nodeId, reviewed:true, confirmed:boolean, sourceUnchanged:boolean}`；确认且源文未变时计撤回，译文后来恢复也不能消除失败。源文变化不算撤回；未审核事件不算完成 |
| 完成与错误 | `completed=true`、`errors=[]`、`blockedReason` 缺省/null/空字符串；`synthetic=true`、`mock=true`、非 live capabilities 明确排除 |

所有节点记录必须对应独立 sourceNodes；重复 id 或凭空出现的预测/标签不接受。页面仍需逐节点审核；只查标题、只查翻译结果截图、只使用模型选择的节点，都不能产生合格记录。负例仍需源节点、独立标签和稳定截图，且必须零不必要翻译请求。

`normalize.py` 中的 `requestIds` 是原始 native candidate 身份，不是云模型调用次数。负例可以有 native assessment；所有请求必须与原始 terminal 身份一一对应且各自唯一，每个 terminal 都须成功、`failedCount=0`、`cacheHits` 和 `requestCount` 为明确的非负整数。主机允许并发请求，终态完成顺序可以不同于请求接纳顺序；保留原始顺序是保留原始事件序列和每个请求的 packet/terminal 身份，不要求全局终态顺序等于 candidate 顺序，也不重排终态或 provenance。模型调用次数只取原始 terminal 的 `requestCount` 总和；automatic-page 必须另有 live capabilities 与至少一个上述成功零调用 terminal，不能把空 terminal 列表的空和当作 policy skip 证明。缺失、额外、重复、身份不符、字段损坏或失败的 terminal 仍阻止完成，不补成成功或零调用。错误、未解决工作、未知节点、必须覆盖的遗漏与撤回继续阻止完成，原始捕获、审核、哈希和生产预测保留。正例仍必须有真实模型调用。

native assessment 的归一化往返回归使用离线合成夹具，核对负例零调用、实际调用、缺失/无效/失败终止与失败证据保留，以及正负例并发 `r1/r2` 请求按 `r2/r1` 完成时的身份、计数和原始序列保留；通过不代表任何真实网站已完成或质量目标已达到。

人类应先基于原文、角色、位置和源截图标注，再与预测匹配。`independent=true` 是审核声明，不是自动产生真值的开关。offline 程序无法验证声明是否诚实、截图是否真实采集或模型是否确实被调用，主控必须审查原始运行证据；本脚本不会把这种本地一致性检查声称为密码学认证。

## 动态与历史证据审核

1. 在逐节点查看生产预测前冻结独立页面标签和全部 sourceNodes 历史源标签，保留证据版本；未知仍为 unknown。新源版本按独立原文重新审核，不能从 eligible/completed、初始清单标签或截图投票复制真值。holdout 标签不用于修复调参。
2. 核对每张截图的时间、原始哈希与实际源观察，审核全部唯一 phase。除基本图、details 和启用时的 before-full/after-full/stable-full 外，每次 stable-refresh-N/stable-full-refresh-N 也分别审核，完整页逐区域检查。`normalize.py` 保留这些 phase 与原始 provenance，不用最后刷新图的票替代前面的票。
3. `finalScreenshotProof.screenshotsCurrent` 是采集端关于时间戳证据与真实最终 idle 的关联证明，不是网站静止或布局正确的声明。独立源文版本/可见性变化可以解释图与最终采样的差异；源文未变的显示/生产预测变化仍需原有限次刷新，持续不匹配、失败、撤回或期限耗尽继续阻止完成。此字段不能替代逐张审核、终止事件或质量评分。
4. `predictions` 是当前生产块，`predictionHistory` 与 sourceObservations 是历史生产状态，`currentlyRendered=false` 是当前不呈现。完整的历史源观察可证明当时的生产选择/分片状态，不能充当 needTranslation 真值。隐藏后当前没有 block 或 eligible=false 不能证明该源历史上被拒绝；若源只在 mutation 中短暂可见、控制端没取得当时的生产观察，应保留 unknown。负观察必须有当时真实 idle、完整独立源成员和可信生产标志；缺失、未完成、相互矛盾的证据不能补成正确拒绝。
5. 把冻结的源标签与实际分片成员逐项匹配，再审核实际完成、错误和撤回。atomic block 的 sourceIds 来自生产成员记录，每个非空成员都要有独立源证据；当前隐藏/空白不能删除成员。旧完成状态不能代替新的 pending；后来缓存译文恢复也不能消除已确认的撤回。

本轮公开诊断显示引用链接之间的独立横向分隔符变化及相同上下文刷新可伴随已完成内容恢复原文；另有短暂显示的机构名称只留下源历史而缺少当时生产观察。它们是需保留的缺陷/未知证据，不能仅凭“动态”或文档更新判定修复成功。细节与保护条件见 [回归约束](REFLOW-REGRESSIONS-20261013.md)。原始失败、所有刷新图、真实 offsets/hashes、日期/计数事实、请求身份、EOF/错误/终止与期限门禁均须保留。

覆盖契约还记录 main `3c3ec34` 的新 Wiki 开发捕获因 `producer_observation_overflow` 失败。历史生产观察保留仍受原 **2048 行、2,000,000 字符**边界约束；优化传输/保留不能提高上限、隐藏溢出、删除源标签或把丢失历史补成 eligible=false/completed=true。源版本、几何、atomic 成员归属、真实扫描 idle、带时间戳观察与过期分片 unknown 保护均须保留。离线大页回归要求 >1800 个独立源在原边界内，且移动/atomic 成员、真实丢失、伪造/损坏/过期证据仍被正确阻止；通过这种回归不证明 Wiki 已重新采集成功。

## 统计口径

报告同时包含 `overall / development / holdout`，每案例给出完成状态、排除/未完成原因及错误计数，未提交清单项列入 `missingCases`。案例有两层状态：

- `evidenceAccepted`：清单身份、真实域、访问/元数据、截图审核、live 声明和节点数据结构可信且完整，允许按独立标签计分。真实模型终止失败、未知标签、错误、遗漏和撤回仍会显示，不用排除失败案例来提高分数。
- `completed`：上述证据齐全，加上运行真正完成、所有节点有有效独立标签/预测、正例真实模型请求与成功终止、无遗漏/撤回/不必要请求、所有检查通过。完成域名与证据域名分别报告，访问成功不等于翻译完成。页面决策错误在 page accuracy 中计错；一次已审核运行可以结束而决策错误。

假模型、越域、缺截图等案例不贡献 TP/TN 或完成数，原因明确列出。未知标签不进入正确数或混淆矩阵，单独报告并阻止通过；缺失/损坏预测不能产生正确节点，已确认需要翻译却没有有效完成的节点仍记遗漏（缺失预测为 FN）。重复域名尝试全部排除。无节点候选证据的案例保守视为未完成。

节点以独立人工 `needTranslation` 为真值、生产 `eligible` 为预测：

- TP：需要翻译且 eligible；TN：不需要且不 eligible；FP：不需要却 eligible；FN：需要但未 eligible。
- accuracy = (TP+TN)/(TP+TN+FP+FN)，precision = TP/(TP+FP)，recall = TP/(TP+FN)。
- `translationCoverage` = 真实完成的已标注正节点 / 全部已标注正节点。即使 eligible 识别正确，缺少完整翻译仍是未翻译正节点；strict 及 automatic-page 中必须覆盖的节点仍计 `confirmedOmissions`。识别准确率不能替代翻译覆盖。
- 页面 accuracy = 实际 boolean `pageDecision` 与独立页面标签一致的次数 / 有独立页面真值和有效截图证据的页面数。
- `completionRate` = 当前声明口径下完成域数 / 固定清单域数；默认 strict 保留原严格完成含义。未知标签、遗漏、全部未翻译正节点、policy skip、撤回和不必要请求同时列出。

零分母指标输出 null，绝不输出 100%。正节点预测为 eligible=false 时仍是 FN；eligible=true 但未翻译可以是 TP，却不产生翻译完成信用。strict 下所有未完成正节点仍计遗漏；automatic-page 仅按上述证据条件调整必须覆盖的子集，不能用全跳过制造正确识别。95% 与 97% 门槛使用整数分数精确比较，避免浮点边界漂移。

## 可审计门禁

`achieved=true` 必须同时满足：

1. 固定清单至少 120 独立域、35 负例、30 holdout；当前报告口径下完成至少 100 个独立域，strict 仍要求原严格完成。
2. 整体节点 accuracy、precision 各 >=95%，页面 accuracy >=97%。
3. holdout 至少 30 个当前报告口径下完成域（strict 保持严格完成），且独立满足相同质量门槛；整体及 holdout 都必须完成正、负两类页面。
4. 整体及 holdout 没有确认遗漏、确认撤回、不必要翻译请求、未知标签或无效预测。

holdout 覆盖和质量门禁是额外的保守约束，防止 100 个开发站遮蔽未测留出集。召回、翻译覆盖和逐站遗漏仍独立呈现。清单中剩余未访问/排除项不算完成，允许主控保留阻塞并使用固定替补，不自动删掉失败证据。

新增口径不调整上述 95/97、域数、holdout、未知/无效预测、错误、撤回及请求门禁；默认 strict 的全部原测试保持不变。文档任务的离线验收运行两个完整 QA 套件：

```powershell
pwsh -NoProfile -File eng/parallel/Test-OwnedFiles.ps1 -PlanPath .parallel/plan.json -TaskId coverage-docs
pwsh -NoProfile -File .parallel/Validate-CoverageEvidenceTask.ps1 -TaskId coverage-docs
python -m unittest discover -s eng/browser-use-qa/tests -p 'test_*.py'
python -m unittest discover -s eng/website-qa -p 'test_*.py'
```

开发 checkout 只读固定基线，依赖是审查门禁，不共享其他任务代码。此处的套件通过不能代替主控集成后的新接口测试、完整仓库门禁或真实网页审核。

### 离线文档链接与配对命令检查

以下检查只读取三个文档及本 checkout 的链接目标；不执行示例中的归一化、评分或真实采集命令，不生成证据文件。它核对本地链接、版本化模式 JSON，以及四条配对命令的模式、输出唯一性和输入保护。新 CLI 的行为仍须由集成后的实现套件验证。

```powershell
@'
import json
from pathlib import Path
import re
import shlex

paths = [Path("eng/website-qa/README.md"),
         Path("eng/browser-use-qa/README.md"),
         Path("docs/WEBSITE-QA-EVIDENCE.md")]
texts = [path.read_text(encoding="utf-8") for path in paths]
links = 0
for path, text in zip(paths, texts):
    prose = re.sub(r"```.*?```", "", text, flags=re.S)
    for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", prose):
        if "://" not in target and not target.startswith("#"):
            assert (path.parent / target.split("#", 1)[0]).is_file(), (path, target)
            links += 1
    metadata = [json.loads(value) for value in re.findall(
        r'\{"schemaVersion":1,"mode":"(?:strict|automatic-page)"\}', prose)]
    assert {value["mode"] for value in metadata} == {"strict", "automatic-page"}, path

commands = re.findall(r"^python eng/website-qa/(?:normalize|evaluate)\.py .*--coverage-scope .+$",
                      texts[0], flags=re.M)
assert len(commands) == 4
outputs, inputs, pairs = set(), set(), set()
for command in commands:
    tokens = shlex.split(command)
    assert len(tokens[2:]) % 2 == 0 and not any(token.startswith(">") for token in tokens)
    options = dict(zip(tokens[2::2], tokens[3::2]))
    mode, output = options["--coverage-scope"], options["--output"]
    assert mode in {"strict", "automatic-page"}
    assert output.endswith("." + mode + ".json") and output not in outputs
    outputs.add(output)
    inputs.update(value for key, value in options.items() if key in {"--case", "--review", "--cases"})
    pairs.add((Path(tokens[1]).stem, mode))
assert pairs == {(tool, mode) for tool in ("normalize", "evaluate")
                 for mode in ("strict", "automatic-page")}
assert outputs.isdisjoint(inputs)
print(f"Checked {links} local links, versioned modes and {len(commands)} paired commands")
'@ | python -
```

`test_evaluate.py` 仅使用保留 `.test` 域、合成节点和 1px 图片测试门禁逻辑（包括 97% 边界、重复子域、假模型、未知标签、全跳过、缺截图、不必要请求与撤回）。合成 fixtures 的评分通过只验证实现，**不代表真实质量结果**。测试临时文件位于本 checkout 的忽略目录 `.parallel/`；运行日志由主控或调用者保留在忽略目录。
