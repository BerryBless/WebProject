# 테스트

테스트는 "코드가 도는가"가 아니라 **"주장한 경계가 실제로 막는가"**를 증명하도록 짰습니다. 그래서 인메모리 대역이 아니라 실제 MySQL·실제 브라우저·실제 호스트를 씁니다.

관련 문서: [보안 설계](security.md) · [개발 환경](development.md) · [진행 기록](history.md)

## 지형

| 층 | 무엇 | 개수(master) | 무엇을 증명하나 |
|---|---|---|---|
| .NET 통합·단위 | xUnit + `WebApplicationFactory` + Testcontainers MySQL | **625** | 접근 매트릭스, 세션 규칙, 마크다운 정제, 이미지 판정·메타데이터 제거, 속도 제한 체인, 공개 페이지·피드·sitemap, DB 제약 ⊇ 앱 검증 |
| 웹 단위 | Vitest + Testing Library + jsdom | **194** | API 클라이언트의 경로 검사, 에디터 상태 기계(저장 중 입력·409·임시본), 오픈 리다이렉트 방지, 소스 가드 |
| 브라우저 E2E | Playwright(Chromium·Firefox) + 실제 백엔드 + production 빌드 | **8** | 배포될 CSP 값 자체, 실제 쿠키·Origin 검사, sandbox iframe, 글쓰기 전 과정, CSP 위반 0건 |
| 배포 스모크 | Node 테스트 러너 + 실제 컨테이너 스택(운영과 같은 이미지·Caddyfile·compose) | 허용 IP 10·비허용 IP 6·오류 응답 1·seed 1·verify-restore 1, 복원 후 허용 IP 10 재실행 | 접근 통제·헤더·업로드 상한, DB 롤 권한(비 superuser, 공개 롤은 읽기 전용), 백업→볼륨 삭제→복원 바이트 단위 일치 |
| 스택 E2E | Playwright(Chromium·Firefox) + 실제 컨테이너 스택(`SMOKE_E2E=1`) | **8** | 배포될 CSP·헤더가 Caddy를 통과한 실제 응답, 로그인부터 글쓰기까지 전 과정 |

master는 .NET **625**, Vitest **194**, 브라우저 E2E **8**, 스택 E2E **8**, 스모크 **19**(허용 10·비허용 6·오류 응답 1·seed 1·verify-restore 1)입니다 → [배포 구성](deployment.md).

## 실행

```powershell
dotnet build PortfolioBlog.slnx -c Release          # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release          # 591개, 약 35초 (Docker 필요)

cd PortfolioBlog.Web
npm ci; npm run lint; npm run typecheck; npm test   # Vitest 188개
npm run e2e:prepare                                  # 개발 인증서, 버려질 MySQL(pb-e2e-mysql), 버려질 비밀번호 해시
npm run e2e                                          # Chromium·Firefox 8개, 재시도 없음
docker rm -f pb-e2e-mysql

bash deploy/smoke/run.sh                             # Docker 필요. 운영과 같은 이미지·Caddyfile·compose로 접근·헤더·한도·DB 롤·복원 리허설
SMOKE_E2E=1 bash deploy/smoke/run.sh                 # 위 + 스택 대상 브라우저 E2E 8개
# SMOKE_KEEP=1: 종료 후 스택을 유지(수동 확인용). SMOKE_HTTP_BIND=127.0.0.1:<포트>: 기본 8081이 Hyper-V 배타 예약 범위와 겹치는 기계용

pwsh scripts/harness-audit.ps1                       # 하네스 구조 8개 항목
```

E2E는 포트 7198·4173·3307을 씁니다 — 비어 있어야 합니다. 이 PC는 Hyper-V가 4173을 배타 예약해 로컬 브라우저 E2E가 막힙니다 — CI가 대신 판정합니다.

## 이 저장소의 테스트 규칙

- **새 테스트·고친 테스트는 사보타주로 실패를 확인합니다.** 제품 코드를 일부러 망가뜨려 그 테스트가 실제로 빨간불이 되는지 본 뒤 되돌립니다. 사보타주가 통과해 버리면 테스트의 **전제 조건**이 성립했는지부터 봅니다(3단계에서 세 번: 지울 임시본이 애초에 없었고, 가짜 범위가 현재 소스에 없는 구문이었습니다).
- **보안 통제와 테스트 하네스도 제품 코드만큼 의심합니다.** 3단계의 정규식 소스 가드는 세 번 연속 다른 입력에서 코드를 삼켜 그 구간의 위반을 놓쳤고, 테스트 도구의 "표에 없는 호출은 실패"는 거짓이었습니다. 지금은 TypeScript 파서 기반이며, 지운 위치가 어떤 토큰 안에도 없다는 것을 독립 파싱으로 교차 검증합니다.
- **절대 시간 상한을 단언하지 않습니다.** 느린 CI에서 깨지고 빠른 기계에서 거짓 통과합니다(로컬 0.8초 → CI 7.2초를 실측). 비율·상대 단언을 쓰고 시간은 `TimeProvider`로 주입합니다.
- **접근 매트릭스는 닫힌 세계입니다.** {허용 IP, 비허용 IP} × {로그인, 미로그인} × {헤더 유무} × {관리 호스트, 공개 호스트}를 모든 `/api` 엔드포인트에 적용하고, 새 엔드포인트가 표에 없으면 테스트가 실패합니다.
- **테스트 클래스마다 자기 DB**를 씁니다(팩토리가 새 DB 이름을 만들고 `Migrate()`가 생성). 해제할 때 연결 풀을 즉시 비웁니다 — 안 그러면 유휴 연결이 쌓여 "too many clients"가 간헐적으로 납니다(실측 최대 61개).

## 필수 통과 항목

- **마크다운 공격 코퍼스:** raw HTML, `javascript:`·`data:`·`vbscript:`와 대소문자·공백·제어문자 난독화, 자동 링크, 외부 이미지, 이미지 `onerror`, 코드블록 안 HTML → 출력에 실행 가능한 요소·속성 0개
- **접근 매트릭스:** XFF 위조 3종(신뢰 프록시 경유·비신뢰 출발지·Production 빈 설정=시작 실패), IPv4-mapped 주소, 잘못된 CIDR=시작 실패
- **세션:** 12시간 경과 401, 로그아웃 뒤 복사한 쿠키 재사용 401, 해시 교체 후 401, 로그인 6회째 429, 변경 요청의 Origin 누락·불일치 403
- **자원:** 비허용 업로드의 본문이 읽히지 않음, 10MB 초과 413, 본문 200KB 초과 400, 검색어 경계·LIKE 와일드카드, `page` 상한
- **출력:** 제목에 `<&"`를 넣은 글의 Atom·sitemap이 유효한 XML, 절대 URL이 위조된 Host가 아닌 설정값, EXIF GPS가 든 JPEG 업로드 후 메타데이터 없음, 첨부 `fileName`에 `../`를 넣어도 같은 파일
- **모델:** slug 형식·유일·불변, `SeriesOrder` CHECK, 시리즈 삭제 트랜잭션, `version` 불일치 409, 같은 새 태그로 동시 저장 2건 모두 성공

## CI

`.github/workflows/ci.yml`이 push·PR마다 ubuntu-latest에서 네 잡을 돕니다.

| 잡 | 내용 |
|---|---|
| `test` | restore → build(Release) → `dotnet test`(Testcontainers) |
| `web` | `npm ci` → `npm audit --omit=dev --audit-level=high` → lint → typecheck → test → build |
| `web-e2e` | 서비스 컨테이너 MySQL + Playwright(Chromium·Firefox). 실패하면 trace를 아티팩트로 올린다 |
| `deploy-smoke` | 운영과 같은 이미지·Caddyfile·compose를 띄워 접근 통제·헤더·한도·DB 롤·백업→삭제→복원·브라우저 E2E까지(`SMOKE_E2E=1 bash deploy/smoke/run.sh`). 실패하면 trace와 Caddy 접근 로그(`caddy.log`)를 아티팩트로 올린다 |

**첫 Linux 실행은 게이트로 취급합니다.** Windows에서 통과한 것이 Linux에서 처음 깨진 전례가 있습니다(절대 시간 상한, 개발 인증서 내보내기 경로).

## 알려진 문제

- **로컬 간헐 실패:** 전체 실행에서 드물게 1개가 **정확히 15초** 만에 실패합니다(`Database.Migrate()`의 새 연결이 연결 타임아웃). 빌드 직후처럼 기계가 바쁠 때, Windows Docker Desktop에서만 나타나고 Linux CI에서는 관측되지 않았습니다. 재실행하면 통과합니다.
- E2E의 세션 만료 단계는 미리보기의 401이 저장 클릭보다 먼저 오면 시간 초과로 깨질 수 있습니다(추론 — 로컬·CI에서 미발생). 데이터는 언마운트 flush가 지키므로 깨지는 것은 테스트뿐입니다.
- WebKit(Safari)은 검증하지 않았습니다.
