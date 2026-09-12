# invoke-codex.ps1 — Codex CLI 비대화형 실호출 래퍼 (cross-verify·codex 스킬 공용)
#
# 목적: 실제 Codex CLI(codex exec)를 호출하고 "실제로 실행되었다"는 증빙(meta JSON)을 남긴다.
#
# 사용:
#   pwsh -NoProfile -File invoke-codex.ps1 -PromptFile <프롬프트.md> -OutFile <응답저장.md> [-TimeoutSec 540] [-Cd <작업루트>] [-Sandbox read-only]
#
# 결과:
#   - OutFile             : Codex 최종 응답 (codex exec -o)
#   - OutFile.log         : 이벤트 스트림(stdout, --json JSONL) — thread_id·토큰 사용량·오류 이벤트
#   - OutFile.err         : stderr
#   - OutFile.meta.json   : { status, exit_code, duration_sec, out_bytes, out_sha256, prompt_sha256, log_bytes,
#                             thread_id, codex_version, cmd, started_at, finished_at, err_tail, sandbox, attempt_id }
#     status = success | timeout | error | empty-output | quota
#   - exit code           : success=0, 그 외=1
#
# 설계 규칙 (plan/harness_cross_check_0913.md cross 절 반영):
#   - 상태는 정확히 한 번만 기록한다. 실패 경로에서 Write-Error 를 쓰지 않는다
#     ($ErrorActionPreference='Stop' 아래에서 Write-Error 가 예외로 승격돼 catch 가 meta 를 덮어쓰는 결함 제거).
#   - 시도마다 .log/.err 를 새로 만든다 (이전 quota 로그로 새 실패를 오분류하지 않기 위해).
#   - quota 판별은 .err 와 .log 양쪽의 오류 줄(ERROR/error: 접두)만 본다.
#   - 경로는 전부 절대 경로로 정규화하고 Start-Process 인자는 개별 인용한다 (공백 경로 안전).
#   - 툴 타임아웃(600s) 안에서 끝나도록 TimeoutSec 기본 540, 최대 570 으로 제한한다.

param(
    [Parameter(Mandatory = $true)][string]$PromptFile,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [int]$TimeoutSec = 540,
    [string]$Cd = "",
    [ValidateSet('read-only', 'workspace-write')][string]$Sandbox = 'read-only'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
if ($TimeoutSec -gt 570) { $TimeoutSec = 570 }   # PowerShell/Bash 툴 상한 600s 안에서 meta 를 쓸 여유 확보

# 작업 루트: CLAUDE_PROJECT_DIR 우선, 없으면 <repo>/.claude/skills/cross-verify/scripts 의 4단계 상위
if (-not $Cd) {
    $Cd = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path }
}
$Cd         = [IO.Path]::GetFullPath($Cd)
$PromptFile = [IO.Path]::GetFullPath($PromptFile)
$OutFile    = [IO.Path]::GetFullPath($OutFile)
$metaFile   = "$OutFile.meta.json"
$logFile    = "$OutFile.log"
$errFile    = "$OutFile.err"
$startedAt  = Get-Date
$attemptId  = [guid]::NewGuid().ToString('N').Substring(0, 12)
$script:metaWritten = $false

function Get-Sha256([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-Tail([string]$Path, [int]$N = 5) {
    if (-not (Test-Path $Path)) { return '' }
    return ((Get-Content $Path -Tail $N -Encoding UTF8) -join ' | ')
}
function Get-ThreadId {
    if (-not (Test-Path $logFile)) { return $null }
    foreach ($line in (Get-Content $logFile -Encoding UTF8 -TotalCount 50)) {
        if ($line -match '"thread_id"\s*:\s*"([^"]+)"') { return $Matches[1] }
    }
    return $null
}
function Test-QuotaError {
    # 오류 줄만 검사한다 (프롬프트 에코·정상 문장 속 'limit' 오탐 방지)
    $lines = @()
    foreach ($f in @($errFile, $logFile)) { if (Test-Path $f) { $lines += Get-Content $f -Tail 40 -Encoding UTF8 } }
    foreach ($l in $lines) {
        if ($l -match '(?i)(ERROR|error"?\s*:|"type"\s*:\s*"error")' -and
            $l -match "(?i)rate[ _-]?limit|usage[ _-]?limit|hit your (usage|limit)|too many requests|quota|\b429\b|insufficient[ _-]?(credits|quota)|usage cap|limit reached|purchase more credits") { return $true }
    }
    return $false
}
function Write-Meta([string]$status, [int]$exitCode, [string[]]$cmd) {
    if ($script:metaWritten) { return }          # 상태는 한 번만 확정
    $script:metaWritten = $true
    [ordered]@{
        status        = $status
        exit_code     = $exitCode
        duration_sec  = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
        out_file      = $OutFile
        out_bytes     = (Test-Path $OutFile) ? (Get-Item $OutFile).Length : 0
        out_sha256    = Get-Sha256 $OutFile
        prompt_file   = $PromptFile
        prompt_sha256 = Get-Sha256 $PromptFile
        log_file      = $logFile
        log_bytes     = (Test-Path $logFile) ? (Get-Item $logFile).Length : 0
        thread_id     = Get-ThreadId
        codex_version = $script:codexVersion
        cmd           = $cmd
        cd            = $Cd
        sandbox       = $Sandbox
        attempt_id    = $attemptId
        started_at    = $startedAt.ToString('o')
        finished_at   = (Get-Date).ToString('o')
        err_tail      = Get-Tail $errFile 5
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $metaFile -Encoding utf8NoBOM
}
function Fail([string]$status, [int]$exitCode, [string[]]$cmd, [string]$message) {
    Write-Meta $status $exitCode $cmd
    [Console]::Error.WriteLine("invoke-codex: [$status] $message")
    exit 1
}

if (-not (Test-Path $PromptFile)) { Fail 'error' -1 @() "프롬프트 파일 없음: $PromptFile" }

$codex = (Get-Command codex.cmd -ErrorAction SilentlyContinue) ?? (Get-Command codex -CommandType Application -ErrorAction SilentlyContinue)
if (-not $codex) { Fail 'error' -1 @() "codex CLI 를 PATH 에서 찾을 수 없음 — npm install -g @openai/codex" }
try { $script:codexVersion = ((& $codex.Source --version 2>$null) -join '').Trim() } catch { $script:codexVersion = $null }

# 시도별 산출물 초기화 (이전 실행 잔재로 인한 오판 방지)
Remove-Item -Path $OutFile, $metaFile, $logFile, $errFile -ErrorAction SilentlyContinue

$cmdArgs = @('exec', '-s', $Sandbox, '-C', $Cd, '--color', 'never', '--json', '-o', $OutFile, '-')
# Start-Process -ArgumentList 는 배열 요소를 인용하지 않으므로 공백 포함 인자를 직접 인용한다
$quoted = $cmdArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }

try {
    $proc = Start-Process -FilePath $codex.Source -ArgumentList $quoted -WorkingDirectory $Cd `
        -RedirectStandardInput $PromptFile -RedirectStandardOutput $logFile -RedirectStandardError $errFile `
        -NoNewWindow -PassThru

    if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
        try { $proc.Kill($true) } catch { }
        Fail 'timeout' -1 $cmdArgs "Codex 호출 타임아웃 (${TimeoutSec}s) — 검증 통과 아님"
    }
    $code = $proc.ExitCode
    $isQuota = Test-QuotaError

    if ($code -ne 0) {
        Fail ($isQuota ? 'quota' : 'error') $code $cmdArgs "Codex 호출 실패 ($($isQuota ? '토큰/사용량 한도' : 'error'), exit $code): $(Get-Tail $errFile 5)"
    }
    if (-not (Test-Path $OutFile) -or (Get-Item $OutFile).Length -eq 0) {
        Fail ($isQuota ? 'quota' : 'empty-output') $code $cmdArgs "Codex 가 응답 파일을 생성하지 않음$($isQuota ? ' (토큰/사용량 한도)' : '')"
    }

    Write-Meta 'success' $code $cmdArgs
    Write-Output "OK: $OutFile ($((Get-Item $OutFile).Length) bytes, $([math]::Round(((Get-Date)-$startedAt).TotalSeconds,1))s, thread=$(Get-ThreadId))"
    exit 0
}
catch {
    if (-not $script:metaWritten) { Write-Meta ((Test-QuotaError) ? 'quota' : 'error') -1 $cmdArgs }
    [Console]::Error.WriteLine("invoke-codex: [exception] $($_.Exception.Message)")
    exit 1
}
