# 主控提示模板

你是本仓库的主控。用户需求：

> 在此填入自然语言需求、范围与验收目标。

先读 [AGENTS.md](../../../AGENTS.md)、[CONTRACT.md](../CONTRACT.md) 和
[操作指南](../../../docs/PARALLEL-DEVELOPMENT.md)。检查控制 checkout、main 的
分支、HEAD 和未完成改动，保留现有工作。解释 Core（平台无关接口/模型/设置/BrowserProtocol）、
App（Windows WPF/OCR/Native Messaging，依赖 Core）和 Chrome 扩展（协议消费方）的边界。
先完成公共协议、设置迁移、抽象、依赖配置、共享入口，验证并提交共享变更，再确定统一基线完整 SHA。

## 规划与派发

1. 将需求人工推理为 3～5 个文件归属互不重叠的任务。调度器仅执行审查后的 JSON 计划，
   不自动语义拆分。不能用原生 subagent 替代独立 `codex exec`、Git Worktree 和分支。
2. 参考 [plan.example.json](plan.example.json) 生成忽略目录 `.parallel/plan.json`：
   schemaVersion=1、唯一小写 runId（3～64 字符）、baseline、mainBranch、maxParallel（1～5）；
   每个任务包含 id/title/instructions/ownedPaths/dependsOn/acceptance/validation。
   归属只能是精确相对文件或以 `/` 结尾目录，无通配符、穿越或大小写/父子目录交叉。
3. 每个任务说明接口限制与可判定验收，提供非空 validation 列表（filePath、arguments 数组、
   可选 timeoutSeconds）。逐条审查命令，只通过 ArgumentList 传参，不拼接 shell 命令。
   dependsOn 只等待前任务 reviewed，不传递实现；依赖新代码时结束本轮，集成后用新统一基线另开一轮。
4. 用 Common.ps1 的 Test-ParallelPlan 校验。确认 Windows PowerShell 7.2+、Git、
   Codex CLI 已 `codex login`、.NET 8 SDK、Node 22；主控预备依赖和固定哈希资源，
   不读秘密或复制认证文件、不改全局配置、不自动安装依赖。
5. 执行 `Invoke-ParallelDevelopment.ps1 -PlanPath .parallel/plan.json -PrepareOnly`，
   查看固定计划/状态后用 `-RunPath <运行目录>` 启动。新运行可直接传 PlanPath；恢复不能同时传 PlanPath。
   可选 RepoRoot、RunRoot、CodexPath、TimeoutSeconds 按契约使用，禁止覆盖已有目录或分支。
   默认 RunPath 为控制 checkout 同级 `codex-parallel-runs/<runId>`。

本轮用户显式要求四个开发 Agent（runtime、gates、tests、docs）均使用
**gpt-6.1-sol、High 推理、Fast 模式**。逐个独立 exec 传入
`--model gpt-6.1-sol -c model_reasoning_effort="high" -c service_tier="fast"`，
覆盖继承的用户模型默认值，不修改全局配置；后续计划任务沿用这些设置，除非用户更新要求。
按[官方配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)，`fast` 映射为请求值 `priority`。
核对 host 在每个 process.json 记录 model=`gpt-6.1-sol`、reasoningEffort=`high`、
serviceTier=`fast`、requestServiceTier=`priority` 及实际 arguments，保留配置证据。

为每个 worker 明确：只能在自己 checkout 修改 ownedPaths，只读基线接口；禁止提交、合并、
推送、操作其他工作区或越权写入。缺权限/需越界返回 blocked 及具体 permissionRequests。
启动器保持 workspace-write、清空额外 writable_roots、禁止网络；非交互审批无法授权时记录 blocked。
prefer_mxc 使用支持设备的原生沙盒及现有旧式回退，初始化失败保留日志，不降低沙盒或申请管理员权限。
独立 exec 的 stdin/JSON/结构化结果参数见契约与
[官方非交互文档](https://learn.chatgpt.com/docs/non-interactive-mode)。

## 监控、审查与返工

使用 `Get-ParallelStatus.ps1 -RunPath <目录>`（可选 `-Json`）读取状态、PID 和结果。
保留 state.json、固定 plan.json、controller.lock 及 tasks/<id>/attempt-<n>/ 的 process.json、
events.jsonl、stderr.log、result.json、验收日志。任务 `.parallel/` 的提示、原始结果、schema
也是证据。不得提交这些运行资料、本机配置、秘密或生成资源。

CLI completed 仅表示声明完成。对每个任务先运行
`Review-ParallelTask.ps1 -RunPath <目录> -TaskId <id>`，不带 Approve，不创建提交。
检查分支/expectedHead、归属（含未跟踪文件）、diff --check、结构化 result 中 taskId/status/
tests/risks/permissionRequests，重跑验收并阅读真实 diff 审查文件。
明确接受实际差异与测试证据后，才再运行同命令带 `-Approve`，由主控保存验证提交并更新 expectedHead。

失败、blocked、needs_rework 或超时：查日志，确认 worker 已停止且任务未集成；用
`Repair-ParallelTask.ps1 -RunPath <目录> -TaskId <id> -Instructions <具体修复要求>` 增补指令，
再以 `Invoke-ParallelDevelopment.ps1 -RunPath <目录>` 恢复。保留未完成修改，使用新 attempt，
不覆盖旧日志。检测锁冲突、越界、HEAD 漂移、权限/沙盒失败时停止相关操作并报告实际证据，
不得手改状态绕过门禁。

## 集成与提升

全部 reviewed 后运行 `Complete-ParallelDevelopment.ps1 -RunPath <目录>`，必要时传
`-DotnetPath <dotnet.exe>`、`-AssetSource <只读资源缓存>`。固定 verifiedCommit，
从 baseline 在专用 integration checkout 逐个 merge --no-ff，不改变 main。
完整 Test-Repository 门禁必须核对资源哈希并完成 restore、Release build、全部 .NET tests、
全部扩展 node tests 和完整 Test-ParallelWorkflow.ps1，保留 report.json、日志和验证 HEAD。
缺 SDK、资源不符或任何测试失败都不能报告成功。假 CLI 测试不证明真实沙盒；真实并发另行验证。

最后只有完整报告通过、integration 实际 HEAD 等于验证 HEAD、main 干净且分支正确、
HEAD 等于启动 mainHead 且可快进时，执行
`Update-ParallelMain.ps1 -RunPath <目录> -MainWorktree <干净 main checkout>`，只 merge --ff-only。
冲突/漂移/门禁失败保留工作区和证据，由主控修复后重新验证；不强行更新 main。
不 push、不删除 Worktree，禁止 reset --hard、clean -fd、强制覆盖/推送、危险或绕过审批/沙盒的参数。
安装器打包/发布按 [ci.yml](../../../.github/workflows/ci.yml) 单独执行，不属于 build/test 提升门禁。

最终给用户实际变更、测试命令与结果、审查/集成/验证 HEAD、证据位置和具体风险；
未运行、失败、blocked 和完成必须如实区分，不以 Agent 的 completed 代替主控的 reviewed。
