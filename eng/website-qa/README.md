# 真实网站 QA：固定清单与离线评分

本目录实现 [公共契约](CONTRACT.md) 的清单与评分基础设施。主控留存的公开开发证据已包含真实站点访问中的重排与隐藏源观察；访问和诊断本身不等于实际完成翻译。本目录文档及合成 fixtures **不能认证百站测试或准确率目标已达到**。统一口径见 [网页 QA 证据指南](../../docs/WEBSITE-QA-EVIDENCE.md)、[本轮回归约束](REFLOW-REGRESSIONS-20261013.md) 与 [真实零调用 assessment / 引用版本契约](ASSESSMENT-PROOF-20261013.md)。以下开发事实引用公开契约，不链接本机或私有运行材料。

## 覆盖口径接口 v1

[覆盖与有界证据契约](COVERAGE-EVIDENCE-20261013.md) 发布两种明确口径，固定基线已有以下参数及 [assessment 契约](ASSESSMENT-PROOF-20261013.md) 的 automatic-page 证据识别。本轮将独立 normalizer、evaluator、各自的新回归和文档移植到已审查采集性能与源 epoch 的固定基线；主控仍须审查差异并在实际 integration HEAD 重跑完整验收。

- `normalize_case`、`evaluate`、`_score_case` 增加关键字 `coverage_scope="strict"`，保留现有位置参数调用。CLI 增加 `--coverage-scope strict|automatic-page`，非法模式必须拒绝。
- 归一化案例与评分报告记录 `coverageScope={"schemaVersion":1,"mode":"strict"}` 或 `{"schemaVersion":1,"mode":"automatic-page"}`。覆盖口径版本与原有案例 envelope 的 `schemaVersion` 分别保留。
- 默认 `strict` 保持原测试与覆盖义务。无 coverageScope 的旧案例只允许 strict；strict 拒绝 automatic-page 案例。automatic-page 必须取得版本、模式均匹配的案例元数据，不能静默转换旧案例或 strict 案例。

| 口径 | 正节点覆盖义务 | 其他完成条件 |
| --- | --- | --- |
| `strict`（默认） | 所有独立已知正标签缺少真实完整翻译均保留原遗漏含义 | 原捕获、审核、预测、请求、截图、未知、错误与撤回门禁 |
| `automatic-page`（显式） | 仅在下述正确跳过负页中，经有效生产预测证明的正节点可以列为 policy skip；其他正节点仍须真实完整翻译 | 同样保留所有其他门禁，policy skip 不产生翻译完成或 TP 奖励 |

正确跳过必须同时满足：固定清单 `expectedPageTranslation=false`、独立且已审核的 `pageAnnotation.needTranslation=false`、实际 boolean `pageDecision=false`、有效整数 `requestCount=0`，以及 `type=capabilities, live=true` 和有效主机证明。先核对全部请求、原始事件与计数，再判断覆盖义务。automatic-page 接受以下两种证明：

- 原有成功零调用 terminal：至少一个 `type=terminal, success=true, requestCount=0, failedCount=0, cacheHits>=0`；全部 candidate 请求与 terminal 身份唯一、一一对应，terminal 计数总和等于顶层计数。
- 实际无翻译请求的主机 assessment：原始 `localAssessment` 是 `type=assessment, schemaVersion=1, live=true, needsTranslation=false`，有非空 `id` 与匹配本案例的 `caseId`；`requestIds` 和 terminals 必须都为有效的空列表。归一化将该原始事件保留到 `hostEvents`，不替换或合成 terminal。主机 `assess` 动作本来就以 assessment 结束；没有模型工作时没有翻译 terminal。

每个事件的身份必须有效，版本与计数必须是明确整数，不能是 null、boolean 或字符串。assessment 是单独验证的已有生产事件类型，其他未知事件类型仍无效。空列表本身、只有 capabilities、缺少能力证明，以及非 live、正向、unknown、版本/身份无效的 assessment 均不能证明 policy skip。任何已发请求仍须成功 terminal；assessment 不能弥补缺失/额外/重复/失败 terminal，非零或损坏计数也不能补成零。非零调用、标签分歧、正页误跳过均不能取得 policy skip。缺失、失败、矛盾或 unknown 的节点预测不能取得豁免；原始错误与未知仍阻止完成。初始清单标签或预测不能代替独立页面/节点标签。默认 strict 的原规则和所有旧测试保持不变；automatic-page 元数据仍是显式 v1，识别已有 assessment 事件无需改变版本。

[公开 assessment 契约](ASSESSMENT-PROOF-20261013.md) 记载 `3c3ec34` 的真实 GMW 开发捕获：主机匹配的 live negative assessment，空 requestIds/terminals，零模型工作。此前 terminal-only 要求错误拒绝了这种证据。此修订只纠正证据类型，不解除 GMW 历史 unknown，也不宣称该页已完成。

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
# 真实采集和独立审核由主控完成；证据放在忽略目录，输出选择未使用的新路径。
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.json --evidence-root .parallel/website-qa --output .parallel/website-qa/report.legacy.json
python -m unittest discover -s eng/website-qa -p 'test_*.py'
```

默认清单为同目录 `sites.json`，可用 `--sites` 指定明确版本。案例输入为 JSON 数组，或 `{"schemaVersion":1,"cases":[...]}`。退出码：0 表示 `achieved=true`；1 表示已成功评分但门槛未满足；2 表示输入/文件错误。报告始终明确 `achieved`，不会把空数据或基础设施验收成功当作真实目标完成。

以下是现有版本化覆盖接口的 legacy 离线 CLI 用法，省略视觉审核开关。同一原始捕获及冻结审核分别生成新文件，不覆盖原 case、review、截图或旧报告。两份 `cases.*.json` 由对应口径的归一化案例组成数组或上述 envelope，不能将单个案例对象当作数组，也不能混用口径。

```powershell
python eng/website-qa/normalize.py --case .parallel/website-qa/case.json --review .parallel/website-qa/review.json --evidence-root .parallel/website-qa --coverage-scope strict --output .parallel/website-qa/normalized.strict.json
python eng/website-qa/normalize.py --case .parallel/website-qa/case.json --review .parallel/website-qa/review.json --evidence-root .parallel/website-qa --coverage-scope automatic-page --output .parallel/website-qa/normalized.automatic-page.json
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.strict.json --evidence-root .parallel/website-qa --coverage-scope strict --output .parallel/website-qa/report.strict.json
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.automatic-page.json --evidence-root .parallel/website-qa --coverage-scope automatic-page --output .parallel/website-qa/report.automatic-page.json
```

评分 CLI 新增可选 `--output <path>`，指定时以独占创建方式写入新报告，已存在的路径必须拒绝；省略时保留原 stdout 输出。配对报告使用 `--output` 并选择未使用的新名字，不用 PowerShell 重定向覆盖旧证据。两份报告并列审核；automatic-page 的完成数不能记作 strict 完成数。退出码与 `achieved` 含义不变，输出文件存在不表示质量目标已达到。

## 独立视觉结果接口 v1（显式启用）

[独立截图结果公共契约](VISUAL-PROOF-20261014.md) 规定 `normalize_case`、`evaluate` 和 `_score_case` 的关键字 `require_visual_review=False`，以及两者 CLI 的 `--require-visual-review`。有效 false 节点的不必要视觉翻译保留为测得的错误，不另加零假阳性要求。每个可见结果，包括 `unchanged`，都须有下述明确输出 phase。默认 False 保留所有旧调用、测试与报告行为，属于 legacy 模式：生产 `completed`、成功 terminal 和截图通过票仍不能独立证明每个源的文字确实正确显示为译文。即使 legacy 报告给出 `achieved=true`，也不足以验证包含实际截图确认的完整目标。

**完整目标验证必须在归一化和评分两步都显式传入 `--require-visual-review`**，逐案例要求独立视觉结果，并在报告记录 `visualCoverage={"schemaVersion":1,"required":true}`。该版本独立于 `coverageScope` 和案例 envelope；视觉审核要求可与 strict 或 automatic-page 配对，不能用覆盖口径开关代替。保持 fixed50、原识别公式与全部质量门槛。

独立审核文件中的新增 `visualReview` 形状如下。示例只展示字段，哈希占位说明须替换为实际值，不能作为真实审核或可评分 fixture：

```json
{
  "visualReview": {
    "schemaVersion": 1,
    "caseSha256": "原始捕获文件字节的 SHA256",
    "sourceReviewSha256": "冻结源审核的 SHA256，与外层同名字段一致",
    "reviewed": true,
    "independent": true,
    "nodes": [
      {
        "sourceId": "source-1",
        "sourceTextSha256": "该源精确原文 UTF-8 字节的 SHA256",
        "outcome": "correct_translation",
        "reviewed": true,
        "independent": true,
        "evidence": [{"phase": "stable-full", "note": "实际查看的文字、位置、事实及几何情况"}]
      }
    ]
  }
}
```

| 校验对象 | 必须满足 |
| --- | --- |
| 类型与版本 | `visualReview` 是对象，`schemaVersion` 是整数 1；review 和每个节点的 reviewed/independent 均为明确 boolean true，不接受字符串、数字或以 boolean 冒充版本整数；nodes 是列表，outcome 是下表枚举之一 |
| 捕获身份 | `caseSha256` 绑定未修改的原始文件字节；normalizer 核对实际原始文件，evaluator 核对保留的 provenance 哈希，不通过重写捕获获取新身份 |
| 源审核身份 | `sourceReviewSha256` 是非空字符串，恰含 64 个十六进制字符（`0-9a-fA-F`），与外层冻结的原值精确一致；合法的大写或混合大小写原值须在 normalizer/evaluator 两端一致接受，不转换大小写、trim 或重算替换；不把新增视觉文件的哈希冒充原源审核哈希 |
| 源身份与原文 | 每个且仅每个原始 sourceId 有一个唯一对象结果行，sourceId 是非空字符串；`sourceTextSha256` 核对精确原文 UTF-8 字节，不 trim、折叠空白、改事实、使用译文或当前源替代历史原文；全部哈希须为 64 位十六进制字符串 |
| 可见证据 | evidence 是非空列表；每项 phase 属于实际捕获且已经审核的原始截图，note 是非空字符串并描述实际检查；每个可见 outcome（包括 unchanged）至少引用一个下列明确输出 phase |

输出 phase 精确为 `after`、`after-full`、`stable`、`stable-full`、`scroll-after`、`return-after`、`reflow-after`、`disclosure-open`、`disclosure-restored`，以及 `stable-refresh-N` / `stable-full-refresh-N`（N 为无前导零的非零十进制整数，匹配 `[1-9][0-9]*`）。`before`、`before-full`、`scroll-before` 单独或组合都不能证明输出；任意自定义非 before phase、`after-refresh-1`、`stable-refresh-0`、`stable-full-refresh-01` 或额外后缀也不接受。该规则适用于 correct/partial/incorrect/unnecessary_translation 和 unchanged，包括负页 policy skip。`unknown` / `not_visible` 可以记录输入图上的无法确认，但仍未解决、阻止完成，不取得跳过信用。

两端均须独立检查完整形状、类型、身份、哈希、phase 和说明，不能由 normalizer 的摘要直接赋予 evaluator 完成信用；直接输入归一化案例评分也受相同要求。缺少 visualReview/结果行、重复或额外源、错误哈希、无效类型、虚构 phase 等均拒绝完整证明，不能推断投票或补写成功。源标签、历史 sourceNodes、原始生产 completed/failed 与请求证据留在 provenance，视觉审核不能覆写它们。

| 冻结标签 / outcome | required 模式中的含义 |
| --- | --- |
| true / `correct_translation` | 仅与原有实际 live 成功 terminal、有效生产预测和对应源完整完成证据同时成立时，才计入 translatedPositiveNodes；审核票不能创造模型工作 |
| true / `partial_translation` 或 `incorrect_translation` | 部分英文残留、错误译文、日期/计数等事实改变均阻止完成，不取得翻译信用 |
| true / `unchanged` | strict 中仍是必须翻译的遗漏；automatic-page 仅在上述全部已证明负页零调用策略及有效预测满足时计 policySkippedPositiveNodes，仍属于未翻译正节点 |
| false / `unchanged` | 已可读中文/繁中、无需翻译的名称或 metadata 正确保留原文；无需人为翻译这些节点取得视觉成功，负页仍须零不必要请求 |
| false / `unnecessary_translation` | 有效且独立确认的不必要输出递增 unnecessaryVisualTranslations，不单独使案例 incomplete；不重写 eligible 或识别混淆矩阵，由原 95% accuracy/precision 门槛衡量识别错误 |
| 任意标签 / `not_visible` 或 `unknown` | 可见性或结果未解决，继续阻止完成；隐藏不消除源或遗漏，不自动变成正确未翻译 |
| unknown 源标签 | 无论视觉票如何，标签仍是 unknown，原未知门禁继续失败；与已知标签矛盾的 outcome 组合无效 |

识别 TP/TN/FP/FN 仍仅由冻结 needTranslation 与真实 eligible 决定，节点 accuracy=(TP+TN)/(TP+TN+FP+FN)、precision=TP/(TP+FP)、recall=TP/(TP+FN)，页面 accuracy 仍为正确页面决策/有独立真值的有效页面。视觉错误不能改标签或识别预测来美化这些比例。

required 模式才将视觉确认加入原 `translatedPositiveNodes`、`unTranslatedPositiveNodes`、`policySkippedPositiveNodes`、`confirmedOmissions` 的证据计算；`annotatedPositiveNodes` 分母及 translationCoverage 原公式不变，policy skip 不增加翻译数。案例和 overall/development/holdout 聚合均须从证据计算并呈现以下视觉计数：

| 视觉计数 | 门禁含义 |
| --- | --- |
| `unknownVisualNodes` | 未解决视觉结果；须为零，unknown/not_visible、缺失或无效证明仍阻止完成 |
| `incorrectVisualTranslations` | 必须翻译的部分或错误输出；须为零，事实错误不能取得完整翻译信用 |
| `unnecessaryVisualTranslations` | false 节点上有效的不必要输出；如实递增，不要求为零，不新增 `noUnnecessaryVisualTranslations` 门禁 |

完整报告仍须必须翻译的节点有已知正确视觉结果、合法 unchanged/policy skip 有完整证明，以及全部旧门禁通过。机器成功却必须翻译的文字仍为原文、未知/缺失视觉结果、部分翻译或事实错误均阻止完成。有效 false / unnecessary_translation 本身不使案例 incomplete，也不抹去其他节点的正确翻译；实际无效 native/source 证据、矛盾标签/outcome、错误、缺请求/终止、源历史 unknown、遗漏或已确认撤回仍阻止完成。原 `unnecessaryRequests` / `noUnnecessaryRequests` 仅按原负页请求规则计算，和逐节点视觉计数是不同字段；修订不解除负页零调用要求，也不把正页上的每个 false 节点输出当成负页请求错误。后来恢复不能擦除历史失败。

### 原始 phase 与可读裁剪票

原始 review（包括 notes 与 crop votes）、捕获、截图哈希和旧公开失败保持不可变。审核者须实际打开并检查图片，先核对完整长页各区域，再用可读裁剪检查具体源的文字和事实；1px 合成图片或机器摘要不能充当真实视觉检查。原 `review.screenshots` 仍是所有且仅所有实际捕获 phase 的 boolean 投票映射，完整页、details 与每次刷新图均分别审核。裁剪便于阅读，不能冒充新捕获 phase，不能把裁剪键或对象票塞入原 boolean 映射，也不能以最终裁剪通过替代旧失败票。

只有完成实际图片检查后，才允许生成新的补充视觉审核文件，保留冻结源标签与 sourceReviewSha256、原 notes/crop votes，使用未占用的新路径。主控负责将裁剪票单独打包，并将 visualReview.evidence.phase 关联到已经审核的原图 phase；不放宽截图 schema、不改旧 review、不合并刷新 phase。本 worker 不进行真实图片审核，也不补造视觉票。

### 完整视觉验证的配对命令（集成后）

以下假设主控已通过图片检查生成新的 `review.visual.json`，其中保留冻结标签与原 phase 投票并添加 visualReview。每个 `cases.visual.*.json` 是对应新归一化案例组成的数组或 envelope，不能将单个案例对象直接评分。两个工具都使用显式开关；每种口径输出到新的独占文件，保留所有旧报告。

```powershell
python eng/website-qa/normalize.py --case .parallel/website-qa/case.json --review .parallel/website-qa/review.visual.json --evidence-root .parallel/website-qa --require-visual-review --coverage-scope strict --output .parallel/website-qa/normalized.visual.strict.json
python eng/website-qa/normalize.py --case .parallel/website-qa/case.json --review .parallel/website-qa/review.visual.json --evidence-root .parallel/website-qa --require-visual-review --coverage-scope automatic-page --output .parallel/website-qa/normalized.visual.automatic-page.json
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.visual.strict.json --evidence-root .parallel/website-qa --require-visual-review --coverage-scope strict --output .parallel/website-qa/report.visual.strict.json
python eng/website-qa/evaluate.py --cases .parallel/website-qa/cases.visual.automatic-page.json --evidence-root .parallel/website-qa --require-visual-review --coverage-scope automatic-page --output .parallel/website-qa/report.visual.automatic-page.json
```

这些是主控最终 required-mode 验证的命令示例，本任务不执行真实捕获或视觉审核。独立 normalizer/evaluator 回归及组合 roundtrip 核对机器成功但文字未译、部分/事实错误、未知可见性、缺失/重复/损坏票、哈希/源身份（包括精确绑定的大写冻结源审核哈希）、输出 phase、正确 true、unchanged false 和合法 policy skip；旧测试不修改。主控须在实际 integration HEAD 运行两套完整 QA 和全仓门禁，再用固定真实清单、独立冻结标签及实际图片检查执行上述 required-mode 最终验证。离线通过只验证接口与失败保护，不证明至少 100 域完成、95% 节点 accuracy/precision 或 97% 页面 accuracy 已达到。

不必要输出回归独立构造多种 false 源（如已可读中文、无需翻译的名称、metadata），配有原有合法 live native 工作、成功 terminal、完整源身份及全部正确翻译的 true 节点。断言不必要计数递增、TP/TN/FP/FN 与原评分一致、有效案例不因该视觉结果单独 incomplete，并保留无效 native/source、矛盾 outcome 与负页请求失败对照。合成算术示例：19 TP、1 FP、0 TN、0 FN 时 accuracy 与 precision 恰为 95%，20 个节点中那一个 false 源的不必要输出不应新增零门禁；18 TP、2 FP 时两项为 90%，仍被原门槛拒绝。原有不少于 100 域、97% 页面、holdout、遗漏/撤回等门禁须分别满足，单个示例不能宣称 achieved。先前两项独立实现的 fixtures 原样保留，再新增组合回归。

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
| 模型与结束状态 | 正例 `liveTranslation=true`，顶层 `requestCount` 为正整数且等于全部 terminal 的 requestCount 之和。流关闭、accepted、部分 updates、缓存文字或 `completed=true` 本身都不能代替终止事件。strict 保留原负例本地规则跳过约定，允许 `hostEvents=[]` 与 `requestCount=0`，不要求云调用；automatic-page 必须有 live capabilities，加上述成功零调用 terminal 或真实匹配的 live negative assessment（仅有效空 requestIds/terminals）；空事件不构成证明 |
| `retractions` | 必须有观察数组；无事件用 `[]`。事件 `{nodeId, reviewed:true, confirmed:boolean, sourceUnchanged:boolean}`；确认且源文未变时计撤回，译文后来恢复也不能消除失败。源文变化不算撤回；未审核事件不算完成 |
| 完成与错误 | `completed=true`、`errors=[]`、`blockedReason` 缺省/null/空字符串；`synthetic=true`、`mock=true`、非 live capabilities 明确排除 |

所有节点记录必须对应独立 sourceNodes；重复 id 或凭空出现的预测/标签不接受。页面仍需逐节点审核；只查标题、只查翻译结果截图、只使用模型选择的节点，都不能产生合格记录。负例仍需源节点、独立标签和稳定截图，且必须零不必要翻译请求。

`normalize.py` 中的 `requestIds` 是原始 native candidate 身份，不是云模型调用次数。所有已发请求必须与原始 terminal 身份一一对应且各自唯一，每个 terminal 都须成功、`failedCount=0`、`cacheHits` 和 `requestCount` 为明确的非负整数。主机允许并发请求，终态完成顺序可以不同于请求接纳顺序；保留原始顺序是保留原始事件序列和每个请求的 packet/terminal 身份，不要求全局终态顺序等于 candidate 顺序，也不重排终态或 provenance。模型调用次数只取原始 terminal 的 `requestCount` 总和；没有 candidate 的 assess-only 路径还须上述真实负向 assessment 才能证明零调用，不能只取空列表的空和。缺失或损坏的 requestIds 列表不等于空列表。缺失、额外、重复、身份不符、字段损坏或失败的 terminal 仍阻止完成，不补成成功或零调用。错误、未解决工作、未知节点、必须覆盖的遗漏与撤回继续阻止完成，原始捕获、审核、哈希和生产预测保留。正例仍必须有真实模型调用。

assessment 扩展的合成回归须包含真实形状的 fixture assessment 证据，并拒绝无效版本/身份、非 live/正向/unknown assessment、损坏或缺失请求列表、非空请求缺终止、失败或非零 terminal 及标签矛盾。旧的缺 capabilities、只有 capabilities、已发工作缺 terminal 的测试原样保留；并发 `r1/r2` 请求按 `r2/r1` 完成时的身份、计数和原始序列仍保留。离线通过不代表任何真实网站已完成或质量目标已达到。

人类应先基于原文、角色、位置和源截图标注，再与预测匹配。`independent=true` 是审核声明，不是自动产生真值的开关。offline 程序无法验证声明是否诚实、截图是否真实采集或模型是否确实被调用，主控必须审查原始运行证据；本脚本不会把这种本地一致性检查声称为密码学认证。

## 动态与历史证据审核

1. 在逐节点查看生产预测前冻结独立页面标签和全部 sourceNodes 历史源标签，保留证据版本；未知仍为 unknown。新源版本按独立原文重新审核，不能从 eligible/completed、初始清单标签或截图投票复制真值。holdout 标签不用于修复调参。
2. 核对每张截图的时间、原始哈希与实际源观察，审核全部唯一 phase。除基本图、details 和启用时的 before-full/after-full/stable-full 外，每次 stable-refresh-N/stable-full-refresh-N 也分别审核，完整页逐区域检查。`normalize.py` 保留这些 phase 与原始 provenance，不用最后刷新图的票替代前面的票。
3. `finalScreenshotProof.screenshotsCurrent` 是采集端关于时间戳证据与真实最终 idle 的关联证明，不是网站静止或布局正确的声明。独立源文版本/可见性变化可以解释图与最终采样的差异；源文未变的显示/生产预测变化仍需原有限次刷新，持续不匹配、失败、撤回或期限耗尽继续阻止完成。此字段不能替代逐张审核、终止事件或质量评分。
4. `predictions` 是当前生产块，`predictionHistory` 与 sourceObservations 是历史生产状态，`currentlyRendered=false` 是当前不呈现。完整的历史源观察可证明当时的生产选择/分片状态，不能充当 needTranslation 真值。隐藏后当前没有 block 或 eligible=false 不能证明该源历史上被拒绝；若源只在 mutation 中短暂可见、控制端没取得当时的生产观察，应保留 unknown。负观察必须有当时真实 idle、完整独立源成员和可信生产标志；缺失、未完成、相互矛盾的证据不能补成正确拒绝。
5. 把冻结的源标签与实际分片成员逐项匹配，再审核实际完成、错误和撤回。atomic block 的 sourceIds 来自生产成员记录，每个非空成员都要有独立源证据；当前隐藏/空白不能删除成员。旧完成状态不能代替新的 pending；后来缓存译文恢复也不能消除已确认的撤回。

本轮公开诊断显示引用链接之间的独立横向分隔符变化及相同上下文刷新可伴随已完成内容恢复原文；另有短暂显示的机构名称只留下源历史而缺少当时生产观察。它们是需保留的缺陷/未知证据，不能仅凭“动态”或文档更新判定修复成功。细节与保护条件见 [回归约束](REFLOW-REGRESSIONS-20261013.md)。原始失败、所有刷新图、真实 offsets/hashes、日期/计数事实、请求身份、EOF/错误/终止与期限门禁均须保留。

[公开契约](ASSESSMENT-PROOF-20261013.md) 保留两次 Wiki 开发失败：`3c3ec34` 的 `producer_observation_overflow`；`8517302` 的 site-021 已通过 producer 值驻留避免前一溢出，但在 **98 次实际调用、四张图片**后因 `independent_source_observation_overflow` 失败。中断前有 1844 个源行、零观察到的撤回；这些事实不能记作 completed，四张图片也不能代替全部截图审核。

引用版本导出（ref-only）仍受原 **2048 行、2,000,000 字符**边界约束。只有为满足原边界而必要时，才精简冗余且未被引用的导出版本；必须保留 retained producer views 引用的全部精确 source 版本与 represented-owner 版本，以及真实成员归属、几何、时间和 scan idle。完整 `sourceNodes` 历史不变，小页/旧 fixtures 的导出行为不变，原 fixture1901 版本数仍须通过。必要见证集合本身超过任一原上限时仍明确溢出失败，不能丢掉所需版本、合并不同版本、由当前位置伪造历史、清零 dropped 或借用后来未取得的完成。伪造/损坏/过期分片和未知丢失保护保留；负 eligibility 不能由缺失证据推断。离线大页移动/ref-only、atomic 归属及真正必要见证溢出回归通过，也不证明 Wiki 已重新采集成功。

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

新增口径不调整上述 95/97、域数、holdout、未知/无效预测、错误、撤回及请求门禁；默认 strict 的全部原测试保持不变。本轮 visual-proof 的验收核对归属，并运行两个完整 QA 套件及完整扩展回归：

```powershell
pwsh -NoProfile -File eng/parallel/Test-OwnedFiles.ps1 -PlanPath .parallel/plan.json -TaskId visual-proof
pwsh -NoProfile -File .parallel/Validate-QualityRecoveryTask.ps1 -TaskId visual-proof
python -m unittest discover -s eng/browser-use-qa/tests -p 'test_*.py'
python -m unittest discover -s eng/website-qa -p 'test_*.py'
```

开发 checkout 只读固定基线，依赖是审查门禁，不共享其他任务代码。此处的套件通过不能代替主控集成后的新接口测试、完整仓库门禁或真实网页审核。

### 离线文档链接与配对命令检查

以下检查只读取三个文档及本 checkout 的链接目标；不执行示例中的归一化、评分或真实采集命令，不生成证据文件。它核对本地链接、版本化模式 JSON，以及四条 legacy 与四条 required-visual 配对命令的模式、显式开关、输出唯一性和输入保护。新视觉接口的 CLI 行为仍须由集成后的实现套件验证。

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
assert len(commands) == 8
outputs, inputs, pairs = set(), set(), set()
for command in commands:
    tokens = shlex.split(command)
    visual = "--require-visual-review" in tokens
    assert tokens.count("--require-visual-review") == int(visual)
    tokens = [token for token in tokens if token != "--require-visual-review"]
    assert len(tokens[2:]) % 2 == 0 and not any(token.startswith(">") for token in tokens)
    options = dict(zip(tokens[2::2], tokens[3::2]))
    mode, output = options["--coverage-scope"], options["--output"]
    assert mode in {"strict", "automatic-page"}
    assert output.endswith("." + mode + ".json") and output not in outputs
    assert (".visual." in output) == visual
    outputs.add(output)
    inputs.update(value for key, value in options.items() if key in {"--case", "--review", "--cases"})
    pairs.add((Path(tokens[1]).stem, mode, visual))
    if visual:
        input_key = "--review" if Path(tokens[1]).stem == "normalize" else "--cases"
        assert ".visual." in options[input_key]
assert pairs == {(tool, mode, visual) for tool in ("normalize", "evaluate")
                 for mode in ("strict", "automatic-page") for visual in (False, True)}
assert outputs.isdisjoint(inputs)
print(f"Checked {links} local links, versioned modes and {len(commands)} paired commands")
'@ | python -
```

`test_evaluate.py` 仅使用保留 `.test` 域、合成节点和 1px 图片测试门禁逻辑（包括 97% 边界、重复子域、假模型、未知标签、全跳过、缺截图、不必要请求与撤回）。合成 fixtures 的评分通过只验证实现，**不代表真实质量结果**。测试临时文件位于本 checkout 的忽略目录 `.parallel/`；运行日志由主控或调用者保留在忽略目录。
