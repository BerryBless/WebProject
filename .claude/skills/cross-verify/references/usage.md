# Cross-Verify 설치·실행 가이드

## 사전 요구사항

| 항목 | 확인 명령 | 비고 |
|------|----------|------|
| Codex CLI | `codex --version` | `npm install -g @openai/codex` (v0.154 검증) |
| Codex 인증 | `codex login status` | "Logged in" 아니면 `codex login` |
| pwsh 7+ | `pwsh -v` | invoke-codex.ps1 실행 |
| .NET 10 SDK | `dotnet --version` | 테스트 실행 |

호출 스크립트 단독 점검 (프롬프트는 **파일 + stdin**으로만 전달한다. argv 전달은 이 환경에서 멈춘다):

```powershell
Set-Content $env:TEMP\p.md "Reply with exactly one line: the sum of 2 and 3 as a digit." -Encoding utf8NoBOM
pwsh -NoProfile -File .claude\skills\cross-verify\scripts\invoke-codex.ps1 -PromptFile $env:TEMP\p.md -OutFile $env:TEMP\o.md -TimeoutSec 120
Get-Content $env:TEMP\o.md; Get-Content $env:TEMP\o.md.meta.json
```

`status: "success"`, `thread_id`, `out_sha256`가 meta에 있으면 연동 정상 (2026-09-13 실측: 6초, `codex-cli 0.154.0`).

## 실행 방법

Claude Code 세션에서 **'코덱스' 또는 'Codex' 키워드를 포함해** 요청한다(키워드가 없으면 트리거되지 않는다):

```
/weatherforecast 응답에 GeneratedAt(UTC) 필드를 코덱스 교차 검증으로 추가해줘
```

부분 재실행:

```
코덱스 교차 검증 리뷰 단계만 다시
```

## 작은 변경으로 검증하는 사용 예시 (권장 첫 실행)

**요청:** "`WeatherForecast` 응답 개수를 설정값(`WeatherForecast:Days`, 기본 5)으로 바꾸는 걸 Codex랑 교차 검증으로 구현해줘"

파일 2~3개(Program.cs, appsettings.json, 테스트 1개) 규모면 10~20분 내 완주:

```
_workspace/cross/20260913_020000_forecast-days/
├── 00_context.md                        ← 요구사항·규칙·기준 커밋(사용자 확인 후 확정)
├── 00_manifest.json                     ← 단계별 입력 해시·채택 파일·Codex 호출 이력
├── 10_claude_plan.md
├── 10_codex_plan.md (+.meta.json/.log/.err)
├── 11_claude_check_of_codex_plan.md / 11_codex_check_of_claude_plan.md (+meta)
├── 12_plan_adjudication.md              ← 채택/기각/미해결
├── 13_final_plan.md
├── 14_claude_final_check.md / 14_codex_final_check.md   ← 마지막 줄 VERDICT
├── 20_impl_notes.md / 20_test_results.txt
├── 30_diff.patch                        ← _workspace/** 제외, 신규 파일 본문 포함
├── 30_claude_review.md / 30_codex_review.md (+meta)
├── 31_claude_adjudication.md / 31_codex_adjudication.md / 31_review_adjudication.md
├── 32_fix_notes.md / 32_test_results.txt   (유효 지적이 있었을 때만)
├── 33_diff.patch                        ← 수정 후 새 diff (수정 없음이면 30_diff.patch 재사용 명시)
├── 33_claude_reverify.md / 33_codex_reverify.md
└── 90_final_report.md
```
라운드 2+는 같은 번호에 `_r2` 접미사가 붙고, 소비자는 항상 가장 높은 접미사를 받는다.

## 산출물 읽는 법

- **Codex가 진짜 실행됐는지:** `*codex*.md.meta.json`의 `status: "success"`, `thread_id`, `out_sha256`(파일 해시와 일치), `log_bytes > 0`. 오케스트레이터가 이 네 가지를 직접 재검증한 결과가 `00_manifest.json`의 `codex_calls`에 있다.
- **독립성이 지켜졌는지:** 1차 단계 `.log`에 `claude_plan|claude_review` 접근 흔적이 없어야 한다(있으면 해당 산출물 무효 표시).
- **의견이 어떻게 조정됐는지:** `12_plan_adjudication*.md`, `31_review_adjudication*.md`.
- **무엇이 검증되지 않았는지:** `90_final_report.md`의 "검증 범위의 한계", `DEGRADED.md`(quota 폴백 시).

## Stop 훅과의 상호작용

파이프라인은 시작 시 `.git/harness_commit_in_progress`를 만들어 Stop 훅의 자동 커밋을 잠근다. 사용자 확인으로 턴이 끝나도 반쯤 구현된 코드가 커밋되지 않는다. 완료 시 WHY 메시지를 `.git/auto_commit_msg.txt`에 남기고 센티널을 지운다. `.log/.err`는 gitignore, `.md/.json/.patch/.txt`는 추적된다.

## 한계

- Codex 호출은 ChatGPT 요금제 사용량을 소모한다. 여러 건을 병렬로 띄우면 한도에 같이 걸리므로 순차 실행한다.
- Codex는 read-only 샌드박스라 테스트를 실행하지 못한다. 테스트 근거는 항상 구현자의 `*_test_results*.txt`.
- 툴 타임아웃 600초 안에서 끝나야 하므로 래퍼 `-TimeoutSec`은 540 이하(최대 570). 큰 리뷰는 diff를 나눈다.
