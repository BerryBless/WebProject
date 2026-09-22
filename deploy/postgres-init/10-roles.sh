#!/bin/sh
# 빈 pgdata에서 처음 뜰 때 한 번만 실행된다(공식 이미지의 /docker-entrypoint-initdb.d 규약).
# 앱은 슈퍼유저(postgres)로 접속하지 않는다:
#   blog_app    — DB·스키마 소유자. 마이그레이션과 관리 API가 쓴다. 슈퍼유저가 아니므로 COPY ... PROGRAM 같은 서버 측 실행이 불가능하다.
#   blog_public — 공개 페이지 조회 전용. 여기서는 접속 권한만 준다. 테이블별 SELECT는 앱이 시작할 때마다 다시 맞춘다
#                 (PortfolioBlog.Api의 PublicRoleGrants — 허용 테이블 목록이 코드와 함께 버전 관리된다).
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
