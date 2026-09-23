#!/bin/sh
# 빈 pgdata에서 처음 뜰 때 한 번만 실행된다(공식 이미지의 /docker-entrypoint-initdb.d 규약).
# 앱은 슈퍼유저(postgres)로 접속하지 않는다:
#   blog_app    — DB·스키마 소유자. 마이그레이션과 관리 API가 쓴다. 슈퍼유저가 아니므로 COPY ... PROGRAM 같은 서버 측 실행이 불가능하다.
#   blog_public — 공개 페이지 조회 전용. 여기서는 접속 권한만 준다. 테이블별 SELECT는 앱이 시작할 때마다 다시 맞춘다
#                 (PortfolioBlog.Api의 PublicRoleGrants — 허용 테이블 목록이 코드와 함께 버전 관리된다).
#
# 두 불변식(Task 1 리뷰 F2·F10이 문 것 — 어기면 권한 경계가 조용히 새거나 기동 순서가 어긋난다):
#   1) public 스키마의 모든 객체는 blog_app이 소유해야 한다. PublicRoleGrants.Apply()는 tableowner = current_user인
#      테이블만 회수한다(PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs:128-130). 남의 소유 테이블이
#      public 스키마에 생기면 앱은 정상 기동하고, 그 테이블에 붙은 blog_public 권한은 회수되지 않고 남는다
#      — 그래서 public 스키마의 객체는 전부 blog_app 소유여야 한다(fail-open이지, 기동 실패가 아니다).
#   2) 실행 순서: 공식 이미지의 initdb.d는 임시 서버에서 돌고 그 동안에도 pg_isready가 통과하므로,
#      compose의 `api: depends_on: postgres: condition: service_healthy`(healthcheck)는 이 순서를 보장하지
#      않는다. 실제로는 이 스크립트가 1초 미만에 끝나고 첫 healthcheck가 10초 뒤라 어긋나지 않을 뿐이다 —
#      init에 오래 걸리는 작업을 추가하면 healthcheck의 start_period나 별도 센티널로 순서를 직접 보장해야 한다.
# 비밀번호는 psql 변수로 넘겨 SQL 문자열 리터럴로 안전하게 인용한다(:'name'). heredoc이 따옴표('SQL')라 셸은 아래 본문을 건드리지 않는다
# — 줄 끝 주석의 ${…}는 어느 환경변수의 값인지 적은 표기일 뿐이다(저장소의 비밀값 스캐너가 자리표시자로 인식한다).
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v app_pw="$BLOG_APP_PASSWORD" -v public_pw="$BLOG_PUBLIC_PASSWORD" <<'SQL'
CREATE ROLE blog_app LOGIN PASSWORD :'app_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE;       -- 값: ${BLOG_APP_PASSWORD}
CREATE ROLE blog_public LOGIN PASSWORD :'public_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE; -- 값: ${BLOG_PUBLIC_PASSWORD}
CREATE DATABASE blog OWNER blog_app;
REVOKE ALL ON DATABASE blog FROM PUBLIC;
GRANT CONNECT ON DATABASE blog TO blog_public;
\connect blog
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO blog_app;
SQL
