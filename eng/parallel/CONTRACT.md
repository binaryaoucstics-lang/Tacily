# 并行开发接口 v1（公共基线）

## 项目边界和验收

| 模块 | 责任与接口 | 验收 |
| --- | --- | --- |
| ScreenTranslator.Core | Abstractions 的翻译/OCR/存储接口，Models 数据类型，BrowserProtocol 传输协议 | Core.Tests |
| ScreenTranslator.App | WPF UI、ViewModels、Services、OCR 和浏览器 Native Messaging；依赖 Core | Release 构建和 IntegrationTests |
| browser-extension | background/content、页面和图片策略；使用 BrowserProtocol 连接桌面端 | 现有全部 node --test |
| eng/parallel | Windows PowerShell 7 主控进程，独立 codex exec 与 Git Worktree | 本地故障测试和真实 CLI 并发验证 |

业务开发前主控固定协议、设置迁移、公共抽象和依赖配置；先完成共享变更提交。
任务只读公共基线，不自动携带其他任务的工作成果。dependsOn 表示先完成验收，
需要读取前任务代码时应结束本轮并以已集成的新提交作为下一轮统一基线。

## 不变文件

本轮四个 Agent 只能读取 AGENTS.md、Common.ps1、Start-CodexTask.ps1、
Test-OwnedFiles.ps1、result.schema.json 和本接口文件，不得修改。

## 任务计划 JSON

字段：schemaVersion=1；runId 为 3-64 字符小写 slug；baseline 为干净提交 ref
（启动即固定完整 SHA）；mainBranch；maxParallel=1..5；tasks 数量 3..5。
每个 task 必须包含 id、title、instructions、ownedPaths、dependsOn、acceptance、validation。
ownedPaths 是精确相对文件名或以 / 结尾的目录，无通配符；大小写不敏感并检查父子目录重叠。
validation 是非空命令列表，每项含 filePath 和 arguments 数组，可含 timeoutSeconds。
命令由主控审查，使用 ArgumentList 原样传参，不使用字符串拼接的 shell。
runId 和 task id 不重复，拒绝路径穿越、循环依赖和已存在的运行目录/分支。

## 固定文件布局和 state.json

RunPath 默认为控制 Worktree 的同级 codex-parallel-runs/<runId>，绝不覆盖已有目录。
其中 plan.json 保存固定后的计划，state.json 保存主控状态，controller.lock 为互斥锁。
worktrees/<id> 是独立任务 checkout；worktrees/integration 是独立集成 checkout。
tasks/<id>/attempt-<n>/ 保存 process.json、events.jsonl、stderr.log、result.json 和验证日志。
每个任务 checkout 的忽略目录 .parallel/ 存放 task.json、plan.json、agent-prompt.txt、
result.schema.json 和 CLI 原始 result.json。

state.json 的必需字段：schemaVersion、runId、repoRoot、runPath、baseline、mainBranch、
mainHead、status、createdAt、tasks、integration。可加字段，但保留这些字段。
tasks 是数组，每项：id、title、instructions、ownedPaths、dependsOn、acceptance、validation、
branch、worktree、expectedHead、status、attempt、attemptPath、workerPid、verifiedCommit、error。
初始 expectedHead=baseline，attempt=0，workerPid=null，verifiedCommit=null，error=''。
初始 integration={branch:'codex/integration/<runId>',worktree:<absolute>,status:'pending',
head:null,validationHead:null,validationStatus:'pending',reportPath:null}。
任务分支为 codex/parallel/<runId>/<id>。

status：pending -> running -> completed（仅 CLI 提交结果）-> reviewed（主控验证且明确审查）
-> integrated。失败状态为 failed、blocked、needs_rework、timed_out。
运行总体状态建议 prepared、running、awaiting_review、failed、integrated、validated、promoted。
主控必须将 CLI 的 completed 与自己验证后 reviewed 区分。

Start-CodexTask.ps1 参数：Worktree、PromptPath、AttemptPath、CodexPath='codex'、TimeoutSeconds=1800。
以独立进程运行：codex --ask-for-approval on-request exec --sandbox workspace-write
--cd <own> --json --output-schema <schema> --output-last-message <result> -。
提示经 UTF-8 stdin 传入；日志由外层 worker 捕获；CLI 不获得其他 Worktree 写权限。
明确清空额外 writable_roots 并禁止沙盒内网络访问；依赖和资源由主控预备。
默认使用现有 Codex 登录与模型设置；无需复制认证文件或覆盖用户配置。
不接受降低沙盒的参数。非交互审批不能取得授权时，返回 blocked 和权限原因交主控。

## 公共 PowerShell 函数（dot source Common.ps1）

- Read-ParallelJson(Path) -> hashtable；Write-ParallelJson(Path,Value) 原子写入。
- Test-ParallelPlan(Plan) -> 经验证的 hashtable；检查归属、数量和依赖图。
- Invoke-ParallelGit(RepoRoot,Arguments) -> 字符串，非零退出抛错。
- Get-ParallelHead(RepoRoot)、Assert-ParallelClean(RepoRoot)。
- Assert-ParallelChildPath(Parent,Path) -> 绝对路径；严格位于 Parent 内。
- ConvertTo-ParallelOwnedPath(Path)、Test-ParallelPathOwned(Path,OwnedPaths)。
- Get-ParallelChangedFiles(RepoRoot,Baseline) -> 文件数组，含未跟踪文件。
- Assert-ParallelTaskWorktree(State,Task)：目录、分支、expectedHead、归属全部核对。
- Enter-ParallelLock(RunPath) -> FileStream，必须在 finally Dispose。
- Resolve-ParallelExecutable(Name)、New-ParallelProcessInfo(FilePath,Arguments,WorkingDirectory)。
- Invoke-ParallelCommand(FilePath,Arguments,WorkingDirectory,LogPath,TimeoutSeconds=1800)
  -> {filePath,arguments,exitCode,timedOut,logPath,startedAt,finishedAt}。
  并行读取 stdout/stderr，超时仅终止自身进程树，保留工作区。

## 独占子任务和命令接口

1. runtime：Invoke-ParallelDevelopment.ps1、Get-ParallelStatus.ps1、Repair-ParallelTask.ps1。
   启动参数 PlanPath（必需）、RepoRoot（默认仓库）、RunRoot（可选）、CodexPath='codex'、
   TimeoutSeconds=1800、PrepareOnly（仅准备）、RunPath（恢复已有运行，不能同时 PlanPath）。
   Get-ParallelStatus.ps1(RunPath,Json 可选) 读取 PID/状态/结果，不启动或合并任务。
   Repair-ParallelTask.ps1(RunPath,TaskId,Instructions) 检查不再运行、未集成，保留改动，
   增补指令，将任务标为 pending；恢复调度器创建全新 attempt，禁止覆盖旧日志。

2. gates：Review-ParallelTask.ps1、Complete-ParallelDevelopment.ps1、Test-Repository.ps1、
   Update-ParallelMain.ps1。
   Review(RunPath,TaskId,Approve 开关)：重跑验收、检查归属/HEAD/结果/权限请求、diff --check；
   输出真实 diff 审查文件。不带 Approve 不创建提交；Approve 后保存验证过的提交并更新 expectedHead。
   Complete(RunPath,DotnetPath 可选,AssetSource 可选)：要求所有任务 reviewed，固定提交 SHA，
   逐个 merge --no-ff 到专用 integration，从基线创建，不动 main；最后运行 Test-Repository。
   Test-Repository(RepoRoot 默认,ResultDirectory 可选,DotnetPath 可选,AssetSource 可选)：
   资源固定哈希核对/预备（AssetSource 只读缓存，不复制业务源文件）；restore、Release build、
   全部 .NET tests、node tests、eng/parallel/Test-ParallelWorkflow.ps1 全部成功才返回0，
   任一步失败返回非零并保存 report.json 和日志，报告包含验证 HEAD。
   DotnetPath 可使用已有 SDK 的 dotnet.exe；缺失 .NET 8 SDK 时明确失败，不伪造测试成功。
   Update(RunPath,MainWorktree)：要求 integration 验证 HEAD 等于实际 HEAD，全部门禁通过，
   main 干净且 HEAD 等于启动时 mainHead，目标分支正确、可快进；只 git merge --ff-only。
   运行时不改变全局配置，不推送远端，不删除 Worktree，不自动安装依赖。

3. tests：Test-ParallelWorkflow.ps1 和 tests/ 目录。
   无需 Pester，使用原生 PowerShell 断言、临时 Git 仓库和假 codex 可执行脚本。
   Test-ParallelWorkflow.ps1 参数 SelfOnly 可选（仅公共基础测试；任务开发期缺其他模块时）。
   完整模式测试至少3个独立并发 worker、空间/中文路径、文件归属/依赖拒绝、任务失败、
   超时、验收失败不可提交、审批拒绝记录、返工日志隔离、main 脏/移动拒绝、集成头变化拒绝。
   假 CLI 仅用于测试控制流，真实 CLI 并发另行验证；不能宣称 mock 验证了沙盒。
   临时工作区保留诊断；如清理，先校验绝对路径在测试根内，用 PowerShell 原生 LiteralPath。
   禁止运行真实 Agent 和网络；禁止递归自调用 Test-Repository/完整调度测试。

4. docs：docs/PARALLEL-DEVELOPMENT.md 和 templates/ 目录。
   提供中文简明使用说明、架构/依赖/验收、自然语言主控工作方式、日志和故障处理。
   templates/plan.example.json 是3～5任务、所有字段齐全、无重叠的项目需求示例；
   templates/controller-prompt.md 是复用主控指令。

以上四组所有文件独占，跨组需求以结果 risks 上报。共享文件仅由主控集成后调整。
基础验收：pwsh -NoProfile -File eng/parallel/Test-OwnedFiles.ps1
-PlanPath .parallel/plan.json -TaskId <id>，各任务再自行增加有意义的本地验证。
