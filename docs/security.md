# 보안 설계

선택이 갈릴 때마다 편의보다 공격 표면 축소를 택했습니다. 이 문서는 **무엇을 왜 그렇게 정했고, 무엇을 아직 못 막는지**를 적습니다.

관련 문서: [아키텍처](architecture.md) · [테스트](testing.md) · [설정 키](configuration.md) · [전체 스펙](../plan/tech_blog_0920.md)

## 큰 결정과 근거

| 결정 | 이유 |
|---|---|
| 공개 페이지는 서버 렌더링, JS 없음 | CSP를 `default-src 'none'`까지 조일 수 있어 저장형 XSS가 들어와도 실행되지 않습니다. npm 공급망 사고가 나도 방문자는 영향을 받지 않습니다 |
| 관리 화면은 `admin.<도메인>`으로 분리 | 경로 분리(`/admin`)는 origin 격리가 아닙니다. 서브도메인으로 나누면 세션 쿠키가 공개 호스트로 전송되지 않고, 공개 도메인에는 `/api` 자체가 존재하지 않습니다 |
| 쓰기 = 허용 IP **AND** 비밀번호 세션 | 네트워크 위치 단일 요소에 의존하지 않습니다. 로그인 엔드포인트가 허용 IP에서만 열리므로 비밀번호 대입 표면이 없습니다 |
| 접근 검사는 요청 본문을 읽기 전에 완료 | 미인증 요청이 10MB 업로드 본문을 서버에 버퍼링시키지 못합니다 |
| 마크다운은 3겹으로 정제 | raw HTML 비활성 + URL 정책 → 최종 HTML 허용 목록 정제 → CSP |
| HTML을 저장하지 않고 요청마다 렌더링 | 렌더러 보안 수정이 과거 글 전체에 즉시 적용됩니다 |
| 잘못된 설정은 시작 실패 | 조용히 약해지는 설정(빈 신뢰 프록시, 같은 두 origin, http origin, 상대 경로 저장소)은 기동을 막습니다 |
| 세션 폐기 | 절대 수명 12시간. 비밀번호를 바꾸면 기존 세션이 자동 폐기되고, 로그아웃은 모든 세션을 폐기합니다 |

설계 초안은 OpenAI Codex CLI로 교차 검토했고(지적 23건), 수용한 항목과 의견이 갈린 4건의 근거를 [스펙 2.6절](../plan/tech_blog_0920.md)에 남겼습니다.

## 접근 계약

| 대상 | 호스트 | IP | CSRF 헤더 + Origin | 세션 |
|---|---|---|---|---|
| 관리 SPA 정적 파일 | admin | 필수(Caddy) | – | 불필요 |
| `POST /api/auth/login` | admin | 필수 | 필수 | 불필요, 속도 제한 |
| `GET /api/auth/me` | admin | 필수 | 헤더 필수 | 불필요(미로그인이면 `{authenticated:false}`) |
| 나머지 `/api/*`(GET 포함) | admin | 필수 | 헤더 필수, 변경 요청은 Origin도 | 필수 |
| 공개 페이지·첨부 GET | 공개(첨부는 양쪽) | – | – | – |

CSRF 방어는 **커스텀 헤더 + Origin 검사 + `SameSite=Strict` 쿠키** 세 겹입니다. 토큰을 쓰지 않는 이유는 관리 표면에 폼 제출이 없고(전부 `fetch`), 커스텀 헤더는 교차 출처에서 프리플라이트 없이 붙일 수 없기 때문입니다. CORS는 등록하지 않습니다.

## 응답 헤더

| 대상 | CSP |
|---|---|
| 공개 HTML | `default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'` |
| 관리 SPA | `default-src 'none'; script-src 'self'; style-src-elem 'self' 'unsafe-inline'; style-src-attr 'none'; img-src 'self'; connect-src 'self'; font-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'` |
| 관리 API | 공개 HTML과 같은 값 + `Cache-Control: no-store` |
| 첨부 | `default-src 'none'; sandbox` |
| 미리보기 iframe | `sandbox=""`(토큰 없음) + `srcdoc` 안에 `<meta http-equiv>` CSP(출처를 명시) |

모든 행에 `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`(전부 비활성), HSTS(Development 제외)가 붙고 `Server` 헤더는 나가지 않습니다.

- 관리 SPA CSP의 정본은 [`PortfolioBlog.Web/admin-headers.ts`](../PortfolioBlog.Web/admin-headers.ts)입니다. `vite preview`(E2E)가 그 값을 그대로 쓰고, 배포 Caddyfile이 같은 값을 옮깁니다. **HSTS는 그 파일에 의도적으로 없습니다** — 루프백 미리보기 서버에서도 같은 헤더가 나가는데 `localhost`에 HSTS를 걸면 개발자 브라우저 프로필의 루프백 전체가 HTTPS로 고정되기 때문입니다. 운영에서는 Caddy가 따로 더합니다.
- `style-src`를 요소/속성으로 나눠 CodeMirror가 주입하는 `<style>` 요소에만 `'unsafe-inline'`을 주고 style 속성은 막습니다.
- 미리보기 CSP에 `'self'`를 쓰지 않는 이유: Firefox는 `about:srcdoc` 문서의 `'self'`를 부모 출처로 보지 않아 스타일시트·이미지를 전부 막습니다(실측). 출처를 명시하면 Chromium·Firefox 둘 다 로드합니다.
- 본문이 없는 응답에는 헤더도 없습니다(호스트 필터 400, Kestrel이 직접 거부하는 414·400). 해석될 내용이 없으므로 의도한 상태입니다.

## 자원 제한

| 대상 | 제한 |
|---|---|
| 공개 페이지 전역 | IP별 120회/분(Atom·sitemap 포함) |
| 첨부 GET·`/health`·`robots.txt`·`highlight.css` | IP별 600회/분 |
| `/search` | IP별 20회/분, 동시 4, `q` 2~100자, `page` 상한 50 |
| `/api/preview` | 전역 60회/분, 동시 2, 본문 200KB |
| 로그인 | IP별 5회/분 + 전역 20회/분 + 해시 검증 동시 2. **영구 잠금 없음**(작성자 서비스 거부 방지) |
| 업로드 | 전역 30회/분 + 동시 2. 앱 10MB / 프레임워크 11MB(multipart 프레이밍 여유 1MB) |
| 렌더링 | 프로세스 전역 동시 2, 슬롯 대기 5초 초과 시 503. 공개 글은 `(PostId, xmin)` 캐시(64MB) + 단일 비행 |
| DB | 공개 조회는 별도 연결(`statement_timeout` 3초 + `default_transaction_read_only`) |
| JSON 본문 | 관리 API 256KB(직렬화 후 바이트 기준) |
| 과부하 응답 | 시간 초과·잠금 대기·렌더 슬롯 초과는 503 + `Retry-After: 5` |

속도 제한기 체인은 **동시 실행 제한기가 고정 창보다 앞**입니다 — 동시 실행 거부가 분당 허용량을 소모하지 않고 `Retry-After`가 5초입니다(고정 창 거부는 최대 60초).

## 첨부 처리

업로드는 파일 시그니처로 PNG·JPEG·GIF·WebP만 허용합니다(SVG 불가). 클라이언트가 보낸 파일 이름과 Content-Type은 믿지 않고, 확장자·Content-Type을 시그니처에서 유도합니다.

메타데이터 제거기는 **이미지를 디코딩하지 않습니다**. 스트림을 한 번만 읽으며 컨테이너 구조(세그먼트·청크)만 따라가는 **기본 거부** 파서로, 허용한 블록만 남기고 그 모양(크기·서브블록 구조)까지 검사하며 파일 끝에 덧붙인 데이터는 버립니다.

- JPEG: 구조 마커는 보존, `APPn`·`COM`은 원칙적으로 폐기(APP0 `JFIF`·APP2 `ICC_PROFILE`·APP14 `Adobe`만 식별자 확인 후 유지), EOI 뒤 바이트 폐기
- PNG: 청크 허용 목록 + 고정/상한 크기표, `IHDR`이 처음이자 한 번, `IDAT` 최소 1개, `IEND` 뒤 폐기
- WebP: 청크 허용 목록, `VP8X`는 정확히 10바이트일 때만 받아 EXIF·XMP 플래그 제거, RIFF 크기 재작성
- GIF: 그래픽 제어 확장과 NETSCAPE2.0 반복 횟수만 재구성해 유지, 나머지 확장·트레일러 뒤 폐기

SHA-256은 **제거 후** 바이트 기준이며 그 값이 곧 저장 경로(`{sha[..2]}/{sha}.{ext}`)입니다. 저장 루트는 정적 파일 루트 밖이고 경로는 서버 생성 값만 씁니다. 삽입·삭제·청소는 sha256 단위 advisory lock으로 직렬화하고, 청소 잡이 6시간마다 1시간 넘은 임시 파일과 참조 없는 파일을 지웁니다.

디코더 없이는 닫을 수 없는 잔여 표면(ICC 프로파일 본문, WebP ANMF 프레임 페이로드, JPEG DQT/DHT/SOF 페이로드, PNG CRC 미검증, GIF LZW 체인)은 10MB 상한·시그니처 기반 Content-Type·`nosniff`·`default-src 'none'; sandbox`로 완화합니다 — 브라우저에서 실행될 수 없습니다.

## 관리 SPA의 경계

- `fetch`는 `src/api/client.ts` **한 곳**에만 있습니다. CSRF 헤더·`credentials: 'same-origin'`·`redirect: 'error'`·`cache: 'no-store'`를 붙이고, 경로가 `/api/`로 시작하지 않거나 `//`·`\`·`..`·`%2e`·제어 문자를 담으면 호출 자체를 던집니다.
- 서버가 만든 HTML이 들어가는 곳은 `PreviewPane`의 `sandbox=""` iframe **하나**뿐입니다. React DOM에 서버 HTML을 넣지 않습니다.
- `localStorage`는 `src/lib/drafts.ts` 한 곳에서만 씁니다(임시본).
- 로그인 뒤 `?next=`는 URL 정규화 **이후**에 다시 검사합니다(오픈 리다이렉트 방지). 비밀번호는 mutation 변수로 넘기지 않고, 로그아웃 시 캐시를 비웁니다.
- 위 규칙은 문서가 아니라 **소스 가드 테스트**가 강제합니다: TypeScript 파서로 주석을 지운 소스 전체에서 금지 패턴(HTML 싱크, `iframe`, `allow-*` 토큰, `fetch`, 저장소 API, 외부 URL, `target=_blank`)을 검사합니다.

## 수용한 잔여 위험

| 위험 | 근거 | 되돌릴 조건 |
|---|---|---|
| `default_transaction_read_only`는 세션이 스스로 끌 수 있다 | 앱은 SQL을 입력으로 조립하지 않고(전부 매개변수화), 심층 방어일 뿐 | 4단계의 쓰기 권한 없는 DB 롤이 진짜 경계가 된다 → [배포](deployment.md) |
| 관리 SPA CSP의 `style-src-elem 'unsafe-inline'` | CodeMirror가 `<style>` 요소를 주입한다. 정적 서빙이라 nonce 불가. `style-src-attr 'none'`·`script-src 'self'`는 유지 | CodeMirror가 constructable stylesheet로 바뀔 때 |
| 소스 가드는 정규식 패턴 검사다 | 목적은 실수 방지. 2차 방어는 CSP와 코드 리뷰 | — |
| 첨부 삭제가 캐시 사본을 회수하지 못한다 | 응답이 `immutable` 1년. 삭제가 보장하는 것은 오리진이 더는 내주지 않는다는 것뿐 | — |
| 렌더 게이트를 공개·관리가 공유한다 | 공개 렌더는 (글 수 × 버전)으로 유계, 단일 비행 + 24시간 캐시 | 작성자가 실제로 503을 보면 관리 전용 슬롯 분리 |
| 업로드 이미지의 비검사 표면 5종 | 위 "첨부 처리" 참조 | 디코딩 기반 정규화를 도입할 때 |
| WebKit(Safari) 미검증 | 작성자 1명의 브라우저에 달림 | Playwright 프로젝트 추가 |

## 확장 후보(아직 하지 않은 것)

TOTP 2단계, 기기별 세션 관리, 비밀번호 변경 UI(현재는 해시 교체 후 재배포), 초안/예약 발행, slug 변경 + 리다이렉트, 전문 검색(`tsvector`), 마이그레이션 전용 DB 롤 분리, 앞단 CDN(`trusted_proxies` 재설계 필요).
