# 실패 이력

<!-- doc-harness:section id="summary" hash="9694f433486d657eddc2170ea4b886b08bb19f58e6e8b5b7291c712496b81334" -->
## 한 줄 요약

기록된 실패·workaround 12건. 과거 기록은 코드에서 사라져도 보존한다.

| ID | 제목 | 원인 확신 | 현재 workaround | 최종 해결 |
|---|---|---|---|---|
| [FAIL001](#fail001) | 공개 DB 롤 권한 회수가 PUBLIC 권한과 비 superuser 소유 문제를 놓침 | CONFIRMED | 없음(해결됨). | BuildStatements(role, ownedTables)로 pg_tables의 자기 소유 테이블만 PUBLIC과 롤 양쪽에서 회수한 뒤 허용 목록에만 SELECT 부여. 비 superuser 소유자 롤(owner_app_test)로 Apply를 검증하는 회귀 테스트 추가. |
| [FAIL002](#fail002) | Caddy 보안 헤더 가드 테스트가 위치 단언이라 헤더 소실을 못 잡고 오류 응답·폴백 Host에 Server 헤더 노출 | CONFIRMED | 없음(해결됨). | handle_errors에서 헤더·Server 삭제 처리, :80/:443 폴백 블록(404), 가드에 csp가 root * /srv보다 앞이라는 단언 추가, @dot에서 /.well-known 제외. |
| [FAIL003](#fail003) | compose edge 네트워크가 internal이 아니라 api가 인터넷으로 나갈 수 있음 | CONFIRMED | 없음(해결됨). | public(caddy만)·edge(internal)·db(internal) 3망으로 분리해 api는 default route가 없다. |
| [FAIL004](#fail004) | 스모크 DB 롤 검사가 pg_hba trust 줄을 타 비밀번호를 검증하지 않음 | CONFIRMED | 없음(해결됨). | 컨테이너 네트워크 주소(-h postgres)로 scram을 강제하고 SQLSTATE·메시지로 판정. |
| [FAIL005](#fail005) | 스모크 허용 IP가 Docker 동적 게이트웨이 주소에 의존 | CONFIRMED | 운영 public 서브넷은 자동 할당(호스트 LAN과 겹치면 기동 실패, 운영 문서에 기재). | 스모크 오버레이에서 public 서브넷을 172.30.1.0/24로 고정한 뒤 허용 목록에 사용. |
| [FAIL006](#fail006) | 운영 문서의 DB 비밀번호 변경 명령이 로그에 평문 비밀번호를 남김 | CONFIRMED | 없음(해결됨). | psql \password 대화형 절차(클라이언트가 SCRAM 검증자 계산)로 교체, 인라인 금지. |
| [FAIL007](#fail007) | 이 PC의 Hyper-V 포트 예약이 스모크 8081을 막고 광고 차단기가 평문 HTTP를 변조 | INFERRED | SMOKE_HTTP_BIND로 로컬만 치환, 실제 호스트 프로브는 HTTPS로만, 최종 판정은 CI(Linux). | 환경변수 탈출구 도입, 커밋 값은 유지. |
| [FAIL008](#fail008) | Firefox에서 첨부 이미지 로드 전 새로고침 레이스 | CONFIRMED | 첨부가 많을 때 이미지 로드 대기 보강은 소형 후속 과제. | 이미지 로드를 기다린 뒤 새로고침(Task 5 r2). |
| [FAIL009](#fail009) | invoke-codex.ps1의 Write-Error가 예외로 승격돼 meta 상태를 덮어씀 | CONFIRMED | 없음(해결됨). | Fail 함수로 상태를 한 번만 기록하고 stderr로 출력 후 exit 1, 시도별 로그 초기화, 타임아웃 상한 570초, 입출력 해시·thread_id 증빙. |
| [FAIL010](#fail010) | Stop 훅 자동 커밋이 파이프라인 중간 상태·잔류 메시지를 커밋 | CONFIRMED | 실행 중 .git/harness_commit_in_progress 센티널 생성, run 진행 파일을 .gitignore에 추가(working tree 변경). | auto-commit.ps1 재작성(센티널·잠금·메시지 선소비·파일별 민감 필터·내용 스캔·push 실패 노출); 일부는 미커밋 .gitignore 수정으로 진행 중. |
| [FAIL011](#fail011) | 하네스 설정·템플릿 결함: settings.json 이스케이프, TddSession.csproj 중복 컴파일 | CONFIRMED | 없음(해결됨). | \\ 이스케이프 수정, EnableDefaultCompileItems=false와 실제 .cs 존재 조건으로 교체, Agent 팬아웃 방식 재작성, harness-audit.ps1로 재감사. |
| [FAIL012](#fail012) | doc-harness INITIAL 실행이 도구 10분 상한을 넘고 기능 분석이 순차라 지연 | CONFIRMED | Start-Process 분리 프로세스 + 로그 tail 모니터(30분마다 재무장), 중단 시 resume. | 분리 프로세스 실행, feature_parallelism 3, 예산 상향(feature 5.0, verification 6.0), 동시 쓰기 경합은 직렬화·tmp 고유명·rename 재시도. |
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL001" hash="64a09c04b2e3e09b89ad4ffc004cf61b9f8bdeee7ebf8608975a4375168982e9" -->
## <a id="fail001"></a>FAIL001 공개 DB 롤 권한 회수가 PUBLIC 권한과 비 superuser 소유 문제를 놓침

**문제:** 공개 조회 전용 롤(blog_public)이 SELECT만 갖도록 앱 시작 시 GRANT/REVOKE를 적용해야 했다.

**시도:** REVOKE ALL … FROM {role}과 REVOKE ON ALL TABLES IN SCHEMA public으로 회수한 뒤 허용 5테이블만 SELECT 부여.

**증상:** GRANT … TO PUBLIC 뒤에도 공개 롤이 AdminState를 읽고 썼다. 관리 롤이 비 superuser인 운영 형태에서는 남의 소유 테이블 하나만 있어도 42501로 기동이 막혔다. superuser 연결 테스트는 둘 다 통과했다.

**원인 [CONFIRMED]:** PUBLIC 의사 롤 권한은 롤 단위 REVOKE로 회수되지 않고, 스키마 전체 REVOKE는 소유하지 않은 테이블에서 실패한다. 테스트 하네스가 superuser라 권한 검사를 우회했다.

**실패한 해결:**
- REVOKE ALL … FROM {role}만 사용
- REVOKE ... ON ALL TABLES IN SCHEMA public

**현재 workaround:** 없음(해결됨).

**최종 해결:** BuildStatements(role, ownedTables)로 pg_tables의 자기 소유 테이블만 PUBLIC과 롤 양쪽에서 회수한 뒤 허용 목록에만 SELECT 부여. 비 superuser 소유자 롤(owner_app_test)로 Apply를 검증하는 회귀 테스트 추가.

**재발 시 확인 절차:**
1. GRANT SELECT ON "AdminState" TO PUBLIC 실행
2. PublicRoleGrants.Apply 호출
3. 공개 롤로 AdminState SELECT가 42501인지 확인
4. 비 superuser 소유 DB에서도 Apply가 예외 없이 끝나는지 확인

**장기 해결:** 권한 관련 테스트는 항상 비 superuser 운영 형태 롤로 실행한다.

관련 파일: `PortfolioBlog.Api/Infrastructure/Data/PublicRoleGrants.cs`, `PortfolioBlog.Api.Tests/Infrastructure/PublicRoleGrantsTests.cs`, `PortfolioBlog.Api.Tests/Infrastructure/PostgresContainerFixture.cs` · 관련 커밋: `531f2078de`, `b3085a0c41` · 근거 종류: code, commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL002" hash="13287b5e917364ff382619d07611aee831cedae8c1e4964cd45aa6f7a2fbecc0" -->
## <a id="fail002"></a>FAIL002 Caddy 보안 헤더 가드 테스트가 위치 단언이라 헤더 소실을 못 잡고 오류 응답·폴백 Host에 Server 헤더 노출

**문제:** 관리 사이트 응답에 보안 헤더 7개와 Server 헤더 제거가 보장돼야 했다.

**시도:** caddyfile.test.ts가 인덱스·탭 깊이 등 위치로 검사, Server 헤더 삭제는 라우트 안에서 처리.

**증상:** 헤더 블록을 관리 route 끝으로 옮겨도 5/5 통과하면서 실제 헤더가 사라졌다. 405·413·502 오류 응답과 두 도메인 밖 Host에 Server: Caddy가 남았고 handle_errors 응답엔 보안 헤더가 없었다. 점 파일 차단이 /.well-known/까지 막았다.

**원인 [CONFIRMED]:** Caddy 오류 응답 경로는 라우트의 지연 응답 래퍼를 거치지 않고, 폴백 사이트 블록이 없었으며, 테스트가 순서만 단언했다.

**실패한 해결:**
- 위치·탭 깊이 기반 정적 가드

**현재 workaround:** 없음(해결됨).

**최종 해결:** handle_errors에서 헤더·Server 삭제 처리, :80/:443 폴백 블록(404), 가드에 csp가 root * /srv보다 앞이라는 단언 추가, @dot에서 /.well-known 제외.

**재발 시 확인 절차:**
1. 헤더 블록을 route 끝으로 이동
2. caddyfile.test.ts 실행 후 통과 여부 확인
3. 실제 응답 헤더 확인

**장기 해결:** 실제 스택 응답 헤더를 스모크로 검증한다.

관련 파일: `deploy/Caddyfile`, `PortfolioBlog.Web/src/test/caddyfile.test.ts` · 관련 커밋: `531f2078de`, `07a04ec3c1` · 근거 종류: code, commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL003" hash="4b99aba3f8836bc110b22ffe58f59cb38e06fac226cdfb9242d358b2ed1f99a3" -->
## <a id="fail003"></a>FAIL003 compose edge 네트워크가 internal이 아니라 api가 인터넷으로 나갈 수 있음

**문제:** api 컨테이너는 외부로 나갈 수 없어야 했다.

**시도:** caddy↔api용 단일 edge 네트워크로 구성.

**증상:** api 컨테이너가 인터넷에 접근 가능함이 실측으로 드러남.

**원인 [CONFIRMED]:** edge가 internal이 아니었다.

**실패한 해결:**
- 단일 edge 네트워크

**현재 workaround:** 없음(해결됨).

**최종 해결:** public(caddy만)·edge(internal)·db(internal) 3망으로 분리해 api는 default route가 없다.

**재발 시 확인 절차:**
1. compose 기동
2. api 컨테이너에서 외부 주소 접속 시도

**장기 해결:** 스모크에 api 외부 접근 불가 검사를 유지한다.

관련 파일: `deploy/docker-compose.yml` · 관련 커밋: `531f2078de`, `07a04ec3c1` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL004" hash="a44045ddaf507fd04144e8d5c9f48a92e5aaf38c390e08e209236e5a366ebd36" -->
## <a id="fail004"></a>FAIL004 스모크 DB 롤 검사가 pg_hba trust 줄을 타 비밀번호를 검증하지 않음

**문제:** 스모크가 공개 롤의 인증·권한을 검증해야 했다.

**시도:** psql -h 127.0.0.1로 접속하고 0이 아닌 종료 코드를 통과로 판정.

**증상:** 틀린 비밀번호로 superuser 접속이 성공했고, 오타·연결 실패도 부정 검사를 통과했다.

**원인 [CONFIRMED]:** 루프백 접속이 trust 규칙을 탔고 부정 검사가 실패 원인을 구분하지 않았다.

**실패한 해결:**
- 127.0.0.1 접속
- 종료 코드만 판정

**현재 workaround:** 없음(해결됨).

**최종 해결:** 컨테이너 네트워크 주소(-h postgres)로 scram을 강제하고 SQLSTATE·메시지로 판정.

**재발 시 확인 절차:**
1. -h 127.0.0.1로 틀린 비밀번호 접속 시도

**장기 해결:** 부정 검사는 기대 오류 코드까지 단언한다.

관련 파일: `deploy/smoke/run.sh`, `deploy/smoke/smoke.test.mjs` · 관련 커밋: `531f2078de`, `b3085a0c41` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL005" hash="c08a64c100efb4e75e5e489df2a6710690fc4fe96379f8bb4b588c3745d1b0f5" -->
## <a id="fail005"></a>FAIL005 스모크 허용 IP가 Docker 동적 게이트웨이 주소에 의존

**문제:** 스모크 허용 IP 목록이 결정적이어야 했다.

**시도:** 계획 스파이크에서 검증한 게이트웨이 172.30.0.1을 허용 목록에 사용.

**증상:** 3망 분리 뒤 게이트웨이가 172.19 → 172.20으로 바뀌어 값이 기동마다 달라질 수 있었다.

**원인 [CONFIRMED]:** Docker 주소 풀 상태에 따른 동적 할당을 검증된 상수로 착각.

**실패한 해결:**
- 스파이크에서 잰 게이트웨이 주소 고정 사용

**현재 workaround:** 운영 public 서브넷은 자동 할당(호스트 LAN과 겹치면 기동 실패, 운영 문서에 기재).

**최종 해결:** 스모크 오버레이에서 public 서브넷을 172.30.1.0/24로 고정한 뒤 허용 목록에 사용.

**재발 시 확인 절차:**
1. 오버레이 없이 스모크 실행
2. 게이트웨이 주소 변동 확인

**장기 해결:** 서브넷·TrustedIp 리터럴 중복(4파일)을 한 곳으로 모은다.

관련 파일: `deploy/docker-compose.smoke.yml`, `deploy/smoke/run.sh` · 관련 커밋: `2a67f8ffd5`, `531f2078de` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL006" hash="d0732bde8b6f355e0d5cdfd32ebf0d147e9c3c296e569dd9dfd7369b6d098d69" -->
## <a id="fail006"></a>FAIL006 운영 문서의 DB 비밀번호 변경 명령이 로그에 평문 비밀번호를 남김

**문제:** 운영 절차로 DB 비밀번호 변경 방법을 제공해야 했다.

**시도:** 인라인 ALTER ROLE … PASSWORD 명령.

**증상:** 오타 한 번이면 postgres 로그에 새 비밀번호가 평문으로 남았다.

**원인 [CONFIRMED]:** 서버가 문장 텍스트를 로그에 기록.

**실패한 해결:**
- 인라인 ALTER ROLE PASSWORD

**현재 workaround:** 없음(해결됨).

**최종 해결:** psql \password 대화형 절차(클라이언트가 SCRAM 검증자 계산)로 교체, 인라인 금지.

**재발 시 확인 절차:**
1. 인라인 ALTER ROLE로 잘못된 문장 실행 후 postgres 로그 확인

**장기 해결:** 운영 명령은 실행하고 로그까지 확인한다.

관련 파일: `deploy/OPERATIONS.md` · 관련 커밋: `2a67f8ffd5`, `531f2078de` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL007" hash="c6514567b4a2bb021608d727f945812eb86a6cb13d0e069d92a9f05a7aee1a12" -->
## <a id="fail007"></a>FAIL007 이 PC의 Hyper-V 포트 예약이 스모크 8081을 막고 광고 차단기가 평문 HTTP를 변조

**문제:** 로컬 Windows에서 스모크·E2E가 안정적으로 실행돼야 했다.

**시도:** 고정 포트 8081/4173과 평문 HTTP 프로브 사용.

**증상:** 8073–8272 예약 범위로 8081·4173 바인딩 실패, AdGuard가 평문 HTTP HTML에 스크립트를 주입해 XSS로 오판할 위험.

**원인 [INFERRED]:** 환경 특이사항(winnat 포트 예약, 광고 차단 프록시).

**실패한 해결:**
- 커밋 값 8081 변경(채택 안 함)

**현재 workaround:** SMOKE_HTTP_BIND로 로컬만 치환, 실제 호스트 프로브는 HTTPS로만, 최종 판정은 CI(Linux).

**최종 해결:** 환경변수 탈출구 도입, 커밋 값은 유지.

**재발 시 확인 절차:**
1. 이 PC에서 bash deploy/smoke/run.sh 실행

**장기 해결:** CI를 정본 판정으로 삼는다.

관련 파일: `deploy/smoke/run.sh`, `docs/worklog.md` · 관련 커밋: `2a67f8ffd5`, `e6bde9dbc7` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL008" hash="591254d44778bf1bb7744b620dfa2def419fb743f0591b7d1003d6cac1697291" -->
## <a id="fail008"></a>FAIL008 Firefox에서 첨부 이미지 로드 전 새로고침 레이스

**문제:** 스택 E2E가 Chromium·Firefox 모두 안정적이어야 했다.

**시도:** 이미지 로드를 기다리지 않고 새로고침.

**증상:** 8개 중 1개(한 번) Firefox에서 이미지가 깨졌고 구현자는 기존 간헐 실패로 오분류했다.

**원인 [CONFIRMED]:** 이미지 로드 전 새로고침 레이스(재리뷰어가 코드로 추적).

**실패한 해결:**
- 기존 간헐 실패로 간주

**현재 workaround:** 첨부가 많을 때 이미지 로드 대기 보강은 소형 후속 과제.

**최종 해결:** 이미지 로드를 기다린 뒤 새로고침(Task 5 r2).

**재발 시 확인 절차:**
1. 스택 E2E를 Firefox에서 반복 실행

**장기 해결:** CI 게이트 테스트는 여러 번 돌려 판정한다.

관련 파일: `PortfolioBlog.Web/e2e/admin.spec.ts` · 관련 커밋: `2a67f8ffd5`, `531f2078de` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL009" hash="2a7245b1c62b8e821ed107a4d51f99cb0e02efa12ebb8306ff6172de997d86ac" -->
## <a id="fail009"></a>FAIL009 invoke-codex.ps1의 Write-Error가 예외로 승격돼 meta 상태를 덮어씀

**문제:** Codex 호출 결과(timeout/empty-output 등)를 meta에 정확히 기록해야 했다.

**시도:** $ErrorActionPreference='Stop' 아래 실패 경로마다 Write-Meta 후 Write-Error.

**증상:** catch가 meta를 error/-1로 덮어써 timeout·empty-output 상태와 exit_code가 기록되지 않았다.

**원인 [CONFIRMED]:** Stop 모드에서 Write-Error가 예외로 승격.

**실패한 해결:**
- Write-Error 기반 실패 보고

**현재 workaround:** 없음(해결됨).

**최종 해결:** Fail 함수로 상태를 한 번만 기록하고 stderr로 출력 후 exit 1, 시도별 로그 초기화, 타임아웃 상한 570초, 입출력 해시·thread_id 증빙.

**재발 시 확인 절차:**
1. TimeoutSec를 짧게 주고 지연 프롬프트 실행 후 meta status 확인

**장기 해결:** meta 해시 증빙으로 위조를 탐지한다.

관련 파일: `.claude/skills/cross-verify/scripts/invoke-codex.ps1` · 관련 커밋: `738cd87108`, `ec918d4c00` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL010" hash="0ef577fafec9ff8e95099c94003576996ea7bfb437b868c75ab4b8023d392c8b" -->
## <a id="fail010"></a>FAIL010 Stop 훅 자동 커밋이 파이프라인 중간 상태·잔류 메시지를 커밋

**문제:** Stop 훅이 안전망으로 자동 커밋하는데 다단계 작업 중에도 커밋했다.

**시도:** 매 턴 종료 시 git add -A 후 폴백 메시지로 커밋.

**증상:** 사용자 확인으로 턴이 끝나면 미검증 상태가 폴백 메시지로 커밋·푸시됨(8b00b44, 7f1535d 등). auto_commit_msg.txt가 잔류하고, .example 문자열이 있으면 민감 파일 검사가 해제되고, doc-harness state.json 같은 진행 파일이 계속 커밋됨.

**원인 [CONFIRMED]:** 센티널·잠금 부재, 메시지 소비 순서, 민감 필터 결함, 진행 상태 파일 미제외.

**실패한 해결:**
- 훅 단독 안전망 의존

**현재 workaround:** 실행 중 .git/harness_commit_in_progress 센티널 생성, run 진행 파일을 .gitignore에 추가(working tree 변경).

**최종 해결:** auto-commit.ps1 재작성(센티널·잠금·메시지 선소비·파일별 민감 필터·내용 스캔·push 실패 노출); 일부는 미커밋 .gitignore 수정으로 진행 중.

**재발 시 확인 절차:**
1. 파이프라인 중 사용자 확인으로 턴 종료
2. git log에서 폴백 메시지 커밋 확인

**장기 해결:** 중간 상태는 센티널로 차단하고 진행 파일은 추적하지 않는다.

관련 파일: `scripts/auto-commit.ps1`, `.claude/settings.json`, `.gitignore` · 관련 커밋: `738cd87108`, `ec918d4c00`, `432327ff0a` · 근거 종류: commit, doc, code · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL011" hash="d7b8b6a8f29daca6cedad88ac1065d6d27fc2e47f0ca57d3c8211bc8fdc238c4" -->
## <a id="fail011"></a>FAIL011 하네스 설정·템플릿 결함: settings.json 이스케이프, TddSession.csproj 중복 컴파일

**문제:** 이식한 하네스가 이 저장소에서 동작해야 했다.

**시도:** ClaudeCodeStudy 설정과 TDD csproj 템플릿을 그대로 이식.

**증상:** settings.json이 파싱되지 않았고, csproj 템플릿은 NETSDK1022 중복과 빈 03_qa/Src에 대한 Exists() 오판으로 스텁이 컴파일되지 않았다(CS0103). 팀 도구(TeamCreate 등) 의존 오케스트레이터도 동작 불가.

**원인 [CONFIRMED]:** JSON 백슬래시 이중 처리 누락, SDK 기본 글로빙 미차단, 빈 디렉터리 Exists 판정, 이 빌드에 없는 팀 도구 참조.

**실패한 해결:**
- 원본 그대로 이식

**현재 workaround:** 없음(해결됨).

**최종 해결:** \\ 이스케이프 수정, EnableDefaultCompileItems=false와 실제 .cs 존재 조건으로 교체, Agent 팬아웃 방식 재작성, harness-audit.ps1로 재감사.

**재발 시 확인 절차:**
1. pwsh scripts/harness-audit.ps1 실행

**장기 해결:** harness-audit를 CI에 추가한다.

관련 파일: `.claude/settings.json`, `.claude/skills/tdd-orchestrator/SKILL.md`, `scripts/harness-audit.ps1` · 관련 커밋: `8b19dacb69`, `8da2beef6c`, `76ac158c4a` · 근거 종류: commit, doc · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="FAIL012" hash="2b2a2b63b09bdc2c6f0c3fc1e57fcc00de35b9ff6e5f6c6a12d499140f56b97d" -->
## <a id="fail012"></a>FAIL012 doc-harness INITIAL 실행이 도구 10분 상한을 넘고 기능 분석이 순차라 지연

**문제:** INITIAL 문서화는 1~3시간 걸리는데 Bash/PowerShell 도구는 명령당 10분 상한이 있다.

**시도:** run_in_background Bash로 harness 실행, feature_parallelism 1.

**증상:** 실행이 도구 상한에 걸릴 위험, 29개 기능 순차 분석 시 약 2.5시간, 기능 F003·F011은 attempts 2까지 재시도.

**원인 [CONFIRMED]:** 장시간 작업을 도구 수명에 종속시켰고 병렬도가 1이었다.

**실패한 해결:**
- run_in_background Bash 실행
- feature_parallelism 1

**현재 workaround:** Start-Process 분리 프로세스 + 로그 tail 모니터(30분마다 재무장), 중단 시 resume.

**최종 해결:** 분리 프로세스 실행, feature_parallelism 3, 예산 상향(feature 5.0, verification 6.0), 동시 쓰기 경합은 직렬화·tmp 고유명·rename 재시도.

**재발 시 확인 절차:**
1. harness run --auto를 도구 안에서 직접 실행

**장기 해결:** 재개 명령 resume으로 이어간다.

관련 파일: `doc-harness/config/harness.yaml`, `.claude/skills/doc-harness/SKILL.md`, `doc-harness/src/fsx.ts` · 관련 커밋: `a651e25e26`, `1553dcf3a9` · 근거 종류: commit, code · 기록일: 2026-09-24
<!-- /doc-harness:section -->

<!-- doc-harness:section id="unknowns" hash="0d956f2ed571f281185835788d9a7418eaf09da6c5e99e33e61ea7fd5e805759" -->
## 확인하지 못한 것

- Codex 2차 교차 검증에서 제기된 PARA 백엔드 계획 결함은 해당 코드가 삭제되어 현재 코드와 대조하지 못했다.
- 2A 계획 결함 16건과 2B 결함 23건의 개별 원인은 diff가 잘려 확인하지 못했다.
- 로컬 Windows Docker Desktop의 간헐 15초 연결 타임아웃 원인은 미확인이다.
- doc-harness F003·F011이 2회 시도된 구체적 실패 원인은 로그가 없어 알 수 없다.
- plan/doc_harness_0923.md 7절이 기록한 doc-harness 실전 결함 12건(특히 12번: 수정 루프 회차 이력이 resume 시 복원되지 않아 LLM 재검증 비용 반복)은 하네스 자체(doc-harness/)의 문제이고 분석 대상이 아니다. 이번 델타에는 해당 코드 변경이나 커밋이 없어 새 실패로 기록하지 않는다. 기존 FAIL012와의 관계도 확인되지 않았다.
- TagEndpoints.cs의 추가 한 줄은 주석뿐이고 임시 점검용이라고 적혀 있다. 되돌려졌는지는 이 입력으로 확인할 수 없다.
<!-- /doc-harness:section -->
