# 종합 코드 리뷰 하네스 교차 검토 및 수정 (2026-09-12)

## 1. 배경 및 목적

2026-09-11 하네스 전수조사에서 `code-review-orchestrator`를 팀 도구(TeamCreate) 의존에서 `Agent` 팬아웃 방식으로 바꿨으나, 하위 정의(리뷰어 에이전트 4종·전용 스킬 4종)는 팀 시대 프로토콜이 그대로 남아 있었다. 사용자의 요청으로 Claude가 독립 검토한 뒤 Codex CLI(`codex exec -s read-only`)에 동일 파일 세트를 검토시켜 결과를 대조하고, 합의된 결함을 수정했다.

- Codex 실행 증빙: 프롬프트는 stdin(`codex exec -`)으로 전달, 출력은 `-o` UTF-8 파일. 첫 시도는 인수 전달 방식에서 stdin 대기로 멈춰 재실행함(스킬 문서 4절 참고).
- 대상 파일: 오케스트레이터 SKILL 1, 에이전트 4, 스킬 4, CLAUDE.md 해당 절, `.gitignore` `_workspace` 규칙.

## 2. 발견 사항 (Claude ∩ Codex 대조)

| # | 심각도 | 위치 | 문제 | Claude | Codex | 조치 |
|---|-------|------|------|:-----:|:-----:|------|
| 1 | high | 오케스트레이터 Phase 0 | `_workspace/` 전체를 `_workspace_<ts>/`로 이동 → tdd·gc·concurrency·cross-verify 산출물 파괴. Stop 훅이 삭제를 커밋 | ✓ | ✓ | 전용 `_workspace/code-review/<run_id>/` 도입, 상위 이동 금지 |
| 2 | high | 에이전트 4종 "팀 통신 프로토콜", 스킬 4종 "출력" | 리더에게 SendMessage, 공유 작업 목록 claim, 형제 리뷰어와 직접 조율 → 이 빌드에서 불가능. 스킬 마지막 줄은 무조건 SendMessage | ✓ | ✓ | 절 삭제. 최종 응답 첫 줄 JSON 보고로 통일, `tools`에서 SendMessage 제거, 조율은 오케스트레이터 Phase 4 |
| 3 | high | Phase 0·4 부분 재실행 | 입력 갱신 없이 옛 diff로 재검토, 실행 ID·해시 없어 결과 호환 판정 불가, 재실행 실패 시 옛 JSON이 성공처럼 채택 | △ | ✓ | `meta.json`(run_id, base/head SHA, diff_sha256) 도입. 해시 동일 시만 `_r2` 부분 재실행, 상이하면 새 실행. 실패 시 옛 파일 채택 금지 |
| 4 | high | Phase 1 케이스 A | master에서 merge-base == HEAD → diff 0줄로 종료(실측). 작업 트리 변경 미고려. `$BASE` 비면 `git diff HEAD`로 오동작 | ✓ | ✓ | A-1 미커밋 변경(미추적 포함) → A-2 브랜치 비교 → A-3 최근 커밋 순. 빈 `$BASE` 실행 금지 |
| 5 | high | Phase 1 크기 관리 | 800줄 초과 시 요약으로 대체 가능 → 리뷰어가 실제 코드를 못 보고 "발견 없음 100점" | ✓ | ✓ | `diff.txt` 원본 보존 의무, `index.md`는 색인 전용, 3000줄 초과 시 그룹 분할 |
| 6 | high | Phase 4 점수 | 실패 도메인 처리 미정(0 대입 vs 재정규화로 65↔100점), 보안 미검토 APPROVE 가능 | ✓ | ✓ | 성공 도메인 가중치 재정규화 + 검토 완료율 표기. 실패 도메인 있으면 APPROVE 불가, 보안 실패는 "판정 보류" |
| 7 | medium | Phase 1 케이스 B·C | `cat` 출력에 파일명 없음, `.csproj`·설정 누락, PR head SHA 미확보 | △ | ✓ | `=== FILE: ===` 헤더 + `cat -n`, 확장자 확대, `gh pr view --json headRefOid`, 보충 조회는 `git show <head_sha>:` |
| 8 | medium | 스킬 4종 입력 절차 | 참조 방향·취약 의존성·테스트 갭은 저장소 문맥 필요한데 diff만 읽음. "검증 못함" 상태 없음 | ✓ | ✓ | 스킬마다 "저장소 문맥 조사" 표(필수 보충 파일·조회 불가 시 처리), JSON에 `unverified[]` 추가 |
| 9 | medium | security-review 입력 | 삭제 줄을 위협 감소로 안내 → `[Authorize]` 삭제 회귀 놓침 | ✗ | ✓ | 삭제된 보안 통제를 필수 검사 항목·critical 기준에 추가 |
| 10 | medium | 점수·판정 | 도메인 산식 없음(비결정적), 중복 제거 후 감점 미반영, REQUEST CHANGES/BLOCK 동시 성립, 소수점 처리 없음 | ✓ | ✓ | `100−25c−10h−4m−1l` 산식, 오케스트레이터가 중복 제거 후 재계산, 판정 우선순위(보류→BLOCK→RC→APPROVE), 정수 반올림 |
| 11 | medium | description·Phase 1 예시 | "파일 경로·PR 번호 언급 시 반드시" 과잉 트리거, 예시가 사용자 레벨 `/comprehensive-review` 이름 사용 | ✓ | ✓ | 리뷰 의도 필수로 축소, 예시를 `/code-review-orchestrator`로 통일 |
| 12 | medium | Phase 3·에러 표 | 능동 타임아웃 불가한데 10분 규칙, 2개+ 실패 확인 규칙 우회 가능 | ✓ | ✓ | 최종 응답 전부 수신 후 상태 확정, 미수신은 사용자 지시로만 실패 확정, 2개+ 실패는 Phase 4 전 확인 |
| 13 | medium | architecture-review 레이어 절 | 그림 `Domain → Infrastructure`와 규칙 "Domain→Infrastructure는 역방향"이 정반대 | ✗ | ✓ | 화살표를 컴파일 의존으로 정의, `Domain ◀── Infrastructure`로 그림 수정 |
| 14 | medium | performance-review LINQ 절 | `Select(t).Where(p)` → `Where(p).Select(t)` 예시가 타입·부작용 무시 | ✗ | ✓ | 이동 조건 2가지 명시, 가능/불가 예시 분리 |
| 15 | medium | style-review XML 문서화 | 주석 존재만 검사, CLAUDE.md 필수 `<remarks>`(Thread Safety·Memory·Blocking)와 네트워크·메모리 선언 근거 주석 미검사. 테스트 갭을 "새 테스트 없음"으로 판정 | △ | ✓ | 4-b 내용 검사(medium), 4-c 인라인 근거 주석(low), 테스트 갭은 저장소 Grep으로 실제 검증 여부 판정 |
| 16 | medium | 에이전트 JSON·Phase 4 | 파싱 성공만으로 신뢰, `{"score":100}`도 통과 | △ | ✓ | Phase 3 구조 검증(필수 키·domain 일치·severity enum·style critical 강등), 건수는 findings에서 계산 |

✓ 독립 발견, △ 부분 발견(Codex가 구체화), ✗ Codex 단독 발견. Claude 단독 발견은 Phase 5 번호 오탈자(1,2,4)로 Codex도 실행 결함이 아니라고 판단, 수정함.

## 3. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 작업 디렉토리 | `_workspace/code-review/<run_id>/` + `latest.txt` | 기존 `_workspace/` 직접 사용 + 전체 이동 | cross-verify가 이미 `_workspace/cross/<run-id>/` 패턴 사용. `.gitignore` `_workspace/*`로 자동 무시 |
| 완료 보고 | 최종 응답 첫 줄 JSON | SendMessage | 서브에이전트는 오케스트레이터 ID를 모름. SendMessage는 대기·오류 원인 |
| 도메인 점수 | `max(0, 100−25c−10h−4m−1l)`, 오케스트레이터 재계산 | 에이전트 자가 점수 | 결정성. 중복 제거 후 점수 일관성. 자가 점수는 5점 이상 괴리 시 표기 |
| 실패 도메인 | 재정규화 + 승인 상한(RC) + 보안 실패 시 보류 | 0점 대입 / 단순 제외 | 0점은 코드 품질과 무관한 벌점, 단순 제외는 보안 미검토 APPROVE 허용 |
| 기본 브랜치 대상 | 미커밋 → 브랜치 diff → 최근 커밋 1개 | 사용자에게 항상 질문 | 자율 실행 유지. 근거를 리포트에 명시해 오해 방지 |
| 리뷰어 tools | Read, Glob, Grep, Bash, Write, Skill | + SendMessage | 사용처 없음. Write는 출력 파일 전용으로 문서화(강제 장치는 없음, 후속 과제) |

## 4. 컴포넌트 구조

```
.claude/skills/code-review-orchestrator/SKILL.md   Phase 0~5 재작성
.claude/agents/{architecture,security,performance,style}-reviewer.md
.claude/skills/{architecture,security,performance,style}-review/SKILL.md
.agents/skills/…(5개 미러, 내용 동일)
.codex/agents/{4}-reviewer.toml                    md 본문에서 재생성

_workspace/code-review/<run_id>/
  00_input/{diff.txt, meta.json, index.md, pr.json}
  02_{domain}_findings[_rN|_gN].json
  03_consolidated_report.md
```

## 5. 핵심 프로토콜

리뷰어 최종 응답 첫 줄:
```json
{"status":"done","output":"_workspace/code-review/20260912_201500/02_security_findings.json","counts":{"critical":0,"high":1,"medium":2,"low":3},"score":79}
```

종합 점수·판정:
```
overall = round( Σ_{성공} w_d×score_d / Σ_{성공} w_d )
판정: 보안 실패→보류 / critical≥1 or overall<60→BLOCK / high≥1 or overall<80 or 부분 검토→REQUEST CHANGES / 그 외→APPROVE
```

## 6. 변경 파일 목록

| 파일 | 변경 |
|------|------|
| `.claude/skills/code-review-orchestrator/SKILL.md` | 전면 재작성(run_dir, Phase 0 해시 비교, Phase 1 A-1/A-2/A-3/B/C/D, 원본 보존, Phase 3 구조 검증, Phase 4 산식·판정, 에러 표) |
| `.claude/agents/*-reviewer.md` ×4 | 팀 통신 절 삭제, 보고 프로토콜·쓰기 범위·unverified·counts 추가, tools에서 SendMessage 제거 |
| `.claude/skills/*-review/SKILL.md` ×4 | 입력 절차(meta.json·전체 읽기·PR 모드), 저장소 문맥 조사 표, 체크리스트 오류 교정, 점수 절, 출력 절 |
| `.agents/skills/` 5개 | 미러 동기화 |
| `.codex/agents/*-reviewer.toml` ×4 | md 본문 재생성 |
| `CLAUDE.md`, `AGENTS.md` | 변경 이력·플랜 목록 |

## 7. 검증

```powershell
pwsh scripts/harness-audit.ps1          # 7개 항목 PASS 확인
```
Phase 1 스니펫 실측(이 저장소, master): A-1 더러운 트리에서 18개 파일 diff 수집, A-2 merge-base == HEAD 판정, B 모드 5개 파일 헤더 생성.

## 8. 향후 확장 포인트 → 처리 결과 (2026-09-12 후속)

| # | 항목 | 상태 | 내용 |
|---|------|------|------|
| 1 | 타 하네스 `_workspace/` 전체 이동 결함 | **완료** | gc-guard·concurrency-guard·pipeline·tdd·git 5개 하네스의 스킬·에이전트·미러·`.codex/agents` toml에서 `_workspace/` → `_workspace/<하네스>/`로 일괄 치환(perl, 음성 lookahead로 멱등). Phase 0 보관 이동은 `_workspace/<하네스>_{ts}/`로 자기 디렉토리만. CLAUDE.md·AGENTS.md에 "작업 디렉토리 규칙" 문단과 하네스별 이력 추가 |
| 2 | 사용자 레벨 `~/.claude/skills/comprehensive-review` 충돌 | **완료** | `~/.claude/skills_disabled/comprehensive-review/`로 이동(복구 가능). 프로젝트 `code-review-orchestrator`만 트리거됨 |
| 3 | 리뷰어 Write 범위 강제 훅 | 미착수 | PreToolUse 훅으로 `run_dir` 밖 Write 차단. 이번 실행에서는 4/4 위반 없음 |
| 4 | 실전 검증 | **완료** | `WebProject.Api/` 경로 모드로 4-에이전트 전체 실행. run_id `20260912_204220`, 종합 96 APPROVE, 재시도 0, JSON 검증 4/4, 중복 조율 1건, `unverified` 실사용 3건. 리포트 `_workspace/code-review/20260912_204220/03_consolidated_report.md` |

실전 검증에서 확인된 하네스 동작: 리뷰어 4개 모두 SendMessage 없이 최종 응답 첫 줄 JSON으로 보고, 자가 점수와 결정적 산식 재계산이 4/4 일치, 보안 리뷰어가 `dotnet list package --vulnerable`을 실제 실행해 `unverified`를 비움, 성능·스타일 리뷰어는 확인 불가 항목을 결함 대신 `unverified`로 분리. 세 도메인이 같은 구간(`Program.cs:22-35`)을 다른 관점으로 지적해 "동일 위치·다른 관점은 유지" 규칙이 유효함을 확인.
