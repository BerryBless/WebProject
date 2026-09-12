---
name: gc-guard-orchestrator
description: ".NET 10 서버 라이브러리의 GC 압력 억제를 위한 메모리 최적화 팀을 조율하는 오케스트레이터. 힙 할당 스캐너와 풀링 강제자가 독립 병렬 감사 후 피어 리뷰어가 교차 검증하여 단일 GC 가드 리포트를 생성한다. 트리거(감사 의도가 명시된 요청에만): 'GC 억제', '힙 할당 감사', '메모리 최적화', 'ArrayPool 검사', 'ValueTask 검증', 'Span 적용', 'boxing 탐지', 'GC 압력 분석', '메모리 최적화 리뷰'. 후속 작업: '다시 분석', 'GC 재검토', '할당 보완', '이전 결과 업데이트', 'ValueTask만 다시'."
---

# GC Guard Orchestrator

.NET 10 고성능 서버 라이브러리의 GC 억제를 위한 메모리 최적화 팀을 조율하는 오케스트레이터.

## 실행 모드: Agent 팬아웃(2, 병렬) → 순차 피어 리뷰(1)

**이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다.** 서브에이전트는 `Agent` 도구로 띄우고 격리 실행 후 **최종 응답 1회**로 결과를 돌려준다. 서브에이전트는 오케스트레이터 ID를 모르고 형제와 통신할 수 없다. 따라서:
- 스캐너와 강제자는 **서로 독립적으로** 같은 입력을 감사한다. 실행 중 버퍼 목록 공유 같은 형제 간 메시지는 없다.
- 두 보고서의 교차·중복·상충 조율은 **피어 리뷰어**가 두 JSON을 읽어 수행한다.
- 완료 보고는 최종 응답 첫 줄 JSON으로만 받는다. 집계의 단일 근거는 JSON 파일이다.

## 에이전트 구성

| 에이전트 타입 | 역할 | 스킬 | 출력 (run_dir 기준) | 단계 |
|-------------|------|------|------|------|
| heap-allocation-scanner | hot path 힙 할당 6패턴 탐지 | /heap-allocation-scan | `02_allocation_findings.json` | A (병렬) |
| pooling-enforcer | ValueTask·Span/Memory·ArrayPool 적용 강제 | /pooling-enforcement | `02_pooling_findings.json` | A (병렬) |
| allocation-peer-reviewer | FP 기각·FN 보완·fix_code 검증·최종 finding 확정 | /allocation-peer-review | `03_peer_review.json` | B (순차) |

## 작업 디렉토리 (다른 하네스와 격리)

```
_workspace/gc-guard/
├── latest.txt                     # 가장 최근 run_id
└── <run_id>/                      # YYYYMMDD_HHmmss
    ├── 00_input/
    │   ├── source.txt             # 분석 대상 원본 (요약 대체 금지)
    │   ├── meta.json              # run_id, target_type, target, base_sha, head_sha, files[], lines, sha256, created
    │   └── index.md               # 800줄 초과 시 파일별 색인
    ├── 02_allocation_findings.json
    ├── 02_pooling_findings.json
    ├── 03_peer_review.json        # 부분 재실행 시 _r2, _r3
    └── 04_gc_guard_report.md
```

`_workspace/` 루트나 다른 하네스 디렉토리는 건드리지 않는다. `.gitignore`의 `_workspace/*`로 커밋되지 않는다.

## 공통 finding 스키마 (세 에이전트 공통)

```json
{
  "id": "HA-1 | PE-1 | PR-1",
  "severity": "critical|high|medium|low",
  "file": "파일명:라인",
  "pattern": "boxing|new-in-loop|linq-hotpath|closure-capture|string-concat|implicit-array|task-instead-of-valuetask|substring-copy|raw-array-alloc|arraypool-return-missing|arraypool-misuse|span-misuse|valuetask-misuse|stackalloc-misuse",
  "hot_path": "confirmed|candidate|unknown",
  "hot_path_evidence": "호출 경로·루프·어트리뷰트 등 근거. unknown이면 사유",
  "alloc_frequency": "요청당 N회 추정 (가능하면)",
  "detail": "무엇이, 왜 GC 압력을 유발하는지(메커니즘)",
  "current_code": "현재 코드 (선택)",
  "fix_code": "동작을 보존하는 수정 코드. 선언부에 CLAUDE.md 인라인 근거 주석 포함",
  "necessary": false
}
```
- `id` 접두사: HA=scanner, PE=enforcer, PR=peer 추가 발견. 피어 리뷰의 verdict는 이 `id`를 참조한다.
- `hot_path`가 `candidate`/`unknown`이면 severity를 한 단계 낮춰 기록한다(`critical→high`, …). 확정 근거 없이 critical을 매기지 않는다.
- `necessary: true`는 "할당이지만 제거 불가·불필요(예: 반환용 최종 string, 1회성 초기화)"를 뜻하며 감점에서 제외되고 리포트에 참고로만 실린다.

---

## 워크플로우

### Phase 0: 실행 모드 결정

1. `_workspace/gc-guard/latest.txt` 없음 → **초기 실행**. Phase 1.
2. 있으면 이전 run의 `00_input/meta.json`을 읽고 분류:
   - **특정 에이전트 재실행** ("ValueTask만 다시" → enforcer, "스캔만 다시" → scanner): Phase 1을 같은 `target_type/target`으로 재수집해 `sha256` 비교.
     - 동일 → 이전 run_dir 재사용. 요청 에이전트만 재호출(출력 같은 파일명 덮어씀). **피어 리뷰는 반드시 다시 실행**해 `03_peer_review_r2.json`(있으면 `_r3`)으로 저장한다. 원본 보고서가 바뀌었는데 옛 피어 리뷰를 쓰면 최종 점수가 오래된 검증에 머문다.
     - 상이 → 부분 재실행 취소, 새 실행으로 전환하고 사용자에게 알린다.
     - 재호출 실패 시 해당 에이전트는 "수집 실패". 옛 JSON을 대신 쓰지 않는다.
   - **새 코드/새 대상/재분석** → 새 run_id로 Phase 1부터. 이전 run_dir 보존.

### Phase 1: 분석 대상 수집

`run_dir=_workspace/gc-guard/<run_id>` 생성 후 `00_input/source.txt`·`meta.json` 작성. Bash 도구, 상대 경로.

**케이스 A — 인수 없음** (우선순위 순, 선택 근거를 `meta.target`에 기록):
```bash
# A-1) 미커밋 변경(미추적 포함)이 있으면 그것이 대상 → worktree
if [ -n "$(git status --porcelain -- '*.cs')" ]; then
  git diff HEAD -- '*.cs' > "$run_dir/00_input/source.txt"
  git ls-files --others --exclude-standard -- '*.cs' | while IFS= read -r f; do
    git diff --no-index -- /dev/null "$f" >> "$run_dir/00_input/source.txt" || true
  done
fi
# A-2) 깨끗하고 기본 브랜치가 아니면 베이스와 비교 → branch. 전부 실패 시 빈 $BASE로 실행 금지, 사용자에게 질문
for ref in main master origin/main origin/master; do BASE=$(git merge-base HEAD "$ref" 2>/dev/null) && break; done
git diff "$BASE" HEAD -- '*.cs' > "$run_dir/00_input/source.txt"
# A-3) BASE == HEAD(기본 브랜치)이면 최근 커밋 1개 → commit. 리포트에 명시
git diff HEAD~1 HEAD -- '*.cs' > "$run_dir/00_input/source.txt"
```

**케이스 D — 명시 범위** (`HEAD~3..HEAD`): `git diff <범위> -- '*.cs'` → range.

**케이스 B — 경로 지정**: 파일 경계와 원본 줄 번호를 보존한다. `xargs cat` 금지.
```bash
find "$path" -type f \( -name '*.cs' -o -name '*.csproj' -o -name 'Directory.Build.props' \) \
  -not -path '*/bin/*' -not -path '*/obj/*' -print0 | sort -z | while IFS= read -r -d '' f; do
  printf '=== FILE: %s ===\n' "$f"; cat -n "$f"; printf '\n'
done > "$run_dir/00_input/source.txt"
```
`.csproj`는 `<TieredPGO>`, `<ServerGarbageCollection>`, `<AllowUnsafeBlocks>`, LangVersion 확인용이다. → path

**케이스 C — PR 번호**: `gh pr diff`는 경로 필터를 지원하지 않는다. 전체 diff를 받고 `.cs` 외 파일 헝크는 그대로 두되 리뷰어 프롬프트에 ".cs만 감사"를 명시한다.
```bash
gh pr view N --json number,title,baseRefOid,headRefOid > "$run_dir/00_input/pr.json"
gh pr diff N > "$run_dir/00_input/source.txt"
git fetch origin "pull/N/head" 2>/dev/null || true
```
diff 밖 파일은 `git show <head_sha>:<경로>`로 읽도록 Phase 2 프롬프트에 명시. → pr

**공통 마무리:** 0바이트면 중지·대상 요청. `lines`, `sha256`을 meta.json에 기록. `latest.txt` 갱신.
**크기 관리:** `source.txt`는 원본 유지. 800줄 초과 시 `index.md`(파일 | 줄수 | source.txt 시작 줄 | 핵심 메서드) 추가. 3000줄 초과 시 범위 축소를 제안하고, 진행 시 파일 그룹별로 A단계 에이전트를 나눠 호출(`_g1`, `_g2`)한 뒤 피어 리뷰어에게 전부 넘긴다.

### Phase 2: A단계 — 독립 병렬 감사 (단일 메시지에서 Agent 2회)

각 프롬프트 필수 항목: `run_dir`, `target_type`, `head_sha`, 입력·출력 파일, "source.txt 전체 읽기", "공통 finding 스키마·id 접두사", "소스 수정 금지·Write는 출력 파일에만", "SendMessage 금지·최종 응답 첫 줄 JSON".

```
Agent(subagent_type="heap-allocation-scanner", description="Heap allocation scan",
      prompt="당신은 GC 가드 팀의 힙 할당 스캐너입니다. 프로젝트 루트는 현재 작업 디렉토리입니다.
              run_dir={run_dir}, target_type={target_type}, head_sha={head_sha}.
              heap-allocation-scan 스킬로 {run_dir}/00_input/source.txt 를 처음부터 끝까지 읽고
              (800줄 초과면 index.md 로 탐색, 판단은 source.txt 코드로) hot path 힙 할당을 탐지하세요.
              finding id 는 HA-1, HA-2… 로 매기고 hot_path 를 confirmed|candidate|unknown 으로 근거와 함께 기록하세요.
              결과를 {run_dir}/02_allocation_findings.json 에 저장하세요. 다른 에이전트와 통신하지 마세요.
              프로젝트 소스는 수정하지 말고 Write 는 위 출력 파일에만 쓰세요. target_type=pr 이면 diff 밖 파일은 git show {head_sha}:<경로>.
              완료 시 최종 응답 첫 줄에 {\"status\":\"done\",\"output\":\"<경로>\",\"counts\":{...},\"hot_path_found\":true|false} 를 적으세요.")
Agent(subagent_type="pooling-enforcer", description="Pooling enforcement",
      prompt="… pooling-enforcement 스킬로 … id 는 PE-1, PE-2… … {run_dir}/02_pooling_findings.json …
              스캐너의 결과를 기다리거나 요청하지 말고 source.txt 에서 직접 버퍼 할당을 찾으세요. …")
```

두 최종 응답을 받으면 **JSON 구조 검증**(필수 키 `domain, run_id, summary, findings[], counts, score, hot_path_found`; 각 finding에 `id, severity, file, pattern, hot_path, detail`; severity enum; id 접두사 일치). 실패 → 1회 재호출 → 재실패 시 "수집 실패".

### Phase 3: B단계 — 피어 리뷰 (순차, A단계 완료 후)

```
Agent(subagent_type="allocation-peer-reviewer", description="Allocation peer review",
      prompt="당신은 GC 가드 팀의 피어 리뷰어입니다. run_dir={run_dir}, target_type={target_type}, head_sha={head_sha}.
              allocation-peer-review 스킬로 {run_dir}/02_allocation_findings.json 과 {run_dir}/02_pooling_findings.json 을
              {run_dir}/00_input/source.txt 기준으로 독립 교차 검증하세요. 수집 실패한 보고서: {없음|파일명}.
              모든 원본 finding id 에 verdict(confirmed|rejected|modified) 와 final_severity 를 매기고, 놓친 패턴은 PR-n 으로 추가하며,
              final_findings 배열(confirmed+modified+추가, 동일 위치·동일 패턴 중복 제거)을 확정하세요.
              결과를 {run_dir}/03_peer_review{suffix}.json 에 저장하세요. 소스 수정 금지, SendMessage 금지, 최종 응답 첫 줄 JSON.")
```

JSON 검증: `verdicts[]`(id·verdict·final_severity·reason), `additional_findings[]`, `final_findings[]`, `fp_rate`, `final_score`. 실패 → 1회 재호출 → 재실패 시 "검증 미완료".

### Phase 4: 결과 통합 및 리포트

1. **최종 finding 집합** = `03_peer_review*.json`의 `final_findings` (피어 리뷰 성공 시). 피어 리뷰가 없으면 두 원본의 합집합을 **미검증** 표시로 사용한다.
2. **점수 재계산 (중앙, 결정적):** `necessary: true` 제외 후
   ```
   score = max(0, 100 − 25×critical − 12×high − 5×medium − 2×low)
   ```
   피어 리뷰의 `final_score`와 5점 이상 다르면 리포트에 병기한다. 원점수(스캐너·강제자 자가 점수)는 참고란에만 쓴다.
3. **FP 비율** = 피어가 `rejected`로 판정한 원본 finding 수 ÷ 검증한 원본 finding 수(중복 기각은 분모·분자 모두 1회). 원본 0건이면 "해당 없음". 60% 이상이면 "스캐너 방법론 재검토 권고".
4. **분석 대상 부재:** 두 A단계 보고서가 모두 `hot_path_found: false`이면 점수는 100이라도 상태를 **"분석 대상 없음(범위 제한)"** 으로 표기하고 APPROVE 대신 "해당 없음"으로 판정한다. hot path가 없는 코드는 건강한 것이 아니라 이 하네스의 대상이 아닌 것이다.
5. **판정 (우선순위):**
   1. **판정 보류** — 피어 리뷰 검증 미완료, 또는 A단계 2개 모두 수집 실패
   2. **해당 없음** — 분석 대상 없음(4항)
   3. **BLOCK** — critical ≥ 1 또는 score < 60
   4. **REQUEST CHANGES** — high ≥ 1 또는 score < 80 또는 A단계 1개 수집 실패(부분 검토)
   5. **APPROVE** — 그 외
6. `{run_dir}/04_gc_guard_report.md` 저장:

```markdown
# GC 가드 리포트
**생성:** {datetime} | **run_id:** {run_id} | **대상:** {target_type} — {target} (head {sha[:7]}) | **A단계:** {k}/2 | **피어 리뷰:** 완료/미완료

## 메모리 건강 점수
| 항목 | 값 |
|------|-----|
| 최종 점수 (피어 확정 finding 기준 재계산) | XX / 100 |
| 스캐너 원점수 / 강제자 원점수 (참고) | XX / XX |
| 원본 finding | HA N건, PE N건 → confirmed N · modified N · rejected N |
| 추가 발견(FN) | N건 |
| FP 비율 | NN% (또는 해당 없음) |
| hot path 탐지 | 있음/없음 |

## CRITICAL — 즉시 수정
### [id] [pattern] — 제목
**위치:** `파일:라인` | **hot path:** confirmed(근거) | **빈도:** 요청당 N회
**문제:** … **수정:** (fix_code, 선언부 근거 주석 포함)

## HIGH — 머지 전 수정
## ArrayPool 소유권 위반 목록
[Return 누락(모든 종료 경로 기준)·외부 배열 Return·중복 Return·Return 후 사용]
## Medium / Low
## 제거 불가 할당 (necessary, 참고)
## 기각된 발견 (FP)
- [id] 사유
## 검증 불가 항목
## 판정: APPROVE / REQUEST CHANGES / BLOCK / 해당 없음 / 판정 보류
```

### Phase 5: 보고
run_dir 보존. 리포트 본문 출력 + 경로 안내(`04_gc_guard_report.md`, `02_*.json`, `03_peer_review*.json`, `00_input/`).

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| source.txt 0바이트 | 중지, 대상 지정 요청 |
| merge-base 전부 실패 | 빈 `$BASE` 실행 금지, 사용자에게 베이스/범위 요청 |
| 기본 브랜치·깨끗한 트리 | 최근 커밋 1개(A-3) + 리포트 명시 |
| A단계 에이전트 실패/검증 실패 | 1회 재호출 → 재실패 시 "수집 실패". 피어 리뷰는 남은 보고서로 진행하며 판정 상한 REQUEST CHANGES |
| A단계 2개 모두 실패 | 사용자 확인. 진행 시 판정 보류 |
| 피어 리뷰 실패 | 원본 합집합으로 리포트 생성, 점수는 "미검증", 판정 보류 |
| 부분 재실행인데 해시 상이 | 새 실행으로 전환 |
| hot_path_found 둘 다 false | 상태 "분석 대상 없음", 판정 "해당 없음" |
| 최종 응답 미수신 | 능동 타임아웃 없음. 사용자에게 알리고 지시 시 "수집 실패" 확정 |

## 테스트 시나리오

### 정상 흐름
1. "Server/ 힙 할당 감사해줘" → 케이스 B, `=== FILE ===` 헤더로 수집
2. A단계: scanner HA-1(루프 내 `new byte[1024]`, hot_path confirmed: `ReceiveLoopAsync` while 루프), HA-2(캡처 람다) / enforcer PE-1(Rent 후 예외 경로 Return 누락), PE-2(`Task<T>` 캐시 히트 경로)
3. B단계: HA-2 rejected(캡처 없는 람다 → 컴파일러 캐싱), PE-2 modified(ValueTask 전환은 타당하나 fix_code에 remarks 누락 → 보정), PR-1 추가(`params object[]` 로깅 호출)
4. Phase 4: final_findings 4건(critical 1, high 2, low 1) → 100−25−24−2 = 49 → BLOCK

### 부분 재실행
1. "ValueTask만 다시" → 재수집 해시 동일 → enforcer만 재호출 → **피어 리뷰 재실행** `03_peer_review_r2.json` → 리포트 재생성

### 에러 흐름
1. enforcer JSON 검증 실패 → 재호출 → 재실패 → "수집 실패"
2. 피어 리뷰어는 scanner 보고서만 검증 + 독립 FN 탐지(ArrayPool 경로 포함)
3. 판정 상한 REQUEST CHANGES, 리포트에 "⚠️ 풀링 강제자 미완료 — ValueTask/ArrayPool 항목은 피어 독립 탐지 결과만 반영"
