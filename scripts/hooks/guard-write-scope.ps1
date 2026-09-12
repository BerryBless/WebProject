# guard-write-scope.ps1 — PreToolUse 훅: 감사·리뷰 전용 서브에이전트의 Write/Edit 를 허용 디렉터리로 제한한다.
#
# 사용(에이전트 프론트매터 hooks 또는 settings.json):
#   pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$env:CLAUDE_PROJECT_DIR\scripts\hooks\guard-write-scope.ps1" -Allow "_workspace/code-review/;_workspace/gc-guard/"
#
# 동작:
#   - stdin 으로 PreToolUse JSON(tool_name, tool_input, cwd …)을 받는다.
#   - tool_name 이 Write/Edit/MultiEdit/NotebookEdit 가 아니면 통과(exit 0, 출력 없음).
#   - tool_input.file_path(또는 notebook_path)를 저장소 루트 기준 상대 경로로 정규화해 -Allow 접두사 중 하나로 시작하면 통과.
#   - 아니면 {"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":…}} 를 stdout 으로 내고 exit 0.
#   - 파싱 실패 등 예외 시에는 차단하지 않고 systemMessage 로 경고만 남긴다(리뷰 흐름을 막지 않기 위해).
# 규칙(plan/harness_cross_check_0913.md 공통 결함 G2 후속): 지시문("Write 는 출력 파일에만")을 권한 수준에서 강제한다.

param(
    [string]$Allow = "_workspace/",
    [string]$Root = ""
)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

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

    $path = "$($hookInput.tool_input.file_path)"
    if (-not $path) { $path = "$($hookInput.tool_input.notebook_path)" }
    if (-not $path) { exit 0 }

    if (-not $Root) { $Root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($hookInput.cwd) { "$($hookInput.cwd)" } else { (Get-Location).Path } }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $full = if ([IO.Path]::IsPathRooted($path)) { [IO.Path]::GetFullPath($path) } else { [IO.Path]::GetFullPath((Join-Path $rootFull $path)) }

    # 저장소 밖(스크래치패드 등)은 이 훅의 관심사가 아니다 — 프로젝트 소스 보호가 목적이므로 통과
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }

    $rel = $full.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')
    $prefixes = $Allow -split '[;,]' | ForEach-Object { $_.Trim().TrimStart('/').Replace('\', '/') } | Where-Object { $_ }
    foreach ($p in $prefixes) {
        if ($rel.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
    }
    Out-Deny "이 에이전트는 감사·리뷰 전용이라 '$rel' 에 쓸 수 없습니다. 허용 경로: $($prefixes -join ', '). 발견 사항은 JSON 산출물에 기록하고 프로젝트 소스는 수정하지 마세요."
}
catch {
    @{ systemMessage = "guard-write-scope: 입력 해석 실패로 검사를 건너뜀 — $($_.Exception.Message)" } | ConvertTo-Json -Compress | Write-Output
    exit 0
}
