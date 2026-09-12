# auto-commit.ps1 — Claude Code Stop 훅
# 메인 세션이 .git/auto_commit_msg.txt 에 남긴 WHY 커밋 메시지로 커밋 & 푸시한다.
# 설계 원칙 (plan/harness_cross_check_0913.md Git 절 반영):
#   - 항상 exit 0. 결과는 stdout JSON {"systemMessage": ...} 로만 알린다 (Stop 차단 없음).
#   - 메시지 파일은 최상단에서 1회 소비한다 (어떤 조기 종료 경로에서도 잔류하지 않음).
#     커밋 실패 시에는 .git/auto_commit_msg.failed.txt 로 보존해 다음 턴이 재사용할 수 있게 한다.
#   - .git/harness_commit_in_progress 센티널이 있으면 커밋을 건너뛴다
#     (commitandpush·cross-verify 가 사용자 확인으로 턴을 끝내야 할 때 만든다. 6시간 지나면 stale 로 간주).
#   - .git/auto-commit.lock 디렉터리로 동시 실행을 배제한다 (asyncRewake 훅 중첩 방지).
#   - 민감 파일 검사는 파일별로 수행하고 .example/.sample/.template 예외는 그 파일에만 적용한다.
#   - 스테이지된 diff 의 추가 줄을 내용 패턴으로 스캔한다 (개인키·클라우드 키·JSON/연결문자열 비밀값).
#   - 모든 git 명령의 종료 코드를 검사하고 push 실패를 숨기지 않는다.

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$env:GIT_TERMINAL_PROMPT = '0'

# 저장소 루트: CLAUDE_PROJECT_DIR 우선, 없으면 scripts/ 의 부모 (절대 경로 하드코딩 금지)
$repo = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { Split-Path -Parent $PSScriptRoot }
$gitDir = Join-Path $repo '.git'
if (-not (Test-Path $gitDir)) { exit 0 }

function Out-Sys([string]$Message) {
    Write-Output (@{ systemMessage = $Message } | ConvertTo-Json -Compress)
}
$script:gitExit = 0
function Invoke-Git {
    # 반환: 출력 줄 배열. 종료 코드는 $script:gitExit 에 저장.
    $out = & git -C $repo -c core.quotepath=false @args 2>&1
    $script:gitExit = $LASTEXITCODE
    return @($out | ForEach-Object { "$_" })
}

# ---- 0. 동시 실행 배제 ----------------------------------------------------------
$lockDir = Join-Path $gitDir 'auto-commit.lock'
try {
    New-Item -ItemType Directory -Path $lockDir -ErrorAction Stop | Out-Null
} catch {
    $ageMin = ((Get-Date) - (Get-Item $lockDir).LastWriteTime).TotalMinutes
    if ($ageMin -lt 10) { exit 0 }                     # 다른 훅 인스턴스가 실행 중
    Remove-Item $lockDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $lockDir -ErrorAction SilentlyContinue | Out-Null
}

$msgFile    = Join-Path $gitDir 'auto_commit_msg.txt'
$failedFile = Join-Path $gitDir 'auto_commit_msg.failed.txt'
$sentinel   = Join-Path $gitDir 'harness_commit_in_progress'
$commitMsg  = ''

try {
    # ---- 1. 메시지 파일 선소비 (모든 경로에서 잔류 방지) ------------------------------
    if (Test-Path $msgFile) {
        $commitMsg = (Get-Content $msgFile -Raw -Encoding UTF8).Trim().TrimStart([char]0xFEFF)
        Remove-Item $msgFile -Force -ErrorAction SilentlyContinue
    } elseif (Test-Path $failedFile) {
        # 직전 턴 커밋 실패로 보존된 메시지가 있으면 재사용
        $commitMsg = (Get-Content $failedFile -Raw -Encoding UTF8).Trim().TrimStart([char]0xFEFF)
        Remove-Item $failedFile -Force -ErrorAction SilentlyContinue
    }

    # ---- 2. 하네스 진행 중 센티널 -------------------------------------------------------
    if (Test-Path $sentinel) {
        $ageH = ((Get-Date) - (Get-Item $sentinel).LastWriteTime).TotalHours
        if ($ageH -lt 6) {
            if ($commitMsg) { Set-Content -Path $failedFile -Value $commitMsg -Encoding utf8NoBOM }
            Out-Sys "커밋 파이프라인(commitandpush/cross-verify) 진행 중 — 자동 커밋을 건너뜀. 파이프라인이 끝나면 센티널 .git/harness_commit_in_progress 가 제거됩니다."
            exit 0
        }
        Remove-Item $sentinel -Force -ErrorAction SilentlyContinue   # stale
    }

    # ---- 3. 변경 확인 ------------------------------------------------------------------
    $status = Invoke-Git status --porcelain
    if ($script:gitExit -ne 0) { Out-Sys "자동 커밋 생략: git status 실패 — $($status -join ' ')"; exit 0 }
    $status = @($status | Where-Object { $_ -and $_.Length -gt 3 })
    if ($status.Count -eq 0) { exit 0 }

    # ---- 4. 파일명 기반 민감 파일 검사 (파일별, 예외는 해당 파일에만) -------------------------
    $namePattern = '(?i)(^|/)\.env(\.[^/]+)?$|\.(pem|key|p12|pfx|jks|ppk)$|(^|/)id_(rsa|ed25519|ecdsa|dsa)$|(^|/)(credentials|secrets)(\.(json|ya?ml))?$|(^|/)service-account\.json$|(^|/)appsettings\.(Production|Staging)\.json$'
    $exemptPattern = '(?i)\.(example|sample|template)$'
    $sensitive = @()
    foreach ($line in $status) {
        $path = $line.Substring(3).Trim()
        if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1] }
        $path = $path.Trim('"')
        if ($path -match $exemptPattern) { continue }
        if ($path -match $namePattern) { $sensitive += $path }
    }
    if ($sensitive.Count -gt 0) {
        if ($commitMsg) { Set-Content -Path $failedFile -Value $commitMsg -Encoding utf8NoBOM }
        Out-Sys ("⚠️ 민감 파일 감지 — 자동 커밋 생략: " + ($sensitive -join ', ') + " . /commitandpush 로 검토 후 처리하세요.")
        exit 0
    }

    # ---- 5. 대용량 파일 가드 (GitHub 100MB 거부 → 이후 매 턴 push 무음 실패 방지) ---------------
    $tooBig = @()
    foreach ($line in $status) {
        $path = $line.Substring(3).Trim().Trim('"'); if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1].Trim('"') }
        $full = Join-Path $repo $path
        if ((Test-Path $full -PathType Leaf) -and ((Get-Item $full).Length -gt 50MB)) { $tooBig += $path }
    }
    if ($tooBig.Count -gt 0) {
        if ($commitMsg) { Set-Content -Path $failedFile -Value $commitMsg -Encoding utf8NoBOM }
        Out-Sys ("⚠️ 50MB 초과 파일 감지 — 자동 커밋 생략: " + ($tooBig -join ', ') + " . .gitignore 추가 또는 Git LFS 를 검토하세요.")
        exit 0
    }

    # ---- 6. 전체 스테이지 --------------------------------------------------------------
    $addOut = Invoke-Git add -A
    if ($script:gitExit -ne 0) {
        if ($commitMsg) { Set-Content -Path $failedFile -Value $commitMsg -Encoding utf8NoBOM }
        Out-Sys "자동 커밋 생략: git add 실패 — $($addOut -join ' ')"; exit 0
    }
    $staged = @(Invoke-Git diff --staged --name-only | Where-Object { $_ })
    if ($staged.Count -eq 0) { exit 0 }

    # ---- 7. 스테이지된 내용 스캔 (추가 줄만) ---------------------------------------------------
    $diff = Invoke-Git diff --staged -U0 --diff-filter=AM --no-color
    $contentPatterns = @(
        '-----BEGIN [A-Z ]*PRIVATE KEY',
        'AKIA[0-9A-Z]{16}', 'ASIA[0-9A-Z]{16}',
        'ghp_[0-9A-Za-z]{36}', 'gho_[0-9A-Za-z]{36}', 'github_pat_[0-9A-Za-z_]{40,}', 'glpat-[0-9A-Za-z\-]{20}',
        'sk_(live|test)_[0-9A-Za-z]{24}', 'xox[baprs]-[0-9A-Za-z\-]{10,}', 'AIza[0-9A-Za-z\-_]{35}',
        'eyJ[A-Za-z0-9\-_]{10,}\.eyJ[A-Za-z0-9\-_]{10,}\.[A-Za-z0-9\-_]{10,}',
        '(?i)["'']?(password|passwd|pwd|secret|client[_-]?secret|api[_-]?key|apikey|access[_-]?token|auth[_-]?token|private[_-]?key)["'']?\s*[=:]\s*["''][^"''${}<>%]{8,}["'']',
        '(?i)(Password|Pwd|AccountKey|SharedAccessKey)=[^;"''\s]{6,}',
        '[a-z][a-z0-9+.\-]*://[^/\s:@"'']+:[^@\s"'']{4,}@'
    )
    $placeholder = '(?i)your[_-]?|replace|example|placeholder|changeme|dummy|sample|<[^>]+>|\*{3,}|x{6,}|\$\{|\$\(|%[A-Z_]+%'
    $hits = @(); $file = ''
    foreach ($l in $diff) {
        if ($l -match '^\+\+\+ b/(.+)$') { $file = $Matches[1]; continue }
        if ($l -notmatch '^\+' -or $l -match '^\+\+\+') { continue }
        if ($file -match $exemptPattern) { continue }
        foreach ($p in $contentPatterns) {
            if ($l -match $p) {
                if ($l -match $placeholder) { break }
                $hits += "$file : " + $l.Substring(1).Trim().Substring(0, [Math]::Min(60, $l.Trim().Length - 1)); break
            }
        }
        if ($hits.Count -ge 5) { break }
    }
    if ($hits.Count -gt 0) {
        if ($commitMsg) { Set-Content -Path $failedFile -Value $commitMsg -Encoding utf8NoBOM }
        Out-Sys ("⚠️ 비밀값 의심 내용 감지 — 자동 커밋 생략(스테이지는 유지): " + ($hits -join ' | ') + " . 값을 제거하거나 /commitandpush 로 감사하세요.")
        exit 0
    }

    # ---- 8. 메시지 검증 / 폴백 (commit-msg 훅과 동일 정규식) ---------------------------------
    $trailer = 'Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>'
    if (-not $commitMsg -or $commitMsg -notmatch '^(추가|수정|버그수정|리팩토링|문서|테스트|의존성): \S') {
        $added  = @(Invoke-Git diff --staged --name-only --diff-filter=A | Where-Object { $_ })
        $prefix =
            if (@($staged | Where-Object { $_ -notmatch '\.(md|txt)$' }).Count -eq 0) { '문서' }
            elseif (@($staged | Where-Object { $_ -notmatch '(^|/)[^/]*Tests?(/|\.cs$)' }).Count -eq 0) { '테스트' }
            elseif (@($staged | Where-Object { $_ -notmatch '\.(csproj|props|targets|sln|lock\.json)$' }).Count -eq 0) { '의존성' }
            elseif ($added.Count -eq $staged.Count) { '추가' }
            else { '수정' }
        $commitMsg = "${prefix}: 자동 커밋(메시지 미전달) — $($staged.Count)개 파일 변경`n`n$trailer"
    } elseif ($commitMsg -notmatch [regex]::Escape($trailer)) {
        $commitMsg = "$commitMsg`n`n$trailer"
    }

    # ---- 9. 커밋 (-F, UTF-8 무BOM) -----------------------------------------------------------
    $tmpMsg = Join-Path $gitDir 'auto_commit_msg.tmp'
    Set-Content -Path $tmpMsg -Value $commitMsg -Encoding utf8NoBOM
    $commitOut = Invoke-Git commit -F $tmpMsg
    Remove-Item $tmpMsg -Force -ErrorAction SilentlyContinue
    if ($script:gitExit -ne 0) {
        Set-Content -Path $failedFile -Value $commitMsg -Encoding utf8NoBOM
        Out-Sys ("❌ 자동 커밋 실패(메시지는 .git/auto_commit_msg.failed.txt 에 보존): " + (($commitOut | Select-Object -Last 6) -join ' / '))
        exit 0
    }
    $sha = (Invoke-Git rev-parse --short HEAD) -join ''

    # ---- 10. 푸시 (원격 있을 때, upstream 없으면 1회 설정 시도) -----------------------------------
    $remotes = @(Invoke-Git remote | Where-Object { $_ })
    if ($remotes.Count -eq 0) { exit 0 }
    $branch = (Invoke-Git branch --show-current) -join ''
    $pushOut = Invoke-Git push
    if ($script:gitExit -ne 0 -and (($pushOut -join ' ') -match 'no upstream|has no upstream|set-upstream')) {
        $pushOut = Invoke-Git push --set-upstream $remotes[0] $branch
    }
    if ($script:gitExit -ne 0) {
        Out-Sys ("⚠️ 커밋 $sha 은 성공했지만 push 실패: " + (($pushOut | Select-Object -Last 4) -join ' / ') + " . 다음 턴에 재시도되며, 인증·충돌이면 수동 처리가 필요합니다.")
    }
    exit 0
}
finally {
    Remove-Item $lockDir -Recurse -Force -ErrorAction SilentlyContinue
}
