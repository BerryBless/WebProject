# 운영 절차 (PortfolioBlog)

이 문서의 명령은 전부 서버의 `deploy/` 디렉터리에서 실행한다. 구조와 그 이유는 `plan/tech_blog_0920.md` 3.10절 참조(4단계 보고서는 병합 시 추가).

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
       mysql (포트 미공개)

네트워크: public(caddy만) · edge(internal, caddy↔api) · db(internal, api↔mysql)
볼륨: mysqldata · attachments · dpkeys · caddy_data · caddy_config
```

## 1. 최초 배포

준비물: Linux 서버(고정 공인 IP), Docker Engine + compose 플러그인, 공개·관리 도메인의 DNS 레코드(이 서버를 가리켜야 인증서가 발급된다), 방화벽에서 TCP 80·443만 개방.

```bash
git clone <저장소> && cd <저장소>/deploy
cp .env.example .env && chmod 600 .env
$EDITOR .env                                   # 도메인·허용 CIDR·비밀번호 셋. 설명은 파일 안에 있다
docker compose build api
docker run --rm -it portfolioblog-api hash-password   # 관리자 비밀번호 입력(화면에 보이지 않음) → 출력된 해시를 .env의 ADMIN_PASSWORD_HASH에
docker compose up -d --build
docker compose ps                              # api·mysql이 healthy, caddy가 running
```

앱은 시작할 때 스스로 한다: 설정 검증(틀리면 어떤 키가 문제인지 말하고 종료) → DB 마이그레이션 → 공개 조회 롤(`blog_public`)의 권한을 허용 테이블의 `SELECT`로 다시 맞춤.

배포 전에 같은 구성을 로컬이나 CI에서 검증하려면 `deploy/smoke/run.sh`를 돌린다(운영과 같은 이미지·Caddyfile·compose를 `*.localhost` 도메인으로 띄워 접근 통제·헤더·한도·백업 복원까지 본다).

## 2. 배포 직후 반드시 확인할 것

| 확인 | 방법 | 기대 |
|---|---|---|
| 공개 사이트 | 브라우저로 `https://<공개 도메인>/` | 글 목록, 자물쇠(유효한 인증서) |
| 관리 표면이 밖에서 안 보인다 | **허용 목록 밖의 회선**(휴대폰 테더링 등)에서 `curl -s -o /dev/null -w '%{http_code}\n' https://<관리 도메인>/` 와 `/api/auth/me`, `/login` | 전부 `404` |
| 공개 도메인에 관리 API가 없다 | `curl -s -o /dev/null -w '%{http_code}\n' https://<공개 도메인>/api/posts` | `404` |
| **원본 IP가 보인다** | 허용 회선에서 관리 사이트를 한 번 연 뒤 `docker compose logs caddy \| grep '"remote_ip"' \| tail -n 3` | `remote_ip`가 **내 공인 IP**다. `172.30.0.1`(Docker 게이트웨이)이면 3절을 본다 |
| 관리 도메인의 인증서 발급 | `docker compose logs caddy \| grep -i certificate` | 관리 도메인도 발급 성공 로그가 있다(허용 IP 밖에 있는 CA가 HTTP-01 챌린지를 통과해야 하므로, 관리 표면의 IP 허용 목록과는 별개로 확인이 필요하다) |
| 로그인 | `https://<관리 도메인>/`에서 로그인, 글 하나 저장, 공개 사이트에서 확인 | 저장 즉시 공개 |
| 헬스 | `docker inspect --format '{{.State.Health.Status}}' portfolioblog-api-1` | `healthy` |

## 3. 원본 IP가 게이트웨이 주소로 보일 때

관리 표면의 IP 허용 목록은 Caddy가 보는 `remote_ip`와 앱이 Caddy에게서 받는 `X-Forwarded-For`에 달려 있다. Docker가 연결을 사용자 공간 프록시로 중계하면 모든 클라이언트가 `172.30.0.1`로 보여 **허용 목록이 무의미해지거나(그 주소를 허용했다면 전 세계 허용) 작성자가 잠긴다.**

- IPv4만 그렇다면: Docker 데몬이 iptables를 관리하고 있는지 확인한다(`/etc/docker/daemon.json`에 `"iptables": false`가 있으면 안 된다).
- IPv6 접속만 그렇다면: Docker의 IPv6 NAT가 꺼진 것이다. 가장 간단한 처방은 관리 도메인에 **AAAA 레코드를 두지 않는 것**이다. IPv6가 필요하면 Docker의 `ip6tables`를 켜고 다시 확인한다.
- `ADMIN_ALLOWED_CIDRS`에 `172.30.0.0/24`나 게이트웨이 주소를 넣어 "해결"하지 않는다. 그것은 허용 목록을 끄는 것과 같다.
- 앞단에 CDN·로드밸런서를 두면 이 구성은 그대로 쓸 수 없다(`trusted_proxies` + `client_ip` 재설계가 필요하다 — 스펙 3.10).

## 4. 업데이트(재배포)

```bash
./backup.sh                       # 마이그레이션은 자동으로 되돌려지지 않는다. 먼저 백업한다(MySQL의 DDL은 암묵적으로 커밋되어 롤백되지 않는다 — 중간에 실패하면 스키마가 부분 적용된 채 남는다)
git pull --ff-only
docker compose up -d --build      # 이미지를 다시 빌드하고 바뀐 컨테이너만 교체한다
docker compose ps && docker compose logs --tail 50 api
```

Caddyfile과 관리 SPA는 caddy 이미지에 구워져 있다 — 고치면 위 명령이 이미지를 다시 만든다. SPA 빌드는 배포되는 의존성에 high 이상 취약점이 있으면 실패한다. 먼저 의존성을 올리는 것이 정답이고, 무관한 긴급 수정을 당장 내보내야 할 때만 `docker compose build --build-arg NPM_AUDIT=off caddy`를 쓴다.

되돌리기: `git checkout <이전 커밋> && docker compose up -d --build`. 새 버전이 DB 마이그레이션을 적용했다면 코드만 되돌려서는 안 맞을 수 있다 — 그때는 5절의 복원을 쓴다.

## 5. 백업과 복원

```bash
./backup.sh [백업 루트]            # 기본 ./backups/<UTC 시각>/ 에 blog.sql · attachments.tar · SHA256SUMS
./restore.sh --yes <백업 디렉터리>  # 현재 DB와 첨부를 전부 지우고 덮어쓴다
```

- 백업은 서비스를 멈추지 않는다. DB를 먼저 덤프하고 첨부를 나중에 묶는다 — 첨부는 내용 주소 파일이라 덮어써지지 않으므로, 그 사이에 올라온 파일은 복원 뒤 청소 잡이 지우는 "행 없는 파일"이 될 뿐이다.
- **백업하지 않는 것:** `dpkeys`(세션 암호화 키 — 복원하면 다시 로그인하면 된다), `caddy_data`(인증서 — 다시 발급된다), `.env`(비밀값 — 비밀번호 관리자 등 별도의 안전한 곳에 보관한다. 없으면 복원한 DB에 앱이 접속하지 못한다).
- 백업 디렉터리에는 글 전체와 첨부가 평문으로 들어 있다. 권한은 700/600으로 만들어진다. **서버 밖으로도 복사한다**(서버가 죽으면 서버 안의 백업도 죽는다).
- 매일 새벽 백업 예: `crontab -e` → `17 3 * * * cd /srv/blog/deploy && ./backup.sh /srv/blog-backups >> /var/log/blog-backup.log 2>&1` (오래된 백업 정리는 `find /srv/blog-backups -maxdepth 1 -mtime +30 -exec rm -rf {} +`).
- 새 서버로 옮길 때: 1절대로 `.env`까지 준비하고(같은 DB 비밀번호 셋) `docker compose build` 뒤, `up` 대신 `./restore.sh --yes <백업>`을 실행한다. 빈 볼륨에서 mysql init 스크립트(`mysql-init/10-users.sh`)가 `blog_app`·`blog_public` 사용자와 빈 `blog` DB를 만들고 `restore.sh`가 덤프로 내용을 채운 뒤 전체를 띄운다.
- **복원 리허설:** `deploy/smoke/run.sh`가 매번 한다(글·첨부 생성 → 백업 → 볼륨 삭제 → 복원 → 바이트 단위 확인). 운영 백업 파일로 직접 해 보려면 다른 기계에서 위 "새 서버" 절차를 따른다.
- **복원이 중간에 멈추면:** 서비스는 멈춰 있고 첨부는 비어 있을 수 있다. 같은 백업으로 `./restore.sh`를 다시 실행하면 처음부터 다시 한다(몇 번을 해도 같다). 계속 실패하면 `docker compose up -d`로 서비스만 올려 이전 상태로 돌아간다(스크립트 자신도 실패 시 같은 안내를 출력한다).

## 6. 관리자 비밀번호 변경

```bash
docker run --rm -it portfolioblog-api hash-password     # 새 해시
$EDITOR .env                                            # ADMIN_PASSWORD_HASH 교체
docker compose up -d api
```

해시가 바뀌면 기존 세션은 전부 자동으로 무효가 된다(쿠키에 든 해시 지문이 맞지 않는다).

## 7. 세션 긴급 폐기

쿠키가 유출됐다고 의심되면, 빠른 순서대로:

1. 관리 화면에서 **로그아웃** — 모든 기기의 세션이 함께 끊긴다(세션 epoch 증가).
2. 화면에 들어갈 수 없으면: `docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot blog -e "UPDATE AdminState SET SessionEpoch = SessionEpoch + 1"'`
3. 비밀번호까지 새 나갔다면 6절로 비밀번호를 바꾼다(세션도 함께 폐기된다).

## 8. 허용 IP 변경

`.env`의 `ADMIN_ALLOWED_CIDRS`를 고치고 `docker compose up -d caddy api` — **두 컨테이너 모두** 그 값을 읽는다. 잘못된 CIDR이면 api가 시작을 거부한다(로그에 `Admin:AllowedCidrs`). 집 회선의 IP가 바뀌어 잠겼다면 서버에 SSH로 들어가 같은 절차를 밟는다.

## 9. DB 비밀번호 변경

mysql은 빈 데이터 볼륨에서 처음 뜰 때만 `.env`의 값으로 사용자를 만든다. 나중에 바꾸려면 DB 안에서 먼저 바꾼다. 새 값은 영문·숫자만 쓴다(연결 문자열에 그대로 들어간다).

```bash
docker compose exec -it mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot'   # 대화형 프롬프트가 뜬다
ALTER USER 'blog_app'@'%' IDENTIFIED BY '<새 값>';      -- 프롬프트 안에서 입력한다(셸 명령줄·ps에 남지 않는다)
ALTER USER 'blog_public'@'%' IDENTIFIED BY '<새 값>';   -- 이어서 blog_public도
ALTER USER 'root'@'localhost' IDENTIFIED BY '<새 값>';  -- 마지막으로 root도(소켓 전용 계정 하나뿐이다)
exit
$EDITOR .env                       # 세 값 모두 같은 값으로
docker compose up -d mysql api     # mysql도 다시 만든다: 헬스체크·백업·복원이 컨테이너 환경변수의 비밀번호를 쓴다
```

`mysql -e "ALTER USER …"`처럼 셸 명령줄에 비밀번호를 넣지 않는다(셸 히스토리·`ps`에 남는다). 대화형 클라이언트는 `IDENTIFIED`·`PASSWORD`가 든 줄을 히스토리 파일에 쓰지 않는다.

root는 컨테이너 안 소켓으로만 접속한다(compose의 `MYSQL_ROOT_HOST: localhost`). 이 설정은 빈 볼륨에서 처음 뜰 때만 적용되므로, 그 전에 만든 기존 배포는 위 프롬프트에서 `SELECT Host FROM mysql.user WHERE User='root';`로 `localhost`가 있는지 확인한 뒤 `DROP USER 'root'@'%';`를 실행한다.

## 10. 이미지 버전 올리기

태그는 정확한 버전으로 고정돼 있다. 올릴 곳:

| 이미지 | 파일 |
|---|---|
| `mcr.microsoft.com/dotnet/sdk`, `mcr.microsoft.com/dotnet/aspnet:<버전>-noble-chiseled-extra` | `PortfolioBlog.Api/Dockerfile` |
| `node`, `caddy` | `PortfolioBlog.Web/Dockerfile` |
| `mysql`(서비스와 `tools` 두 곳) | `deploy/docker-compose.yml` |
| `node`(스모크 클라이언트) | `deploy/docker-compose.smoke.yml` |

올린 뒤에는 `deploy/smoke/run.sh`를 통과시킨다(CI의 `deploy-smoke` 잡이 같은 것을 돌린다). MySQL의 **주 버전**(8.4 → 9.x 등, LTS 경계를 넘는 올림)은 데이터 디렉터리 형식이 달라 태그만 바꾸면 뜨지 않을 수 있다 — 릴리스 노트를 확인하고, 안전하지 않으면 백업 → 새 버전으로 빈 볼륨에서 복원한다. 8.4 안의 패치 올림(예: `8.4.11` → `8.4.x`)은 보통 태그만 바꾸면 되지만 매번 스모크로 확인한다.

**MySQL 클라이언트 이미지를 올릴 때 주의:** MySQL 8.x 클라이언트는 비밀번호를 `MYSQL_PWD` 환경변수로 넘기는 방식을 폐기 예정(deprecated)으로 표시한다. 이 저장소는 헬스체크(`deploy/docker-compose.yml`의 mysql `healthcheck`)·`backup.sh`·`restore.sh`가 전부 `MYSQL_PWD`로 비밀번호를 넘긴다(명령줄 인자로 노출하지 않기 위해). 이미지를 올릴 때마다 이 세 곳이 여전히 경고 없이 동작하는지 확인하고, 폐기되면 `--defaults-extra-file`(임시 파일, 0600, 사용 후 즉시 삭제) 같은 대안으로 교체한다.

## 11. 로그

- `docker compose logs -f api|caddy|mysql`. 컨테이너마다 10MB × 5개로 돌려 쓴다(그 이상은 사라진다 — 보존이 필요하면 외부 수집기를 붙인다).
- Caddy 액세스 로그는 `Cookie`·`Set-Cookie`·`Authorization` 값을 `REDACTED`로 남긴다(기본 동작, 실측). 앱은 비밀번호·쿠키·요청 본문을 기록하지 않는다. 로그인 실패는 IP만 남는다.
- 과부하(503) 한 건은 스택 포함 약 50줄이다. 검색어·본문은 남지 않는다.

## 12. 문제 해결

| 증상 | 볼 곳 |
|---|---|
| api가 `unhealthy`/재시작 반복 | `docker compose logs --tail 100 api` — 설정 오류면 첫 예외 메시지에 설정 키가 있다. 헬스체크 자체의 출력은 `docker inspect --format '{{json .State.Health}}' portfolioblog-api-1` |
| "공개 조회 사용자의 권한이 허용 목록과 다릅니다" 로그로 api가 기동을 거부한다(R6, fail-closed) | 누군가 `blog_public`에 수동으로 권한을 넓힌 것이다(앱은 초과 권한을 자동 회수하지 않는다). 9절처럼 대화형 프롬프트를 열어 `SHOW GRANTS FOR 'blog_public'@'%';`로 실제 권한을 확인하고, 로그에 나온 초과분만 `REVOKE ... FROM 'blog_public'@'%';`로 회수한 뒤 `docker compose up -d api`로 재기동한다. 앱이 다시 SELECT 5개 테이블을 GRANT·검증한다 |
| 인증서가 안 나온다 | `docker compose logs caddy \| grep -i acme` — DNS가 이 서버를 가리키는지, 80·443이 열려 있는지 |
| 관리 사이트가 허용 회선에서도 404 | 2·3절의 원본 IP 확인. `.env`의 CIDR에 내 현재 공인 IP가 있는지 |
| 로그인은 되는데 저장이 403 | `.env`의 `ADMIN_ORIGIN`이 브라우저 주소창의 출처와 글자 그대로 같은지(스킴·호스트, 포트는 443이면 생략) |
| 업로드가 413 | 이미지는 10MB까지다. Caddy의 상한(11MiB)은 그보다 크게 잡혀 있다 |
| 컨테이너를 다시 만들 때 `Address already in use` | 고정 IP(172.30.0.2)를 다른 컨테이너가 잡았다 — `docker compose down && docker compose up -d`(볼륨은 지워지지 않는다) |
| `caddy_data`·`caddy_config`를 손으로 들여다보거나 고쳤다 | caddy는 비루트(1654)로 뜬다. 이미지가 `/data/caddy`·`/config/caddy`를 소유자는 root(0)인 채로 1777(sticky, 누구나 읽고 쓰기 가능)로 만들어 둬 평소엔 별도 chown 없이도 그 안에 쓸 수 있다(실측: `docker compose exec -T caddy stat -c '%u %a %n' /data/caddy` → `0 1777 /data/caddy`). 볼륨을 호스트에서 직접 만지다가 그 밑에 다른 권한(소유자만 쓰기 가능한 파일 등)으로 새로 만들면 caddy가 그 파일을 못 건드린다 — caddy 자신은 `cap_drop: ALL`이라 안에서 chown도 못 한다. 고치는 방법은 그 파일·디렉터리를 지우고 caddy가 다시 만들게 하는 것뿐이다(인증서라면 재발급된다) |
| Git Bash(MSYS/Windows)에서 `docker compose exec`나 백업·복원 스크립트의 경로 인자가 이상하게 바뀐다 | MSYS가 `/data` 같은 컨테이너 경로를 Windows 경로로 자동 변환하는 함정이다 — 앞에 `MSYS_NO_PATHCONV=1`을 붙인다(`backup.sh`·`restore.sh`는 이미 그렇게 한다; Linux 서버에서는 아무 영향이 없다) |
