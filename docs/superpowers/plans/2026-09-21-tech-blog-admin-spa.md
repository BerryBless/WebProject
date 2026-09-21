# 기술 블로그 3단계 — 관리 에디터 SPA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 작성자가 브라우저에서 글·시리즈·태그·첨부를 관리하는 SPA(`PortfolioBlog.Web`)를 만든다. 백엔드는 바꾸지 않는다(테스트 1개 추가 제외).

**Architecture:** React 19 + TypeScript + Vite SPA가 `admin.<도메인>`에서 정적 파일로 서빙되고 같은 출처의 `/api/*`만 호출한다. 서버 상태는 TanStack Query, 화면 전환은 react-router의 데이터 라우터, 본문 편집은 CodeMirror 6(편집 화면만 지연 로딩)이다. 서버가 정제한 HTML은 `sandbox=""` iframe의 `srcdoc`에만 넣는다 — React DOM에는 넣지 않는다. 권한 판정은 전부 서버가 한다(화면의 가드는 편의일 뿐이다).

**Tech Stack:** Node 24(react-router 8이 22.22 이상 요구), React 19.2, react-router 8.4, TanStack Query 5, CodeMirror 6, Tailwind CSS 4, Vite 8, TypeScript 6, Vitest 5 + Testing Library + jsdom, Playwright 1.63(Chromium·Firefox), oxlint. 정확한 버전은 Task 1의 `package.json`(전부 고정 버전).

**Spec:** `plan/tech_blog_0920.md` — 3.3(접근 계약), 3.4(관리 API), 3.6(응답 헤더), 3.7(자원 제한), 3.8(첨부), 3.9(관리 SPA). 입력: `plan/tech_blog_2b_report_0921.md` 8절, `plan/resume_guide_0921.md` 3절("백엔드가 SPA에 요구하는 것").

**브랜치:** `feature/blog-admin-spa`

> **이 계획의 코드는 작성 중에 실제로 조립해 돌려 본 것이다(2026-09-21, 저장소 밖 임시 프로젝트).** `tsc -b` 오류 0, `oxlint` 경고 0, Vitest 110개 통과, Playwright 6개(Chromium 3 + Firefox 3)가 **실제 백엔드 + PostgreSQL + production 빌드 + 배포용 CSP** 아래에서 2회 연속 통과, .NET 스냅숏 테스트는 통과와 사보타주 실패를 모두 확인했다. 그 과정에서 계획 초안의 결함 6건을 테스트가 잡았다(끝의 "작성 중에 잡은 결함"). **그래도 계획은 틀릴 수 있다** — 2A·2B에서 계획 결함은 매번 "측정하지 않고 쓴 문장"에서 나왔다. 구현자는 단계마다 실패를 직접 보고, 리뷰어는 직접 공격하고 측정한다.

## Global Constraints

- **보안이 최우선.** 선택지가 갈리면 더 엄격한 쪽.
- **서버 HTML은 `PreviewPane`의 `sandbox=""` iframe `srcDoc`에만.** `dangerouslySetInnerHTML`·`innerHTML`·`insertAdjacentHTML`·`DOMParser`·`document.write` 금지. iframe의 `sandbox`에 토큰을 추가하지 않는다. Task 5의 소스 가드 테스트가 닫힌 세계로 검사한다.
- **`fetch`는 `src/api/client.ts` 한 곳에서만.** 모든 요청에 `X-Requested-With: XMLHttpRequest`, `credentials: 'same-origin'`, `redirect: 'error'`, `cache: 'no-store'`. 경로는 `/api/`로 시작하는 같은 출처 경로만.
- **인증 정보를 브라우저 저장소에 두지 않는다.** 세션은 서버의 `__Host-AdminSession`(HttpOnly) 쿠키뿐이다. `localStorage`는 `src/lib/drafts.ts`(임시본)만 쓴다. 비밀번호는 제출 직후 React 상태에서 지운다.
- **외부 출처 금지.** CDN·외부 글꼴·분석 스크립트·외부 이미지 URL을 코드에 넣지 않는다(CSP `default-src 'none'`). 새 창 링크(`target="_blank"`) 금지.
- **서버가 준 문자열은 JSX 텍스트로만** 그린다(React가 이스케이프한다). 오류 메시지도 마찬가지다.
- **의존성:** `package.json`은 전부 고정 버전(`^`·`~` 없음), `package-lock.json` 커밋, 설치는 `npm ci`. 이 계획에 없는 패키지를 추가하지 않는다. CI는 `npm audit --omit=dev --audit-level=high`를 게이트로 둔다.
- **백엔드 변경 금지.** 예외는 Task 5의 `PreviewCssSnapshotTests.cs`(테스트 1개) 하나다. 백엔드 동작이 계획과 다르면 멈추고 보고한다.
- **NUL 표기:** 6글자 유니코드 이스케이프를 코드·주석·문서·보고서·셸 어디에도 쓰지 않는다(실제 0x00 바이트로 바뀌어 파일에 들어간 전례 2회). TypeScript에서는 `String.fromCharCode(0)`과 코드 값 비교(`charCodeAt(i) < 0x20`)를 쓴다.
- **비밀값을 커밋하지 않는다.** `.certs/`(개발 인증서 개인 키)와 `.e2e/`(실행마다 만드는 버려질 비밀번호·해시)는 `.gitignore` 대상이다. 실제 도메인·IP·비밀번호·해시를 코드·문서에 쓰지 않는다. Stop 훅의 비밀값 스캐너는 한 줄짜리 연결 문자열 리터럴을 막는다 — 연결 문자열은 조각으로 조립한다.
- **절대 경로 금지**(`E:\project\...`). 스크립트는 자기 위치 기준으로 경로를 계산한다.
- **주석:** TypeScript는 export된 함수·컴포넌트에 **왜 그렇게 했는지**(보안 근거·서버 계약·실측 사실)를 TSDoc/줄 주석으로 적는다. 무엇을 하는지만 되풀이하는 주석은 쓰지 않는다. 내부 동작에 대한 주장은 측정했거나 `(추론)`·`(미검증)` 라벨을 붙인다(규칙 7). C# 파일(Task 5의 테스트)은 `CLAUDE.md`의 XML 문서 주석 규칙을 따른다 — 테스트 클래스는 `<summary>` + 3항목 `<remarks>`, 테스트 메서드는 `<summary>`만. 제품 코드·테스트에 프로세스 표현("리뷰", "라운드", "Task N", "브리프")을 쓰지 않는다.
- **테스트는 실패할 수 있어야 한다(규칙 8).** 새 테스트는 구현 전에 실패를 보고, 보안 단언은 제품 코드를 잠깐 되돌려 실패를 확인한 뒤 복구한다. 확인 결과를 보고서에 적는다.
- **줄 끝:** 새 `.cs`는 CRLF. `PortfolioBlog.Web` 아래 파일은 한 파일 안에서 줄 끝을 섞지 않는다(저장소는 `autocrlf=true`).
- **커밋:** `{접두사}: {제목}`(접두사 `추가|수정|버그수정|리팩토링|문서|테스트|의존성`), 제목 50자 이내·WHY 중심·파일명 나열 금지, 마지막 줄은 정확히 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. 실행 중에는 `.git/harness_commit_in_progress` 센티널을 유지한다(Stop 훅의 중간 상태 커밋 방지).
- **UI 문구는 한국어.** i18n 라이브러리를 쓰지 않는다.

## 스파이크 — 계획 전에 실측한 가정 (2026-09-21, Windows 11, Node 24.16.0, npm 11.13.0)

버려질 Vite 프로젝트와 실제 `PortfolioBlog.Api`(Release, 스크래치 `postgres:17-alpine`)로 측정했다. 브라우저 측정은 Playwright 1.63의 Chromium·Firefox.

| # | 가정 | 실측 | 계획에 미친 영향 |
|---|---|---|---|
| S1 | 스펙 3.9의 라이브러리 목록이 지금도 최신이다 | `npm create vite@latest`(react-ts) + `npm install` 결과: react 19.2.8, **react-router 8.4.0**(스펙은 7), @tanstack/react-query 5.103.2, **vite 8.3.0**, **typescript 6.0.2**, tailwindcss 4.3.3, codemirror 6.0.2, **vitest 5.0.1**, jsdom 30.1.0, @playwright/test 1.63.0. 템플릿의 린터는 ESLint가 아니라 **oxlint** | D1·D2. react-router 8은 Node ≥ 22.22.0을 요구한다 |
| S2 | react-router 8에서도 데이터 라우터 API가 같다 | `createBrowserRouter`·`createMemoryRouter`·`RouterProvider`·`Navigate`·`Outlet`·`NavLink`·`useNavigate`·`useParams`·`useSearchParams`가 전부 `react-router`에서 export되고, `route.lazy`(모듈의 `Component` export)가 동작한다 | 스펙의 "react-router 7"을 8로 올린다 |
| S3 | production 빌드에 인라인 스크립트·스타일이 없다 | `dist/index.html`은 외부 모듈 스크립트 1개 + 스타일시트 링크 1개뿐. 전부 한 덩어리면 JS 953KB(gzip 316KB), 편집 화면을 `route.lazy`로 떼면 **본체 371KB(gzip 116KB) + 편집 화면 618KB(gzip 212KB)** | D11. `script-src 'self'`로 충분 |
| S4 | Vite 프록시 뒤에서 세션 쿠키와 Origin 검사가 통과한다 | SPA를 **HTTPS**(`dotnet dev-certs https --export-path … --format Pem --no-password`로 내보낸 개발 인증서)로 띄우고 프록시를 `changeOrigin: false, secure: false`로 두고 백엔드를 `Site__AdminOrigin=<SPA 출처>`로 띄우면: 로그인 204, 브라우저에 `__Host-AdminSession`(Secure·HttpOnly·SameSite=Strict·Path=/) 저장, 이후 `POST`·`PUT`·`DELETE`가 Origin 검사를 통과한다. `Host: localhost:4173`은 호스트 필터를 통과한다(포트는 보지 않는다). `document.cookie`는 빈 문자열 | D3. 스펙 3.9의 "프록시 → `http://localhost:5055`"는 쓰지 않는다 — 쿠키가 `Secure`이고 이 PC의 AdGuard가 평문 HTTP를 변조한다 |
| S5 | 비 ASCII 파일 이름의 multipart 업로드 | `FormData`에 `한글 이름.png`로 넣으면 201, `url`은 퍼센트 인코딩(`/attachments/<id>/%ED%95%9C…`). 같은 내용을 다시 올리면 200(기존 첨부) | 업로드 응답의 `url`을 그대로 마크다운에 넣는다 |
| S6 | `JSON.stringify`는 한글을 이스케이프하지 않는다 | 한글 68,000자 본문의 요청 본문 = 204,137바이트 → 201(256KB 상한 안) | API 클라이언트는 `JSON.stringify`만 쓴다(테스트로 고정) |
| S7 | 스펙 3.6의 미리보기 CSP(`img-src 'self'; style-src 'self'`)가 `srcdoc` iframe에서 동작한다 | **Firefox에서 거짓.** Firefox는 `about:srcdoc` 문서의 `'self'`를 부모 출처로 보지 않아 스타일시트와 이미지를 **모두 차단**한다(`sandbox` 유무와 무관). Chromium은 허용한다. **출처를 명시**(`img-src https://host:port`)하면 둘 다 허용한다. 루트 상대 URL(`/attachments/…`)은 부모 문서 기준으로 해석된다 | D4. 스펙 3.6·3.9 수정(Task 8) |
| S8 | CodeMirror 때문에 `style-src 'unsafe-inline'`이 필요하다 | `style-src 'self'`만 두면 브라우저마다 위반이 **정확히 1건**(`style-src-elem`, `<style>` 요소 1개 주입). `style-src-elem 'self' 'unsafe-inline'; style-src-attr 'none'`으로 나누고 `default-src 'none'; connect-src 'self'; font-src 'self'; form-action 'none'`까지 좁혀도 **위반 0건**(로그인·편집기·업로드·미리보기·충돌·삭제·로그아웃 전 과정, 두 브라우저) | D5. 스펙 3.6의 관리 SPA CSP보다 좁힌다 |
| S9 | Vitest 기본 설정으로 충분하다 | (a) 기본 `include`가 Playwright의 `e2e/*.spec.ts`까지 집어 실패한다. (b) `globals`를 켜지 않으면 Testing Library의 자동 `cleanup`이 등록되지 않아 앞 테스트의 DOM이 남는다("Found multiple elements") | `vitest.config.ts`의 `include`와 `src/test/setup.ts` |
| S10 | CodeMirror를 jsdom에서 테스트할 수 있다 | 시도하지 않았다(jsdom에 레이아웃 API가 없다 — 추론). 단위 테스트는 `MarkdownEditor`를 `textarea`로 바꿔 끼우고, 진짜 편집기는 Playwright가 본다 | Task 6·8 |
| S11 | `hash-password` CLI를 스크립트에서 쓸 수 있다 | 표준 입력으로 비밀번호를 주면 출력의 마지막 줄이 해시(86자)다 | `scripts/e2e-prepare.mjs` |
| S12 | 로그아웃 뒤 `queryClient.clear()`로 캐시를 비우면 된다 | **거짓.** `clear()`는 쿼리 객체를 없애는데 `RequireAuth`의 관찰자는 옛 객체에 매달린 채 남아 로그인 화면으로 가지 않는다. `setQueryData` 후 `removeQueries`(auth 제외)로 해결 | `Layout.tsx` |
| S13 | 공개 사이트 CSS를 관리 출처에서 읽을 수 있다 | `/css/highlight.css`는 공개 호스트에만 매핑된다(2B). 운영에서 Caddy는 관리 호스트의 `/api/*`·`/attachments/*`만 백엔드로 넘긴다(스펙 3.10) | D6. SPA가 사본을 들고, .NET 테스트가 사본이 낡는 것을 막는다(통과 + 사보타주 실패 확인) |

**측정하지 않은 것:** WebKit(Safari), Linux에서 `dotnet dev-certs`의 PEM 내보내기(첫 CI가 게이트), `vite dev`의 HMR(측정은 전부 production 빌드 + `vite preview`), 200KB 본문에서 CodeMirror의 입력 지연, `blob:` 이미지(CSP에는 두었지만 지금 쓰는 곳이 없다).

## 설계 결정 (질문 없이 추천안으로 정한 것 — 승인할 때 뒤집을 수 있다)

| # | 결정 | 대안 | 이유 |
|---|---|---|---|
| D1 | react-router **8**(스펙은 7), Vite 8, TypeScript 6, Vitest 5 — 오늘의 최신 고정 버전 | 스펙대로 7에 고정 | 쓰는 API가 같고(S2) 새 프로젝트를 한 세대 뒤에서 시작할 이유가 없다. 틀리면 `package.json` 한 줄 |
| D2 | 린터는 템플릿 기본인 **oxlint**(`react/rules-of-hooks` error) + `tsc -b`(strict) | ESLint + typescript-eslint | 설정이 작고 빠르다. 보안 규칙은 린터가 아니라 소스 가드 테스트가 강제한다 |
| D3 | 개발·미리보기·E2E 모두 **HTTPS**(.NET 개발 인증서를 `.certs/`로 내보냄), 백엔드는 `Site__AdminOrigin=<SPA 출처>`로 실행 | 프록시가 `Origin` 헤더를 고쳐 쓴다 / 평문 HTTP | 헤더를 위조하지 않고 실제 Origin 검사를 그대로 통과한다(S4). 개발 편의를 위해 백엔드 검사를 약하게 만들지 않는다 |
| D4 | 미리보기 문서의 CSP는 **출처를 명시**한다(`window.location.origin`, 모양 검사 후 삽입). `base-uri 'none'; form-action 'none'` 추가 | 스펙대로 `'self'` | Firefox에서 미리보기가 통째로 깨진다(S7) |
| D5 | 관리 SPA의 CSP를 스펙보다 **좁힌다**(S8). 정본은 `PortfolioBlog.Web/admin-headers.ts` — 지금은 `vite preview`(E2E)가 쓰고 Plan 4의 Caddyfile이 같은 값을 옮긴다 | 스펙의 `default-src 'self'; style-src 'self' 'unsafe-inline'` | 실측으로 위반 0건. 값을 바꾸면 E2E가 두 브라우저에서 다시 증명해야 한다 |
| D6 | 미리보기용 CSS는 SPA의 정적 파일 **스냅숏**(`public/preview/site.css`·`highlight.css`) + .NET 드리프트 테스트 | 관리 호스트용 CSS 엔드포인트를 백엔드에 추가 | 백엔드·Caddy 라우팅을 건드리지 않는다. 사본이 낡으면 `dotnet test`가 실패한다 |
| D7 | 서버 상태는 TanStack Query, **자동 재시도 없음·창 포커스 재조회 없음**. 전역 상태 저장소·폼 라이브러리·UI 컴포넌트 라이브러리 없음 | Redux, react-hook-form, shadcn 등 | 429·503에는 `Retry-After`가 있고 4xx는 다시 보내도 같다. 편집 중 포커스 재조회는 입력을 덮어쓴다. 의존성이 적을수록 공급망 표면이 작다 |
| D8 | 401은 전역에서 "로그인 안 됨"으로 기록하고 `RequireAuth`가 `/login?next=`로 보낸다. `next`는 `safeNext`로 검증(같은 출처 절대 경로만) | 라우터 밖에서 `location` 변경 | 오픈 리다이렉트 방지. 임시본은 `localStorage`에 있어 로그인 뒤 복원된다 |
| D9 | 임시본: 글별 `localStorage` 키(`pb.draft.v1:<id 또는 new>`), 1초 디바운스 자동 저장, 다시 열면 **복원을 묻는다**(자동으로 덮어쓰지 않는다), 저장 성공 시 삭제. 읽을 때 모양 검사 | 자동 복원 / IndexedDB | 자동 복원은 서버본과 다른 내용을 말없이 띄운다. 저장 실패(용량)는 알리고 창 닫기 전에 묻는다 |
| D10 | 409(`version` 불일치): 최신 서버본을 받아 **내 본문과 나란히** 보여 주고 사용자가 고른다 — 자동 병합 없음 | 3-way 병합 | 스펙 3.9. 단일 작성자에게 병합 UI는 과하다 |
| D11 | 편집 화면만 `route.lazy`로 지연 로딩(S3) | 전부 한 덩어리 | 로그인·목록이 CodeMirror(618KB)를 기다리지 않는다 |
| D12 | 이미지 업로드는 **순차**, 업로드 전 편의 검사(크기 10MB·형식 4종). 미리보기는 429·503의 `Retry-After` 동안 요청을 멈추고 마지막 성공 결과를 남긴다 | 병렬 업로드 / 실패 시 미리보기 비움 | 서버 한도(업로드 전역 동시 2, 미리보기 전역 60회/분·동시 2)를 다른 탭과 나눠 쓴다 |
| D13 | 스펙에 없는 **`/tags` 화면** 추가(목록·삭제) | 시리즈 화면에 끼워 넣기 | `GET /api/tags`·`DELETE /api/tags/{id}`가 이미 있고, 안 쓰는 태그를 지울 곳이 필요하다 |
| D14 | 테스트 3층: Vitest 단위·컴포넌트(jsdom, `fetch` 표 스텁) → 소스 가드(닫힌 세계) → **Playwright E2E**(실제 백엔드 + PostgreSQL + production 빌드 + 배포용 CSP, Chromium·Firefox, "CSP 위반 0건" 단언). CI에 `web`·`web-e2e` 잡 추가 | E2E를 Plan 4로 미룸(스펙 5절은 web 잡을 4단계에 둔다) | `CLAUDE.md`: 프로젝트를 추가하면 CI를 함께 갱신한다. S7·S12와 E2E의 경쟁 조건은 실제 브라우저에서만 드러났다 |
| D15 | slug는 직접 입력(자동 생성 없음), 수정 화면에서는 읽기 전용 | 제목에서 자동 생성 | 제목이 한글이라 자동 생성이 의미 없고, slug는 서버에서 불변이다 |
| D16 | 삭제(글·시리즈·태그·첨부)는 `window.confirm` 한 번 + 결과를 문장으로 알린다 | 자체 모달 | 글에는 초안 상태가 없어 삭제가 즉시 공개 사이트에 반영된다. 의존성·코드 없이 실수 방지 |

## 파일 구조

```
PortfolioBlog.Web/
├─ package.json · package-lock.json      # 전부 고정 버전
├─ index.html                             # noindex, referrer same-origin, 인라인 스크립트 없음
├─ vite.config.ts                         # HTTPS(.certs/) · 프록시(/api, /attachments) · preview에 실제 보안 헤더
├─ vitest.config.ts                       # include는 src/**/*.test.* 만, setupFiles
├─ playwright.config.ts                   # webServer 2개: dotnet run(API) + build && preview
├─ admin-headers.ts                       # 관리 SPA 보안 헤더의 정본(CSP 포함)
├─ tsconfig.json · tsconfig.app.json · tsconfig.node.json · .oxlintrc.json · .gitignore
├─ scripts/e2e-prepare.mjs                # 개발 인증서 내보내기 · 스크래치 PostgreSQL · 버려질 비밀번호 해시
├─ public/preview/site.css · highlight.css  # 공개 사이트 CSS 스냅숏(드리프트는 .NET 테스트가 잡는다)
├─ e2e/admin.spec.ts
└─ src/
   ├─ main.tsx · App.tsx · index.css
   ├─ api/        types.ts · errors.ts · client.ts · endpoints.ts · client.test.ts
   ├─ lib/        safeNext.ts · validation.ts · drafts.ts · previewDoc.ts · markdownImage.ts · useDebounced.ts · lib.test.ts
   ├─ app/        queryClient.ts · routes.tsx
   ├─ auth/       RequireAuth.tsx · LoginPage.tsx
   ├─ components/ notices.tsx · Layout.tsx · PreviewPane.tsx · MarkdownEditor.tsx · TagInput.tsx · ConflictPanel.tsx
   ├─ pages/      PostsPage.tsx · SeriesPage.tsx · TagsPage.tsx · PostEditorPage.tsx · AttachmentsPage.tsx
   └─ test/       setup.ts · harness.tsx · auth.test.tsx · lists.test.tsx · preview.test.tsx · source-guards.test.ts · editor.test.tsx · attachments.test.tsx
PortfolioBlog.Api.Tests/Infrastructure/PreviewCssSnapshotTests.cs   # 유일한 백엔드 쪽 변경
.github/workflows/ci.yml                  # web · web-e2e 잡 추가
```

의존 방향: `pages` → `components`·`auth` → `app` → `api`·`lib`. `api`와 `lib`은 React를 모른다(`useDebounced.ts`만 예외). `pages`끼리는 서로 import하지 않는다.

---

모든 명령은 따로 적지 않으면 `PortfolioBlog.Web/`에서 실행한다. 각 Task의 마지막 단계는 `npm run lint && npm run typecheck && npm test`(경고 0·오류 0·전부 통과) 확인 후 커밋이다.

### Task 1: 프로젝트 골격·도구·보안 헤더 정본·CI

**Files:**
- Create: `PortfolioBlog.Web/{package.json, package-lock.json, index.html, vite.config.ts, vitest.config.ts, admin-headers.ts, tsconfig.json, tsconfig.app.json, tsconfig.node.json, .oxlintrc.json, .gitignore}`, `PortfolioBlog.Web/scripts/e2e-prepare.mjs`, `PortfolioBlog.Web/src/{main.tsx, App.tsx, index.css}`, `PortfolioBlog.Web/src/test/setup.ts`
- Modify: `.github/workflows/ci.yml`(`web` 잡), 루트 `.gitignore`(확인만 — `node_modules/`·`*.pem`·`*.key`는 이미 있다)

**Interfaces:**
- Produces: npm 스크립트 `dev`·`build`·`preview`·`lint`·`typecheck`·`test`·`certs`·`e2e:prepare`·`e2e`. `admin-headers.ts`의 `ADMIN_CSP: string`, `ADMIN_SECURITY_HEADERS: Record<string, string>`. 개발 서버 `https://localhost:5173`, 미리보기 서버 `https://localhost:4173`(실제 보안 헤더 포함).

- [ ] **Step 1: 폴더와 설정 파일을 만든다.** `npm create vite`를 쓰지 않는다 — 템플릿의 예제 자산(`src/assets`, `public/*.svg`, `App.css`, `README.md`)이 필요 없고 버전을 고정해야 한다. 아래 파일을 그대로 만든다.

**`PortfolioBlog.Web/package.json`**

````json
{
  "name": "portfolioblog-web",
  "private": true,
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "preview": "vite preview",
    "lint": "oxlint",
    "typecheck": "tsc -b",
    "test": "vitest run",
    "certs": "node scripts/e2e-prepare.mjs certs",
    "e2e:prepare": "node scripts/e2e-prepare.mjs",
    "e2e": "playwright test"
  },
  "dependencies": {
    "@codemirror/lang-markdown": "6.5.2",
    "@codemirror/state": "6.7.5",
    "@codemirror/view": "6.43.12",
    "@tanstack/react-query": "5.103.2",
    "codemirror": "6.0.2",
    "react": "19.2.8",
    "react-dom": "19.2.8",
    "react-router": "8.4.0"
  },
  "devDependencies": {
    "@playwright/test": "1.63.0",
    "@tailwindcss/vite": "4.3.3",
    "@testing-library/jest-dom": "7.0.1",
    "@testing-library/react": "16.3.3",
    "@testing-library/user-event": "14.6.7",
    "@types/node": "24.13.3",
    "@types/react": "19.2.18",
    "@types/react-dom": "19.2.7",
    "@vitejs/plugin-react": "6.1.1",
    "jsdom": "30.1.0",
    "oxlint": "1.81.0",
    "tailwindcss": "4.3.3",
    "typescript": "6.0.2",
    "vite": "8.3.0",
    "vitest": "5.0.1"
  }
}
````

**`PortfolioBlog.Web/.gitignore`**

````gitignore
node_modules/
dist/
*.local
*.log

# 개발 인증서의 개인 키(.NET 개발 인증서를 내보낸 것). 절대 커밋하지 않는다.
.certs/
# E2E 실행마다 새로 만드는 버려질 비밀번호·해시.
.e2e/
# Playwright 산출물
test-results/
playwright-report/
````

**`PortfolioBlog.Web/index.html`**

````html
<!doctype html>
<html lang="ko">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta name="robots" content="noindex, nofollow" />
    <meta name="referrer" content="same-origin" />
    <title>블로그 관리</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
````

**`PortfolioBlog.Web/tsconfig.json`**

````json
{
  "files": [],
  "references": [
    { "path": "./tsconfig.app.json" },
    { "path": "./tsconfig.node.json" }
  ]
}
````

**`PortfolioBlog.Web/tsconfig.app.json`**

````json
{
  "compilerOptions": {
    "tsBuildInfoFile": "./node_modules/.tmp/tsconfig.app.tsbuildinfo",
    "target": "es2023",
    "lib": ["ES2023", "DOM"],
    "module": "esnext",
    "types": ["vite/client", "node"],
    "allowArbitraryExtensions": true,
    "skipLibCheck": true,

    /* Bundler mode */
    "moduleResolution": "bundler",
    "allowImportingTsExtensions": true,
    "verbatimModuleSyntax": true,
    "moduleDetection": "force",
    "noEmit": true,
    "jsx": "react-jsx",

    /* Linting */
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "erasableSyntaxOnly": true,
    "noFallthroughCasesInSwitch": true
  },
  "include": ["src"]
}
````

**`PortfolioBlog.Web/tsconfig.node.json`**

````json
{
  "compilerOptions": {
    "tsBuildInfoFile": "./node_modules/.tmp/tsconfig.node.tsbuildinfo",
    "target": "es2023",
    "lib": ["ES2023", "DOM"],
    "types": ["node"],
    "skipLibCheck": true,

    /* Bundler mode */
    "module": "nodenext",
    "allowImportingTsExtensions": true,
    "verbatimModuleSyntax": true,
    "moduleDetection": "force",
    "noEmit": true,

    /* Linting */
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "erasableSyntaxOnly": true,
    "noFallthroughCasesInSwitch": true
  },
  "include": ["vite.config.ts", "vitest.config.ts", "playwright.config.ts", "admin-headers.ts", "e2e"]
}
````

**`PortfolioBlog.Web/.oxlintrc.json`**

````json
{
  "$schema": "./node_modules/oxlint/configuration_schema.json",
  "plugins": ["react", "typescript", "oxc"],
  "rules": {
    "react/rules-of-hooks": "error",
    "react/only-export-components": ["warn", { "allowConstantExport": true }]
  }
}
````

**`PortfolioBlog.Web/admin-headers.ts`**

````ts
// 관리 SPA 문서에 붙는 보안 헤더의 정본. 지금은 `vite preview`(E2E)가 쓰고, Plan 4의 Caddyfile이 같은 값을 관리 사이트 블록에 옮긴다.
// 값을 바꾸면 E2E의 "CSP 위반 0건" 검사를 Chromium·Firefox 양쪽에서 다시 통과시켜야 한다.
//
// 스펙 3.6보다 좁다(실측으로 좁힌 것):
// - default-src 'none' + 필요한 것만 나열(connect-src·font-src를 'self'로 명시).
// - style-src를 요소와 속성으로 나눴다. CodeMirror는 <style> 요소 하나를 주입하므로 style-src-elem에만 'unsafe-inline'을 둔다.
//   style 속성(style-src-attr)은 막는다 — React의 style prop과 CodeMirror의 치수 지정은 CSSOM으로 들어가 CSP 대상이 아니다.
// - form-action 'none': 이 SPA는 폼을 서버로 제출하지 않는다(전부 fetch).
export const ADMIN_CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src-elem 'self' 'unsafe-inline'",
  "style-src-attr 'none'",
  "img-src 'self' blob:",
  "connect-src 'self'",
  "font-src 'self'",
  "frame-src 'self'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join('; ')

export const ADMIN_SECURITY_HEADERS: Record<string, string> = {
  'Content-Security-Policy': ADMIN_CSP,
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
  'Referrer-Policy': 'strict-origin-when-cross-origin',
  // PortfolioBlog.Api의 SecurityHeadersMiddleware.PermissionsPolicy와 같은 값.
  'Permissions-Policy': 'accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()',
}
````

**`PortfolioBlog.Web/vite.config.ts`**

````ts
import { existsSync, readFileSync } from 'node:fs'
import { defineConfig, type ProxyOptions } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { ADMIN_SECURITY_HEADERS } from './admin-headers.ts'

// 백엔드(PortfolioBlog.Api)의 주소. 세션 쿠키가 Secure + __Host- 라서 SPA도 HTTPS로 띄운다(개발·미리보기 공통).
const API_ORIGIN = process.env.BLOG_API_ORIGIN ?? 'https://localhost:7198'
const CERT = '.certs/dev.pem'
const KEY = '.certs/dev.key'

// changeOrigin: false — Host 헤더를 SPA 출처 그대로 넘긴다. 백엔드의 호스트 필터는 포트를 뺀 호스트(localhost)만 보고,
// Origin 검사는 Site:AdminOrigin과 비교한다. 그래서 백엔드는 Site__AdminOrigin=<이 SPA의 출처>로 띄워야 한다(README).
// secure: false — 개발 인증서는 OS 저장소에서만 신뢰된다(Node는 OS 저장소를 보지 않는다). 루프백 전용 설정이다.
const proxy: Record<string, ProxyOptions> = {
  '/api': { target: API_ORIGIN, changeOrigin: false, secure: false },
  '/attachments': { target: API_ORIGIN, changeOrigin: false, secure: false },
}

function https() {
  if (!existsSync(CERT) || !existsSync(KEY)) throw new Error('개발 인증서가 없습니다. 먼저 `npm run certs`를 실행하세요(.NET 개발 인증서를 .certs/로 내보냅니다).')
  return { cert: readFileSync(CERT), key: readFileSync(KEY) }
}

export default defineConfig(({ command }) => ({
  base: '/',
  plugins: [react(), tailwindcss()],
  build: { sourcemap: false, chunkSizeWarningLimit: 700 },
  // 인증서는 서버를 띄울 때만 읽는다 — `vite build`(CI의 web 잡)는 인증서 없이 돌아야 한다.
  server: command === 'serve' ? { host: 'localhost', port: 5173, strictPort: true, https: https(), proxy } : undefined,
  // preview는 production 빌드를 실제 보안 헤더와 함께 내보낸다: E2E가 "배포될 CSP 아래에서 위반 0건"을 검사하는 자리다.
  preview: command === 'serve' ? { host: 'localhost', port: 4173, strictPort: true, https: https(), proxy, headers: ADMIN_SECURITY_HEADERS } : undefined,
}))
````

**`PortfolioBlog.Web/vitest.config.ts`**

````ts
import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    environment: 'jsdom',
    // e2e/*.spec.ts는 Playwright 몫이다. 기본 include는 그것까지 집어 실패한다(실측).
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['src/test/setup.ts'],
    restoreMocks: true,
  },
})
````

**`PortfolioBlog.Web/src/test/setup.ts`**

````ts
import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// vitest의 globals를 켜지 않았으므로 Testing Library의 자동 정리가 등록되지 않는다(실측: 앞 테스트의 DOM이 남아 "multiple elements").
afterEach(() => cleanup())
````

**`PortfolioBlog.Web/scripts/e2e-prepare.mjs`**

````js
// E2E 준비물을 만든다: (1) 개발 인증서(.certs/), (2) 버려질 PostgreSQL 컨테이너, (3) 버려질 관리자 비밀번호의 해시(.e2e/env.json).
// 저장소에는 비밀번호도 해시도 남기지 않는다 — 실행할 때마다 새로 만든다. `node scripts/e2e-prepare.mjs certs`는 (1)만 한다.
import { execFileSync, spawnSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const web = join(dirname(fileURLToPath(import.meta.url)), '..')
const api = process.env.BLOG_API_DIR ?? join(web, '..', 'PortfolioBlog.Api')
const only = process.argv[2]

function exportCerts() {
  const pem = join(web, '.certs', 'dev.pem')
  if (existsSync(pem) && existsSync(join(web, '.certs', 'dev.key'))) return
  mkdirSync(join(web, '.certs'), { recursive: true })
  // Windows·macOS에서는 이미 신뢰된 개발 인증서를 내보낸다. Linux CI에서는 신뢰되지 않은 인증서가 새로 만들어진다 —
  // Playwright(ignoreHTTPSErrors)와 Vite 프록시(secure: false)는 신뢰 여부를 보지 않으므로 그대로 쓴다.
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', pem, '--format', 'Pem', '--no-password'], { stdio: 'inherit' })
  if (!existsSync(join(web, '.certs', 'dev.key'))) throw new Error('dotnet dev-certs가 .certs/dev.key를 만들지 않았습니다.')
}

function ensurePostgres(port, password) {
  if (process.env.E2E_SKIP_DOCKER === '1') return // CI: 워크플로의 services.postgres를 쓴다
  const name = 'pb-e2e-pg'
  spawnSync('docker', ['rm', '-f', name], { stdio: 'ignore' }) // 앞 실행의 데이터로 시작하지 않는다
  execFileSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${password}`, '-e', 'POSTGRES_DB=blog_e2e',
    '-p', `127.0.0.1:${port}:5432`, 'postgres:17-alpine'], { stdio: 'inherit' })
  for (let i = 0; i < 60; i++) {
    if (spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres', '-d', 'blog_e2e'], { stdio: 'ignore' }).status === 0) return
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500)
  }
  throw new Error('PostgreSQL 컨테이너가 준비되지 않았습니다.')
}

exportCerts()
if (only !== 'certs') {
  const port = process.env.E2E_PG_PORT ?? '5433'
  const pgPassword = process.env.E2E_PG_PASSWORD ?? randomBytes(12).toString('hex')
  ensurePostgres(port, pgPassword)
  const adminPassword = randomBytes(18).toString('base64url')
  // hash-password는 표준 입력으로 비밀번호를 받고 마지막 줄에 해시를 쓴다(실측).
  const output = execFileSync('dotnet', ['run', '--project', api, '-c', 'Release', '--', 'hash-password'], { input: adminPassword + '\n', encoding: 'utf8' })
  const hash = output.trim().split(/\r?\n/).at(-1)
  if (!hash || hash.length < 40) throw new Error('hash-password의 출력을 해석하지 못했습니다.')
  mkdirSync(join(web, '.e2e'), { recursive: true })
  writeFileSync(join(web, '.e2e', 'env.json'), JSON.stringify({ port, pgPassword, adminPassword, hash }), { mode: 0o600 })
  console.log('e2e 준비 완료')
}
````

**`PortfolioBlog.Web/src/index.css`**

````css
@import "tailwindcss";
````

**`PortfolioBlog.Web/src/main.tsx`**

````tsx
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
````


- [ ] **Step 2: 자리 표시용 `src/App.tsx`**(Task 4에서 교체한다).

```tsx
export default function App() {
  return <main className="p-8 text-sm">블로그 관리</main>
}
```

- [ ] **Step 3: 설치·잠금 파일 생성.** `npm install` → `package-lock.json`이 생긴다. 그 뒤 `rm -rf node_modules && npm ci`가 성공하는지 확인한다(CI와 같은 경로). `npm audit --omit=dev --audit-level=high`가 0으로 끝나는지 확인하고, 아니면 멈추고 보고한다(버전을 임의로 바꾸지 않는다).

- [ ] **Step 4: 검증.** `npm run lint`(경고 0) · `npm run typecheck`(오류 0) · `npm test`(테스트 파일이 아직 없어 "No test files found"로 실패한다 — Task 2에서 생긴다. 이 단계에서는 `npx vitest run --passWithNoTests`로 설정 오류가 없는지만 본다) · `npm run build` → `dist/index.html`에 인라인 `<script>`·`<style>`·`style=`이 없는지 눈으로 확인한다. `npm run certs` → `.certs/dev.pem`·`.certs/dev.key`가 생기고 `git status`에 나타나지 않는지(무시되는지) 확인한다.

- [ ] **Step 5: CI `web` 잡.** `.github/workflows/ci.yml`의 `jobs:` 아래에 추가한다(기존 `test` 잡은 그대로 둔다).

```yaml
  web:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: PortfolioBlog.Web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '24'
          cache: npm
          cache-dependency-path: PortfolioBlog.Web/package-lock.json
      - run: npm ci
      - name: Audit production dependencies
        run: npm audit --omit=dev --audit-level=high
      - run: npm run lint
      - run: npm run typecheck
      - run: npm test
      - run: npm run build
```

- [ ] **Step 6: 커밋.** `추가: 관리 SPA 골격 — 고정 버전 의존성·HTTPS 개발 서버·보안 헤더 정본`

### Task 2: API 클라이언트와 오류 모델

**Files:**
- Create: `src/api/{types.ts, errors.ts, client.ts, endpoints.ts}`
- Test: `src/api/client.test.ts`

**Interfaces:**
- Consumes: 백엔드 계약(스펙 3.4, `PortfolioBlog.Api/Contracts/*.cs`). JSON은 camelCase, `version`은 number, 검증 실패는 `{ errors: { <camelCase 필드>: string[] } }`, 충돌·과부하는 ProblemDetails(`title`·`detail`) + 선택적 `Retry-After`(초).
- Produces:
  - `request<T>(method: 'GET'|'POST'|'PUT'|'DELETE', path: string, options?: { json?, form?, query?, signal? }): Promise<T>` — 실패는 `ApiError`를 던진다(취소는 `AbortError` 그대로).
  - `class ApiError { status; title; detail; fieldErrors: FieldErrors; retryAfterSeconds: number|null }`(status 0 = 네트워크 실패), `type FieldErrors = Record<string, string[]>`, `describeError(error: unknown): string`, `parseRetryAfter(value: string|null): number|null`.
  - `auth.{me,login,logout}`, `posts.{list,get,create,update,remove(id, version)}`, `series.{list,get,create,update,remove}`, `tags.{list,remove}`, `attachments.{list,upload(file, fileName),remove}`, `preview.render(markdown, signal?)`.
  - 타입: `AuthStatus`, `PostSummary`, `PostDetail`, `PagedPosts`, `UpsertPostRequest`, `Series`, `SeriesPost`, `SeriesDetail`, `UpsertSeriesRequest`, `Tag`, `Attachment`, `PagedAttachments`, `PreviewResponse`.

- [ ] **Step 1: 실패하는 테스트를 쓴다.**

**`PortfolioBlog.Web/src/api/client.test.ts`**

````ts
import { afterEach, describe, expect, it, vi } from 'vitest'
import { request } from './client'
import { ApiError, describeError, parseRetryAfter } from './errors'
import { posts, attachments } from './endpoints'

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/problem+json', ...headers } })

function mockFetch(response: Response | Error | DOMException) {
  const spy = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => {
    if (!(response instanceof Response)) throw response
    return response.clone()
  })
  vi.stubGlobal('fetch', spy)
  return spy
}

afterEach(() => vi.unstubAllGlobals())

/** 거부된 값을 돌려준다. 성공하면 테스트를 실패시킨다(거부를 기대한 자리에서 조용히 통과하지 않게). */
async function caught(promise: Promise<unknown>): Promise<ApiError> {
  try { await promise } catch (error) { return error as ApiError }
  throw new Error('요청이 실패해야 하는데 성공했다')
}

describe('request', () => {
  it('모든 요청에 CSRF 헤더·same-origin 쿠키·리다이렉트 거부·no-store를 붙인다', async () => {
    const spy = mockFetch(json(200, { authenticated: true }))
    await request('GET', '/api/auth/me')
    const init = spy.mock.calls[0][1]!
    expect((init.headers as Record<string, string>)['X-Requested-With']).toBe('XMLHttpRequest')
    expect(init.credentials).toBe('same-origin')
    expect(init.redirect).toBe('error')
    expect(init.cache).toBe('no-store')
  })

  it('JSON 본문은 비 ASCII를 이스케이프하지 않는다(서버의 256KB 상한은 직렬화 후 바이트 기준)', async () => {
    const spy = mockFetch(json(201, {}))
    await request('POST', '/api/posts', { json: { title: '한글' } })
    const init = spy.mock.calls[0][1]!
    expect(init.body).toBe('{"title":"한글"}')
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json')
  })

  it('multipart에는 Content-Type을 직접 붙이지 않는다(boundary는 브라우저 몫)', async () => {
    const spy = mockFetch(json(201, { id: 'a', url: '/attachments/a/x.png' }))
    await attachments.upload(new Blob(['x'], { type: 'image/png' }), 'x.png')
    const init = spy.mock.calls[0][1]!
    expect((init.headers as Record<string, string>)['Content-Type']).toBeUndefined()
    expect((init.body as FormData).get('file')).toBeInstanceOf(Blob)
  })

  it.each(['https://evil.test/api/x', '//evil.test/api/x', '/apix', '/api//x', '/api/\\x'])('API 경로가 아니면 호출하지 않는다: %s', async (path) => {
    const spy = mockFetch(json(200, {}))
    await expect(request('GET', path)).rejects.toThrow('API 경로가 아닙니다')
    expect(spy).not.toHaveBeenCalled()
  })

  it('빈 쿼리 값은 보내지 않고, 값은 인코딩한다', async () => {
    const spy = mockFetch(json(200, { items: [], total: 0 }))
    await posts.list('', 0, 50)
    expect(spy.mock.calls[0][0]).toBe('/api/posts?skip=0&take=50')
    await posts.list('a&b=c #', 50, 50)
    expect(spy.mock.calls[1][0]).toBe('/api/posts?q=a%26b%3Dc+%23&skip=50&take=50')
  })

  it('204는 undefined', async () => {
    mockFetch(new Response(null, { status: 204 }))
    await expect(request('POST', '/api/auth/logout')).resolves.toBeUndefined()
  })

  it('검증 실패(400)의 필드 오류와 Retry-After를 읽는다', async () => {
    mockFetch(json(400, { title: 'One or more validation errors occurred.', errors: { slug: ['slug는 필수입니다.'], bogus: 'x' } }))
    const error = await caught(request('POST', '/api/posts', { json: {} }))
    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(400)
    expect(error.fieldErrors).toEqual({ slug: ['slug는 필수입니다.'] })
  })

  it('ProblemDetails가 아닌 실패 본문(HTML·빈 본문)에서도 던지지 않고 상태 코드만으로 만든다', async () => {
    mockFetch(new Response('<html>nope</html>', { status: 404, statusText: 'Not Found' }))
    const a = await caught(request('GET', '/api/posts/x'))
    expect(a).toBeInstanceOf(ApiError); expect(a.status).toBe(404)
    mockFetch(new Response(null, { status: 401 }))
    const b = await caught(request('GET', '/api/posts/x'))
    expect(b.status).toBe(401)
  })

  it('네트워크 실패는 status 0, 취소(AbortError)는 그대로 던진다', async () => {
    mockFetch(new TypeError('Failed to fetch'))
    const a = await caught(request('GET', '/api/tags'))
    expect(a).toBeInstanceOf(ApiError); expect(a.status).toBe(0)
    mockFetch(new DOMException('aborted', 'AbortError'))
    const b = await caught(request('GET', '/api/tags'))
    expect(b).toBeInstanceOf(DOMException)
  })
})

describe('errors', () => {
  it.each([['5', 5], ['60', 60], ['0', 1], ['999999', 3600], [' 7 ', 7]])('Retry-After %s → %s', (raw, expected) => {
    expect(parseRetryAfter(raw)).toBe(expected)
  })
  it.each([null, '', '-1', '1.5', 'Wed, 21 Oct 2026 07:28:00 GMT', '1e3'])('해석할 수 없는 Retry-After %s → null', (raw) => {
    expect(parseRetryAfter(raw)).toBeNull()
  })
  it('429·503은 대기 시간을 안내한다', async () => {
    mockFetch(json(503, { title: '서버가 바쁩니다' }, { 'Retry-After': '5' }))
    const e = await caught(request('POST', '/api/preview', { json: { markdown: '' } }))
    expect(describeError(e)).toContain('5초')
    expect(describeError(new Error('x'))).toBe('알 수 없는 오류가 발생했습니다.')
  })
})
````


- [ ] **Step 2: 실패를 본다.** `npx vitest run src/api` → 모듈을 찾지 못해 실패.

- [ ] **Step 3: 구현한다.**

**`PortfolioBlog.Web/src/api/types.ts`**

````ts
// 서버 DTO(PortfolioBlog.Api/Contracts/*.cs)의 JSON 모양. ASP.NET Core 기본 직렬화라 속성은 camelCase,
// Guid·DateTimeOffset은 문자열, uint Version은 number(최대 4,294,967,295 — Number.MAX_SAFE_INTEGER 안)다.

export interface AuthStatus { authenticated: boolean }

export interface PostSummary {
  id: string; slug: string; title: string; summary: string; tags: string[]
  seriesId: string | null; seriesOrder: number | null
  createdAt: string; updatedAt: string; version: number
}
export interface PostDetail extends PostSummary { contentMarkdown: string }
export interface PagedPosts { items: PostSummary[]; total: number }

/** 생성·수정 공용 요청. 연결(tagNames·seriesId·seriesOrder)은 통째로 교체된다. version은 수정에서만 필수. */
export interface UpsertPostRequest {
  slug: string; title: string; summary: string; contentMarkdown: string
  tagNames: string[]; seriesId: string | null; seriesOrder: number | null; version?: number
}

export interface Series { id: string; slug: string; title: string; description: string; postCount: number }
export interface SeriesPost { id: string; slug: string; title: string; seriesOrder: number }
export interface SeriesDetail { series: Series; posts: SeriesPost[] }
export interface UpsertSeriesRequest { slug: string; title: string; description: string }

export interface Tag { id: string; name: string; normalizedName: string; postCount: number }

export interface Attachment {
  id: string; url: string; fileName: string; contentType: string; sizeBytes: number; sha256: string; createdAt: string
}
export interface PagedAttachments { items: Attachment[]; total: number }

export interface PreviewResponse { html: string }
````

**`PortfolioBlog.Web/src/api/errors.ts`**

````ts
/** 필드별 검증 오류. 키는 서버가 주는 camelCase 필드 이름이다(예: contentMarkdown, tagNames). */
export type FieldErrors = Record<string, string[]>

/**
 * 관리 API의 실패 1건. status 0은 네트워크 실패(응답을 받지 못함)다.
 * 서버가 준 문자열(title·detail·fieldErrors)은 화면에 **텍스트로만** 넣는다 — HTML로 해석하지 않는다.
 */
export class ApiError extends Error {
  readonly status: number
  readonly title: string
  readonly detail: string | null
  readonly fieldErrors: FieldErrors
  /** Retry-After(초). 헤더가 없거나 해석할 수 없으면 null. */
  readonly retryAfterSeconds: number | null

  constructor(status: number, title: string, detail: string | null = null, fieldErrors: FieldErrors = {}, retryAfterSeconds: number | null = null) {
    super(`${status} ${title}`)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.detail = detail
    this.fieldErrors = fieldErrors
    this.retryAfterSeconds = retryAfterSeconds
  }
}

const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)

/** Retry-After 헤더(초 단위 정수만 — 서버는 HTTP-date 형식을 쓰지 않는다)를 1~3600초로 읽는다. */
export function parseRetryAfter(value: string | null): number | null {
  if (value === null || !/^\d{1,6}$/.test(value.trim())) return null
  return Math.min(3600, Math.max(1, Number(value.trim())))
}

function parseFieldErrors(raw: unknown): FieldErrors {
  if (!isRecord(raw)) return {}
  const out: FieldErrors = {}
  for (const [field, messages] of Object.entries(raw)) {
    if (Array.isArray(messages)) out[field] = messages.filter((m): m is string => typeof m === 'string')
  }
  return out
}

/**
 * 실패 응답을 ApiError로 바꾼다. 본문이 ProblemDetails가 아닐 수 있다(Caddy 계층의 404, 프레임워크가 직접 낸 413,
 * 본문 없는 401) — 그때는 상태 코드만으로 만든다. 본문 파싱 실패로 예외를 던지지 않는다.
 */
export async function toApiError(res: Response): Promise<ApiError> {
  const retryAfter = parseRetryAfter(res.headers.get('Retry-After'))
  let body: unknown = null
  try {
    const text = await res.text()
    if (text.length > 0 && text.length <= 65_536) body = JSON.parse(text)
  } catch { /* ProblemDetails가 아닌 본문 */ }
  if (!isRecord(body)) return new ApiError(res.status, res.statusText || '요청 실패', null, {}, retryAfter)
  const title = typeof body.title === 'string' ? body.title : (res.statusText || '요청 실패')
  const detail = typeof body.detail === 'string' ? body.detail : null
  return new ApiError(res.status, title, detail, parseFieldErrors(body.errors), retryAfter)
}

/** 사용자에게 보일 한 줄 설명. 서버 detail이 있으면 그것을 우선한다(서버 메시지는 이미 한국어다). */
export function describeError(error: unknown): string {
  if (!(error instanceof ApiError)) return '알 수 없는 오류가 발생했습니다.'
  const wait = error.retryAfterSeconds === null ? '' : ` ${error.retryAfterSeconds}초 뒤에 다시 시도하세요.`
  switch (error.status) {
    case 0: return '서버에 연결할 수 없습니다. 네트워크를 확인하세요.'
    case 400: return error.detail ?? '입력값을 확인하세요.'
    case 401: return '로그인이 필요합니다.'
    case 403: return error.detail ?? '이 네트워크 또는 출처에서는 관리 기능을 쓸 수 없습니다.'
    case 404: return '대상을 찾을 수 없습니다. 이미 삭제되었을 수 있습니다.'
    case 409: return error.detail ?? '다른 곳에서 먼저 변경되었습니다. 다시 불러온 뒤 시도하세요.'
    case 413: return error.detail ?? '요청이 너무 큽니다.'
    case 415: return error.detail ?? '지원하지 않는 형식입니다.'
    case 429: return `요청이 너무 잦습니다.${wait}`
    case 503: return `서버가 잠시 바쁩니다.${wait}`
    default: return error.detail ?? `요청이 실패했습니다(${error.status}).`
  }
}
````

**`PortfolioBlog.Web/src/api/client.ts`**

````ts
import { ApiError, toApiError } from './errors'

type Query = Record<string, string | number | null | undefined>

export interface RequestOptions {
  /** JSON 본문. JSON.stringify는 비 ASCII 문자를 이스케이프하지 않는다 — 서버의 256KB 상한은 직렬화 후 바이트 기준이다. */
  json?: unknown
  /** multipart 본문. Content-Type(boundary 포함)은 브라우저가 붙이므로 직접 지정하지 않는다. */
  form?: FormData
  query?: Query
  signal?: AbortSignal
}

/** 서버의 AdminSurfaceMiddleware가 모든 /api 요청에 요구하는 CSRF 헤더. */
export const CSRF_HEADER = 'X-Requested-With'
export const CSRF_VALUE = 'XMLHttpRequest'

function buildUrl(path: string, query?: Query): string {
  // 같은 출처의 /api 경로만 허용한다: 절대 URL·프로토콜 상대 URL(//host)로 쿠키 없는 교차 출처 호출이 섞여 들어오는 실수를 막는다.
  if (!path.startsWith('/api/') || path.includes('//') || path.includes('\\')) throw new Error(`API 경로가 아닙니다: ${path}`)
  if (!query) return path
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') params.set(key, String(value))
  }
  const qs = params.toString()
  return qs ? `${path}?${qs}` : path
}

/**
 * 관리 API 호출의 단일 통로. 성공이면 JSON(204는 undefined)을, 실패면 ApiError를 던진다.
 * - credentials 'same-origin': 세션 쿠키(__Host-AdminSession)는 관리 출처에만 붙는다.
 * - redirect 'error': 이 API는 리다이렉트하지 않는다. 리다이렉트가 오면 중간자·오설정이므로 따라가지 않는다.
 * - cache 'no-store': 서버도 no-store를 주지만 브라우저 HTTP 캐시를 한 번 더 배제한다.
 */
export async function request<T>(method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, options: RequestOptions = {}): Promise<T> {
  const headers: Record<string, string> = { [CSRF_HEADER]: CSRF_VALUE, Accept: 'application/json' }
  let body: BodyInit | undefined
  if (options.json !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(options.json)
  } else if (options.form) {
    body = options.form
  }

  // 경로 검사는 try 밖에서 한다: 잘못된 경로는 프로그래밍 오류이지 "네트워크 오류"가 아니다.
  const url = buildUrl(path, options.query)
  let res: Response
  try {
    res = await fetch(url, {
      method, headers, body, signal: options.signal, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
    })
  } catch (cause) {
    // 취소는 오류가 아니다: TanStack Query가 signal로 끊은 요청은 그대로 흘려보낸다.
    if ((cause as { name?: unknown } | null)?.name === 'AbortError') throw cause
    throw new ApiError(0, '네트워크 오류')
  }
  if (!res.ok) throw await toApiError(res)
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}
````

**`PortfolioBlog.Web/src/api/endpoints.ts`**

````ts
import { request } from './client'
import type {
  Attachment, AuthStatus, PagedAttachments, PagedPosts, PostDetail, PreviewResponse,
  Series, SeriesDetail, Tag, UpsertPostRequest, UpsertSeriesRequest,
} from './types'

// 경로의 id는 서버가 준 Guid만 들어오지만, 경로 조립은 항상 encodeURIComponent를 거친다(경로 주입 방지).
const id = (value: string) => encodeURIComponent(value)

export const auth = {
  me: (signal?: AbortSignal) => request<AuthStatus>('GET', '/api/auth/me', { signal }),
  login: (password: string) => request<void>('POST', '/api/auth/login', { json: { password } }),
  logout: () => request<void>('POST', '/api/auth/logout'),
}

export const posts = {
  list: (q: string, skip: number, take: number, signal?: AbortSignal) =>
    request<PagedPosts>('GET', '/api/posts', { query: { q, skip, take }, signal }),
  get: (postId: string, signal?: AbortSignal) => request<PostDetail>('GET', `/api/posts/${id(postId)}`, { signal }),
  create: (body: UpsertPostRequest) => request<PostDetail>('POST', '/api/posts', { json: body }),
  update: (postId: string, body: UpsertPostRequest) => request<PostDetail>('PUT', `/api/posts/${id(postId)}`, { json: body }),
  remove: (postId: string, version: number) => request<void>('DELETE', `/api/posts/${id(postId)}`, { query: { version } }),
}

export const series = {
  list: (signal?: AbortSignal) => request<Series[]>('GET', '/api/series', { signal }),
  get: (seriesId: string, signal?: AbortSignal) => request<SeriesDetail>('GET', `/api/series/${id(seriesId)}`, { signal }),
  create: (body: UpsertSeriesRequest) => request<Series>('POST', '/api/series', { json: body }),
  update: (seriesId: string, body: UpsertSeriesRequest) => request<Series>('PUT', `/api/series/${id(seriesId)}`, { json: body }),
  remove: (seriesId: string) => request<void>('DELETE', `/api/series/${id(seriesId)}`),
}

export const tags = {
  list: (signal?: AbortSignal) => request<Tag[]>('GET', '/api/tags', { signal }),
  remove: (tagId: string) => request<void>('DELETE', `/api/tags/${id(tagId)}`),
}

export const attachments = {
  list: (skip: number, take: number, signal?: AbortSignal) =>
    request<PagedAttachments>('GET', '/api/attachments', { query: { skip, take }, signal }),
  /** multipart 필드 이름은 서버 계약상 'file'이다. 같은 내용이면 서버가 기존 첨부(200)를 돌려준다. */
  upload: (file: Blob, fileName: string) => {
    const form = new FormData()
    form.append('file', file, fileName)
    return request<Attachment>('POST', '/api/attachments', { form })
  },
  remove: (attachmentId: string) => request<void>('DELETE', `/api/attachments/${id(attachmentId)}`),
}

export const preview = {
  render: (markdown: string, signal?: AbortSignal) => request<PreviewResponse>('POST', '/api/preview', { json: { markdown }, signal }),
}
````


- [ ] **Step 4: 통과를 본다.** `npx vitest run src/api` → 통과. **규칙 8:** `client.ts`에서 `const url = buildUrl(...)`을 `try` 안으로 옮기면 "API 경로가 아니면 호출하지 않는다" 5건이 `'0 네트워크 오류'`로 실패하는지 확인하고 되돌린다(계획 작성 중 실제로 이 결함이 있었다). `redirect: 'error'`를 지우면 첫 테스트가 실패하는지도 확인한다.

- [ ] **Step 5: 커밋.** `추가: 관리 API 호출의 단일 통로 — CSRF 헤더·같은 출처·리다이렉트 거부를 한 곳에서 강제`

### Task 3: 보안에 민감한 순수 함수들 (next 검증·클라이언트 검증·임시본·미리보기 문서)

**Files:**
- Create: `src/lib/{safeNext.ts, validation.ts, drafts.ts, previewDoc.ts, markdownImage.ts, useDebounced.ts}`
- Test: `src/lib/lib.test.ts`(`altTextOf`의 테스트는 Task 7의 `attachments.test.tsx`에 있다)

**Interfaces:**
- Consumes: `FieldErrors`, `UpsertPostRequest`, `UpsertSeriesRequest`(Task 2).
- Produces:
  - `safeNext(raw: string|null|undefined, origin?: string): string`, `hasControlChar(value: string): boolean`
  - `LIMITS`(slugMax 100, titleMax 200, summaryMax 300, contentMaxBytes 204_800, seriesDescriptionMax 1000, tagMax 50, maxTagsPerPost 20, queryMax 100, attachmentMaxBytes 10_485_760), `validatePost(req): FieldErrors`, `validateSeries(req): FieldErrors`, `validateImageFile({size, type}): string|null`, `hasErrors(errors): boolean`, `utf8ByteLength(value): number`, `displayTag(raw): string`, `SLUG_PATTERN`, `IMAGE_TYPES`
  - `Draft`, `DraftFields`, `NEW_POST_KEY = 'new'`, `loadDraft(postId, storage?)`, `saveDraft(postId, draft, storage?): boolean`, `clearDraft(postId, storage?)`, `sameFields(a, b): boolean`
  - `buildPreviewDocument(html: string, origin?: string): string`, `previewCsp(origin: string): string`, `PREVIEW_STYLESHEETS`
  - `altTextOf(fileName: string): string`, `useDebounced<T>(value: T, delayMs: number): T`, `formatDateTime(iso): string`, `formatBytes(bytes): string`

- [ ] **Step 1: 실패하는 테스트를 쓴다.**

**`PortfolioBlog.Web/src/lib/lib.test.ts`**

````ts
import { describe, expect, it } from 'vitest'
import { hasControlChar, safeNext } from './safeNext'
import { buildPreviewDocument, previewCsp } from './previewDoc'
import { clearDraft, loadDraft, saveDraft, sameFields, type Draft } from './drafts'
import { LIMITS, displayTag, utf8ByteLength, validateImageFile, validatePost, validateSeries } from './validation'
import type { UpsertPostRequest } from '../api/types'

const ORIGIN = 'https://admin.blog.test'

describe('safeNext', () => {
  it.each([
    ['/posts/new', '/posts/new'], ['/posts/0199?x=1#top', '/posts/0199?x=1#top'], ['/', '/'],
  ])('같은 출처 경로는 그대로: %s', (raw, expected) => expect(safeNext(raw, ORIGIN)).toBe(expected))

  it.each([
    null, '', 'posts', 'https://evil.test/', '//evil.test/x', '/\\evil.test/x', 'javascript:alert(1)',
    '/login', '/login?next=/x', '/a' + String.fromCharCode(10) + 'b', '/a' + String.fromCharCode(0), '/' + 'a'.repeat(2048),
  ])('그 밖은 전부 "/": %s', (raw) => expect(safeNext(raw, ORIGIN)).toBe('/'))

  it('제어 문자 판정', () => {
    expect(hasControlChar('abc 한글')).toBe(false)
    expect(hasControlChar('a' + String.fromCharCode(0x1f))).toBe(true)
    expect(hasControlChar('a' + String.fromCharCode(0x7f))).toBe(true)
  })
})

describe('previewDoc', () => {
  it('CSP는 출처를 명시하고 self를 쓰지 않는다(Firefox는 srcdoc의 self를 부모 출처로 보지 않는다)', () => {
    const csp = previewCsp(ORIGIN)
    expect(csp).toBe(`default-src 'none'; img-src ${ORIGIN}; style-src ${ORIGIN}; base-uri 'none'; form-action 'none'`)
    expect(csp).not.toContain("'self'")
    expect(csp).not.toContain('script-src')
  })
  it.each(["https://a.test; script-src *", "https://a.test'", 'https://a.test/path', 'javascript:x', '', 'https://a b'])('이상한 출처는 거부: %s', (origin) => {
    expect(() => previewCsp(origin)).toThrow()
  })
  it('CSP meta가 head의 첫 요소이고, 서버 HTML은 article-body 안에만 들어간다', () => {
    const doc = buildPreviewDocument('<p>본문</p>', 'https://localhost:5173')
    expect(doc.indexOf('<meta http-equiv="Content-Security-Policy"')).toBe(doc.indexOf('<head>') + '<head>'.length)
    expect(doc).toContain('<div class="article-body"><p>본문</p></div>')
    expect(doc).toContain('<link rel="stylesheet" href="/preview/site.css"><link rel="stylesheet" href="/preview/highlight.css">')
    expect(doc).not.toContain('<script')
  })
})

describe('drafts', () => {
  const memory = (): Storage => {
    const map = new Map<string, string>()
    return { getItem: k => map.get(k) ?? null, setItem: (k, v) => void map.set(k, v), removeItem: k => void map.delete(k),
      clear: () => map.clear(), key: i => [...map.keys()][i] ?? null, get length() { return map.size } }
  }
  const draft: Draft = { slug: 's', title: 't', summary: '', contentMarkdown: '# 본문', tagNames: ['a'], seriesId: null, seriesOrder: null, baseVersion: 7, savedAt: '2026-09-21T00:00:00.000Z' }

  it('저장·조회·삭제는 글별 키로 분리된다', () => {
    const s = memory()
    expect(saveDraft('id-1', draft, s)).toBe(true)
    expect(loadDraft('id-1', s)).toEqual(draft)
    expect(loadDraft('id-2', s)).toBeNull()
    clearDraft('id-1', s)
    expect(loadDraft('id-1', s)).toBeNull()
  })
  it.each(['{', '[]', 'null', '{"slug":1}', JSON.stringify({ ...draft, tagNames: [1] }), JSON.stringify({ ...draft, baseVersion: 'x' })])('깨진 값은 없는 것으로 본다: %s', (raw) => {
    const s = memory(); s.setItem('pb.draft.v1:x', raw)
    expect(loadDraft('x', s)).toBeNull()
  })
  it('용량 초과로 저장이 실패해도 던지지 않는다', () => {
    const s = memory(); s.setItem = () => { throw new DOMException('quota', 'QuotaExceededError') }
    expect(saveDraft('x', draft, s)).toBe(false)
  })
  it('sameFields는 태그 순서까지 본다', () => {
    expect(sameFields(draft, { ...draft })).toBe(true)
    expect(sameFields({ ...draft, tagNames: ['a', 'b'] }, { ...draft, tagNames: ['b', 'a'] })).toBe(false)
    expect(sameFields(draft, { ...draft, contentMarkdown: '# 본문 ' })).toBe(false)
  })
})

describe('validation', () => {
  const ok: UpsertPostRequest = { slug: 'my-first-post', title: '제목', summary: '', contentMarkdown: '', tagNames: [], seriesId: null, seriesOrder: null }
  it('정상 입력은 오류가 없다', () => expect(validatePost(ok)).toEqual({}))
  it.each(['', 'My-Post', 'a--b', '-a', 'a-', 'a_b', '한글', 'a'.repeat(101)])('slug 거부: %s', (slug) => {
    expect(validatePost({ ...ok, slug }).slug).toBeDefined()
  })
  it('slug 100자는 통과', () => expect(validatePost({ ...ok, slug: 'a'.repeat(100) })).toEqual({}))
  it('제목은 트림 후 1~200자', () => {
    expect(validatePost({ ...ok, title: '   ' }).title).toBeDefined()
    expect(validatePost({ ...ok, title: ' ' + '가'.repeat(200) + ' ' })).toEqual({})
    expect(validatePost({ ...ok, title: '가'.repeat(201) }).title).toBeDefined()
  })
  it('본문은 글자 수가 아니라 UTF-8 바이트로 잰다', () => {
    expect(utf8ByteLength('가')).toBe(3)
    expect(validatePost({ ...ok, contentMarkdown: '가'.repeat(68_266) })).toEqual({})           // 204,798바이트
    expect(validatePost({ ...ok, contentMarkdown: '가'.repeat(68_267) }).contentMarkdown).toBeDefined() // 204,801바이트
    expect(validatePost({ ...ok, contentMarkdown: 'a'.repeat(LIMITS.contentMaxBytes) })).toEqual({})
  })
  it('NUL은 모든 텍스트 필드에서 거부', () => {
    const nul = String.fromCharCode(0)
    const errors = validatePost({ ...ok, title: 'a' + nul, summary: nul, contentMarkdown: nul, tagNames: ['x' + nul] })
    expect(Object.keys(errors).sort()).toEqual(['contentMarkdown', 'summary', 'tagNames', 'title'])
  })
  it('태그: 공백 정리·빈 값 무시·슬래시 거부·50자·대소문자 무시 20개', () => {
    expect(displayTag('  C#   고급  ')).toBe('C# 고급')
    expect(validatePost({ ...ok, tagNames: ['', '  ', 'a'] })).toEqual({})
    expect(validatePost({ ...ok, tagNames: ['a/b'] }).tagNames).toBeDefined()
    expect(validatePost({ ...ok, tagNames: ['가'.repeat(51)] }).tagNames).toBeDefined()
    const twenty = Array.from({ length: 20 }, (_, i) => `t${i}`)
    expect(validatePost({ ...ok, tagNames: [...twenty, 'T0'] })).toEqual({})   // 대소문자만 다른 중복은 1개로 센다
    expect(validatePost({ ...ok, tagNames: [...twenty, 't20'] }).tagNames).toBeDefined()
  })
  it('시리즈와 순서는 쌍으로, 순서는 1 이상의 정수', () => {
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: null }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: null, seriesOrder: 1 }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: 0 }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: 1.5 }).seriesOrder).toBeDefined()
    expect(validatePost({ ...ok, seriesId: 'x', seriesOrder: 1 })).toEqual({})
  })
  it('시리즈 설명 1000자', () => {
    expect(validateSeries({ slug: 's', title: 't', description: '가'.repeat(1000) })).toEqual({})
    expect(validateSeries({ slug: 's', title: 't', description: '가'.repeat(1001) }).description).toBeDefined()
  })
  it('이미지 사전 검사', () => {
    expect(validateImageFile({ size: 10, type: 'image/png' })).toBeNull()
    expect(validateImageFile({ size: 0, type: 'image/png' })).not.toBeNull()
    expect(validateImageFile({ size: LIMITS.attachmentMaxBytes + 1, type: 'image/png' })).not.toBeNull()
    expect(validateImageFile({ size: 10, type: 'image/svg+xml' })).not.toBeNull()
  })
})
````


- [ ] **Step 2: 실패를 본다.** `npx vitest run src/lib`

- [ ] **Step 3: 구현한다.** 검증 값은 서버(`PostValidation`·`SeriesValidation`·`TagResolver.Validate`·`AppDbContext` 상수)와 같아야 한다 — 구현 전에 서버 코드에서 값을 다시 확인한다(계획 작성 시점 값이다).

**`PortfolioBlog.Web/src/lib/safeNext.ts`**

````ts
/** C0 제어 문자(0x00~0x1F)나 DEL(0x7F)이 있는가. 정규식의 유니코드 이스케이프 대신 코드 값으로 비교한다(저장소 규칙: NUL 표기 금지). */
export function hasControlChar(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i)
    if (code < 0x20 || code === 0x7f) return true
  }
  return false
}

/**
 * 로그인 뒤 돌아갈 경로(?next=)를 검증한다. 같은 출처의 절대 경로만 통과시키고 나머지는 '/'로 바꾼다(오픈 리다이렉트 방지).
 * 거부: 스킴·호스트가 있는 값, '//'·'/\' 시작(브라우저가 호스트로 해석), 제어 문자, 로그인 화면 자신(루프).
 */
export function safeNext(raw: string | null | undefined, origin: string = window.location.origin): string {
  if (!raw || raw.length > 2048) return '/'
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return '/'
  if (hasControlChar(raw)) return '/'
  let url: URL
  try { url = new URL(raw, origin) } catch { return '/' }
  if (url.origin !== origin) return '/'
  if (url.pathname === '/login') return '/'
  return url.pathname + url.search + url.hash
}
````

**`PortfolioBlog.Web/src/lib/validation.ts`**

````ts
import type { FieldErrors } from '../api/errors'
import type { UpsertPostRequest, UpsertSeriesRequest } from '../api/types'

// 서버 검증(PostValidation·SeriesValidation·TagResolver.Validate, AppDbContext의 상수)을 그대로 비춘 값이다.
// 이 검사는 편의(저장 전에 바로 알려 줌)일 뿐이고 권한 있는 판정은 서버가 한다 — 서버의 400도 같은 필드 키로 화면에 표시된다.
export const LIMITS = {
  slugMax: 100, titleMax: 200, summaryMax: 300, contentMaxBytes: 204_800,
  seriesDescriptionMax: 1000, tagMax: 50, maxTagsPerPost: 20, queryMax: 100,
  attachmentMaxBytes: 10_485_760,
} as const

export const SLUG_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/
export const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'] as const

const utf8 = new TextEncoder()
/** UTF-8 바이트 수. 서버·DB CHECK와 같은 단위다(글자 수가 아니다 — 한글 1자는 3바이트). */
export const utf8ByteLength = (value: string): number => utf8.encode(value).length

const hasNul = (value: string): boolean => value.includes(String.fromCharCode(0))
const NUL_MESSAGE = '제어 문자(NUL)는 포함할 수 없습니다.'

function add(errors: FieldErrors, field: string, message: string): void {
  (errors[field] ??= []).push(message)
}

/** 서버의 TagResolver.Display와 같은 표시 형태: 공백 정리 + NFC. */
export const displayTag = (raw: string): string => raw.split(/\s+/).filter(Boolean).join(' ').normalize('NFC')

function validateSlugAndTitle(errors: FieldErrors, slug: string, title: string): void {
  if (slug.length === 0) add(errors, 'slug', 'slug는 필수입니다.')
  else if (hasNul(slug)) add(errors, 'slug', NUL_MESSAGE)
  else if (slug.length > LIMITS.slugMax || !SLUG_PATTERN.test(slug)) add(errors, 'slug', `slug는 소문자·숫자·하이픈만 쓰고 ${LIMITS.slugMax}자 이하여야 합니다(예: my-first-post).`)
  if (title.trim().length === 0) add(errors, 'title', '제목은 비울 수 없습니다.')
  else if (hasNul(title)) add(errors, 'title', NUL_MESSAGE)
  else if (title.trim().length > LIMITS.titleMax) add(errors, 'title', `제목은 ${LIMITS.titleMax}자 이하여야 합니다.`)
}

export function validatePost(req: UpsertPostRequest): FieldErrors {
  const errors: FieldErrors = {}
  validateSlugAndTitle(errors, req.slug, req.title)
  if (hasNul(req.summary)) add(errors, 'summary', NUL_MESSAGE)
  else if (req.summary.trim().length > LIMITS.summaryMax) add(errors, 'summary', `요약은 ${LIMITS.summaryMax}자 이하여야 합니다.`)
  if (hasNul(req.contentMarkdown)) add(errors, 'contentMarkdown', NUL_MESSAGE)
  else if (utf8ByteLength(req.contentMarkdown) > LIMITS.contentMaxBytes) add(errors, 'contentMarkdown', `본문은 UTF-8 기준 ${LIMITS.contentMaxBytes / 1024}KB 이하여야 합니다.`)

  const distinct = new Set<string>()
  for (const raw of req.tagNames) {
    if (raw.trim().length === 0) continue
    const display = displayTag(raw)
    if (hasNul(display)) add(errors, 'tagNames', '태그는 제어 문자(NUL)를 포함할 수 없습니다.')
    else if (display.length > LIMITS.tagMax) add(errors, 'tagNames', `태그는 ${LIMITS.tagMax}자 이하여야 합니다: ${display.slice(0, 20)}…`)
    else if (display.includes('/')) add(errors, 'tagNames', `태그에 '/'를 쓸 수 없습니다: ${display}`)
    else distinct.add(display.toLowerCase())
  }
  if (distinct.size > LIMITS.maxTagsPerPost) add(errors, 'tagNames', `태그는 글당 ${LIMITS.maxTagsPerPost}개까지입니다.`)

  if ((req.seriesId === null) !== (req.seriesOrder === null)) add(errors, 'seriesOrder', '시리즈와 순서는 함께 정하거나 함께 비워야 합니다.')
  else if (req.seriesOrder !== null && (!Number.isInteger(req.seriesOrder) || req.seriesOrder <= 0)) add(errors, 'seriesOrder', '순서는 1 이상의 정수여야 합니다.')
  return errors
}

export function validateSeries(req: UpsertSeriesRequest): FieldErrors {
  const errors: FieldErrors = {}
  validateSlugAndTitle(errors, req.slug, req.title)
  if (hasNul(req.description)) add(errors, 'description', NUL_MESSAGE)
  else if (req.description.trim().length > LIMITS.seriesDescriptionMax) add(errors, 'description', `설명은 ${LIMITS.seriesDescriptionMax}자 이하여야 합니다.`)
  return errors
}

/** 업로드 전 편의 검사. 형식의 권한 있는 판정은 서버의 시그니처 검사다(file.type은 브라우저가 확장자로 추측한 값일 뿐이다). */
export function validateImageFile(file: { size: number; type: string }): string | null {
  if (file.size === 0) return '빈 파일은 올릴 수 없습니다.'
  if (file.size > LIMITS.attachmentMaxBytes) return `이미지는 ${LIMITS.attachmentMaxBytes / 1_048_576}MB 이하여야 합니다.`
  if (!(IMAGE_TYPES as readonly string[]).includes(file.type)) return 'PNG·JPEG·GIF·WebP 이미지만 올릴 수 있습니다.'
  return null
}

export const hasErrors = (errors: FieldErrors): boolean => Object.keys(errors).length > 0
````

**`PortfolioBlog.Web/src/lib/drafts.ts`**

````ts
// 글별 임시본(localStorage). 저장에 성공하면 지운다. 인증 정보는 절대 넣지 않는다 — 들어가는 것은 작성자 자신의 글 내용뿐이다.
// localStorage는 같은 출처의 스크립트가 고칠 수 있으므로 읽을 때 모양을 검사한다(깨진 값은 없는 것으로 본다).

export interface Draft {
  slug: string; title: string; summary: string; contentMarkdown: string
  tagNames: string[]; seriesId: string | null; seriesOrder: number | null
  /** 이 임시본이 바탕으로 삼은 서버 version. 새 글이면 null. 409 뒤 비교 화면에서 쓴다. */
  baseVersion: number | null
  /** 저장 시각(ISO 8601). */
  savedAt: string
}

export type DraftFields = Omit<Draft, 'baseVersion' | 'savedAt'>

const PREFIX = 'pb.draft.v1:'
export const NEW_POST_KEY = 'new'
const keyOf = (postId: string) => PREFIX + postId

const isString = (v: unknown): v is string => typeof v === 'string'

function isDraft(v: unknown): v is Draft {
  if (typeof v !== 'object' || v === null) return false
  const d = v as Record<string, unknown>
  return isString(d.slug) && isString(d.title) && isString(d.summary) && isString(d.contentMarkdown) && isString(d.savedAt)
    && Array.isArray(d.tagNames) && d.tagNames.every(isString)
    && (d.seriesId === null || isString(d.seriesId))
    && (d.seriesOrder === null || typeof d.seriesOrder === 'number')
    && (d.baseVersion === null || typeof d.baseVersion === 'number')
}

export function loadDraft(postId: string, storage: Storage = window.localStorage): Draft | null {
  try {
    const raw = storage.getItem(keyOf(postId))
    if (raw === null) return null
    const parsed: unknown = JSON.parse(raw)
    return isDraft(parsed) ? parsed : null
  } catch { return null }
}

/** 저장에 실패하면(용량 초과·비공개 모드) false. 임시본은 편의 기능이므로 실패해도 편집은 계속된다. */
export function saveDraft(postId: string, draft: Draft, storage: Storage = window.localStorage): boolean {
  try { storage.setItem(keyOf(postId), JSON.stringify(draft)); return true } catch { return false }
}

export function clearDraft(postId: string, storage: Storage = window.localStorage): void {
  try { storage.removeItem(keyOf(postId)) } catch { /* 지울 수 없으면 둔다 */ }
}

/** 임시본이 주어진 내용과 같은가(태그는 순서까지 비교한다 — 화면에 보이는 순서가 곧 입력이다). */
export function sameFields(a: DraftFields, b: DraftFields): boolean {
  return a.slug === b.slug && a.title === b.title && a.summary === b.summary && a.contentMarkdown === b.contentMarkdown
    && a.seriesId === b.seriesId && a.seriesOrder === b.seriesOrder
    && a.tagNames.length === b.tagNames.length && a.tagNames.every((t, i) => t === b.tagNames[i])
}
````

**`PortfolioBlog.Web/src/lib/previewDoc.ts`**

````ts
// 미리보기 iframe(srcdoc)에 넣을 문서를 만든다. 서버가 정제한 HTML은 **여기에만** 들어간다 — React DOM에는 절대 넣지 않는다.
//
// 방어는 세 겹이다: (1) 서버의 마크다운 파이프라인이 이미 정제했다, (2) iframe sandbox=""(토큰 없음)라 스크립트·폼·팝업·
// 같은 출처 접근이 전부 꺼진다, (3) 문서 안 CSP가 default-src 'none'이라 이 출처의 이미지·스타일시트 말고는 아무것도 못 읽는다.
//
// CSP에 'self'를 쓰지 않는 이유(실측, Playwright): Firefox는 about:srcdoc 문서의 'self'를 부모 출처로 보지 않아
// 스타일시트와 이미지를 모두 차단한다. Chromium은 허용한다. 출처를 명시하면 둘 다 허용한다.

const ORIGIN_PATTERN = /^https?:\/\/[a-z0-9.-]+(:\d{1,5})?$/i

/** 공개 사이트와 같은 모양으로 보이게 하는 스타일시트(공개 사이트 CSS의 스냅숏 — 서버 테스트가 원본과 같은지 검사한다). */
export const PREVIEW_STYLESHEETS = ['/preview/site.css', '/preview/highlight.css'] as const

export function previewCsp(origin: string): string {
  // origin은 window.location.origin에서만 온다. 그래도 CSP 문자열에 끼워 넣기 전에 모양을 검사한다(따옴표·세미콜론·공백 주입 차단).
  if (!ORIGIN_PATTERN.test(origin)) throw new Error('미리보기 CSP에 쓸 수 없는 출처입니다.')
  return `default-src 'none'; img-src ${origin}; style-src ${origin}; base-uri 'none'; form-action 'none'`
}

export function buildPreviewDocument(html: string, origin: string = window.location.origin): string {
  const links = PREVIEW_STYLESHEETS.map(href => `<link rel="stylesheet" href="${href}">`).join('')
  // CSP meta는 <head>의 첫 요소여야 뒤따르는 <link>에도 적용된다.
  return '<!doctype html><html lang="ko"><head>' +
    `<meta http-equiv="Content-Security-Policy" content="${previewCsp(origin)}">` +
    `<meta charset="utf-8">${links}</head>` +
    `<body><main><article><div class="article-body">${html}</div></article></main></body></html>`
}
````

**`PortfolioBlog.Web/src/lib/markdownImage.ts`**

````ts
import { hasControlChar } from './safeNext'

/** 파일 이름에서 마크다운 이미지의 대체 텍스트를 만든다. 대괄호·괄호·제어 문자는 문법을 깨므로 뺀다. */
export function altTextOf(fileName: string): string {
  const stem = fileName.replace(/\.[^.]*$/, '')
  let out = ''
  for (const ch of stem) if (!'[]()'.includes(ch) && !hasControlChar(ch)) out += ch
  return out.trim().slice(0, 100) || 'image'
}
````

**`PortfolioBlog.Web/src/lib/useDebounced.ts`**

````ts
import { useEffect, useState } from 'react'

/** value가 delayMs 동안 바뀌지 않았을 때만 따라오는 값. */
export function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delayMs)
    return () => window.clearTimeout(timer)
  }, [value, delayMs])
  return debounced
}

export const formatDateTime = (iso: string): string => {
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('ko-KR', { dateStyle: 'medium', timeStyle: 'short' })
}

export const formatBytes = (bytes: number): string =>
  bytes < 1024 ? `${bytes}B` : bytes < 1_048_576 ? `${(bytes / 1024).toFixed(1)}KB` : `${(bytes / 1_048_576).toFixed(1)}MB`
````


- [ ] **Step 4: 통과를 본다.** **규칙 8:** (a) `safeNext`에서 `raw.startsWith('//')` 검사를 지우면 — `new URL('//evil.test/x', origin).origin !== origin`이 여전히 막는다. 즉 그 줄은 이중 방어다. 대신 `url.origin !== origin` 줄을 지우고 `startsWith('//')`도 지웠을 때 `//evil.test/x`가 실패하는지 확인한다. (b) `previewCsp`의 `ORIGIN_PATTERN` 검사를 지우면 "이상한 출처는 거부" 6건이 실패하는지 확인한다. 둘 다 되돌린다.

- [ ] **Step 5: 커밋.** `추가: 로그인 후 이동 경로 검증·임시본·미리보기 문서 — 화면이 기대는 순수 함수`

### Task 4: 앱 셸·인증 흐름·목록 화면(글·시리즈·태그)

**Files:**
- Create: `src/app/{queryClient.ts, routes.tsx}`, `src/auth/{RequireAuth.tsx, LoginPage.tsx}`, `src/components/{notices.tsx, Layout.tsx}`, `src/pages/{PostsPage.tsx, SeriesPage.tsx, TagsPage.tsx}`, `src/test/harness.tsx`
- Modify: `src/App.tsx`(교체)
- Test: `src/test/{auth.test.tsx, lists.test.tsx}`

**Interfaces:**
- Consumes: Task 2·3 전부.
- Produces: `ME_KEY = ['auth','me']`, `createQueryClient(): QueryClient`, `noteAuthFailure(client, error): void`, `routes: RouteObject[]`, `ErrorNotice({error, onRetry?})`, `FieldError({errors, field})`, `Loading({label?})`. 테스트 도구 `stubApi(table)`(표에 없는 호출은 실패), `renderApp(path)`, `LOGGED_IN`. 쿼리 키 규약: `['posts','list',q,page]`, `['posts','detail',id]`, `['series','list']`, `['tags','list']`, `['attachments','list',page]`.

- [ ] **Step 1: 테스트 도구와 실패하는 테스트를 쓴다.**

**`PortfolioBlog.Web/src/test/harness.tsx`**

````tsx
import '@testing-library/jest-dom/vitest'
import { QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { vi } from 'vitest'
import { routes } from '../app/routes'
import { createQueryClient } from '../app/queryClient'

export interface Call { method: string; url: string; body: unknown }
type Reply = { status: number; body?: unknown; headers?: Record<string, string> }
type Handler = Reply | ((call: Call) => Reply)

/**
 * fetch를 "METHOD 경로" 표로 대신한다. 표에 없는 호출은 테스트를 실패시킨다(조용한 404로 통과하지 않게).
 * 경로는 쿼리 문자열을 뺀 값으로 찾는다. 기록된 호출은 calls로 확인한다.
 */
export function stubApi(table: Record<string, Handler>) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    const raw = init?.body
    const call: Call = { method, url, body: typeof raw === 'string' ? JSON.parse(raw) : raw ?? null }
    calls.push(call)
    const handler = table[`${method} ${url.split('?')[0]}`]
    if (!handler) throw new Error(`stubApi: 예상하지 못한 호출 ${method} ${url}`)
    const reply = typeof handler === 'function' ? handler(call) : handler
    return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), { status: reply.status, headers: reply.headers })
  }))
  return calls
}

/** 실제 라우트 표(app/routes)를 메모리 라우터로 띄운다. */
export function renderApp(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  const view = render(<QueryClientProvider client={createQueryClient()}><RouterProvider router={router} /></QueryClientProvider>)
  return { router, ...view }
}

export const LOGGED_IN = { 'GET /api/auth/me': { status: 200, body: { authenticated: true } } } as const
````

**`PortfolioBlog.Web/src/test/auth.test.tsx`**

````tsx
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => vi.unstubAllGlobals())
const EMPTY_LIST = { status: 200, body: { items: [], total: 0 } }

describe('인증 흐름', () => {
  it('로그인하지 않았으면 가려던 경로를 next에 담아 로그인 화면으로 보낸다', async () => {
    stubApi({ 'GET /api/auth/me': { status: 200, body: { authenticated: false } } })
    const { router } = renderApp('/series?x=1')
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(router.state.location.pathname).toBe('/login')
    expect(router.state.location.search).toBe('?next=' + encodeURIComponent('/series?x=1'))
  })

  it('로그인에 성공하면 next로 가고, 비밀번호 입력은 지워진다', async () => {
    const calls = stubApi({ 'POST /api/auth/login': { status: 204 }, ...LOGGED_IN, 'GET /api/tags': { status: 200, body: [] } })
    const { router } = renderApp('/login?next=%2Ftags')
    await userEvent.type(screen.getByLabelText('비밀번호'), 'dummy-pass')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await screen.findByRole('heading', { name: '태그' })
    expect(router.state.location.pathname).toBe('/tags')
    expect(calls.find(c => c.url === '/api/auth/login')?.body).toEqual({ password: 'dummy-pass' })
  })

  it.each(['https://evil.test/', '//evil.test', '/\\evil.test', '/login'])('적대적 next(%s)는 무시하고 "/"로 간다', async (next) => {
    stubApi({ 'POST /api/auth/login': { status: 204 }, ...LOGGED_IN, 'GET /api/posts': EMPTY_LIST })
    const { router } = renderApp('/login?next=' + encodeURIComponent(next))
    await userEvent.type(screen.getByLabelText('비밀번호'), 'dummy-pass')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await screen.findByRole('heading', { name: '글' })
    expect(router.state.location.pathname).toBe('/')
  })

  it('비밀번호가 틀리면 안내하고 입력을 지운다. 429는 대기 시간을 알린다', async () => {
    let status = 401
    stubApi({ 'POST /api/auth/login': () => status === 401 ? { status, body: { title: '로그인 실패' } } : { status, body: { title: 'x' }, headers: { 'Retry-After': '42' } } })
    renderApp('/login')
    const input = screen.getByLabelText('비밀번호') as HTMLInputElement
    await userEvent.type(input, 'wrong-dummy')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('비밀번호가 맞지 않습니다.')
    expect(input.value).toBe('')
    status = 429
    await userEvent.type(input, 'wrong-dummy')
    await userEvent.click(screen.getByRole('button', { name: '로그인' }))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('42초'))
  })

  it('화면을 쓰는 중에 401이 오면(세션 만료) 로그인 화면으로 돌아간다', async () => {
    stubApi({ ...LOGGED_IN, 'GET /api/tags': { status: 401 } })
    const { router } = renderApp('/tags')
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(router.state.location.search).toBe('?next=%2Ftags')
  })

  it('로그아웃하면 로그인 화면으로 간다', async () => {
    let loggedIn = true // 실제 서버처럼 로그아웃 뒤의 /me는 false를 돌려준다
    const calls = stubApi({
      'GET /api/auth/me': () => ({ status: 200, body: { authenticated: loggedIn } }),
      'GET /api/tags': { status: 200, body: [] },
      'POST /api/auth/logout': () => { loggedIn = false; return { status: 204 } },
    })
    renderApp('/tags')
    await userEvent.click(await screen.findByRole('button', { name: '로그아웃' }))
    await screen.findByRole('heading', { name: '관리자 로그인' })
    expect(calls.some(c => c.method === 'POST' && c.url === '/api/auth/logout')).toBe(true)
  })
})
````

**`PortfolioBlog.Web/src/test/lists.test.tsx`**

````tsx
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

describe('글 목록', () => {
  const post = { id: 'p1', slug: 'hello', title: '안녕', summary: '', tags: ['a'], seriesId: null, seriesOrder: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z', version: 41 }

  it('삭제는 확인을 거치고, 목록에서 받은 version을 쿼리로 보낸다', async () => {
    const calls = stubApi({ ...LOGGED_IN, 'GET /api/posts': { status: 200, body: { items: [post], total: 1 } }, 'DELETE /api/posts/p1': { status: 204 } })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    renderApp('/')
    await userEvent.click(await screen.findByRole('button', { name: '삭제' }))
    expect(calls.some(c => c.method === 'DELETE')).toBe(false) // 취소하면 보내지 않는다
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    await waitFor(() => expect(calls.find(c => c.method === 'DELETE')?.url).toBe('/api/posts/p1?version=41'))
    expect(confirm).toHaveBeenCalledTimes(2)
  })

  it('서버가 준 제목은 텍스트로만 그려진다(마크업으로 해석되지 않는다)', async () => {
    stubApi({ ...LOGGED_IN, 'GET /api/posts': { status: 200, body: { items: [{ ...post, title: '<img src=x onerror=alert(1)>' }], total: 1 } } })
    const view = renderApp('/')
    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(view.container.querySelector('img')).toBeNull()
  })
})
````


- [ ] **Step 2: 실패를 본다.** `npx vitest run src/test`

- [ ] **Step 3: 구현한다.**

**`PortfolioBlog.Web/src/app/queryClient.ts`**

````ts
import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { ApiError } from '../api/errors'
import type { AuthStatus } from '../api/types'

export const ME_KEY = ['auth', 'me'] as const

/**
 * 어느 호출에서든 401(세션 만료·폐기)을 받으면 "로그인 안 됨"으로 기록한다. 화면 전환은 RequireAuth가 이 값을 보고 한다 —
 * 라우터 밖에서 location을 직접 바꾸지 않는다(임시본은 localStorage에 있으므로 로그인 뒤 그대로 복원된다).
 */
export function noteAuthFailure(client: QueryClient, error: unknown): void {
  if (error instanceof ApiError && error.status === 401) client.setQueryData<AuthStatus>(ME_KEY, { authenticated: false })
}

export function createQueryClient(): QueryClient {
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: error => noteAuthFailure(client, error) }),
    mutationCache: new MutationCache({ onError: error => noteAuthFailure(client, error) }),
    defaultOptions: {
      // 자동 재시도 없음: 429·503에는 Retry-After가 있고, 4xx는 다시 보내도 같다. 다시 시도는 사용자가 버튼으로 한다.
      // 창 포커스 재조회 없음: 편집 중인 글이 포커스만으로 다시 불려 와 입력을 덮어쓰면 안 된다.
      queries: { retry: false, refetchOnWindowFocus: false, staleTime: 0 },
      mutations: { retry: false },
    },
  })
  return client
}
````

**`PortfolioBlog.Web/src/components/notices.tsx`**

````tsx
import { describeError, type FieldErrors } from '../api/errors'

/** 오류 한 건을 텍스트로 보여 준다. 서버가 준 문자열은 JSX 텍스트 노드로만 들어간다(React가 이스케이프한다). */
export function ErrorNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (!error) return null
  return (
    <div role="alert" className="rounded border border-red-300 bg-red-50 p-3 text-sm text-red-800">
      <span>{describeError(error)}</span>
      {onRetry && <button type="button" className="ml-3 underline" onClick={onRetry}>다시 시도</button>}
    </div>
  )
}

export function FieldError({ errors, field }: { errors: FieldErrors; field: string }) {
  const messages = errors[field]
  if (!messages || messages.length === 0) return null
  return <ul className="mt-1 text-sm text-red-700" data-field-error={field}>{messages.map((m, i) => <li key={i}>{m}</li>)}</ul>
}

export function Loading({ label = '불러오는 중…' }: { label?: string }) {
  return <p role="status" className="p-4 text-sm text-gray-600">{label}</p>
}
````

**`PortfolioBlog.Web/src/auth/RequireAuth.tsx`**

````tsx
import { useQuery } from '@tanstack/react-query'
import { Navigate, Outlet, useLocation } from 'react-router'
import { auth } from '../api/endpoints'
import { ME_KEY } from '../app/queryClient'
import { ErrorNotice, Loading } from '../components/notices'

/**
 * 로그인한 세션에만 하위 라우트를 보여 준다. 이것은 **화면 편의**다 — 권한의 실제 판정은 서버가 요청마다 한다
 * (호스트·IP·CSRF 헤더·Origin·세션). 이 컴포넌트를 우회해도 API는 401을 돌려준다.
 */
export function RequireAuth() {
  const location = useLocation()
  const me = useQuery({ queryKey: ME_KEY, queryFn: ({ signal }) => auth.me(signal) })
  if (me.isPending) return <Loading label="세션 확인 중…" />
  if (me.isError) return <div className="p-4"><ErrorNotice error={me.error} onRetry={() => void me.refetch()} /></div>
  if (!me.data.authenticated) {
    const next = location.pathname + location.search
    return <Navigate to={`/login?next=${encodeURIComponent(next)}`} replace />
  }
  return <Outlet />
}
````

**`PortfolioBlog.Web/src/auth/LoginPage.tsx`**

````tsx
import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router'
import { auth } from '../api/endpoints'
import { ApiError, describeError } from '../api/errors'
import type { AuthStatus } from '../api/types'
import { ME_KEY } from '../app/queryClient'
import { safeNext } from '../lib/safeNext'

const PASSWORD_MAX = 256 // 서버 AuthEndpoints.PasswordMaxLength

export function LoginPage() {
  const [password, setPassword] = useState('')
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const client = useQueryClient()

  const login = useMutation({
    mutationFn: (value: string) => auth.login(value),
    // 성공·실패와 무관하게 입력을 지운다: 비밀번호를 필요 이상으로 메모리(React 상태)에 두지 않는다.
    onSettled: () => setPassword(''),
    onSuccess: () => {
      client.setQueryData<AuthStatus>(ME_KEY, { authenticated: true })
      void navigate(safeNext(params.get('next')), { replace: true })
    },
  })

  const message = login.error instanceof ApiError && login.error.status === 401
    ? '비밀번호가 맞지 않습니다.'
    : login.error ? describeError(login.error) : null

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (password.length === 0 || login.isPending) return
    login.mutate(password)
  }

  return (
    <main className="mx-auto mt-24 max-w-sm p-4">
      <h1 className="mb-4 text-xl font-bold">관리자 로그인</h1>
      <form onSubmit={submit} className="space-y-3">
        <label className="block text-sm">
          <span>비밀번호</span>
          <input type="password" name="password" autoComplete="current-password" autoFocus required maxLength={PASSWORD_MAX}
            value={password} onChange={e => setPassword(e.target.value)} className="mt-1 w-full rounded border p-2" />
        </label>
        {message && <p role="alert" className="text-sm text-red-700">{message}</p>}
        <button type="submit" disabled={login.isPending} className="w-full rounded bg-black p-2 text-white disabled:opacity-50">
          {login.isPending ? '확인 중…' : '로그인'}
        </button>
      </form>
    </main>
  )
}
````

**`PortfolioBlog.Web/src/components/Layout.tsx`**

````tsx
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { NavLink, Outlet } from 'react-router'
import { auth } from '../api/endpoints'
import type { AuthStatus } from '../api/types'
import { ME_KEY } from '../app/queryClient'
import { ErrorNotice } from './notices'

const LINKS = [['/', '글'], ['/series', '시리즈'], ['/tags', '태그'], ['/attachments', '첨부']] as const

export function Layout() {
  const client = useQueryClient()
  const logout = useMutation({
    mutationFn: () => auth.logout(),
    onSuccess: () => {
      // "로그인 안 됨"을 기록하면 RequireAuth가 로그인 화면으로 보낸다. client.clear()는 쓰지 않는다(실측): 쿼리 객체를 통째로
      // 없애면 RequireAuth의 관찰자가 옛 객체에 매달린 채 남아 새 값을 보지 못하고 화면이 그대로 있다.
      client.setQueryData<AuthStatus>(ME_KEY, { authenticated: false })
      // 다른 사람이 같은 브라우저로 로그인했을 때 앞사람의 목록이 캐시에서 보이지 않게 나머지는 지운다. 임시본은 남긴다.
      client.removeQueries({ predicate: query => query.queryKey[0] !== ME_KEY[0] })
    },
  })
  return (
    <div className="min-h-screen">
      <header className="flex items-center gap-4 border-b px-4 py-2">
        <strong>블로그 관리</strong>
        <nav className="flex gap-3 text-sm">
          {LINKS.map(([to, label]) => (
            <NavLink key={to} to={to} end={to === '/'} className={({ isActive }) => isActive ? 'font-bold underline' : ''}>{label}</NavLink>
          ))}
        </nav>
        <span className="flex-1" />
        {/* 로그아웃은 서버의 SessionEpoch를 올린다 — 다른 기기·탭의 세션도 전부 끝난다. */}
        <button type="button" className="text-sm underline" disabled={logout.isPending} onClick={() => logout.mutate()} title="모든 기기의 세션이 함께 끝납니다">
          로그아웃
        </button>
      </header>
      {logout.isError && <div className="p-4"><ErrorNotice error={logout.error} /></div>}
      <Outlet />
    </div>
  )
}
````

**`PortfolioBlog.Web/src/pages/PostsPage.tsx`**

````tsx
import { useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { posts } from '../api/endpoints'
import type { PostSummary } from '../api/types'
import { ErrorNotice, Loading } from '../components/notices'
import { LIMITS } from '../lib/validation'
import { formatDateTime, useDebounced } from '../lib/useDebounced'

const PAGE_SIZE = 50 // 서버 기본 take

export function PostsPage() {
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(0)
  const q = useDebounced(search.trim(), 300)
  const client = useQueryClient()

  const list = useQuery({
    queryKey: ['posts', 'list', q, page],
    queryFn: ({ signal }) => posts.list(q, page * PAGE_SIZE, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  })
  const remove = useMutation({
    mutationFn: (post: PostSummary) => posts.remove(post.id, post.version),
    onSettled: () => client.invalidateQueries({ queryKey: ['posts'] }),
  })

  const confirmRemove = (post: PostSummary) => {
    // 글은 상태가 없다 — 삭제는 즉시 공개 사이트에서 사라지고 되돌릴 수 없다.
    if (window.confirm(`"${post.title}" 글을 삭제할까요?\n공개 사이트에서 즉시 사라지며 되돌릴 수 없습니다.`)) remove.mutate(post)
  }

  const total = list.data?.total ?? 0
  const lastPage = Math.max(0, Math.ceil(total / PAGE_SIZE) - 1)

  return (
    <main className="mx-auto max-w-5xl space-y-4 p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-bold">글</h1>
        <input type="search" aria-label="글 검색" placeholder="제목·요약·본문 검색" maxLength={LIMITS.queryMax} value={search}
          onChange={e => { setSearch(e.target.value); setPage(0) }} className="flex-1 rounded border p-2 text-sm" />
        <Link to="/posts/new" className="rounded bg-black px-3 py-2 text-sm text-white">새 글</Link>
      </div>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <table className="w-full text-left text-sm">
          <thead><tr className="border-b"><th className="p-2">제목</th><th className="p-2">태그</th><th className="p-2">수정</th><th /></tr></thead>
          <tbody>
            {list.data?.items.map(post => (
              <tr key={post.id} className="border-b">
                <td className="p-2"><Link to={`/posts/${post.id}`} className="font-medium underline">{post.title}</Link><div className="text-xs text-gray-500">/posts/{post.slug}</div></td>
                <td className="p-2 text-xs">{post.tags.join(', ')}</td>
                <td className="p-2 text-xs">{formatDateTime(post.updatedAt)}</td>
                <td className="p-2 text-right"><button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(post)}>삭제</button></td>
              </tr>
            ))}
            {list.data?.items.length === 0 && <tr><td colSpan={4} className="p-6 text-center text-gray-500">글이 없습니다.</td></tr>}
          </tbody>
        </table>
      )}
      <div className="flex items-center gap-3 text-sm">
        <button type="button" disabled={page === 0} onClick={() => setPage(p => p - 1)} className="underline disabled:opacity-40">이전</button>
        <span>{page + 1} / {lastPage + 1} (총 {total}건)</span>
        <button type="button" disabled={page >= lastPage} onClick={() => setPage(p => p + 1)} className="underline disabled:opacity-40">다음</button>
      </div>
    </main>
  )
}
````

**`PortfolioBlog.Web/src/pages/SeriesPage.tsx`**

````tsx
import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { series as api } from '../api/endpoints'
import { ApiError, type FieldErrors } from '../api/errors'
import type { Series, UpsertSeriesRequest } from '../api/types'
import { ErrorNotice, FieldError, Loading } from '../components/notices'
import { hasErrors, validateSeries } from '../lib/validation'

const EMPTY: UpsertSeriesRequest = { slug: '', title: '', description: '' }
const input = 'mt-1 w-full rounded border p-2 text-sm'

function SeriesForm({ initial, slugLocked, submitLabel, onSubmit, onCancel }: {
  initial: UpsertSeriesRequest; slugLocked: boolean; submitLabel: string
  onSubmit: (value: UpsertSeriesRequest) => Promise<unknown>; onCancel?: () => void
}) {
  const [value, setValue] = useState(initial)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [failure, setFailure] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  const submit = async () => {
    const local = validateSeries(value)
    setErrors(local); setFailure(null)
    if (hasErrors(local)) return
    setBusy(true)
    try { await onSubmit(value); if (!slugLocked) setValue(EMPTY) }
    catch (cause) { if (cause instanceof ApiError && cause.status === 400) setErrors(cause.fieldErrors); else setFailure(cause) }
    finally { setBusy(false) }
  }
  return (
    <div className="space-y-2 rounded border p-3">
      <label className="block text-sm">제목<input className={input} value={value.title} onChange={e => setValue({ ...value, title: e.target.value })} /><FieldError errors={errors} field="title" /></label>
      <label className="block text-sm">slug (공개 주소 /series/&lt;slug&gt; — 만든 뒤에는 바꿀 수 없습니다)
        <input className={input} value={value.slug} readOnly={slugLocked} spellCheck={false} onChange={e => setValue({ ...value, slug: e.target.value })} /><FieldError errors={errors} field="slug" /></label>
      <label className="block text-sm">설명<textarea className={input} rows={3} value={value.description} onChange={e => setValue({ ...value, description: e.target.value })} /><FieldError errors={errors} field="description" /></label>
      <ErrorNotice error={failure} />
      <div className="flex gap-3 text-sm">
        <button type="button" disabled={busy} onClick={() => void submit()} className="rounded bg-black px-3 py-1 text-white disabled:opacity-40">{submitLabel}</button>
        {onCancel && <button type="button" className="underline" onClick={onCancel}>취소</button>}
      </div>
    </div>
  )
}

export function SeriesPage() {
  const client = useQueryClient()
  const [editing, setEditing] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['series', 'list'], queryFn: ({ signal }) => api.list(signal) })
  const refresh = () => client.invalidateQueries({ queryKey: ['series'] })
  const remove = useMutation({ mutationFn: (item: Series) => api.remove(item.id), onSettled: refresh })

  const confirmRemove = (item: Series) => {
    if (window.confirm(`시리즈 "${item.title}"을(를) 삭제할까요?\n소속 글 ${item.postCount}편은 삭제되지 않고 시리즈에서만 빠집니다.`)) remove.mutate(item)
  }

  return (
    <main className="mx-auto max-w-3xl space-y-4 p-4">
      <h1 className="text-xl font-bold">시리즈</h1>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <ul className="space-y-2">
          {list.data?.map(item => (
            <li key={item.id}>
              {editing === item.id ? (
                <SeriesForm initial={{ slug: item.slug, title: item.title, description: item.description }} slugLocked submitLabel="수정 저장"
                  onSubmit={async value => { await api.update(item.id, value); setEditing(null); await refresh() }} onCancel={() => setEditing(null)} />
              ) : (
                <div className="flex items-start gap-3 rounded border p-3 text-sm">
                  <div className="flex-1"><strong>{item.title}</strong> <span className="text-xs text-gray-500">/series/{item.slug} · 글 {item.postCount}편</span>
                    <p className="whitespace-pre-wrap text-gray-700">{item.description}</p></div>
                  <button type="button" className="underline" onClick={() => setEditing(item.id)}>수정</button>
                  <button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(item)}>삭제</button>
                </div>
              )}
            </li>
          ))}
          {list.data?.length === 0 && <li className="text-sm text-gray-500">시리즈가 없습니다.</li>}
        </ul>
      )}
      <h2 className="font-bold">새 시리즈</h2>
      <SeriesForm initial={EMPTY} slugLocked={false} submitLabel="만들기" onSubmit={async value => { await api.create(value); await refresh() }} />
    </main>
  )
}
````

**`PortfolioBlog.Web/src/pages/TagsPage.tsx`**

````tsx
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { tags as api } from '../api/endpoints'
import type { Tag } from '../api/types'
import { ErrorNotice, Loading } from '../components/notices'

/** 태그는 글을 저장할 때 자동으로 만들어진다. 여기서는 쓰이지 않게 된 태그를 지우는 일만 한다. */
export function TagsPage() {
  const client = useQueryClient()
  const list = useQuery({ queryKey: ['tags', 'list'], queryFn: ({ signal }) => api.list(signal) })
  const remove = useMutation({ mutationFn: (tag: Tag) => api.remove(tag.id), onSettled: () => client.invalidateQueries({ queryKey: ['tags'] }) })

  const confirmRemove = (tag: Tag) => {
    if (window.confirm(`태그 "${tag.name}"을(를) 삭제할까요?\n이 태그가 붙은 글 ${tag.postCount}편에서 태그만 떨어집니다(글은 남습니다).`)) remove.mutate(tag)
  }

  return (
    <main className="mx-auto max-w-3xl space-y-4 p-4">
      <h1 className="text-xl font-bold">태그</h1>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <ul className="divide-y text-sm">
          {list.data?.map(tag => (
            <li key={tag.id} className="flex items-center gap-3 py-2">
              <span className="flex-1">{tag.name} <span className="text-xs text-gray-500">/tags/{tag.normalizedName} · 글 {tag.postCount}편</span></span>
              <button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(tag)}>삭제</button>
            </li>
          ))}
          {list.data?.length === 0 && <li className="py-2 text-gray-500">태그가 없습니다.</li>}
        </ul>
      )}
    </main>
  )
}
````


`src/app/routes.tsx` — 이 Task에서는 편집 화면과 첨부 화면이 아직 없다. 아래가 이 시점의 파일이다(Task 6·7에서 줄을 더한다). "새 글"·첨부 링크는 그때까지 "없는 화면"으로 간다.

**`PortfolioBlog.Web/src/app/routes.tsx`**

````tsx
import { Link, type RouteObject } from 'react-router'
import { LoginPage } from '../auth/LoginPage'
import { RequireAuth } from '../auth/RequireAuth'
import { Layout } from '../components/Layout'
import { PostsPage } from '../pages/PostsPage'
import { SeriesPage } from '../pages/SeriesPage'
import { TagsPage } from '../pages/TagsPage'


export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  {
    element: <RequireAuth />,
    children: [{
      element: <Layout />,
      children: [
        { path: '/', element: <PostsPage /> },
        { path: '/series', element: <SeriesPage /> },
        { path: '/tags', element: <TagsPage /> },
      ],
    }],
  },
  { path: '*', element: <main className="p-8 text-sm">없는 화면입니다. <Link to="/" className="underline">글 목록으로</Link></main> },
]
````

**`PortfolioBlog.Web/src/App.tsx`**

````tsx
import { useState } from 'react'
import { QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { createQueryClient } from './app/queryClient'
import { routes } from './app/routes'

export default function App() {
  const [client] = useState(createQueryClient)
  const [router] = useState(() => createBrowserRouter(routes))
  return <QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>
}
````


- [ ] **Step 4: 통과를 본다.** **규칙 8:** (a) `Layout.tsx`의 로그아웃 `onSuccess`를 `client.clear(); client.setQueryData(...)`로 바꾸면 "로그아웃하면 로그인 화면으로 간다"가 실패하는지 확인한다(S12). (b) `LoginPage`에서 `safeNext(...)`를 `params.get('next') ?? '/'`로 바꾸면 적대적 next 4건 중 실패하는 것이 있는지 확인한다 — **메모리 라우터는 교차 출처로 나가지 못하므로 `'/login'` 1건만 실패할 수 있다(추론). 실제 브라우저에서의 오픈 리다이렉트 차단 증명은 `lib.test.ts`의 `safeNext` 단위 테스트가 맡는다.** 관측한 결과를 보고서에 적는다. 둘 다 되돌린다.

- [ ] **Step 5: 손으로 확인(선택이 아니다).** 백엔드를 `Site__AdminOrigin=https://localhost:5173`로 띄우고(`README`의 로컬 실행 절에 적을 명령 — Task 8) `npm run dev` → 로그인 → 목록 → 로그아웃이 되는지 본다. 백엔드 준비가 번거로우면 Task 8의 `npm run e2e:prepare`가 만드는 환경을 먼저 당겨 써도 된다.

- [ ] **Step 6: 커밋.** `추가: 관리 화면의 뼈대 — 로그인·세션 만료 처리와 글·시리즈·태그 목록`

### Task 5: 미리보기 — sandbox iframe·CSS 스냅숏·소스 가드

**Files:**
- Create: `src/components/PreviewPane.tsx`, `public/preview/{site.css, highlight.css}`, `PortfolioBlog.Api.Tests/Infrastructure/PreviewCssSnapshotTests.cs`
- Test: `src/test/{preview.test.tsx, source-guards.test.ts}`

**Interfaces:**
- Consumes: `preview.render`, `buildPreviewDocument`, `useDebounced`, `noteAuthFailure`, `LIMITS`, `utf8ByteLength`, `ErrorNotice`. 백엔드의 `HighlightCss.Value`(공개 `/css/highlight.css`의 내용), `PortfolioBlog.Api/wwwroot/css/site.css`.
- Produces: `PreviewPane({ markdown: string })`, `PREVIEW_DEBOUNCE_MS = 500`. 미리보기 문서의 본문은 공개 글 페이지와 같은 `<main><article><div class="article-body">` 안에 들어간다(`Pages/Post.cshtml`의 구조).

- [ ] **Step 1: .NET 드리프트 테스트를 먼저 쓴다**(CRLF로 저장).

**`PortfolioBlog.Api.Tests/Infrastructure/PreviewCssSnapshotTests.cs`**

````csharp
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>관리 SPA의 미리보기 iframe이 쓰는 CSS 스냅숏(<c>PortfolioBlog.Web/public/preview/*.css</c>)이 공개 사이트의 원본과 같은지 검사한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 픽스처를 공유하지 않는다. 읽기 전용 검사는 다른 테스트와 병렬로 돌아도 안전하다.
/// 단 환경변수 <c>UPDATE_PREVIEW_SNAPSHOTS=1</c>로 돌리면 저장소의 파일을 <b>덮어쓴다</b> — 그 모드는 이 클래스만 골라 실행한다.</description></item>
/// <item><description><b>Memory Allocation:</b> CSS 두 파일(각 4KB 안팎)을 문자열로 읽는다.</description></item>
/// <item><description><b>Blocking:</b> 동기 파일 I/O. DB·네트워크·Docker를 쓰지 않는다.</description></item>
/// </list>
/// 미리보기는 관리 출처에서 뜨는데, 공개 사이트의 <c>/css/highlight.css</c>는 공개 호스트에만 매핑되고 운영에서는 Caddy가 관리 호스트의
/// <c>/api/*</c>·<c>/attachments/*</c>만 백엔드로 넘긴다. 그래서 SPA가 사본을 정적 파일로 들고 있고, 이 테스트가 사본이 낡는 것을 막는다.
/// </remarks>
public sealed class PreviewCssSnapshotTests
{
    private const string UpdateVariable = "UPDATE_PREVIEW_SNAPSHOTS";

    /// <summary><c>site.css</c> 사본이 <c>PortfolioBlog.Api/wwwroot/css/site.css</c>와 같다(줄 끝만 무시한다 — 작업 트리의 줄 끝은 git 설정에 따라 다르다).</summary>
    [Fact]
    public void SiteCss_Snapshot_MatchesThePublicStylesheet()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "PortfolioBlog.Api", "wwwroot", "css", "site.css"));
        AssertSnapshot(Path.Combine(root, "PortfolioBlog.Web", "public", "preview", "site.css"), source);
    }

    /// <summary><c>highlight.css</c> 사본이 공개 사이트가 <c>/css/highlight.css</c>로 내보내는 값(<see cref="HighlightCss.Value"/>)과 같다.</summary>
    [Fact]
    public void HighlightCss_Snapshot_MatchesTheGeneratedStylesheet() =>
        AssertSnapshot(Path.Combine(RepositoryRoot(), "PortfolioBlog.Web", "public", "preview", "highlight.css"), HighlightCss.Value);

    private static void AssertSnapshot(string snapshotPath, string expected)
    {
        var normalized = Normalize(expected);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, normalized);
            return;
        }

        Assert.True(File.Exists(snapshotPath), $"스냅숏이 없습니다: {snapshotPath}. {UpdateVariable}=1로 이 테스트 클래스를 실행해 만드세요.");
        Assert.True(normalized == Normalize(File.ReadAllText(snapshotPath)),
            $"{Path.GetFileName(snapshotPath)} 스냅숏이 원본과 다릅니다. 공개 사이트 CSS를 바꿨다면 {UpdateVariable}=1로 이 테스트 클래스를 실행해 사본을 갱신하세요.");
    }

    private static string Normalize(string css) => css.Replace("\r\n", "\n", StringComparison.Ordinal);

    // 테스트 출력 폴더(bin/Release/net10.0)에서 위로 올라가며 솔루션 파일을 찾는다. 절대 경로를 하드코딩하지 않는다(저장소 경로 규칙).
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PortfolioBlog.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("PortfolioBlog.slnx를 찾지 못했습니다.");
    }
}
````


- [ ] **Step 2: 실패를 보고, 스냅숏을 만든다.** 저장소 루트에서 `dotnet test PortfolioBlog.Api.Tests -c Release --filter "FullyQualifiedName~PreviewCssSnapshotTests"` → "스냅숏이 없습니다"로 2건 실패. PowerShell에서 `$env:UPDATE_PREVIEW_SNAPSHOTS='1'; dotnet test PortfolioBlog.Api.Tests -c Release --filter "FullyQualifiedName~PreviewCssSnapshotTests"; Remove-Item Env:UPDATE_PREVIEW_SNAPSHOTS` → 두 파일이 생긴다(계획 작성 시점: `site.css` 3,993바이트, `highlight.css` 3,503바이트). 환경변수 없이 다시 실행 → 2건 통과. **규칙 8:** `highlight.css` 끝에 주석 한 줄을 붙이면 1건이 실패하는지 확인하고 되돌린다(계획 작성 중 확인함).

- [ ] **Step 3: 실패하는 프런트 테스트를 쓴다.**

**`PortfolioBlog.Web/src/test/preview.test.tsx`**

````tsx
import { QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '../app/queryClient'
import { PREVIEW_DEBOUNCE_MS, PreviewPane } from '../components/PreviewPane'
import { stubApi } from './harness'

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

// 클라이언트는 한 번만 만든다: rerender마다 새로 만들면 PreviewPane의 effect 의존성이 바뀌어 디바운스와 무관한 요청이 나간다.
const client = createQueryClient()
const pane = (markdown: string) => <QueryClientProvider client={client}><PreviewPane markdown={markdown} /></QueryClientProvider>
const frame = () => screen.getByTitle('미리보기') as HTMLIFrameElement

describe('미리보기', () => {
  it('서버 HTML은 sandbox=""(토큰 없음) iframe의 srcdoc으로만 들어간다', async () => {
    stubApi({ 'POST /api/preview': { status: 200, body: { html: '<p id="from-server">본문</p>' } } })
    const view = render(pane('# 제목'))
    await waitFor(() => expect(frame().getAttribute('srcdoc')).toContain('<p id="from-server">본문</p>'))
    expect(frame().getAttribute('sandbox')).toBe('')
    expect(frame().getAttribute('srcdoc')).toContain(`img-src ${window.location.origin}; style-src ${window.location.origin}`)
    expect(view.container.querySelector('#from-server')).toBeNull() // 부모 문서의 DOM에는 서버 HTML이 없다
  })

  it('입력이 멎은 뒤 500ms에 한 번만 요청한다', async () => {
    vi.useFakeTimers()
    const calls = stubApi({ 'POST /api/preview': { status: 200, body: { html: '' } } })
    const view = render(pane('a'))
    await act(() => vi.advanceTimersByTimeAsync(0))
    const before = calls.length // 첫 렌더의 요청
    view.rerender(pane('ab')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS - 1))
    view.rerender(pane('abc')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS - 1))
    expect(calls.length).toBe(before)
    await act(() => vi.advanceTimersByTimeAsync(1))
    expect(calls.length).toBe(before + 1)
    expect(calls.at(-1)?.body).toEqual({ markdown: 'abc' })
  })

  it('429의 Retry-After 동안은 요청을 보내지 않고, 마지막으로 성공한 미리보기를 남긴다', async () => {
    vi.useFakeTimers()
    let limited = false
    const calls = stubApi({ 'POST /api/preview': () => limited ? { status: 429, body: { title: 'x' }, headers: { 'Retry-After': '5' } } : { status: 200, body: { html: '<p>good</p>' } } })
    const view = render(pane('one'))
    await act(() => vi.advanceTimersByTimeAsync(0))
    expect(frame().getAttribute('srcdoc')).toContain('<p>good</p>')

    limited = true
    view.rerender(pane('two')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS))
    expect(screen.getByRole('alert')).toHaveTextContent('5초')
    expect(frame().getAttribute('srcdoc')).toContain('<p>good</p>')
    const afterLimit = calls.length

    view.rerender(pane('three')); await act(() => vi.advanceTimersByTimeAsync(PREVIEW_DEBOUNCE_MS + 1000))
    expect(calls.length).toBe(afterLimit) // 대기 중에는 보내지 않는다
    limited = false
    await act(() => vi.advanceTimersByTimeAsync(5000))
    expect(calls.length).toBe(afterLimit + 1)
    expect(calls.at(-1)?.body).toEqual({ markdown: 'three' })
  })
})
````

**`PortfolioBlog.Web/src/test/source-guards.test.ts`**

````ts
// @vitest-environment node
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { describe, expect, it } from 'vitest'

// 소스 전체를 훑는 닫힌 세계 검사: "쓰지 않기로 한 것"이 코드 리뷰를 빠져나가 들어오는 것을 막는다.
const ROOT = join(__dirname, '..')

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) return name === 'test' ? [] : sources(path)
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : []
  })
}

/** 주석을 뺀다: "쓰지 말 것"을 설명하는 주석이 검사에 걸리지 않게. 줄 주석은 따옴표·콜론 바로 뒤의 //(URL·문자열)는 건드리지 않는다. */
const stripComments = (text: string) => text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1')

const FILES = sources(ROOT).map(path => ({ path: relative(ROOT, path).replaceAll('\\', '/'), text: stripComments(readFileSync(path, 'utf8')) }))
const offenders = (pattern: RegExp, allow: (path: string) => boolean = () => false) =>
  FILES.filter(f => !allow(f.path) && pattern.test(f.text)).map(f => f.path)

describe('소스 가드', () => {
  it('검사 대상이 비어 있지 않다(경로가 바뀌어 공집합으로 통과하지 않게)', () => {
    expect(FILES.length).toBeGreaterThan(15)
    expect(FILES.map(f => f.path)).toContain('components/PreviewPane.tsx')
  })

  it('서버 HTML을 React DOM에 넣는 경로가 없다', () => {
    expect(offenders(/dangerouslySetInnerHTML|\.innerHTML|\.outerHTML|insertAdjacentHTML|document\.write|DOMParser/)).toEqual([])
  })

  it('문자열을 코드로 실행하는 경로가 없다', () => {
    expect(offenders(/\beval\s*\(|new\s+Function\s*\(|set(Timeout|Interval)\s*\(\s*['"`]/)).toEqual([])
  })

  it('iframe은 PreviewPane 한 곳이고 sandbox 토큰이 비어 있다', () => {
    expect(offenders(/<iframe/)).toEqual(['components/PreviewPane.tsx'])
    const pane = FILES.find(f => f.path === 'components/PreviewPane.tsx')!.text
    expect(pane).toMatch(/<iframe[^>]*\ssandbox=""/)
    expect(pane).not.toMatch(/allow-(scripts|same-origin|forms|popups|top-navigation)/)
  })

  it('fetch는 API 클라이언트 한 곳에서만 부른다(CSRF 헤더·same-origin·redirect 거부가 빠진 호출이 생기지 않게)', () => {
    expect(offenders(/\bfetch\s*\(|XMLHttpRequest\s*\(|navigator\.sendBeacon|new\s+WebSocket|new\s+EventSource/)).toEqual(['api/client.ts'])
  })

  it('외부 출처를 가리키는 URL이 없다(CSP default-src none — 글꼴·스크립트·이미지 CDN 금지)', () => {
    expect(offenders(/["'`](https?:)?\/\/[a-z0-9]/i)).toEqual([])
  })

  it('세션·비밀번호를 브라우저 저장소에 두지 않는다: 저장소 접근은 임시본 모듈뿐이다', () => {
    expect(offenders(/localStorage|sessionStorage|indexedDB|document\.cookie/)).toEqual(['lib/drafts.ts'])
  })

  it('새 창을 여는 링크가 없다(있다면 rel="noopener noreferrer"를 강제하는 검사로 바꿀 것)', () => {
    expect(offenders(/target=["']_blank["']|window\.open\s*\(/)).toEqual([])
  })
})
````


- [ ] **Step 4: 실패를 본다.** `npx vitest run src/test/preview.test.tsx src/test/source-guards.test.ts` — 소스 가드의 "검사 대상이 비어 있지 않다"는 이 시점에 파일 수(15개 초과)로는 이미 통과할 수 있고 `components/PreviewPane.tsx` 존재 단언에서 실패한다.

- [ ] **Step 5: 구현한다.**

**`PortfolioBlog.Web/src/components/PreviewPane.tsx`**

````tsx
import { useEffect, useMemo, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { preview } from '../api/endpoints'
import { ApiError } from '../api/errors'
import { noteAuthFailure } from '../app/queryClient'
import { buildPreviewDocument } from '../lib/previewDoc'
import { useDebounced } from '../lib/useDebounced'
import { LIMITS, utf8ByteLength } from '../lib/validation'
import { ErrorNotice } from './notices'

export const PREVIEW_DEBOUNCE_MS = 500

/**
 * 공개 페이지와 같은 렌더러(/api/preview)의 결과를 sandbox="" iframe에 보여 준다.
 * - 서버 HTML은 srcDoc 문자열로만 간다. dangerouslySetInnerHTML을 쓰지 않는다.
 * - 실패해도 마지막으로 성공한 미리보기는 남긴다(입력 중 한 번의 429·503으로 화면이 비지 않게).
 * - 429·503의 Retry-After 동안은 요청을 보내지 않는다(미리보기는 전역 60회/분·동시 2 — 다른 탭과 나눠 쓴다).
 */
export function PreviewPane({ markdown }: { markdown: string }) {
  const debounced = useDebounced(markdown, PREVIEW_DEBOUNCE_MS)
  const client = useQueryClient()
  const [html, setHtml] = useState('')
  const [error, setError] = useState<unknown>(null)
  const [retryTick, setRetryTick] = useState(0)
  const blockedUntil = useRef(0)
  const tooLarge = utf8ByteLength(debounced) > LIMITS.contentMaxBytes

  useEffect(() => {
    if (tooLarge) return // 서버가 400으로 거부할 크기다. 보내지 않는다(안내는 렌더에서 파생한다)
    const wait = blockedUntil.current - Date.now()
    if (wait > 0) {
      const timer = window.setTimeout(() => setRetryTick(t => t + 1), wait)
      return () => window.clearTimeout(timer)
    }
    const controller = new AbortController()
    preview.render(debounced, controller.signal).then(
      result => { setHtml(result.html); setError(null) },
      (cause: unknown) => {
        if (controller.signal.aborted) return
        noteAuthFailure(client, cause)
        if (cause instanceof ApiError && cause.retryAfterSeconds !== null) {
          blockedUntil.current = Date.now() + cause.retryAfterSeconds * 1000
          setRetryTick(t => t + 1)
        }
        setError(cause)
      })
    return () => controller.abort()
  }, [debounced, tooLarge, retryTick, client])

  const srcDoc = useMemo(() => buildPreviewDocument(html), [html])
  return (
    <section aria-label="미리보기" className="flex h-full flex-col gap-2">
      {tooLarge
        ? <p role="alert" className="text-sm text-red-700">본문이 UTF-8 기준 {LIMITS.contentMaxBytes / 1024}KB를 넘어 미리보기를 만들 수 없습니다.</p>
        : <ErrorNotice error={error} />}
      {/* sandbox=""(토큰 없음): 스크립트·폼·팝업·같은 출처 접근이 전부 꺼진다. 토큰을 추가하지 말 것. */}
      <iframe title="미리보기" sandbox="" srcDoc={srcDoc} className="min-h-[24rem] w-full flex-1 rounded border bg-white" />
    </section>
  )
}
````


- [ ] **Step 6: 통과를 본다.** **규칙 8:** (a) `<iframe … sandbox="">`를 `sandbox="allow-scripts"`로 바꾸면 미리보기 테스트와 소스 가드가 모두 실패하는지, (b) 아무 파일에나 `dangerouslySetInnerHTML`을 쓴 줄을 넣으면 소스 가드가 그 파일 이름을 대며 실패하는지, (c) `PreviewPane`에서 `blockedUntil` 대기를 지우면 "Retry-After 동안은 요청을 보내지 않고"가 실패하는지 확인하고 되돌린다.

- [ ] **Step 7: 커밋 2개.** `테스트: 미리보기용 CSS 사본이 공개 사이트 원본에서 멀어지지 않게 고정`(C# 테스트 + 스냅숏 2개), `추가: 서버 HTML을 sandbox iframe에만 넣는 미리보기와 소스 전체 가드`

### Task 6: 글 편집 화면 — CodeMirror·검증·저장·409·임시본·이미지 업로드

**Files:**
- Create: `src/components/{MarkdownEditor.tsx, TagInput.tsx, ConflictPanel.tsx}`, `src/pages/PostEditorPage.tsx`
- Modify: `src/app/routes.tsx`(편집 화면 라우트 2개)
- Test: `src/test/editor.test.tsx`

**Interfaces:**
- Consumes: Task 2~5 전부.
- Produces: `MarkdownEditor({ initialValue, onChange, onImageFiles, ref })`(비제어 — 내용을 통째로 바꾸려면 `key`로 다시 마운트), `MarkdownEditorHandle.insertAtCursor(text)`, 편집기의 접근성 이름 `본문(마크다운)`(E2E와 단위 테스트가 이 이름으로 찾는다), `TagInput({ value, onChange, suggestions })`, `ConflictPanel({ server, mineMarkdown, onTakeServer, onKeepMine })`, `PostEditorPage`(+ `route.lazy`용 `Component` export).

- [ ] **Step 1: 실패하는 테스트를 쓴다.** 단위 테스트는 `MarkdownEditor`를 `textarea`로 바꿔 끼운다(S10). 진짜 편집기는 Task 8의 E2E가 본다.

**`PortfolioBlog.Web/src/test/editor.test.tsx`**

````tsx
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PostDetail } from '../api/types'
import { loadDraft, saveDraft } from '../lib/drafts'
import { LOGGED_IN, renderApp, stubApi } from './harness'

// CodeMirror는 jsdom에 없는 레이아웃 API에 기대므로 단위 테스트에서는 textarea로 바꾼다. 진짜 편집기는 Playwright E2E가 본다.
vi.mock('../components/MarkdownEditor', () => ({
  MarkdownEditor: ({ initialValue, onChange }: { initialValue: string; onChange: (v: string) => void }) =>
    <textarea aria-label="본문(마크다운)" defaultValue={initialValue} onChange={e => onChange(e.target.value)} />,
}))

const POST: PostDetail = {
  id: '0199aaaa-0000-7000-8000-000000000001', slug: 'hello', title: '안녕', summary: '요약', contentMarkdown: '# 서버 본문',
  tags: ['C#'], seriesId: null, seriesOrder: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z', version: 7,
}
const COMMON = { ...LOGGED_IN, 'GET /api/series': { status: 200, body: [] }, 'GET /api/tags': { status: 200, body: [] }, 'POST /api/preview': { status: 200, body: { html: '<p>ok</p>' } } }

beforeEach(() => window.localStorage.clear())
afterEach(() => vi.unstubAllGlobals())

describe('글 편집', () => {
  it('새 글: "저장하면 즉시 공개됩니다"를 보여 주고, 검증을 통과한 내용만 보낸다', async () => {
    const calls = stubApi({ ...COMMON, 'POST /api/posts': { status: 201, body: { ...POST, slug: 'new-post' } }, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    const { router } = renderApp('/posts/new')
    expect(await screen.findByText('저장하면 즉시 공개됩니다')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    expect(await screen.findByText('slug는 필수입니다.')).toBeInTheDocument()
    expect(calls.some(c => c.method === 'POST' && c.url === '/api/posts')).toBe(false) // 클라이언트 검증 실패면 보내지 않는다

    await userEvent.type(screen.getByLabelText(/^제목/), '새 글 제목')
    await userEvent.type(screen.getByLabelText(/^slug/), 'new-post')
    await userEvent.type(screen.getByLabelText('태그 추가'), 'C#{Enter}')
    fireEvent.change(screen.getByLabelText('본문(마크다운)'), { target: { value: '본문 **굵게**' } })
    await userEvent.click(screen.getByRole('button', { name: '저장' }))

    await waitFor(() => expect(router.state.location.pathname).toBe(`/posts/${POST.id}`))
    expect(calls.find(c => c.method === 'POST' && c.url === '/api/posts')?.body).toEqual({
      slug: 'new-post', title: '새 글 제목', summary: '', contentMarkdown: '본문 **굵게**', tagNames: ['C#'], seriesId: null, seriesOrder: null,
    })
    expect(loadDraft('new')).toBeNull() // 저장에 성공하면 임시본을 지운다
  })

  it('수정: slug는 읽기 전용이고, 받은 version을 그대로 돌려보낸다', async () => {
    const calls = stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST }, [`PUT /api/posts/${POST.id}`]: { status: 200, body: { ...POST, title: '바뀐 제목', version: 8 } } })
    renderApp(`/posts/${POST.id}`)
    expect(await screen.findByLabelText(/^slug/)).toHaveAttribute('readonly')
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled() // 바뀐 것이 없으면 저장하지 않는다
    await userEvent.clear(screen.getByLabelText(/^제목/))
    await userEvent.type(screen.getByLabelText(/^제목/), '바뀐 제목')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(calls.find(c => c.method === 'PUT')).toBeDefined())
    expect(calls.find(c => c.method === 'PUT')?.body).toMatchObject({ title: '바뀐 제목', version: 7, tagNames: ['C#'] })
    await waitFor(() => expect(screen.getByRole('button', { name: '저장' })).toBeDisabled()) // 저장 뒤 기준선이 갱신된다
  })

  it('서버의 400은 필드 옆에 표시한다', async () => {
    stubApi({ ...COMMON, 'POST /api/posts': { status: 400, body: { title: 'validation', errors: { contentMarkdown: ['중첩이 너무 깊습니다.'] } } } })
    renderApp('/posts/new')
    await userEvent.type(await screen.findByLabelText(/^제목/), 't')
    await userEvent.type(screen.getByLabelText(/^slug/), 's')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    expect(await screen.findByText('중첩이 너무 깊습니다.')).toBeInTheDocument()
  })

  it('409: 최신 서버본을 받아 내 본문과 나란히 보여 주고, 내 변경은 임시본에 남는다', async () => {
    const latest = { ...POST, contentMarkdown: '# 다른 탭에서 고친 본문', version: 9 }
    let gets = 0
    const calls = stubApi({
      ...COMMON,
      [`GET /api/posts/${POST.id}`]: () => ({ status: 200, body: gets++ === 0 ? POST : latest }),
      [`PUT /api/posts/${POST.id}`]: call => (call.body as { version: number }).version === 9
        ? { status: 200, body: { ...latest, contentMarkdown: '# 내 본문', version: 10 } }
        : { status: 409, body: { title: '충돌', detail: '다른 곳에서 이 글이 먼저 수정되었습니다.' } },
    })
    renderApp(`/posts/${POST.id}`)
    fireEvent.change(await screen.findByLabelText('본문(마크다운)'), { target: { value: '# 내 본문' } })
    await userEvent.click(screen.getByRole('button', { name: '저장' }))

    const panel = await screen.findByRole('alertdialog', { name: '저장 충돌' })
    expect(within(panel).getByLabelText('서버본')).toHaveValue('# 다른 탭에서 고친 본문')
    expect(within(panel).getByLabelText('내 본문')).toHaveValue('# 내 본문')
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled() // 고르기 전에는 저장할 수 없다

    await userEvent.click(within(panel).getByRole('button', { name: /내 내용 유지/ }))
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(calls.filter(c => c.method === 'PUT')).toHaveLength(2))
    expect(calls.filter(c => c.method === 'PUT')[1].body).toMatchObject({ contentMarkdown: '# 내 본문', version: 9 })
  })

  it('임시본: 자동 저장되고, 다시 열면 복원을 묻는다(자동으로 덮어쓰지 않는다)', async () => {
    stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    saveDraft(POST.id, { slug: 'hello', title: '안녕', summary: '요약', contentMarkdown: '# 임시본 본문', tagNames: ['C#'], seriesId: null, seriesOrder: null, baseVersion: 6, savedAt: '2026-09-03T00:00:00.000Z' })
    renderApp(`/posts/${POST.id}`)
    const body = await screen.findByLabelText('본문(마크다운)')
    expect(body).toHaveValue('# 서버 본문')
    expect(screen.getByText(/그 뒤에 서버본이 바뀌었습니다/)).toBeInTheDocument() // baseVersion 6 ≠ 서버 7
    expect(loadDraft(POST.id)?.contentMarkdown).toBe('# 임시본 본문')            // 고르기 전에는 임시본을 건드리지 않는다
    await userEvent.click(screen.getByRole('button', { name: '임시본 복원' }))
    expect(await screen.findByLabelText('본문(마크다운)')).toHaveValue('# 임시본 본문')
  })
})
````


- [ ] **Step 2: 실패를 본다.**

- [ ] **Step 3: 구현한다.**

**`PortfolioBlog.Web/src/components/MarkdownEditor.tsx`**

````tsx
import { useEffect, useImperativeHandle, useRef, type Ref } from 'react'
import { EditorView, basicSetup } from 'codemirror'
import { markdown } from '@codemirror/lang-markdown'

export interface MarkdownEditorHandle {
  /** 현재 커서(선택 영역) 자리에 텍스트를 넣는다. 이미지 업로드가 끝났을 때 마크다운 이미지 문법을 넣는 데 쓴다. */
  insertAtCursor: (text: string) => void
}

interface Props {
  /** 처음 문서. 이후의 변경은 onChange로만 나간다(비제어). 외부에서 내용을 통째로 바꾸려면 key를 바꿔 다시 마운트한다. */
  initialValue: string
  onChange: (value: string) => void
  /** 붙여넣기·끌어다 놓기로 들어온 이미지 파일. */
  onImageFiles: (files: File[]) => void
  ref?: Ref<MarkdownEditorHandle>
}

const imagesOf = (list: DataTransfer | null): File[] =>
  list ? Array.from(list.files).filter(file => file.type.startsWith('image/')) : []

/**
 * CodeMirror 6 마크다운 편집기. CodeMirror는 <style> 요소 하나를 문서에 주입한다(실측) — 관리 SPA의 CSP가
 * style-src-elem에 'unsafe-inline'을 두는 유일한 이유다. 스크립트 실행 경로는 없다(입력은 텍스트로만 다뤄진다).
 */
export function MarkdownEditor({ initialValue, onChange, onImageFiles, ref }: Props) {
  const host = useRef<HTMLDivElement>(null)
  const view = useRef<EditorView | null>(null)
  // 콜백은 ref로 들고 있는다: 부모가 매 렌더마다 새 함수를 줘도 편집기를 다시 만들지 않는다.
  const callbacks = useRef({ onChange, onImageFiles })
  useEffect(() => { callbacks.current = { onChange, onImageFiles } })

  useEffect(() => {
    const editor = new EditorView({
      doc: initialValue,
      parent: host.current!,
      extensions: [
        basicSetup, markdown(), EditorView.lineWrapping,
        EditorView.contentAttributes.of({ 'aria-label': '본문(마크다운)' }),
        EditorView.updateListener.of(update => { if (update.docChanged) callbacks.current.onChange(update.state.doc.toString()) }),
        EditorView.domEventHandlers({
          paste: event => {
            const files = imagesOf(event.clipboardData)
            if (files.length === 0) return false
            event.preventDefault(); callbacks.current.onImageFiles(files); return true
          },
          drop: event => {
            const files = imagesOf(event.dataTransfer)
            if (files.length === 0) return false
            event.preventDefault(); callbacks.current.onImageFiles(files); return true
          },
        }),
      ],
    })
    view.current = editor
    return () => { editor.destroy(); view.current = null }
    // initialValue는 마운트 시점 값만 쓴다(비제어). 바꾸려면 key로 다시 마운트한다.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useImperativeHandle(ref, () => ({
    insertAtCursor: (text: string) => {
      const editor = view.current
      if (!editor) return
      const { from, to } = editor.state.selection.main
      editor.dispatch({ changes: { from, to, insert: text }, selection: { anchor: from + text.length } })
      editor.focus()
    },
  }), [])

  return <div ref={host} className="min-h-[24rem] rounded border text-sm" data-testid="markdown-editor" />
}
````

**`PortfolioBlog.Web/src/components/TagInput.tsx`**

````tsx
import { useId, useState, type KeyboardEvent } from 'react'
import { displayTag } from '../lib/validation'

interface Props { value: string[]; onChange: (next: string[]) => void; suggestions: string[] }

/** 태그 칩 입력. Enter·쉼표로 추가, 대소문자만 다른 중복은 추가하지 않는다(서버도 정규화 이름으로 합친다). */
export function TagInput({ value, onChange, suggestions }: Props) {
  const [text, setText] = useState('')
  const listId = useId()

  const commit = () => {
    const tag = displayTag(text)
    setText('')
    if (tag.length === 0 || value.some(existing => existing.toLowerCase() === tag.toLowerCase())) return
    onChange([...value, tag])
  }
  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // 한글 조합 중의 Enter는 조합 확정이다 — 태그 추가로 처리하지 않는다.
    if (event.nativeEvent.isComposing) return
    if (event.key === 'Enter' || event.key === ',') { event.preventDefault(); commit() }
    else if (event.key === 'Backspace' && text.length === 0 && value.length > 0) onChange(value.slice(0, -1))
  }

  return (
    <div className="flex flex-wrap items-center gap-1 rounded border p-1">
      {value.map(tag => (
        <span key={tag} className="flex items-center gap-1 rounded bg-gray-100 px-2 py-0.5 text-xs">
          {tag}
          <button type="button" aria-label={`태그 ${tag} 제거`} onClick={() => onChange(value.filter(t => t !== tag))}>×</button>
        </span>
      ))}
      <input aria-label="태그 추가" list={listId} value={text} onChange={e => setText(e.target.value)} onKeyDown={onKeyDown} onBlur={commit}
        placeholder="태그 입력 후 Enter" className="min-w-[8rem] flex-1 p-1 text-sm outline-none" />
      <datalist id={listId}>{suggestions.map(name => <option key={name} value={name} />)}</datalist>
    </div>
  )
}
````

**`PortfolioBlog.Web/src/components/ConflictPanel.tsx`**

````tsx
import type { PostDetail } from '../api/types'
import { formatDateTime } from '../lib/useDebounced'

interface Props {
  server: PostDetail
  mineMarkdown: string
  /** 서버본으로 바꾼다(내 변경과 임시본을 버린다). */
  onTakeServer: () => void
  /** 내 내용을 유지하고 서버의 최신 version을 바탕으로 다시 저장할 수 있게 한다(서버의 변경을 덮어쓴다). */
  onKeepMine: () => void
}

/** 409(version 불일치): 다른 탭·기기에서 먼저 저장됐다. 서버본과 내 본문을 나란히 보여 주고 사용자가 고르게 한다 — 자동 병합은 하지 않는다. */
export function ConflictPanel({ server, mineMarkdown, onTakeServer, onKeepMine }: Props) {
  return (
    <section role="alertdialog" aria-label="저장 충돌" className="space-y-3 rounded border border-amber-400 bg-amber-50 p-4 text-sm">
      <p><strong>다른 곳에서 이 글이 먼저 수정되었습니다.</strong> 서버본은 {formatDateTime(server.updatedAt)}에 저장됐습니다. 내 변경은 임시본으로 보관되어 있습니다.</p>
      <div className="grid gap-3 md:grid-cols-2">
        <label className="block"><span className="font-medium">서버본</span>
          <textarea readOnly value={server.contentMarkdown} className="mt-1 h-64 w-full rounded border bg-white p-2 font-mono text-xs" /></label>
        <label className="block"><span className="font-medium">내 본문</span>
          <textarea readOnly value={mineMarkdown} className="mt-1 h-64 w-full rounded border bg-white p-2 font-mono text-xs" /></label>
      </div>
      <div className="flex gap-3">
        <button type="button" className="rounded border px-3 py-1" onClick={onTakeServer}>서버본으로 바꾸기(내 변경 버림)</button>
        <button type="button" className="rounded border px-3 py-1" onClick={onKeepMine}>내 내용 유지(다시 저장하면 서버본을 덮어씀)</button>
      </div>
    </section>
  )
}
````

**`PortfolioBlog.Web/src/pages/PostEditorPage.tsx`**

````tsx
import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router'
import { attachments, posts, series as seriesApi, tags as tagsApi } from '../api/endpoints'
import { ApiError, type FieldErrors } from '../api/errors'
import type { PostDetail } from '../api/types'
import { noteAuthFailure } from '../app/queryClient'
import { ConflictPanel } from '../components/ConflictPanel'
import { MarkdownEditor, type MarkdownEditorHandle } from '../components/MarkdownEditor'
import { ErrorNotice, FieldError, Loading } from '../components/notices'
import { PreviewPane } from '../components/PreviewPane'
import { TagInput } from '../components/TagInput'
import { NEW_POST_KEY, clearDraft, loadDraft, sameFields, saveDraft, type Draft, type DraftFields } from '../lib/drafts'
import { altTextOf } from '../lib/markdownImage'
import { formatDateTime, useDebounced } from '../lib/useDebounced'
import { LIMITS, hasErrors, utf8ByteLength, validateImageFile, validatePost } from '../lib/validation'

const EMPTY: DraftFields = { slug: '', title: '', summary: '', contentMarkdown: '', tagNames: [], seriesId: null, seriesOrder: null }
const fromServer = (post: PostDetail): DraftFields => ({
  slug: post.slug, title: post.title, summary: post.summary, contentMarkdown: post.contentMarkdown,
  tagNames: post.tags, seriesId: post.seriesId, seriesOrder: post.seriesOrder,
})
const detailKey = (postId: string) => ['posts', 'detail', postId] as const

export function PostEditorPage() {
  const { id } = useParams()
  const postId = id ?? null
  const detail = useQuery({
    queryKey: detailKey(postId ?? NEW_POST_KEY), enabled: postId !== null,
    queryFn: ({ signal }) => posts.get(postId!, signal),
    // 편집 중에는 다시 불러오지 않는다: 입력을 덮어쓸 수 있다. 최신본은 저장 응답과 409 처리에서만 받는다.
    staleTime: Infinity, gcTime: 0,
  })
  if (postId !== null && detail.isPending) return <Loading />
  if (postId !== null && detail.isError) return <main className="p-4"><ErrorNotice error={detail.error} onRetry={() => void detail.refetch()} /> <Link to="/" className="underline">목록으로</Link></main>
  // key: /posts/new ↔ /posts/:id 사이를 오갈 때 편집 상태를 통째로 새로 만든다.
  return <Editor key={postId ?? NEW_POST_KEY} postId={postId} server={detail.data ?? null} />
}
export { PostEditorPage as Component } // react-router의 route.lazy가 찾는 이름

function Editor({ postId, server }: { postId: string | null; server: PostDetail | null }) {
  const draftKey = postId ?? NEW_POST_KEY
  const navigate = useNavigate()
  const client = useQueryClient()
  const editor = useRef<MarkdownEditorHandle>(null)

  const [baseline, setBaseline] = useState(() => ({ fields: server ? fromServer(server) : EMPTY, version: server?.version ?? null }))
  const [fields, setFields] = useState<DraftFields>(baseline.fields)
  const [editorKey, setEditorKey] = useState(0)
  const [pendingDraft, setPendingDraft] = useState<Draft | null>(() => {
    const draft = loadDraft(draftKey)
    return draft && !sameFields(draft, baseline.fields) ? draft : null
  })
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [conflict, setConflict] = useState<PostDetail | null>(null)
  const [uploadError, setUploadError] = useState<unknown>(null)
  const [uploading, setUploading] = useState(false)
  const [draftFailed, setDraftFailed] = useState(false)

  const dirty = !sameFields(fields, baseline.fields)
  const set = <K extends keyof DraftFields>(key: K, value: DraftFields[K]) => setFields(prev => ({ ...prev, [key]: value }))

  const seriesList = useQuery({ queryKey: ['series', 'list'], queryFn: ({ signal }) => seriesApi.list(signal) })
  const tagList = useQuery({ queryKey: ['tags', 'list'], queryFn: ({ signal }) => tagsApi.list(signal) })

  // 임시본 자동 저장(1초 디바운스). 복원 여부를 아직 고르지 않았으면(pendingDraft) 기존 임시본을 건드리지 않는다.
  const settled = useDebounced(fields, 1000)
  useEffect(() => {
    if (pendingDraft !== null) return
    // oxlint-disable-next-line react/set-state-in-effect
    if (sameFields(settled, baseline.fields)) { clearDraft(draftKey); setDraftFailed(false); return }
    // 저장소 쓰기(부수 효과)의 성공 여부를 화면에 알려야 한다 — 렌더 중에 파생할 수 있는 값이 아니다.
    // oxlint-disable-next-line react/set-state-in-effect
    setDraftFailed(!saveDraft(draftKey, { ...settled, baseVersion: baseline.version, savedAt: new Date().toISOString() }))
  }, [settled, baseline, pendingDraft, draftKey])

  // 임시본을 저장하지 못했는데(용량 초과 등) 바뀐 내용이 있으면 창을 닫기 전에 한 번 묻는다.
  useEffect(() => {
    if (!(dirty && draftFailed)) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty, draftFailed])

  const replaceAll = (next: DraftFields) => { setFields(next); setEditorKey(k => k + 1) } // 편집기는 비제어라 다시 마운트해야 본문이 바뀐다

  const save = useMutation({
    mutationFn: () => postId === null ? posts.create(fields) : posts.update(postId, { ...fields, version: baseline.version ?? undefined }),
    onSuccess: saved => {
      clearDraft(draftKey)
      client.setQueryData(detailKey(saved.id), saved)
      void client.invalidateQueries({ queryKey: ['posts', 'list'] })
      void client.invalidateQueries({ queryKey: ['tags'] })
      void client.invalidateQueries({ queryKey: ['series'] })
      if (postId === null) { void navigate(`/posts/${saved.id}`, { replace: true }); return }
      const next = fromServer(saved)
      setBaseline({ fields: next, version: saved.version })
      if (next.contentMarkdown === fields.contentMarkdown) setFields(next); else replaceAll(next)
    },
    onError: async error => {
      if (!(error instanceof ApiError)) return
      if (error.status === 400) setFieldErrors(error.fieldErrors)
      // 409는 본문 검증(400)보다 먼저 올 수 있다(서버는 version을 렌더보다 먼저 본다). 최신본을 받아 나란히 보여 준다.
      if (error.status === 409 && postId !== null) {
        try { setConflict(await posts.get(postId)) } catch (cause) { noteAuthFailure(client, cause) }
      }
    },
  })

  const submit = () => {
    const errors = validatePost(fields)
    setFieldErrors(errors)
    if (!hasErrors(errors)) save.mutate()
  }

  const uploadImages = async (files: File[]) => {
    setUploadError(null); setUploading(true)
    try {
      for (const file of files) { // 순차 업로드: 서버의 업로드 동시 실행 한도는 전역 2다
        const problem = validateImageFile(file)
        if (problem) { setUploadError(new ApiError(400, '올릴 수 없는 파일', problem)); continue }
        const uploaded = await attachments.upload(file, file.name || 'image.png')
        editor.current?.insertAtCursor(`![${altTextOf(uploaded.fileName)}](${uploaded.url})\n`)
      }
    } catch (cause) { noteAuthFailure(client, cause); setUploadError(cause) } finally { setUploading(false) }
  }

  const bytes = utf8ByteLength(fields.contentMarkdown)
  const input = 'mt-1 w-full rounded border p-2 text-sm'

  return (
    <main className="space-y-3 p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-bold">{postId === null ? '새 글' : '글 수정'}</h1>
        <Link to="/" className="text-sm underline">목록</Link>
        <span className="flex-1" />
        {draftFailed && <span className="text-xs text-amber-700">임시본을 저장하지 못했습니다(브라우저 저장 공간).</span>}
        {/* 글에는 초안 상태가 없다 — 저장이 곧 발행이다. */}
        <span className="text-sm font-medium text-red-700">저장하면 즉시 공개됩니다</span>
        <button type="button" onClick={submit} disabled={save.isPending || conflict !== null || (postId !== null && !dirty)}
          className="rounded bg-black px-4 py-2 text-sm text-white disabled:opacity-40">{save.isPending ? '저장 중…' : '저장'}</button>
      </div>

      {pendingDraft && (
        <div role="status" className="flex flex-wrap items-center gap-3 rounded border border-blue-300 bg-blue-50 p-3 text-sm">
          <span>{formatDateTime(pendingDraft.savedAt)}에 저장된 임시본이 있습니다.
            {pendingDraft.baseVersion !== baseline.version && ' 그 뒤에 서버본이 바뀌었습니다 — 복원 후 저장하면 서버의 변경을 덮어씁니다.'}</span>
          <button type="button" className="underline" onClick={() => { replaceAll(pendingDraft); setPendingDraft(null) }}>임시본 복원</button>
          <button type="button" className="underline" onClick={() => { clearDraft(draftKey); setPendingDraft(null) }}>버리기</button>
        </div>
      )}
      {conflict && (
        <ConflictPanel server={conflict} mineMarkdown={fields.contentMarkdown}
          onTakeServer={() => { const next = fromServer(conflict); setBaseline({ fields: next, version: conflict.version }); replaceAll(next); clearDraft(draftKey); setConflict(null); save.reset() }}
          onKeepMine={() => { setBaseline({ fields: fromServer(conflict), version: conflict.version }); setConflict(null); save.reset() }} />
      )}
      {!conflict && <ErrorNotice error={save.error instanceof ApiError && save.error.status === 400 ? null : save.error} />}

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="space-y-3">
          <label className="block text-sm">제목
            <input className={input} value={fields.title} maxLength={LIMITS.titleMax + 50} onChange={e => set('title', e.target.value)} />
            <FieldError errors={fieldErrors} field="title" /></label>
          <label className="block text-sm">slug (공개 주소 /posts/&lt;slug&gt; — 만든 뒤에는 바꿀 수 없습니다)
            <input className={input} value={fields.slug} readOnly={postId !== null} spellCheck={false} autoCapitalize="none"
              placeholder="my-first-post" onChange={e => set('slug', e.target.value)} />
            <FieldError errors={fieldErrors} field="slug" /></label>
          <label className="block text-sm">요약 (목록·검색 결과·피드에 쓰입니다)
            <textarea className={input} rows={2} value={fields.summary} onChange={e => set('summary', e.target.value)} />
            <FieldError errors={fieldErrors} field="summary" /></label>
          <div className="text-sm">태그
            <TagInput value={fields.tagNames} onChange={next => set('tagNames', next)} suggestions={tagList.data?.map(t => t.name) ?? []} />
            <FieldError errors={fieldErrors} field="tagNames" /></div>
          <div className="flex gap-3 text-sm">
            <label className="flex-1">시리즈
              <select className={input} value={fields.seriesId ?? ''} onChange={e => {
                const value = e.target.value === '' ? null : e.target.value
                setFields(prev => ({ ...prev, seriesId: value, seriesOrder: value === null ? null : prev.seriesOrder ?? 1 }))
              }}>
                <option value="">(없음)</option>
                {seriesList.data?.map(s => <option key={s.id} value={s.id}>{s.title}</option>)}
              </select></label>
            <label className="w-28">순서
              <input className={input} type="number" min={1} step={1} disabled={fields.seriesId === null} value={fields.seriesOrder ?? ''}
                onChange={e => set('seriesOrder', e.target.value === '' ? null : Number(e.target.value))} /></label>
          </div>
          <FieldError errors={fieldErrors} field="seriesOrder" />
          <FieldError errors={fieldErrors} field="seriesId" />

          <div className="flex items-center gap-3 text-sm">
            <span>본문 (마크다운)</span>
            <label className="cursor-pointer underline">이미지 올리기
              <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden
                onChange={e => { const list = Array.from(e.target.files ?? []); e.target.value = ''; if (list.length > 0) void uploadImages(list) }} /></label>
            {uploading && <span role="status">올리는 중…</span>}
            <span className="flex-1" />
            <span className={bytes > LIMITS.contentMaxBytes ? 'text-red-700' : 'text-gray-500'}>{(bytes / 1024).toFixed(1)} / {LIMITS.contentMaxBytes / 1024}KB</span>
          </div>
          <ErrorNotice error={uploadError} />
          <MarkdownEditor key={editorKey} ref={editor} initialValue={fields.contentMarkdown}
            onChange={value => set('contentMarkdown', value)} onImageFiles={files => void uploadImages(files)} />
          <FieldError errors={fieldErrors} field="contentMarkdown" />
        </div>
        <PreviewPane markdown={fields.contentMarkdown} />
      </div>
    </main>
  )
}
````


`src/app/routes.tsx`에 편집 화면을 더한다(이 시점의 파일 전체):

**`PortfolioBlog.Web/src/app/routes.tsx`**

````tsx
import { Link, type RouteObject } from 'react-router'
import { LoginPage } from '../auth/LoginPage'
import { RequireAuth } from '../auth/RequireAuth'
import { Layout } from '../components/Layout'
import { PostsPage } from '../pages/PostsPage'
import { SeriesPage } from '../pages/SeriesPage'
import { TagsPage } from '../pages/TagsPage'

// 편집 화면만 지연 로딩한다: CodeMirror가 번들의 대부분이다(실측: 전부 한 덩어리면 953KB). 로그인·목록은 가볍게 뜬다.
const editor = () => import('../pages/PostEditorPage')

export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  {
    element: <RequireAuth />,
    children: [{
      element: <Layout />,
      children: [
        { path: '/', element: <PostsPage /> },
        { path: '/posts/new', lazy: editor },
        { path: '/posts/:id', lazy: editor },
        { path: '/series', element: <SeriesPage /> },
        { path: '/tags', element: <TagsPage /> },
      ],
    }],
  },
  { path: '*', element: <main className="p-8 text-sm">없는 화면입니다. <Link to="/" className="underline">글 목록으로</Link></main> },
]
````


- [ ] **Step 4: 통과를 본다.** **규칙 8:** (a) 임시본 자동 저장 effect의 `if (pendingDraft !== null) return`을 지우면 "임시본 … 고르기 전에는 임시본을 건드리지 않는다"가 실패하는지(마운트 직후 1초 뒤 기존 임시본이 지워진다 — 테스트가 1초를 기다리지 않아 통과할 수도 있다. **통과한다면 테스트에 가짜 타이머로 1,100ms를 흘린 뒤 `loadDraft`를 다시 단언하는 줄을 더해 실패를 만든 다음 되돌린다.** 그 줄은 남긴다), (b) `update` 호출에서 `version`을 빼면 "받은 version을 그대로 돌려보낸다"가 실패하는지 확인한다.

- [ ] **Step 5: 번들 확인.** `npm run build` → `PostEditorPage-*.js`가 따로 나오고 본체(`index-*.js`)가 400KB 안쪽인지 본다(계획 작성 시점: 371KB / 618KB). 본체가 CodeMirror를 끌어안았다면 `pages/PostEditorPage`를 정적으로 import한 곳을 찾는다(계획 작성 중 `AttachmentsPage`가 `altTextOf`를 편집 화면에서 import해 그렇게 됐었다 — 그래서 `lib/markdownImage.ts`로 뺐다).

- [ ] **Step 6: 커밋.** `추가: 글 편집 화면 — 저장 즉시 공개 안내·409 나란히 비교·임시본 복원 확인`

### Task 7: 첨부 화면

**Files:**
- Create: `src/pages/AttachmentsPage.tsx`
- Modify: `src/app/routes.tsx`(첨부 라우트)
- Test: `src/test/attachments.test.tsx`

- [ ] **Step 1: 실패하는 테스트를 쓴다.**

**`PortfolioBlog.Web/src/test/attachments.test.tsx`**

````tsx
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { altTextOf } from '../lib/markdownImage'
import { LOGGED_IN, renderApp, stubApi } from './harness'

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

const ITEM = { id: 'a1', url: '/attachments/a1/%EA%B7%B8%EB%A6%BC%20%281%29.png', fileName: '그림 (1).png', contentType: 'image/png', sizeBytes: 2048, sha256: 'x', createdAt: '2026-09-01T00:00:00Z' }

describe('첨부', () => {
  it.each([
    ['그림 (1).png', '그림 1'], ['a[b]c.webp', 'abc'], ['.png', 'image'], ['no-extension', 'no-extension'],
    ['x'.repeat(150) + '.png', 'x'.repeat(100)], ['줄' + String.fromCharCode(10) + '바꿈.png', '줄바꿈'],
  ])('대체 텍스트는 마크다운 문법을 깨는 문자를 뺀다: %s', (fileName, expected) => expect(altTextOf(fileName)).toBe(expected))

  it('목록을 그리고, 삭제는 확인을 거친다', async () => {
    const calls = stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [ITEM], total: 1 } }, 'DELETE /api/attachments/a1': { status: 204 } })
    vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    renderApp('/attachments')
    expect(await screen.findByRole('img', { name: '그림 (1).png' })).toHaveAttribute('src', ITEM.url)
    expect(screen.getByText(/2\.0KB/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    expect(calls.some(c => c.method === 'DELETE')).toBe(false)
    await userEvent.click(screen.getByRole('button', { name: '삭제' }))
    await waitFor(() => expect(calls.some(c => c.method === 'DELETE' && c.url === '/api/attachments/a1')).toBe(true))
  })

  it('이미지가 아닌 파일은 서버로 보내지 않고 이유를 알린다', async () => {
    const calls = stubApi({ ...LOGGED_IN, 'GET /api/attachments': { status: 200, body: { items: [], total: 0 } } })
    const view = renderApp('/attachments')
    await screen.findByText('첨부가 없습니다.')
    const input = view.container.querySelector('input[type=file]') as HTMLInputElement
    // applyAccept: false — accept 속성은 파일 선택 창의 힌트일 뿐이다. 끌어다 놓기·이름 바꾸기로 들어오는 파일을 흉내 낸다.
    await userEvent.upload(input, new File(['<svg/>'], 'x.svg', { type: 'image/svg+xml' }), { applyAccept: false })
    expect(await screen.findByRole('alert')).toHaveTextContent('PNG·JPEG·GIF·WebP')
    expect(calls.some(c => c.method === 'POST')).toBe(false)
  })
})
````


- [ ] **Step 2: 실패를 본다**(`altTextOf` 6건은 Task 3의 구현으로 이미 통과한다 — 나머지 2건이 실패해야 한다).

- [ ] **Step 3: 구현한다.**

**`PortfolioBlog.Web/src/pages/AttachmentsPage.tsx`**

````tsx
import { useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { attachments as api } from '../api/endpoints'
import { ApiError } from '../api/errors'
import type { Attachment } from '../api/types'
import { ErrorNotice, Loading } from '../components/notices'
import { formatBytes, formatDateTime } from '../lib/useDebounced'
import { validateImageFile } from '../lib/validation'
import { altTextOf } from '../lib/markdownImage'

const PAGE_SIZE = 50

export function AttachmentsPage() {
  const client = useQueryClient()
  const [page, setPage] = useState(0)
  const [copied, setCopied] = useState<string | null>(null)
  const list = useQuery({
    queryKey: ['attachments', 'list', page], placeholderData: keepPreviousData,
    queryFn: ({ signal }) => api.list(page * PAGE_SIZE, PAGE_SIZE, signal),
  })
  const refresh = () => client.invalidateQueries({ queryKey: ['attachments'] })
  const remove = useMutation({ mutationFn: (item: Attachment) => api.remove(item.id), onSettled: refresh })
  const upload = useMutation({
    mutationFn: async (files: File[]) => {
      for (const file of files) { // 순차: 서버의 업로드 동시 실행 한도는 전역 2
        const problem = validateImageFile(file)
        if (problem) throw new ApiError(400, '올릴 수 없는 파일', `${file.name}: ${problem}`)
        await api.upload(file, file.name || 'image.png')
      }
    },
    onSettled: refresh,
  })

  const confirmRemove = (item: Attachment) => {
    // 서버는 글↔첨부 참조를 추적하지 않는다. 지우면 그 이미지를 쓰는 글에서 깨진 이미지가 된다.
    if (window.confirm(`"${item.fileName}"을(를) 삭제할까요?\n이 이미지를 쓰는 글이 있으면 그 글에서 이미지가 깨집니다. 이미 받아 간 브라우저 캐시의 사본은 회수되지 않습니다.`)) remove.mutate(item)
  }
  const copyMarkdown = async (item: Attachment) => {
    try { await navigator.clipboard.writeText(`![${altTextOf(item.fileName)}](${item.url})`); setCopied(item.id) } catch { setCopied(null) }
  }

  const total = list.data?.total ?? 0
  const lastPage = Math.max(0, Math.ceil(total / PAGE_SIZE) - 1)

  return (
    <main className="mx-auto max-w-5xl space-y-4 p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-bold">첨부</h1>
        <label className="cursor-pointer rounded bg-black px-3 py-2 text-sm text-white">이미지 올리기
          <input type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple hidden
            onChange={e => { const files = Array.from(e.target.files ?? []); e.target.value = ''; if (files.length > 0) upload.mutate(files) }} /></label>
        {upload.isPending && <span role="status" className="text-sm">올리는 중…</span>}
      </div>
      <p className="text-xs text-gray-600">올린 이미지는 글에 넣지 않아도 주소를 아는 사람은 볼 수 있습니다. 메타데이터(EXIF·GPS)는 서버가 제거합니다.</p>
      <ErrorNotice error={list.error} onRetry={() => void list.refetch()} />
      <ErrorNotice error={upload.error} />
      <ErrorNotice error={remove.error} />
      {list.isPending ? <Loading /> : (
        <ul className="grid grid-cols-2 gap-3 md:grid-cols-4">
          {list.data?.items.map(item => (
            <li key={item.id} className="space-y-1 rounded border p-2 text-xs">
              {/* 같은 출처의 /attachments 경로다(CSP img-src 'self'). 응답은 nosniff + sandbox CSP로 온다. */}
              <img src={item.url} alt={item.fileName} loading="lazy" className="h-32 w-full rounded bg-gray-50 object-contain" />
              <div className="truncate" title={item.fileName}>{item.fileName}</div>
              <div className="text-gray-500">{formatBytes(item.sizeBytes)} · {formatDateTime(item.createdAt)}</div>
              <div className="flex gap-2">
                <button type="button" className="underline" onClick={() => void copyMarkdown(item)}>{copied === item.id ? '복사됨' : '마크다운 복사'}</button>
                <button type="button" className="text-red-700 underline" disabled={remove.isPending} onClick={() => confirmRemove(item)}>삭제</button>
              </div>
            </li>
          ))}
          {list.data?.items.length === 0 && <li className="col-span-full text-sm text-gray-500">첨부가 없습니다.</li>}
        </ul>
      )}
      <div className="flex items-center gap-3 text-sm">
        <button type="button" disabled={page === 0} onClick={() => setPage(p => p - 1)} className="underline disabled:opacity-40">이전</button>
        <span>{page + 1} / {lastPage + 1} (총 {total}건)</span>
        <button type="button" disabled={page >= lastPage} onClick={() => setPage(p => p + 1)} className="underline disabled:opacity-40">다음</button>
      </div>
    </main>
  )
}
````


`src/app/routes.tsx` 최종:

**`PortfolioBlog.Web/src/app/routes.tsx`**

````tsx
import { Link, type RouteObject } from 'react-router'
import { LoginPage } from '../auth/LoginPage'
import { RequireAuth } from '../auth/RequireAuth'
import { Layout } from '../components/Layout'
import { AttachmentsPage } from '../pages/AttachmentsPage'
import { PostsPage } from '../pages/PostsPage'
import { SeriesPage } from '../pages/SeriesPage'
import { TagsPage } from '../pages/TagsPage'

// 편집 화면만 지연 로딩한다: CodeMirror가 번들의 대부분이다(실측: 전부 한 덩어리면 953KB). 로그인·목록은 가볍게 뜬다.
const editor = () => import('../pages/PostEditorPage')

export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  {
    element: <RequireAuth />,
    children: [{
      element: <Layout />,
      children: [
        { path: '/', element: <PostsPage /> },
        { path: '/posts/new', lazy: editor },
        { path: '/posts/:id', lazy: editor },
        { path: '/series', element: <SeriesPage /> },
        { path: '/tags', element: <TagsPage /> },
        { path: '/attachments', element: <AttachmentsPage /> },
      ],
    }],
  },
  { path: '*', element: <main className="p-8 text-sm">없는 화면입니다. <Link to="/" className="underline">글 목록으로</Link></main> },
]
````


- [ ] **Step 4: 통과를 본다.** 전체 `npm test` → **110개 통과**(계획 작성 시점 값 — 실제 수를 보고한다). **규칙 8:** `AttachmentsPage`의 업로드에서 `validateImageFile` 검사를 지우면 "이미지가 아닌 파일은 서버로 보내지 않고"가 실패하는지(`stubApi`가 예상하지 못한 `POST`로 던진다) 확인한다.

- [ ] **Step 5: 커밋.** `추가: 첨부 화면 — 목록·마크다운 복사·삭제 전 경고`

### Task 8: 실제 백엔드 E2E·CI·문서

**Files:**
- Create: `PortfolioBlog.Web/playwright.config.ts`, `PortfolioBlog.Web/e2e/admin.spec.ts`
- Modify: `.github/workflows/ci.yml`(`web-e2e` 잡), `README.md`, `plan/tech_blog_0920.md`(3.6·3.9·5절·6절·8절), `CLAUDE.md`·`AGENTS.md`(구성 절), `plan/resume_guide_0921.md`

- [ ] **Step 1: E2E 구성과 시나리오.**

**`PortfolioBlog.Web/playwright.config.ts`**

````ts
import { existsSync, mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, devices } from '@playwright/test'

// 실제 백엔드(PortfolioBlog.Api + PostgreSQL)와 production 빌드(`vite preview`, 실제 보안 헤더)를 띄워 브라우저로 검사한다.
// 먼저 `npm run e2e:prepare`가 .e2e/env.json(버려질 비밀번호·해시)을 만들어야 한다.
const web = dirname(fileURLToPath(import.meta.url))
const api = process.env.BLOG_API_DIR ?? join(web, '..', 'PortfolioBlog.Api')
const envFile = join(web, '.e2e', 'env.json')
if (!existsSync(envFile)) throw new Error('먼저 `npm run e2e:prepare`를 실행하세요.')
const prepared = JSON.parse(readFileSync(envFile, 'utf8')) as { port: string; pgPassword: string; adminPassword: string; hash: string }

export const SPA_ORIGIN = 'https://localhost:4173'
const API_ORIGIN = 'https://localhost:7198'
process.env.E2E_ADMIN_PASSWORD = prepared.adminPassword // 테스트 워커가 읽는다(파일에 다시 쓰지 않는다)

// 연결 문자열은 조각으로 조립한다(저장소의 비밀값 스캐너는 한 줄짜리 연결 문자열 리터럴을 막는다).
const connection = ['Host=localhost', `Port=${prepared.port}`, 'Database=blog_e2e', 'Username=postgres', `Password=${prepared.pgPassword}`].join(';')

export default defineConfig({
  testDir: 'e2e',
  timeout: 90_000,
  fullyParallel: false, // 두 브라우저가 같은 DB를 쓴다. 시나리오는 브라우저 이름을 slug에 넣어 서로 부딪히지 않게 한다
  workers: 1,
  retries: 0,           // 재시도로 간헐 실패를 가리지 않는다
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: { baseURL: SPA_ORIGIN, ignoreHTTPSErrors: true, trace: 'retain-on-failure' },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } }, // 미리보기 CSP의 'self' 문제는 Firefox에서만 드러났다
  ],
  webServer: [
    {
      command: `dotnet run --project "${api}" -c Release --no-launch-profile`,
      url: `${API_ORIGIN}/health`, ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: API_ORIGIN,
        ConnectionStrings__Default: connection,
        Site__PublicOrigin: API_ORIGIN,
        Site__AdminOrigin: SPA_ORIGIN, // Origin 검사의 기준 — SPA가 뜨는 출처와 같아야 변경 요청이 403이 되지 않는다
        Admin__PasswordHash: prepared.hash,
        Admin__AllowedCidrs: '127.0.0.1/32 ::1/128',
        Attachments__RootPath: mkdtempSync(join(tmpdir(), 'pb-e2e-attachments-')),
      },
    },
    { command: 'npm run build && npm run preview', url: SPA_ORIGIN, ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 180_000 },
  ],
})
````

**`PortfolioBlog.Web/e2e/admin.spec.ts`**

````ts
import { expect, test, type Page } from '@playwright/test'

// production 빌드 + 실제 보안 헤더 + 실제 백엔드. 단위 테스트가 볼 수 없는 것만 본다:
// 진짜 CodeMirror, 진짜 CSP, 진짜 쿠키·Origin 검사, 진짜 sandbox iframe.
const PASSWORD = process.env.E2E_ADMIN_PASSWORD!
// 1x1 PNG
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')

/** CSP 위반과 콘솔 오류를 모은다. 위반은 문서 이벤트로, 콘솔은 브라우저가 찍는 "Refused to…/blocked" 문구로 잡는다(iframe 안의 위반은 콘솔에만 나온다). */
async function watch(page: Page) {
  const problems: string[] = []
  page.on('console', message => {
    const text = message.text()
    // Chromium은 4xx 응답마다 "Failed to load resource"를 오류로 찍는다 — 이 시나리오의 409는 의도한 것이므로 뺀다(CSP 문구는 다르다).
    if (/^Failed to load resource/.test(text)) return
    // Playwright의 addInitScript는 모든 프레임에 주입된다. sandbox="" 미리보기 프레임에서는 그 주입이 막히고 Chromium이 이 문구를 찍는다(실측) —
    // 앱의 스크립트가 아니라 이 테스트의 스크립트이며, 샌드박스가 동작한다는 증거다.
    if (/^Blocked script execution in 'about:srcdoc'/.test(text)) return
    if (message.type() === 'error' || /Content[- ]Security[- ]Policy|Refused to/i.test(text)) problems.push(`console: ${text.slice(0, 300)}`)
  })
  page.on('pageerror', error => problems.push(`pageerror: ${error.message}`))
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation', e => {
    const w = window as unknown as { __csp?: string[] }
    ;(w.__csp ??= []).push(`${e.violatedDirective} ${e.blockedURI}`)
  }))
  return async () => [...problems, ...await page.evaluate(() => (window as unknown as { __csp?: string[] }).__csp ?? [])]
}

async function login(page: Page) {
  await page.getByLabel('비밀번호').fill(PASSWORD)
  await page.getByRole('button', { name: '로그인' }).click()
}

test('문서 응답에 배포될 보안 헤더가 붙는다', async ({ request }) => {
  const response = await request.get('/')
  const csp = response.headers()['content-security-policy']
  expect(csp).toContain("default-src 'none'")
  expect(csp).toContain("script-src 'self'")
  expect(csp).not.toContain("script-src 'self' 'unsafe")
  expect(csp).toContain("style-src-attr 'none'")
  expect(response.headers()['x-content-type-options']).toBe('nosniff')
})

test('세션이 없으면 API는 401이고, CSRF 헤더가 없으면 403이다(화면을 우회해도 서버가 막는다)', async ({ request }) => {
  expect((await request.get('/api/posts', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })).status()).toBe(401)
  expect((await request.get('/api/posts')).status()).toBe(403)
})

test('글쓰기 전 과정: 로그인 → 시리즈 → 새 글(편집기·이미지·미리보기) → 충돌 → 삭제 → 로그아웃', async ({ page, context, browser, browserName }) => {
  const problems = await watch(page)
  const slug = `e2e-${browserName}-${Date.now()}`

  // 로그인: 보호된 경로로 들어가면 로그인 화면을 거쳐 원래 경로로 돌아온다.
  await page.goto('/series')
  await expect(page.getByRole('heading', { name: '관리자 로그인' })).toBeVisible()
  await login(page)
  await expect(page.getByRole('heading', { name: '시리즈', exact: true })).toBeVisible()
  const session = (await context.cookies()).find(c => c.name === '__Host-AdminSession')
  expect(session).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict', path: '/' })
  expect(await page.evaluate(() => document.cookie)).toBe('') // HttpOnly: 스크립트는 세션을 볼 수 없다

  // 시리즈 만들기
  await page.getByLabel(/^제목/).last().fill(`E2E 시리즈 ${browserName}`)
  await page.getByLabel(/^slug/).last().fill(`${slug}-series`)
  await page.getByRole('button', { name: '만들기' }).click()
  await expect(page.getByText(`/series/${slug}-series`)).toBeVisible()

  // 새 글
  await page.getByRole('link', { name: '글', exact: true }).click()
  await page.getByRole('link', { name: '새 글' }).click()
  await expect(page.getByText('저장하면 즉시 공개됩니다')).toBeVisible()
  await page.getByLabel(/^제목/).fill(`E2E 글 ${browserName}`)
  await page.getByLabel(/^slug/).fill(slug)
  await page.getByLabel('태그 추가').fill('E2E'); await page.keyboard.press('Enter')
  await page.getByLabel(/^시리즈/).selectOption({ label: `E2E 시리즈 ${browserName}` })
  const editor = page.getByLabel('본문(마크다운)')
  await editor.click()
  await page.keyboard.type('# 제목\n\n```csharp\nvar x = 1;\n```\n\n<script>window.__pwned = 1</script>\n\n')

  // 이미지 올리기 → 편집기에 마크다운이 들어간다
  await page.locator('input[type=file]').setInputFiles({ name: '그림 (1).png', mimeType: 'image/png', buffer: PNG })
  await expect(editor).toContainText('](/attachments/')

  // 미리보기: sandbox="" iframe 안에 공개 사이트 CSS·강조·이미지가 실제로 적용된다. 스크립트는 없다.
  const frameElement = page.getByTitle('미리보기')
  await expect(frameElement).toHaveAttribute('sandbox', '')
  const frame = page.frameLocator('iframe[title="미리보기"]')
  await expect(frame.locator('h1')).toHaveText('제목')
  await expect(frame.locator('pre span.keyword').first()).toHaveText('var')
  await expect(frame.locator('script')).toHaveCount(0)
  const inFrame = page.frames().find(f => f !== page.mainFrame())!
  await expect.poll(() => inFrame.evaluate(() => {
    const img = document.querySelector('img')
    return { sheets: document.styleSheets.length, image: img ? img.naturalWidth : -1 }
  })).toEqual({ sheets: 2, image: 1 })
  expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined()

  // 저장 → 편집 주소로 바뀐다
  await page.getByRole('button', { name: '저장' }).click()
  await expect(page).toHaveURL(/\/posts\/[0-9a-f-]{36}$/)
  await expect(page.getByLabel(/^slug/)).toHaveAttribute('readonly', '')
  const postUrl = page.url()

  // 다른 탭에서 먼저 고친다 → 이 탭의 저장은 409 → 나란히 비교 → 내 내용으로 다시 저장
  const other = await (await browser.newContext({ ignoreHTTPSErrors: true, storageState: await context.storageState() })).newPage()
  await other.goto(postUrl)
  await other.getByLabel(/^제목/).fill(`다른 탭이 고친 제목 ${browserName}`)
  // 응답을 기다린다: 저장 버튼은 요청 중에도 disabled라서 "disabled가 됐다"로는 저장이 끝났는지 알 수 없다(실측: Chromium에서
  // 요청이 끝나기 전에 컨텍스트를 닫아 PUT이 취소됐고, 그래서 409가 나지 않았다).
  await Promise.all([
    other.waitForResponse(r => r.request().method() === 'PUT' && r.status() === 200),
    other.getByRole('button', { name: '저장' }).click(),
  ])
  await other.context().close()

  await page.getByLabel(/^요약/).fill('이 탭에서 쓴 요약')
  await page.getByRole('button', { name: '저장' }).click()
  const conflict = page.getByRole('alertdialog', { name: '저장 충돌' })
  await expect(conflict).toBeVisible()
  await conflict.getByRole('button', { name: /내 내용 유지/ }).click()
  await Promise.all([
    page.waitForResponse(r => r.request().method() === 'PUT' && r.status() === 200),
    page.getByRole('button', { name: '저장' }).click(),
  ])

  // 목록에서 찾고 지운다
  await page.getByRole('link', { name: '목록' }).click()
  await page.getByLabel('글 검색').fill(slug)
  const row = page.getByRole('row').filter({ hasText: `/posts/${slug}` })
  await expect(row).toBeVisible()
  page.once('dialog', dialog => void dialog.accept())
  await row.getByRole('button', { name: '삭제' }).click()
  await expect(row).toHaveCount(0)

  // 로그아웃 → 보호된 화면은 다시 로그인으로
  await page.getByRole('button', { name: '로그아웃' }).click()
  await expect(page.getByRole('heading', { name: '관리자 로그인' })).toBeVisible()

  expect(await problems()).toEqual([])
})
````


- [ ] **Step 2: 실행.** Docker Desktop을 켠 상태에서 `npx playwright install chromium firefox` → `npm run e2e:prepare`(개발 인증서·스크래치 PostgreSQL `pb-e2e-pg`·버려질 비밀번호 해시) → `npm run e2e` → **6개 통과**(Chromium 3 + Firefox 3, 계획 작성 시점 약 20초). **두 번 연속** 돌려 둘 다 통과하는지 본다. 끝나면 `docker rm -f pb-e2e-pg`.
  - 이 PC의 AdGuard는 평문 HTTP를 변조한다 — E2E는 전부 HTTPS라 영향이 없다. 손으로 확인할 때도 `https://`로 연다.
  - 포트 7198·4173·5433이 비어 있어야 한다(`reuseExistingServer: false`).

- [ ] **Step 3: 규칙 8 — E2E가 실패할 수 있음을 확인한다**(하나씩 바꾸고, 실패를 보고, 되돌린다).
  1. `src/lib/previewDoc.ts`의 CSP를 스펙 원문(`img-src 'self'; style-src 'self'`)으로 바꾼다 → **Firefox만** `{ sheets: 2, image: 1 }` 단언에서 실패해야 한다(S7).
  2. `admin-headers.ts`에서 `style-src-elem`의 `'unsafe-inline'`을 지운다 → 두 브라우저 모두 마지막 `problems()` 단언이 CSP 위반으로 실패해야 한다(S8).
  3. `playwright.config.ts`의 `Site__AdminOrigin`을 `API_ORIGIN`으로 바꾼다 → 로그인(POST)이 403이 되어 실패해야 한다(S4).

- [ ] **Step 4: CI `web-e2e` 잡.** Linux에서 `dotnet dev-certs https`의 PEM 내보내기는 **측정하지 않았다** — 첫 CI 실행이 게이트다. 실패하면 원인을 보고하고, `scripts/e2e-prepare.mjs`의 `exportCerts`에 `openssl req -x509 -newkey rsa:2048 -nodes -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost"`로 Vite용 자체 서명 인증서를 만드는 Linux 분기를 더한다(백엔드의 Kestrel 인증서와 Vite의 인증서는 같을 필요가 없다 — 프록시는 `secure: false`, Playwright는 `ignoreHTTPSErrors`).

```yaml
  web-e2e:
    runs-on: ubuntu-latest
    needs: [test, web]
    services:
      postgres:
        image: postgres:17-alpine
        env:
          POSTGRES_PASSWORD: e2e-ci-dummy
          POSTGRES_DB: blog_e2e
        ports:
          - 5433:5432
        options: >-
          --health-cmd "pg_isready -U postgres -d blog_e2e"
          --health-interval 5s --health-timeout 5s --health-retries 12
    env:
      E2E_SKIP_DOCKER: '1'
      E2E_PG_PORT: '5433'
      E2E_PG_PASSWORD: e2e-ci-dummy
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - uses: actions/setup-node@v4
        with:
          node-version: '24'
          cache: npm
          cache-dependency-path: PortfolioBlog.Web/package-lock.json
      - name: Build API
        run: dotnet build PortfolioBlog.Api --configuration Release
      - name: Install web dependencies
        working-directory: PortfolioBlog.Web
        run: npm ci
      - name: Install browsers
        working-directory: PortfolioBlog.Web
        run: npx playwright install --with-deps chromium firefox
      - name: Prepare (dev cert, password hash)
        working-directory: PortfolioBlog.Web
        run: npm run e2e:prepare
      - name: E2E
        working-directory: PortfolioBlog.Web
        run: npm run e2e
      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: playwright-traces
          path: PortfolioBlog.Web/test-results
          retention-days: 7
```

- [ ] **Step 5: 문서.** 코드에서 확인한 사실만 적는다.
  - `plan/tech_blog_0920.md`:
    - 3.6 "관리 SPA (Caddy)" 행의 CSP를 `admin-headers.ts`의 값으로 바꾸고 "정본은 `PortfolioBlog.Web/admin-headers.ts`, E2E가 Chromium·Firefox에서 위반 0건을 검사한다"를 적는다.
    - 3.6 "미리보기 iframe" 행을 `default-src 'none'; img-src <관리 origin>; style-src <관리 origin>; base-uri 'none'; form-action 'none'`으로 바꾸고 "`'self'`는 쓰지 않는다 — Firefox는 `about:srcdoc`의 `'self'`를 부모 출처로 보지 않는다(실측)"를 적는다.
    - 3.9: react-router 8, `/tags` 라우트 추가, 개발 서버는 HTTPS(`https://localhost:5173`) + 프록시 대상 `https://localhost:7198` + 백엔드 `Site__AdminOrigin` 재정의, 미리보기 CSS는 스냅숏 + .NET 드리프트 테스트, 미리보기는 `Retry-After` 동안 멈춤.
    - 5절의 3·4단계 행(CI의 web 잡이 3단계로 당겨짐), 6절의 검증 명령(`npm ci; npm run lint; npm run typecheck; npm test; npm run build; npm run e2e:prepare; npm run e2e`), 8절 Plan 3 행.
  - `README.md`: "관리 SPA" 절 — 요구 도구(Node 24), `npm ci`, `npm run certs`, 백엔드를 SPA 출처로 띄우는 명령(PowerShell: `$env:Site__AdminOrigin='https://localhost:5173'; dotnet run --project PortfolioBlog.Api --launch-profile https`), `npm run dev`, 테스트·E2E 실행법, 공개 사이트 CSS를 바꾸면 스냅숏을 갱신해야 한다는 것(`UPDATE_PREVIEW_SNAPSHOTS=1`), 테스트 개수 갱신.
  - `CLAUDE.md`·`AGENTS.md` 구성 절의 `PortfolioBlog.Web` 줄에서 "(예정, 3단계)"를 빼고 실제 구성(스크립트, E2E, `admin-headers.ts`가 CSP 정본)을 적는다. CI 설명에 `web`·`web-e2e` 잡을 더한다. 두 파일을 함께 고친다. `pwsh scripts/harness-audit.ps1` → PASS 8/8.
  - `plan/resume_guide_0921.md`: 상태 표(3단계 완료 처리는 병합 뒤 컨트롤러가 한다 — 이 Task에서는 함정 표에 S7·S9·S12와 "E2E에서 저장 버튼의 disabled로 저장 완료를 판단하지 말 것"을 더한다).

- [ ] **Step 6: 전체 검증.** 저장소 루트: `dotnet build PortfolioBlog.slnx -c Release`(경고 0) · `dotnet test PortfolioBlog.slnx -c Release`(기준선 589 + 2 = **591개**). `PortfolioBlog.Web`: `npm run lint && npm run typecheck && npm test && npm run build`.

- [ ] **Step 7: 커밋 2~3개.** `테스트: 실제 백엔드와 배포용 CSP 아래에서 글쓰기 전 과정을 두 브라우저로 검증`, `문서: 관리 SPA의 실측 결과를 스펙·README·프로젝트 규칙에 반영`

---

## 실행 순서와 최종 리뷰

Task 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 순서로, 한 번에 하나씩 한다(Task 4 이후는 `routes.tsx`·`harness.tsx`를 공유한다). 작업별 리뷰 모델: Task 2·3·5·8은 보안 판단이 걸려 있으므로 상위 모델, 나머지는 표준 모델.

**최종 브랜치 리뷰(최상위 모델)가 할 일** — 2B에서 TestServer 스위트가 놓친 결함 3건을 실제 호스트 공격이 찾았다. 이번에도 실제로 띄워 찔러 본다:

1. `npm run e2e:prepare` 환경에서 production 빌드를 `vite preview`로 띄우고 **손으로·스크립트로** 공격한다: 적대적 제목·태그·파일 이름(`<img onerror>`, `"><script>`, `javascript:` 링크가 든 본문)으로 글을 만들어 목록·편집·미리보기·첨부 화면에서 마크업으로 해석되는 곳이 있는지, 미리보기 iframe 밖으로 새는 것이 있는지.
2. `?next=`에 넣을 수 있는 모든 변형(`/%2F/evil.test`, `/\t/evil.test`, 백슬래시·인코딩 조합)으로 실제 브라우저에서 교차 출처 이동이 일어나는지.
3. 개발자 도구로 `sandbox` 속성을 지웠을 때 남는 방어가 무엇인지(문서 안 CSP `default-src 'none'`) 확인하고 기록한다.
4. 세션 만료·로그아웃 뒤 브라우저 뒤로 가기·캐시로 이전 화면의 데이터가 보이는지.
5. 두 탭에서 같은 글을 고칠 때(409), 업로드 중 로그아웃, 미리보기 429 중 저장 — 상태가 꼬이는 조합.
6. `dist/` 산출물: 소스맵이 없는지, `.certs`·`.e2e`·테스트 파일이 섞이지 않았는지, 인라인 스크립트가 없는지, 외부 URL 문자열이 번들에 있는지(`grep -E "https?://" dist/assets/*.js`의 결과를 분류한다 — React·CodeMirror의 문서 링크 문자열은 나올 수 있다).
7. `npm audit`(dev 포함) 결과와 `package-lock.json`의 레지스트리 출처(전부 `registry.npmjs.org`인지).
8. 접근성의 최소선: 키보드만으로 로그인 → 새 글 → 저장이 되는지, 오류가 `role="alert"`로 읽히는지.

## 다루지 않는 것

- **배포:** Caddyfile의 `header` 지시문, `PortfolioBlog.Web/Dockerfile`, 운영 도메인에서의 검증은 Plan 4. 이 계획은 `admin-headers.ts`를 정본으로 남기고, Plan 4가 Caddyfile과의 일치를 검사한다.
- **WebKit(Safari) 지원 검증.** 작성자 1명의 브라우저가 무엇인지에 따라 Plan 4 전에 Playwright 프로젝트를 하나 더하면 된다.
- 글↔첨부 참조 추적(어느 글이 이 이미지를 쓰는지), 첨부 교체, 이미지 크기 조절·썸네일.
- 비밀번호 변경 UI·TOTP·기기별 세션(스펙 7절), 초안/예약 발행, slug 변경.
- 마크다운 도구 모음(굵게·링크 버튼), 맞춤법 검사, 분할 화면 스크롤 동기화, 다크 모드.
- 오프라인 지원·서비스 워커(캐시가 세션 만료 뒤에도 화면을 보여 주는 표면이 된다 — 의도적으로 넣지 않는다).
- 공개 사이트에서 글을 여는 링크: SPA는 공개 origin을 모른다(백엔드가 알려 주는 엔드포인트가 없다). 목록에는 경로(`/posts/<slug>`)만 보여 준다. 필요하면 Plan 4에서 빌드 시점 환경변수(`VITE_PUBLIC_ORIGIN`)로 넣는다.

## Self-Review

**1. 스펙 대조**

| 스펙 요구 | 담당 |
|---|---|
| 3.9 라우트 `/login`·`/`·`/posts/new`·`/posts/:id`·`/series`·`/attachments`, `base: '/'` | Task 1(`vite.config.ts`), 4·6·7(`routes.tsx`). `/tags`는 추가(D13) |
| 3.9 라이브러리(react-router·TanStack Query·Tailwind 4·CodeMirror 6) | Task 1. react-router는 8(D1) |
| 3.9 fetch 래퍼: `X-Requested-With`·`credentials: 'same-origin'`·401이면 `/login` | Task 2(`client.ts`), Task 4(`noteAuthFailure` + `RequireAuth`) |
| 3.9 미리보기: 500ms 디바운스 → `/api/preview` → `sandbox=""` iframe `srcdoc`, React DOM에 서버 HTML 금지 | Task 5(+ 소스 가드) |
| 3.9 임시본: 글별 localStorage, 저장 성공 시 삭제. "저장하면 즉시 공개됩니다" 표시 | Task 3(`drafts.ts`), Task 6 |
| 3.9 409 → "다른 곳에서 수정됨" + 서버본·임시본 나란히 | Task 6(`ConflictPanel`) |
| 3.9 개발 프록시 `/api`·`/attachments` | Task 1 — 대상은 `https://localhost:7198`(D3, 스펙의 `http://localhost:5055`와 다름) |
| 3.6 관리 SPA CSP, 미리보기 iframe CSP | Task 1(`admin-headers.ts`, D5), Task 3(`previewDoc.ts`, D4) — 둘 다 스펙과 다르며 Task 8이 스펙을 고친다 |
| 3.3 접근 계약(CSRF 헤더, 변경 요청의 Origin, 세션) | 백엔드가 강제. E2E가 401·403을 직접 확인(Task 8) |
| 3.4 관리 API 전부(글·시리즈·태그·첨부·미리보기·인증) | Task 2(`endpoints.ts`) — 화면: 글(4·6), 시리즈·태그(4), 첨부(6·7), 미리보기(5) |
| 3.7 본문 200KB·JSON 256KB·업로드 10MB·미리보기/업로드 한도 | Task 3(`LIMITS`), Task 2(`JSON.stringify` 고정), Task 5·6(D12) |
| 3.8 업로드 → 커서에 `![](url)` 삽입 | Task 6(`uploadImages` + `insertAtCursor`) |
| 2B 인계: 409가 400보다 먼저 올 수 있다 / 503·429의 `Retry-After` | Task 6(`onError`), Task 2(`describeError`)·Task 5 |
| `CLAUDE.md`: 프로젝트 추가 시 CI·구성 절 갱신, AGENTS.md 미러 | Task 1·8 |

**2. 자리 표시자 검사:** "TBD"·"적절히 처리" 없음. 모든 코드 단계에 전체 코드가 있다. Task 8 Step 5(문서)만 산문 지시다 — 고칠 문장과 넣을 사실을 항목별로 적었다.

**3. 타입·이름 일관성:** 계획의 코드는 한 프로젝트로 조립해 `tsc -b`를 통과시킨 것이다(이름 불일치가 있으면 컴파일되지 않는다). Task 사이에 바뀌는 파일은 `src/app/routes.tsx` 하나이며 세 시점의 전체 내용을 각각 실었다(Task 4·6·7).

## 작성 중에 잡은 결함 (계획 초안이 틀렸던 곳 — 같은 유형을 의심할 것)

| # | 초안 | 어떻게 드러났나 | 고친 것 |
|---|---|---|---|
| 1 | 미리보기 CSP에 `'self'`(스펙 그대로) | Playwright/Firefox에서 CSS·이미지 전부 차단 | 출처 명시(D4) |
| 2 | `buildUrl()`을 `try` 안에서 호출 | 단위 테스트: 잘못된 경로가 "네트워크 오류"로 둔갑 | `try` 밖으로 |
| 3 | 로그아웃 시 `queryClient.clear()` | 컴포넌트 테스트: 로그인 화면으로 가지 않음 | `setQueryData` + `removeQueries` |
| 4 | `AttachmentsPage`가 편집 화면 모듈에서 `altTextOf`를 import | 번들: CodeMirror가 본체에 들어감 | `lib/markdownImage.ts`로 분리 |
| 5 | E2E에서 "저장 버튼이 disabled가 됐다"로 저장 완료를 판단 | Chromium에서만 간헐 실패: 요청 중에도 disabled라 PUT이 끝나기 전에 컨텍스트를 닫음 | `waitForResponse`로 응답을 기다림 |
| 6 | 정규식에 제어 문자 범위를 유니코드 이스케이프로 표기 | 저장소의 NUL 표기 규칙 위반 | `charCodeAt` 비교(`hasControlChar`) |

그 밖에 테스트 쪽 함정 3건: Testing Library 자동 정리 미등록(S9), Vitest가 Playwright 스펙을 집음(S9), 소스 가드가 "쓰지 말 것"을 설명하는 **주석**에 걸림(주석을 빼고 검사하도록 고침).

## 알려진 불확실성

1. **Linux CI의 개발 인증서 PEM 내보내기** — 측정하지 않았다. 첫 `web-e2e` 실행이 게이트이고 대안(openssl 분기)을 Task 8 Step 4에 적었다.
2. **CI에서 E2E의 안정성** — 로컬 2회 연속 통과가 전부다. 느린 러너에서 `webServer`의 `dotnet run`(빌드 포함) 180초 제한, Firefox 설치 시간을 본다. `retries: 0`은 의도다 — 간헐 실패를 재시도로 가리지 않는다.
3. **`npm audit` 게이트** — 오늘은 통과했는지 측정하지 않았다(Task 1 Step 3에서 확인). 미래의 권고가 무관한 PR을 막을 수 있다. 막히면 컨트롤러가 판정한다(버전 올림 vs 예외 기록).
4. **CodeMirror의 큰 문서 성능**(200KB)과 한글 IME 조합 중 동작 — 측정하지 않았다. `TagInput`은 `isComposing`을 본다. 편집기는 CodeMirror 자체 처리에 맡긴다(추론).
5. **`style-src-attr 'none'`** — 지금 코드와 CodeMirror 6.0.2/view 6.43.12에서는 위반 0건이지만, 라이브러리를 올리면 달라질 수 있다. E2E가 잡는다.
6. **`Retry-After` 형식** — 서버는 초 단위 정수만 쓴다(2B). 프록시(Caddy)가 HTTP-date 형식의 자체 응답을 끼워 넣으면 `null`로 읽고 대기 없이 안내만 한다.
7. **React 19 StrictMode의 effect 이중 실행** — 개발 모드에서 미리보기 요청이 마운트 때 2번 나갈 수 있다(첫 요청은 취소된다 — 추론). production 빌드(E2E)에는 없다.
8. **검증 값의 드리프트** — `LIMITS`는 서버 상수를 손으로 옮긴 것이다. 서버가 바뀌면 클라이언트 검사만 낡는다(서버의 400은 그대로 화면에 표시되므로 안전 쪽으로 실패한다). 자동 동기화는 하지 않는다.
