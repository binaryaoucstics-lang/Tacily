# Browser Use CLI 真实网页采集基础设施

此目录实现采集驱动，不包含任何已完成的真实网站结果或准确率结论。开发验收只运行合成 DOM、mock CLI 输出与离线 JSONL 主机；它们不计真实 case。

当前 `predictions` 表示最后采样时仍在生产 session 中的块；全部观察历史保存在
`predictionHistory` 和原始 JSONL。重排后已被新块替代的旧 pending ID 不再冒充当前漏译，
历史中没有独立源证据的选择仍会阻止完成。sourceNodes 保留完整历史，新增
currentlyRendered 表示它在本次真实 DOM 采样中是否仍呈现；审核不能凭预测生成真值。
已跳过的动态页面以页面决策稳定为等待条件，持续变化的源文字与几何仍保留，不声称
网页内容静止。正例仍需内容与完成状态稳定、真实请求及成功终止。原样名称的源前缀
（如 FOI → FOIA）单调补全不算译文撤回；真正不同的译文恢复原文/空白仍记录。

## 边界和入口

- Python 标准库，无新增依赖。离线页面适配器检查使用仓库现有 Node。
- 所有浏览器操作只有一个入口：`C:/Users/cool848457/.codex/skills/browser-use-cli/scripts/run_browser_use.py`。使用命名 session、`--json open/state/eval/screenshot`，命令始终为参数列表、`shell=false`。没有 Playwright、CDP、WebDriver、HTTP 或 MCP 备用通道。
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

### 帧批量传送与期限

转发只合并已经到达的 host 帧，不额外等待凑批。每批最多 32 帧，且将 RPC ID、原始 packet、首次确认标志及同步 JavaScript 执行包装全部 JSON 编码后计算 UTF-16 字节数，完整 eval 表达式不得超过 20000 bytes。同一批保持原事件顺序，各 RPC 仅在首次帧之前调用一次 `acknowledge`，随后每个原始帧调用 `deliver`；跨批和交错 RPC 也不重复确认。执行是同步循环，不把 Promise 或 `{}` 作为传送成功。单个 packet 过大时，通过既有 `inject` 分块存入数据，再用小表达式一次交给 `deliver`；不会截断、重写或重新序列化该原始帧字符串，数据也不会作为 JavaScript 执行。存储的数据在交付表达式的 finally 中清除。

速度改善来自减少包装器/CLI 进程调用：以离线回归中的 102 个较小合成帧为例，旧逻辑需要 204 次确认/交付调用，现在为 4 次批量 eval。真实包大小、到达时序和大包分块会影响实际调用数；这不是实际网站、真实模型或语料验证成功的结论。stdout 原行及每个 `host_event` 仍分别记录、flush/fsync；关联 ID、case ID、packet 错误与 terminal 失败仍核验。遇到错误先交付此前有效的已排队帧，错误 packet 本身也保持原样交付，然后记录失败，不用批量传送掩盖错误或生成完成证据。

每次 forward 最多读取 1000 个事件，并在 drain、outbound 转发、每次 poll 和批量交付前后检查同一 case 期限；持续有帧也不能无限占用循环。Browser Use 子进程 timeout 取 60 秒与 case 剩余时间的较小值，超过期限返回的结果仍判为超时；大包的每次分块 eval 同样受限。assessment 等待也不超过 case 期限。失败证据仍保存，历史 attempt 不修改；期限耗尽后清理调用可能无法执行，其失败会追加为 cleanup 错误。

## 页面证据

每个 attempt 包含 `case.json`、`events.jsonl` 和截图。日志/截图只有外部输出，不提交仓库。

- before：配置翻译前独立遍历实际渲染的文本节点，包含代码、日期、按钮、数字等，不以生产 eligible 过滤。采集标签、角色、Range 几何、文档坐标和首屏标记。开放 Shadow DOM、slot 可读；关闭 Shadow DOM、跨域 iframe 无法读取，人工审核需记录这些遗漏。
- sourceNodes：身份分配与可见证据独立；只有首次确认实际 rendered 的完整行（标签、角色、几何等）才加入历史，不收录 SCRIPT/STYLE/隐藏文本的占位项。已观察的译文消失为空白时保留此前合法位置；已在任一阶段显示过的节点后来隐藏或脱离仍保留最后完整行。保留原文；用 DOM locator、源版本和节点关联分配稳定 ID，相同位置与原文的节点替换可保持 ID。生产 record 的保留原文用于翻译后追踪，不能让译文替代原文。来源改变使用新 ID。ID 只在当前文档 attempt 内稳定，不能声称跨加载稳定。
- predictions：生产 blocks 的实际 eligible、completed、failed、local/fromCache 及 sourceId。生产预测不能创建可见源证据；未独立观察到的源标记 `sourceEvidence=unmatched` 和 `auditIssue=source_not_independently_visible`，不得记为完整待审核证据或完成。后来实际显示后可匹配，其先前 unmatched 状态保留在阶段日志中。`productionEligibility` 另列当前可见候选是否对应生产 block；原始阶段快照均在日志中，便于核对动态 eligibility。独立 annotations 初始全部为 unknown，不能算正确。
- screenshots：before、after、stable、scroll-before、scroll-after、return-after、reflow-after。实际滚动约一屏，再返回顶部；存在关闭 details 时额外展开/还原并截图。重排通过临时设置 body max-width 为 viewport 的 80% 触发真实换行，保存并恢复原 inline 值及 priority；同时派发 resize 检查生产监听器。没有改浏览器窗口尺寸的能力，因此这不是 viewport resize 测试。
- 可选 `--full-screenshots`（别名 `--full`）另外捕获 before-full、after-full、stable-full，通过同一包装器调用 `screenshot <path> --full`。保留原首屏/滚动截图，完整页截图有独立 phase、文件、时间、viewport、SHA-256 与 `fullPage=true`；启用时缺少任一完整页截图不能记 evidenceReady，审核须对所有 phase 单独投票且核对原始哈希。选项冻结在 run 配置中；改变选项或源文件须创建新 run ID，原失败 attempt 不修改。完整页截图仍需人工检查长页的正文、裁切、重叠和重排，不证明无限滚动内容已经加载完毕。
- 每个阶段等待生产无 jobs/pending/scanning/rpcPending、实际 skipped/completed 状态、boolean pageDecision、全部已发请求成功终止且无 failed，再要求语义连续不变达到 stable_seconds（默认 2 秒）。只有 `pageDecision=true` 才要求当前 eligible 预测均 completed；`pageDecision=false` 时允许保留 `eligible=true, completed=false, failed=false` 的未翻译候选，原标志不改写，候选识别准确率另行评分。负例仍要求零候选翻译请求，任一 failed、未解决任务、缺失/失败 terminal 都不能通过。语义签名使用当前 `nodes` 的源文/显示文本/标签/角色多重集，以及当前生产预测的源文/文本、eligible/completed/failed 和源证据匹配标志、pageDecision；保留重复行数量，忽略顺序、时间戳、坐标、locator 和仅用于 bookkeeping 的 ID/groupId。实际文本或数量变化会重新计时。使用当前节点而非累计 sourceNodes 历史判定，以免相同内容的新身份不断替换阻止语义稳定。
- `settled` 仅表示上述语义等待通过，另写 `settlementKind=semantic`、`semanticStable`、`layoutReviewRequired=true`。每次 raw sample 的完整几何和阶段信息保持在 JSONL；另追加 `stability` 记录，并在 case 的 `stability[phase]` 保存最后一条：semanticUnchanged/semanticStable/stableForSeconds 与 geometryChanged/geometryVaried/geometryChanges 分开记录。几何变化单独比较 rects/可见状态多重集及 viewport，不用文本、locator 或行顺序冒充位移；这些诊断不认证布局稳定。CSS 轮播持续移动仍可能语义稳定，截图的人工布局检查始终必须完成。所有源身份继续累积到 sourceNodes 和 unknown annotations，不按语义签名去重或过滤；未匹配预测、历史未知证据和失败继续保留。
- 截图存路径、时间、viewport 与 SHA-256。必要时主控增加滚动范围/站点交互；默认一次滚动不能证明覆盖无限滚动站点的全部内容。本轮仅以离线夹具验证语义等待修复，没有重跑真实 People.com.cn 或宣称真实语料通过。
- retractions：观察已显示的文本回到原文/空白，要求生产保留原文未变、显示处于译文视图。主动原文/双语切换与已知真源变化不计撤回。生产观察器修复后采样，因此只记录持续可观察撤回，不把同一回调内已修复的瞬态写入当已显示。不能证明从未出现帧级闪烁；截图/视频审核应补充此项。

`visitSuccess` 与 `completed` 分离。外语页必须 live、真实 requestCount>0、每候选成功终止、逐节点完成、截图齐备与语义稳定；模拟、全缓存无实际模型调用、失败或缺终止均不满足。目标语言页要求实际 false 决策与零候选翻译请求。`evidenceReady=true` 是等待截图审核的完整证据，初始 `completed=false`。

## 独立审核（离线）

人类依据原文和截图独立标注，不根据 predictions 自动生成标签。准备外部 review JSON：

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

必须审核 case 中的全部 screenshot phase（存在 details 时也包括额外两张，启用完整页截图时也包括 before-full/after-full/stable-full）。运行：

```powershell
python eng/browser-use-qa/review.py `
  --case C:/qa-evidence/public-sites-001/site-001/attempt-0001/case.json `
  --review C:/qa-evidence/site-001-review.json `
  --output C:/qa-evidence/public-sites-001/site-001/attempt-0001/reviewed-001.json
```

审核核对 case 与截图哈希，生成全新结果，不覆盖 case、review 或历史结果。模拟和缺失 live/terminal 的证据即使所有票为 true 也无法变为 completed。未标注节点继续 unknown。完成捕获不是准确率通过；统计应纳入有独立真值的有效决策错误和未知/遗漏，不可只筛 completed 来美化指标。全体/holdout 指标与至少 100 不同域的验收由 `eng/website-qa/` 主控流程负责。

## 离线验收

```powershell
pwsh -NoProfile -File eng/parallel/Test-OwnedFiles.ps1 -PlanPath .parallel/plan.json -TaskId transport
C:/Users/cool848457/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe -m unittest discover -s eng/browser-use-qa/tests -p 'test_*.py'
```

测试包括 mock CLI JSON/失败/分块注入（转义、非 BMP、中文与 UTF-16 边界），批量交付的原始帧/交错 RPC/单次确认/顺序、完整包装预算边界、大包安全数据分块、错误与逐事件日志、case 期限与子进程超时，Promise 空对象及任务 completed/failed/timeout 回归，负例只有完整 probe 才能转发 assess，合成 DOM 可见候选（脚本/样式/隐藏节点不生成证据、后来显示/隐藏/脱离保留完整历史、预测 unmatched）/稳定 ID/撤回/RPC/监听器，独立 JSONL 严格 mock 进程（拒绝空身份/重复请求、核验响应关联、同一主机连续两个站点），实际 Windows 句柄占用与模拟替换冲突/耗尽/旧证据保留/失败堆栈，持续 rect/locator/顺序/身份运动的语义稳定、所有替换源身份/unknown 历史保留、13 个 eligible 未翻译候选的 skipped 负例零翻译请求且捕获全部截图、负例文本变化/failed/未解决任务仍阻止稳定、文本/数量变化和未解决任务阻止稳定、完整页独立截图/哈希/审核门槛，逐站截图阶段、失败恢复、冻结/历史保护、完成门槛与独立审核。测试临时目录在 owned tests 内并自动清理。实际验收日志和修复前 trace 保留于忽略的 `.test-logs/`，原主控审查失败报告保持不动；没有执行真实 Browser Use、网络或模型。
