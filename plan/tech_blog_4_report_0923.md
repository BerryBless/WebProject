# 기술 블로그 4단계(배포) 실행 보고서 — 2026-09-23

계획: `docs/superpowers/plans/2026-09-22-tech-blog-deploy.md`(Task 6개, 스파이크 S1~S16, 설계 결정 D1~D15). 스펙: `plan/tech_blog_0920.md` 3.1·3.3·3.6·3.7·3.10·7절. 브랜치 `feature/blog-deploy`(분기점 master `3a98985`, 실행 중 master `eb8359c`를 병합). 실행 방식: Subagent-Driven Development(작업마다 새 구현자 → 리뷰 → 수정 라운드 → 범위 재리뷰, 마지막에 브랜치 전체 최종 리뷰). 2026-09-22에 Task 1~3까지 하고 중단, 2026-09-23에 재개해 완주했다.

## 1. 한눈에

| 항목 | 결과 |
|---|---|
| Task 1 공개 조회 전용 DB 롤·시작 검증·헬스체크 CLI | 완료(`4ffb9fa`·`7efab39`·`88310ee`) — 리뷰 F1~F9 수정, 재리뷰 APPROVE, 비 superuser 소유자 회귀 가드 추가 |
| Task 2 Caddyfile·이미지 2개 | 완료(`b5c686f`·`140624c`·`1452204`) — 수정 2라운드(오류 경로 Server 헤더, 평문 HTTP 404, 폴백 사이트, 오류 응답 헤더, 테스트 가드) |
| Task 3 compose·DB 롤 init·스모크 | 완료(`17f4ed3`·`8522c03`·`3f20cc1`) — 3망 분리, DB 롤 검사 실질화, 비루트 caddy, 불변식 주석 정정 |
| Task 4 백업·복원·OPERATIONS.md | 완료(`36e8394`·`dc4a207`) — 복원 리허설이 스모크에 들어감, 비밀번호 로그 유출 절차 교체, 실패 안내 |
| Task 5 스택 E2E·CI `deploy-smoke` | 완료(`76e076a`·`10324b0`·`b72bbf6`·`b8265a0`) — 허용 목록 결정성(public 서브넷 고정), 실패 시 caddy 로그 보존, Firefox 레이스 제거 |
| Task 6 문서 as-built | 완료(`002a01d` + 수정 r1) — 스펙·docs/deployment.md·README·testing·worklog·history·재개 가이드·CLAUDE.md/AGENTS.md |
| 최종 리뷰(실제 스택 공격) | **병합 Yes** — Critical 0 · Important 0 · Minor 3(정적 가드 강도, 계획서 숫자, 공개 `/health` 200). 관리 우회 시도 전부 차단, IP 게이트가 Caddy·api 두 계층에서 독립 강제, 비밀·`Server` 노출 없음. deferred minor 10건 전부 수용. Minor 1·2는 fix wave로 고침 |
| PR · CI · 병합 | _(작성 중)_ |
| 검증 수치 | .NET **625**, Vitest **193**, 기존 E2E **8**, 스택 E2E **8**, 스모크 허용 10·비허용 6·오류 응답 1·seed 1·verify-restore 1·복원 후 허용 10, 하네스 감사 8/8, Release 빌드 경고 0 |

## 2. 만든 것

- **DB 롤 분리(Task 1).** `blog_app`(소유자)·`blog_public`(조회 전용). 앱이 시작할 때마다 `PublicRoleGrants.Apply`가 **자기 소유 테이블**의 권한을 `blog_public`과 `PUBLIC` 양쪽에서 회수하고 허용 5테이블(`Posts`·`Series`·`Tags`·`PostTags`·`Attachments`)에 `SELECT`만 준다. `ConnectionStrings:Public`은 Development가 아니면 필수이고 관리 연결과 같은 사용자면 시작 실패. `healthcheck`·`hash-password` CLI(이미지에는 셸이 없다).
- **이미지 2개(Task 2).** api는 `aspnet:10.0.12-noble-chiseled-extra`(셸·패키지 관리자 없음, uid 1654), 빌드 단계에서 publish 출력 모양을 검사(`.pdb`·EF 디자인 타임·개발 설정 없음, `wwwroot`는 `css/site.css` 하나). SPA는 node 24로 빌드해 `caddy:2.11.4-alpine`의 `/srv`에 굽고 `npm audit --omit=dev --audit-level=high`·`caddy validate`를 게이트로 돈다.
- **Caddyfile(Task 2).** 공개·관리 사이트 2개 + `:80`/`:443` 폴백. 관리 사이트는 `route` 안에서 IP 허용 목록 검사가 모든 처리보다 앞(허용 밖은 전 경로 404, 평문 HTTP도 404), `/api/*`·`/attachments/*`만 백엔드로, 그 뒤에 SPA 보안 헤더 7개(`admin-headers.ts`의 5개 + HSTS + COOP). 공개 사이트는 GET/HEAD 외 405, `/api*` 404, 헤더 4개. `handle_errors`·폴백에도 같은 헤더, 모든 경로에서 `Server`·`Via` 제거. `@dot`(점 파일 404)와 `/.well-known/*` 404(준비 작업). 정적 가드 `caddyfile.test.ts`는 헤더 블록의 **위치**(백엔드 프록시 뒤·`root` 앞)까지 문다.
- **compose(Task 3).** caddy(비루트 1654, `cap_drop ALL` + `NET_BIND_SERVICE`, `read_only`) · api(1654, `read_only`, tmpfs `/tmp` 64m) · postgres · `tools`(백업 전용, 네트워크 없음). 네트워크 3개: `public`(caddy만, 포트 게시·아웃바운드) · `edge`(internal, 172.30.0.0/24, caddy 고정 172.30.0.2 = `Proxy__TrustedIp`) · `db`(internal). **api는 인터넷으로 나갈 수 없다**(default route 자체가 없음).
- **스모크(Task 3~5).** `bash deploy/smoke/run.sh`: 이미지 빌드 → 이미지 검사 → 해시 생성 → 기동 → 허용 IP 10 → 비허용 IP 6 → 오류 응답(api 중단 중 502 헤더) → 복원 리허설(seed → backup → 볼륨 삭제 → restore → verify-restore 바이트 일치 → 허용 10 재실행) → DB 롤 검사(`-h postgres` scram, SQLSTATE 판정) → (선택 `SMOKE_E2E=1`) Playwright 8 → `=== 통과`. `SMOKE_KEEP=1`로 스택을 남기고, `SMOKE_HTTP_BIND`로 호스트 HTTP 포트를 바꾼다(이 PC는 Hyper-V가 8073–8272를 예약). 스모크 오버레이가 `public` 서브넷을 172.30.1.0/24로 고정해 호스트 브라우저의 `remote_ip`(=게이트웨이 172.30.1.1)를 결정적으로 만든다. 실패 시 caddy 접근 로그를 `smoke/caddy.log`로 남긴다.
- **백업·복원(Task 4).** `backup.sh [루트]`: DB 덤프 → 첨부 tar → `SHA256SUMS`, 그 자리에서 읽기 검증, 실패 시 부분 디렉터리 삭제, 같은 초 중복 실행은 실패. `restore.sh --yes <디렉터리>`: 체크섬 검증 → 쓰기 서비스 정지 → 볼륨 초기화 → 복원 → 기동. 중간 실패 시 다음 행동을 안내(`trap ERR`, 실제 상태에 따라 문구 분기). 상대 경로는 호출자 cwd 기준.
- **OPERATIONS.md(Task 4).** 최초 배포·배포 직후 확인(ACME 발급 로그 포함)·원본 IP·업데이트·백업/복원·관리자 비밀번호·세션 긴급 폐기·허용 IP·DB 비밀번호(대화형 `\password` — 인라인 `ALTER ROLE … PASSWORD`는 실패 시 로그에 평문이 남아 금지)·이미지 버전·로그·문제 해결(비루트 caddy 볼륨 소유권, MSYS 경로 변환).
- **스택 E2E·CI(Task 5).** `playwright.stack.config.ts`가 같은 `admin.spec.ts`를 스모크 스택(`https://admin.blog.localhost:8443`)에 대해 돌린다 — Caddy가 주는 실제 헤더 아래에서 CodeMirror·미리보기 iframe·첨부가 CSP 위반 0건으로 돈다. CI 잡 `deploy-smoke`(web-e2e 뒤, timeout 30분, 실패 시 Playwright 결과 + caddy.log 아티팩트).
- **문서(Task 6).** `docs/deployment.md` as-built, 스펙 3.6·3.7·3.10·5·6·8절, README·testing·worklog·history·재개 가이드 상태·숫자, CLAUDE.md/AGENTS.md `deploy/` 항목·CI 잡 목록·스모크 통과 규칙.

## 3. 검증 — 무엇에 근거한 결론인가

- 컨트롤러는 매 커밋마다 `verify.sh`(트레일러·접두사·NUL·줄 끝·비밀값 스캔·빌드·테스트)를 돌렸다. 리뷰어는 전부 **저장소 밖 복사본에서 실제 스택을 띄워** 측정했고, 새 테스트·가드마다 사보타주로 실패를 확인했다.
- 리뷰가 실측으로 찾은 것(구현자 보고만으로는 지나갔을 것): PUBLIC 경유 권한 우회(T1 F1), 비 superuser 소유자 형태에서 기동 실패(T1 F2), 오류 응답·평문 HTTP·낯선 Host의 `Server` 헤더(T2), 헤더 블록을 route 끝으로 옮겨도 통과하는 정적 가드(T2 N-A), api의 인터넷 아웃바운드(T3-1), trust 인증을 타는 DB 롤 검사와 "0 아닌 종료=통과"(T3-2), 불변식 주석이 실측과 반대(X-1), 문서 절차의 비밀번호 로그 유출(T4 I1), 복원 중단 시 안내 부재(T4 I2), 자동 할당 게이트웨이에 기댄 허용 목록(T5 I1), Firefox 이미지 로드 레이스(T5 r1 재리뷰).
- 최종 리뷰: _(작성 중)_

## 4. 계획 결함과 교훈

계획의 코드는 스파이크 복사본에서 실제로 돌려 본 것이었지만, 리뷰는 그 위에서 더 찾았다(계획서 끝 "구현 중 발견해 고친 계획 결함" 절과 각 `task-N-review.md`). 이번 단계에서 새로 배운 것:

1. **"검증됐다"는 값도 환경 산물일 수 있다.** 계획이 S6에서 잰 호스트 `remote_ip` 172.30.0.1은 3망 분리 뒤 `public` 게이트웨이로 바뀌었고, 그 값은 Docker 주소 풀 상태에 따라 달라졌다(172.19 → 172.20). 결정적이어야 하는 값은 **고정하고 나서** 허용 목록에 넣는다.
2. **보안 통제의 주석도 검증 대상이다.** DB 롤 init 스크립트의 "불변식" 두 개가 실측과 정반대였고(fail-open을 fail-closed로 서술), 그것을 지시한 판정 문장(컨트롤러)도 부정확했다. 리뷰어가 살아 있는 스택에서 재현해 바로잡았다.
3. **운영 문서의 명령은 실행해 보고 로그를 본다.** 계획서가 준 DB 비밀번호 변경 명령은 오타 한 번이면 postgres 로그에 새 비밀번호를 평문으로 남겼다.
4. **CI 게이트가 될 테스트는 여러 번 돌린다.** Firefox 레이스는 한 번(8개 중 1개)만 나타났고 구현자는 "기존 간헐 실패와 같은 부류"로 오분류했다. 재리뷰어가 코드로 원인을 추적해 다른 계층의 새 결함임을 밝혔다.
5. **컨트롤러 지시가 결함을 만들기도 한다.** "상태 한 줄만 갱신"이라는 좁힘 때문에 history.md의 헤딩과 본문이 모순됐다(Task 6 F1). 문서는 문장 단위가 아니라 절 단위로 정합성을 본다.
6. **환경 예약 포트.** 이 PC의 Hyper-V 배타 예약(8073–8272, 세션에 따라 4105–4204)이 스모크 8081과 기존 E2E 4173을 막았다. 스모크에는 `SMOKE_HTTP_BIND` 탈출구를 뒀고, 기존 E2E는 CI가 판정한다.

## 5. 내가 내린 판정 (질문 없이 추천안으로 결정한 것)

| # | 판정 | 이유 | 틀렸을 때의 대가 |
|---|---|---|---|
| R1 | 계획은 `sha256sum`·bash 전제, macOS는 대상 아님 | Linux 서버·Git Bash 모두 있음 | mac 사용자는 `shasum`으로 바꿔야 함 |
| R2 | T1 F1: `REVOKE … FROM PUBLIC`도 회수 | PUBLIC 경유 우회 실측 | 없음 |
| R3 | T1 F2: 회수 대상을 자기 소유 테이블로 한정 | 비 superuser 소유자 운영 형태에서 남의 테이블 하나로 기동 차단 제거 | 남의 소유 테이블에 붙은 공개 롤 권한은 회수 못 함(fail-open, 문서화) |
| R4 | T1 F6 수용: 다른 DB(`postgres`·`template1`)로의 CONNECT·TEMP 잔존 | 읽을 것이 없음 | 임시 테이블로 디스크 점유 가능(잔여 위험) |
| R5 | T2 F2: 평문 HTTP도 허용 목록 밖 404(명시 `http://` 블록) | "허용 밖 전 경로 404" 보증을 평문에서도 | ACME HTTP-01 공존 — 리뷰어가 로컬 CA 실발급으로 증명 |
| R6 | T2 F4: 공개 사이트 GET/HEAD 외 405 | 더 엄격한 쪽 | 스모크의 413 기대를 405로 바꿈 |
| R7 | T1 재리뷰 N1·N2·N4를 테스트 전용 r2로 닫음 | 핵심 경계(F2)를 하네스가 실제로 물어야 함 | 실행 시간 +수 초 |
| R8 | T3-1: 3망(public/edge/db) | api는 아웃바운드가 필요 없음 | compose가 길어짐, 호스트 게이트웨이 주소가 바뀜(→ R17) |
| R9 | T3-2: DB 롤 검사 `-h postgres` + SQLSTATE 판정 | 기존 검사는 사실상 무의미 | 없음 |
| R10 | T3-6: caddy 비루트 시도(안 되면 수용) | 더 엄격한 쪽 | 됐다 — 스모크 클라이언트도 1654로 맞춰야 했고 CA 개인키까지 읽힘(잔여 위험) |
| R11 | Task 2 r2 + Task 3 r1을 한 구현자·커밋 2개로 배치 | 같은 스택을 두 번 띄우는 비용 절감 | 한쪽 실패가 다른 쪽을 지연 |
| R12 | 커밋 값 8081 유지, 로컬은 `SMOKE_HTTP_BIND`로 치환 | CI(Linux)·계획과 일치, 비관리자라 winnat 재시작 불가 | 로컬 스모크에 환경변수 한 개 필요 |
| R13 | X-1 주석 재작성 + Minor X-2·X-3·X-5를 같은 라운드에 | 한 줄짜리, 최종 리뷰 잡음 감소 | 루프 규칙 예외 |
| R14 | T4 I1: `\password` 대화형으로 교체, 인라인 금지 | 실패 시 로그 평문 실측 — 전역 제약 위반 | 자동화 불가(1인 운영이라 무관) |
| R15 | T4 I2: `trap ERR` 안내(`touched` 분기) + §5 한 줄 | 재실행으로 완전 회복됨을 확인 — 로직이 아니라 안내 결함 | 없음 |
| R16 | T4 Minor 3~8 전부 같은 라운드 | 한두 줄, M3는 평문 덤프 오프사이트 유출 | 재리뷰 범위 확대 |
| R17 | T5 I1: **스모크 오버레이**에서만 `public` 172.30.1.0/24 고정, 허용 목록 2개 | 리뷰어가 probe 네트워크 점유 상태에서 exit 0 검증; 운영은 호스트 게이트웨이 허용 불필요 | 운영 `public` 서브넷은 여전히 자동(LAN 겹침 위험, 잔여) |
| R18 | T5 M1~M4 같은 라운드(caddy.log 아티팩트, timeout 30, 죽은 값 제거, origin 단일화); HTTPS 포트 탈출구는 범위 밖 | CI 첫 실행 진단에 필수 | 8443 예약 기계에서는 스모크 불가 |
| R19 | Firefox 레이스를 Task 5 r2(테스트만)로 지금 고침 | CI 게이트의 무작위 빨간불 방지, spec 한 곳 | 라운드 하나 추가 |
| R20 | Task 6 Step 2(README 배포 절) 대신 `docs/deployment.md` as-built | master가 문서를 docs/로 나눔 | 없음 |
| R21 | Task 6 F1~F3 수정과 최종 리뷰를 병행 | 문서 수정은 스택 공격과 무관, fix wave 재리뷰가 덮음 | 최종 리뷰어가 본 history.md가 한 커밋 옛것 |
| R22 | master 병합 시 README 충돌은 master 랜딩 버전 채택 | 브랜치의 설정 키 2줄은 docs/configuration.md에 이미 있음 | 없음 |

_(최종 리뷰 이후 판정은 아래에 추가한다.)_

## 6. 수용한 잔여 위험

- `blog_public`이 `postgres`·`template1`에 CONNECT·임시 테이블 생성 가능(읽을 데이터 없음).
- 스모크 클라이언트(uid 1654)가 스모크 전용 로컬 CA의 개인키까지 읽을 수 있다(매 실행 버려지는 CA, 운영 무관). 대안: `root.crt`만 `docker cp`로 넘기기.
- 운영 `public` 네트워크에 ipam이 없어 Docker가 서브넷을 고른다(호스트 LAN과 겹치면 기동 실패 — OPERATIONS.md 문제 해결 절 참조).
- caddy 컨테이너에 healthcheck가 없다(`restart: unless-stopped`가 프로세스 종료만 본다).
- `SMOKE_KEEP=1`에서 오류 응답 단계가 실패하면 api가 정지 상태로 남는다(디버깅 혼동 요소).
- DB 롤 검사는 복원 **뒤**에만 돈다(복원 경로가 init·덤프 양쪽 ACL을 반영).
- 공개 `request_body` 상한은 GET에 무동작(Kestrel 상한이 받는다).
- 서브넷·`Proxy__TrustedIp`가 4파일에 리터럴로 중복. Windows에서 `.env.smoke`가 644. `tools`만 로그 회전 없음.
- ACME HTTP-01은 로컬 CA로만 실증 — 공인 CA는 첫 배포 확인 항목(OPERATIONS.md §2).
- 첨부가 많아 그리드가 뷰포트 밖이면 스택 E2E의 이미지 로드 폴이 타임아웃할 수 있다(현재 시드 1개). 8443 예약 기계에서는 스모크 불가.

## 7. 알려진 문제

- 이 PC에서는 Hyper-V 포트 예약 때문에 기존 E2E(4173)를 코드 변경 없이 돌릴 수 없었다(재부팅으로 범위가 바뀐다). CI `web-e2e`가 판정한다.
- 첫 Linux CI 실행이 게이트다: Docker 빌드 시간, 러너의 `*.localhost` 해석, `remote_ip`(고정 서브넷으로 결정적이어야 함).
- 기존 .NET 간헐 실패(Testcontainers 연결 타임아웃)는 그대로다.

## 8. 다음 단계

- PR → CI → squash 병합 → 이 보고서의 "작성 중" 칸 채우기, CLAUDE.md/AGENTS.md 플랜 표 행, 스펙 8절 완료 표기.
- 그다음 백로그: 노션식 에디터·보기(브레인스토밍부터).

## 9. 빌드 검증

```bash
dotnet build PortfolioBlog.slnx -c Release && dotnet test PortfolioBlog.slnx -c Release --no-build   # 625
(cd PortfolioBlog.Web && npm run lint && npm run typecheck && npx vitest run && npm run build)        # 193
(cd PortfolioBlog.Web && npm run e2e:prepare && npm run e2e; docker rm -f pb-e2e-pg)                  # 8
SMOKE_E2E=1 bash deploy/smoke/run.sh            # === 통과 (이 PC: SMOKE_HTTP_BIND=127.0.0.1:18081)
pwsh scripts/harness-audit.ps1                  # 8/8
```

## 10. 변경 파일 요약

`git diff --stat eb8359c..HEAD`: 36파일(+ Task 6 수정) — `PortfolioBlog.Api/`(PublicRoleGrants·시작 검증·헬스체크 CLI·Dockerfile), `PortfolioBlog.Api.Tests/`, `PortfolioBlog.Web/`(Dockerfile·caddyfile.test.ts·admin.spec.ts·playwright.stack.config.ts·tsconfig.node.json), `deploy/`(Caddyfile·docker-compose.yml·docker-compose.smoke.yml·.env.example·postgres-init·backup.sh·restore.sh·OPERATIONS.md·smoke/), `.github/workflows/ci.yml`, `.gitattributes`·`.dockerignore`·`.gitignore`, 문서(스펙·docs/·README·CLAUDE.md·AGENTS.md·재개 가이드).
