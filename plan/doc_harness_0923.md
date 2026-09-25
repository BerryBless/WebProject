# 문서화 하네스(doc-harness) 구현·실전 실행 보고서 (2026-09-23 ~ 09-25)

## 1. 배경 및 목적

원 개발자가 없어도 신규 개발자가 이해·실행·디버깅·수정·확장할 수 있는 기술 문서를 **코드를 근거로** 생성하고, 이후에는 `문서화` 한마디로 변경분만 증분 갱신하는 시스템이 필요했다. 요구 원문은 3회분(다단계 파이프라인·증분 문서화·Diagram 규칙), 승인된 설계는 `docs/superpowers/specs/2026-09-23-doc-harness-design.md`(결정 D1~D15), 구현 계획은 `docs/superpowers/plans/2026-09-23-doc-harness.md`(Task 1~15)다.

## 2. 설계 결정(승인 D1~D15 요약과 실행 중 내린 판정)

| 결정 | 채택 | 대안·이유 |
|---|---|---|
| 언어·실행 | TypeScript + Node 24, `tsx` 무빌드, Vitest | PowerShell(기존 하네스 관습)은 스키마 검증·워커 풀에 약함. Agent SDK는 OAuth 실측 불가 → CLI 어댑터 뒤에 인터페이스만 둠 |
| Claude 호출 | `claude -p --json-schema --tools Read,Glob,Grep --setting-sources user` 읽기 전용 자식 세션, 파일은 하네스가 씀 | 프로덕션 코드 수정 금지를 구조로 보장. 프로젝트 Stop 훅·쓰기 가드는 자식에서 실행되지 않음(실측) |
| 원자성 | Run 트랜잭션: `staging/` → 검증 → `current/`·`docs/generated/`·`baseline.json` 원자 교체 | 실패한 Run이 정본을 건드리지 않음 |
| 변경 감지 | 파일 해시 1차 + git(rename·hunk) 보강 | 커밋·스테이지·미커밋·untracked를 같은 방식으로 잡음 |
| 문서 갱신 | HTML 주석 앵커 섹션 단위, 수동 수정 보존, Diagram UNCHANGED/UPDATED 판정 | 문서가 Diagram의 원본(이미지 파일 없음) |
| 검증 | 결정적(참조·Mermaid 3층·교차 일관성, 비용 0) + LLM 검증자 + 수정 루프 | Mermaid 파서는 `mermaid@12`+jsdom으로 실제 파싱(실측) |

**실행 중 질문 없이 내린 판정(추천안):**

| 판정 | 이유 | 틀렸을 때의 비용 |
|---|---|---|
| 기능 분석 병렬 3 | 기능당 4~6분·$1~2.6, 29개 순차면 2.5시간 | 동시 쓰기 경합 → 상태 저장 직렬화·tmp 고유명·rename 재시도로 해결 |
| phase 예산 상향(feature $5, verification $6) | 실측 최대 $2.6·$4.1 | 폭주 시 상한까지만 소비 |
| 결정적 검사의 이름 대조·participant 대조·컴포넌트 언급·엔드포인트 언급 누락을 **경고**(보고만)로 강등 | 실측 오탐 254+42건이 수정 루프를 $60/회로 몰아감 | 놓치는 진짜 오류는 LLM 검증자와 잔여 지적 표가 보완 |
| `publish_on_residual: true` — 결정적 차단 0이면 잔여 LLM 지적을 `19_UNKNOWN_AND_TODO.md`에 적고 발행 | LLM 검증자는 50개 문서에서 항상 무언가를 찾아 "이슈 0"이 비현실적 | 문서에 오류가 남을 수 있음 → 잔여 표로 사람이 확인 |
| 결정적 사전 루프(비용 0)를 LLM 검증 앞에 둠 | 없는 경로 하나 때문에 $30짜리 LLM 검증을 반복하지 않도록 | 없음 |
| `max_iterations` 2, 기능 검증 조각 4개 고정 | INITIAL 회차당 $30~60·1시간, 크기 기준 조각은 캐시가 전부 깨짐 | 반복이 적어 잔여 지적이 남음(발행 정책으로 흡수) |
| 입력 해시에서 ISO 날짜 제거, `analysisStatus`·diff의 하네스 변경 제외, Map 순서 정렬 | 날짜·상태·순서 때문에 resume마다 캐시가 깨져 $40+ 재실행 | 없음 |
| Run 진행 상태 파일(`state.json` 등)은 커밋하지 않음 | 턴마다 폴백 커밋이 쌓임 | `run.json`·`report.txt`만으로 추적 가능 |

## 3. 컴포넌트 구조

```text
doc-harness/
├── config/harness.yaml      제외 규칙·병렬도·예산·검증 반복·조각·발행 정책
├── prompts/                 00_global_rules + 01~09, I02/I04/I07/I08
├── schemas/                 common $defs 인라인 → --json-schema
├── src/
│   ├── claude.ts            CLI 어댑터(재시도, 한도 즉시 실패, 로그 비밀값 가림)
│   ├── run.ts · state.ts    Run 트랜잭션·재개(FAILED 재개 포함)
│   ├── change/              detect(해시+git) · classify(LLM) · impact(depgraph) · featureDelta
│   ├── phases/              inventory·architecture·discovery·feature(워커 풀)·dataApi·failures·operations
│   ├── render/              sections(앵커)·templates·documents·diagrams
│   ├── verify/              references·mermaid·consistency(결정적) · llm · loop(사전 루프+수정 루프) · fixer
│   └── pipeline.ts · cli.ts · report.ts · mode.ts · baseline.ts · depgraph.ts
├── scripts/det-check.ts     결정적 검사 재현 도구
└── workspace/               baseline.json · depgraph.json · current/ · runs/run-NNNN/
```

트리거: `.claude/skills/doc-harness/SKILL.md`(+ `.agents` 미러), CLAUDE.md/AGENTS.md "하네스: 문서화" 절. 장시간 실행은 PowerShell `Start-Process` 분리 프로세스 + Monitor 폴링(Bash 10분 상한 회피).

## 4. 핵심 사용법

```bash
cd doc-harness && npm ci
npm run harness -- run            # 자동 판정(최초 INITIAL / 이후 INCREMENTAL / 변경 없음 종료)
npm run harness -- status         # LLM 없이 동기화 상태
npm run harness -- verify         # 검증만
npm run harness -- resume         # 미완료·실패 Run 이어가기(성공 항목 캐시)
npm run harness -- run --phase discovery | --feature F003   # 개발용 부분 실행
```
Claude Code에서는 `문서화` / `문서화 전체` / `문서화 상태` / `문서화 검증`.

## 5. 변경 파일 목록

| 파일 | 내용 |
|---|---|
| `doc-harness/**` | 하네스 전체(소스 30여 파일, 프롬프트 17, 스키마 17, 단위 테스트 99개) |
| `.claude/skills/doc-harness/SKILL.md`, `.agents/skills/doc-harness/SKILL.md` | `문서화` 트리거 스킬(분리 프로세스 실행 절차) |
| `CLAUDE.md`, `AGENTS.md` | "하네스: 문서화" 절, 플랜 표 |
| `docs/harness.md`, `README.md`, `plan/harness_changelog.md` | 구성 표·트리·변경 이력 |
| `.gitignore` | `doc-harness/node_modules`·staging·logs·inbox·Run 상태 파일 제외 |
| `docs/generated/**` | 생성 문서 61개(생성물, 커밋) |
| `doc-harness/workspace/{baseline.json,depgraph.json,current/,runs/run-0001/{run.json,report.txt}}` | 정본 산출물(커밋) |

## 6. 빌드·검증

```bash
cd doc-harness && npm test && npm run typecheck        # 99 passed / 오류 0
DOC_HARNESS_LIVE=1 npm test                             # 실제 claude 어댑터 스모크
pwsh scripts/harness-audit.ps1                          # PASS 8/8
npx tsx scripts/det-check.ts                            # 결정적 검사 재현(run-0001 스테이징)
```

## 7. 실전 실행 결과(run-0001, INITIAL)

| 항목 | 값 |
|---|---|
| 기능 | 발견 29 · 분석 29(병렬 3, 기능당 4~6분·$0.85~2.6) |
| 문서 | 61개(고정 22 + 기능 29 + ADR 10), Mermaid 101개(Sequence 30·Flowchart 43·Data Flow 11·State 10·Architecture 4·ER 2·Class 1) |
| 실패 기록·횡단 이슈 | FAIL 12건, 트러블슈팅 11건, 확인 1·잠재 31·개선 10 |
| 검증 | 최종 패스: 환각 4·누락 17·관계 13·다이어그램 7·근거없음 5(전부 LLM 지적) + 경고 18 → 잔여 87건 `19_UNKNOWN_AND_TODO.md`에 기록하고 발행. 결정적 차단 0. Mermaid 파서 VERIFIED |
| 단계별 실측 | Inventory 5분 $0.79(sonnet) · Architecture 6.5분 $2.44(opus, 컴포넌트 23·다이어그램 4) · Discovery 2분 $0.72 · Data $0.60 · API $0.67 · Failures $1.5 · Operations 4×$0.25 · 서술 문서 12개 $0.08~0.39 · LLM 검증 문서군당 $1.3~4.1(패스당 약 $30) · 기능 재분석(수정) $1~2.6 |
| 비용·시간 | 누적 약 $347, 이틀(사용량 한도 5회 도달로 중단·재개, 캐시 무효화 재실행 포함). 사고 없는 이상적 INITIAL은 약 $130~160·3~4시간으로 추정 |

### 실전에서 잡은 결함과 교훈

| # | 결함 | 수정 |
|---|---|---|
| 1 | 워커 풀 동시 쓰기의 tmp 파일명 충돌·Windows rename 경합 | tmp 고유명, rename 재시도, 상태 저장 직렬화 |
| 2 | 사용량 한도 오류를 3회 재시도하고 남은 기능도 계속 시도 | 한도 즉시 실패(`QUOTA:`), 워커 풀 중단, 미시작은 PENDING |
| 3 | FAILED Run을 `resume`이 이어가지 못함 | FAILED도 재개(reopen), RUNNING 항목은 PENDING으로 |
| 4 | 프롬프트에 `analysisStatus`·오늘 날짜가 들어가 resume마다 캐시 무효화(29개 재분석 $45) | 상태 제거, ISO 날짜 전부 자리표시자로 치환한 안정 해시, 해시 복구 스크립트로 어제 결과 복원 |
| 5 | working tree diff에 하네스 자신의 변경이 섞여 실패 이력 프롬프트가 매번 바뀜 | git pathspec으로 제외 경로 적용 |
| 6 | 횡단 분석 프롬프트가 병렬 완료 순서(Map 순서)를 따라 해시가 흔들림 | id 정렬 |
| 7 | 검증 조각을 누적 크기로 잘라 문서 하나가 커지면 모든 조각 캐시가 깨짐 | 기능 문서군은 개수 고정 조각(설정) |
| 8 | Mermaid 검사기 오탐 254건(개념 노드·별칭), 경로 검사 오탐 42건(짧은 표기), 부정문 언급("…은 없다")을 환각으로 판정 | 개념 다이어그램 이름 대조 제외·느슨한 대응·경고 강등, 접미 일치, 문장 단위 부정 판정 |
| 9 | 09_FEATURES 의존 다이어그램 라벨의 괄호로 문법 오류, 노드 28개로 복잡도 초과 | 따옴표 라벨, 25/40 초과 시 표로 |
| 10 | 서술 문서가 기능 문서 파일명을 추측해 깨진 링크 | 프롬프트에 실제 문서 이름 목록 주입 |
| 11 | 디렉터리를 가리키는 코드 근거를 파일로 읽어 EISDIR | 디렉터리는 존재만 확인 |
| 12 | 수정 루프가 resume 시 1회차부터 다시 시작해 LLM 재검증 $30이 반복된다고 판단 | **오판정(2026-09-25 스파이크로 정정)**: 검증·수정 항목은 id+입력 해시로 캐시되므로, 하네스 코드를 안 고친 채 재개하면 실패한 항목만 다시 돈다(Fake 러너 재현: 2회차 검증 도중 한도 오류 → 재개 시 LLM 호출 2건, 그 외 전부 캐시). 실제 비용은 재개 사이에 코드를 고쳐 문서가 다시 렌더된 것이 원인이며, 문서군 단위 캐시라 군 안의 문서 하나만 바뀌어도 군 전체가 다시 검증된다 |
| 13 | (run-0002) LLM 검증 결과가 검증 조각 밖 문서까지 지적해 조각 캐시·수정 대상이 부풀음 | 지적을 조각 안 문서로 한정 |
| 14 | (run-0002) 색인 문서 지적 하나가 기능 문서 전체 재분석으로 확산(fan-out) | 색인은 단일 F-id 지적만 해당 기능으로 |
| 15 | (run-0002) 사소한(TRIVIAL) 변경도 depgraph 이웃 기능까지 재분석 | TRIVIAL만이면 직접 기능만 |
| 16 | (run-0002) 증분 이전 워크스페이스를 마지막 run 스테이징에서 읽어 실패 run의 잔재를 이어받음 | `workspace/current/`(발행본)에서 읽음 |
| 17 | (run-0002) 교차 일관성 검사가 `GET, HEAD` 복합 메서드를 한 토큰으로 봐 08_API 누락 10건 오탐 → 수정 루프가 풀 수 없는 차단 | api.json·표 양쪽을 메서드별 키로 펴서 대조. 사후 결정적 검사(LLM 없이 싼 수정만)도 추가 |

### 실전 증분 실행 결과(run-0002, INCREMENTAL)

소스 한 줄(`TagEndpoints.cs` 주석 한 줄, 검증 뒤 되돌림)을 바꾸고 `문서화`를 실행했다.

| 항목 | 값 |
|---|---|
| 변경 감지 | 변경 파일 6개(소스 1 + 문서·계획 5, 후자는 분석 불필요로 분류), 신규·삭제 기능 0 |
| 영향 | 기능 2개(F006·F008, TRIVIAL이라 depgraph 이웃 확대 없음), 문서 11개, 다이어그램 7개 |
| 결과 | 문서 수정 15, 다이어그램 수정 3·유지 9(UNCHANGED 경로 실증), 기술 부채 14건 |
| 검증 | 2회: 환각 5·누락 9·관계 17·다이어그램 4·근거없음 2 + 경고 27 → 잔여 61건을 `19_UNKNOWN_AND_TODO.md`에 기록하고 발행(결정적 차단 0, Mermaid VERIFIED, 내부 지표 coverage 88 / accuracy 91) |
| 비용·시간 | 누적 $149·Claude 호출 32회. 사용량 한도 7회·결함 13~17 수정 후 재개를 반복한 합계이며, 사고 없는 증분은 약 $25~40·15~25분으로 추정(LLM 검증 패스가 대부분) |

**교훈:** (1) 입력 해시에 들어가는 모든 것은 결정적이어야 한다(날짜·상태·Map 순서·자기 변경). (2) 결정적 검사는 "확신할 수 있는 것만 차단"하고 나머지는 경고여야 수정 루프가 폭주하지 않는다. (3) 사용량 한도가 있는 환경에서는 비싼 단계(LLM 검증)를 가장 나중에 한 번만 두고, 싼 단계(결정적 검사)를 앞에 반복한다. (4) 장시간 실행은 도구 상한(10분)과 분리해야 하며, 실행 중 하네스 코드를 고치면 프롬프트 해시가 바뀌어 캐시가 깨진다.

## 8. 수용한 잔여 위험·알려진 문제

- 잔여 LLM 지적 87건이 문서에 남아 있다(`19_UNKNOWN_AND_TODO.md`). 코드가 맞고 문서가 틀렸을 수도, 지적이 틀렸을 수도 있다 — 다음 `문서화` 전에 사람이 훑는다.
- (정정) resume 자체는 캐시로 무료에 가깝다. 비용이 드는 경우는 재개 사이에 하네스 코드·프롬프트를 고쳐 문서가 바뀔 때이며, 그때 LLM 검증은 바뀐 문서가 속한 문서군 전체($3~14)를 다시 본다.
- 기능 문서의 `HEAD` 엔드포인트 언급 누락 등은 경고로만 남는다.
- INCREMENTAL 실전 실행(run-0002)은 임시 주석을 넣은 `TagEndpoints.cs`로 검증했고, 검증 뒤 주석을 되돌렸다. 따라서 baseline 2는 주석이 있는 판본을 기억하므로 다음 `문서화`는 그 파일 하나를 TRIVIAL 증분으로 다시 본다(비용 절감을 위해 되돌림 직후 재실행하지 않았다).
- 잔여 LLM 지적은 run-0001 87건 → run-0002 61건으로 줄었지만 같은 문서를 두고 회차마다 다른 지적을 내는 경향이 있다. 결정적 차단이 0인 한 발행되므로 문서 품질의 최종 판단은 사람이 한다.
- 생성 문서가 구성 설명 중에 `Password=${…}` 같은 문자열을 그대로 적으면 Stop 훅의 비밀값 스캔에 걸려 커밋이 막힌다(run-0002 F026 문서에서 실제 발생, 문구를 바꿔 해결). 렌더 단계에서 `Password=`·`Pwd=` 직후를 자리표시자로 바꾸는 정제가 향후 과제다.
- 비용: INITIAL은 수백 달러 규모의 opus 호출이 든다. 모델·예산은 `harness.yaml`에서 조정한다.

## 9. 향후 확장 포인트

1. LLM 검증 캐시를 문서군 단위에서 문서 단위 부분 재검증으로 좁히기(군 안의 바뀐 문서만 다시 보고, 안 바뀐 문서의 지적은 캐시에서 합침). 회차 영속화는 스파이크로 불필요함이 확인돼 제외.
2. 결정적 검사 확장: 설정 키 실재(05), 명령 실재(04)의 코드 대조.
3. 검증자 모델을 sonnet으로 낮추고 opus는 기능 분석에만 쓰는 비용 프로파일.
4. `문서화 상태`에 depgraph 기반 영향 문서 목록 표시.
5. Agent SDK 어댑터(현재 CLI 서브프로세스) — 인터페이스는 준비됨.
