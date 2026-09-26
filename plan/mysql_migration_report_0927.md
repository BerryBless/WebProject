# MySQL 전환 실행 보고서 — 2026-09-27

스펙: `plan/mysql_migration_0926.md`(대체표 D1~D19, 판정 R1~R6, 오류 번호 2.4절, 7절 잔여 위험). 계획: `plan/mysql_migration_impl_0926.md`(Task 0~11). 브랜치 `feat/mysql-migration`(분기점 master `e486288`) → **PR #7 squash 병합, master `37c432c`**(2026-09-27). 실행 방식: Subagent-Driven Development — 태스크마다 새 구현자 → 스펙·품질 리뷰 → 수정 라운드 → 범위 재리뷰, 마지막에 브랜치 전체 최종 리뷰와 fix wave.

## 1. 한눈에

| 항목 | 결과 |
|---|---|
| 요청 | "모든 데이터를 MySQL로" → 질의로 확정: DB 테이블만(첨부 바이트·DataProtection 키는 볼륨 유지), PG 완전 교체, 이관 데이터 없음 |
| Task 0 스파이크 | Oracle `MySql.EntityFrameworkCore` 10.0.9 **no-go** → Pomelo 비교 스파이크 **go** → 사용자 선택으로 **Pomelo 9.0.0 + EF Core 9.0.20**(MySqlConnector 2.4.0). 계획·스펙 개정 `5f419d9` |
| Task 1 순수 부품(TDD) | `7c799cf`·`aae8a52` — 오류 분류기·잠금 이름·권한 판정·UTC 변환기. 리뷰: 절대 실패하지 않는 단언 1건 교체 |
| Task 2 프로덕션 코드 전환 | `90dff4e` — Npgsql 제거, 인터셉터 3종, GET_LOCK, SHOW GRANTS 검증, 마이그레이션 재생성, 실제 MySQL 기동·/health 200 |
| Task 3 테스트 기반 | `dcc23bf`·`aea244c` — Testcontainers MySQL, 팩토리별 DB·공개 사용자, PG 고유 단언 걷어냄(복귀처 표대로 Task 4~8에서 재증명) |
| Task 4~8 통제 재증명 | `3905413`(공개 권한) · `fc59865`(공개 세션) · `a990f01`·`1539da3`(GET_LOCK, 팩토리 풀 누수 수정) · `9523bc7`(행 버전·격리·태그 동시성·FOR UPDATE 실잠금) · `47b800a`·`72dfcb4`(오류 매핑·콜레이션·정규식 앵커·연결 보안) |
| Task 9 배포 | `037c10e`·`9b09edb` — compose `mysql:8.4.11`, init 스크립트, mysqldump 백업·복원, 스모크. 리뷰로 init 비밀번호 argv 노출 제거·root 소켓 전용 |
| Task 10 CI·E2E | `4adfddd`·`3a243d2` — web-e2e 서비스 컨테이너, e2e-prepare, CI 트리거에 `feat/**` |
| Task 11 문서 | `69dd863`·`6038f02`·`fc8e84a`·`95ca275` — README·docs 9개·OPERATIONS·CLAUDE.md/AGENTS.md·스펙, PG 주석 잔재, CLAUDE.md `$row` 오염 제거 |
| 최종 리뷰 | Critical 0 · Important 4(MDL 상한, PAD SPACE, EF 9 종료일 미기록, docs/generated) → fix wave `b939ece`~`aa40435` 전부 반영, 재리뷰 clean |
| 검증 수치 | .NET **675/675**, 빌드 경고 0, 스모크 통과(복원 리허설 포함), CI 4잡 green(PR run 36198290084, 병합 후 master run 36263574742) |
| 미완 | `docs/generated/` 갱신(문서화)이 계정 세션 한도로 2회 중단 → 사용자 결정으로 후속(6절) |

## 2. 만든 것

- **프로바이더·등록.** `UseMySql(cs, ServerVersion.Create(8.4.11, MySql))` 고정(AutoDetect 금지 — 옵션을 만들 때 연결을 연다). 두 연결 모두 `ConnectionReset=true` 강제(`WithSessionReset`).
- **공개 조회 경로.** `PublicSessionInterceptor`가 연결을 열 때마다 `transaction_read_only=ON, max_execution_time, lock_wait_timeout`. `PublicRoleGrants.Apply`가 5개 테이블 `GRANT SELECT` 후 **공개 연결 자신의 `SHOW GRANTS`**로 정확한 집합을 검증하고, 초과면 기동 실패(자동 회수 안 함, R6). `blog_app`은 `blog.*` 한정 `WITH GRANT OPTION`(R1).
- **동시성.** `ReadCommittedTransactionInterceptor`(MySqlConnector가 인자 없는 `BeginTransaction()`에서 REPEATABLE READ를 강제 — 실측) + 서버 플래그. 앱 관리 `Version`(`PostVersionInterceptor`, 시리즈 해제 벌크 갱신도 +1, 소스 스캔 테스트로 강제). 태그는 서수 순서 `ON DUPLICATE KEY UPDATE`.
- **첨부 잠금.** `GET_LOCK`(이름 `att:` + DB 이름 해시 8 + SHA 앞 48, 61자), 10초 → `DbLockTimeoutException` → 503, 잠금 세션에 `lock_wait_timeout`도 설정(PG `lock_timeout` 동등), 취소 경쟁 시 `RELEASE_LOCK` 최선 노력.
- **스키마.** 식별자 열 `utf8mb4_0900_bin`(NO PAD), `REGEXP_LIKE(..., '\z', 'c')` CHECK, `CHAR(36)` Guid, `DATETIME(6)` UTC 변환기, 내림차순 인덱스.
- **오류.** 단일 `DbErrorClassifier` — 1062·1451/1452 → 409, 3024(실행 시간·MDL)·1205·`DbLockTimeoutException`·1213 → 503.
- **시작 검증.** `SslMode=Required` 이상(비 Development), `AllowPublicKeyRetrieval` 금지, Default 명령 시간 제한은 0 또는 잠금 대기(10초) 초과.
- **배포.** root 소켓 전용(`MYSQL_ROOT_HOST=localhost`), `require_secure_transport=ON`, `local_infile=0`, `secure_file_priv=NULL`, 사용자 `REQUIRE SSL`, init 비밀번호는 `printf` 내장으로만, 헬스체크는 blog_app 실쿼리, `mysqldump --single-transaction` 백업·DROP→가져오기 복원.

## 3. 검증 — 무엇에 근거한 결론인가

- 보안 통제마다 **실제 MySQL 서버에서** 테스트했고, 핵심 테스트는 코드를 일부러 깨서 실패하는지 확인했다(변이 검사: 인터셉터 제거, Version 증가 제거, 잠금 이름 DB 태그 제거, 콜레이션·정규식 앵커 되돌리기, 교착 매핑 제거 등).
- 리뷰가 찾은 것(구현 보고만으로는 지나갔을 것): 절대 실패하지 않는 Kind 단언(T1), 테스트 팩토리가 엉뚱한 풀을 비워 연결 누수(T5에서 발견 → T6 수정 — Pomelo가 연결 문자열에 옵션을 덧붙여 풀 키가 달라짐), 원시 연결로는 영원히 통과하는 세션 리셋 단언(T5), 해시 CHECK의 `\z`/`$`를 구별 못 하는 런타임 케이스(T8 → 스키마 정적 검사), init 스크립트의 `sed` argv 비밀번호 노출·네트워크 root(T9), 문서의 틀린 SET 문장·거꾸로 쓴 D7 근거(T11), 잠금 세션 MDL 무기한 대기·PAD SPACE(최종).
- 측정으로 확정한 사실: MDL 대기 → 3024(1205 아님), FILE 없는 INTO OUTFILE → 1227, `secure_file_priv=NULL`은 값이 문자열 `'NULL'`로 보이지만 root(FILE 보유)로도 모든 경로 1290 — 빈 값(`=`)이 오히려 무제한, 원격 root → 1045, 비TLS → 3159(새 사용자 첫 비TLS 로그인은 2061).

## 4. 판정 기록(질문 없이 내린 것)

| # | 판정 | 틀렸을 때 비용 |
|---|---|---|
| 1 | 스파이크(Task 0)는 컨트롤러가 go 조건표와 대조해 검증 | 스파이크 코드 결함이 판정을 오도 |
| 2 | 태스크 경계 조정: KeyFor 참조는 T3에서, DeployInitScriptTests는 T9에서 | 없음 |
| 3 | Oracle no-go 후 사용자에게 묻기 전에 Pomelo 비교 스파이크 | 스파이크 비용 |
| 4 | 잠금 해제 표현 완화("늦어도 재대여 시"), 서버 격리 플래그도 필수 | 주석 과장/과소 |
| 5 | 계획의 무의미 단언 교체 | 없음 |
| 6 | `Version`에 DB DEFAULT 없음(모든 삽입이 EF·인터셉터 경유) | 원시 삽입 행 Version=0 |
| 7 | 시리즈 삭제 FOR UPDATE가 실제로 행을 잠그는지 증명 테스트 추가 | 경쟁 보호가 조용히 빠진 채 병합 |
| 8 | 계획 코드가 빠뜨린 규칙상 remarks 보충 | 없음 |
| 9 | 테스트 팩토리가 앱이 실제 쓴 풀을 비우도록 수정 | 대규모 스위트 간헐 too many connections |
| 10 | 해시 CHECK는 스키마 정적 검사로 증명 | 없음 |
| 11 | Npgsql `Options` 테스트 폐기 근거 정정(공격면 자체가 없음) | 없음 |
| 12 | init 비밀번호 `sed` → `printf` | 없음 |
| 13 | root 소켓 전용 | 원격 root 운영 불가(현재 사용처 없음) |
| 14 | CI 트리거에 `feat/**` | 트리거 정책 확대 |
| 15 | Task 11 분할(문서는 서브에이전트, 문서화·검증·PR은 컨트롤러) | 없음 |
| 16 | 문서성 minor를 Task 11에 포함 | 없음 |
| 17 | CLAUDE.md/AGENTS.md `$row` 제거(컨트롤러의 `sed` 실수) | 없음 |
| 18 | 문서화 비용이 고지치를 넘을 수 있어 실행 전 사용자 확인 | 생성 문서 지연 |
| 19 | 잠금 세션 `lock_wait_timeout`(PG 동등성) | 없음 |
| 20 | NO PAD 콜레이션 + 마이그레이션 재생성 | 재생성 비용 |
| 21 | EF 9 종료일·대응을 7절에 기록 | 없음 |
| 22 | 명령 시간 제한 검증·취소 시 잠금 해제·스모크 NULL 판정 | 없음 |

## 5. 교훈

- **프로바이더는 스파이크로 고른다.** "EF 10을 지원하는 유일한 프로바이더"라는 조건만으로 고른 Oracle은 DDL 반영·SQL 생성 결함 두 건으로 탈락했다. go/no-go 기준을 계획 첫 태스크에 둔 덕에 코드 한 줄 바꾸기 전에 멈췄다.
- **드라이버 기본 동작은 서버 설정을 이긴다.** MySqlConnector는 인자 없는 트랜잭션마다 REPEATABLE READ를 보낸다. 서버를 READ COMMITTED로 두어도 앱 쪽 보장이 필요했다.
- **"같은 연결 문자열"은 같은 풀이 아니다.** Pomelo가 옵션을 덧붙여 원시 연결과 EF 연결의 풀이 갈렸다. 풀을 가정하는 테스트는 앱과 같은 경로로 연결을 만들어야 한다 — 이 가정에 기댄 단언 두 개가 변이 검사에서야 "절대 실패하지 않음"으로 드러났다.
- **변이 검사가 리뷰보다 먼저 거짓 녹색을 잡는다.** 잠금 세션 고정 테스트는 읽기 순서 때문에 변이에서도 통과했고, 순서를 바꾸고서야 판별력이 생겼다.
- **설정 값의 표시와 실제 효과는 다를 수 있다.** `secure_file_priv=NULL`은 문자열로 보여 설정 오류처럼 보였지만 실측하니 완전 차단이었고, 빈 값이 무제한이었다. 추론 대신 버리는 컨테이너로 쟀다.
- **컨트롤러의 셸 한 줄도 리뷰 대상이다.** 계획 표에 행을 넣던 `sed`가 CLAUDE.md·AGENTS.md 전 줄 사이에 `$row`를 끼웠고, 하네스 감사(미러 일치)는 양쪽이 똑같이 오염돼 못 잡았다. 문서 리뷰가 잡았다.

## 6. 비용(API 정가 추정)

| 구분 | 비용 |
|---|---|
| 컨트롤러(메인 세션, Opus 5.5) | $39.63 |
| 구현자 12(Task 1~11·최종 fix) | $44.40 — 최대는 Task 11 문서 $16.75(205턴) |
| 리뷰어 18 | $15.43 |
| 스파이크 2 | $6.55 |
| 계획 개정·탐색 | $3.49 |
| **코드 작업 합계** | **약 $109.50** |
| 문서화 하네스(run-0003, 2회 중단) | 약 $150+ — `resume`이 끝난 기능 분석을 재사용하지 못해 두 번 전부 다시 돌았다 |

캐시 적중 90~99%로 비용의 대부분은 긴 문맥 재읽기다. 문서화 진행 이벤트마다 컨트롤러 턴이 소비된 것은 낭비였다(감시 필터를 좁혀 줄임).

## 7. 남은 일

1. **`docs/generated/` 갱신** — 아직 PG 서술(41개 파일). 먼저 doc-harness `resume`이 성공한 단계 산출물을 재사용하도록 고친 뒤 `문서화`. 중단 기록은 `doc-harness/workspace/runs/run-0003`. ADR-003 → ADR-011 대체, ADR-008 개정(스펙 8절 초안).
2. **EF Core 9 지원 종료 예정 2026-11-10**(공식 정책으로 재확인 필요) — Pomelo의 EF 10 출시를 감시하고, 없으면 Oracle/Pomelo 스파이크를 다시 돌려 결정(스펙 7절).
3. 로컬 개발 DB 볼륨 재생성(마이그레이션 ID 변경).
4. 수용한 잔여(스펙 7절): TLS 인증서 미검증, 검색 악센트 무시, 공개 세션의 read_only 자체 해제(권한 겹이 막음), DDL 자동 커밋(마이그레이션 전 백업), 잠금 테스트의 `performance_schema` 계측 의존, 명령 시간 제한 여유 1초 경계값 등 minor.
