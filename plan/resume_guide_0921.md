# 작업 재개 가이드 (2026-09-21 기준)

> 이 문서 하나만 읽으면 어느 세션에서든 이어서 작업할 수 있도록 쓴 인계 기록이다. 상태가 바뀌면 **이 문서를 갱신**하고 날짜를 고친다(새 파일을 만들지 않는다).

## 1. 지금 어디까지 왔나

| 단계 | 내용 | 상태 | 근거 |
|---|---|---|---|
| 설계 | 기술 블로그 스펙(PARA 노트앱 대체), Codex 교차 검토 반영 | 완료 | `plan/tech_blog_0920.md` |
| 0 | 솔루션 개명(`PortfolioBlog`), `.slnx` 전환, 샘플 제거 | 완료 | master |
| 1 | 도메인·DB 제약, 접근 제어(호스트·IP·CSRF), 비밀번호 로그인·세션, 글·시리즈·태그 관리 API | 완료 | PR #1 → `5604f2d` |
| 2A | 마크다운 파이프라인, `/api/preview`, 이미지 판정·메타데이터 제거, 이미지 첨부 | 완료 | PR #2 → `898b839`, 보고서 `plan/tech_blog_2a_report_0921.md` |
| **2B** | 공개 Razor 페이지·검색·Atom·sitemap·보안 헤더·공개 속도 제한·렌더 캐시 | **다음 작업 — 계획 작성됨, 승인·실행 대기** | `docs/superpowers/plans/2026-09-21-tech-blog-public-site.md`, 3절 |
| 3 | 관리 에디터 SPA(React 19 + Vite) | 예정, 계획 미작성 | 스펙 8절 |
| 4 | Docker Compose·Caddy·백업/복원 | 예정, 계획 미작성 | 스펙 8절 |

- 기준 커밋: master `898b839`(이 문서를 추가한 문서 커밋이 그 위에 하나 더 있다). 작업 트리 깨끗함, 열린 PR 없음.
- 검증 상태: Release 빌드 경고 0 / 오류 0, 테스트 **412개 통과**, 하네스 감사 8/8, CI(ubuntu-latest) 통과.
- 남아 있는 것: 원격 브랜치 `origin/feature/blog-backend-core`, `origin/feature/blog-content-pipeline`(둘 다 squash 병합 완료 — 지워도 된다. 아직 지우지 않았다).

## 2. 다시 시작할 때 5분 점검

```powershell
git switch master; git pull --ff-only
git status --short                         # 비어 있어야 한다
Test-Path .git/harness_commit_in_progress  # False여야 한다(True면 이전 실행이 중단된 것 — 4절)
Test-Path .git/hooks/commit-msg            # False면: Copy-Item scripts/git-hooks/commit-msg .git/hooks/
docker info --format '{{.ServerVersion}}'  # Docker가 떠 있어야 통합 테스트가 돈다
dotnet build PortfolioBlog.slnx -c Release # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release # 412개 통과 (한 개가 연결 타임아웃으로 실패하면 5절 참고 후 재실행)
pwsh scripts/harness-audit.ps1             # PASS 8/8
```

필요 도구: .NET SDK 10.0.303, Docker Desktop, PowerShell 7, `gh`(GitHub CLI, 로그인됨), Python 3 + Pillow(이미지 픽스처·호환성 확인용, 테스트 실행에는 불필요), Codex CLI(교차 검증을 쓸 때만).

## 3. 다음 작업: Plan 2B 승인 → 실행

**흐름(저장소 규칙):** `superpowers:writing-plans`(**완료, 2026-09-21**) → (사용자 승인) → `superpowers:subagent-driven-development` → 최종 리뷰 → PR → CI → squash 병합 → 보고서.

계획: `docs/superpowers/plans/2026-09-21-tech-blog-public-site.md` — Task 9개(속도 제한 체인 → 보안 헤더·호스트·본문 상한 → 렌더 게이트/캐시 → 읽기 전용 DbContext → 공개 페이지 → 검색 → 피드·sitemap → 첨부 정합성 → 검증⊆제약 테스트·문서). 새 NuGet 패키지·마이그레이션 없음. 문서 앞의 **스파이크 표 S1~S12**(실측한 가정)와 **설계 결정 표 D1~D12**(질문 없이 추천안으로 정한 것 — 승인할 때 뒤집을 수 있다), 끝의 "알려진 불확실성" 6건부터 읽는다.

승인 뒤 Claude Code에 이렇게 요청하면 된다:

```
/superpowers:subagent-driven-development docs/superpowers/plans/2026-09-21-tech-blog-public-site.md
브랜치 feature/blog-public-site. 모든 선택지는 추천안으로, 끝나면 "내린 판정" 표가 든 보고서.
```

**2B가 다루는 것**(2A가 남긴 숙제 — 출처: 2A 계획 정오표, 2A 보고서 6·8절. 계획의 Self-Review 표가 항목별 담당 Task를 적어 두었다):

1. **렌더 비용**: 렌더링은 동기·취소 불가다. 코드 강조는 렌더당 약 2.25초가 상한이지만 Markdig 파서 자체가 적대적 200KB 입력에서 인라인 약 8.5초, 블록 약 6.6초 걸린다. 공개 페이지는 렌더 결과를 **캐시**하거나 동시성을 제한해야 하고, 글 저장(`POST`/`PUT /api/posts`)도 같은 게이트 안에 넣는다. 시작 시 렌더러 워밍업(첫 렌더 약 185ms).
2. **보안 헤더·호스트**: 전역 보안 헤더 미들웨어(라우트 제약 실패 404에도 nosniff), `AllowedHosts`를 두 호스트로 제한(지금 `*`), `Server` 헤더 제거. 사이트 CSS에 id 선택자 금지(제목 id가 작성자 텍스트에서 나온다).
3. **한도**: 공개·검색 속도 제한, 관리 JSON 본문 256KB 상한(지금은 Kestrel 기본 30MB 뒤에 200KB 검증), 업로드 속도 제한, `statement_timeout`. 속도 제한 체인에서 동시 실행 거부가 분당 허용량을 소모하고 `Retry-After: 60`을 돌려주는 문제(로그인·미리보기 공통).
4. **정합성**: 고아 파일 정리(중복 외 사유로 INSERT 실패, 오래된 `.tmp`), 같은 내용의 삭제·업로드 교차 시 파일 없는 행 가능성(미검증).
5. **테스트**: 시간 의존 테스트를 주입 가능한 시계로 교체(`Render_ManyFastBlocks…`는 약 6배 빠른 기계에서 거짓 실패). 절대 시간 상한 대신 비율 단언을 쓴다.

Plan 3·4 메모: 미리보기 iframe의 이미지는 **관리 오리진**에서 읽힌다(첨부 응답에 `Cross-Origin-Resource-Policy: same-origin`을 붙이면 깨진다). Caddy `request_body`는 앱 상한 10MB가 아니라 프레임워크 상한 **11MB**. 저장 볼륨은 앱 시작 전에 마운트(시작 시 쓰기 확인). multipart 버퍼링이 프로세스 임시 폴더에 쓴다(읽기 전용 루트 FS 불가). Release 출력의 EF 디자인 타임 어셈블리 제거.

## 4. 계획 실행 중에 끊겼다면 (Subagent-Driven Development)

- 진행 기록은 `.superpowers/sdd/<계획 파일 이름>/progress.md`에 있다(git 무시 대상). 첫 줄이 그 계획 파일을 가리키면 `Task N: complete`가 찍힌 작업은 **끝난 것**이다 — 다시 맡기지 않는다. 마지막 줄이 수정 라운드면 그 라운드부터 잇는다. 기록이 없으면 `git log`로 복구한다.
- 실행 중에는 Stop 훅의 자동 커밋을 막는 센티널 `.git/harness_commit_in_progress`를 둔다(6시간 유효, 작업마다 `touch`로 갱신). 실행이 끝나거나 **중단을 확정하면 지운다** — 남아 있으면 훅이 아무것도 커밋하지 않는다.
- 컨트롤러가 매 커밋마다 직접 확인한 것: Release 빌드 경고 0, 전체 테스트, 커밋 트레일러(`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` — 구현 에이전트가 자기 모델 이름을 쓰는 일이 두 번 있었다), 변경 텍스트 파일의 0x00 바이트, 새 `.cs`의 줄 끝(`git ls-files --eol` → `w/crlf`).
- 리뷰어에게는 diff 파일(`scripts/review-package PLAN BASE HEAD`의 출력)과 지시서 경로만 넘긴다. 구현자 보고는 **검증 대상**이다(2A에서 "CRLF 유지", "주석 갱신 완료"가 거짓이었다).
- 이번 세션에서 효과가 컸던 것: 리뷰어에게 "계획 코드 자체가 틀렸을 수 있다 — 직접 공격하고 측정하라"고 명시, 최종 리뷰에서 **실제 Production 호스트를 띄워 HTTP로 찔러 보기**, 첫 Linux CI를 게이트로 취급.

## 5. 이 저장소에서 자주 밟는 함정

| 함정 | 증상 | 대처 |
|---|---|---|
| 셸 heredoc이 백슬래시를 망가뜨린다 | `\n`이 든 Python/C# 조각을 heredoc으로 쓰면 치환이 안 맞는다 | 스크립트는 Write 도구로 파일에 쓰고 실행한다 |
| NUL 표기 | 6글자 유니코드 이스케이프가 실제 0x00 바이트로 바뀌어 파일에 들어간 전례(2회) | 소스에는 C# `\0` 이스케이프나 `0x00` 바이트 리터럴만. 커밋 전 바이트 스캔 |
| Stop 훅 비밀값 스캐너 | `password = "…"` 꼴의 줄이 있으면 커밋 차단 | 테스트 값에 `dummy` 같은 자리표시자 단어를 넣는다 |
| Stop 훅 자동 커밋 | 에이전트의 작업 중간 상태가 커밋된다 | 실행 중 센티널 유지(4절) |
| 로컬 간헐 테스트 실패 | 412개 중 1개가 `Database.Migrate()`의 15초 연결 타임아웃으로 실패(실행 시간 19초 → 33초). 빌드 직후 등 기계가 바쁠 때, Windows Docker Desktop에서만 | 재실행. Linux CI에서는 나오지 않았다. 근본 대책 후보: 테스트 클래스 간 DB 공유 |
| 느린 CI 러너 | 절대 시간 상한 테스트가 깨진다(로컬 0.8초 → CI 7.2초) | 비율·상대 단언을 쓴다 |
| `Accepts` 메타데이터 | Content-Type이 안 맞으면 라우팅이 인가보다 먼저 415(핸들러는 실행되지 않음) | 접근 매트릭스는 엔드포인트가 받는 형식으로 본문을 보낸다 |
| 줄 끝 | 작업 트리는 CRLF(`autocrlf=true`), `CLAUDE.md`·`AGENTS.md`·`plan/*.md` 일부는 LF | 파일의 기존 스타일 유지, 한 파일 안에서 섞지 않는다 |
| Codex 실행 뒤 | `.agents/skills/{codex,cross-verify}`·`.codex/agents/cross-*.toml`이 다시 생길 수 있다 | 턴 끝에 untracked 확인 후 삭제(미러 제외 대상) |
| 이 PC의 AdGuard | 평문 HTTP(`http://127.0.0.1:…`)로 받은 HTML `<head>`에 `<script src="//local.adguard.org…">`가 **주입돼 있다**(2B 스파이크에서 실측). 앱이 낸 것이 아니다 | 실제 호스트를 HTTP로 찔러 볼 때 XSS로 오판하지 말 것. 서버 쪽 바이트로 확인하거나 AdGuard를 끄고 본다. TestServer(인메모리)는 영향 없음 |
| Razor Pages의 `page` | 핸들러 매개변수 `int? page`가 쿼리 문자열이 아니라 예약 라우트 값(`/Index`)을 바인딩하려다 실패한다 | `Request.Query["page"]`를 직접 읽는다(2B 계획의 `PageNumber`). 링크도 `asp-route-page`를 쓰지 않는다 |

## 6. 문서 지도

| 무엇을 알고 싶나 | 어디 |
|---|---|
| 제품이 무엇이고 왜 이렇게 설계했나 | `plan/tech_blog_0920.md`(스펙, 구속력 있는 기준) |
| 2A에서 무엇을 만들고 어떻게 검증했나, 내린 판정 12건, 잔여 위험 | `plan/tech_blog_2a_report_0921.md` |
| 계획 코드가 틀렸던 곳과 다음 계획이 지킬 규칙 1~9 | 두 구현 계획 문서의 끝 "구현 중 발견해 고친 계획 결함" |
| 1단계·2A 구현 계획(TDD 단계별) | `docs/superpowers/plans/2026-09-20-…backend-core.md`, `…2026-09-21-…content-pipeline.md` |
| 프로젝트 규칙(주석·커밋·하네스·경로) | `CLAUDE.md`(Codex용 미러 `AGENTS.md` — 함께 고친다) |
| 하네스 변경 이력 | `plan/harness_changelog.md` |
| 로컬 실행 방법, API 예시 요청 | `README.md` "시작하기", `PortfolioBlog.Api/PortfolioBlog.Api.http` |

## 7. 코드 지도 (2A 이후)

```
PortfolioBlog.Api/
├─ Program.cs                      # 파이프라인: forwarded headers → 예외 처리 → 관리 게이트 → 속도 제한 → 인증 → 인가
├─ Domain/                         # Post · Series · Tag · PostTag · AdminState · Attachment
├─ Contracts/                      # DTO, TextRules(NUL 거부), ValidationErrors
├─ Features/
│  ├─ ApiEndpoints.cs              # 보호된 /api 그룹(RequireHost + RequireAuthorization)
│  ├─ Auth/ Posts/ Series/ Tags/   # 1단계
│  ├─ Preview/                     # POST /api/preview
│  └─ Attachments/                 # 관리 업로드·목록·삭제 + 공개 GET/HEAD /attachments/{id}/{fileName}
└─ Infrastructure/
   ├─ Access/                      # CIDR 허용 목록, AdminSurfaceMiddleware, 세션 검증, StartupValidation(fail-fast)
   ├─ Data/                        # AppDbContext, 마이그레이션 2개, DbConflict, TagResolver, SlugRules
   ├─ Markdown/                    # MarkdownRenderer(싱글턴) · UrlPolicy · HighlightingCodeBlockRenderer · BoundedHighlighting · HeadingIds · HtmlAllowlist
   ├─ Storage/                     # ImageSignature · MetadataStripper(기본 거부) · FileSystemAttachmentStore(내용 주소)
   └─ Web/                         # ClientIp · RateLimitPolicy(메타데이터) · RateLimitingExtensions
PortfolioBlog.Api.Tests/           # 412개. ApiFactory(팩토리별 DB·임시 첨부 루트), AccessMatrixTests(닫힌 세계), Fixtures/Images
```

의존 방향: `Features → Infrastructure → Contracts → Domain`. Feature끼리는 서로 참조하지 않는다. 패키지 버전은 `Directory.Packages.props`가 일괄 관리한다(Markdig 1.4.0, ColorCode.HTML 2.0.15, HtmlSanitizer 9.2.1039, Npgsql EF 10.0.3, Testcontainers.PostgreSql 4.15.0, xunit 2.9.3).

## 8. 사용자가 정해 둔 것 (다시 묻지 않는다)

- **보안이 최우선.** 선택지가 갈리면 더 엄격한 쪽.
- 글은 상태 없음 — 저장 즉시 공개. PARA·노션 가져오기/내보내기는 폐기.
- 공개 페이지는 서버 렌더링(Razor + Markdig), React는 관리 에디터에만. 관리 표면은 `admin.<도메인>` 서브도메인.
- 쓰기 권한 = IP 화이트리스트 **AND** 비밀번호 세션(아이디 없음).
- 실행 단계에서는 **질문하지 말고 추천안으로 끝까지**, 끝나면 "내린 판정" 표가 든 보고서를 쓴다.
- XML 문서 주석 규칙은 CLAUDE.md "적용 범위" 표대로(내용 없는 상용구 remarks 금지).

## 9. 빌드 검증

```bash
dotnet build PortfolioBlog.slnx -c Release   # 경고 0 / 오류 0
dotnet test  PortfolioBlog.slnx -c Release   # 412개 통과 (Docker 필요)
pwsh scripts/harness-audit.ps1               # 8/8 PASS
```
