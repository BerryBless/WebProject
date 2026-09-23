# 배포 구성

> **상태: 브랜치 `feature/blog-deploy`에 구현 완료, PR 대기.** 이 문서가 설명하는 `deploy/` 디렉터리와 Dockerfile은 master에 아직 병합되지 않았습니다. 실행 이력은 [작업일지 Step 6](worklog.md)에, 구현 계획 전체는 [`docs/superpowers/plans/2026-09-22-tech-blog-deploy.md`](superpowers/plans/2026-09-22-tech-blog-deploy.md)에 있습니다. **실제 서버·도메인에 올리는 일은 이 단계의 범위 밖입니다** — 구성과 절차를 만들고 운영과 같은 이미지로 로컬(또는 CI)에서 검증하는 데까지입니다. 실제 배포 절차는 [`deploy/OPERATIONS.md`](../deploy/OPERATIONS.md)입니다.

관련 문서: [아키텍처](architecture.md) · [보안 설계](security.md) · [설정 키](configuration.md) · [테스트](testing.md)

## 목표 토폴로지

```
인터넷
  │  public (포트 게시·아웃바운드)
  ▼
caddy (80·443, TLS 종단, 비루트 1654, cap_drop ALL + NET_BIND_SERVICE만, 읽기 전용 루트 FS — api와 동일)
  │
  │  edge (internal, 172.30.0.0/24, caddy 고정 IP 172.30.0.2)
  ├──▶ 공개 도메인     → api:8080   (/api* 는 Caddy가 404)
  └──▶ 관리 서브도메인 → 허용 IP만: /api/*·/attachments/* → api:8080, 그 밖은 SPA 정적 파일
        │
        ▼
       api (포트 미공개, 비루트·읽기 전용 루트 FS)
        │  db (internal)
        ▼
       postgres (포트 미공개)

네트워크: public(caddy만) · edge(internal, caddy↔api) · db(internal, api↔postgres)
볼륨: pgdata · attachments · dpkeys · caddy_data · caddy_config
```

1차 배포 토폴로지는 **인터넷 → Caddy → api**로 고정합니다. 앞단에 CDN·로드밸런서를 두면 `remote_ip`가 프록시 주소가 되므로 `trusted_proxies` + `client_ip`로 재설계해야 합니다.

## 구성 요소

| 파일 | 역할 |
|---|---|
| `PortfolioBlog.Api/Dockerfile` | sdk:10.0.401 → `aspnet:10.0.12-noble-chiseled-extra`(셸·패키지 관리자 없음, 비루트 1654, ICU·tzdata 포함). publish 출력의 모양을 빌드 단계에서 검사한다(`wwwroot`는 `css/site.css` 하나, EF 디자인 타임 어셈블리·`.pdb`·`web.config`·개발 설정 없음) |
| `PortfolioBlog.Web/Dockerfile` | node:24.21.0-alpine으로 SPA를 빌드해 `caddy:2.11.4-alpine` 이미지의 `/srv`에 굽는다. 빌드 중 `npm audit --omit=dev --audit-level=high`(`NPM_AUDIT=off`로만 끈다)와 `caddy validate`를 게이트로 돌린다 |
| `deploy/Caddyfile` | 사이트 2개. 관리 사이트는 `route` 안에서 IP 검사가 **모든 처리보다 앞**이고, `/api/*`·`/attachments/*`만 백엔드로 보낸 뒤 그 다음에 SPA 보안 헤더를 붙인다(백엔드 응답의 CSP를 덮지 않기 위해) |
| `deploy/docker-compose.yml` | caddy·api·postgres + 백업/복원 전용 `tools`(profile). 전 서비스 `cap_drop: ALL`·`no-new-privileges`, api·caddy는 `read_only`, 네트워크 셋(`public` 게시·아웃바운드, `edge`·`db`는 `internal`) |
| `deploy/docker-compose.smoke.yml` | 스모크 전용 덮어쓰기(로그인 속도 제한 상향, `public` 서브넷 고정). 운영에는 쓰지 않는다 |
| `deploy/postgres-init/10-roles.sh` | 빈 볼륨 최초 기동에만 실행. 롤 셋(`blog_app` 소유자 / `blog_public` 조회 전용)과 DB를 만든다 |
| `deploy/.env.example` | 도메인·오리진·허용 CIDR·`ACME_EMAIL`·사이트 표기·DB 비밀번호 셋·관리자 해시. 실제 값은 `deploy/.env`(git 제외, 권한 600) |
| `deploy/backup.sh` · `restore.sh` | 무중단 백업(DB 덤프 → 첨부 tar, 자체 검증 + `SHA256SUMS`)과 복원(무결성 확인 → 쓰기 쪽 정지 → `pg_restore` → 첨부 원복 → 재기동) |
| `deploy/OPERATIONS.md` | 최초 배포, 배포 직후 확인, 원본 IP 문제, 업데이트, 백업·복원, 관리자 비밀번호 변경, 세션 긴급 폐기, 허용 IP 변경, DB 비밀번호 변경, 이미지 버전 올리기, 로그, 문제 해결 |
| `deploy/smoke/` | 운영과 **같은 이미지·같은 Caddyfile·같은 compose**로 스택을 띄워 찌르는 스모크(`run.sh` + `smoke.test.mjs`). `SMOKE_E2E=1`이면 `PortfolioBlog.Web/playwright.stack.config.ts`로 브라우저 E2E까지 |

이미지 태그는 정확한 버전으로 고정합니다(다이제스트가 아니라 태그 — 기준 OS의 보안 패치 재빌드를 막지 않기 위해). 올릴 곳과 절차는 `deploy/OPERATIONS.md` §10.

## 쓰기 권한 없는 DB 롤

2B단계의 잔여 위험("`default_transaction_read_only`는 세션이 스스로 끌 수 있다")을 닫는 조각입니다. **구현 완료.**

- `postgres`(운영자 전용) · `blog_app`(스키마 소유자, 마이그레이션과 관리 API) · `blog_public`(공개 조회) 셋으로 나눕니다. 앱은 **슈퍼유저로 접속하지 않습니다**.
- 공개 롤의 권한은 init 스크립트의 일괄 부여가 아니라 **앱이 시작할 때마다** 다시 맞춥니다(`PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs`): 자기가 소유한 테이블의 권한을 `PUBLIC`과 그 롤 양쪽에서 회수한 뒤, 허용 테이블 5개(`Posts`·`Series`·`Tags`·`PostTags`·`Attachments`)에만 `SELECT`를 줍니다. 허용 목록이 코드와 함께 버전 관리되고 테스트됩니다.
- `ALTER DEFAULT PRIVILEGES`로 일괄 부여하면 공개 롤이 `AdminState`(세션 폐기 카운터)와 마이그레이션 이력까지 읽습니다(실측). 그래서 쓰지 않습니다.
- 회수를 스키마 전체(`REVOKE … ON ALL TABLES IN SCHEMA public`)로 잡으면 **관리 롤이 비 슈퍼유저인 운영 형태에서 남의 소유 테이블 하나 때문에 기동이 42501로 막힙니다**(실측). 자기 소유 테이블로 한정합니다.
- `REVOKE … FROM {role}`만으로는 `GRANT … TO PUBLIC`으로 준 권한을 회수하지 못합니다(실측: 공개 롤이 `PUBLIC` 의사 롤을 통해 `AdminState`를 읽고 썼습니다) — `FROM PUBLIC`도 함께 회수합니다.
- 회귀 가드: 통합 테스트가 비 superuser 소유자 롤로 `Apply`를 실행해 42501이 재발하지 않는지 매 실행마다 확인합니다.

## 배포 스모크

Docker만 있으면 어디서든 도는 단일 명령입니다. 운영과 다른 것은 이름(`pb-smoke`)·루프백 포트(8081·8443)·`*.localhost` 도메인(Caddy 내부 CA)·버려질 비밀값뿐입니다.

```bash
bash deploy/smoke/run.sh              # 빌드 → 기동 → 접근·헤더·한도 → DB 롤 → 복원 리허설 → 통과
SMOKE_E2E=1 bash deploy/smoke/run.sh  # 위 + 스택 대상 브라우저 E2E(Chromium·Firefox)
SMOKE_KEEP=1 bash deploy/smoke/run.sh # 종료 후에도 스택을 내리지 않는다(수동 확인·재사용)
SMOKE_HTTP_BIND=127.0.0.1:18081 bash deploy/smoke/run.sh  # 기본 8081이 Hyper-V 배타 예약 포트 범위와 겹치는 기계에서
```

단계와 통과한 개수(브랜치 최신 실행):

1. **이미지 빌드·이미지 검사** — api 이미지가 비루트(1654)이고 셸이 없는지.
2. **기동** — postgres·api·caddy가 healthy(또는 running, caddy는 healthcheck 없음).
3. **스모크: 허용 IP** — 허용 CIDR 안의 컨테이너(172.30.0.10)에서 찌른다. **10개**(공개 사이트 `/api` 차단의 경로 표기 변형, 관리 SPA 보안 헤더가 `admin-headers.ts` + HSTS + COOP과 글자 그대로 같은지, `/assets`의 없는 파일 404, 로그인·업로드 크기 경계·로그아웃 등).
4. **스모크: 비허용 IP** — 허용 목록 밖 컨테이너(172.30.0.11)에서 찌른다. **6개**(관리 호스트의 모든 경로가 본문 없는 404, Caddy를 건너뛰고 api에 직접 붙어 `X-Forwarded-For`를 위조해도 막히는지 등).
5. **스모크: 오류 응답** — api를 잠시 멈춰 502를 내게 한 뒤에도 보안 헤더가 붙는지. **1개**.
6. **복원 리허설** — 글·첨부 생성(seed, **1개**) → `backup.sh`(자체 검증) → `docker compose down -v`(볼륨 전부 삭제) → `restore.sh --yes` → 바이트 단위 확인(verify-restore, **1개**) → 복원된 스택에서 허용 IP 스모크를 다시 **10개** 실행.
7. **DB 롤** — 앱 롤이 슈퍼유저가 아니고, 공개 롤은 5개 허용 테이블만 읽을 수 있고, `AdminState`·마이그레이션 이력·쓰기·`COPY … PROGRAM`은 전부 거부되는지(SQLSTATE·메시지 기반 판정, 컨테이너 네트워크 주소로 접속해 `pg_hba`의 `trust` 줄을 타지 않는다).
8. **(`SMOKE_E2E=1`) 브라우저 E2E** — Caddy가 실제로 내보내는 헤더 아래에서 Chromium·Firefox 각각 로그인·CSRF·오픈 리다이렉트·글쓰기 전 과정. **8개**.

합계(HTTP 스모크만): 허용 10 + 비허용 6 + 오류 응답 1 + seed 1 + verify-restore 1 + 복원 후 허용 재실행 10 = **29회 어서션 실행**(고유 케이스는 19). 브라우저 E2E는 별도 8개.

Caddy 액세스 로그에 쿠키·비밀번호가 남지 않는 것도 확인합니다(`REDACTED`). 실패하면 `deploy/smoke/caddy.log`(접근 로그만, 비밀값 없음)와 Playwright trace가 남습니다.

## CI

`.github/workflows/ci.yml`의 `deploy-smoke` 잡이 `test`·`web` 잡 뒤에 `ubuntu-latest`에서 `SMOKE_E2E=1 bash deploy/smoke/run.sh`를 돌립니다(`timeout-minutes: 30`). 실패하면 Playwright trace와 `deploy/smoke/caddy.log`를 아티팩트로 올립니다. **첫 Linux 실행은 게이트입니다** — 게시 포트(`public` 네트워크)의 게이트웨이 주소가 Docker의 기본 주소 풀에서 골라지므로(운영 compose에 `ipam` 없음), 이 저장소의 두 로컬 세션에서만도 `172.30.0.1`과 `172.19.0.1`로 서로 다르게 관측됐습니다. Linux 러너에서 실패하면 `caddy.log`의 `remote_ip`로 원인을 읽고 스모크 전용 허용 목록(운영 Caddyfile이 아니라 `deploy/smoke/run.sh`)만 고칩니다.

## 운영에서 반드시 확인할 것

배포 직후 확인 절차 전체는 [`deploy/OPERATIONS.md` §2](../deploy/OPERATIONS.md)입니다. 핵심만:

| 확인 | 기대 |
|---|---|
| 허용 목록 **밖** 회선에서 `https://admin.<도메인>/`, `/api/auth/me`, `/login` | 전부 404 |
| 공개 도메인의 `/api/posts` | 404 |
| Caddy 액세스 로그의 `remote_ip` | **실제 클라이언트 IP**. Docker 게이트웨이 주소(예: `172.30.0.1`)로 보이면 허용 목록이 무의미하다 — 게이트웨이를 허용 목록에 넣어 "해결"하지 않는다(`OPERATIONS.md` §3) |
| 관리 도메인의 인증서 발급 | `docker compose logs caddy \| grep -i certificate`에 관리 도메인도 발급 성공 로그가 있다(허용 IP 밖에 있는 CA가 HTTP-01 챌린지를 통과해야 한다) |
| 관리 사이트 응답 헤더 | `admin-headers.ts`의 다섯 헤더 + HSTS + COOP |
| 컨테이너 헬스체크 | api는 `dotnet PortfolioBlog.Api.dll healthcheck`(`Host: <공개 호스트>`를 붙여 `/health` 호출), postgres는 `pg_isready`. **caddy는 헬스체크가 없다** — `docker compose ps`에서 `running`까지만 보인다(아래 잔여 위험) |

## 백업과 복원

- `pgdata` 덤프와 `attachments`를 **같은 시점에**(DB 먼저 → 첨부 나중). 내용 주소 파일은 덮어써지지 않으므로 그 사이의 어긋남은 "행 없는 파일"(청소 잡이 지움) 쪽으로만 납니다.
- `dpkeys`(세션 키)와 `caddy_data`(인증서)는 백업하지 않습니다 — 복원하면 다시 로그인하고 인증서는 다시 발급됩니다.
- `deploy/.env`는 별도의 안전한 곳에 보관합니다. 없으면 복원한 DB에 앱이 접속하지 못합니다.
- 복원 리허설은 스모크가 매번 수행합니다(위 "배포 스모크" 6단계). 절차·크론 예시·새 서버 이전은 `deploy/OPERATIONS.md` §5.

## 실행하며 측정한 사실

| 항목 | 측정 결과 |
|---|---|
| ACME HTTP-01 | 명시 `http://` 사이트 블록·IP 허용 목록과 **공존한다**. 로컬 ACME CA를 허용 목록 밖에 두고 실제 발급까지 확인했다(챌린지 핸들러가 IP 거부보다 앞선다) |
| Caddy 고정 IP | `ip_range: 172.30.0.128/25`로 동적 할당을 위쪽 절반에 가두지 않으면 먼저 뜬 컨테이너가 `172.30.0.2`를 가져가 Caddy가 기동 실패한다 |
| 업로드 경계 | 10MiB 바로 아래 201 / 10MiB 조금 위는 앱의 413(설명 포함) / 12MiB는 프레임워크 413 / 길이를 알리지 않은(chunked) 12MiB도 413 |
| `request_body`(관리) | `11MiB`(=11,534,336)로 맞춰야 한다. `11MB`는 11,000,000이라 프레임워크 상한과 어긋난다 |
| chiseled 이미지 | 셸이 없어 헬스체크에 `curl`을 쓸 수 없다 → 앱 자신의 CLI 경로로 해결 |
| publish 출력 | EF 디자인 타임 어셈블리는 애초에 포함되지 않는다(우려는 build 출력 얘기였다). `web.config`와 apphost는 섞여 나오므로 지운다 |
| 오류 응답의 헤더 | Caddy의 오류 경로는 라우트의 지연 응답(`defer`) 래퍼를 거치지 않아 `Server` 삭제 지시가 무효가 된다 → `handle_errors`로 처리 |
| `public` 네트워크 게이트웨이 | 운영 compose는 `public`에 `ipam`을 지정하지 않는다 — 호스트 브라우저의 `remote_ip`가 Docker 데몬의 기본 주소 풀 상태에 따라 기동마다 달라질 수 있다(실측: 이 저장소 안에서만도 `172.30.0.1`과 `172.19.0.1`로 관측됨). 스모크는 `docker-compose.smoke.yml`에서만 `public` 서브넷을 고정해 이 문제를 피한다 |
| `caddy_data`/`caddy_config` 소유권 | 이미지가 `/data/caddy`·`/config/caddy`를 소유자 root(0)·권한 1777(sticky, 누구나 쓰기 가능)로 만들어 둬 비루트 caddy(1654)가 별도 chown 없이 쓸 수 있다(실측). caddy는 `cap_drop: ALL`이라 컨테이너 안에서 스스로 chown도 못 한다 — 문제가 생기면 파일을 지우고 caddy가 다시 만들게 하는 것뿐이다(`OPERATIONS.md` §12) |

## 수용한 잔여 위험

- **`public` 네트워크에 고정 서브넷이 없다(운영 compose).** 스모크에서만 고쳤다(위 "실행하며 측정한 사실"). 실제 배포 시 `remote_ip`가 예상과 다르면 `ADMIN_ALLOWED_CIDRS`가 무의미해지거나 작성자가 잠길 수 있다 — 1차 배포는 Docker 게이트웨이가 아니라 Caddy 앞에 아무것도 없는 구성이므로 영향이 없지만, Docker 데몬 설정에 따라 게시 포트의 `remote_ip`가 게이트웨이 주소로 보일 가능성은 `OPERATIONS.md` §3이 별도로 다룬다.
- **`blog_public`이 `postgres`·`template1` 데이터베이스에 CONNECT하고 임시 테이블을 만들 수 있다.** init 스크립트는 `blog` DB에서만 `REVOKE ALL ON DATABASE … FROM PUBLIC`을 하므로 다른 두 시스템 DB의 기본 PUBLIC 권한이 남는다. 그 DB에는 읽을 데이터가 없고 쓸 수 있는 것은 임시 테이블뿐이라(디스크 점유 정도) 수용했다 — 공개 롤 비밀번호가 새면 어차피 `blog`의 5테이블이 먼저 읽힌다.
- **caddy 컨테이너에 헬스체크가 없다.** `docker compose ps`가 caddy를 `running`까지만 보여 준다 — 프로세스가 떠 있지만 TLS 종단이 응답하지 않는 상태(예: 설정 리로드 실패)를 compose 헬스 상태만으로는 구분하지 못한다. 배포 직후 확인(위 표)이 이를 보완한다.
- **ACME HTTP-01이 실제 공개 CA(Let's Encrypt 등)를 통과하는지는 이 스택에서 검증되지 않았다.** 스모크·개발은 Caddy 내부 CA(`issuer: local`)를 쓴다 — 허용 IP 밖에 있는 CA가 HTTP-01 챌린지를 통과한다는 것은 로컬 ACME CA로 실제 발급까지 확인했지만(위 "실행하며 측정한 사실"), 진짜 공개 CA·진짜 DNS·진짜 방화벽 조합은 **첫 실제 배포에서 반드시 확인**해야 한다(`OPERATIONS.md` §2의 인증서 발급 확인 행).
- **스모크 클라이언트 컨테이너(uid 1654)가 caddy의 ACME CA 개인키를 읽을 수 있다.** `smoke-allowed`/`smoke-denied`가 `caddy_data`를 읽기 전용으로 마운트해 `NODE_EXTRA_CA_CERTS`로 내부 CA 루트 인증서를 신뢰하는데, caddy도 같은 uid(1654)로 돌기 때문에 그 볼륨 안의 파일 권한(0600, uid 1654 전용)이 스모크 클라이언트도 함께 통과시킨다. 스모크 전용 구성(`docker-compose.smoke.yml`)에만 있는 설계이고 운영 compose에는 이 마운트가 없어 운영에는 영향이 없다.
- 그 밖에 [보안 설계](security.md)가 다루는 잔여 위험(이미지 비검사 표면 5종, 렌더 게이트 공유, 이미지 태그 고정이 다이제스트가 아님 등)은 4단계와 무관하게 이전 단계부터 유지된다.
