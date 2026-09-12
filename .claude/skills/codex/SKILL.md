---
name: codex
description: >
  Claude Code 세션 안에서 OpenAI Codex CLI(`codex exec`)를 세컨드 오피니언·교차 검증·병렬 작업자로
  호출하는 스킬. 트리거: 'codex', '코덱스', 'codex에게 물어봐', 'codex 의견', '세컨드 오피니언',
  'codex 리뷰', 'codex로 검증', '교차 리뷰', 'codex한테 시켜'. 후속: 'codex 이어서', 'codex 다시'.
  기본은 read-only 샌드박스로 안전하게 실행하고, 파일 수정 위임은 사용자 확인 후 workspace-write로 실행한다.
---

# Codex 호출 스킬 (Claude ↔ Codex 협업)

## 전제

- `codex` CLI(v0.154+)가 전역 설치되어 있고 `~/.codex/auth.json`으로 ChatGPT 로그인 상태다. 확인: `codex login status`
- Codex는 이 리포의 `AGENTS.md`(프로젝트 규칙)와 `.agents/skills/`(스킬 미러)를 자동으로 읽는다.
- **이 환경에서 프롬프트를 argv로 넘기면 멈춘다.** Claude의 Bash/PowerShell 툴은 stdin이 닫히지 않은 파이프라, `codex exec "질문"`은 "Reading additional input from stdin..."에서 툴 타임아웃까지 대기한다(2026-09-12 실측). **프롬프트는 항상 파일로 쓰고 stdin(`-`)으로 넘긴다.**

## 호출 패턴

### 1) 세컨드 오피니언 / 질문 (기본, 읽기 전용) — 래퍼 사용 권장

```powershell
# 프롬프트를 UTF-8 파일로 저장 (Write 도구 또는 Set-Content -Encoding utf8NoBOM)
pwsh -NoProfile -File .claude/skills/cross-verify/scripts/invoke-codex.ps1 `
  -PromptFile "$env:TEMP\codex_prompt.md" -OutFile "$env:TEMP\codex_out.md" -TimeoutSec 540
# 결과: codex_out.md (응답), codex_out.md.meta.json (status·thread_id·out_sha256 증빙)
```
래퍼는 stdin 전달·read-only 샌드박스·`-o` 수집·증빙 meta를 모두 처리한다. 툴 `timeout`은 600000ms로 두고 `-TimeoutSec`은 540 이하로 유지한다(래퍼가 570으로 상한).

직접 호출이 필요할 때(Bash 툴):
```bash
codex exec -s read-only -o "$TMP/codex_out.md" - < "$TMP/codex_prompt.md"
```
PowerShell 툴에서는 `<` 리다이렉션이 없으므로 `Get-Content prompt.md -Raw | codex exec -s read-only -o out.md -` 를 쓴다.

실행 후 `Read`로 출력 파일을 읽어 사용자에게 요약 전달. **Claude 자신의 의견과 Codex 의견이 갈리는 지점을 명시**할 것. 인용할 때는 "Codex 의견"임을 밝힌다.

### 2) 코드 리뷰 (Codex 내장 리뷰어)

`codex exec review`는 **변경분** 리뷰다. 대상을 명시하지 않으면 깨끗한 저장소에서 아무것도 검토하지 않는다.
```bash
codex exec review --uncommitted                # 미커밋 변경
codex exec review --base master                # 기준 브랜치 대비
codex exec review --commit <sha>               # 특정 커밋
```
저장소 전체 감사가 필요하면 1)의 방식으로 파일 범위를 명시한 프롬프트를 보낸다.

### 3) 편집 위임 (사용자가 명시적으로 요청한 경우만)

```powershell
pwsh -NoProfile -File .claude/skills/cross-verify/scripts/invoke-codex.ps1 `
  -PromptFile "$env:TEMP\codex_task.md" -OutFile "$env:TEMP\codex_out.md" -Sandbox workspace-write
```
프롬프트에 "절대 git commit 하지 말 것, `.git/auto_commit_msg.txt`·`.git/harness_commit_in_progress`를 만들지 말 것"을 반드시 포함한다. 완료 후 Claude가 `git diff`로 변경을 검토·요약하고 문제가 있으면 수정한다.

### 4) 직전 세션 이어가기

`resume --last`는 **현재 디렉터리의 가장 최근 세션**을 고르므로 cross-verify가 만든 다른 세션을 이어갈 수 있다. 최초 호출의 `thread_id`(meta.json에 기록됨)로 재개한다:
```bash
codex exec resume <thread_id> -s read-only -o "$TMP/codex_out2.md" - < "$TMP/followup.md"
```

## 실행 규칙

- **타임아웃:** 툴 `timeout` 최대 600000ms. 긴 작업은 `run_in_background`로 실행하고 완료 알림을 기다린다. 래퍼 `-TimeoutSec`은 540 이하.
- **사용량 한도:** 한도 초과 시 래퍼 meta `status=quota`(또는 직접 호출 시 stderr "usage limit"). 재시도하지 말고 해제 시각을 사용자에게 알린다. 병렬로 여러 건을 띄우면 전부 한도에 걸려 토큰만 소모하므로 **순차 실행**한다(2026-09-12 5건 병렬 전부 실패 사례).
- **커밋 금지:** 커밋은 Claude Code Stop 훅 책임. Codex 편집 위임 프롬프트에 항상 커밋 금지를 포함하고, 완료 후 Codex가 `.git/auto_commit_msg.txt`를 만들지 않았는지 확인한다(AGENTS.md에도 금지 규칙 명시).
- **위험 플래그 금지:** `--dangerously-bypass-approvals-and-sandbox` 사용 금지. 쓰기가 필요하면 `workspace-write`까지만.
- **결과 귀속:** Codex의 판단은 "Codex 의견"으로 표시하고 Claude가 검증한 결론과 구분해 보고한다. 래퍼 meta가 `success`가 아니면 그 출력은 인용하지 않는다.
