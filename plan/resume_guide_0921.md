# 작업 재개 가이드 (2026-09-21 기준, 2B 완료 후 갱신)

> 이 문서 하나만 읽으면 어느 세션에서든 이어서 작업할 수 있도록 쓴 인계 기록이다. 상태가 바뀌면 **이 문서를 갱신**하고 날짜를 고친다(새 파일을 만들지 않는다).

## 1. 지금 어디까지 왔나

| 단계 | 내용 | 상태 | 근거 |
|---|---|---|---|
| 설계 | 기술 블로그 스펙(PARA 노트앱 대체), Codex 교차 검토 반영 | 완료 | `plan/tech_blog_0920.md` |
| 0 | 솔루션 개명(`PortfolioBlog`), `.slnx` 전환, 샘플 제거 | 완료 | master |
| 1 | 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 비밀번호 로그인·세션, 글·시리즈·태그 관리 API | 완료 | PR #1 → `5604f2d` |
| 2A | 마크다운 파이프라인, `/api/preview`, 이미지 판정·메타데이터 제거, 이미지 첨부 | 완료 | PR #2 → `898b839`, 보고서 `plan/tech_blog_2a_report_0921.md` |
| 2B | 공개 Razor 페이지·검색·Atom·sitemap·보안 헤더·호스트 필터·공개 속도 제한·읽기 전용 DB 연결·렌더 게이트/캐시·첨부 정합성 | 완료 | PR #3(squash 병합), 보고서 `plan/tech_blog_2b_report_0921.md` |
| **3** | 관리 에디터 SPA(React 19 + Vite) | **다음 작업 — 계획 미작성** | 스펙 8절, 3절 |
| 4 | Docker Compose·Caddy·백업/복원 | 예정, 계획 미작성 | 스펙 8절 |

- 기준 커밋: master의 PR #3 squash 커밋(`git log --oneline -3 master`로 확인). 작업 트리 깨끗함, 열린 PR 없음.
- 검증 상태: Release 빌드 경고 0 / 오류 0, 테스트 **589개 통과**, 하네스 감사 8/8, CI(ubuntu-latest) 통과.
- 남아 있는 것: 원격 브랜치 `origin/feature/blog-backend-core`, `origin/feature/blog-content-pipeline`, `origin/feature/blog-public-site`(전부 squash 병합 완료 — 지워도 된다. 아직 지우지 않았다).

## 2. 다시 시작할 때 5분 점검

```powershell
git switch master; git pull --ff-only
git status --short                         # 비어 있어야 한다
Test-Path .git/harness_commit_in_progress  # False여야 한다(True면 이전 실행이 중단된 것 — 4절)
Test-Path .git/hooks/commit-msg            # False면: Copy-Item scripts/git-hooks/commit-msg .git/hooks/
docker info --format '{{.ServerVersion}}'  # Docker가 떠 있어야 통합 테스트가 돈다
dotnet build PortfolioBlog.slnx -c Release # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release # 589개 통과 (한 개가 연결 타임아웃으로 실패하면 5절 참고 후 재실행)
pwsh scripts/harness-audit.ps1             # PASS 8/8
```

필요 도구: .NET SDK 10.0.303, Docker Desktop, PowerShell 7, `gh`(GitHub CLI, 로그인됨), Python 3 + Pillow(이미지 픽스처·호환성 확인용, 테스트 실행에는 불필요), Codex CLI(교차 검증을 쓸 때만). Plan 3부터 Node.js(버전은 계획에서 정한다).

## 3. 다음 작업: Plan 3(관리 에디터 SPA) 계획 작성

**흐름(저장소 규칙):** `superpowers:writing-plans` → (사용자 승인) → `superpowers:subagent-driven-development` → 최종 리뷰(실제 호스트 공격 포함) → PR → CI → squash 병합 → 보고서.

입력 자료: 스펙 `plan/tech_blog_0920.md` 8절의 Plan 3 행과 3.2(관리 표면)·3.6(CSP)·3.7(자원 제한), 2B 보고서 8절, 2B 계획 끝의 "구현 중 발견해 고친 계획 결함"(있다면), 2A 보고서 8절.

**백엔드가 SPA에 요구하는 것(2A·2B에서 확정된 사실):**

1. 본문은 `JSON.stringify`(비 ASCII를 이스케이프하지 않음)로 보낸다 — 관리 JSON 본문 상한은 **직렬화 후 256KB**다. 비 ASCII를 이스케이프하는 인코더는 200KB 한글 본문을 최대 6배로 부풀려 413을 받는다.
2. 글 수정의 **409(version 불일치)는 본문 검증 400보다 먼저** 올 수 있다. 409를 받으면 최신본을 다시 불러오게 안내한다.
3. 과부하는 **503 + `Retry-After`**(렌더 슬롯 대기 초과, DB statement/lock timeout), 속도 제한은 **429 + `Retry-After`**(미리보기·업로드: 전역 30/분·동시 2). 둘 다 재시도 안내가 필요하다.
4. 모든 관리 요청은 `X-Requested-With` 헤더 + 같은 오리진(`admin.<도메인>`) + 세션 쿠키. 매칭되지 않는 `/api/...`는 404.
5. 미리보기 iframe은 `sandbox=""` + `srcdoc` 안 CSP meta. iframe의 이미지는 **관리 오리진**에서 읽힌다(첨부 응답에 `Cross-Origin-Resource-Policy: same-origin`을 붙이면 깨진다). 첨부 GET은 공개 읽기 전용 연결(3초 statement_timeout)을 쓴다.
6. 공개 CSP는 스크립트를 허용하지 않는다 — SPA는 `admin.<도메인>`에서 Caddy가 서빙하는 정적 파일이고 API 앱의 CSP와 별개다(Plan 4의 Caddyfile에서 SPA용 CSP를 정한다).

**Plan 4 메모(2B에서 추가된 것):** 컨테이너 헬스체크·`curl`은 `Host: <공개 호스트>` 헤더가 필요하다(`localhost`는 본문 없는 400). `ConnectionStrings:Default`에 `Options`를 넣으면 시작 실패, `Command Timeout`(초)×1000은 `Public:StatementTimeoutMs`보다 커야 한다. Caddy `request_body`는 프레임워크 상한 **11MB**. 저장 볼륨은 앱 시작 전에 마운트. multipart 버퍼링이 프로세스 임시 폴더에 쓴다(읽기 전용 루트 FS 불가). Release 출력의 EF 디자인 타임 어셈블리 제거. 이미지의 `wwwroot`가 `css/site.css` 하나인지 검증(csproj의 `CompressionEnabled=false`가 지워지면 `.gz`·`.br`가 돌아온다 — 회귀 테스트 없음). 쓰기 권한 없는 DB 롤(공개 연결의 read-only는 세션에서 끌 수 있는 심층 방어일 뿐). 503 1건당 로그 약 50줄. 첨부 GET이 공개 풀을 공유한다 — 풀 고갈 시 500 가능성(추론)은 부하 검증 항목. 실제 Kestrel 고유 동작(요청 줄 8KB 초과 414, `Expect: 100-continue`의 413)은 스모크 테스트로.

## 4. 계획 실행 중에 끊겼다면 (Subagent-Driven Development)

- 진행 기록은 `.superpowers/sdd/<계획 파일 이름>/progress.md`에 있다(git 무시 대상). 첫 줄이 그 계획 파일을 가리키면 `Task N: complete`가 찍힌 작업은 **끝난 것**이다 — 다시 맡기지 않는다. 마지막 줄이 수정 라운드면 그 라운드부터 잇는다. 기록이 없으면 `git log`로 복구한다.
- 실행 중에는 Stop 훅의 자동 커밋을 막는 센티널 `.git/harness_commit_in_progress`를 둔다(6시간 유효, 작업마다 `touch`로 갱신). 실행이 끝나거나 **중단을 확정하면 지운다** — 남아 있으면 훅이 아무것도 커밋하지 않는다.
- 컨트롤러가 매 커밋마다 직접 확인한 것: Release 빌드 경고 0, 전체 테스트, 커밋 트레일러(`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`), 변경 텍스트 파일의 0x00 바이트, 새 `.cs`·`.cshtml`·`.css`의 줄 끝(CRLF). 2B에서는 이것을 작업공간의 `verify.sh <BASE>` 하나로 묶었다(테스트 출력 보관, 실패 시 FAIL) — 다음 실행에서도 먼저 만든다.
- 리뷰어에게는 diff 파일(`scripts/review-package PLAN BASE HEAD`의 출력)과 지시서 경로만 넘긴다. 구현자 보고는 **검증 대상**이다.
- 서브에이전트의 완료 보고가 늦거나 오지 않을 때가 있다(2B에서 2회). 결과를 가정하지 말고 저장소를 직접 본다(커밋, 작업 트리, 보고서 파일, 남은 dotnet 프로세스, 사보타주 복구 여부). 지시문에 "응답을 마지막 행동으로 하라"를 넣는다.
- 효과가 컸던 것: 리뷰어에게 "계획 코드 자체가 틀렸을 수 있다 — 직접 공격하고 측정하라"고 명시, 새·고친 테스트마다 **사보타주로 실패를 확인**(규칙 8), 최종 리뷰에서 **실제 Production 호스트를 HTTPS로 찔러 보기**(2B에서 TestServer 스위트 579개가 놓친 결함 3건을 여기서 찾았다), 첫 Linux CI를 게이트로 취급.
- 계획을 쓸 때: **기반을 바꾸는 작업에는 "기존 소비자 목록"을 넣는다.** 2B에서 새 공개 DB 연결을 만든 작업이 2A의 공개 첨부 핸들러를 옮기지 않아 최종 리뷰에서야 발견됐다.

## 5. 이 저장소에서 자주 밟는 함정

| 함정 | 증상 | 대처 |
|---|---|---|
| 셸 heredoc이 백슬래시를 망가뜨린다 | `\n`이 든 Python/C# 조각을 heredoc으로 쓰면 치환이 안 맞는다 | 스크립트는 Write 도구로 파일에 쓰고 실행한다 |
| NUL 표기 | 6글자 유니코드 이스케이프가 실제 0x00 바이트로 바뀌어 파일에 들어간 전례(2회) | 소스에는 C# `\0` 이스케이프나 `0x00` 바이트 리터럴만. 커밋 전 바이트 스캔 |
| Stop 훅 비밀값 스캐너 | `password = "…"` 꼴의 줄이 있으면 커밋 차단 | 테스트 값에 `dummy` 같은 자리표시자 단어를 넣는다 |
| Stop 훅 자동 커밋 | 에이전트의 작업 중간 상태가 커밋된다 | 실행 중 센티널 유지(4절) |
| 로컬 간헐 테스트 실패 | 589개 중 1개가 정확히 15초 만에 실패(`Database.Migrate()`의 연결 타임아웃, 실행 시간 33초 → 48초). 빌드 직후 등 기계가 바쁠 때, Windows Docker Desktop에서만. 2B 실행 중 1회 관측 | 재실행. Linux CI에서는 나오지 않았다. 근본 대책 후보: 테스트 클래스 간 DB 공유 |
| 느린 CI 러너 | 절대 시간 상한 테스트가 깨진다(로컬 0.8초 → CI 7.2초) | 비율·상대 단언을 쓴다. 시간은 `TimeProvider`로 주입한다(2B에서 렌더러에 도입) |
| `Accepts` 메타데이터 | Content-Type이 안 맞으면 라우팅이 인가보다 먼저 415(핸들러는 실행되지 않음) | 접근 매트릭스는 엔드포인트가 받는 형식으로 본문을 보낸다 |
| 줄 끝 | 작업 트리는 CRLF(`autocrlf=true`), `CLAUDE.md`·`AGENTS.md`·`plan/*.md` 일부는 LF | 파일의 기존 스타일 유지, 한 파일 안에서 섞지 않는다 |
| Codex 실행 뒤 | `.agents/skills/{codex,cross-verify}`·`.codex/agents/cross-*.toml`이 다시 생길 수 있다 | 턴 끝에 untracked 확인 후 삭제(미러 제외 대상) |
| 이 PC의 AdGuard | 평문 HTTP(`http://127.0.0.1:…`) 응답의 HTML `<head>`에 `<script src="//local.adguard.org…">`를 **주입하고 CSP 헤더까지 다시 쓴다**(`default-src local.adguard.org …`). curl·pwsh 소켓도 가로채고 h2c는 끊는다(2B 최종 리뷰 실측) | 실제 호스트 프로브는 **개발 인증서로 HTTPS 엔드포인트를 띄워 `curl -k`로 직결**한다. 평문 HTTP에서 본 헤더·본문은 믿지 않는다. TestServer(인메모리)는 영향 없음 |
| Razor Pages의 `page` | 핸들러 매개변수 `int? page`가 쿼리 문자열이 아니라 예약 라우트 값(`/Index`)을 바인딩하려다 실패한다 | `Request.Query["page"]`를 직접 읽는다(`PageNumber`). 링크도 `asp-route-page`를 쓰지 않는다 |
| MVC 바인딩의 공백 → null | 경로 값이 공백뿐이면(`/tags/%20`) `string` 매개변수가 **null**로 들어와 NRE → 500(2B 최종 리뷰 실측) | 핸들러 매개변수는 `string?`, 첫 줄에서 `IsNullOrWhiteSpace` → 404. 쿼리 값은 `Request.Query`에서 직접 읽는다 |
| Razor와 한글 | `@Model.Total건`은 컴파일되지 않는다(한글을 식별자 문자로 읽는다) | `@(Model.Total)건` |
| 프레임워크 기본값 | 호스트 필터의 400이 HTML 본문을 보낸다(`IncludeFailureMessage` 기본 true), publish가 `wwwroot`에 `.gz`·`.br` 사본을 만든다(`CompressionEnabled` 기본 true) — 둘 다 TestServer에서는 안 보인다 | 2B에서 둘 다 껐다. 새 미들웨어·SDK 기능을 켤 때는 publish 출력과 실제 호스트 응답을 확인한다 |
| EF Core 10 런타임 모델 | `db.Model.GetCheckConstraints()`가 예외를 던진다 | `db.GetService<IDesignTimeModel>().Model`을 쓴다 |
| 컨텍스트가 둘 | `dotnet ef`가 컨텍스트를 고르지 못한다 | `--context AppDbContext`(마이그레이션은 관리 컨텍스트에만) |

## 6. 문서 지도

| 무엇을 알고 싶나 | 어디 |
|---|---|
| 제품이 무엇이고 왜 이렇게 설계했나 | `plan/tech_blog_0920.md`(스펙, 구속력 있는 기준 — 2B에서 3.3~3.8이 as-built로 갱신됐다) |
| 2A에서 무엇을 만들고 어떻게 검증했나, 내린 판정 12건, 잔여 위험 | `plan/tech_blog_2a_report_0921.md` |
| 2B에서 무엇을 만들고 어떻게 검증했나, 계획 결함과 교훈, 내린 판정 28건, 잔여 위험, Plan 3·4 인계 | `plan/tech_blog_2b_report_0921.md` |
| 계획 코드가 틀렸던 곳과 다음 계획이 지킬 규칙 1~9 | 1단계·2A 구현 계획 문서의 끝 "구현 중 발견해 고친 계획 결함", 2B는 보고서 4절 |
| 구현 계획(TDD 단계별) | `docs/superpowers/plans/2026-09-20-…backend-core.md`, `…2026-09-21-…content-pipeline.md`, `…2026-09-21-tech-blog-public-site.md`(앞의 스파이크 표 S1~S12·설계 결정 D1~D12) |
| 프로젝트 규칙(주석·커밋·하네스·경로) | `CLAUDE.md`(Codex용 미러 `AGENTS.md` — 함께 고친다) |
| 하네스 변경 이력 | `plan/harness_changelog.md` |
| 로컬 실행 방법, 설정 키 표, API 예시 요청 | `README.md`, `PortfolioBlog.Api/PortfolioBlog.Api.http` |

## 7. 코드 지도 (2B 이후)

```
PortfolioBlog.Api/
├─ Program.cs                      # 파이프라인: (호스트 필터) → 보안 헤더 → forwarded headers → 예외 처리(과부하 503)·상태 코드 페이지 → 정적 파일 → 관리 게이트(IP) → 속도 제한 → 인증 → 인가 → /api 본문 상한(401이 413보다 먼저)
├─ Domain/                         # Post · Series · Tag · PostTag · AdminState · Attachment
├─ Contracts/                      # DTO, TextRules(NUL 거부), ValidationErrors
├─ Features/
│  ├─ ApiEndpoints.cs              # 보호된 /api 그룹(RequireHost + RequireAuthorization)
│  ├─ Auth/ Posts/ Series/ Tags/   # 1단계 (Posts는 렌더 게이트·캐시 선채움 가드)
│  ├─ Preview/                     # POST /api/preview
│  └─ Attachments/                 # 관리 업로드(advisory lock)·목록·삭제 + 공개 GET/HEAD(PublicDbContext)
├─ Pages/                          # 공개 Razor 페이지: Index · Post · Tag · Series · Search, _Layout · _PostList · _TagList · _Pager,
│                                  # PublicPageConvention(GET/HEAD·공개 호스트·속도 제한), PageNumber, SiteEndpoints(feed.xml · sitemap.xml · robots.txt · /css/highlight.css)
├─ wwwroot/css/site.css            # 유일한 정적 파일(id 선택자 금지)
└─ Infrastructure/
   ├─ Access/                      # CIDR 허용 목록, AdminSurfaceMiddleware, 세션 검증, StartupValidation(fail-fast — 연결 문자열 검사 포함)
   ├─ Data/                        # AppDbContext(관리) · PublicDbContext(읽기 전용·statement_timeout) · PublicQueries · PublicModels, 마이그레이션 2개
   ├─ Markdown/                    # MarkdownRenderer(TimeProvider) · RenderGate · RenderedPostCache · UrlPolicy · HighlightingCodeBlockRenderer · HeadingIds · HtmlAllowlist · HighlightCss
   ├─ Storage/                     # ImageSignature · MetadataStripper · FileSystemAttachmentStore · AttachmentLock · AttachmentJanitor
   └─ Web/                         # ClientIp · RateLimit* · PublicOptions · SecurityHeadersMiddleware · ErrorResponses · OverloadExceptionHandler · ApiBodyLimitMiddleware · PublicUrls · XmlText
PortfolioBlog.Api.Tests/           # 589개. ApiFactory(팩토리별 DB·임시 첨부 루트·청소 잡 꺼짐), AccessMatrixTests(닫힌 세계), PublicSeed, HtmlDoc, SteppingTimeProvider
```

파일 위치는 위 트리가 어긋나면 코드가 맞다(`Glob`으로 확인). 의존 방향: `Features·Pages → Infrastructure → Contracts → Domain`. Feature끼리, `Pages`와 `Features`는 서로 참조하지 않는다. 패키지 버전은 `Directory.Packages.props`가 일괄 관리한다(2B에서 새 패키지·마이그레이션 없음).

## 8. 사용자가 정해 둔 것 (다시 묻지 않는다)

- **보안이 최우선.** 선택지가 갈리면 더 엄격한 쪽.
- 글은 상태 없음 — 저장 즉시 공개. PARA·노션 가져오기/내보내기는 폐기.
- 공개 페이지는 서버 렌더링(Razor + Markdig), React는 관리 에디터에만. 관리 표면은 `admin.<도메인>` 서브도메인.
- 쓰기 권한 = IP 화이트리스트 **AND** 비밀번호 세션(아이디 없음).
- 실행 단계에서는 **질문하지 말고 추천안으로 끝까지**(PR 생성·CI 확인·squash 병합 포함), 끝나면 "내린 판정" 표가 든 보고서를 쓴다. 브레인스토밍·설계 단계의 제품 방향 결정은 묻는다.
- XML 문서 주석 규칙은 CLAUDE.md "적용 범위" 표대로(내용 없는 상용구 remarks 금지).

## 9. 빌드 검증

```bash
dotnet build PortfolioBlog.slnx -c Release   # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release   # 589개 통과 (Docker 필요)
pwsh scripts/harness-audit.ps1               # 8/8 PASS
```
