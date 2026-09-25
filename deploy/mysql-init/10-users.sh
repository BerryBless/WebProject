#!/bin/sh
# 빈 데이터 볼륨에서 처음 뜰 때 한 번만 실행된다(공식 이미지의 /docker-entrypoint-initdb.d 규약).
# 엔트리포인트는 실행 비트가 없는 .sh는 source하고(docker_process_sql 사용 가능), 있는 .sh는 자식 프로세스로 실행한다(함수 없음).
# 저장소의 파일 모드는 100644(source 경로)지만, Windows Docker Desktop의 바인드 마운트는 모든 파일을 0777로 보여 실행 경로를 탄다(실측).
# 그래서 두 경로를 다 받는다: 함수가 있으면 그것을, 없으면 같은 SQL을 root로 로컬 소켓에 직접 보낸다(root 비밀번호는 init 파일 처리 전에 이미 설정돼 있다).
# 본문을 서브셸로 감싼다: source된 파일의 set -eu가 엔트리포인트 셸에 남으면 이후 엔트리포인트 코드의 미설정 변수 참조가 깨진다.
# 앱은 root로 접속하지 않는다:
#   blog_app    — blog DB 한정 권한 + GRANT OPTION. 마이그레이션과 관리 API가 쓴다. 전역 권한(FILE·SUPER·PROCESS·CREATE USER)이 없다.
#                 GRANT OPTION은 앱이 기동할 때 blog_public에 허용 테이블 SELECT를 주기 위한 것이다(스펙 R1: 순증 위험 없음 분석).
#   blog_public — 여기서는 접속만(USAGE). 테이블별 SELECT는 앱(PublicRoleGrants)이 마이그레이션 직후 주고 SHOW GRANTS로 검증한다.
# 두 사용자 모두 TLS 접속만 허용한다(서버도 --require-secure-transport=ON).
# 권한 문자열은 PortfolioBlog.Api.Tests의 MySqlContainerFixture.AppPrivileges와 같아야 한다(DeployInitScriptTests가 대조).
# 비밀번호는 영문·숫자만 허용한다(아래 검사). 그래서 sed 치환과 SQL 문자열 리터럴이 깨지지 않는다. heredoc은 따옴표('SQL')라 셸이 본문을 건드리지 않는다.
# 순서 보장: init 동안 서버는 --skip-networking 임시 서버로 뜨므로 TCP 헬스체크(compose)가 실패한다 — api는 init이 끝난 뒤에만 뜬다.
(
  set -eu
  case "$BLOG_APP_PASSWORD$BLOG_PUBLIC_PASSWORD" in *[!A-Za-z0-9]*) echo "BLOG_APP_PASSWORD·BLOG_PUBLIC_PASSWORD는 영문·숫자만 쓴다" >&2; exit 1;; esac
  sql=$(cat <<'SQL'
CREATE DATABASE blog CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'blog_app'@'%' IDENTIFIED BY '@APP_PW@' REQUIRE SSL;
CREATE USER 'blog_public'@'%' IDENTIFIED BY '@PUBLIC_PW@' REQUIRE SSL;
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON `blog`.* TO 'blog_app'@'%' WITH GRANT OPTION;
SQL
  )
  run_sql() {
    if command -v docker_process_sql > /dev/null 2>&1; then
      # 엔트리포인트 함수는 미설정 변수를 전제로 짜여 있다(_mysql_passfile의 "$1", $MYSQL_DATABASE). set -u 아래서 부르면
      # 비밀번호 파일이 비어 root 인증이 실패한다(실측: ERROR 1045 using password: NO). 이 호출에서만 -u를 끈다.
      set +u
      docker_process_sql --database=mysql
    else
      MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --protocol=socket -uroot mysql
    fi
  }
  printf '%s\n' "$sql" | sed -e "s/@APP_PW@/$BLOG_APP_PASSWORD/" -e "s/@PUBLIC_PW@/$BLOG_PUBLIC_PASSWORD/" | run_sql
) || exit 1
