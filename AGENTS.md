# 主控与并行开发约定

收到自然语言开发需求时，主控先阅读 `eng/parallel/CONTRACT.md` 和
`docs/PARALLEL-DEVELOPMENT.md`，检查工作区、定义接口和验收条件，然后生成
3～5 个不重叠的文件归属任务，使用本仓库的 PowerShell 调度器。

- 并行开发必须使用独立 `codex exec` 进程、独立 Git Worktree 和独立分支。
- 公共接口、依赖配置、共享入口先由主控完成并提交，再固定所有任务的基线 SHA。
- 开发 Agent 只在分配的 Worktree 内修改 ownedPaths，禁止提交、合并、推送或操作其他工作区。
- 需要越过文件归属或额外权限时，返回 blocked 和具体原因，由主控处理。
- 主控审查真实差异并重跑验收命令后保存任务提交，再逐个合并到 integration 分支。
- 完整构建、.NET 测试、扩展测试和调度器测试全部通过后，主控才可快进更新干净的 main。
- 保留现有未提交改动，禁止 reset --hard、clean -fd、强制覆盖、强制推送和绕过沙盒/审批。
- 运行日志、提示、结果及本机配置留在忽略目录，不提交密钥、用户资料或生成资源。

项目：Core 是平台无关的翻译、设置和浏览器协议；App 是 Windows WPF、OCR、
原生消息和 UI；browser-extension 是 Chrome JS 扩展。测试位于 tests 和
browser-extension/tests。完整本地验证与现有 CI 的构建和测试步骤一致；
安装器发布仍按 `.github/workflows/ci.yml` 的发布步骤单独执行。
