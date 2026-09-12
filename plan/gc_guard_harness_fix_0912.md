# GC 가드 하네스 교차 점검 및 수정 (2026-09-12)

## 1. 배경 및 목적

종합 코드 리뷰 하네스 교차 검토(`plan/code_review_harness_fix_0912.md`)에서 드러난 결함 유형(팀 시대 프로토콜 잔재, 공유 작업공간, 빈 diff, 비결정적 점수)이 GC 가드 하네스에도 그대로 있을 것으로 보고, 사용자 요청으로 Codex CLI(`codex exec -s read-only`, stdin 프롬프트)에 동일 파일 세트를 점검시켰다. Claude 독립 검토 15건과 Codex 20건을 대조해 합의된 결함을 수정했다.

대상: `gc-guard-orchestrator` 스킬, `heap-allocation-scanner`·`pooling-enforcer`·`allocation-peer-reviewer` 에이전트, `heap-allocation-scan`·`pooling-enforcement`·`allocation-peer-review` 스킬.

## 2. 발견 사항 (Claude ∩ Codex)

### 2-1. 실행·계약 결함

| # | 심각도 | 위치 | 문제 | Claude | Codex | 조치 |
|---|-------|------|------|:-----:|:-----:|------|
| 1 | high | 에이전트 3종·스킬 3종 | 리더 SendMessage, 작업 목록 claim, **스캐너→강제자 버퍼 목록 SendMessage** 공유(형제 통신 불가) | ✓ | ✓ | 독립 병렬 감사로 재설계. 교차는 피어 리뷰어가 두 JSON으로 수행. SendMessage 제거 |
| 2 | medium | pooling-enforcer 프론트매터 | `tools:` 없음 → 상위 도구 상속(Edit 포함) | ✓ | ✓ | 3종 모두 `Read, Glob, Grep, Bash, Write, Skill` + Write 범위 명시 |
| 3 | high | 오케스트레이터 Phase 0 | "ValueTask만 다시"가 피어 리뷰를 재실행하지 않아 최종 점수가 옛 검증에 고정. 입력 해시 없음 | ✓ | ✓ | run_dir + meta.json 해시. 부분 재실행 후 **피어 리뷰 필수 재실행**(`_r2`) |
| 4 | high | 오케스트레이터 Phase 3 / 스캐너 스키마 | `buffer_allocations` 계약 부재, `necessary` 필드 없음, 스캐너 `hot_path_allocs` vs 강제자 `findings` | ✓ | ✓ | 공통 finding 스키마(`id`, `hot_path` 3등급, `hot_path_evidence`, `necessary`, `fix_code`) |
| 5 | high | 피어 리뷰 점수 | `modified` 판정이 감점에서 누락. verdict에 severity·id 없음 | ✗ | ✓ | `verdicts[].id/final_severity`, `final_findings` = confirmed+modified+additional, 중앙 재계산 |
| 6 | high | 오케스트레이터 Phase 5 | 판정 임계값 없음, 피어 실패 시 점수 산식 없음, JSON 검증 없음 | ✓ | ✓ | 구조 검증, 25/12/5/2 산식, 판정 우선순위(보류→해당 없음→BLOCK→RC→APPROVE) |
| 7 | high | Phase 1 | master 빈 diff, 작업 트리 미수집, 빈 `$BASE`, `gh pr diff -- "*.cs"` 미지원 옵션 | ✓ | ✓ | A-1/A-2/A-3 폴백, D 범위, PR은 전체 diff 후 .cs 집중 |
| 8 | high | Phase 1 케이스 B | `find | xargs cat` → 파일 경계·줄번호 소실, `파일:라인` 근거 불가, bin/obj 포함 | ✓ | ✓ | `=== FILE ===` + `cat -n`, `-print0`, bin/obj 제외, csproj 포함 |
| 9 | medium | hot path 판정 | "루프 내부 무조건 hot path", `AggressiveInlining` 표식, hot path 없으면 100점="건강" | ✓ | ✓ | confirmed/candidate/unknown 3등급, 미확정 severity 하향, `hot_path_found:false` → "분석 대상 없음/해당 없음" |
| 10 | low | FP 비율·리포트 | 분모 없음, `60%+` vs `60% 초과`, FN 전부 HIGH 배치 | △ | ✓ | `fp_rate = rejected/검증 원본`(병합 제외), 60% 이상, 추가 발견은 final severity로 배치 |

### 2-2. .NET 기술 오답

| # | 위치 | 오답 | 교정 | Claude | Codex |
|---|------|------|------|:-----:|:-----:|
| 11 | scan Pattern 4 / peer FP | 캡처 없는 람다(`Sort((a,b)=>…)`)를 "매번 delegate 할당". peer는 반대로 기술해 상호 모순. `AggressiveInlining`이 클로저 제거 | 캡처 있을 때만 보고. 캐싱 규칙 명시. AggressiveInlining 무관 | ✓ | ✓ |
| 12 | peer FN-4 | `Nullable<T>.Value` 추출을 GC 결함으로 | 제외(할당 없음) | ✓ | ✓ |
| 13 | scan Pattern 3 | `List<T>.Count()` "열거자 할당" | ICollection 경로 무할당. `Any()`도 .NET 8+ 무할당 | ✓ | ✓ |
| 14 | peer FN-3 | `IEnumerable<int>` 캐스트가 원소 박싱 | struct 열거자 박싱으로 메커니즘 교정 | ✓ | ✓ |
| 15 | scan Pattern 1 | `string.Format` 박싱 ".NET 6 미만" | 모든 버전 박싱. 대안: 보간 핸들러, .NET 8 `CompositeFormat<T>` | ✗ | ✓ |
| 16 | pooling 기법 2 | `Split(',')[0]` → `AsSpan(0, IndexOf(','))` (구분자 부재 시 예외) | `idx < 0 ? AsSpan() : AsSpan(0, idx)`, `Skip/Take` 범위 차이 명시 | ✗ | ✓ |
| 17 | pooling 기법 3 / peer | Return 누락 = "메모리 누수 CRITICAL"; peer는 "다른 경로에 Return 있으면 기각" | 풀 미스로 교정, 모든 종료 경로 추적, 풀 밖 배열·중복·Return 후 사용 추가, 0 초기화 의존 확인 | △ | ✓ |
| 18 | pooling 허용 패턴 / peer | stackalloc "이미 최적화됨" 제외, "≤16바이트" 임계 | 루프 내·상한·async·반환 검사, ≤256~512B 지침, 기존 stackalloc도 검사 | △ | ✓ |
| 19 | pooling 기법 1 | `Task.FromResult` 항상 할당, ValueTask 로컬 저장 "위험" | .NET 6+ 캐시 값 예외, 단일 소비가 기준, 미완료 `.Result`·WhenAll 검사 추가 | △ | ✓ |
| 20 | pooling Span 표 / peer | "외부 반환은 Memory만", "await 이후 Span 접근 금지" | 안전 backing 반환 허용, C# 13 지역 Span 허용, Memory 교체 시 소유권 잔존 명시 | ✗ | ✓ |
| 21 | scan Pattern 6 / peer | `params` 고정 인수면 무할당으로 기각 | 확장 호출은 할당. C# 13 `params ReadOnlySpan<T>` 해법 추가 | ✓ | ✓ |
| 22 | scan Pattern 5 / peer FN | "ReadOnlySpan으로 string 생성 0회 조립", yield→List 무조건, 예외 인스턴스 재사용 | 최종 string은 necessary, `string.Create`, yield/예외는 소비 의미 보존 시에만 | ✗ | ✓ |
| 23 | 스킬 3종 fix_code | CLAUDE.md 필수 주석(선언부 근거 `//`, `<remarks>`) 미포함·미검사 | 작성 원칙·검증 체크리스트에 추가, 예시 코드 준수 | ✓ | ✓ |

✓ 독립 발견, △ 부분, ✗ Codex 단독. 의견 충돌 없음.

## 3. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| A단계 구조 | 스캐너·강제자 **독립 병렬**, 교차는 피어 | 순차(스캐너→강제자 버퍼 전달) | 형제 통신 불가. 순차는 벽시계 2배. 피어가 어차피 두 보고서를 대조 |
| 정본 | 피어 리뷰의 `final_findings` | 원본 두 보고서 합집합 | FP 기각·FN 추가·modified 보정이 반영된 유일한 집합. 피어 실패 시 합집합은 "미검증" 참고 |
| 점수 | 중앙 재계산 25/12/5/2, `necessary` 제외 | 에이전트 자가 점수 | 결정성. GC 하네스 기존 가중치 유지 |
| hot path 없음 | "분석 대상 없음 / 해당 없음" | 100점 APPROVE | 소규모 API에서 건강 오인 방지 |
| Return 누락 severity | 빈도·경로별 medium~high | 일괄 CRITICAL | 누수가 아닌 풀 미스. CRITICAL은 풀 오염(외부 배열·중복 Return·Return 후 사용)에 배정 |

## 4. 컴포넌트 구조

```
_workspace/gc-guard/<run_id>/
  00_input/{source.txt, meta.json, index.md, pr.json}
  02_allocation_findings.json   (HA-n)
  02_pooling_findings.json      (PE-n)
  03_peer_review[_rN].json      (verdicts, additional PR-n, final_findings)
  04_gc_guard_report.md
```

## 5. 핵심 계약

```json
// 공통 finding
{"id":"PE-1","severity":"high","file":"Server/Receiver.cs:88","pattern":"arraypool-return-missing",
 "hot_path":"confirmed","hot_path_evidence":"ReceiveLoopAsync while 루프","alloc_frequency":"프레임당 1회",
 "detail":"…","current_code":"…","fix_code":"…// ArrayPool<byte>.Shared: TLS 슬롯 우선 …","behavior_preserved":"…","necessary":false}
// 점수
score = max(0, 100 − 25c − 12h − 5m − 2l)   over final_findings, necessary 제외
판정: 피어 미완료→보류 / hot_path 없음→해당 없음 / c≥1 or <60→BLOCK / h≥1 or <80 or 부분→REQUEST CHANGES / 그 외→APPROVE
```

## 6. 변경 파일

| 파일 | 변경 |
|------|------|
| `.claude/skills/gc-guard-orchestrator/SKILL.md` | 전면 재작성 |
| `.claude/agents/{heap-allocation-scanner,pooling-enforcer,allocation-peer-reviewer}.md` | 전면 재작성, tools 명시 |
| `.claude/skills/{heap-allocation-scan,pooling-enforcement,allocation-peer-review}/SKILL.md` | 기술 오답 교정, 저장소 문맥 조사 표, 출력 프로토콜 |
| `.agents/skills/` 4개, `.codex/agents/` 3개 | 동기화 |
| `CLAUDE.md`, `AGENTS.md` | 변경 이력·플랜 목록 |

## 7. 검증

```powershell
pwsh scripts/harness-audit.ps1
```
실전 실행: `WebProject.Api/` 경로 모드 — 결과는 8절.

## 8. 실전 검증 결과

(실행 후 기입)

## 9. 향후 확장 포인트

1. concurrency-guard·pipeline-architect·tdd 하네스도 같은 유형(팀 프로토콜 잔재, 데이터 계약, 기술 지침)의 Codex 교차 점검 필요. 경로 격리만 적용된 상태
2. 리뷰어 Write 범위를 `run_dir`로 강제하는 PreToolUse 훅(코드 리뷰 하네스와 공통)
3. hot path 확정을 위한 호출 그래프 보조 도구(예: Roslyn 기반 호출자 목록) 도입 검토
