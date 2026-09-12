#Requires -Version 7.0
<#
.SYNOPSIS
  Claude Code 하네스(.claude/agents, .claude/skills, .agents/skills 미러) 구조 검증 스크립트.

.DESCRIPTION
  저장소 루트는 $PSScriptRoot 기준으로 계산한다 (절대 경로 하드코딩 금지 규칙).
  검사 항목:
    1. 에이전트 프론트매터 존재 + name == 파일명
    2. 에이전트·스킬이 참조하는 `/스킬명` → .claude/skills/<명>/SKILL.md 실존
    3. 스킬이 스폰하는 에이전트(subagent_type= / agent_type: / Agent(<name>, 세 표기) → .claude/agents/<명>.md 실존
    4. 절대 경로(E:\ , E:/) 하드코딩 없음
    5. 이 빌드에 없는 팀 도구(TeamCreate/TaskCreate/TaskGet/TeamDelete) 참조 없음
    6. Codex 미러 = .claude/skills − {cross-verify, codex}, 공통 파일 내용 동일(commitandpush 서명 줄 예외)
    7. Co-Authored-By 서명 일관성 (.claude 측 = Claude, .agents 측 = Codex)
    8. 쓰기 범위 훅: guard-write-scope.ps1 AgentMap 과 감사·리뷰 에이전트 프론트매터 PreToolUse 훅 일치, settings.json map 모드 훅 존재
  결과: 항목별 PASS/FAIL 표. FAIL이 하나라도 있으면 exit 1.

.EXAMPLE
  pwsh scripts/harness-audit.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$AgentsDir = Join-Path $Root '.claude/agents'
$SkillsDir = Join-Path $Root '.claude/skills'
$MirrorDir = Join-Path $Root '.agents/skills'
$MirrorExcluded = @('cross-verify', 'codex')

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$Check, [bool]$Pass, [string[]]$Details) {
    $results.Add([pscustomobject]@{ Check = $Check; Result = $(if ($Pass) { 'PASS' } else { 'FAIL' }); Details = ($Details -join '; ') })
}
function Get-Frontmatter([string]$Path) {
    $text = Get-Content -LiteralPath $Path -Raw
    if (-not $text.StartsWith('---')) { return $null }
    $end = $text.IndexOf("`n---", 3)
    if ($end -lt 0) { return $null }
    $fm = @{}
    foreach ($line in ($text.Substring(3, $end - 3) -split "`n")) {
        if ($line -match '^\s*([A-Za-z_]+):\s*(.*)$') { $fm[$Matches[1]] = $Matches[2].Trim().Trim('"') }
    }
    return $fm
}

$agentFiles = Get-ChildItem -LiteralPath $AgentsDir -Filter '*.md'
$skillDirs = Get-ChildItem -LiteralPath $SkillsDir -Directory
$agentNames = $agentFiles | ForEach-Object { $_.BaseName }
$skillNames = $skillDirs | ForEach-Object { $_.Name }

# ---- 1. 에이전트 프론트매터 -----------------------------------------------------
$bad = @()
foreach ($f in $agentFiles) {
    $fm = Get-Frontmatter $f.FullName
    if ($null -eq $fm) { $bad += "$($f.Name): 프론트매터 없음"; continue }
    if (-not $fm.ContainsKey('name')) { $bad += "$($f.Name): name 없음"; continue }
    if ($fm['name'] -ne $f.BaseName) { $bad += "$($f.Name): name='$($fm['name'])' != 파일명" }
    if (-not $fm.ContainsKey('description')) { $bad += "$($f.Name): description 없음" }
}
Add-Result "1. 에이전트 프론트매터 ($($agentFiles.Count)개)" ($bad.Count -eq 0) $bad

# ---- 2. 스킬 참조 실존 ----------------------------------------------------------
$bad = @()
$allDocs = @($agentFiles) + @(Get-ChildItem -LiteralPath $SkillsDir -Recurse -Filter 'SKILL.md')
foreach ($f in $allDocs) {
    $text = Get-Content -LiteralPath $f.FullName -Raw
    foreach ($m in [regex]::Matches($text, '(?<![\w/])/([a-z][a-z0-9-]+)\s*스킬')) {
        $name = $m.Groups[1].Value
        if ($name -in @('harness-evolve') -or $skillNames -contains $name) { continue }
        $bad += "$($f.Name) → /$name 스킬 없음"
    }
}
Add-Result "2. 참조 스킬 실존" ($bad.Count -eq 0) ($bad | Select-Object -Unique)

# ---- 3. 스폰 에이전트 실존 ------------------------------------------------------
$bad = @()
$spawnPatterns = @(
    'subagent_type\s*[=:]\s*"([a-z][a-z0-9-]+)"',
    'agent_type\s*:\s*"([a-z][a-z0-9-]+)"',
    'Agent\(\s*([a-z][a-z0-9-]+)\s*,'
)
foreach ($f in $allDocs) {
    $text = Get-Content -LiteralPath $f.FullName -Raw
    foreach ($p in $spawnPatterns) {
        foreach ($m in [regex]::Matches($text, $p)) {
            $name = $m.Groups[1].Value
            if ($name -in @('general-purpose', 'Explore', 'Plan') -or $agentNames -contains $name) { continue }
            $bad += "$($f.Directory.Name)/$($f.Name) → 에이전트 '$name' 없음"
        }
    }
}
Add-Result "3. 스폰 에이전트 실존" ($bad.Count -eq 0) ($bad | Select-Object -Unique)

# ---- 4. 절대 경로 하드코딩 ------------------------------------------------------
$scanDirs = @($AgentsDir, $SkillsDir, $MirrorDir, (Join-Path $Root 'scripts'), (Join-Path $Root '.claude/settings.json'))
$hits = Get-ChildItem -LiteralPath $scanDirs -Recurse -File -Include '*.md', '*.ps1', '*.json', '*.sh' |
    Where-Object { $_.Name -ne 'harness-audit.ps1' } |
    Select-String -Pattern '[A-Za-z]:[\\/]project[\\/]' |
    ForEach-Object { "$($_.Filename):$($_.LineNumber)" }
Add-Result "4. 절대 경로 하드코딩 없음" ($hits.Count -eq 0) $hits

# ---- 5. 미존재 팀 도구 참조 -----------------------------------------------------
$hits = Get-ChildItem -LiteralPath $AgentsDir, $SkillsDir, $MirrorDir -Recurse -File -Filter '*.md' |
    Select-String -Pattern '\b(TeamCreate|TaskCreate|TaskGet|TeamDelete)\b' |
    Where-Object { $_.Line -notmatch '이 빌드에는' -and $_.Line -notmatch '팀 도구가 없다' } |
    ForEach-Object { "$($_.Filename):$($_.LineNumber)" }
Add-Result "5. 미존재 팀 도구 참조 없음" ($hits.Count -eq 0) $hits

# ---- 6. Codex 미러 동기화 -------------------------------------------------------
$bad = @()
$expected = $skillNames | Where-Object { $_ -notin $MirrorExcluded } | Sort-Object
$actual = (Get-ChildItem -LiteralPath $MirrorDir -Directory | ForEach-Object { $_.Name }) | Sort-Object
$diff = Compare-Object $expected $actual
foreach ($d in $diff) {
    $side = if ($d.SideIndicator -eq '<=') { '미러에 없음' } else { '미러에만 있음(제외 대상 포함)' }
    $bad += "$($d.InputObject): $side"
}
foreach ($name in $expected) {
    $src = Join-Path $SkillsDir $name
    $dst = Join-Path $MirrorDir $name
    if (-not (Test-Path -LiteralPath $dst)) { continue }
    foreach ($sf in Get-ChildItem -LiteralPath $src -Recurse -File) {
        $rel = $sf.FullName.Substring($src.Length + 1)
        $df = Join-Path $dst $rel
        if (-not (Test-Path -LiteralPath $df)) { $bad += "$name/$rel 미러 누락"; continue }
        $a = (Get-Content -LiteralPath $sf.FullName -Raw) -replace 'Co-Authored-By: .*', ''
        $b = (Get-Content -LiteralPath $df -Raw) -replace 'Co-Authored-By: .*', ''
        if ($a -ne $b) { $bad += "$name/$rel 내용 상이" }
    }
}
# Claude 전용 에이전트(Codex 재호출·교차 검증 역할)는 .codex/agents 에도 없어야 한다 (재귀 방지)
$codexAgentsDir = Join-Path $Root '.codex/agents'
foreach ($n in @('codex-adapter', 'cross-planner', 'cross-reviewer', 'cross-implementer')) {
    if (Test-Path -LiteralPath (Join-Path $codexAgentsDir "$n.toml")) { $bad += ".codex/agents/$n.toml: Claude 전용 에이전트가 Codex 측에 노출됨" }
}
Add-Result "6. Codex 미러 동기화 (제외: $($MirrorExcluded -join ', '); .codex/agents 재귀 검사 포함)" ($bad.Count -eq 0) $bad

# ---- 7. 서명 일관성 -------------------------------------------------------------
$bad = @()
$claudeSig = Get-ChildItem -LiteralPath $AgentsDir, $SkillsDir, (Join-Path $Root 'scripts'), (Join-Path $Root 'CLAUDE.md') -Recurse -File -Include '*.md', '*.ps1' |
    Where-Object { $_.Name -ne 'harness-audit.ps1' } |
    Select-String -Pattern 'Co-Authored-By:' | Where-Object { $_.Line -notmatch 'Claude Fable 5\.1 <noreply@anthropic\.com>' -and $_.Line -notmatch 'Codex <noreply@openai\.com>' }
foreach ($h in $claudeSig) { $bad += ".claude 측 비표준 서명 $($h.Filename):$($h.LineNumber)" }
$codexSig = Get-ChildItem -LiteralPath $MirrorDir, (Join-Path $Root 'AGENTS.md') -Recurse -File -Filter '*.md' |
    Select-String -Pattern 'Co-Authored-By:' | Where-Object { $_.Line -notmatch 'Codex <noreply@openai\.com>' -and $_.Line -notmatch 'Claude' }
foreach ($h in $codexSig) { $bad += "미러 측 비표준 서명 $($h.Filename):$($h.LineNumber)" }
$mixed = Get-ChildItem -LiteralPath $MirrorDir -Recurse -File -Filter '*.md' | Select-String -Pattern 'Codex .*<noreply@anthropic\.com>'
foreach ($h in $mixed) { $bad += "잡종 서명 $($h.Filename):$($h.LineNumber)" }
Add-Result "7. Co-Authored-By 서명 일관성" ($bad.Count -eq 0) $bad

# ---- 8. 쓰기 범위 훅 (감사·리뷰 전용 에이전트) ---------------------------------------
# guard-write-scope.ps1 의 $AgentMap 과 에이전트 프론트매터 훅이 일치해야 한다. 구현 역할(cross-implementer)은 훅이 없어야 한다.
$bad = @()
$guard = Join-Path $Root 'scripts/hooks/guard-write-scope.ps1'
if (-not (Test-Path -LiteralPath $guard)) { $bad += 'scripts/hooks/guard-write-scope.ps1 없음' }
else {
    $guardText = Get-Content -LiteralPath $guard -Raw
    $map = @{}
    foreach ($m in [regex]::Matches($guardText, "'([a-z][a-z0-9-]+)'\s*=\s*'(_workspace/[a-z-]+/)'")) { $map[$m.Groups[1].Value] = $m.Groups[2].Value }
    foreach ($f in $agentFiles) {
        $text = Get-Content -LiteralPath $f.FullName -Raw
        $end = $text.IndexOf("`n---", 3); $fmText = if ($end -gt 0) { $text.Substring(0, $end) } else { '' }
        $hookMatch = [regex]::Match($fmText, 'guard-write-scope\.ps1 -Allow (\S+)')
        if ($map.ContainsKey($f.BaseName)) {
            if (-not $hookMatch.Success) { $bad += "$($f.Name): 프론트매터 쓰기 범위 훅 없음(기대 $($map[$f.BaseName]))" }
            elseif ($hookMatch.Groups[1].Value.Trim('"') -ne $map[$f.BaseName]) { $bad += "$($f.Name): 훅 접두사 $($hookMatch.Groups[1].Value) != AgentMap $($map[$f.BaseName])" }
            if ($fmText -notmatch '(?m)^hooks:\s*$' -or $fmText -notmatch '(?m)^\s+PreToolUse:\s*$') { $bad += "$($f.Name): hooks/PreToolUse 키 구조 오류" }
        } elseif ($hookMatch.Success) { $bad += "$($f.Name): AgentMap 에 없는데 훅 선언됨" }
    }
    foreach ($n in $map.Keys) { if ($agentNames -notcontains $n) { $bad += "AgentMap '$n' 에 해당하는 에이전트 파일 없음" } }
    $settings = Get-Content -LiteralPath (Join-Path $Root '.claude/settings.json') -Raw
    if ($settings -notmatch 'guard-write-scope\.ps1 -Mode map') { $bad += 'settings.json 에 PreToolUse map 모드 훅 없음' }
}
Add-Result "8. 쓰기 범위 훅 (AgentMap $($map.Count)개 ↔ 프론트매터)" ($bad.Count -eq 0) $bad

# ---- 출력 ----------------------------------------------------------------------
$results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
if ($failed -gt 0) { Write-Host "하네스 감사 FAIL: $failed 항목" -ForegroundColor Red; exit 1 }
Write-Host "하네스 감사 PASS: $($results.Count)/$($results.Count) 항목" -ForegroundColor Green
exit 0
