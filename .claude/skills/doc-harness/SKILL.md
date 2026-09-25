---
name: doc-harness
description: "프로젝트 기술 문서를 다단계 Claude 파이프라인으로 생성하고 이후 변경분만 증분 갱신한다(docs/generated). 트리거(메시지 전체가 정확히 이 말일 때만): '문서화', '문서화 전체', '문서화 상태', '문서화 검증'. 되묻지 않고 모드를 자동 판정한다. 문장 속 '문서화'(예: '문서화 규칙 알려줘')에는 실행하지 않는다."
---

# 문서화 하네스 (doc-harness)

`doc-harness/`(TypeScript, Node 24)가 실제 실행 주체다. 이 스킬은 **사용자 한마디를 하네스 실행으로 옮기고 결과를 그대로 보고**하는 얇은 껍데기다. 프로덕션 코드는 절대 수정하지 않는다(하네스가 부르는 Claude 세션은 쓰기 도구가 없다). 설계: `docs/superpowers/specs/2026-09-23-doc-harness-design.md`.

## 명령 매핑

| 사용자 입력 | 실행 | 설명 |
|---|---|---|
| `문서화` | `npm run harness -- run --auto` | 최초면 INITIAL(전체), baseline이 있으면 INCREMENTAL(변경분), 변경이 없으면 "문서화 확인 완료"로 끝 |
| `문서화 전체` | `npm run harness -- run --full` | baseline과 무관하게 전체 재분석 |
| `문서화 상태` | `npm run harness -- status` | LLM 호출 없이 동기화 상태·변경 파일·예상 영향 기능 출력 |
| `문서화 검증` | `npm run harness -- verify` | 문서를 고치지 않고 Verification만 실행 |

무엇을 문서화할지 **되묻지 않는다.** 하네스가 Git·working tree·baseline을 보고 판정한다.

## 절차 (`문서화` / `문서화 전체`)

1. **사전 점검** — `doc-harness/node_modules`가 없으면 `cd doc-harness && npm ci`. `claude --version`이 동작하는지 확인(하네스는 `claude -p`를 자식 프로세스로 띄운다).
2. **세션 맥락 작성(있을 때만)** — 이 세션에서 직전에 기능을 구현하거나 버그를 고쳤다면 Git만으로 알기 어려운 정보를 `doc-harness/workspace/inbox/session_context.md`에 쓴다: 왜 구현했는지, 사용자가 요구한 동작, 처음 시도한 방식과 실패 증상·오류 메시지, 왜 다른 방식으로 바꿨는지, 사용자가 거부한 방법, 임시 workaround. 비밀값·키·연결 문자열은 절대 넣지 않는다. 직전 작업이 없으면 파일을 만들지 않는다. 하네스는 이 파일을 **최하위 근거**로만 쓰고, 소비 후 Run 디렉터리로 옮긴다.
3. **센티널** — `.git/harness_commit_in_progress`를 만들어(내용: 현재 시각) 실행 중 Stop 훅이 중간 상태를 커밋하지 못하게 한다.
4. **실행(분리 프로세스)** — Bash/PowerShell 도구는 명령 하나에 10분 상한이 있고 INITIAL은 1~3시간 걸리므로, 하네스를 **도구와 분리된 프로세스**로 띄운다. PowerShell 도구에서:
   ```powershell
   $log = "doc-harness/workspace/harness-run.log"
   Start-Process -FilePath "npm.cmd" -ArgumentList "run","harness","--","run","--auto" -WorkingDirectory "doc-harness" -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
   ```
   (전체는 `--auto` 대신 `--full`. `-FilePath "npm"`은 Windows에서 "올바른 Win32 응용 프로그램이 아닙니다"로 실패한다 — 반드시 `npm.cmd`.) 그다음 Monitor 도구로 `doc-harness/workspace/harness-run.log`를 `tail -f`해 `✓`·`검증`·`문서화 완료`·`문서화 실패`·`Error` 줄을 이벤트로 받는다(모니터는 30분마다 만료되므로 재무장). 그 사이 다른 작업을 하지 않는다. 증분(INCREMENTAL)은 보통 10~20분이다.
5. **보고** — 완료되면 `doc-harness/workspace/runs/<최신 run>/report.txt`를 읽어 **그대로** 출력한다(요약·재작성 금지). `status`가 FAILED면 리포트의 "남은 문제"를 보여 주고 baseline·기존 문서는 유지되었음을 알린다. 실패 원인이 일시적(타임아웃·한도)이면 `npm run harness -- resume`으로 이어갈 수 있다고 안내한다.
6. **정리** — 센티널을 지운다. 변경된 파일(`docs/generated/**`, `doc-harness/workspace/baseline.json`·`current/`·`depgraph.json`·`runs/*/run.json`)은 이 저장소의 커밋 규칙대로 커밋한다(메시지 접두사 `문서:`, 제목은 모드와 요지 — 예: `문서: 문서화 INCREMENTAL — 기능 3개 갱신, 신규 1개`).

## 절차 (`문서화 상태` / `문서화 검증`)

동기 실행(`status`는 수 초, `verify`는 수 분 — Bash `timeout` 600000). 출력을 그대로 보고한다. 아무것도 커밋하지 않는다(`verify`는 `runs/`에 검증 결과만 남긴다).

## 하지 않는 것

- 하네스 실행 없이 "문서화 완료"라고 보고하지 않는다. 증빙은 `runs/<run>/run.json`(status=SUCCESS)과 `report.txt`다.
- 리포트를 다듬거나 숫자를 바꾸지 않는다.
- 검증이 지적한 문제를 프로덕션 코드를 고쳐서 없애지 않는다. 문서 문제는 하네스의 수정 루프가 처리하고, 남은 문제는 사용자에게 보인다.
- `docs/`의 손글씨 문서 9개는 건드리지 않는다(생성물은 `docs/generated/`에만).
