# 작업 재개 가이드 (2026-09-23 기준, Plan 4 병합 완료)

> 이 문서 하나만 읽으면 어느 세션에서든 이어서 작업할 수 있도록 쓴 인계 기록이다. 상태가 바뀌면 **이 문서를 갱신**하고 날짜를 고친다(새 파일을 만들지 않는다).

## 1. 지금 어디까지 왔나

| 단계 | 내용 | 상태 | 근거 |
|---|---|---|---|
| 설계 | 기술 블로그 스펙(PARA 노트앱 대체), Codex 교차 검토 반영 | 완료 | `plan/tech_blog_0920.md` |
| 0 | 솔루션 개명(`PortfolioBlog`), `.slnx` 전환, 샘플 제거 | 완료 | master |
| 1 | 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 비밀번호 로그인·세션, 글·시리즈·태그 관리 API | 완료 | PR #1 → `5604f2d` |
| 2A | 마크다운 파이프라인, `/api/preview`, 이미지 판정·메타데이터 제거, 이미지 첨부 | 완료 | PR #2 → `898b839`, 보고서 `plan/tech_blog_2a_report_0921.md` |
| 2B | 공개 Razor 페이지·검색·Atom·sitemap·보안 헤더·호스트 필터·공개 속도 제한·읽기 전용 DB 연결·렌더 게이트/캐시·첨부 정합성 | 완료 | PR #3 → `80c8dc4`, 보고서 `plan/tech_blog_2b_report_0921.md` |
| 3 | 관리 에디터 SPA(`PortfolioBlog.Web` — React 19 + Vite): 글·시리즈·태그·첨부 관리, sandbox 미리보기, 임시본, 실제 백엔드 Playwright E2E, CI `web`·`web-e2e` | 완료 | PR #4 → `16a3d25`, 보고서 `plan/tech_blog_3_report_0922.md` |
| **4** | Docker Compose·Caddy·DB 롤·백업/복원·스택 스모크 | **완료 — PR #5 → `531f207`(master 병합)** | 계획 `docs/superpowers/plans/2026-09-22-tech-blog-deploy.md`(S1~S16·D1~D15), 실행 보고서 `plan/tech_blog_4_report_0923.md` |
| TODO | **글쓰기와 보기를 노션처럼**(사용자 요청 2026-09-22, Plan 4 다음). 설계 전 — 브레인스토밍으로 방향부터(편집기 방식, 저장 형식, 공개 페이지는 스크립트 없는 서버 렌더링 유지) | 미착수 | 스펙 7절의 TODO 항목 |

- 기준 커밋: master `531f207`(PR #5 병합, 4단계 배포 구성까지 포함). 작업 트리 깨끗함, 열린 PR 없음.
- 검증 상태: .NET Release 빌드 경고 0 / 오류 0, 테스트 **625개 통과**. 웹: 타입 오류 0·린트 경고 0·Vitest **194개**·Playwright E2E **8개**(Chromium+Firefox)·스택 E2E **8개**. 하네스 감사 8/8, CI(ubuntu-latest: `test`·`web`·`web-e2e`·`deploy-smoke`) 통과.
- 남아 있는 것: 원격 브랜치 `origin/feature/blog-backend-core`, `origin/feature/blog-content-pipeline`, `origin/feature/blog-public-site`, `origin/feature/blog-admin-spa`, `origin/feature/blog-deploy`(전부 squash 병합 완료 — 지워도 된다. 아직 지우지 않았다).

## 2. 다시 시작할 때 5분 점검

```powershell
git switch master; git pull --ff-only
git status --short                         # 비어 있어야 한다
Test-Path .git/harness_commit_in_progress  # False여야 한다(True면 이전 실행이 중단된 것 — 4절)
Test-Path .git/hooks/commit-msg            # False면: Copy-Item scripts/git-hooks/commit-msg .git/hooks/
docker info --format '{{.ServerVersion}}'  # Docker가 떠 있어야 통합 테스트가 돈다
dotnet build PortfolioBlog.slnx -c Release # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release # 625개 통과 (한 개가 연결 타임아웃으로 실패하면 5절 참고 후 재실행)
cd PortfolioBlog.Web; npm ci; npm run lint; npm run typecheck; npm test; npm run build   # Vitest 194개
npm run e2e:prepare; npm run e2e; docker rm -f pb-e2e-pg; cd ..                          # E2E 8개(포트 7198·4173·5433이 비어 있어야 한다)
pwsh scripts/harness-audit.ps1             # PASS 8/8
```

필요 도구: .NET SDK 10.0.303, Docker Desktop, PowerShell 7, `gh`(GitHub CLI, 로그인됨), Python 3 + Pillow(이미지 픽스처·호환성 확인용, 테스트 실행에는 불필요), Codex CLI(교차 검증을 쓸 때만). Node.js 24(react-router 8이 22.22 이상 요구), Playwright 브라우저(`npx playwright install chromium firefox`).

## 3. Plan 4(배포) — 완료·병합됨

계획은 `docs/superpowers/plans/2026-09-22-tech-blog-deploy.md`(Task 6개, 스파이크 S1~S16, 설계 결정 D1~D15)에 있고 master `3a98985`로 올라가 있었다. 실행은 `superpowers:subagent-driven-development`로 브랜치 **`feature/blog-deploy`**에서 진행했다. 2026-09-22에 사용자 요청으로 한 차례 정지했다가 09-23에 재개해 Task 1~6(3.2 커밋 표)과 최종 리뷰(실제 스택 공격)를 전부 마치고 **PR #5로 master(`531f207`)에 병합됐다**(CI `test`·`web`·`web-e2e`·`deploy-smoke` 전부 통과, 2026-09-23). 상세 결함표와 판정 근거는 `plan/tech_blog_4_report_0923.md`에 있다. 진행 기록(ledger) `.superpowers/sdd/2026-09-22-tech-blog-deploy/`는 정리(삭제)될 예정이며, 그 기록은 이제 `plan/tech_blog_4_report_0923.md`와 git 이력에 남는다. **다음 작업은 노션식 에디터·보기다**(사용자 요청, 브레인스토밍으로 방향부터 시작) — 위 표의 TODO 항목.

### 3.1 재개 절차

```powershell
git switch feature/blog-deploy; git pull --ff-only      # origin에 push돼 있다
git log --oneline master..HEAD                          # 3.2 표의 커밋들이 보여야 한다(개수는 라운드가 늘수록 늘어난다 — 최신 개수는 이 출력으로 확인)
New-Item -ItemType File .git/harness_commit_in_progress  # 실행 중 Stop 훅 잠금(끝나거나 중단하면 삭제)
```

진행 기록(ledger)·지시서·리뷰 파일은 `.superpowers/sdd/2026-09-22-tech-blog-deploy/`에 있다(**git 무시 대상 — `git clean -fdx`로 사라진다**). 없어졌다면 계획 문서에서 `scripts/task-brief`로 지시서를 다시 뽑고, 아래 3.3의 남은 결함 목록으로 이어가면 된다. 컨트롤러 검증은 `bash .superpowers/sdd/2026-09-22-tech-blog-deploy/verify.sh <BASE> [dotnet|web|none]`(커밋 트레일러·접두사·NUL·줄 끝·비밀값·빌드·테스트).

### 3.2 어디까지 했나

| 커밋 | 내용 | 상태 |
|---|---|---|
| `4ffb9fa` | Task 1: 공개 조회 전용 DB 롤(`PublicRoleGrants`)·시작 검증 강화·헬스체크 CLI | |
| `b5c686f` | Task 2: `.gitattributes`·`.dockerignore`·`deploy/Caddyfile`·이미지 2개·`caddyfile.test.ts` | |
| `17f4ed3` | Task 3: `deploy/docker-compose.yml`·DB 롤 init·`.env.example`·스모크(`smoke.test.mjs`·`run.sh`) | |
| `7efab39` | Task 1 수정 r1(리뷰 F1~F9: PUBLIC·소유 테이블로 회수 범위 한정) | |
| `140624c` | Task 2 수정 r1(리뷰 F1~F8: 캐디 오류 경로·평문 HTTP·메서드 게이트) | |
| `88310ee` | Task 1 수정 r2(비 superuser 소유자 회귀 가드 테스트) | **Task 1 완료**(재리뷰 APPROVE) |
| `17063f3` | 문서: master의 문서 재구성·계획서 결함 표시를 배포 브랜치에 병합 | |
| `1452204` | Task 2 수정 r2(관리 헤더 위치 가드 강화, Caddy 폴백·오류 응답 빈틈) | **Task 2 완료** |
| `d14c101` | 문서: 단계별 고민과 판정을 작업일지로 남김 | |
| `8522c03` | Task 3 수정 r1(api 아웃바운드 차단, DB 롤 검사 trust 우회 수정) | |
| `3f20cc1` | Task 3 수정 r2(보안 경계 주석을 실측대로 정정, 폴백 접근 로그) | **Task 3 완료** |
| `36e8394` | Task 4: 백업·복원 리허설, 운영 절차 문서화(`deploy/OPERATIONS.md`) | |
| `dc4a207` | Task 4 수정 r1(복원 실패 안내, 비밀번호 로그 유출 등 리뷰 결함) | **Task 4 완료** |
| `76e076a` | Task 5: 배포 스택 전체를 브라우저 E2E로 매 PR마다 검증, CI `deploy-smoke` | |
| `10324b0` | 문서: 허용 IP 주석의 원인을 실측 네트워크 구조로 정정 | |
| `b72bbf6` | Task 5 수정 r1(허용 IP를 고정하고 실패 시 접근 로그로 진단 가능하게 함) | |
| `b8265a0` | Task 5 수정 r2(첨부 이미지 로드를 기다려 새로고침 레이스를 없앰) | **Task 5 완료** |
| `002a01d` | Task 6: 스펙·README·`docs/`·CLAUDE.md/AGENTS.md를 as-built로 반영 | **Task 6 완료** |
| `e5a8bcb` | 문서: 상태 헤딩과 어긋난 본문·라벨·커밋 표를 실제와 맞춤(리뷰 F1~F3) | |
| `53adc6d` | 최종 리뷰 Minor 1·2 수정: handle_errors 헤더 미러 테스트 분리, 계획 문서 dotnet test 주석 갱신 | **최종 리뷰 반영 완료** |

검증 상태: .NET **625개** 통과·빌드 경고 0, Vitest **194개**, `bash deploy/smoke/run.sh` **exit 0**(허용 IP 10·비허용 IP 6·오류 응답 1, 복원 리허설 포함), `SMOKE_E2E=1`의 스택 E2E **8개**(Chromium·Firefox). Task 1~6 전부 완료(위 표).

### 3.3 다음 작업

**완료·병합됨.** Task 1~6(DB 롤·이미지·Caddyfile·compose·스모크·백업/복원·스택 E2E·CI `deploy-smoke`·as-built 문서)을 전부 구현·커밋하고 최종 리뷰 뒤 PR #5로 master(`531f207`)에 병합했다. 상세 결함표와 수정 라운드별 근거는 `plan/tech_blog_4_report_0923.md`에 있다. **다음 작업은 노션식 에디터·보기다** — 브레인스토밍으로 방향부터 정한다(편집기 방식, 저장 형식; 공개 페이지는 스크립트 없는 서버 렌더링 유지).

### 3.4 실행하며 확인된 사실(계획에 없던 것)

- **ACME HTTP-01은 명시 `http://` 사이트 블록·IP 게이트와 공존한다** — 리뷰어가 로컬 ACME CA를 허용 목록 밖에 두고 **실제 발급**으로 증명했다(챌린지 핸들러가 `@denied respond 404`보다 앞선다). 챌린지 비진행 시 `/.well-known/acme-challenge/*`는 308.
- `ip_range: 172.30.0.128/25`가 caddy의 고정 IP 172.30.0.2를 실제로 지킨다(없으면 동적 컨테이너가 가져간다 — 대조 실측).
- 비 superuser `blog_app`으로 `PublicRoleGrants.Apply`가 성공하고 `\dp`에 허용 5테이블만 `blog_public=r`로 남는다(운영 형태 검증).
- 서브에이전트의 커밋 트레일러가 세 번 모두 세션 모델 이름으로 들어갔다 → 컨트롤러가 push 전에 `git commit --amend`로 정정했다. 지시에 트레일러 원문을 넣어도 반복되니 **매 커밋 뒤 `verify.sh`로 확인**한다.
- 리뷰어·구현자가 같은 기계의 Docker를 공유한다 — 스모크는 172.30.0.0/24·포트 8081·8443·프로젝트 `pb-smoke`를 쓰므로, 동시에 두 개를 돌리지 않고 리뷰어에게는 Testcontainers(무작위 포트)만 쓰게 한다.

### 3.5 Plan 4가 이어받은 사실(1~3단계에서 확정된 것)

1. **관리 사이트 헤더:** Caddyfile의 관리 사이트 블록은 `PortfolioBlog.Web/admin-headers.ts`의 다섯 헤더를 옮기고 **HSTS를 따로 더한다**(그 파일에는 의도적으로 없다). 구현 완료 — 일치는 `caddyfile.test.ts`(정적)와 `deploy/smoke`(실제 응답)가 본다.
2. **관리 사이트 라우팅:** `/api/*`·`/attachments/*`만 백엔드로. SPA 라우트 `/attachments`가 백엔드로 가면 안 된다. `/assets/없는파일`은 404. COOP 추가됨.
3. **SPA 빌드:** `PortfolioBlog.Web/Dockerfile`(node 24 빌드 → caddy 이미지). `npm ci` + `npm audit --omit=dev --audit-level=high`(`NPM_AUDIT=off`로만 끈다).
4. **공개 origin:** SPA는 공개 사이트 주소를 모른다. 필요하면 `VITE_PUBLIC_ORIGIN`.
5. **백엔드(2B에서):** 헬스체크는 `Host: <공개 호스트>` 필요(앱의 `healthcheck` CLI가 처리). `ConnectionStrings:Default`에 `Options` 금지, `Command Timeout`×1000 > `Public:StatementTimeoutMs`. Caddy `request_body`는 **11MiB**. 저장 볼륨은 앱 시작 전 마운트. multipart 버퍼가 임시 폴더에 쓴다(`tmpfs /tmp`). publish 출력에 EF 디자인 타임 어셈블리는 없다(스파이크 S2). 이미지의 `wwwroot`는 `css/site.css` 하나(빌드 단계에서 검사).
6. **브라우저:** WebKit(Safari) 미검증. 파일 입력의 `sr-only` 포커스 가능성 미확인.
7. 공개 사이트 CSS를 바꾸면 `PortfolioBlog.Web/public/preview/*.css` 스냅숏을 갱신해야 한다(안 하면 `PreviewCssSnapshotTests` 실패).

## 4. 계획 실행 중에 끊겼다면 (Subagent-Driven Development)

- 진행 기록은 `.superpowers/sdd/<계획 파일 이름>/progress.md`에 있다(git 무시 대상). 첫 줄이 그 계획 파일을 가리키면 `Task N: complete`가 찍힌 작업은 **끝난 것**이다 — 다시 맡기지 않는다. 마지막 줄이 수정 라운드면 그 라운드부터 잇는다. 기록이 없으면 `git log`로 복구한다.
- 실행 중에는 Stop 훅의 자동 커밋을 막는 센티널 `.git/harness_commit_in_progress`를 둔다(6시간 유효, 작업마다 `touch`로 갱신). 실행이 끝나거나 **중단을 확정하면 지운다** — 남아 있으면 훅이 아무것도 커밋하지 않는다.
- 컨트롤러가 매 커밋마다 직접 확인한 것: Release 빌드 경고 0, 전체 테스트, 커밋 트레일러(`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`), 변경 텍스트 파일의 0x00 바이트, 새 `.cs`·`.cshtml`·`.css`의 줄 끝(CRLF). 2B에서는 이것을 작업공간의 `verify.sh <BASE>` 하나로 묶었다(테스트 출력 보관, 실패 시 FAIL) — 다음 실행에서도 먼저 만든다.
- 리뷰어에게는 diff 파일(`scripts/review-package PLAN BASE HEAD`의 출력)과 지시서 경로만 넘긴다. 구현자 보고는 **검증 대상**이다.
- 서브에이전트의 완료 보고가 늦거나 오지 않을 때가 있다(2B에서 2회). 결과를 가정하지 말고 저장소를 직접 본다(커밋, 작업 트리, 보고서 파일, 남은 dotnet 프로세스, 사보타주 복구 여부). 지시문에 "응답을 마지막 행동으로 하라"를 넣는다.
- 효과가 컸던 것: 리뷰어에게 "계획 코드 자체가 틀렸을 수 있다 — 직접 공격하고 측정하라"고 명시, 새·고친 테스트마다 **사보타주로 실패를 확인**(규칙 8), 최종 리뷰에서 **실제 Production 호스트를 HTTPS로 찔러 보기**(2B에서 TestServer 스위트 579개가 놓친 결함 3건을 여기서 찾았다), 첫 Linux CI를 게이트로 취급.
- 리뷰어는 **저장소 밖 `git archive` 복사본에서만** 측정하게 한다(`node_modules`는 정션으로, 끝나면 정션만 비재귀 제거 — `tsconfig.node.json`까지 꺼내야 vitest가 뜬다). 3단계에서 리뷰어 1명이 작업 트리에 임시 파일을 만들었다 지웠다. 리뷰 N과 구현 N+1을 겹쳐 돌릴 때는 서로 다른 폴더일 때만, 구현자끼리는 절대 겹치지 않는다.
- **규칙 8의 함정:** 사보타주가 통과해 버리면 테스트의 전제 조건이 실제로 성립했는지 본다(3단계에서 세 번: 지울 임시본이 애초에 없었다 ×2, 가짜 범위가 현재 소스에 없는 구문이라 적용되지 않았다).
- **보안 통제(소스 가드·테스트 하네스)도 제품 코드만큼 의심한다.** 3단계의 정규식 기반 소스 가드는 세 번 연속 다른 입력에서 코드를 삼켜 그 구간의 위반을 놓쳤고, 테스트 도구의 "표에 없는 호출은 실패"는 거짓이었다. 리뷰어에게 독립 정본을 만들어 전수 비교하게 한다.
- 화면 상태 기계(편집 화면)는 수정 라운드가 많이 든다(3라운드 + 최종 물결) — 계획에 상태 전이 표(저장 중 입력, 저장 직후, 언마운트, 세션 만료, 저장소 실패)를 먼저 그린다.
- 계획을 쓸 때: **기반을 바꾸는 작업에는 "기존 소비자 목록"을 넣는다.** 2B에서 새 공개 DB 연결을 만든 작업이 2A의 공개 첨부 핸들러를 옮기지 않아 최종 리뷰에서야 발견됐다.

## 5. 이 저장소에서 자주 밟는 함정

| 함정 | 증상 | 대처 |
|---|---|---|
| 셸 heredoc이 백슬래시를 망가뜨린다 | `\n`이 든 Python/C# 조각을 heredoc으로 쓰면 치환이 안 맞는다 | 스크립트는 Write 도구로 파일에 쓰고 실행한다 |
| NUL 표기 | 6글자 유니코드 이스케이프가 실제 0x00 바이트로 바뀌어 파일에 들어간 전례(2회) | 소스에는 C# `\0` 이스케이프나 `0x00` 바이트 리터럴만. 커밋 전 바이트 스캔 |
| Stop 훅 비밀값 스캐너 | `password = "…"` 꼴의 줄이 있으면 커밋 차단 | 테스트 값에 `dummy` 같은 자리표시자 단어를 넣는다 |
| Stop 훅 자동 커밋 | 에이전트의 작업 중간 상태가 커밋된다 | 실행 중 센티널 유지(4절) |
| 로컬 간헐 테스트 실패 | 589개 중 1개가 정확히 15초 만에 실패(`Database.Migrate()`의 연결 타임아웃, 실행 시간 33초 → 48초). 빌드 직후 등 기계가 바쁠 때, Windows Docker Desktop에서만. 2B 실행 중 1회 관측 | 재실행. Linux CI에서는 나오지 않았다. 근본 대책 후보: 테스트 클래스 간 DB 공유 |
| 느린 CI 러너 | 절대 시간 상한 테스트가 깨진다(로컬 0.8초 → CI 7.2초) | 비율·상대 단언을 쓴다. 시간은 `TimeProvider`로 주입한다(2B에서 렌더러에 도입) |
| `Accepts` 메타데이터 | Content-Type이 안 맞으면 라우팅이 인가보다 먼저 415(핸들러는 실행되지 않음) | 접근 매트릭스는 엔드포인트가 받는 형식으로 본문을 보낸다 |
| 줄 끝 | 작업 트리는 CRLF(`autocrlf=true`), `CLAUDE.md`·`AGENTS.md`·`plan/*.md` 일부는 LF | 파일의 기존 스타일 유지, 한 파일 안에서 섞지 않는다 |
| Codex 실행 뒤 | `.agents/skills/{codex,cross-verify}`·`.codex/agents/cross-*.toml`이 다시 생길 수 있다 | 턴 끝에 untracked 확인 후 삭제(미러 제외 대상) |
| 이 PC의 AdGuard | 평문 HTTP(`http://127.0.0.1:…`) 응답의 HTML `<head>`에 `<script src="//local.adguard.org…">`를 **주입하고 CSP 헤더까지 다시 쓴다**(`default-src local.adguard.org …`). curl·pwsh 소켓도 가로채고 h2c는 끊는다(2B 최종 리뷰 실측) | 실제 호스트 프로브는 **개발 인증서로 HTTPS 엔드포인트를 띄워 `curl -k`로 직결**한다. 평문 HTTP에서 본 헤더·본문은 믿지 않는다. TestServer(인메모리)는 영향 없음 |
| Razor Pages의 `page` | 핸들러 매개변수 `int? page`가 쿼리 문자열이 아니라 예약 라우트 값(`/Index`)을 바인딩하려다 실패한다 | `Request.Query["page"]`를 직접 읽는다(`PageNumber`). 링크도 `asp-route-page`를 쓰지 않는다 |
| MVC 바인딩의 공백 → null | 경로 값이 공백뿐이면(`/tags/%20`) `string` 매개변수가 **null**로 들어와 NRE → 500(2B 최종 리뷰 실측) | 핸들러 매개변수는 `string?`, 첫 줄에서 `IsNullOrWhiteSpace` → 404. 쿼리 값은 `Request.Query`에서 직접 읽는다 |
| `srcdoc` iframe의 CSP `'self'` | Firefox는 `about:srcdoc` 문서의 `'self'`를 부모 출처로 보지 않는다 — 미리보기의 CSS·이미지가 전부 차단된다(Plan 3 스파이크 실측, Chromium은 허용) | CSP에 출처를 명시한다(`img-src https://host`). 브라우저 동작에 기대는 것은 Chromium·Firefox 둘 다에서 잰다 |
| Razor와 한글 | `@Model.Total건`은 컴파일되지 않는다(한글을 식별자 문자로 읽는다) | `@(Model.Total)건` |
| 프레임워크 기본값 | 호스트 필터의 400이 HTML 본문을 보낸다(`IncludeFailureMessage` 기본 true), publish가 `wwwroot`에 `.gz`·`.br` 사본을 만든다(`CompressionEnabled` 기본 true) — 둘 다 TestServer에서는 안 보인다 | 2B에서 둘 다 껐다. 새 미들웨어·SDK 기능을 켤 때는 publish 출력과 실제 호스트 응답을 확인한다 |
| EF Core 10 런타임 모델 | `db.Model.GetCheckConstraints()`가 예외를 던진다 | `db.GetService<IDesignTimeModel>().Model`을 쓴다 |
| 컨텍스트가 둘 | `dotnet ef`가 컨텍스트를 고르지 못한다 | `--context AppDbContext`(마이그레이션은 관리 컨텍스트에만) |
| 미리보기 CSP `'self'`(스펙 원문, S7) | Firefox에서만 `{ sheets, image }` 단언이 0으로 실패한다(위 "`srcdoc` iframe의 CSP `'self'`" 행과 같은 원인) — Task 8 E2E 사보타주로 재확인 | 관리 origin을 명시한다(`img-src <origin>; style-src <origin>`), `'self'`를 쓰지 않는다 |
| Vitest가 Playwright 스펙을 집는다(S9) | 기본 include가 `e2e/*.spec.ts`까지 실행해 실패한다 | `vitest.config.ts`의 `test.include`를 `src/**/*.test.{ts,tsx}`로 좁힌다 |
| Testing Library 자동 정리 미등록(S9) | 컴포넌트 테스트마다 이전 테스트의 DOM이 남는다 | `setupFiles`(`src/test/setup.ts`)에서 `@testing-library/react`의 자동 cleanup을 등록한다 |
| 로그아웃에서 `queryClient.clear()`(S12) | 로그인 화면으로 가지 않는다 — `RequireAuth`의 관찰자가 없어진 쿼리 객체에 매달린 채 남아 새 값을 보지 못한다 | `setQueryData(ME_KEY, { authenticated: false })` + `removeQueries`(`ME_KEY` 제외)로 바꾼다 |
| E2E에서 저장 버튼 `disabled`로 저장 완료를 판단 | Chromium에서 간헐 실패: 버튼은 요청 중에도 disabled라 "disabled가 됐다"만으로는 응답이 왔는지 알 수 없다. 실측: 응답 전에 다른 컨텍스트를 닫아 PUT이 취소되고 409가 나지 않았다 | `page.waitForResponse(r => r.request().method() === 'PUT' && r.status() === 200)`로 응답 자체를 기다린다 |

## 6. 문서 지도

| 무엇을 알고 싶나 | 어디 |
|---|---|
| 제품이 무엇이고 왜 이렇게 설계했나 | `plan/tech_blog_0920.md`(스펙, 구속력 있는 기준 — 2B에서 3.3~3.8이 as-built로 갱신됐다) |
| 2A에서 무엇을 만들고 어떻게 검증했나, 내린 판정 12건, 잔여 위험 | `plan/tech_blog_2a_report_0921.md` |
| 2B에서 무엇을 만들고 어떻게 검증했나, 계획 결함과 교훈, 내린 판정 28건, 잔여 위험, Plan 3·4 인계 | `plan/tech_blog_2b_report_0921.md` |
| 3단계(관리 SPA)에서 무엇을 만들고 어떻게 검증했나, 계획 코드의 결함 약 20건과 교훈, 내린 판정 16건, 잔여 위험, Plan 4 인계 | `plan/tech_blog_3_report_0922.md` |
| 계획 코드가 틀렸던 곳과 다음 계획이 지킬 규칙 1~9 | 1단계·2A 구현 계획 문서의 끝 "구현 중 발견해 고친 계획 결함", 2B는 보고서 4절 |
| 구현 계획(TDD 단계별) | `docs/superpowers/plans/2026-09-20-…backend-core.md`, `…2026-09-21-…content-pipeline.md`, `…2026-09-21-tech-blog-public-site.md`(앞의 스파이크 표 S1~S12·설계 결정 D1~D12), `…2026-09-21-tech-blog-admin-spa.md`(S1~S13·D1~D16, 끝에 구현 중 고친 계획 결함) |
| 프로젝트 규칙(주석·커밋·하네스·경로) | `CLAUDE.md`(Codex용 미러 `AGENTS.md` — 함께 고친다) |
| 하네스 변경 이력 | `plan/harness_changelog.md` |
| 로컬 실행 방법, 설정 키 표, API 예시 요청 | `README.md`, `PortfolioBlog.Api/PortfolioBlog.Api.http` |

## 7. 코드 지도 (3단계 이후)

```
PortfolioBlog.Api/
├─ Program.cs                      # 파이프라인: (호스트 필터) → 보안 헤더 → forwarded headers → 예외 처리(과부하 503)·상태 코드 페이지 → 정적 파일 → 관리 게이트(IP) → 속도 제한 → 인증 → 인가 → /api 본문 상한(401이 413보다 먼저)
├─ Domain/                         # Post · Series · Tag · PostTag · AdminState · Attachment
├─ Contracts/                      # DTO, TextRules(NUL 거부), ValidationErrors
├─ Features/
│  ├─ ApiEndpoints.cs              # 보호된 /api 그룹(RequireHost + RequireAuthorization)
│  ├─ Auth/ Posts/ Series/ Tags/   # 1단계 (Posts는 렌더 게이트·캐시 선채움 가드)
│  ├─ Preview/                     # POST /api/preview
│  └─ Attachments/                 # 관리 업로드(advisory lock)·목록·삭제 + 공개 GET/HEAD(PublicDbContext)
├─ Pages/                          # 공개 Razor 페이지: Index · Post · Tag · Series · Search, _Layout · _PostList · _TagList · _Pager,
│                                  # PublicPageConvention(GET/HEAD·공개 호스트·속도 제한), PageNumber, SiteEndpoints(feed.xml · sitemap.xml · robots.txt · /css/highlight.css)
├─ wwwroot/css/site.css            # 유일한 정적 파일(id 선택자 금지)
└─ Infrastructure/
   ├─ Access/                      # CIDR 허용 목록, AdminSurfaceMiddleware, 세션 검증, StartupValidation(fail-fast — 연결 문자열 검사 포함)
   ├─ Data/                        # AppDbContext(관리) · PublicDbContext(읽기 전용·statement_timeout) · PublicQueries · PublicModels, 마이그레이션 2개
   ├─ Markdown/                    # MarkdownRenderer(TimeProvider) · RenderGate · RenderedPostCache · UrlPolicy · HighlightingCodeBlockRenderer · HeadingIds · HtmlAllowlist · HighlightCss
   ├─ Storage/                     # ImageSignature · MetadataStripper · FileSystemAttachmentStore · AttachmentLock · AttachmentJanitor
   └─ Web/                         # ClientIp · RateLimit* · PublicOptions · SecurityHeadersMiddleware · ErrorResponses · OverloadExceptionHandler · ApiBodyLimitMiddleware · PublicUrls · XmlText
PortfolioBlog.Api.Tests/           # 591개(PreviewCssSnapshotTests — 관리 SPA의 미리보기 CSS 사본이 원본과 같은지). ApiFactory(팩토리별 DB·임시 첨부 루트·청소 잡 꺼짐), AccessMatrixTests(닫힌 세계), PublicSeed, HtmlDoc, SteppingTimeProvider
```

```
PortfolioBlog.Web/                 # 관리 SPA. admin-headers.ts(보안 헤더 정본) · vite.config.ts(HTTPS, 프록시 ^/api/ · ^/attachments/) · scripts/e2e-prepare.mjs
├─ src/api/                        # client(fetch의 유일한 자리: CSRF 헤더·same-origin·redirect error·경로 검사) · errors · endpoints · types
├─ src/lib/                        # safeNext(정규화 뒤 재검사) · validation(서버 규칙의 사본) · drafts(localStorage의 유일한 자리) · previewDoc(CSP에 출처 명시) · markdownImage
├─ src/app/ · src/auth/            # queryClient(401 전역 기록) · routes(오류 경계) · RequireAuth · LoginPage
├─ src/components/                 # PreviewPane(sandbox="" iframe — 서버 HTML의 유일한 자리) · MarkdownEditor(CodeMirror) · TagInput · ConflictPanel · RouteError
├─ src/pages/                      # Posts · PostEditor(지연 로딩) · Series · Tags · Attachments
├─ src/test/                       # Vitest 188개. harness(stubApi — 표 밖 호출은 실패) · stripComments(TS 파서) · source-guards(전체 소스 금지 패턴)
└─ e2e/admin.spec.ts               # Playwright 8개: 실제 백엔드 + 배포용 CSP, CSP 위반 0건
```

파일 위치는 위 트리가 어긋나면 코드가 맞다(`Glob`으로 확인). 의존 방향: `Features·Pages → Infrastructure → Contracts → Domain`. Feature끼리, `Pages`와 `Features`는 서로 참조하지 않는다. 패키지 버전은 `Directory.Packages.props`가 일괄 관리한다(2B에서 새 패키지·마이그레이션 없음).

## 8. 사용자가 정해 둔 것 (다시 묻지 않는다)

- **보안이 최우선.** 선택지가 갈리면 더 엄격한 쪽.
- 글은 상태 없음 — 저장 즉시 공개. PARA·노션 가져오기/내보내기는 폐기.
- 공개 페이지는 서버 렌더링(Razor + Markdig), React는 관리 에디터에만. 관리 표면은 `admin.<도메인>` 서브도메인.
- 쓰기 권한 = IP 화이트리스트 **AND** 비밀번호 세션(아이디 없음).
- 실행 단계에서는 **질문하지 말고 추천안으로 끝까지**(PR 생성·CI 확인·squash 병합 포함), 끝나면 "내린 판정" 표가 든 보고서를 쓴다. 브레인스토밍·설계 단계의 제품 방향 결정은 묻는다.
- XML 문서 주석 규칙은 CLAUDE.md "적용 범위" 표대로(내용 없는 상용구 remarks 금지).

## 9. 빌드 검증

```bash
dotnet build PortfolioBlog.slnx -c Release   # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release   # 591개 통과 (Docker 필요)
(cd PortfolioBlog.Web && npm ci && npm run lint && npm run typecheck && npm test && npm run build)   # Vitest 188개
(cd PortfolioBlog.Web && npm run e2e:prepare && npm run e2e; docker rm -f pb-e2e-pg)               # E2E 8개
pwsh scripts/harness-audit.ps1               # 8/8 PASS
```
