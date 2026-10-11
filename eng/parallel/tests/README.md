# Native workflow tests

Run `pwsh -NoProfile -File eng/parallel/Test-ParallelWorkflow.ps1 -SelfOnly`
for immutable Common.ps1 and Start-CodexTask.ps1 checks. Omit `-SelfOnly` after all production runtime
and gate scripts have been integrated. Missing production scripts fail full mode.

No Pester, network, actual Codex, SDK, or external test package is used. Each
suite creates fresh Git fixtures under the current repository's ignored
`.parallel/wf/` directory. Git commits, branches and worktrees are
created only inside these fixtures. All fixture files are retained, including
`summary.json`, command logs, worker process logs and concurrency evidence.
The suite never deletes fixtures or changes the enclosing checkout's index or
history. It never calls the real Test-Repository.

Stub-Codex.ps1 accepts the production CLI's `--cd`, `--output-schema` and
`--output-last-message` (`-o`) arguments. It reads UTF-8 stdin and emits schema
valid results except for explicitly requested invalid/missing-result scenarios.
Every fake worker writes only within its own checkout. The concurrency test
waits for three local sentinels, releases each worker, then verifies distinct
PIDs and a three-way overlap of recorded UTC intervals. Every wait is bounded;
the test terminates only processes it launched when a deadline fails.

Full mode exercises scheduling, result failures, permission denials, timeout,
repair evidence isolation, review validation, approval of new untracked output,
ownership, sequential integration, successful promotion and promotion rejections.
Rejections must match the intended guard and leave main/edits intact.

Integration fixtures commit a tiny empty asset manifest and node smoke test
before preparation. They copy the real Complete script into their repository,
but replace that fixture's Test-Repository with Stub-Repository.ps1 BEFORE the
baseline commit. Complete therefore runs real review/merge/state control flow
with a clearly marked synthetic report, without recursing into this suite.
The report records the actual integration HEAD, no errors, empty manifest asset
rows and all six required commands with precise arguments, successful exit codes
and existing ignored logs under integration `.parallel/`. Those commands are
not executed; the logs and report explicitly say so. Tasks have real approved
commits and integration verifies ancestry, one merge per task and retained output.

Fixtures use ordinary `git worktree add`. If sandbox metadata restrictions
prevent Git worktree operations, the suite reports a failure and retains its
diagnostics; it does not retry with bypasses or silently skip the assertion.
Repository build/tests and real Codex sandbox isolation require separate
controller validation; mocks do not verify a sandbox or actual build success.
