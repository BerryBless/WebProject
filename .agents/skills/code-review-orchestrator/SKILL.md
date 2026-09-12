---
name: code-review-orchestrator
description: "종합 코드 리뷰 하네스를 실행하는 오케스트레이터. 아키텍처·보안·성능·코드 스타일을 4개 에이전트가 병렬 감사하고 단일 리포트로 통합한다. 트리거(리뷰 의도가 명시된 요청에만): '코드 리뷰', '리뷰 해줘', '코드 점검', 'PR 검토', '코드 감사', '종합 리뷰', '전체 리뷰', '이 코드 봐줘', '/code-review-orchestrator'. 후속 작업: '다시 리뷰', '리뷰 업데이트', '보안만 다시', '아키텍처 재검토', '이전 리뷰 개선', '리뷰 보완'. 파일 경로·PR 번호가 언급되더라도 설명·수정·질문 요청이면 실행하지 않는다."
---

# Code Review Orchestrator

종합 코드 리뷰 팀을 조율하여 단일 리포트를 생성하는 오케스트레이터.

## 실행 모드: 에이전트 팬아웃/팬인 (Agent 도구)

**이 빌드에는 TeamCreate/TaskCreate/TaskGet/TeamDelete 팀 도구가 없다.** 서브에이전트는 `Agent` 도구로 띄우고, 각자 격리 실행 후 **최종 응답 1회**로 결과를 돌려주고 종료한다. 서브에이전트는 오케스트레이터 ID를 모르고 형제 에이전트와 통신할 수 없다. 따라서 완료 보고는 최종 응답으로만 받고, 중복 조율은 오케스트레이터가 Phase 4에서 수행한다.

## 에이전트 구성

| 에이전트 타입 | 역할 | 스킬 | 출력 (run_dir 기준) | 가중치 |
|-------------|------|------|------|------|
| architecture-reviewer | SOLID·레이어·결합도 감사 | /architecture-review | `02_architecture_findings.json` | 0.25 |
| security-reviewer | OWASP·CWE 기반 취약점 스캔 | /security-review | `02_security_findings.json` | 0.35 |
| performance-reviewer | N+1·async·LINQ 병목 탐지 | /performance-review | `02_performance_findings.json` | 0.25 |
| style-reviewer | 네이밍·복잡도·문서화 감사 | /style-review | `02_style_findings.json` | 0.15 |

## 작업 디렉토리 (다른 하네스와 격리)

`_workspace/`는 gc-guard·concurrency-guard·tdd·cross-verify 등 여러 하네스가 공유한다. **`_workspace/` 전체를 이동·삭제하지 않는다.** 이 하네스는 실행마다 자기 전용 디렉토리만 사용한다.

```
_workspace/code-review/
├── latest.txt                     # 가장 최근 run_id 한 줄
└── <run_id>/                      # run_id = YYYYMMDD_HHmmss
    ├── 00_input/
    │   ├── diff.txt               # 리뷰 대상 원본 (요약으로 대체 금지)
    │   ├── meta.json              # 대상 식별 정보 (아래 스키마)
    │   └── index.md               # 800줄 초과 시 파일별 색인 (보조 자료)
    ├── 02_{domain}_findings.json  # 도메인별 결과 (부분 재실행은 _r2, _r3 접미사)
    └── 03_consolidated_report.md
```

`.gitignore`의 `_workspace/*` 규칙으로 이 디렉토리는 커밋되지 않는다(`_workspace/cross/`만 재포함). 중간 산출물은 삭제하지 않는다.

**meta.json 스키마:**
```json
{
  "run_id": "20260912_201500",
  "target_type": "worktree | branch | commit | range | path | pr",
  "target": "사용자가 지정한 값 또는 자동 결정 근거",
  "base_sha": "…", "head_sha": "…",
  "pr_number": null,
  "files": ["src/A.cs", "src/B.cs"],
  "diff_lines": 412,
  "diff_sha256": "…",
  "created": "2026-09-12T20:15:00+09:00"
}
```

---

## 워크플로우

### Phase 0: 실행 모드 결정 (후속 작업 지원)

1. `_workspace/code-review/latest.txt`가 없으면 → **초기 실행**. Phase 1로 진행.
2. 있으면 이전 run의 `00_input/meta.json`을 읽고 요청을 분류한다:
   - **특정 도메인 재검토 요청** (예: "보안만 다시") → **부분 재실행 후보**. Phase 1을 같은 `target_type`/`target`으로 다시 실행해 diff를 새로 수집하고 `diff_sha256`을 비교한다.
     - 해시 동일 → 이전 run_dir 재사용. 요청 도메인만 Phase 2로 재호출하고 출력은 `02_{domain}_findings_r2.json`(이미 있으면 `_r3`). 나머지 도메인은 기존 JSON 유지. Phase 4에서 도메인별 **가장 높은 접미사 파일**만 채택한다.
     - 해시 상이 → 코드가 바뀌었으므로 부분 재실행 불가. 사용자에게 알리고 **새 실행**으로 전환한다.
     - 재호출이 실패하면 해당 도메인은 "수집 실패"다. **이전 JSON을 성공 결과로 대신 쓰지 않는다.**
   - **새 코드/새 대상/재리뷰 요청** → **새 실행**. 새 run_id 디렉토리를 만들고 Phase 1부터 시작한다. 이전 run_dir은 그대로 둔다.

---

### Phase 1: 리뷰 대상 수집

`run_dir=_workspace/code-review/<run_id>`를 만들고 아래 케이스 중 하나로 `00_input/diff.txt`와 `00_input/meta.json`을 작성한다. 모든 명령은 Bash 도구로 실행한다(상대 경로만 사용).

**케이스 A — 인수 없음.** 우선순위대로 판정하고 선택한 근거를 `meta.target`에 적는다.

```bash
# A-1) 미커밋 변경(스테이지·미스테이지·미추적)이 있으면 그것이 대상 → target_type=worktree
if [ -n "$(git status --porcelain)" ]; then
  git diff HEAD > "$run_dir/00_input/diff.txt"
  git ls-files --others --exclude-standard | while IFS= read -r f; do
    git diff --no-index -- /dev/null "$f" >> "$run_dir/00_input/diff.txt" || true
  done
  base_sha=$(git rev-parse HEAD); head_sha="WORKTREE"
fi
# A-2) 깨끗하고 기본 브랜치가 아니면 베이스 브랜치와 비교 → target_type=branch
#      merge-base 후보를 순서대로 시도하고, 전부 실패하면 빈 $BASE로 git diff를 실행하지 말고 사용자에게 베이스를 묻는다.
for ref in main master origin/main origin/master; do
  BASE=$(git merge-base HEAD "$ref" 2>/dev/null) && break
done
# BASE == HEAD 이면 기본 브랜치 자체이므로 A-3으로 간다.
git diff "$BASE" HEAD > "$run_dir/00_input/diff.txt"
# A-3) 깨끗하고 기본 브랜치 위(merge-base == HEAD)이면 최근 커밋 1개 → target_type=commit
git diff HEAD~1 HEAD > "$run_dir/00_input/diff.txt"
```
A-3을 택했으면 리포트와 최종 보고에 "최근 커밋 1개(`<sha>`)를 대상으로 했다"고 명시하고, 다른 범위를 원하면 `HEAD~3..HEAD`처럼 지정하라고 안내한다.

**케이스 D — 명시 범위** (예: `HEAD~3..HEAD`, `abc123..def456`): `git diff <범위>` → `target_type=range`.

**케이스 B — 경로 지정** (예: `/code-review-orchestrator WebProject.Api/`): 파일마다 경계 헤더와 원본 줄 번호를 보존한다. 파일명 없는 `cat` 출력은 금지.
```bash
# 대상 확장자: .cs .csproj .props .targets .json .cshtml .razor .xaml .sql .yml .yaml (bin/ obj/ 제외)
find "$path" -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' \
  -o -name '*.json' -o -name '*.cshtml' -o -name '*.razor' -o -name '*.xaml' -o -name '*.sql' -o -name '*.yml' -o -name '*.yaml' \) \
  -not -path '*/bin/*' -not -path '*/obj/*' | sort | while IFS= read -r f; do
  printf '=== FILE: %s ===\n' "$f"; cat -n "$f"; printf '\n'
done > "$run_dir/00_input/diff.txt"
```
`meta.files`에 목록을 기록하고 `head_sha=$(git rev-parse HEAD)`, 작업 트리가 더러우면 `target`에 `(dirty)`를 덧붙인다. → `target_type=path`

**케이스 C — PR 번호** (예: `/code-review-orchestrator 42`):
```bash
gh pr view 42 --json number,title,baseRefOid,headRefOid > "$run_dir/00_input/pr.json"
gh pr diff 42 > "$run_dir/00_input/diff.txt"
git fetch origin "pull/42/head" 2>/dev/null || true   # 보충 조회용. 실패해도 진행
```
`meta.base_sha/head_sha`는 pr.json 값으로 채운다. 리뷰어가 diff 밖 파일을 읽어야 할 때는 작업 트리가 아니라 `git show <head_sha>:<경로>`로 PR head 버전을 읽도록 Phase 2 프롬프트에 명시한다. → `target_type=pr`

**공통 마무리:**
- `diff.txt`가 비어 있으면(0바이트 또는 공백만) 즉시 중지하고 대상을 지정해 달라고 요청한다.
- `diff_lines=$(wc -l < diff.txt)`, `diff_sha256`(`sha256sum` 또는 `Get-FileHash`)을 meta.json에 기록한다.
- `_workspace/code-review/latest.txt`에 run_id를 쓴다.

**diff 크기 관리 (원본 보존 원칙):**
- `diff.txt`는 어떤 경우에도 원본 전체를 유지한다. **요약으로 대체하지 않는다.**
- 800줄 초과 시 `00_input/index.md`에 파일별 색인을 추가로 만든다: `| 파일 | +줄 | -줄 | diff.txt 시작 줄 | 주요 변경 한 줄 |`. 리뷰어는 이 색인으로 탐색하되 실제 판단은 `diff.txt`의 코드로 한다.
- 3000줄 초과 시 사용자에게 범위 축소를 먼저 제안한다. 그대로 진행하면 `index.md` 기준으로 파일을 2~3개 그룹으로 나누고, 도메인마다 그룹 수만큼 에이전트를 호출한다(출력은 `02_{domain}_findings_g1.json`, `_g2`…). Phase 4에서 그룹 파일을 하나로 병합한 뒤 점수를 계산한다.

---

### Phase 2: 병렬 감사 실행 (Agent 팬아웃)

**공통 실행 규칙:**
- 4개 에이전트를 **한 메시지 안에서 `Agent` 도구 4회 호출**로 동시에 띄운다(서브에이전트 타입 = 에이전트 파일명).
- 각 프롬프트에 반드시 포함: `run_dir`, 입력 파일, 출력 파일명, `target_type`과 `head_sha`, "diff.txt 전체를 읽을 것", "프로젝트 소스 수정 금지·Write는 출력 파일에만", "SendMessage 사용 금지, 최종 응답으로만 보고".
- 완료는 서브에이전트의 **최종 응답**으로 수신한다. 최종 응답의 건수·점수는 참고값일 뿐이며, 집계의 단일 근거는 JSON 파일이다.
- 에이전트 1개가 실패(에러 종료, 파일 미생성, JSON 검증 실패)하면 동일 프롬프트로 **1회** 재호출한다. 재호출 출력은 같은 파일명을 덮어쓴다. 재실패 시 해당 도메인은 "수집 실패"다.

프롬프트 템플릿:
```
Agent(subagent_type="architecture-reviewer", description="Architecture review",
      prompt="당신은 종합 코드 리뷰 팀의 아키텍처 리뷰어입니다. 프로젝트 루트는 현재 작업 디렉토리입니다.
              run_dir={run_dir}, target_type={target_type}, head_sha={head_sha}.
              architecture-review 스킬을 사용하여 {run_dir}/00_input/diff.txt 를 처음부터 끝까지 읽고 감사하세요
              (800줄 초과면 {run_dir}/00_input/index.md 로 탐색하되 판단은 diff.txt 의 실제 코드로 합니다).
              결과 JSON을 {run_dir}/02_architecture_findings.json 에 저장하세요.
              프로젝트 소스는 수정하지 마세요. Write 는 위 출력 파일에만 사용하고, 다른 파일은 읽기만 하세요.
              target_type=pr 이면 diff 밖 파일은 `git show {head_sha}:<경로>` 로 읽으세요.
              SendMessage 는 사용하지 말고, 완료 시 최종 응답 첫 줄에
              {\"status\":\"done\",\"output\":\"<경로>\",\"counts\":{\"critical\":N,\"high\":N,\"medium\":N,\"low\":N},\"score\":N} 을 적으세요.")
Agent(subagent_type="security-reviewer",     description="Security review",    prompt="… 02_security_findings.json …")
Agent(subagent_type="performance-reviewer",  description="Performance review", prompt="… 02_performance_findings.json …")
Agent(subagent_type="style-reviewer",        description="Style review",       prompt="… 02_style_findings.json …")
```

### Phase 3: 완료 수신 및 결과 검증

1. 4개 최종 응답을 모두 받은 뒤 도메인별로 상태를 확정한다: `성공 / 재시도 후 성공 / 수집 실패`.
2. **JSON 구조 검증** (파싱 성공만으로 신뢰하지 않는다). 하나라도 어긋나면 "검증 실패"로 보고 재호출 대상에 넣는다:
   - 필수 키: `domain`, `summary`, `findings`(배열), `score`(0~100 정수)
   - `domain` 값이 파일명의 도메인과 일치
   - 각 finding 필수 키: `severity`, `file`, `title`, `detail`, `suggestion` (security는 `cwe` 권장)
   - `severity` ∈ {critical, high, medium, low}. style 도메인에 `critical`이 있으면 `high`로 강등하고 리포트에 명시
3. 이 빌드에서 오케스트레이터는 능동 타임아웃을 걸 수 없다. 최종 응답이 오지 않는 에이전트가 있으면 사용자에게 상황을 알리고, 사용자가 진행을 지시하면 그 도메인을 "수집 실패"로 확정한다.
4. 수집 실패가 **2개 이상**이면 Phase 4 전에 사용자에게 진행 여부를 확인한다(자동으로 우회하지 않는다).

---

### Phase 4: 결과 통합 및 리포트 생성

1. 도메인별로 채택 파일(가장 높은 `_rN`/그룹 병합본)을 Read로 수집한다.

2. **중복 조율 (오케스트레이터가 수행):**
   - 같은 `file` 위치라도 관점이 다르면 각각 유지한다.
   - `file`·`severity`·`title`이 사실상 동일하면 더 관련성 높은 도메인 하나만 남긴다. 제거한 항목은 해당 도메인의 findings에서 빼고 **점수를 다시 계산**한다.

3. **도메인 점수 재계산 (결정적 산식, 에이전트 자가 점수는 참고값):**
   ```
   score_d = max(0, 100 − 25×critical − 10×high − 4×medium − 1×low)
   ```
   에이전트 보고 점수와 5점 이상 차이 나면 리포트 하단에 "자가 점수 N → 재계산 M"으로 남긴다.

4. **종합 점수 (성공 도메인 가중치로 재정규화, 정수 반올림):**
   ```
   overall = round( Σ_{d∈성공} w_d × score_d  /  Σ_{d∈성공} w_d )
   w = security 0.35, architecture 0.25, performance 0.25, style 0.15
   ```
   성공 도메인 수 `k/4`를 "검토 완료율"로 함께 표기한다.

5. **판정 (우선순위 순서로 첫 조건 채택):**
   1. **판정 보류** — security 도메인이 수집 실패
   2. **BLOCK** — critical ≥ 1 또는 overall < 60
   3. **REQUEST CHANGES** — high ≥ 1 또는 overall < 80 또는 수집 실패 도메인 존재(부분 검토는 승인 불가)
   4. **APPROVE** — 위 어디에도 해당하지 않음 (critical·high 없음, overall ≥ 80, 4/4 완료)

6. 아래 형식으로 `{run_dir}/03_consolidated_report.md`를 저장한다.

```markdown
# 종합 코드 리뷰 리포트
**생성:** {datetime}  |  **run_id:** {run_id}  |  **대상:** {target_type} — {target} (base {base_sha[:7]} → head {head_sha[:7]})  |  **검토 완료율:** {k}/4

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low | 상태 |
|--------|------|----------|------|--------|-----|------|
| 🏗️ 아키텍처 | XX / 100 | N | N | N | N | 성공 |
| 🔒 보안 | XX / 100 | N | N | N | N | 성공 |
| ⚡ 성능 | — | — | — | — | — | 수집 실패 |
| 🎨 스타일 | XX / 100 | — | N | N | N | 성공 |
| **종합** | **XX / 100** | **N** | **N** | **N** | **N** | 재정규화 가중치 |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15% (실패 도메인 제외 후 재정규화)

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [도메인] [SEVERITY] — 제목
**위치:** `파일명:라인`
**CWE:** CWE-XXX _(보안만)_
**문제:** 상세 설명
**수정:** 제안

_(없으면: "Critical/High 발견사항 없음 ✅")_

---

## Medium 발견사항 ← 권장 수정
_(없으면 생략)_

---

## Low / 정보성 ← 검토 권장
- [도메인] `파일명:라인` — 제목: 한줄 요약
_(없으면 생략)_

---

## 검증 불가 항목
- [도메인] 항목 — 사유 (예: 취약 의존성: 오프라인이라 `dotnet list package --vulnerable` 미실행)
_(없으면 생략)_

---

## 총평 및 판정
{3–5문장 종합 평가. 대상 결정 근거(A-3이면 "최근 커밋 1개") 포함}

**판정: APPROVE / REQUEST CHANGES / BLOCK / 판정 보류**
- APPROVE: Critical·High 없음, 종합 80+, 4/4 완료
- REQUEST CHANGES: High 존재 또는 종합 60–79 또는 부분 검토
- BLOCK: Critical 존재 또는 종합 60 미만
- 판정 보류: 보안 도메인 수집 실패

_(자가 점수와 재계산 점수가 5점 이상 다른 도메인이 있으면 여기에 표기)_
```

---

### Phase 5: 보고

1. 별도 팀 해제 절차 없음(서브에이전트는 완료와 함께 종료됨).
2. `run_dir`은 보존한다(중간 산출물 삭제 안 함, 사후 확인·부분 재실행용).
3. 사용자에게 리포트 본문을 출력하고 경로를 안내한다:
   - 상세 리포트: `{run_dir}/03_consolidated_report.md`
   - 도메인별 원본: `{run_dir}/02_{domain}_findings*.json`
   - 입력 스냅샷: `{run_dir}/00_input/`

---

## 데이터 흐름

```
사용자 요청
    │
    ▼
Phase 0: latest.txt / meta.json 확인 → 초기·부분 재실행·새 실행 결정
    │
    ▼
Phase 1: 대상 수집 → run_dir/00_input/{diff.txt, meta.json, index.md}
    │
    ▼
Phase 2: Agent 4개 동시 호출 (단일 메시지)
    │
    ▼
Phase 3: 최종 응답 4개 수신 → JSON 구조 검증 → 실패 도메인 1회 재호출
    ├── architecture-reviewer → 02_architecture_findings.json
    ├── security-reviewer     → 02_security_findings.json
    ├── performance-reviewer  → 02_performance_findings.json
    └── style-reviewer        → 02_style_findings.json
    │
    ▼
Phase 4: 중복 조율 → 점수 재계산 → 재정규화 종합 → 판정 → 03_consolidated_report.md
    │
    ▼
Phase 5: 사용자 보고
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| diff 없음 (0바이트) | 즉시 중지, 대상 지정 요청 |
| 베이스 브랜치 merge-base 전부 실패 | 빈 `$BASE`로 diff 실행 금지. 사용자에게 베이스 브랜치 또는 범위 요청 |
| 기본 브랜치·깨끗한 트리 | 최근 커밋 1개 대상(A-3) + 리포트에 명시 |
| 에이전트 실패 / 파일 미생성 / JSON 검증 실패 | 동일 프롬프트 1회 재호출 → 재실패 시 "수집 실패" |
| 수집 실패 2개 이상 | 사용자 확인 후 진행. 진행 시 판정은 최대 REQUEST CHANGES |
| 보안 도메인 수집 실패 | 리포트는 생성하되 판정 "보류" |
| 최종 응답 미수신 | 사용자에게 알림. 지시 시 "수집 실패"로 확정(능동 타임아웃 없음) |
| 부분 재실행인데 diff 해시 상이 | 부분 재실행 취소, 새 실행으로 전환 |
| style 도메인에 critical | high로 강등 후 리포트에 표기 |

---

## 테스트 시나리오

### 정상 흐름 (PR)
1. 사용자: "PR #15 리뷰해줘"
2. Phase 1 케이스 C: `gh pr view/diff 15` → `run_dir/00_input/{diff.txt, meta.json, pr.json}`
3. Phase 2: Agent 4개 동시 호출 (`head_sha`·`target_type=pr` 전달)
4. Phase 3: 최종 응답 4개 수신, JSON 4개 구조 검증 통과
5. Phase 4: 중복 1건 제거 → 점수 재계산 → 종합 → 판정
6. Phase 5: 리포트 출력. 예상: `03_consolidated_report.md` 생성, 검토 완료율 4/4

### 기본 브랜치·깨끗한 트리
1. 사용자: "코드 리뷰해줘" (master, 미커밋 변경 없음)
2. Phase 1 A-1 해당 없음 → A-2 merge-base == HEAD → A-3 최근 커밋 1개 채택
3. 리포트 상단과 총평에 "최근 커밋 `<sha>` 대상" 명시

### 부분 재실행
1. 사용자: "보안만 다시 봐줘"
2. Phase 0: 이전 meta.json으로 같은 대상을 재수집 → 해시 동일
3. security-reviewer만 재호출 → `02_security_findings_r2.json`
4. Phase 4: security는 `_r2`, 나머지는 기존 파일 채택 → 리포트 재생성

### 에러 흐름 (에이전트 1개 실패)
1. performance-reviewer가 JSON 없이 종료
2. 동일 프롬프트로 1회 재호출
3. 재실패 → performance "수집 실패". 종합은 3개 도메인 가중치로 재정규화, 검토 완료율 3/4
4. 판정은 최대 REQUEST CHANGES, 리포트에 "⚠️ performance 도메인 수집 실패 — 수동 확인 필요" 명시
