---
name: concurrency-guard-orchestrator
description: ".NET 10 고성능 비동기 서버 라이브러리의 동시성·락·데드락을 종합 감사하는 오케스트레이터. Lock-Free 설계 강제, 락 정당화 주석 감사(독립 병렬), 데드락 생성-검증 분석(순차)을 조율하고 단일 동시성 리포트를 생성한다. 트리거(감사 의도가 명시된 요청에만): '동시성 검사', '락 감사', '데드락 분석', '동시성 리뷰', 'Lock-Free 검증', 'async 데드락', 'concurrency guard', '컨텐션 분석'. 후속 작업: '다시 분석', '데드락만 재검토', '락 정당화 재감사', '이전 결과 업데이트', '보완 분석'."
---

# Concurrency Guard Orchestrator

.NET 10 고성능 비동기 서버 라이브러리의 동시성 감사 팀을 조율하는 오케스트레이터.

## 실행 모드: Agent 팬아웃(2, 독립 병렬) → 순차 생성-검증(analyzer → reviewer, 재분석 최대 1회)

**이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다.** 서브에이전트는 `Agent` 도구로 격리 실행되고 **최종 응답 1회**로 보고한다. 오케스트레이터 ID를 모르고 형제와 통신할 수 없다. 따라서:
- lock-free-enforcer와 lock-justification-auditor는 **서로 독립적으로** 같은 입력을 감사한다(필요 락 목록 공유 없음). 두 결과의 대조는 오케스트레이터가 Phase 4에서 한다.
- deadlock-analyzer → deadlock-reviewer는 순차 호출이며, 재분석은 reviewer JSON의 `needs_reanalysis: true` + `reanalysis_targets[]`를 오케스트레이터가 읽어 analyzer를 **새로 호출**하는 방식이다(에이전트 간 메시지 없음).

## 에이전트 구성

| 에이전트 | 스킬 | 출력 (run_dir 기준) | 단계 | 가중치 |
|---------|------|------|------|------|
| lock-free-enforcer | /lock-free-enforcement | `02_lockfree_findings.json` (id `LF-n`) | A 병렬 | 0.35 |
| lock-justification-auditor | /lock-justification-audit | `02_lockjustification_findings.json` (id `LJ-n`) | A 병렬 | 0.30 |
| deadlock-analyzer | /deadlock-static-analysis | `03_deadlock_analysis[_r2].json` (id `DA-n`) | B 순차 | — |
| deadlock-reviewer | /deadlock-review | `03_deadlock_review[_r2].json` (id `DR-n`, `final_findings`) | B 순차 | 0.35 |

## 작업 디렉토리

```
_workspace/concurrency-guard/
├── latest.txt
└── <run_id>/
    ├── 00_input/{source.txt, meta.json, index.md, pr.json}
    ├── 02_lockfree_findings.json
    ├── 02_lockjustification_findings.json
    ├── 03_deadlock_analysis.json        (재분석: _r2)
    ├── 03_deadlock_review.json          (재분석: _r2)
    └── 04_concurrency_guard_report.md
```
`_workspace/` 루트나 다른 하네스 디렉토리는 건드리지 않는다. **중간 파일은 반드시 run_dir 안에만 만든다**(저장소 루트에 `combined_source.txt` 같은 파일을 만들면 Stop 훅이 커밋한다).

## 공통 finding 스키마 (네 에이전트 공통)

```json
{
  "id": "LF-1 | LJ-1 | DA-1 | DR-1",
  "severity": "critical|high|medium|low",
  "file": "파일명:라인",
  "pattern": "lock-replaceable|lock-necessary|interlocked-misuse|justification-missing|justification-insufficient|justification-nonstandard|remarks-inconsistent|sync-blocking|monitor-await|semaphore-release-path|lock-order|configure-await|async-void|cancellation-policy|channel-complete|lock-api-mix",
  "lock_type": "lock|Lock|Monitor|Mutex|SemaphoreSlim|ReaderWriterLockSlim|SpinLock|null",
  "context": "library|app|test|entrypoint|unknown",
  "is_conditional": false,
  "condition": "조건부 위험이면 성립 조건",
  "detail": "메커니즘(왜 문제인지)",
  "current_code": "…",
  "fix_code": "동작 보존 수정. 동시성·메모리 타입 선언에는 CLAUDE.md 내부 동작 근거 // 주석, public 시그니처면 <remarks>",
  "necessary": false
}
```
- `context`: `library`(재사용 클래스 라이브러리 public/internal API) / `app`(ASP.NET Core 호스트·엔드포인트·서비스) / `test` / `entrypoint`(`Main`, `app.Run()`) / `unknown`. 데드락 심각도는 이 문맥으로 갈린다(스킬 참조).
- `necessary: true`(LF의 "필요한 락")는 감점에서 제외되고 LJ 감사 대상 대조에만 쓴다.
- `unknown` 문맥이면 severity 한 단계 하향.

---

## 워크플로우

### Phase 0: 실행 모드 결정

1. `latest.txt` 없음 → 초기 실행.
2. 있으면 이전 `00_input/meta.json`을 읽고:
   - **특정 도메인 재실행**("데드락만 다시", "락 정당화 재감사") → Phase 1로 같은 대상을 재수집해 `sha256` 비교. 동일 → 이전 run_dir 재사용, 요청 도메인만 재호출. **deadlock-analyzer를 다시 돌리면 deadlock-reviewer도 반드시 다시 돌린다**(`_r2`). 상이 → 새 실행으로 전환.
   - 새 코드/새 대상 → 새 run_id.
   - 재호출 실패 시 해당 도메인 "수집 실패". 옛 JSON 대체 사용 금지.

### Phase 1: 분석 대상 수집

`run_dir` 생성 후 `00_input/source.txt`·`meta.json`. Bash 도구, 상대 경로, **run_dir 안에만 쓴다**.

**케이스 A — 인수 없음** (우선순위, 근거를 `meta.target`에):
```bash
# A-1) 미커밋 .cs 변경(미추적 포함) → worktree
if [ -n "$(git status --porcelain -- '*.cs')" ]; then
  git diff HEAD -- '*.cs' > "$run_dir/00_input/source.txt"
  git ls-files --others --exclude-standard -- '*.cs' | while IFS= read -r f; do git diff --no-index -- /dev/null "$f" >> "$run_dir/00_input/source.txt" || true; done
fi
# A-2) 깨끗 + 기본 브랜치 아님 → branch (전부 실패 시 빈 $BASE 실행 금지, 사용자에게 질문)
for ref in main master origin/main origin/master; do BASE=$(git merge-base HEAD "$ref" 2>/dev/null) && break; done
git diff "$BASE" HEAD -- '*.cs' > "$run_dir/00_input/source.txt"
# A-3) BASE == HEAD → 최근 커밋 1개 → commit (리포트에 명시)
git diff HEAD~1 HEAD -- '*.cs' > "$run_dir/00_input/source.txt"
```
**케이스 D — 명시 범위**: `git diff <범위> -- '*.cs'`.
**케이스 B — 경로 지정** (파일 경계·줄번호 보존, `xargs cat` 금지):
```bash
find "$path" -type f \( -name '*.cs' -o -name '*.csproj' \) -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/_workspace/*' -print0 | sort -z \
  | while IFS= read -r -d '' f; do printf '=== FILE: %s ===\n' "$f"; cat -n "$f"; printf '\n'; done > "$run_dir/00_input/source.txt"
```
`.csproj`는 SDK 종류(Web/클래스 라이브러리)와 LangVersion 확인용 — `context` 판정 근거가 된다.
**케이스 C — PR**: `gh pr view N --json number,title,baseRefOid,headRefOid > 00_input/pr.json`, `gh pr diff N > source.txt`(경로 필터 없음, 리뷰어에게 `.cs`만 감사 지시), `git fetch origin pull/N/head`. 보충 조회는 `git show <head_sha>:<경로>`.

**공통 마무리:** 0바이트면 중지. `lines`·`sha256` 기록, `latest.txt` 갱신. 800줄 초과 시 `index.md`(파일 | 시작 줄 | async/lock 포함 메서드). 3000줄 초과 시 범위 축소 제안, 진행 시 그룹 분할(`_g1`…) 후 리뷰어에게 전부 전달.

### Phase 2: A단계 — 독립 병렬 락 감사 (단일 메시지에서 Agent 2회)

```
Agent(subagent_type="lock-free-enforcer", description="Lock-free audit",
      prompt="당신은 동시성 가드 팀의 Lock-Free 강제자입니다. 프로젝트 루트는 현재 작업 디렉토리입니다.
              run_dir={run_dir}, target_type={target_type}, head_sha={head_sha}.
              lock-free-enforcement 스킬로 {run_dir}/00_input/source.txt 를 처음부터 끝까지 읽고 모든 동기화 프리미티브를 판정하세요.
              id 는 LF-1… , 각 finding 에 context(library|app|test|entrypoint|unknown)·necessary 를 기록하세요.
              결과를 {run_dir}/02_lockfree_findings.json 에 저장하세요. 다른 에이전트와 통신하거나 결과를 기다리지 마세요.
              소스 수정 금지, Write 는 출력 파일에만. SendMessage 금지.
              최종 응답 첫 줄: {\"status\":\"done\",\"output\":\"<경로>\",\"counts\":{...},\"necessary\":N,\"score\":N,\"locks_found\":true|false}")
Agent(subagent_type="lock-justification-auditor", description="Lock justification audit",
      prompt="… lock-justification-audit 스킬로 … 모든 락 위치를 직접 탐지해(다른 에이전트의 목록을 기다리지 말고) 정당화 주석을 감사하세요.
              id 는 LJ-1… … {run_dir}/02_lockjustification_findings.json … 최종 응답 첫 줄: {…,\"locks_found\":true|false}")
```
두 최종 응답 후 **JSON 구조 검증**(필수 키 `domain, run_id, summary, findings[], counts, score, locks_found`; finding `id, severity, file, pattern, context, detail`; enum). 실패 → 1회 재호출 → "수집 실패".

### Phase 3: B단계 — 생성-검증 (순차)

1. **analyzer**:
```
Agent(subagent_type="deadlock-analyzer", description="Deadlock static analysis",
      prompt="… deadlock-static-analysis 스킬로 {run_dir}/00_input/source.txt 를 전부 읽고(참고: {run_dir}/02_lockfree_findings.json 의 락 위치)
              8패턴을 분석하세요. id DA-1…, context 와 is_conditional/condition 필수. 결과 {run_dir}/03_deadlock_analysis.json.
              SendMessage 금지, 소스 수정 금지. 최종 응답 첫 줄 {…,\"async_found\":true|false}")
```
2. **reviewer**(analyzer JSON 검증 후):
```
Agent(subagent_type="deadlock-reviewer", description="Deadlock review",
      prompt="… deadlock-review 스킬로 {run_dir}/03_deadlock_analysis.json 을 {run_dir}/00_input/source.txt 기준으로 독립 검증하세요.
              모든 DA-n 에 verdict(confirmed|rejected|modified)+final_severity, 누락은 DR-n 추가, final_findings 확정, fp_rate·final_score.
              재분석이 필요하면 needs_reanalysis:true 와 reanalysis_targets:[DA-id 또는 새 패턴 설명] 을 적으세요(직접 요청 불가).
              결과 {run_dir}/03_deadlock_review.json. SendMessage 금지.")
```
3. **재분석(최대 1회)**: reviewer JSON에 `needs_reanalysis === true`이고 `reanalysis_targets`가 비어 있지 않으면 analyzer를 `reanalysis_targets`와 함께 재호출 → `03_deadlock_analysis_r2.json` → reviewer 재호출 → `03_deadlock_review_r2.json`. 2라운드 후 남은 쟁점은 `disputed`로 리포트에 승계한다.
4. JSON 검증: reviewer `verdicts[]`(id·verdict·final_severity·reason), `additional_findings[]`, `final_findings[]`, `fp_rate`, `needs_reanalysis`(bool), `reanalysis_targets`(array), `final_score`. 실패 → 1회 재호출 → "검증 미완료".

### Phase 4: 결과 통합 및 리포트

1. **채택 파일**: 도메인별 최고 접미사. 데드락 도메인의 finding 집합은 reviewer `final_findings`(confirmed+modified+additional). reviewer 미완료면 analyzer 원본을 **미검증** 표시로 사용.
2. **교차 대조(오케스트레이터가 수행):**
   - LF `necessary: true` 락마다 같은 `file`의 LJ finding이 있는지 확인. 없으면 LJ가 놓친 것 → `unverified`에 "LJ 미탐지 락: LF-n"로 기록(감점 없음, 리포트 표기).
   - LF `lock-replaceable`인데 LJ가 `[LOCK-REQUIRED]` 충족으로 본 락 → 양쪽 유지하되 리포트에 "교체 가능하나 정당화됨"으로 묶는다.
   - 같은 위치의 `lock-order`가 LF와 DA에 모두 있으면 DA만 남기고 LF 점수 재계산.
3. **도메인 점수 재계산(결정적):** `necessary: true` 제외
   ```
   score_d = max(0, 100 − 25×critical − 12×high − 5×medium − 2×low)
   ```
   LJ 도메인은 critical이 없다(있으면 high로 강등). 에이전트 자가 점수와 5점 이상 차이면 병기.
4. **종합(성공 도메인 재정규화, 정수 반올림):** `overall = round(Σ w_d×score_d / Σ w_d)`, w = LF 0.35 · LJ 0.30 · 데드락 0.35. 검토 완료율 `k/3`.
5. **분석 대상 부재:** 세 도메인 모두 `locks_found:false`·`async_found:false`면 상태 "분석 대상 없음", 판정 "해당 없음".
6. **판정(우선순위):** ① 판정 보류 — 데드락 검증 미완료 또는 도메인 2개 이상 수집 실패 ② 해당 없음 ③ BLOCK — critical ≥ 1 또는 overall < 60 ④ REQUEST CHANGES — high ≥ 1 또는 overall < 80 또는 부분 검토 ⑤ APPROVE.
7. `{run_dir}/04_concurrency_guard_report.md`:
```markdown
# 동시성 가드 리포트
**생성:** … | **run_id:** … | **대상:** {target_type} — {target} (head …) | **검토 완료율:** k/3 | **데드락 검증:** 완료(라운드 N)/미완료

## 종합 건강 점수
| 도메인 | 점수 | Critical | High | Medium | Low | 상태 |
|---|---|---|---|---|---|---|
| 🔓 Lock-Free | XX | N | N | N | N | 성공 (necessary N건 제외) |
| 📝 락 정당화 | XX | — | N | N | N | 성공 |
| ⚡ 데드락 | XX | N | N | N | N | 검증 완료 (confirmed N·modified N·rejected N·추가 N, FP NN%) |
| **종합** | **XX** | … | 재정규화 가중치 |

## CRITICAL / HIGH  (id, pattern, 위치, context, 조건, 문제, 수정 코드)
## 락 정당화 미비 목록 (LJ, 필수 주석 템플릿 포함)
## 필요한 락 (LF necessary, 참고) · 교체 가능하나 정당화됨
## Medium / Low
## 기각된 발견 (DR rejected, 사유) · 쟁점(disputed)
## 검증 불가 항목
## 판정: APPROVE / REQUEST CHANGES / BLOCK / 해당 없음 / 판정 보류
```

### Phase 5: 보고
run_dir 보존. 리포트 본문 + 경로 안내.

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| source.txt 0바이트 | 중지, 대상 지정 요청 |
| merge-base 전부 실패 | 빈 `$BASE` 금지, 사용자에게 베이스/범위 요청 |
| 기본 브랜치·깨끗한 트리 | 최근 커밋 1개 + 리포트 명시 |
| A단계 에이전트 실패/검증 실패 | 1회 재호출 → "수집 실패", 판정 상한 REQUEST CHANGES |
| analyzer 실패 | 1회 재호출 → 데드락 도메인 "수집 실패", 판정 보류 |
| reviewer 실패 | 1회 재호출 → analyzer 원본을 미검증으로 표시, 판정 보류 |
| 재분석 2라운드 후 쟁점 잔존 | `disputed`로 승계, 판정은 확정 finding으로만 |
| 부분 재실행인데 해시 상이 | 새 실행으로 전환 |
| 최종 응답 미수신 | 능동 타임아웃 없음. 사용자에게 알리고 지시 시 "수집 실패" 확정 |

## 테스트 시나리오

**정상:** "Server/ 락 감사해줘" → 케이스 B(`=== FILE ===`) → A단계: LF `lock(_sync)` 3건(1 replaceable, 2 necessary) / LJ 3건 중 1건 `[LOCK-REQUIRED]` 없음(high) → B단계: DA `.Result`(context=library, critical conditional) 1건 + ConfigureAwait(library) 2건 → DR: `.Result` confirmed, ConfigureAwait 1건 `context=test`로 rejected → 재분석 불필요 → Phase 4: LF 92 / LJ 88 / 데드락 75 → overall 85, high 1건 → REQUEST CHANGES.
**재분석:** DR가 `needs_reanalysis:true, reanalysis_targets:["DA-2","semaphore-release-path in Cache.cs"]` → analyzer `_r2` → reviewer `_r2` → 리포트에 라운드 2 표기.
**에러:** reviewer JSON 검증 실패 2회 → 데드락 도메인 미검증, 판정 보류.
