# 트러블슈팅

<!-- doc-harness:section id="summary" hash="2dcc75cfab26c7a08719dd1d9611c934d8f6b4bba5320bec8e8b80a5aabb402e" -->
## 한 줄 요약

장애 재발 시 바로 확인할 항목 11개. 증상 → 원인 → 확인 방법 → 해결 → 관련 코드 순서다.
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS001" hash="467a3d0bf696e2525cb73620daa14b30713fbb22366d98afebd2f797127f8f8d" -->
## TS001 GRANT … TO PUBLIC 뒤에도 공개 롤이 AdminState를 읽고 썼다. 관리 롤이 비 superuser인 운영 형태에서는 남의 소유 테이블 하나만 있어도 42501로 기동이 막혔다. superuser 연결 테스트는 둘 다 통과했다.

**원인 [CONFIRMED]:** PUBLIC 의사 롤 권한은 롤 단위 REVOKE로 회수되지 않고, 스키마 전체 REVOKE는 소유하지 않은 테이블에서 실패한다. 테스트 하네스가 superuser라 권한 검사를 우회했다.

**확인 방법:**
1. GRANT SELECT ON "AdminState" TO PUBLIC 실행
2. PublicRoleGrants.Apply 호출
3. 공개 롤로 AdminState SELECT가 42501인지 확인
4. 비 superuser 소유 DB에서도 Apply가 예외 없이 끝나는지 확인

**해결:**
- BuildStatements(role, ownedTables)로 pg_tables의 자기 소유 테이블만 PUBLIC과 롤 양쪽에서 회수한 뒤 허용 목록에만 SELECT 부여. 비 superuser 소유자 롤(owner_app_test)로 Apply를 검증하는 회귀 테스트 추가.

관련 코드: `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs`, `PortfolioBlog.Api.Tests/Infrastructure/PublicRoleGrantsTests.cs`, `PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS002" hash="cb058781121b49f906bd036b2c9f4e9cdea3da51a7fba55abbe51bb7956bfb16" -->
## TS002 헤더 블록을 관리 route 끝으로 옮겨도 5/5 통과하면서 실제 헤더가 사라졌다. 405·413·502 오류 응답과 두 도메인 밖 Host에 Server: Caddy가 남았고 handle_errors 응답엔 보안 헤더가 없었다. 점 파일 차단이 /.well-known/까지 막았다.

**원인 [CONFIRMED]:** Caddy 오류 응답 경로는 라우트의 지연 응답 래퍼를 거치지 않고, 폴백 사이트 블록이 없었으며, 테스트가 순서만 단언했다.

**확인 방법:**
1. 헤더 블록을 route 끝으로 이동
2. caddyfile.test.ts 실행 후 통과 여부 확인
3. 실제 응답 헤더 확인

**해결:**
- handle_errors에서 헤더·Server 삭제 처리, :80/:443 폴백 블록(404), 가드에 csp가 root * /srv보다 앞이라는 단언 추가, @dot에서 /.well-known 제외.

관련 코드: `deploy/Caddyfile`, `PortfolioBlog.Web/src/test/caddyfile.test.ts` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS003" hash="248e0511f11de723634b577a37d75e9d92ca3894844db2148c4ae6af57a70872" -->
## TS003 api 컨테이너가 인터넷에 접근 가능함이 실측으로 드러남.

**원인 [CONFIRMED]:** edge가 internal이 아니었다.

**확인 방법:**
1. compose 기동
2. api 컨테이너에서 외부 주소 접속 시도

**해결:**
- public(caddy만)·edge(internal)·db(internal) 3망으로 분리해 api는 default route가 없다.

관련 코드: `deploy/docker-compose.yml` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS004" hash="a1d464e6bdae071e0b36012ded9e632ac3324b803856300c4b742637688fc628" -->
## TS004 틀린 비밀번호로 superuser 접속이 성공했고, 오타·연결 실패도 부정 검사를 통과했다.

**원인 [CONFIRMED]:** 루프백 접속이 trust 규칙을 탔고 부정 검사가 실패 원인을 구분하지 않았다.

**확인 방법:**
1. -h 127.0.0.1로 틀린 비밀번호 접속 시도

**해결:**
- 컨테이너 네트워크 주소(-h postgres)로 scram을 강제하고 SQLSTATE·메시지로 판정.

관련 코드: `deploy/smoke/run.sh`, `deploy/smoke/smoke.test.mjs` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS005" hash="9c3a9216831e2f6492faae1eee7f73bb01bc95d983ea2b5408c3f69b88a39f8b" -->
## TS005 3망 분리 뒤 게이트웨이가 172.19 → 172.20으로 바뀌어 값이 기동마다 달라질 수 있었다.

**원인 [CONFIRMED]:** Docker 주소 풀 상태에 따른 동적 할당을 검증된 상수로 착각.

**확인 방법:**
1. 오버레이 없이 스모크 실행
2. 게이트웨이 주소 변동 확인

**해결:**
- 스모크 오버레이에서 public 서브넷을 172.30.1.0/24로 고정한 뒤 허용 목록에 사용.

관련 코드: `deploy/docker-compose.smoke.yml`, `deploy/smoke/run.sh` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS008" hash="c8006ef1158deac5443d26b3a187c67324c2e7509d3c4972f2e46f3949aa2a52" -->
## TS008 8개 중 1개(한 번) Firefox에서 이미지가 깨졌고 구현자는 기존 간헐 실패로 오분류했다.

**원인 [CONFIRMED]:** 이미지 로드 전 새로고침 레이스(재리뷰어가 코드로 추적).

**확인 방법:**
1. 스택 E2E를 Firefox에서 반복 실행

**해결:**
- 이미지 로드를 기다린 뒤 새로고침(Task 5 r2).

관련 코드: `PortfolioBlog.Web/e2e/admin.spec.ts` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS009" hash="6f8dde3e6957975b2f5ce6a29ad3b68f9f02609e0616d906f5564b84ad3c8578" -->
## TS009 catch가 meta를 error/-1로 덮어써 timeout·empty-output 상태와 exit_code가 기록되지 않았다.

**원인 [CONFIRMED]:** Stop 모드에서 Write-Error가 예외로 승격.

**확인 방법:**
1. TimeoutSec를 짧게 주고 지연 프롬프트 실행 후 meta status 확인

**해결:**
- Fail 함수로 상태를 한 번만 기록하고 stderr로 출력 후 exit 1, 시도별 로그 초기화, 타임아웃 상한 570초, 입출력 해시·thread_id 증빙.

관련 코드: `.claude/skills/cross-verify/scripts/invoke-codex.ps1` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS006" hash="acc0d3fd32b77e21e9f20b5fb644c628aa04877555c81c1ea1006d9debb8bfa5" -->
## TS006 오타 한 번이면 postgres 로그에 새 비밀번호가 평문으로 남았다.

**원인 [CONFIRMED]:** 서버가 문장 텍스트를 로그에 기록.

**확인 방법:**
1. 인라인 ALTER ROLE로 잘못된 문장 실행 후 postgres 로그 확인

**해결:**
- psql \password 대화형 절차(클라이언트가 SCRAM 검증자 계산)로 교체, 인라인 금지.

관련 코드: `deploy/OPERATIONS.md` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS007" hash="2fb43f217bf46cdc93569d41d3ab191098b9843b5717cb24c4c712a56bab857c" -->
## TS007 8073–8272 예약 범위로 8081·4173 바인딩 실패, AdGuard가 평문 HTTP HTML에 스크립트를 주입해 XSS로 오판할 위험.

**원인 [INFERRED]:** 환경 특이사항(winnat 포트 예약, 광고 차단 프록시).

**확인 방법:**
1. 이 PC에서 bash deploy/smoke/run.sh 실행

**해결:**
- 환경변수 탈출구 도입, 커밋 값은 유지.

관련 코드: `deploy/smoke/run.sh`, `docs/worklog.md` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS010" hash="4203ff87167a20e0ed130630983cf566422181f125f06a13515894f1046b6ec8" -->
## TS010 사용자 확인으로 턴이 끝나면 미검증 상태가 폴백 메시지로 커밋·푸시됨(8b00b44, 7f1535d 등). auto_commit_msg.txt가 잔류하고, .example 문자열이 있으면 민감 파일 검사가 해제되고, doc-harness state.json 같은 진행 파일이 계속 커밋됨.

**원인 [CONFIRMED]:** 센티널·잠금 부재, 메시지 소비 순서, 민감 필터 결함, 진행 상태 파일 미제외.

**확인 방법:**
1. 파이프라인 중 사용자 확인으로 턴 종료
2. git log에서 폴백 메시지 커밋 확인

**해결:**
- auto-commit.ps1 재작성(센티널·잠금·메시지 선소비·파일별 민감 필터·내용 스캔·push 실패 노출); 일부는 미커밋 .gitignore 수정으로 진행 중.

관련 코드: `scripts/auto-commit.ps1`, `.claude/settings.json`, `.gitignore` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="TS012" hash="8011bd7fbc39338e491da2741e757c0ffd35b4cd3fe51684e78e8c77b7a0550f" -->
## TS012 실행이 도구 상한에 걸릴 위험, 29개 기능 순차 분석 시 약 2.5시간, 기능 F003·F011은 attempts 2까지 재시도.

**원인 [CONFIRMED]:** 장시간 작업을 도구 수명에 종속시켰고 병렬도가 1이었다.

**확인 방법:**
1. harness run --auto를 도구 안에서 직접 실행

**해결:**
- 분리 프로세스 실행, feature_parallelism 3, 예산 상향(feature 5.0, verification 6.0), 동시 쓰기 경합은 직렬화·tmp 고유명·rename 재시도.

관련 코드: `doc-harness/config/harness.yaml`, `.claude/skills/doc-harness/SKILL.md`, `doc-harness/src/fsx.ts` · 기록일: 2026-09-23
<!-- /doc-harness:section -->

<!-- doc-harness:section id="related" hash="cf406202e6fe2a61dd55833a06b351e35a583c130e75a07a0cf4cbe337d321f2" -->
## 관련 문서

- [11_FAILURE_HISTORY](11_FAILURE_HISTORY.md)
- [10_ERROR_HANDLING](10_ERROR_HANDLING.md)
- [16_DEPLOYMENT](16_DEPLOYMENT.md)
<!-- /doc-harness:section -->
