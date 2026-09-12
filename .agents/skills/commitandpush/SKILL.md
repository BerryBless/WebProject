---
name: commitandpush
description: >
  Git 변경사항을 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 및 푸시까지 처리하는 자동화 파이프라인.
  '/commitandpush', '커밋해줘', '커밋하고 푸시해줘', '변경사항 올려줘', '깃 커밋', '깃 푸시', '자동 커밋',
  '커밋 메시지 만들어서 올려줘', 'git commit and push', '커밋 자동화' 요청 시 반드시 이 스킬을 사용할 것.
  후속 실행: '다시 커밋해줘', '커밋 재실행', '이전 커밋 수정', '보안 재검사 후 커밋', '푸시만 다시' 포함.
  민감 정보(.env, 하드코딩 키, API 토큰 등) 감지 시 커밋을 원천 차단한다.
---

# commitandpush 오케스트레이터

## 실행 모드: 서브 에이전트 순차 파이프라인 (Agent 도구, 최종 응답 보고)

```
git-security-auditor → git-commit-writer → git-push-controller
  (PASS/WARN/FAIL)        (메시지 생성)        (커밋 & 푸시)
```

**이 빌드에는 팀 도구가 없다.** 각 에이전트는 `Agent` 도구로 격리 실행되고 최종 응답 첫 줄 JSON으로 보고한다. 형제 통신 없음.

**Codex 세션 주의:** 이 스킬은 Claude Code 전용 파이프라인이다. Codex 세션은 AGENTS.md의 "커밋 규칙(Codex 세션 전용)"대로 직접 `git commit` 하며, 이 절차와 `.git/auto_commit_msg.txt`·`.git/harness_commit_in_progress`를 사용하지 않는다.

---

## Stop 훅과의 관계 (두 커밋 경로가 충돌하지 않게 하는 규칙)

Stop 훅(`scripts/auto-commit.ps1`)은 **턴이 끝날 때마다** 남은 변경을 커밋·푸시한다. 파이프라인 도중에 사용자에게 질문하면 그 순간 턴이 끝나고 훅이 먼저 커밋한다. 이를 막기 위해:

1. **Phase 0에서 센티널 `.git/harness_commit_in_progress`를 만든다.** 훅은 이 파일이 있으면(6시간 이내) 커밋을 건너뛴다.
2. **질문은 최소화한다.** 사용자가 이미 커밋을 요청했으므로 스테이지·메시지 확인을 다시 묻지 않는다. 질문이 필요한 경우는 두 가지뿐이다: 보안 WARN, 그리고 사용자가 "메시지 확인하고" 같은 조건을 명시했을 때.
3. **파이프라인이 끝나면(성공·중단 모두) 센티널을 삭제**하고, `.git/auto_commit_msg.txt`가 있으면 삭제한다.
4. **이 턴에서 스킬이 이미 커밋했으면 `.git/auto_commit_msg.txt`를 새로 쓰지 않는다** (남은 변경이 없으므로 훅은 조용히 종료한다).
5. 재개(사용자 답변 후)할 때는 센티널이 살아 있는지, 스테이지 해시가 같은지 확인한다.

---

## 절대 금지 규칙 (오케스트레이터 자체도 준수)

| 금지 명령 | 사유 |
|----------|------|
| `git config` **쓰기**(`--global`, `user.*` 변경 등) | 시스템 설정 불변. 읽기(`git config --get`)는 허용 |
| `git reset --hard` | 사용자 미동의 작업 손실 |
| `git clean -fd` | 추적 불가 파일 영구 삭제 |
| `git push --force`, `git push -f`, `--force-with-lease` | 원격 히스토리 강제 덮어쓰기 |
| `git rebase -i`, `git add -i` 등 `-i` | 인터랙티브 명령 지원 불가 |
| `git commit --amend` | 커밋 **실패** 후에는 절대 금지(이전 커밋에 합쳐짐). 성공한 이번 커밋에 hook 포맷 수정을 반영할 때만 SHA 확인 후 허용 |

---

## 작업 디렉토리

```
_workspace/git/
├── latest.txt
└── <run_id>/                 # YYYYMMDD_HHmmss
    ├── 00_meta.json          # head_sha, branch, staged_sha256, staged_files[], mode, created
    ├── 01_security_result.md
    ├── 02_commit_message.txt # UTF-8 무BOM
    └── 03_push_result.md
```
`.gitignore`의 `_workspace/*`로 커밋되지 않는다.

**00_meta.json:**
```json
{ "run_id": "20260913_013000", "mode": "full | push-only | resume",
  "head_sha": "…", "branch": "master", "remote": "origin",
  "staged_sha256": "sha256(git diff --staged)", "staged_files": ["…"],
  "user_options": { "confirm_message": false }, "created": "…" }
```

---

## Phase 0: 사전 점검 및 대상 확정

1. `git rev-parse --git-dir` 실패 → "git 저장소가 아닙니다" 안내 후 종료.
2. **센티널 생성:** `.git/harness_commit_in_progress` (내용: run_id). 이후 어떤 경로로 끝나든 Phase 4에서 삭제한다.
3. `run_dir` 생성, `latest.txt` 갱신.
4. 모드 결정:
   - `git status --porcelain`이 비어 있고 `git log @{u}..HEAD`에 미푸시 커밋이 있으면 → **push-only** (Phase 3의 push 단계만).
   - 변경이 없고 미푸시도 없으면 → "커밋할 변경사항이 없습니다" 후 종료(센티널 삭제).
   - 사용자가 "다시/재실행"을 요청했고 `latest.txt`의 이전 run에 `00_meta.json`이 있으면 → **resume 후보**: 현재 스테이지 해시를 계산해 이전 `staged_sha256`과 비교. 같으면 요청한 단계부터(메시지만 다시 → Phase 2, 재커밋 → Phase 3). 다르면 **Phase 1부터** 전체 재실행(보안 게이트 우회 방지).
   - 그 외 → **full**.
5. 스테이징: 스테이지된 파일이 없으면 `git add -A`를 실행한다(사용자가 특정 경로를 지정했으면 그 경로만). 묻지 않는다. 결과 보고에 "전체 스테이지함"을 명시한다.
6. `00_meta.json` 기록: `staged_sha256 = sha256(git diff --staged)`, 파일 목록, head_sha, branch, upstream.

---

## Phase 1: 보안 감사 (git-security-auditor)

```
Agent(subagent_type="git-security-auditor", description="Git security audit",
      prompt="당신은 commitandpush 파이프라인의 보안 게이트키퍼입니다. 프로젝트 루트는 현재 작업 디렉토리입니다.
              run_dir={run_dir}. 스테이지된 변경(git diff --staged)과 미추적 파일 목록을
              .claude/skills/commitandpush/references/security-patterns.md 의 정본 패턴으로 스캔하세요
              (파일별 grep 방식, 30,000자 출력 한계를 넘기지 않도록 매치 줄만 수집).
              결과를 {run_dir}/01_security_result.md 에 저장하고 판정은 PASS|WARN|FAIL 중 하나입니다.
              파일을 수정·삭제·마스킹하지 마세요. SendMessage 사용 금지.
              최종 응답 첫 줄: {\"status\":\"done\",\"verdict\":\"PASS|WARN|FAIL\",\"critical\":N,\"high\":N,\"medium\":N,\"low\":N,\"scanned_files\":N,\"output\":\"<경로>\"}")
```

- 최종 응답 첫 줄 JSON을 파싱하고 `01_security_result.md`의 판정과 일치하는지 확인한다(불일치 → FAIL 취급).
- **PASS** → Phase 2.
- **FAIL** → 파이프라인 중단. 파일:라인·유형·조치(`.gitignore` 추가, 값 환경 변수화, 이미 커밋된 비밀은 회전) 안내. 센티널 삭제. **스테이지는 건드리지 않는다.**
- **WARN(MEDIUM만)** → 발견 내용을 보여주고 진행 여부를 묻는다. 이때 턴이 끝나도 센티널이 훅을 막는다. 사용자가 진행을 답하면 Phase 0-4의 resume 절차(해시 비교)로 이어간다.
- 에이전트 실패/JSON 없음 → 1회 재호출 → 재실패 시 FAIL 취급(안전 우선).

---

## Phase 2: 커밋 메시지 작성 (git-commit-writer)

```
Agent(subagent_type="git-commit-writer", description="Write commit message",
      prompt="당신은 commitandpush 파이프라인의 커밋 메시지 작성자입니다. run_dir={run_dir}.
              git log --oneline -15 로 스타일을 학습하되 '자동 커밋(메시지 미전달)' 폴백 커밋은 학습에서 제외하세요.
              git diff --staged --stat 과 git diff --staged 를 분석해
              .claude/skills/commitandpush/references/commit-message-guide.md 규칙으로 WHY 중심 한국어 메시지를 작성하세요.
              접두사: 추가/수정/버그수정/리팩토링/문서/테스트/의존성. 제목 50자 이내, 파일명 나열 금지, 마지막 트레일러 블록에
              'Co-Authored-By: Codex <noreply@openai.com>'.
              {run_dir}/02_commit_message.txt 에 UTF-8(BOM 없음)으로 저장하세요. 커밋은 실행하지 마세요. SendMessage 사용 금지.
              최종 응답 첫 줄: {\"status\":\"done\",\"output\":\"<경로>\",\"prefix\":\"수정\",\"title_len\":N}")
```

- 저장된 파일 첫 줄이 `^(추가|수정|버그수정|리팩토링|문서|테스트|의존성): \S`에 맞는지, 트레일러가 있는지 오케스트레이터가 검증한다. 불일치 → 1회 재호출 → 재실패 시 사용자에게 수동 입력 요청(센티널 유지).
- 기본은 **미리보기만 출력하고 바로 Phase 3**. 사용자가 확인을 요구한 경우(`user_options.confirm_message = true`)에만 "이 메시지로 커밋할까요? (y/n/수정 내용)"를 묻고, `수정 내용`이 오면 오케스트레이터가 `02_commit_message.txt`를 **덮어쓴 뒤** Phase 3으로 간다.

---

## Phase 3: 커밋 & 푸시 (git-push-controller)

```
Agent(subagent_type="git-push-controller", description="Commit and push",
      prompt="당신은 commitandpush 파이프라인의 최종 실행자입니다. run_dir={run_dir}, mode={full|push-only}.
              (full) {run_dir}/02_commit_message.txt 를 검증(첫 줄 접두사, BOM 없음)한 뒤 git commit -F 로 커밋하세요.
              commit-msg 훅이 거부하면 메시지 첫 줄만 규칙에 맞게 고쳐 1회 재시도하고, pre-commit 실패 시 amend 는 절대 하지 마세요.
              (공통) upstream 을 git rev-parse --abbrev-ref @{u} 로 해석하고 git fetch 후 ahead/behind 를 확인한 뒤 push 하세요.
              upstream 없으면 --set-upstream origin <branch> 1회. non-fast-forward 면 push 하지 말고 상황을 보고하세요.
              보호 브랜치 여부는 확인 수단이 없으면 '미확인' 으로 적고, 승인이 필요한 상황은 needs_confirmation 으로 반환하세요(직접 묻지 마세요).
              결과를 {run_dir}/03_push_result.md 에 저장하세요. 커밋 성공 후 .git/auto_commit_msg.txt 가 있으면 삭제하세요.
              절대 금지: force push, reset --hard, clean -fd, git config 쓰기, -i 명령, 실패한 커밋 뒤 amend. SendMessage 사용 금지.
              최종 응답 첫 줄: {\"status\":\"done|needs_confirmation|failed\",\"commit\":\"<sha|null>\",\"pushed\":true|false,\"branch\":\"…\",\"upstream\":\"…\",\"reason\":\"…\",\"output\":\"<경로>\"}")
```

- `needs_confirmation` → 사유를 보여주고 사용자에게 묻는다(센티널 유지). 승인 시 `mode=push-only`로 Phase 3 재호출.
- `failed` → 사유와 조치를 보고하고 중단. 커밋은 됐는데 push만 실패한 경우 "다음에 '푸시만 다시'로 재시도 가능"을 안내한다.

---

## Phase 4: 정리 및 보고

1. `.git/harness_commit_in_progress` 삭제. `.git/auto_commit_msg.txt`가 남아 있으면 삭제.
2. **이 턴에서는 `.git/auto_commit_msg.txt`를 새로 쓰지 않는다.**
3. 결과 출력:
```
✅ 커밋 & 푸시 완료
─────────────────────────────
커밋: abc1234 (수정: …)
브랜치: master → origin/master (ahead 0)
보안 감사: PASS (스캔 N개 파일) | 스테이지: 전체 스테이지함
─────────────────────────────
```
실패·중단 시: `❌ 파이프라인 중단: [단계] — [사유]` + 조치. 센티널은 이 경우에도 삭제한다.

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| git 저장소 아님 | 즉시 중단 |
| 보안 에이전트 실패/JSON 불일치 | 1회 재호출 → FAIL 취급 |
| WARN 응답 대기 중 스테이지 변경 | resume 시 해시 불일치 → Phase 1부터 재실행 |
| 메시지 형식 불량 | 1회 재호출 → 수동 입력 요청 |
| commit-msg 훅 거부 | 컨트롤러가 첫 줄 교정 후 1회 재시도 → 실패 시 보고 |
| pre-commit 실패 | 새 커밋 없음. amend 금지. hook 출력 원문 보고 |
| push 인증/네트워크 실패 | 커밋 유지, "푸시만 다시" 경로 안내 |
| non-fast-forward | push 금지, `git fetch` 후 상황 보고. merge/rebase는 사용자 결정 |
| 센티널이 6시간 이상 남음 | 훅이 stale로 무시. 파이프라인 시작 시 새로 만든다 |

---

## 테스트 시나리오

### 정상 흐름
1. `/commitandpush` → Phase 0: 센티널 생성, `git add -A`, meta 기록
2. Phase 1 PASS → Phase 2 메시지 생성(미리보기) → Phase 3 `commit -F` + push
3. Phase 4: 센티널 삭제. 턴 종료 시 훅은 변경 없음으로 조용히 종료

### 보안 WARN
1. Phase 1 WARN(테스트 픽스처의 더미 토큰) → 사용자에게 질문 → 턴 종료
2. 훅 실행: 센티널 감지 → 커밋 건너뜀(systemMessage)
3. 사용자 "진행" → resume: 해시 동일 → Phase 2부터

### 푸시만 실패
1. Phase 3 commit 성공, push 인증 실패 → `failed` + 커밋 SHA 보고
2. 사용자 "푸시만 다시" → Phase 0 push-only 모드 → Phase 3 push만
