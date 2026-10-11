# 本地并行开发

以后直接向本仓库主控提交自然语言需求即可。主控按 `AGENTS.md` 定义接口、
划分 3～5 个无文件交叉的任务，再调用 PowerShell 7 调度器。

默认：**gpt-6.1-sol / High / Fast**，独立 CLI 进程、独立 Worktree 和分支。
使用原生 `codex.exe`、UTF-8、`workspace-write`、`on-request`；设备支持时优先 MXC。
需要已登录的 Codex CLI 0.162+、Git、PowerShell 7.2+、.NET 8 SDK 和 Node.js 22+。

主控常用命令（在仓库根目录运行）：

```powershell
# 主控生成并审查 .parallel/plan.json 后
pwsh -File eng/parallel/Invoke-ParallelDevelopment.ps1 -PlanPath .parallel/plan.json
pwsh -File eng/parallel/Get-ParallelStatus.ps1 -RunPath <run>
pwsh -File eng/parallel/Review-ParallelTask.ps1 -RunPath <run> -TaskId <id>
pwsh -File eng/parallel/Review-ParallelTask.ps1 -RunPath <run> -TaskId <id> -Approve
pwsh -File eng/parallel/Complete-ParallelDevelopment.ps1 -RunPath <run>
pwsh -File eng/parallel/Update-ParallelMain.ps1 -RunPath <run> -MainWorktree <main-checkout>
```

失败由 `Repair-ParallelTask.ps1` 增补指令，再用调度器 `-RunPath` 重试；旧改动和日志保留。
main 新增提交时，主控使用 `Sync-ParallelMain.ps1` 核对并合入 integration，记录重规划 SHA，
然后重新执行 Complete 的全部测试。该命令不改 main，也不会继承旧的通过记录。

局部验收和审查后才保存任务提交，依次合入 integration；Release 构建、全部 .NET、
浏览器扩展和调度测试通过后，才可快进更新干净且 HEAD 未漂移的 main。不会推送远端。

[完整说明](../../docs/PARALLEL-DEVELOPMENT.md) · [接口契约](CONTRACT.md) ·
[计划模板](templates/plan.example.json) · [主控提示](templates/controller-prompt.md)
