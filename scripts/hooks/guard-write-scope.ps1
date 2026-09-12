# guard-write-scope.ps1 — PreToolUse 훅: 감사·리뷰 전용 서브에이전트의 Write/Edit 를 허용 디렉터리로 제한한다.
#
# 두 가지 사용법:
#   (A) 에이전트 프론트매터 훅(1차 방어선, 에이전트별 명시):
#       pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts/hooks/guard-write-scope.ps1 -Allow _workspace/code-review/
#   (B) 프로젝트 settings.json PreToolUse 훅(2차 방어선, agent_type 으로 판별):
#       pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File scripts/hooks/guard-write-scope.ps1 -Mode map
#       → stdin 의 agent_type 이 아래 $AgentMap 에 있으면 그 접두사로 제한, 없으면(메인 세션·구현 에이전트) 통과.
#
# 동작:
#   - stdin 으로 PreToolUse JSON(tool_name, tool_input, cwd, agent_type …)을 받는다.
#   - tool_name 이 Write/Edit/MultiEdit/NotebookEdit 가 아니면 통과(exit 0, 출력 없음).
#   - tool_input.file_path(또는 notebook_path)를 저장소 루트 기준 상대 경로로 정규화해 허용 접두사로 시작하면 통과.
#   - 아니면 {"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":…}} 를 stdout 으로 내고 exit 0.
#   - 저장소 밖 경로(스크래치패드)는 관심사가 아니므로 통과. 파싱 실패 시 차단하지 않고 systemMessage 경고만.
# 주의: 프론트매터 훅과 settings.json 훅 모두 세션 시작 시 읽힌다 — 변경 후 세션을 재시작해야 적용된다.
# 규칙 근거: plan/harness_cross_check_0913.md 공통 결함 G2 후속(지시문 "Write 는 출력 파일에만"의 권한 수준 강제).

param(
    [string]$Allow = "",
    [ValidateSet('allow', 'map')][string]$Mode = 'allow',
    [string]$Root = ""
)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 감사·리뷰 전용 에이전트 → 허용 접두사 (구현 역할인 cross-implementer 는 없음)
$AgentMap = @{
    'architecture-reviewer'      = '_workspace/code-review/'
    'security-reviewer'          = '_workspace/code-review/'
    'performance-reviewer'       = '_workspace/code-review/'
    'style-reviewer'             = '_workspace/code-review/'
    'heap-allocation-scanner'    = '_workspace/gc-guard/'
    'pooling-enforcer'           = '_workspace/gc-guard/'
    'allocation-peer-reviewer'   = '_workspace/gc-guard/'
    'lock-free-enforcer'         = '_workspace/concurrency-guard/'
    'lock-justification-auditor' = '_workspace/concurrency-guard/'
    'deadlock-analyzer'          = '_workspace/concurrency-guard/'
    'deadlock-reviewer'          = '_workspace/concurrency-guard/'
    'pipeline-supervisor'        = '_workspace/pipeline/'
    'io-loop-designer'           = '_workspace/pipeline/'
    'thread-dispatcher-designer' = '_workspace/pipeline/'
    'load-test-auditor'          = '_workspace/pipeline/'
    'tdd-analyst'                = '_workspace/tdd/'
    'tdd-builder'                = '_workspace/tdd/'
    'tdd-qa'                     = '_workspace/tdd/'
    'git-security-auditor'       = '_workspace/git/'
    'git-commit-writer'          = '_workspace/git/'
    'git-push-controller'        = '_workspace/git/'
    'cross-planner'              = '_workspace/cross/'
    'cross-reviewer'             = '_workspace/cross/'
    'codex-adapter'              = '_workspace/cross/'
}

function Out-Deny([string]$reason) {
    @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $reason } } |
        ConvertTo-Json -Compress -Depth 4 | Write-Output
    exit 0
}

try {
    $raw = [Console]::In.ReadToEnd()
    if (-not $raw) { exit 0 }
    $hookInput = $raw | ConvertFrom-Json
    $tool = "$($hookInput.tool_name)"
    if ($tool -notin @('Write', 'Edit', 'MultiEdit', 'NotebookEdit')) { exit 0 }

    $agent = "$($hookInput.agent_type)"
    if (-not $agent) { $agent = "$($hookInput.agent_name)" }
    if ($Mode -eq 'map') {
        if (-not $agent -or -not $AgentMap.ContainsKey($agent)) { exit 0 }   # 메인 세션·구현 에이전트·미등록 → 통과
        $Allow = $AgentMap[$agent]
    }
    if (-not $Allow) { exit 0 }

    $path = "$($hookInput.tool_input.file_path)"
    if (-not $path) { $path = "$($hookInput.tool_input.notebook_path)" }
    if (-not $path) { exit 0 }

    if (-not $Root) { $Root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hookInput.cwd) { "$($hookInput.cwd)" } else { (Get-Location).Path } }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $full = if ([IO.Path]::IsPathRooted($path)) { [IO.Path]::GetFullPath($path) } else { [IO.Path]::GetFullPath((Join-Path $rootFull $path)) }

    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }   # 저장소 밖

    $rel = $full.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
    $prefixes = $Allow -split '[;,]' | ForEach-Object { $_.Trim().TrimStart('/').Replace('\', '/') } | Where-Object { $_ }
    foreach ($p in $prefixes) {
        if ($rel.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
    }
    $who = if ($agent) { "에이전트 '$agent'" } else { "이 에이전트" }
    Out-Deny "$who 는 감사·리뷰 전용이라 '$rel' 에 쓸 수 없습니다. 허용 경로: $($prefixes -join ', '). 발견 사항은 run_dir 산출물에 기록하고 프로젝트 소스는 수정하지 마세요."
}
catch {
    @{ systemMessage = "guard-write-scope: 입력 해석 실패로 검사를 건너뜀 — $($_.Exception.Message)" } | ConvertTo-Json -Compress | Write-Output
    exit 0
}
