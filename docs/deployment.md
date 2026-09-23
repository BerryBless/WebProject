# 배포 구성

> **상태: 구현 중.** 이 문서가 설명하는 `deploy/` 디렉터리와 Dockerfile은 **브랜치 `feature/blog-deploy`에만 있고 아직 병합되지 않았습니다**(master에는 없습니다). 무엇이 끝났고 무엇이 남았는지는 [재개 가이드 3절](../plan/resume_guide_0921.md)에, 구현 계획 전체는 [`docs/superpowers/plans/2026-09-22-tech-blog-deploy.md`](superpowers/plans/2026-09-22-tech-blog-deploy.md)에 있습니다. **실제 서버·도메인에 올리는 일은 이 단계의 범위 밖입니다** — 구성과 절차를 만들고 로컬에서 검증하는 데까지입니다.

관련 문서: [아키텍처](architecture.md) · [보안 설계](security.md) · [설정 키](configuration.md)

## 목표 토폴로지

```
인터넷 ──▶ caddy (80·443, TLS 종단, 고정 IP)
              ├─ 공개 도메인      ──▶ api:8080   (/api* 는 Caddy가 404)
              └─ 관리 서브도메인  ──▶ 허용 IP만: /api/*·/attachments/* → api:8080
                                       그 밖은 관리 SPA 정적 파일
           api (포트 미공개, 비루트·읽기 전용 루트 FS) ──▶ postgres (포트 미공개, 내부 전용 네트워크)
볼륨: pgdata · attachments · dpkeys · caddy_data · caddy_config
```

1차 배포는 **인터넷 → Caddy → api**로 고정합니다. 앞단에 CDN·로드밸런서를 두면 `remote_ip`가 프록시 주소가 되므로 `trusted_proxies` + `client_ip`로 재설계해야 합니다.

## 구성 요소

| 파일 | 역할 |
|---|---|
| `PortfolioBlog.Api/Dockerfile` | sdk → `aspnet:*-noble-chiseled-extra`(셸·패키지 관리자 없음, 비루트 1654). publish 출력의 모양을 빌드 단계에서 검사한다(`wwwroot`는 `css/site.css` 하나, EF 디자인 타임 어셈블리·`web.config`·개발 설정 없음) |
| `PortfolioBlog.Web/Dockerfile` | node로 SPA를 빌드해 `caddy` 이미지의 `/srv`에 굽는다. 빌드 중 `npm audit --omit=dev --audit-level=high`와 `caddy validate`를 게이트로 돌린다 |
| `deploy/Caddyfile` | 사이트 2개. 관리 사이트는 `route` 안에서 IP 검사가 **모든 처리보다 앞**이고, `/api/*`·`/attachments/*`만 백엔드로 보낸 뒤 그 다음에 SPA 보안 헤더를 붙인다(백엔드 응답의 CSP를 덮지 않기 위해) |
| `deploy/docker-compose.yml` | caddy·api·postgres + 백업 전용 `tools`. 전 서비스 `cap_drop: ALL`·`no-new-privileges`, api·caddy는 `read_only`, DB는 내부 전용 네트워크 |
| `deploy/postgres-init/10-roles.sh` | 빈 볼륨 최초 기동에만 실행. 롤 셋(`blog_app` 소유자 / `blog_public` 조회 전용)과 DB를 만든다 |
| `deploy/.env.example` | 도메인·허용 CIDR·비밀번호 셋·관리자 해시. 실제 값은 `deploy/.env`(git 제외, 권한 600) |
| `deploy/backup.sh` · `restore.sh` | 무중단 백업(DB 덤프 → 첨부 tar)과 복원. *(예정)* |
| `deploy/OPERATIONS.md` | 최초 배포, 배포 직후 확인, 원본 IP 문제, 업데이트, 백업·복원, 비밀번호 변경, 세션 폐기, 로그, 문제 해결. *(예정)* |
| `deploy/smoke/` | 운영과 **같은 이미지·같은 Caddyfile**로 스택을 띄워 찌르는 스모크 테스트 |

이미지 태그는 정확한 버전으로 고정합니다(다이제스트가 아니라 태그 — 기준 OS의 보안 패치 재빌드를 막지 않기 위해).

## 쓰기 권한 없는 DB 롤

2B단계의 잔여 위험("`default_transaction_read_only`는 세션이 스스로 끌 수 있다")을 닫는 조각입니다.

- `postgres`(운영자 전용) · `blog_app`(스키마 소유자, 마이그레이션과 관리 API) · `blog_public`(공개 조회) 셋으로 나눕니다. 앱은 **슈퍼유저로 접속하지 않습니다**.
- 공개 롤의 권한은 init 스크립트의 일괄 부여가 아니라 **앱이 시작할 때마다** 다시 맞춥니다: 자기가 소유한 테이블의 권한을 `PUBLIC`과 그 롤 양쪽에서 회수한 뒤, 허용 테이블 5개(`Posts`·`Series`·`Tags`·`PostTags`·`Attachments`)에만 `SELECT`를 줍니다. 허용 목록이 코드와 함께 버전 관리되고 테스트됩니다.
- `ALTER DEFAULT PRIVILEGES`로 일괄 부여하면 공개 롤이 `AdminState`(세션 폐기 카운터)와 마이그레이션 이력까지 읽습니다(실측). 그래서 쓰지 않습니다.
- 회수 대상을 스키마 전체로 잡으면 **관리 롤이 비 슈퍼유저인 운영 형태에서 남의 소유 테이블 하나 때문에 기동이 막힙니다**(42501, 실측). 자기 소유 테이블로 한정합니다.

## 배포 스모크

Docker만 있으면 어디서든 도는 단일 명령입니다. 운영과 다른 것은 이름·루프백 포트·`*.localhost` 도메인(Caddy 내부 CA)·버려질 비밀값뿐입니다.

```bash
bash deploy/smoke/run.sh            # 빌드 → 기동 → 접근·헤더·한도 → DB 롤 → (예정) 복원 리허설
SMOKE_E2E=1 bash deploy/smoke/run.sh  # 위 + 브라우저 E2E (예정)
```

- 허용 IP(172.30.0.10)와 비허용 IP(172.30.0.11) **두 컨테이너**에서 찌릅니다. 호스트 NAT 동작에 기대면 "비허용 IP" 검사가 환경마다 달라지기 때문입니다.
- 검사하는 것: 공개 사이트의 `/api` 차단(경로 표기 변형 포함), 평문 HTTP 처리, 관리 SPA에 붙는 보안 헤더가 정본과 **글자 그대로** 같은지, SPA 화면 주소가 백엔드로 새지 않는지, `/assets`의 없는 파일이 404인지, 로그인·업로드 크기 경계(10MB 통과 / 그 위 413)·로그아웃, 비허용 IP에서 관리 호스트 전 경로가 **본문 없는 404**인지, Caddy를 건너뛰고 api에 직접 붙어 `X-Forwarded-For`를 위조해도 막히는지, DB가 외부 네트워크에서 안 보이는지.
- Caddy 액세스 로그에 쿠키·비밀번호가 남지 않는 것도 확인합니다(`REDACTED`).

## 운영에서 반드시 확인할 것

| 확인 | 기대 |
|---|---|
| 허용 목록 **밖** 회선에서 `https://admin.<도메인>/`, `/api/auth/me`, `/login` | 전부 404 |
| 공개 도메인의 `/api/posts` | 404 |
| Caddy 액세스 로그의 `remote_ip` | **실제 클라이언트 IP**. Docker 게이트웨이 주소(예: `172.30.0.1`)로 보이면 허용 목록이 무의미하다 — 네트워크 모드를 고친다. 게이트웨이를 허용 목록에 넣어 "해결"하지 않는다 |
| 관리 사이트 응답 헤더 | `admin-headers.ts`의 다섯 헤더 + HSTS |
| 컨테이너 헬스체크 | 앱의 `healthcheck` CLI가 `Host: <공개 호스트>`를 붙여 `/health`를 부른다(호스트 필터 때문에 `localhost`로는 400) |

## 백업

- `pgdata` 덤프와 `attachments`를 **같은 시점에**(DB 먼저 → 첨부 나중). 내용 주소 파일은 덮어써지지 않으므로 그 사이의 어긋남은 "행 없는 파일"(청소 잡이 지움) 쪽으로만 납니다.
- `dpkeys`(세션 키)와 `caddy_data`(인증서)는 백업하지 않습니다 — 복원하면 다시 로그인하고 인증서는 다시 발급됩니다.
- `deploy/.env`는 별도의 안전한 곳에 보관합니다. 없으면 복원한 DB에 앱이 접속하지 못합니다.
- 복원 리허설은 스모크가 매번 수행합니다(글·첨부 생성 → 백업 → 볼륨 삭제 → 복원 → 바이트 단위 확인).

## 실행하며 측정한 사실

| 항목 | 측정 결과 |
|---|---|
| ACME HTTP-01 | 명시 `http://` 사이트 블록·IP 허용 목록과 **공존한다**. 로컬 ACME CA를 허용 목록 밖에 두고 실제 발급까지 확인했다(챌린지 핸들러가 IP 거부보다 앞선다) |
| Caddy 고정 IP | `ip_range`로 동적 할당을 위쪽 절반에 가두지 않으면 먼저 뜬 컨테이너가 `172.30.0.2`를 가져가 Caddy가 기동 실패한다 |
| 업로드 경계 | 10MiB 바로 아래 201 / 10MiB 조금 위는 앱의 413(설명 포함) / 12MiB는 프레임워크 413 / 길이를 알리지 않은(chunked) 12MiB도 413 |
| `request_body` | `11MiB`(=11,534,336)로 맞춰야 한다. `11MB`는 11,000,000이라 프레임워크 상한과 어긋난다 |
| chiseled 이미지 | 셸이 없어 헬스체크에 `curl`을 쓸 수 없다 → 앱 자신의 CLI 경로로 해결 |
| publish 출력 | EF 디자인 타임 어셈블리는 애초에 포함되지 않는다(우려는 build 출력 얘기였다). `web.config`와 apphost는 섞여 나오므로 지운다 |
| 오류 응답의 헤더 | Caddy의 오류 경로는 라우트의 지연 응답 래퍼를 거치지 않아 `Server` 삭제 지시가 무효가 된다 → `handle_errors`로 처리 |
