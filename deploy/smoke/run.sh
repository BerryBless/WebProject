#!/usr/bin/env bash
# 배포 스택 스모크: 이미지 빌드 → 기동 → 접근·헤더·한도 검사(허용/비허용 IP) → DB 사용자 검사 → 백업·삭제·복원 리허설 → (선택) 브라우저 E2E.
# 운영과 같은 compose·Caddyfile·이미지를 쓴다. 다른 것은 이름(pb-smoke), 루프백 포트, *.localhost 도메인(Caddy 내부 CA), 버려질 비밀값뿐이다.
# 사용법: deploy/smoke/run.sh          환경변수: SMOKE_E2E=1(Playwright까지), SMOKE_KEEP=1(끝나도 스택을 남긴다)
set -euo pipefail
export MSYS_NO_PATHCONV=1
here="$(cd "$(dirname "$0")" && pwd)"
deploy="$(dirname "$here")"
cd "$deploy"

export COMPOSE_PROJECT_NAME=pb-smoke
export COMPOSE_PATH_SEPARATOR=:
export COMPOSE_FILE=docker-compose.yml:docker-compose.smoke.yml
export COMPOSE_ENV_FILES=smoke/.env.smoke

random() { head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 20; }
admin_password="smoke-$(random)"
# write_env(관리 사이트 ORIGIN)와 아래 브라우저 E2E 블록이 같은 값을 두 번 따로 적지 않도록 한 곳에서 만든다(리뷰 M4).
admin_origin="https://admin.blog.localhost:8443"

write_env() { # $1 = 관리자 비밀번호 해시
  umask 077
  cat > smoke/.env.smoke <<EOF
DOMAIN=blog.localhost
ADMIN_DOMAIN=admin.blog.localhost
PUBLIC_ORIGIN=https://blog.localhost:8443
ADMIN_ORIGIN=${admin_origin}
# 172.30.0.10: smoke-allowed 컨테이너 고정 IP(edge 네트워크). 172.30.1.1: 호스트 브라우저(Playwright)가 게시 포트
# (8443, public 네트워크)로 붙을 때의 remote_ip — docker-compose.smoke.yml이 public의 ipam을 고정해서 이 값이
# 결정적이다(리뷰 I1: 운영처럼 ipam을 안 정하면 Docker 데몬의 기본 주소 풀 상태에 따라 매번 바뀐다). edge의 게이트웨이
# 172.30.0.1은 호스트 브라우저가 아니라 컨테이너 간 통신에만 쓰여 아무도 이 목록을 거치지 않는다(리뷰 M3) — 뺐다.
ADMIN_ALLOWED_CIDRS=172.30.0.10/32 172.30.1.1/32
ACME_EMAIL=smoke@example.test
# 이 PC의 호스트 포트 8081은 Hyper-V 배타 예약 범위(8073-8272) 안이라 바인드가 거부될 수 있다 —
# 그럴 때만 SMOKE_HTTP_BIND로 덮어쓴다(기본값·CI는 8081 그대로).
HTTP_BIND=${SMOKE_HTTP_BIND:-127.0.0.1:8081}
HTTPS_BIND=127.0.0.1:8443
MYSQL_ROOT_PASSWORD=${mysql_root_password}
BLOG_APP_PASSWORD=${app_password}
BLOG_PUBLIC_PASSWORD=${public_password}
ADMIN_PASSWORD_HASH=${1}
SMOKE_ADMIN_PASSWORD=${admin_password}
EOF
}
# random은 영문·숫자만 낸다 — init 스크립트가 앱·공개 비밀번호에 그 밖의 문자를 거부한다.
mysql_root_password="$(random)"; app_password="$(random)"; public_password="$(random)"

cleanup() {
  status=$?
  # 실패 진단용(리뷰 M1): teardown 전에 Caddy 액세스 로그를 남긴다. 액세스 로그는 remote_ip·상태 코드·경로만 찍고
  # 요청 헤더·쿠키·본문 값은 찍지 않으므로 비밀값이 섞이지 않는다 — CI는 실패했을 때만 이 파일을 아티팩트로 올린다.
  docker compose logs caddy > smoke/caddy.log 2>&1 || true
  if [ "${SMOKE_KEEP:-0}" != "1" ]; then
    docker compose down -v --remove-orphans > /dev/null 2>&1 || true
    rm -rf smoke/.env.smoke smoke/backups
    [ "$status" -eq 0 ] && rm -f smoke/caddy.log # 성공하면 진단용 로그도 남기지 않는다
  fi
  exit $status
}
trap cleanup EXIT

step() { printf '\n=== %s\n' "$1"; }

step "이미지 빌드"
write_env "pending-dummy" # 빌드에는 값이 필요 없지만 compose가 변수 존재를 요구한다
docker compose down -v --remove-orphans > /dev/null 2>&1 || true
docker compose build

step "이미지 검사(api: 비루트·셸 없음)"
test "$(docker image inspect pb-smoke-api --format '{{.Config.User}}')" = "1654"
if docker run --rm --entrypoint /bin/sh pb-smoke-api -c true > /dev/null 2>&1; then echo "api 이미지에 셸이 있다" >&2; exit 1; fi

step "관리자 비밀번호 해시 생성(이미지의 hash-password 명령)"
hash="$(printf '%s\n' "$admin_password" | docker run --rm -i pb-smoke-api hash-password | tail -n 1)"
test -n "$hash"
write_env "$hash"

step "기동"
docker compose up -d --wait
test "$(docker inspect pb-smoke-api-1 --format '{{.HostConfig.ReadonlyRootfs}}')" = "true"

step "스모크: 허용 IP"
SMOKE_ROLE=allowed docker compose run --rm -T smoke-allowed
step "스모크: 비허용 IP"
docker compose run --rm -T smoke-denied

step "스모크: 오류 응답(api 중단 중에도 502에 보안 헤더가 붙는다, N-C)"
docker compose stop api > /dev/null
SMOKE_ROLE=errors docker compose run --rm -T --no-deps smoke-allowed
docker compose start api > /dev/null
docker compose up -d --wait

step "복원 리허설: 글·첨부 생성 → 백업 → 볼륨 삭제 → 복원 → 확인"
SMOKE_ROLE=seed docker compose run --rm -T smoke-allowed
backup_dir="$(./backup.sh smoke/backups | tail -n 1)"
docker compose down -v --remove-orphans
./restore.sh --yes "$backup_dir"
SMOKE_ROLE=verify-restore docker compose run --rm -T smoke-allowed
SMOKE_ROLE=allowed docker compose run --rm -T smoke-allowed # 복원된 스택에서도 전 과정이 돈다(새 dpkeys·권한 재부여 포함)

step "DB 사용자: 앱은 전역 권한이 없고, 공개 사용자는 허용 테이블 읽기만 한다"
# 복원 리허설 뒤에 돈다: 복원은 DB를 지우고 다시 만든다. 권한 경계가 조용히 열릴 수 있는 지점은 정확히 복원 직후다.
# 앱·공개 사용자는 -h mysql(네트워크 주소)로 붙어 TLS·비밀번호 인증을 실제로 거친다(소켓 접속은 인증 경로가 다르다).
# root는 소켓 전용이다(MYSQL_ROOT_HOST=localhost). root 쿼리는 backup.sh처럼 컨테이너 안 환경변수의 비밀번호로 소켓에 붙는다. SQL은 stdin으로 넘긴다.
root_sql() { docker compose exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" exec mysql -uroot -N -B'; }
mysql_as() { docker compose exec -T -e MYSQL_PWD="$2" mysql mysql -h mysql -u "$1" --ssl-mode=REQUIRED -D blog -N -B -e "$3"; }
# 부정 검사는 종료 코드가 아니라 메시지로 판정한다: "0이 아닌 종료 코드"에는 연결 실패·SQL 오타도 섞여 거짓 통과를 만든다.
deny() { # $1 사용자 $2 비밀번호 $3 SQL
  out="$(mysql_as "$1" "$2" "$3" 2>&1)" && { echo "허용돼서는 안 되는 문장이 성공했다: $3" >&2; exit 1; }
  case "$out" in *"denied"*) ;; *) echo "거부됐지만 이유가 권한이 아니다: $out" >&2; exit 1;; esac
}
test "$(root_sql <<< "select count(*) from mysql.user where User in ('blog_app','blog_public') and Super_priv='N' and File_priv='N' and Process_priv='N' and Create_user_priv='N' and Grant_priv='N' and ssl_type='ANY'")" = "2"
test "$(root_sql <<< "select concat(@@local_infile, ':', ifnull(@@secure_file_priv,'NULL'), ':', @@require_secure_transport, ':', @@global.transaction_isolation)")" = "0:NULL:1:READ-COMMITTED"
# root 계정은 localhost 하나뿐이고, 네트워크 root 로그인은 거부된다(비밀번호가 맞아도).
test "$(root_sql <<< "select group_concat(Host) from mysql.user where User='root'")" = "localhost"
out="$(mysql_as root "$mysql_root_password" 'select 1' 2>&1)" && { echo "root가 네트워크로 로그인했다" >&2; exit 1; }
case "$out" in *"denied"*|*"not allowed to connect"*) ;; *) echo "네트워크 root 거부 이유가 예상과 다르다: $out" >&2; exit 1;; esac
mysql_as blog_public "$public_password" 'select count(*) from `Posts`' > /dev/null
for sql in 'delete from `Posts`' 'set session transaction_read_only = off; delete from `Posts`' 'create table smoke_t(i int)' 'select * from `AdminState`' 'select * from `__EFMigrationsHistory`' 'select * from mysql.user' 'use mysql'; do
  deny blog_public "$public_password" "$sql"
done
# FILE 권한 검사에서 먼저 막힌다: ERROR 1227 ... Access denied; you need (at least one of) the FILE privilege(s)(운영 인자 --secure-file-priv=NULL 아래 실측).
deny blog_app "$app_password" "select 1 into outfile '/tmp/smoke'"
# TLS 없는 접속은 두 겹으로 막힌다. 앱 사용자는 계정의 TLS 요구에서 먼저 걸려 ERROR 1045(Access denied)가 된다(실측) — 같은 비밀번호로
# TLS 접속이 위에서 성공했으므로 이 거부는 비밀번호가 아니라 TLS 때문이다. 서버 전역 설정(require_secure_transport)은 계정에 TLS 요구가 없는
# 일회용 사용자로 따로 확인한다: ERROR 3159 "Connections using insecure transport are prohibited"(실측). 확인 뒤 지운다.
no_tls() { docker compose exec -T -e MYSQL_PWD="$2" mysql mysql -h mysql -u "$1" --ssl-mode=DISABLED -e 'select 1' 2>&1; }
out="$(no_tls blog_app "$app_password")" && { echo "앱 사용자의 비TLS 접속이 허용됐다" >&2; exit 1; }
case "$out" in *"denied"*) ;; *) echo "앱 사용자 비TLS 거부 이유가 예상과 다르다: $out" >&2; exit 1;; esac
notls_password="$(random)"
root_sql <<< "CREATE USER 'smoke_notls'@'%' IDENTIFIED BY '${notls_password}'"
# caching_sha2_password는 인증 캐시가 비어 있으면 비TLS에서 클라이언트가 먼저 멈춘다(ERROR 2061 "Authentication requires secure
# connection", 실측 — 서버 설정까지 가지 않는다). TLS로 한 번 로그인해 캐시를 채운 뒤에야 서버의 3159 거부가 드러난다.
docker compose exec -T -e MYSQL_PWD="$notls_password" mysql mysql -h mysql -u smoke_notls --ssl-mode=REQUIRED -e 'select 1' > /dev/null
out="$(no_tls smoke_notls "$notls_password")" && { echo "비TLS 접속이 허용됐다(require_secure_transport가 꺼져 있다)" >&2; exit 1; }
case "$out" in *"insecure transport"*) ;; *) echo "비TLS 거부 이유가 서버 설정이 아니다: $out" >&2; exit 1;; esac
root_sql <<< "DROP USER 'smoke_notls'@'%'"

if [ "${SMOKE_E2E:-0}" = "1" ]; then
  step "브라우저 E2E(Chromium·Firefox): Caddy가 주는 실제 헤더 아래에서 SPA 전 과정"
  (cd "$deploy/../PortfolioBlog.Web" && E2E_SPA_ORIGIN="$admin_origin" E2E_ADMIN_PASSWORD="$admin_password" npx playwright test -c playwright.stack.config.ts)
fi

step "통과"
