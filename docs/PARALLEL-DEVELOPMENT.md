# 并行开发操作指南

接口以 [CONTRACT.md](../eng/parallel/CONTRACT.md) 为准。自然语言需求经仓库
[AGENTS.md](../AGENTS.md) 进入主控：主控读约定、检查工作区、明确接口和验收，
生成并审查 JSON 计划，然后调用确定性的 PowerShell 调度器。
脚本不自动理解需求或做语义拆分；不能用原生 subagent 代替独立 `codex exec`
进程、独立 Git Worktree 和独立分支。

## 边界与准备

| 模块 | 责任及依赖 | 验收 |
| --- | --- | --- |
| Core | 平台无关的翻译、OCR/存储抽象、Models、设置、BrowserProtocol | Core.Tests |
| App | Windows WPF、ViewModels、Services、OCR、Native Messaging、UI；依赖 Core | Release 构建、IntegrationTests |
| browser-extension | Chrome JS background/content、页面/图片策略；经 BrowserProtocol 连接 App | 全部 `node --test` |
| eng/parallel | 独立 CLI 进程、Worktree、调度、审查与集成 | 故障测试、另行真实 CLI 并发验证 |

Windows 主控需 PowerShell **7.2+**（`pwsh`）、Git、已安装且完成 `codex login`
的 Codex CLI、**.NET 8 SDK**、**Node.js 22**。使用现有登录，
不读取或复制认证文件，不改全局配置。本轮用户显式指定四个开发 Agent
（runtime、gates、tests、docs）均使用 **gpt-6.1-sol、High 推理、Fast 模式**；
主控必须逐进程传入 `--model gpt-6.1-sol -c model_reasoning_effort="high" -c service_tier="fast"`，
覆盖继承的用户模型默认值。后续计划任务沿用这些显式设置，除非用户更新要求。
官方配置文档说明 `fast` 映射为请求值 `priority`。主控先预备依赖与固定哈希资源
（见 [eng/assets.json](../eng/assets.json)），任务沙盒内禁止网络，不自动安装依赖。
CLI 路径应为 `.exe` 或 `.ps1`，公共启动器不支持 `.cmd`/`.bat` 插值。

1. 检查控制 checkout 和目标 main 的分支、HEAD、未提交/未跟踪文件；有未完成改动先保留，勿覆盖。
2. 主控先完成协议、设置迁移、公共抽象、依赖配置和共享入口，验证并提交共享变更。
3. 以该干净提交的同一完整 SHA 为全部任务基线，规划 **3～5** 个文件归属任务。
   开发 Agent 只读公共接口；只能修改自己的 `ownedPaths`，不能提交、合并、推送或操作其他 checkout。
4. 主控审查计划与验收命令。精确相对文件或尾随 `/` 的目录可作为归属，无通配符；
   大小写不敏感，父子目录也不能交叉；拒绝路径穿越、重复 id、循环依赖及运行目录/分支冲突。
   `maxParallel` 为 1～5；validation 非空，每项 `filePath`、`arguments` 数组，
   可选 `timeoutSeconds`，通过 ArgumentList 原样传参。

`dependsOn` 仅表示前任务已通过主控验收（reviewed）才启动后任务，**不会携带前任务代码**。
若需要前任务的新实现，结束本轮，集成后再以新提交启动下一轮，全部任务仍使用统一基线。
示例 [plan.example.json](../eng/parallel/templates/plan.example.json) 是浏览器策略回归需求，
不是可直接套用的任务拆分；复用 [controller-prompt.md](../eng/parallel/templates/controller-prompt.md)
时须按实际需求调整。

## 操作顺序

以下命令在控制仓库根目录的 PowerShell 7 中运行。`.parallel/` 是忽略目录；
准备计划后不要把运行状态、提示、日志或本机配置加入 Git。替换运行 id、任务 id、
main checkout 位置；`baseline` 应替换成共享提交 SHA，每次新运行使用新 `runId`。

```powershell
New-Item -ItemType Directory -Path .parallel -ErrorAction SilentlyContinue | Out-Null
Copy-Item -LiteralPath eng/parallel/templates/plan.example.json -Destination .parallel/plan.json
# 编辑 .parallel/plan.json，主控审查内容和命令后：
. ./eng/parallel/Common.ps1
$plan = Test-ParallelPlan (Read-ParallelJson .parallel/plan.json)
$repo = (Get-Location).Path
$run = Join-Path (Split-Path $repo -Parent) "codex-parallel-runs/$($plan.runId)"

# 仅准备：固定 SHA、创建独立分支/checkout 和状态；不启动 Agent
pwsh -NoProfile -File eng/parallel/Invoke-ParallelDevelopment.ps1 -PlanPath .parallel/plan.json -RepoRoot $repo -PrepareOnly
# 在已准备运行上启动/恢复；RunPath 与 PlanPath 不能同时使用
pwsh -NoProfile -File eng/parallel/Invoke-ParallelDevelopment.ps1 -RunPath $run
# 新运行也可直接启动（不要对上面已有运行重复执行）：
# pwsh -NoProfile -File eng/parallel/Invoke-ParallelDevelopment.ps1 -PlanPath .parallel/plan.json -RepoRoot $repo

pwsh -NoProfile -File eng/parallel/Get-ParallelStatus.ps1 -RunPath $run
pwsh -NoProfile -File eng/parallel/Get-ParallelStatus.ps1 -RunPath $run -Json
```

启动可选 `-RunRoot`（默认控制 Worktree 同级 `codex-parallel-runs`）、
`-CodexPath`（默认 `codex`）、`-TimeoutSeconds`（默认 1800）。已有目录不会被覆盖。
Status 读取 PID、状态和结果，不启动任务、不合并。

```powershell
# 每个 completed 任务都先执行无 Approve 的审查；此命令重跑验收，不提交
pwsh -NoProfile -File eng/parallel/Review-ParallelTask.ps1 -RunPath $run -TaskId core-policy
# 主控阅读输出的真实 diff、未跟踪文件、结果、权限和测试证据，再明确批准
pwsh -NoProfile -File eng/parallel/Review-ParallelTask.ps1 -RunPath $run -TaskId core-policy -Approve
# 对 app-memo、extension-policy 重复以上审查步骤

# 失败/blocked/需要返工：确认 worker 已结束、任务尚未集成，保留原改动
pwsh -NoProfile -File eng/parallel/Repair-ParallelTask.ps1 -RunPath $run -TaskId app-memo -Instructions '修复已记录的失败，保留现有改动并重跑验收；不得越过归属。'
pwsh -NoProfile -File eng/parallel/Invoke-ParallelDevelopment.ps1 -RunPath $run

# 全部 reviewed 后逐个集成并执行完整门禁，不改变 main
pwsh -NoProfile -File eng/parallel/Complete-ParallelDevelopment.ps1 -RunPath $run
# 按需向 Complete 或 Test-Repository 传 -DotnetPath <dotnet.exe>、-AssetSource <只读资源缓存>
# 单独重跑仓库门禁时：
# pwsh -NoProfile -File eng/parallel/Test-Repository.ps1 -RepoRoot <integration checkout> -ResultDirectory <忽略的报告目录>

# 主控确认完整门禁通过；替换为干净、分支正确的 main checkout
$mainCheckout = 'C:/repos/ScreenTranslator'
pwsh -NoProfile -File eng/parallel/Update-ParallelMain.ps1 -RunPath $run -MainWorktree $mainCheckout
```

Review 核对归属、分支/expectedHead、CLI 结果及权限请求，执行 `diff --check` 并重跑计划验收。
`-Approve` 才由主控保存验证过的任务提交，更新 expectedHead/verifiedCommit。
Complete 要求全部 reviewed、固定提交 SHA，从 baseline 创建
`codex/integration/<runId>`，逐个 `merge --no-ff`，最后执行 Test-Repository。
完整门禁为固定资源哈希检查/预备、restore、Release build、全部 .NET tests、全部扩展
node tests 和 `eng/parallel/Test-ParallelWorkflow.ps1`；任何失败都不能提升 main。
缺 .NET 8 SDK 明确失败，不能跳过测试或将未运行报告为成功。

Update 还检查 integration 实际 HEAD 等于验证 HEAD、完整报告通过、main 干净、目标分支正确、
main HEAD 等于启动时 mainHead 且可快进，只执行 `merge --ff-only`。本流程**不 push**、
不删除 Worktree。安装器打包、Inno Setup、校验和与安装器测试仍按
[ci.yml](../.github/workflows/ci.yml) 的发布步骤单独执行，不属于本地 build/test 门禁。

## 状态与证据

- 任务：`pending → running → completed → reviewed → integrated`。`completed` 只是 CLI
  声明完成；主控验证与明确批准后才是 reviewed。失败为 `failed`、`blocked`、`needs_rework`、`timed_out`。
- 总体状态建议：prepared、running、awaiting_review、failed、integrated、validated、promoted。
  `state.json` 保留 schemaVersion、runId、repoRoot、runPath、baseline、mainBranch、mainHead、
  status、createdAt、tasks、integration；任务保存 expectedHead、attempt、workerPid、verifiedCommit、error 等。
- `$run/plan.json` 是固定基线的计划，`state.json` 是主控状态；`controller.lock` 防止并发主控写入。
  `worktrees/<id>` 分支为 `codex/parallel/<runId>/<id>`；`worktrees/integration` 为集成 checkout。
- `tasks/<id>/attempt-<n>/` 保留 process.json、events.jsonl、stderr.log、result.json、验收日志及审查证据。
  主控核对 host 在 process.json 记录的 model=`gpt-6.1-sol`、reasoningEffort=`high`、
  serviceTier=`fast`、requestServiceTier=`priority` 及实际 arguments；这些是进程配置证据。
  每个 checkout 的 `.parallel/` 保留 task.json、plan.json、agent-prompt.txt、result.schema.json 和 CLI 原始 result.json。
- 结果结构遵循 [result.schema.json](../eng/parallel/result.schema.json)：taskId、status
  （completed/blocked/failed）、summary、changedFiles、tests（command/outcome/details）、risks、permissionRequests。
  Agent 如实填写实际测试结果，缺权限返回 blocked 及具体 permissionRequests。
- 主控查看真实 diff、命令/退出码/超时/日志以及完整门禁 report.json 中的验证 HEAD。
  假 CLI 故障测试只证明控制流；真实 CLI 并发与沙盒有效性须另行验证，不可混称。

## 失败与恢复

| 现象 | 主控处理 |
| --- | --- |
| CLI/验收失败或超时 | 查 attempt 的事件、stderr、process、result 和验收日志；超时只终止该进程树。保留 checkout 和未完成改动，Repair 增补指令标为 pending，恢复创建新 attempt，旧日志不覆盖。 |
| 归属外修改、分支/HEAD 漂移 | 停止审查/调度，记录实际路径和 SHA；由主控核实，不能手改状态绕过检查。需共享接口变更则集成后重开统一基线。 |
| 权限不足、非交互审批拒绝、沙盒初始化失败 | 保留日志和 blocked 原因，由主控在已有授权和沙盒规则内处理。启用 prefer_mxc 时支持设备使用原生 MXC，保留既有旧式沙盒回退；失败不降低沙盒、不自动申请管理员权限。 |
| 锁冲突、仍有 worker 或运行名冲突 | 用 Status 核对 PID 与证据；不重复启动、不在 worker 运行中 Repair，不覆盖已有运行目录/分支。 |
| 集成冲突或完整门禁失败 | 保留 integration、报告及改动，由主控处理并重新验证；不得更新 main，不以任务局部验收代替完整门禁。 |
| main 脏/移动，integration 验证后变化 | 拒绝提升；保存已有工作并核对漂移原因。integration 变化必须重跑完整验证；main 移动需要重新规划，不能强行覆盖。 |

禁止 `reset --hard`、`clean -fd`、强制覆盖/推送及危险或绕过沙盒/审批的 CLI 参数。
不要提交日志、提示、本机配置、密钥、用户资料或生成资源。恢复保留证据，不能靠删除日志或强改 HEAD 消除失败。

## 本地文档校验与 CLI 参考

```powershell
. ./eng/parallel/Common.ps1
Test-ParallelPlan (Read-ParallelJson eng/parallel/templates/plan.example.json) | Out-Null
pwsh -NoProfile -File eng/parallel/Test-OwnedFiles.ps1 -PlanPath .parallel/plan.json -TaskId docs
```

独立 worker 在契约的 exec 命令上加本轮显式配置：

```text
codex --ask-for-approval on-request exec --sandbox workspace-write --model gpt-6.1-sol -c model_reasoning_effort="high" -c service_tier="fast" --cd <own> --json --output-schema <schema> --output-last-message <result> -
```

提示经 UTF-8 stdin 输入；命令中的三个模型配置参数只覆盖该进程，不修改全局配置。
清空额外 writable_roots，禁止任务内网络；非交互审批无法授权时返回 blocked。
官方说明：[非交互 exec](https://learn.chatgpt.com/docs/non-interactive-mode)、
[配置参考（fast → priority）](https://learn.chatgpt.com/docs/config-file/config-reference)、
[CLI 参数参考](https://developers.openai.com/codex/cli/reference/)。
