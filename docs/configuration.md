# 설정 키

`appsettings.json` · `appsettings.Development.json` · 환경변수 · user-secrets로 채웁니다. 환경변수는 `__`로 계층을 구분합니다(`Site__PublicOrigin`). 기본값은 코드(`SiteOptions`·`PublicOptions`·`RenderingOptions`·`AdminOptions`·`AttachmentOptions`)에서 확인한 값입니다.

관련 문서: [개발 환경](development.md) · [배포 구성](deployment.md) · [보안 설계](security.md)

## 시작할 때 검증되는 것

`StartupValidation`이 마이그레이션보다 **먼저** 돕니다. 형식 오류는 모든 환경에서, 아래 필수 항목은 `Development`가 아닌 모든 환경에서 **시작 실패**입니다(`Staging`이나 오타난 환경 이름이 그냥 통과하지 않게 `IsProduction()` 대신 `IsDevelopment()` 부정으로 판정합니다).

| 조건 | 왜 |
|---|---|
| `Proxy:TrustedIp`가 비어 있지 않다 | 비면 ForwardedHeaders가 모든 `X-Forwarded-For`를 믿어 IP 검사가 무의미해진다 |
| `Admin:AllowedCidrs`가 비어 있지 않고 전부 파싱된다 | 허용 목록이 비면 관리 표면이 열리거나 잠긴다 |
| `Admin:PasswordHash`가 있고 base64다 | 없으면 로그인 자체가 불가능 |
| 두 origin이 서로 다르다 | 같으면 서브도메인 격리(쿠키가 공개 호스트로 새지 않음)가 사라진다 |
| 두 origin의 스킴이 `https`다 | 세션 쿠키가 `Secure`라 http origin은 쿠키를 주고받지 못한다 |
| `Attachments:RootPath`가 절대 경로다 | 상대 경로는 작업 디렉터리에 따라 엉뚱한 위치를 가리킨다 |

`Development`는 로컬 편의를 위해 위 검사에서 예외입니다(같은 origin 하나, 상대 경로 저장소).

## 사이트

| 키 | 기본값 | 의미 |
|---|---|---|
| `Site:PublicOrigin` | (빈 값 — 형식 검증 대상) | 공개 사이트의 절대 origin. 피드·sitemap의 절대 URL과 호스트 필터가 이 값을 쓴다(위조된 `Host`를 쓰지 않는다) |
| `Site:AdminOrigin` | (빈 값) | 관리 사이트의 절대 origin. 변경 요청의 `Origin` 검사 기준 |
| `Site:Title` | `Blog`(공백 불가 — 시작 실패) | `<title>`·머리글·Atom 피드 제목 |
| `Site:Description` | (빈 값) | 첫 쪽 meta description·Atom subtitle. 비면 생략 |
| `Site:Author` | (빈 값) | Atom 작성자 이름. 비면 `Site:Title`을 쓴다 |

## 연결과 저장소

| 키 | 기본값 | 의미 |
|---|---|---|
| `ConnectionStrings:Default` | (빈 값 — 없으면 시작 실패) | 관리 연결 문자열(MySqlConnector). `AllowPublicKeyRetrieval=true`는 **금지**(공개키를 바꿔치기해 비밀번호를 빼낼 수 있어 시작 실패), 비개발 환경은 `SslMode`가 `Required` 이상이어야 한다. `Command Timeout`(초)을 지정하면 ×1000이 `Public:StatementTimeoutMs`보다 **커야** 한다 — 어기면 시작 실패(클라이언트 취소가 DB의 `max_execution_time`보다 먼저 나면 503 매핑이 깨진다). `Command Timeout=0`(무한)은 검사 제외. 이 관리 연결은 추가로 `Command Timeout`이 0이거나 첨부 잠금 대기 상한(10초)보다 **커야** 한다 — 아니면 경합 중인 `GET_LOCK`이 서버의 타임아웃 응답보다 먼저 클라이언트에서 끊겨 503 대신 500이 된다(시작 실패) |
| `ConnectionStrings:Public` | (빈 값 — 비개발 환경에서 필수) | 공개 페이지 조회 전용 연결. **테이블 소유자가 아닌 별도 DB 사용자**여야 한다(사용자 이름은 소문자·숫자·밑줄, 관리 연결과 같으면 시작 실패). 앱이 시작할 때마다 그 사용자의 권한을 허용 테이블 5개의 `SELECT`로 GRANT하고, 공개 연결 자신의 `SHOW GRANTS`로 정확히 그 집합인지 검증한다(불일치·초과 권한이면 시작 실패, fail-closed). 비면(개발) 관리 연결로 조회한다 |
| `DataProtection:KeysPath` *(브랜치 `feature/blog-deploy`)* | (빈 값 — 비개발 환경에서 절대 경로 필수) | 세션 쿠키 암호화 키를 둘 경로. 컨테이너에서는 `dpkeys` 볼륨. 비면(개발) 프레임워크 기본 위치 |
| `Attachments:RootPath` | (빈 값 — 필수) | 첨부 저장 루트. 정적 파일 루트 **밖**이어야 한다. 비개발 환경은 절대 경로 |
| `Attachments:JanitorEnabled` | `true` | 고아 파일 청소 잡(시작 직후 1회 + 6시간마다). 마지막 쓰기가 1시간 안인 파일, 내용 주소 모양이 아닌 파일, 심볼릭 링크·정션은 건드리지 않는다. 지우는 것은 오래된 `.tmp`와 참조 없는 첨부 파일뿐이며 삭제 직전 잠금 안에서 DB를 다시 조회한다. `파일이 없는 첨부 행 N건` 경고는 **반대 방향 불일치**(행은 있고 파일이 없음)를 알리는 진단이며 청소 잡이 고치지 않는다 |
| `Proxy:TrustedIp` | (빈 값 — 비개발 환경에서 필수) | 신뢰하는 프록시(Caddy)의 **단일 IP**. CIDR이 아니다 |

## 접근·세션

| 키 | 기본값 | 의미 |
|---|---|---|
| `Admin:AllowedCidrs` | (빈 값 — 비개발 환경에서 필수) | 관리 표면 허용 CIDR, 공백으로 구분. Caddy와 앱이 같은 값을 읽는다 |
| `Admin:PasswordHash` | (빈 값 — 비개발 환경에서 필수) | 관리자 비밀번호의 PBKDF2 해시(base64). 비밀번호 자체가 아니다. `dotnet run --project PortfolioBlog.Api -- hash-password`로 만든다 |
| `Admin:SessionHours` | `12` | 세션 절대 수명(시간). sliding 연장 없음 |
| `Admin:LoginPerIpPerMinute` | `5` | 분당 IP별 로그인 시도 |
| `Admin:LoginGlobalPerMinute` | `20` | 분당 전체 로그인 시도 |
| `Admin:LoginConcurrency` | `2` | 동시 해시 검증 수(PBKDF2는 CPU 바운드) |
| `Admin:PreviewPerMinute` | `60` | `/api/preview`의 분당 전역 한도 |
| `Admin:PreviewConcurrency` | `2` | 미리보기 동시 실행 한도 |
| `Admin:UploadPerMinute` | `30` | 첨부 업로드의 분당 전역 한도 |
| `Admin:UploadConcurrency` | `2` | 첨부 업로드의 동시 실행 한도 |

## 공개 표면

| 키 | 기본값 | 의미 |
|---|---|---|
| `Public:PagePerIpPerMinute` | `120` | 공개 페이지·Atom·sitemap의 IP별 분당 한도 |
| `Public:AssetPerIpPerMinute` | `600` | 첨부 GET·`/health`·`robots.txt`·`highlight.css`의 IP별 분당 한도 |
| `Public:SearchPerIpPerMinute` | `20` | `/search`의 IP별 분당 한도 |
| `Public:SearchConcurrency` | `4` | 검색의 전역 동시 실행 한도 |
| `Public:StatementTimeoutMs` | `3000` | 공개 조회 연결이 열릴 때마다 세션에 거는 `max_execution_time`(밀리초, SELECT 전용). 100~60000 밖이면 시작 실패. 메타데이터 잠금(MDL) 대기 상한 `lock_wait_timeout`은 이 값을 초 단위로 올림한 값이다 |

## 렌더링

| 키 | 기본값 | 의미 |
|---|---|---|
| `Rendering:Concurrency` | `2` | 프로세스 전체 동시 렌더 수(미리보기·저장·공개 페이지 합산). 1~64 |
| `Rendering:QueueTimeoutMs` | `5000` | 렌더 슬롯 대기 상한(밀리초). 넘으면 503 + `Retry-After: 5` |
| `Rendering:CacheMegabytes` | `64` | 렌더 결과 캐시의 메모리 상한(MB). 1~1024 |

## 테스트 전용

| 키 | 의미 |
|---|---|
| `Test:Environment` | 통합 테스트가 호스팅 환경 이름을 덮어쓸 때 쓴다(`Production`·`Staging` 경로 검증) |
| `UPDATE_PREVIEW_SNAPSHOTS`(환경변수) | 미리보기 CSS 스냅숏 갱신 모드 → [개발 환경](development.md) |
