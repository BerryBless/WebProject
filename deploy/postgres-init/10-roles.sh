#!/bin/sh
# 빈 pgdata에서 처음 뜰 때 한 번만 실행된다(공식 이미지의 /docker-entrypoint-initdb.d 규약).
# 앱은 슈퍼유저(postgres)로 접속하지 않는다:
#   blog_app    — DB·스키마 소유자. 마이그레이션과 관리 API가 쓴다. 슈퍼유저가 아니므로 COPY ... PROGRAM 같은 서버 측 실행이 불가능하다.
#   blog_public — 공개 페이지 조회 전용. 여기서는 접속 권한만 준다. 테이블별 SELECT는 앱이 시작할 때마다 다시 맞춘다
#                 (PortfolioBlog.Api의 PublicRoleGrants — 허용 테이블 목록이 코드와 함께 버전 관리된다).
#
# 두 불변식(Task 1 리뷰 F2·F10이 문 것 — 어기면 앱이 기동 실패한다):
#   1) public 스키마의 모든 객체는 blog_app이 소유해야 한다. PublicRoleGrants.Apply()는 REVOKE로 "허용 목록 밖의
#      부여"를 지우는데, REVOKE는 현재 롤이 부여자인 권한만 지운다 — public 스키마에 blog_app이 아닌 소유자의
#      테이블이 하나라도 있으면(superuser가 만든 것 포함) 그 REVOKE가 42501로 실패해 트랜잭션이 롤백되고 앱이
#      시작하지 못한다. 아래 `ALTER SCHEMA public OWNER TO blog_app`은 스키마 자체의 소유자만 바꾼다 —
#      이후 이 DB에 만드는 모든 테이블(EF 마이그레이션 포함)이 실제로 blog_app 소유로 생성되는지는 마이그레이션
#      코드 쪽 책임이다. 이 스크립트는 그 전제(스키마 소유자)만 마련한다.
#   2) 실행 순서: 이 스크립트는 compose가 postgres를 healthy로 올리기 전, api 컨테이너가 뜨기 전에 끝나야 한다
#      (docker-compose.yml의 `api: depends_on: postgres: condition: service_healthy`가 이를 강제한다 —
#      공식 이미지는 /docker-entrypoint-initdb.d의 모든 스크립트를 healthcheck가 통과하기 전에 실행한다).
#      blog_public 롤이 아직 없는 채로 앱이 먼저 뜨면 Apply()가 42704(role does not exist)로 기동을 거부한다
#      (fail-closed로 옳은 동작이지만, 이 순서 의존이 지켜지지 않으면 그 형태로 드러난다).
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
