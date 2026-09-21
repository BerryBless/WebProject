# 기술 블로그 콘텐츠 파이프라인 구현 계획 (Plan 2A/4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 마크다운을 안전한 HTML로 바꾸는 정제 파이프라인과 `/api/preview`, 그리고 메타데이터를 제거해 내용 주소로 저장하는 이미지 첨부(업로드·목록·삭제·공개 GET)를 `PortfolioBlog.Api`에 추가한다(스펙 2단계의 전반부).

**Architecture:** 마크다운은 `Markdig 파싱(raw HTML 비활성·확장 허용 목록) → AST의 URL 정책 → 서버 측 하이라이팅(CSS 클래스만) → HTML 허용 목록 정제`의 순수 함수 파이프라인 하나를 공개 페이지와 미리보기가 공유한다. 첨부는 서버가 이미지를 **디코딩하지 않고** 컨테이너 구조만 읽어 메타데이터 세그먼트를 버린 뒤, 그 결과 바이트의 SHA-256을 파일 이름으로 쓴다. 속도 제한은 `Infrastructure/Web`으로 옮겨 엔드포인트 메타데이터로 파티션을 고른다.

**Tech Stack:** .NET SDK 10.0.303, ASP.NET Core Minimal API, EF Core 10 + Npgsql 10.0.3, `Markdig 1.4.0`, `ColorCode.HTML 2.0.15`(+ `ColorCode.Core`), `HtmlSanitizer 9.2.1039`(+ `AngleSharp 1.7.2`, `AngleSharp.Css 1.0.2`), xUnit 2.9.3, Testcontainers.PostgreSql 4.15.0. 새 런타임 의존성은 이 6개 패키지가 전부다.

**Spec:** `plan/tech_blog_0920.md` (승인됨) — 3.5(마크다운 파이프라인), 3.7(자원 제한 중 미리보기·업로드), 3.8(첨부), 3.4의 `attachments`·`preview` 행. 선행 계획: `docs/superpowers/plans/2026-09-20-tech-blog-backend-core.md`(완료, master `5604f2d`) — **그 문서 끝의 "구현 중 발견해 고친 계획 결함" 표와 규칙 4개를 이 계획도 따른다.** 후속: Plan 2B(공개 Razor 페이지·검색·Atom·sitemap·보안 헤더·공개 속도 제한·`statement_timeout`·앱 검증⊆DB 제약 테스트), Plan 3(관리 SPA), Plan 4(배포).

## Global Constraints

- 대상 프레임워크 `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`. 빌드는 **경고 0·오류 0**. 솔루션 `PortfolioBlog.slnx`. 새 코드 네임스페이스는 `PortfolioBlog.Api.*`, 테스트는 `PortfolioBlog.Api.Tests.*`.
- 의존 방향: `Features → Infrastructure → Contracts → Domain`. `Features` 간 직접 참조 금지.
- **주석 규칙(CLAUDE.md "적용 범위" 표, 2026-09-21 개정):** 인터페이스·public 클래스와 그 메서드(생성자 포함)·확장 메서드·미들웨어·엔드포인트 클래스·테스트 클래스에는 한국어 `<summary>` + `<remarks>`의 `<b>[성능 및 동시성 제약 조건]</b>` 3항목(Thread Safety / Memory Allocation / Blocking). **자동 속성·상수·enum 멤버·DTO record·옵션 속성·테스트 메서드는 `<summary>`만**(내용 없는 상용구 remarks를 붙이지 않는다). 메모리·네트워크·동시성 타입(`Stream`, `ArrayPool`, `FileStream`, `IncrementalHash`, `RateLimiter`, `HtmlSanitizer` 공유 인스턴스 등) 선언부에는 "왜 이 타입인가"를 내부 동작 근거로 인라인 `//` 주석. 이 계획의 코드 블록은 대표 주석만 보이므로 구현 시 표에 맞게 채운다.
- 오류 응답은 `ProblemDetails`. 잘못된 입력은 필드 키가 있는 400이며 **500이 되어서는 안 된다**. DB에 닿는 모든 문자열(본문 필드·쿼리 문자열·경로 값·파일 이름)은 NUL 문자를 거부한다(`TextRules`).
- **선행 계획의 규칙 4개:** (1) 속도 제한 파티션은 원시 경로가 아니라 엔드포인트 메타데이터로 고른다. (2) 앱 검증과 DB 제약에 같은 정규식 문자열을 공유하지 않는다 — .NET 정규식은 `\A…\z`로 앵커한다(`$`는 끝의 개행 앞에서도 매칭된다). (3) `ExecuteUpdateAsync`/`ExecuteDeleteAsync`의 DB 오류는 `DbUpdateException`으로 감싸이지 않은 `PostgresException`이다. (4) NUL은 소스에서 C# 이스케이프(백슬래시-0)로만 표기한다 — 6글자 유니코드 이스케이프를 코드·주석·문서·보고서·셸 명령 어디에도 쓰지 않는다(도구가 실제 NUL 바이트로 바꾼 전례가 있다). 커밋 전에 변경 파일의 0x00 바이트를 검사한다.
- 접근 계약은 그대로다: `/api/*`는 관리 호스트·허용 IP·`X-Requested-With`·(변경 요청) Origin·세션을 **본문을 읽기 전에** 통과해야 한다. 새 `/api` 엔드포인트는 보호된 그룹 안에만 만들고 `AllowAnonymous`를 붙이지 않는다. `/api` 밖에 매핑하는 엔드포인트는 GET/HEAD 전용 공개 엔드포인트뿐이며 `AccessMatrixTests`의 공개 허용 목록에 명시적으로 올린다.
- 설정은 `builder.Build()` 이후에만 읽는다(`IOptions<T>` 지연 바인딩). 보안·저장 경로 설정 오류는 `StartupValidation`에서 시작 실패로 처리한다.
- 비밀번호·쿠키·요청 본문·글 본문·파일 내용은 로그에 남기지 않는다. 감사 로그는 id·slug·sha256·크기만.
- 서버는 업로드된 이미지를 **디코딩하지 않는다**(디코더는 새 공격 표면). 형식 판정은 시그니처, 메타데이터 제거는 컨테이너 구조 파싱으로만 한다. SVG는 허용하지 않는다.
- 커밋 메시지: `{접두사}: {제목}`(접두사: 추가|수정|버그수정|리팩토링|문서|테스트|의존성), 끝 줄은 정확히 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`(자기 모델 이름으로 바꾸지 않는다). `.git/auto_commit_msg.txt`를 만들지 않는다. 푸시·amend 금지.
- 새 `.cs` 파일은 CRLF(저장소 작업 트리 규약). 실제 비밀번호·해시·도메인·IP를 커밋하지 않는다. 절대 경로 하드코딩 금지.
- 테스트 실행 전 Docker를 실행해 둔다. 전체: `dotnet test PortfolioBlog.slnx`. 시작 기준선: **166개 통과, 경고 0**.

**사전 스파이크로 확인한 사실(2026-09-21, 이 저장소의 SDK·패키지 버전으로 실행):**
- `Markdig 1.4.0` + `.DisableHtml()` + 확장 6종, AST에서 `LinkInline`을 `MoveChildrenAfter(link)` + `Remove()`로 풀면 링크만 사라지고 안의 서식(굵게·기울임)은 남는다. (`ReplaceBy(new LiteralInline(...))`로 텍스트를 다시 만들면 **텍스트가 중복 출력**된다 — 쓰지 말 것.) Markdig는 URL의 HTML 엔티티를 파싱 단계에서 복원하므로 엔티티로 난독화한 스킴도 정책 검사에 걸린다.
- `ColorCode.HTML`의 `HtmlClassFormatter.GetHtmlString(code, language)`는 `<div class="csharp"><pre><span class="keyword">…` 형태로 **클래스만** 출력한다(인라인 `style` 없음). `Languages.FindById("csharp")`처럼 별칭이 통한다. 지원 언어: javascript, html, c#, vb.net, sql, xml, php, css, cpp, java, json, powershell, typescript, f#, haskell, markdown, fortran, python, matlab 등. bash·yaml·go·rust는 없다 → 일반 `<pre><code>`로 떨어진다.
- `HtmlSanitizer 9.2.1039`는 태그·속성·스킴·**클래스 허용 목록**(`AllowedClasses`)을 지원하고, 공유 인스턴스 하나로 8개 병렬 `Sanitize` 호출이 정상 동작했다. 160KB·코드블록 1,500개 문서의 전체 렌더링은 약 200ms다.
- 스트림 기반 메타데이터 제거기(이 계획 Task 4의 코드)를 PIL로 만든 실제 JPEG(EXIF+GPS+COM)·PNG(eXIf+tEXt+iTXt)·WebP(EXIF+XMP)·GIF(주석, 2프레임)에 적용한 결과: 메타데이터 0건, **디코딩한 픽셀 동일**, 프레임 수 유지, 재적용 시 바이트 동일(멱등), 잘린 파일은 `InvalidDataException`. 스캔 10개짜리 프로그레시브 JPEG 뒤에 ZIP 페이로드를 덧붙인 폴리글랏도 모든 스캔이 남고 EOI 뒤는 버려졌다. 그 이미지 5개가 `PortfolioBlog.Api.Tests/Fixtures/Images/`에 커밋되어 있다.

---

## 파일 구조 (이 계획이 만드는/바꾸는 파일)

```
Directory.Packages.props                              # 신규: 중앙 패키지 버전 관리
PortfolioBlog.Api/
  PortfolioBlog.Api.csproj                            # 수정: Version 속성 제거, 새 패키지 3개
  Program.cs                                          # 수정: AddAppRateLimiting, AddMarkdown, AddAttachments, FormOptions, 공개 첨부 엔드포인트
  appsettings.json / appsettings.Development.json     # 수정: Attachments:RootPath
  Contracts/TextRules.cs                              # 신규: NUL 규칙 단일 출처
  Contracts/PreviewDtos.cs, AttachmentDtos.cs         # 신규
  Domain/Attachment.cs                                # 신규
  Infrastructure/Web/ClientIp.cs                      # 신규: 속도 제한 파티션 키(IPv4-mapped 정규화, IPv6 /64)
  Infrastructure/Web/RateLimitPolicy.cs               # 신규: enum + RateLimitMetadata
  Infrastructure/Web/RateLimitingExtensions.cs        # 신규: 체인 구성 + Retry-After (Access에서 이전)
  Infrastructure/Access/AuthServiceCollectionExtensions.cs   # 수정: 속도 제한 코드 제거, 기본 인증 스킴 제거
  Infrastructure/Access/LoginRateLimitMetadata.cs     # 삭제(RateLimitMetadata로 대체)
  Infrastructure/Access/AdminOptions.cs               # 수정: PreviewPerMinute·PreviewConcurrency
  Infrastructure/Access/StartupValidation.cs          # 수정: Attachments:RootPath 검증
  Infrastructure/Markdown/UrlPolicy.cs, HighlightingCodeBlockRenderer.cs, HtmlAllowlist.cs, MarkdownRenderer.cs   # 신규
  Infrastructure/Storage/ImageKind.cs, ImageSignature.cs, MetadataStripper.cs      # 신규(순수 함수)
  Infrastructure/Storage/AttachmentOptions.cs, FileSystemAttachmentStore.cs        # 신규
  Infrastructure/Data/AppDbContext.cs                 # 수정: Attachments
  Infrastructure/Data/Migrations/*_AddAttachments.cs  # 생성
  Features/ApiEndpoints.cs                            # 수정: MapPreviewEndpoints, MapAttachmentEndpoints
  Features/Auth/AuthEndpoints.cs                      # 수정: RateLimitMetadata, me의 명시적 인증
  Features/Posts/PostValidation.cs, PostEndpoints.cs, Features/Series/SeriesValidation.cs, Infrastructure/Data/TagResolver.cs   # 수정: TextRules 사용
  Features/Preview/PreviewEndpoints.cs                # 신규
  Features/Attachments/AttachmentEndpoints.cs         # 신규: 관리 3종
  Features/Attachments/PublicAttachmentEndpoints.cs   # 신규: 공개 GET (/api 밖)
PortfolioBlog.Api.Tests/
  PortfolioBlog.Api.Tests.csproj                      # 수정: Version 제거, Fixtures 복사
  Fixtures/Images/{exif-gps.jpg, exif-text.png, exif-xmp.webp, comment-animated.gif, progressive-trailing.jpg, make-fixtures.py}   # 이미 커밋됨
  Infrastructure/TextRulesTests.cs, ClientIpTests.cs, UrlPolicyTests.cs, MarkdownRendererTests.cs, ImageSignatureTests.cs, MetadataStripperTests.cs
  Infrastructure/ApiFactory.cs                        # 수정: 미리보기 한도 기본값, 첨부 루트(임시 폴더)
  Features/RateLimitHeaderTests.cs, PreviewEndpointsTests.cs, AttachmentEndpointsTests.cs
  Features/AccessMatrixTests.cs                       # 수정: 닫힌 세계 검사, 라우트 수
.gitignore                                            # 수정: .data/
plan/tech_blog_0920.md                                # 수정(Task 5): 3.5·3.8의 "구현 계획에서 확정" 두 곳을 결정으로 교체
```

---

### Task 1: 기반 정리 — 중앙 패키지 버전 · 텍스트 규칙 · 속도 제한 이전 · 닫힌 세계 접근 매트릭스

> ⚠️ 이 Task의 일부 코드·문장은 리뷰에서 결함으로 판정되어 구현에서 교정됐다 — 문서 끝 "구현 중 발견해 고친 계획 결함" 표를 따를 것.

**Files:**
- Create: `Directory.Packages.props`, `PortfolioBlog.Api/Contracts/TextRules.cs`, `PortfolioBlog.Api/Infrastructure/Web/{ClientIp,RateLimitPolicy,RateLimitingExtensions}.cs`
- Delete: `PortfolioBlog.Api/Infrastructure/Access/LoginRateLimitMetadata.cs`
- Modify: 두 csproj, `Program.cs`, `Infrastructure/Access/AuthServiceCollectionExtensions.cs`, `Features/Auth/AuthEndpoints.cs`, `Features/Posts/{PostValidation,PostEndpoints}.cs`, `Features/Series/SeriesValidation.cs`, `Infrastructure/Data/TagResolver.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/{TextRulesTests,ClientIpTests}.cs`, `PortfolioBlog.Api.Tests/Features/RateLimitHeaderTests.cs`; Modify: `Features/AccessMatrixTests.cs`

**Interfaces:**
- Consumes: 기존 `AdminOptions`(`LoginPerIpPerMinute`, `LoginGlobalPerMinute`, `LoginConcurrency`), `ValidationErrors`, `ApiFactory`.
- Produces:
  - `TextRules.ContainsNul(string? value) : bool`, 상수 `TextRules.NulMessage = "제어 문자(NUL)를 포함할 수 없습니다."`
  - `ClientIp.PartitionKey(IPAddress? ip) : string` — null → `""`, IPv4-mapped → IPv4 표기, IPv6 → 앞 64비트 + `/64`.
  - `enum RateLimitPolicy { Login, Preview }`, `sealed record RateLimitMetadata(RateLimitPolicy Policy)`.
  - `RateLimitingExtensions.AddAppRateLimiting(this IServiceCollection) : IServiceCollection` — 체인 `GlobalLimiter`, 429 + `Retry-After`. `AddAdminAuth()`는 더 이상 속도 제한을 등록하지 않는다.
  - 인증은 기본 스킴 없이 등록된다: `AddAuthentication().AddCookie(Scheme)`. `Admin` 정책이 스킴을 지정하므로 보호 엔드포인트는 그대로 동작하고, `GET /api/auth/me`는 핸들러에서 `AuthenticateAsync(Scheme)`를 직접 호출한다. 효과: 쿠키가 실린 요청이라도 정책이 없는 경로(`/health`, 공개 첨부 GET)에서는 세션 검증 DB 조회가 일어나지 않는다.
  - `AccessMatrixTests.PublicAllowlist`(테스트): `/api` 밖 라우트는 이 목록에 있어야 하고 GET/HEAD만 허용.

- [ ] **Step 1: 중앙 패키지 버전 관리** — 저장소 루트에 `Directory.Packages.props` 생성:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <!-- 전이 의존성도 아래 버전으로 고정한다: Design 패키지(PrivateAssets)가 끌어오는 EF Core 버전과
         테스트 프로젝트가 전이로 받는 버전이 어긋나 CS1705가 났던 문제의 근본 처방. -->
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.11" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.15.0" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
```

두 csproj의 모든 `<PackageReference>`에서 `Version="…"` 속성을 지운다(`PrivateAssets`·`IncludeAssets` 등 다른 속성과 자식 요소는 그대로). 그다음 테스트 csproj의 `Microsoft.EntityFrameworkCore.Relational` 참조와 그 위 설명 주석을 **지우고** 빌드한다.

Run: `dotnet build PortfolioBlog.slnx`
Expected: 경고 0·오류 0. **CS1705(어셈블리 버전 불일치)가 다시 나오면** 전이 고정이 이 구성에서 통하지 않는 것이다 → 테스트 csproj에 `<PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />`(버전 없음)만 되살리고 주석에 이유를 남긴 뒤 보고서에 적는다. `NU1507`(패키지 소스 여러 개 경고)이 나오면 경고 0을 위해 저장소 루트 `nuget.config`를 만들지 말고 보고한다(이 머신 설정 문제).

Run: `dotnet test PortfolioBlog.slnx` → 166 통과.

- [ ] **Step 2: 실패하는 단위 테스트 작성**

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/TextRulesTests.cs
using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>DB에 닿는 문자열의 공통 규칙(<see cref="TextRules"/>) 단위 테스트.</summary>
public sealed class TextRulesTests
{
    /// <summary>NUL이 어디에 있든 잡아내고, null·빈 문자열·다른 제어 문자는 통과시킨다(PostgreSQL text가 못 담는 것은 NUL뿐이다).</summary>
    [Theory]
    [InlineData("\0", true)]
    [InlineData("a\0b", true)]
    [InlineData("끝\0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("탭\t개행\n은 허용", false)]
    public void ContainsNul_DetectsOnlyNul(string? value, bool expected) =>
        Assert.Equal(expected, TextRules.ContainsNul(value));
}
```

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/ClientIpTests.cs
using System.Net;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>속도 제한 파티션 키 정규화 단위 테스트. 같은 클라이언트가 표기만 바꿔 예산을 여러 개 얻지 못해야 한다.</summary>
public sealed class ClientIpTests
{
    /// <summary>IPv4와 그 IPv4-mapped IPv6 표기는 같은 키가 된다.</summary>
    [Fact]
    public void PartitionKey_Ipv4Mapped_EqualsPlainIpv4() =>
        Assert.Equal(ClientIp.PartitionKey(IPAddress.Parse("203.0.113.9")), ClientIp.PartitionKey(IPAddress.Parse("::ffff:203.0.113.9")));

    /// <summary>IPv6는 /64 단위로 묶는다: 한 가입자가 받은 /64 안에서 주소를 바꿔 가며 한도를 피하지 못한다.</summary>
    [Fact]
    public void PartitionKey_Ipv6_IsBucketedBySlash64()
    {
        var a = ClientIp.PartitionKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        var b = ClientIp.PartitionKey(IPAddress.Parse("2001:db8:1:2:bbbb::2"));
        var other = ClientIp.PartitionKey(IPAddress.Parse("2001:db8:1:3::1"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.EndsWith("/64", a, StringComparison.Ordinal);
    }

    /// <summary>주소가 없으면 빈 키(하나의 공용 예산 — 더 엄격한 쪽)로 떨어진다.</summary>
    [Fact]
    public void PartitionKey_Null_IsEmpty() => Assert.Equal(string.Empty, ClientIp.PartitionKey(null));

    /// <summary>서로 다른 IPv4는 서로 다른 키다.</summary>
    [Fact]
    public void PartitionKey_DifferentIpv4_Differ() =>
        Assert.NotEqual(ClientIp.PartitionKey(IPAddress.Parse("203.0.113.9")), ClientIp.PartitionKey(IPAddress.Parse("203.0.113.10")));
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~TextRulesTests|FullyQualifiedName~ClientIpTests"`
Expected: 컴파일 오류(타입 없음).

- [ ] **Step 3: `TextRules` · `ClientIp` 구현**

```csharp
// PortfolioBlog.Api/Contracts/TextRules.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary>DB에 닿는 모든 문자열이 지키는 공통 규칙의 단일 출처.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(벡터화된 문자 검색).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// PostgreSQL <c>text</c>는 NUL을 저장할 수 없고(SqlState 22021) JSON·쿼리 문자열은 NUL을 실어 나를 수 있다.
/// 본문 필드뿐 아니라 쿼리 문자열·경로 값·파일 이름까지 같은 규칙으로 막아 "검증 통과 → DB에서 500"을 없앤다.
/// </remarks>
public static class TextRules
{
    /// <summary>NUL 거부 시 필드 오류 메시지.</summary>
    public const string NulMessage = "제어 문자(NUL)를 포함할 수 없습니다.";

    public static bool ContainsNul(string? value) => value is not null && value.Contains('\0');
}
```

```csharp
// PortfolioBlog.Api/Infrastructure/Web/ClientIp.cs
using System.Net;
using System.Net.Sockets;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>속도 제한 파티션 키로 쓸 클라이언트 주소 정규화.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> 키 문자열 1개(IPv6는 16바이트 스택 버퍼를 거친다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// 접근 판정(<c>IAdminAccessPolicy</c>)과 로그에는 원래 주소를 그대로 쓴다 — 이 정규화는 파티션 키 전용이다.
/// </remarks>
public static class ClientIp
{
    public static string PartitionKey(IPAddress? ip)
    {
        if (ip is null) return string.Empty;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        bytes[8..].Clear(); // 뒤 64비트(인터페이스 ID)를 버린다
        return new IPAddress(bytes).ToString() + "/64";
    }
}
```

Run: 위 필터 → 10개 PASS(Theory 6 + Fact 4).

- [ ] **Step 4: NUL 검사 호출부를 `TextRules`로 교체** — 동작은 그대로, 중복만 제거한다.
  - `Features/Posts/PostValidation.cs`: `req.Slug.Contains('\0')` → `TextRules.ContainsNul(req.Slug)`, `title`·`contentMarkdown`도 같게. `req.Summary?.Contains('\0') == true` → `TextRules.ContainsNul(req.Summary)`. 메시지 리터럴 4곳 → `TextRules.NulMessage`.
  - `Features/Series/SeriesValidation.cs`: 같은 방식 3곳.
  - `Features/Posts/PostEndpoints.cs` `ListAsync`: `term?.Contains('\0') == true` → `TextRules.ContainsNul(term)`, 메시지 → `TextRules.NulMessage`.
  - `Infrastructure/Data/TagResolver.cs` `Validate`: `display.Contains('\0')` → `TextRules.ContainsNul(display)`(메시지는 기존 "태그는 …" 문구 유지).

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~PostEndpointsTests|FullyQualifiedName~SeriesEndpointsTests|FullyQualifiedName~TagResolverTests"` → 전부 PASS(어서션 변경 없음).

- [ ] **Step 5: 속도 제한 헤더 테스트 작성(실패 확인용)** — `PortfolioBlog.Api.Tests/Features/RateLimitHeaderTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>429 응답이 재시도 시점을 알려 주는지 검증한다(관리 SPA가 무작정 재시도하지 않게).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 격리된 <see cref="ApiFactory"/>(자체 DB·자체 제한기 상태)를 만든다.</description></item>
/// <item><description><b>Memory Allocation:</b> 팩토리·HttpClient는 <c>using</c>으로 해제.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너에 접속한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class RateLimitHeaderTests(PostgresContainerFixture pg)
{
    /// <summary>고정 창(1분) 한도를 넘긴 로그인은 429와 함께 1~60초의 Retry-After를 받는다.</summary>
    [Fact]
    public async Task LoginOverLimit_Returns429_WithRetryAfterSeconds()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:LoginPerIpPerMinute"] = "1" });
        using var client = factory.CreateAdminClient(handleCookies: false);
        using var first = await client.PostAsJsonAsync("/api/auth/login", new { password = "wrong-dummy-value" });
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var second = await client.PostAsJsonAsync("/api/auth/login", new { password = "wrong-dummy-value" });
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.True(second.Headers.TryGetValues("Retry-After", out var values), "Retry-After 헤더가 없다.");
        var seconds = int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 60);
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~RateLimitHeaderTests"`
Expected: FAIL — "Retry-After 헤더가 없다."

- [ ] **Step 6: 속도 제한을 `Infrastructure/Web`으로 이전**

```csharp
// PortfolioBlog.Api/Infrastructure/Web/RateLimitPolicy.cs
namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>엔드포인트에 붙여 어떤 제한기 묶음을 적용할지 고르는 정책 이름. Plan 2B가 <c>Search</c>·<c>PublicPage</c>를 추가한다.</summary>
public enum RateLimitPolicy
{
    /// <summary>비밀번호 로그인: IP별·전역 고정 창 + 해시 검증 동시 실행 제한.</summary>
    Login,
    /// <summary>마크다운 미리보기: 전역 고정 창 + 렌더링 동시 실행 제한.</summary>
    Preview,
}

/// <summary>엔드포인트 메타데이터 마커. 제한기는 <b>원시 경로가 아니라</b> 라우팅이 선택한 엔드포인트의 이 메타데이터로 파티션을 고른다
/// (경로 문자열 비교는 끝 슬래시·대소문자 변형으로 우회된 전례가 있다).</summary>
public sealed record RateLimitMetadata(RateLimitPolicy Policy);
```

```csharp
// PortfolioBlog.Api/Infrastructure/Web/RateLimitingExtensions.cs
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>앱 전체의 속도 제한 체인을 등록한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 등록은 시작 시 1회. 제한기 자체는 프레임워크가 Thread-safe하게 관리한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 파티션(키)마다 제한기 1개. 로그인 IP 파티션은 허용 IP 수로, 나머지는 상수로 한정된다.</description></item>
/// <item><description><b>Blocking:</b> 대기열 0 — 한도를 넘으면 기다리지 않고 즉시 429.</description></item>
/// </list>
/// 미들웨어 위치는 <c>AdminSurfaceMiddleware</c> 뒤다: 허용 IP 밖의 요청이 한도를 소진하지 못한다.
/// <c>WebApplication</c>은 라우팅을 사용자 미들웨어보다 앞에 두므로 제한기가 돌 때 <c>GetEndpoint()</c>는 이미 채워져 있다.
/// </remarks>
public static class RateLimitingExtensions
{
    private const int FallbackRetryAfterSeconds = 60;

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>>((o, adminOptions) =>
        {
            var admin = adminOptions.Value;
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = static (context, _) =>
            {
                // 고정 창 제한기는 창이 끝나는 시점을 RetryAfter 메타데이터로 준다. 동시성 제한기는 주지 않으므로 보수적인 기본값을 쓴다.
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Clamp((int)Math.Ceiling(retryAfter.TotalSeconds), 1, FallbackRetryAfterSeconds)
                    : FallbackRetryAfterSeconds;
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            // 체인: 모든 제한기를 통과해야 한다. 해당 정책이 아닌 요청은 NoLimiter 파티션으로 빠진다.
            // CreateChained는 앞에서부터 permit을 빌린다 — 뒤 제한기가 거부해도 앞에서 빌린 permit은 돌아오지 않는다(고정 창에는 반환 API가 없다). 의도된 보수적 동작이다.
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                // FixedWindow: 창마다 카운터 하나만 두는 O(1) 제한기.
                Window(RateLimitPolicy.Login, ctx => "login-ip:" + ClientIp.PartitionKey(ctx.Connection.RemoteIpAddress), admin.LoginPerIpPerMinute),
                Window(RateLimitPolicy.Login, _ => "login-global", admin.LoginGlobalPerMinute),
                // Concurrency: PBKDF2 검증은 CPU 바운드라 동시에 도는 수를 묶는다. 임대는 요청이 끝날 때 미들웨어가 반납한다.
                Concurrency(RateLimitPolicy.Login, "login-concurrency", admin.LoginConcurrency));
        });
        return services;
    }

    internal static bool Matches(HttpContext ctx, RateLimitPolicy policy) =>
        ctx.GetEndpoint()?.Metadata.GetMetadata<RateLimitMetadata>()?.Policy == policy;

    internal static PartitionedRateLimiter<HttpContext> Window(RateLimitPolicy policy, Func<HttpContext, string> key, int permitsPerMinute) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => Matches(ctx, policy)
            ? RateLimitPartition.GetFixedWindowLimiter(key(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
            })
            : RateLimitPartition.GetNoLimiter("none"));

    internal static PartitionedRateLimiter<HttpContext> Concurrency(RateLimitPolicy policy, string key, int permits) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => Matches(ctx, policy)
            ? RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = permits, QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("none"));
}
```

정리:
- `Infrastructure/Access/AuthServiceCollectionExtensions.cs`에서 `AddRateLimiter`부터 `RateLimiterOptions` 구성 블록, `IsLogin`·`NormalizedIp`·`Window` 헬퍼와 더는 쓰지 않는 `using`을 지운다.
- `Infrastructure/Access/LoginRateLimitMetadata.cs` 삭제. `Features/Auth/AuthEndpoints.cs`의 `.WithMetadata(new LoginRateLimitMetadata())` → `.WithMetadata(new RateLimitMetadata(RateLimitPolicy.Login))`(using `PortfolioBlog.Api.Infrastructure.Web`). 로그인은 POST 전용 라우트라 메서드 검사는 필요 없다.
- `Program.cs`: `builder.Services.AddAdminAuth();` 다음 줄에 `builder.Services.AddAppRateLimiting();`(using `PortfolioBlog.Api.Infrastructure.Web`). `app.UseRateLimiter()`의 위치는 그대로.
- `plan/tech_blog_0920.md` 3.3절 불릿의 `LoginRateLimitMetadata`를 `RateLimitMetadata(RateLimitPolicy.Login)`로 고친다.

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~RateLimitHeaderTests|FullyQualifiedName~AuthEndpointsTests"`
Expected: 전부 PASS(기존 로그인 속도 제한 테스트 5종 포함 — 경로 변형, IP별, 전역, 외부 IP 비소진, IPv4-mapped 공유 예산).
`Retry-After`가 여전히 없으면 체인 임대가 메타데이터를 전달하지 않는 것이다 → 그 사실을 보고서에 적고, `TryGetMetadata` 실패 분기(기본값 60)가 헤더를 쓰는지 확인한다(테스트의 1~60 범위는 그대로 만족해야 한다).

- [ ] **Step 7: 기본 인증 스킴 제거** — `AuthServiceCollectionExtensions.cs`의 `services.AddAuthentication(Scheme).AddCookie(Scheme);`를 `services.AddAuthentication().AddCookie(Scheme);`로 바꾸고 주석을 단다: "기본 스킴을 두지 않는다 — 쿠키가 실린 요청이라도 `Admin` 정책(스킴을 직접 지정)이 걸린 엔드포인트에서만 티켓 검증과 세션 DB 조회가 일어난다. 공개 경로(`/health`, 첨부 GET)는 인증 비용이 0이다."
`Features/Auth/AuthEndpoints.cs`의 `me` 핸들러를 다음으로 바꾼다(using `Microsoft.AspNetCore.Authentication`):

```csharp
        auth.MapGet("/me", async (HttpContext ctx) =>
        {
            // 기본 인증 스킴이 없으므로 익명 허용 엔드포인트는 필요한 스킴을 직접 인증한다(SessionValidator도 이 경로로 실행된다).
            var result = await ctx.AuthenticateAsync(AuthServiceCollectionExtensions.Scheme);
            return TypedResults.Ok(new AuthStatusDto(result.Succeeded));
        }).AllowAnonymous().WithName("GetAuthStatus");
```

`LoginAsync`·`LogoutAsync`의 `SignInAsync`/`SignOutAsync`는 이미 스킴을 명시하므로 그대로다. `SessionValidator`의 `SignOutAsync(Scheme)`도 그대로다.

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AuthEndpointsTests|FullyQualifiedName~AccessMatrixTests"`
Expected: 전부 PASS. 특히 `Me_ReflectsSession`, `Logout_RevokesEverySession_IncludingCopiedCookies`, `Session_ExpiresAfterAbsoluteLifetime_NoSliding`, `HashRotation…`이 통과해야 한다(=`me`가 여전히 서버 측 검증을 거친다). 하나라도 실패하면 이 Step을 되돌리지 말고 원인을 찾는다 — 실패는 인증 경로가 달라졌다는 뜻이다.

- [ ] **Step 8: 접근 매트릭스를 닫힌 세계로** — `Features/AccessMatrixTests.cs`에 추가:

```csharp
    /// <summary><c>/api</c> 밖에 매핑해도 되는 공개 라우트의 명시적 허용 목록. 여기에 없는 라우트가 생기면 테스트가 실패한다.
    /// Task 5가 공개 첨부 GET을, Plan 2B가 공개 페이지들을 추가한다.</summary>
    private static readonly string[] PublicAllowlist =
    [
        "/health",
        "/openapi/{documentName}.json", // Development에서만 매핑된다
    ];

    /// <summary><c>/api</c> 밖의 모든 라우트는 허용 목록에 있어야 하고 GET/HEAD만 받아야 한다 —
    /// 관리 핸들러를 실수로 <c>/api</c> 그룹 밖에 매핑하면(그러면 어떤 접근 검사도 받지 않는다) 여기서 잡힌다.</summary>
    [Fact]
    public void EveryRouteOutsideApi_IsOnThePublicAllowlist_AndReadOnly()
    {
        using var _ = factory.CreateClient();
        var outside = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => !(e.RoutePattern.RawText ?? string.Empty).StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(outside); // 최소한 /health는 있어야 한다(열거가 비어 통과하는 일을 막는다)
        foreach (var endpoint in outside)
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            Assert.True(PublicAllowlist.Contains(raw, StringComparer.Ordinal), $"허용 목록에 없는 공개 라우트: {raw}");
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            Assert.True(methods.Count > 0 && methods.All(m => m is "GET" or "HEAD"), $"{raw}: 공개 라우트는 GET/HEAD 전용이어야 한다(실제: {string.Join(",", methods)})");
        }
    }
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AccessMatrixTests"`
Expected: 7개 PASS. 허용 목록에 없는 라우트가 보고되면 그 `RawText`를 그대로 보고서에 적는다 — 프레임워크가 만든 라우트라면 목록에 추가하되 이유를 주석으로 남기고, 앱 코드가 만든 것이면 BLOCKED로 보고한다.

- [ ] **Step 9: 전체 회귀 · 위생 · 커밋**

```bash
dotnet build PortfolioBlog.slnx -c Release
dotnet test PortfolioBlog.slnx -c Release
```
Expected: 경고 0·오류 0, 178개 통과(166 + TextRules 6 + ClientIp 4 + RateLimitHeader 1 + AccessMatrix 1).
변경 파일의 0x00 바이트를 검사한 뒤:

```bash
git add -A
git commit -m "리팩토링: 속도 제한·텍스트 규칙을 공용 기반으로 옮기고 접근 매트릭스를 닫힌 세계로

- 패키지 버전을 중앙 관리하고 전이 의존성을 고정(EF 버전 불일치의 근본 처방)
- NUL 규칙을 TextRules 한 곳으로, 속도 제한을 Infrastructure/Web으로 이전하고 429에 Retry-After 추가
- 파티션 키를 IPv4-mapped 정규화 + IPv6 /64로 묶어 주소 표기만 바꾼 예산 회피 차단
- 기본 인증 스킴 제거: 정책이 없는 공개 경로에서는 세션 DB 조회가 일어나지 않음
- /api 밖 라우트는 명시적 허용 목록 + GET/HEAD 전용임을 테스트로 고정

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 2: 마크다운 정제 파이프라인

> ⚠️ 이 Task의 일부 코드·문장은 리뷰에서 결함으로 판정되어 구현에서 교정됐다 — 문서 끝 "구현 중 발견해 고친 계획 결함" 표를 따를 것.

**Files:**
- Modify: `Directory.Packages.props`, `PortfolioBlog.Api/PortfolioBlog.Api.csproj`, `PortfolioBlog.Api/Program.cs`
- Create: `PortfolioBlog.Api/Infrastructure/Markdown/{UrlPolicy,HighlightingCodeBlockRenderer,HtmlAllowlist,MarkdownRenderer}.cs`
- Test: `PortfolioBlog.Api.Tests/Infrastructure/{UrlPolicyTests,MarkdownRendererTests}.cs`

**Interfaces:**
- Consumes: 없음(순수 함수. DB·시계·설정에 의존하지 않는다).
- Produces:
  - `UrlPolicy.IsAllowedLink(string? url) : bool`, `UrlPolicy.IsAllowedImage(string? url) : bool`
  - `MarkdownRenderer`(sealed class, 싱글턴 등록): `string Render(string markdown)`, 상수 `MarkdownRenderer.MaxInputBytes = 204_800`. 입력이 상한을 넘으면 `ArgumentException`(호출부가 먼저 검증해야 한다).
  - `HtmlAllowlist.Create() : HtmlSanitizer`, `HtmlAllowlist.AllowedTags`/`AllowedAttributes`(테스트가 읽는 `IReadOnlySet<string>`).
  - DI: `builder.Services.AddSingleton<MarkdownRenderer>()`.

- [ ] **Step 1: 패키지 추가** — `Directory.Packages.props`의 `<ItemGroup>`에 추가:

```xml
    <PackageVersion Include="Markdig" Version="1.4.0" />
    <PackageVersion Include="ColorCode.HTML" Version="2.0.15" />
    <PackageVersion Include="HtmlSanitizer" Version="9.2.1039" />
```

`PortfolioBlog.Api.csproj`의 패키지 `<ItemGroup>`에 추가(버전 없음 — 중앙 관리):

```xml
    <PackageReference Include="Markdig" />
    <PackageReference Include="ColorCode.HTML" />
    <PackageReference Include="HtmlSanitizer" />
```

Run: `dotnet build PortfolioBlog.slnx` → 경고 0. `dotnet list PortfolioBlog.Api package --include-transitive`에 새로 보이는 것은 `Markdig`, `ColorCode.HTML`, `ColorCode.Core`, `HtmlSanitizer`, `AngleSharp`, `AngleSharp.Css` 6개뿐이어야 한다(다르면 보고).

- [ ] **Step 2: URL 정책 테스트 작성** — `PortfolioBlog.Api.Tests/Infrastructure/UrlPolicyTests.cs`

```csharp
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>마크다운 링크·이미지 URL 정책 단위 테스트. 허용 목록 방식이라 "허용되는 것"을 좁게, "거부되는 것"을 넓게 고정한다.</summary>
public sealed class UrlPolicyTests
{
    /// <summary>링크는 http·https·mailto 절대 URL, 같은 사이트의 루트 상대 경로, 문서 내 앵커만 허용한다.</summary>
    [Theory]
    [InlineData("https://example.test/a?b=1#c")]
    [InlineData("http://example.test")]
    [InlineData("HTTPS://EXAMPLE.TEST/UPPER")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("/posts/my-post")]
    [InlineData("/tags/C%23")]
    [InlineData("#section-1")]
    public void IsAllowedLink_Accepts(string url) => Assert.True(UrlPolicy.IsAllowedLink(url));

    /// <summary>스크립트·데이터 스킴, 프로토콜 상대, 백슬래시 트릭, 공백·제어문자 난독화, 상대 경로는 전부 거부한다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/x")]
    [InlineData("//evil.test/x")]
    [InlineData("/\\evil.test/x")]
    [InlineData("\\\\evil.test\\x")]
    [InlineData("https:\\\\evil.test")]
    [InlineData("relative/path")]
    [InlineData("../up")]
    [InlineData("https://example.test/a b")]
    [InlineData("https://exa\0mple.test")]
    public void IsAllowedLink_Rejects(string? url) => Assert.False(UrlPolicy.IsAllowedLink(url));

    /// <summary>이미지는 이 사이트가 직접 서빙하는 첨부 경로(<c>/attachments/{guid}/{파일명}</c>)만 허용한다.</summary>
    [Theory]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/diagram.png")]
    [InlineData("/attachments/0192F0C4-7A3B-7C1D-9E2F-1A2B3C4D5E6F/%ED%95%9C%EA%B8%80.webp")]
    public void IsAllowedImage_Accepts(string url) => Assert.True(UrlPolicy.IsAllowedImage(url));

    /// <summary>외부 이미지(핫링크·추적 픽셀), data URI, 경로 탈출, 질의 문자열은 거부한다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.test/pixel.png")]
    [InlineData("//evil.test/pixel.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/attachments/../api/posts")]
    [InlineData("/attachments/%2e%2e/api/posts")]
    [InlineData("/attachments/not-a-guid/x.png")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/a/b.png")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png?download=1")]
    [InlineData("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png\n")]
    [InlineData("/Attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png")]
    [InlineData("/posts/my-post")]
    public void IsAllowedImage_Rejects(string? url) => Assert.False(UrlPolicy.IsAllowedImage(url));
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~UrlPolicyTests"` → 컴파일 오류.

- [ ] **Step 3: `UrlPolicy` 구현** — `PortfolioBlog.Api/Infrastructure/Markdown/UrlPolicy.cs`

```csharp
using System.Text.RegularExpressions;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>마크다운의 링크·이미지 URL을 허용 목록으로 판정한다. 통과하지 못한 URL은 렌더러가 링크를 풀어 텍스트만 남긴다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
/// <item><description><b>Memory Allocation:</b> 절대 URL 판정 시 <see cref="Uri"/> 1개. 그 외 경로는 할당 없음.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 네트워크·DNS 조회 없음.</description></item>
/// </list>
/// 차단 목록이 아니라 허용 목록이다: 브라우저마다 다른 URL 정규화(공백·제어문자 제거, 백슬래시를 슬래시로 취급)를 흉내 내지 않고,
/// 그런 문자가 하나라도 있으면 거부한다. 이미지를 자체 첨부로 한정하는 것은 CSP <c>img-src 'self'</c>와 이중 방어이자 추적 픽셀·핫링크 차단이다.
/// </remarks>
public static partial class UrlPolicy
{
    // \A…\z: .NET의 $는 끝의 개행 앞에서도 매칭된다(선행 계획 정오표 규칙 2). 파일명 세그먼트는 '/', '?', '#', '\', 공백·제어문자를 뺀 1자 이상.
    [GeneratedRegex(@"\A/attachments/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/[^/?#\\\s\p{Cc}]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPath();

    public static bool IsAllowedLink(string? url)
    {
        if (!IsClean(url)) return false;
        if (url![0] == '#') return true;
        if (url[0] == '/') return url.Length == 1 || url[1] != '/'; // 루트 상대. "//host"(프로토콜 상대)는 거부
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
    }

    public static bool IsAllowedImage(string? url) =>
        IsClean(url) && !url!.Contains("..", StringComparison.Ordinal) && !url.Contains("%2e", StringComparison.OrdinalIgnoreCase) && AttachmentPath().IsMatch(url);

    /// <summary>비어 있지 않고 공백·제어문자·백슬래시가 없는가.</summary>
    private static bool IsClean(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        foreach (var c in url)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') return false;
        }
        return true;
    }
}
```

Run: 같은 필터 → 42개 PASS(7 + 20 + 2 + 13). `Uri.UriSchemeMailto` 상수가 없다는 컴파일 오류가 나면 `"mailto"` 리터럴로 비교한다.

- [ ] **Step 4: 렌더러 테스트 작성(공격 코퍼스)** — `PortfolioBlog.Api.Tests/Infrastructure/MarkdownRendererTests.cs`

```csharp
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PortfolioBlog.Api.Infrastructure.Markdown;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>마크다운 → 안전한 HTML 파이프라인 테스트. 문자열 비교가 아니라 <b>출력을 다시 파싱한 DOM</b>으로 "실행 가능한 것이 0개"임을 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 렌더러는 불변 싱글턴이라 테스트 간 공유한다(병렬 테스트 1개가 이를 직접 검증).</description></item>
/// <item><description><b>Memory Allocation:</b> 입력 크기에 비례. 가장 큰 입력은 약 190KB.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업만. DB·Docker 불필요.</description></item>
/// </list>
/// </remarks>
public sealed class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new();
    private static readonly string[] UrlAttributes = ["href", "src"];

    private static IElement Parse(string html) => new HtmlParser().ParseDocument("<body>" + html + "</body>").Body!;

    /// <summary>출력 DOM 전체를 훑어 허용 목록 밖의 태그·속성, 이벤트 핸들러, 정책 밖 URL이 하나도 없음을 단언한다.</summary>
    private static void AssertInert(string html)
    {
        foreach (var element in Parse(html).QuerySelectorAll("*"))
        {
            Assert.True(HtmlAllowlist.AllowedTags.Contains(element.LocalName), $"허용되지 않은 태그: <{element.LocalName}> in {html}");
            foreach (var attribute in element.Attributes)
            {
                Assert.True(HtmlAllowlist.AllowedAttributes.Contains(attribute.Name), $"허용되지 않은 속성: {attribute.Name} in {html}");
                Assert.False(attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"이벤트 핸들러 속성: {attribute.Name}");
            }
            if (element.GetAttribute("href") is { } href) Assert.True(UrlPolicy.IsAllowedLink(href), $"정책 밖 href: {href}");
            if (element.GetAttribute("src") is { } src) Assert.True(UrlPolicy.IsAllowedImage(src), $"정책 밖 src: {src}");
            if (element.LocalName == "input") Assert.Equal("checkbox", element.GetAttribute("type"));
        }
    }

    /// <summary>공격 입력은 전부 비활성 출력이 된다.</summary>
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"//evil.test\"></iframe>")]
    [InlineData("<svg onload=alert(1)>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<form action=\"/api/auth/logout\" method=post><button>x</button></form>")]
    [InlineData("<details open ontoggle=alert(1)>x</details>")]
    [InlineData("<style>body{display:none}</style>")]
    [InlineData("<base href=\"//evil.test/\">")]
    [InlineData("<meta http-equiv=refresh content=\"0;url=//evil.test\">")]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](&#106;avascript:alert(1))")]
    [InlineData("[x](&#x6A;avascript&colon;alert(1))")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[x](//evil.test/x)")]
    [InlineData("[x](/\\evil.test)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("![i](https://evil.test/pixel.png)")]
    [InlineData("![i](data:image/png;base64,AAAA)")]
    [InlineData("![i](/attachments/../api/posts)")]
    [InlineData("![i](/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/x.png \"t\\\" onerror=\\\"alert(1)\")")]
    [InlineData("[x](https://ok.test \"t\\\" onmouseover=\\\"alert(1)\")")]
    [InlineData("# Title {#id .cls onclick=alert(1)}")]
    [InlineData("text{onmouseover=alert(1)}")]
    [InlineData("```html\n<script>alert(1)</script>\n```")]
    [InlineData("```\"><script>alert(1)</script>\ncode\n```")]
    [InlineData("    <script>alert(1)</script>")]
    [InlineData("`<script>alert(1)</script>`")]
    [InlineData("| a |\n|---|\n| <script>alert(1)</script> |")]
    [InlineData("- [x] <input type=text autofocus onfocus=alert(1)>")]
    public void Render_AttackInput_ProducesInertOutput(string markdown)
    {
        var html = Renderer.Render(markdown);
        AssertInert(html);
        var body = Parse(html);
        Assert.Empty(body.QuerySelectorAll("script, iframe, svg, form, button, style, base, meta, details, object, embed, link"));
    }

    /// <summary>거부된 링크는 사라지되 글자와 안쪽 서식은 남고, 텍스트가 중복되지 않는다.</summary>
    [Fact]
    public void Render_RejectedLink_KeepsTextOnce_AndInnerFormatting()
    {
        var body = Parse(Renderer.Render("**[굵은 *기울임* 링크](javascript:alert(1))** 끝"));
        Assert.Empty(body.QuerySelectorAll("a"));
        Assert.Equal("굵은 기울임 링크 끝", body.TextContent.Trim());
        Assert.NotNull(body.QuerySelector("strong em"));
    }

    /// <summary>거부된 이미지는 대체 텍스트만 남는다.</summary>
    [Fact]
    public void Render_RejectedImage_LeavesAltText()
    {
        var body = Parse(Renderer.Render("앞 ![대체 텍스트](https://evil.test/a.png) 뒤"));
        Assert.Empty(body.QuerySelectorAll("img"));
        Assert.Equal("앞 대체 텍스트 뒤", body.TextContent.Trim());
    }

    /// <summary>정상 문서의 기능은 유지된다: 링크·자체 첨부 이미지·표·작업 목록·취소선·각주·제목 앵커·자동 링크.</summary>
    [Fact]
    public void Render_LegitimateDocument_KeepsFeatures()
    {
        const string markdown = """
            ## 한글 제목

            [외부](https://example.test/a) [내부](/posts/x) [앵커](#한글-제목) <https://auto.test/b> https://bare.test/c

            ![도식](/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/diagram.png "캡션")

            | 열1 | 열2 |
            |---|---|
            | a | b |

            - [x] 완료
            - [ ] 예정

            ~~취소~~ 각주[^1]

            [^1]: 각주 본문
            """;
        var html = Renderer.Render(markdown);
        AssertInert(html);
        var body = Parse(html);

        Assert.Equal("한글-제목", body.QuerySelector("h2")!.Id);
        Assert.Equal(5, body.QuerySelector("h2 + p")!.QuerySelectorAll("a[href]").Length); // 제목 바로 다음 문단의 링크 5개(각주 앵커는 다른 문단에 있다)
        var img = body.QuerySelector("img")!;
        Assert.Equal("/attachments/0192f0c4-7a3b-7c1d-9e2f-1a2b3c4d5e6f/diagram.png", img.GetAttribute("src"));
        Assert.Equal("도식", img.GetAttribute("alt"));
        Assert.Equal("캡션", img.GetAttribute("title"));
        Assert.NotNull(body.QuerySelector("table thead th"));
        Assert.Equal(2, body.QuerySelectorAll("li.task-list-item input[type=checkbox][disabled]").Length);
        Assert.NotNull(body.QuerySelector("del"));
        Assert.NotNull(body.QuerySelector("a.footnote-ref"));
    }

    /// <summary>지원 언어는 CSS 클래스로만 강조하고(인라인 style 금지 — CSP style-src 'self'), 코드 안의 HTML은 이스케이프된다. 미지원 언어는 일반 코드블록이다.</summary>
    [Fact]
    public void Render_CodeBlocks_HighlightWithClassesOnly_AndEscapeContent()
    {
        var html = Renderer.Render("```csharp\nvar s = \"<b>\"; // 주석\n```\n\n```bash\necho \"<x>\"\n```\n");
        AssertInert(html);
        var body = Parse(html);

        Assert.NotNull(body.QuerySelector("div.csharp pre span.keyword"));
        Assert.Empty(body.QuerySelectorAll("[style]"));
        Assert.Empty(body.QuerySelectorAll("b, x"));
        var plain = body.QuerySelectorAll("pre > code").Single();
        Assert.Contains("echo \"<x>\"", plain.TextContent, StringComparison.Ordinal);
    }

    /// <summary>허용 목록에 없는 클래스는 남지 않는다(정제기가 임의 클래스를 걸러 낸다).</summary>
    [Fact]
    public void Render_OutputClasses_AreFromTheAllowlistOnly()
    {
        var html = Renderer.Render("```csharp\nclass C { }\n```\n\n- [ ] 할 일\n");
        var allowed = HtmlAllowlist.Create().AllowedClasses;
        foreach (var element in Parse(html).QuerySelectorAll("[class]"))
        {
            foreach (var name in element.ClassList) Assert.Contains(name, allowed);
        }
    }

    /// <summary>상한(UTF-8 204,800바이트)까지는 렌더링하고, 넘으면 호출부의 검증 누락으로 보고 예외를 던진다.</summary>
    [Fact]
    public void Render_InputSizeLimit()
    {
        var atLimit = new string('a', MarkdownRenderer.MaxInputBytes);
        Assert.NotEmpty(Renderer.Render(atLimit));
        Assert.Throws<ArgumentException>(() => Renderer.Render(atLimit + "a"));
        Assert.Throws<ArgumentException>(() => Renderer.Render(new string('가', MarkdownRenderer.MaxInputBytes / 3 + 1))); // 글자 수가 아니라 바이트
    }

    /// <summary>공유 인스턴스를 여러 스레드가 동시에 써도 결과가 같다(싱글턴 등록의 전제).</summary>
    [Fact]
    public void Render_IsThreadSafe()
    {
        var markdown = new StringBuilder();
        for (var i = 0; i < 200; i++) markdown.Append("## 제목 ").Append(i).Append("\n\n[링크](https://ok.test/").Append(i).Append(") `code`\n\n```csharp\nvar x = ").Append(i).Append(";\n```\n\n");
        var expected = Renderer.Render(markdown.ToString());
        Parallel.For(0, 16, _ => Assert.Equal(expected, Renderer.Render(markdown.ToString())));
    }

    /// <summary>빈 입력은 빈 출력이다.</summary>
    [Fact]
    public void Render_Empty_ReturnsEmpty() => Assert.Equal(string.Empty, Renderer.Render(string.Empty).Trim());
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~MarkdownRendererTests"` → 컴파일 오류.

- [ ] **Step 5: 구현** — `PortfolioBlog.Api/Infrastructure/Markdown/`

```csharp
// HighlightingCodeBlockRenderer.cs
using System.Text;
using ColorCode;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>코드블록을 서버에서 강조한다. 출력은 CSS 클래스만 쓰며 인라인 <c>style</c>을 만들지 않는다(공개 페이지 CSP가 <c>style-src 'self'</c>·스크립트 전면 금지이기 때문).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 인스턴스에 상태가 없다. 렌더링마다 새 <see cref="HtmlRenderer"/>에 붙여 쓴다.</description></item>
/// <item><description><b>Memory Allocation:</b> 코드블록마다 원문 StringBuilder와 포매터 1개. 블록 크기에 비례.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업(정규식 기반 토큰화).</description></item>
/// </list>
/// 언어 이름(info string)은 작성자 입력이다. <c>Languages.FindById</c>로 찾은 언어 객체만 쓰고 원문 문자열을 출력에 넣지 않는다.
/// 모르는 언어는 이스케이프한 일반 코드블록으로 떨어진다.
/// </remarks>
public sealed class HighlightingCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    protected override void Write(HtmlRenderer renderer, CodeBlock block)
    {
        var code = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            code.Append(block.Lines.Lines[i].Slice.ToString()).Append('\n');
        }
        var info = (block as FencedCodeBlock)?.Info;
        var language = string.IsNullOrWhiteSpace(info) ? null : Languages.FindById(info.Trim().ToLowerInvariant());
        if (language is null)
        {
            renderer.Write("<pre><code>");
            renderer.WriteEscape(code.ToString());
            renderer.Write("</code></pre>\n");
            return;
        }
        renderer.Write(new HtmlClassFormatter().GetHtmlString(code.ToString(), language));
        renderer.Write("\n");
    }
}
```

```csharp
// HtmlAllowlist.cs
using AngleSharp.Dom;
using ColorCode;
using ColorCode.Styling;
using Ganss.Xss;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>최종 HTML에 적용하는 허용 목록. 파서 설정(raw HTML 비활성)과 URL 정책이 1·2차 방어이고 이것이 3차다:
/// 하이라이터나 Markdig 확장에 버그가 있어도 허용 목록 밖의 태그·속성·클래스·스킴은 출력에 남지 못한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="Create"/>가 돌려준 인스턴스는 구성을 바꾸지 않는 한 여러 스레드에서 동시에 <c>Sanitize</c>할 수 있다(호출마다 새 DOM을 만든다).</description></item>
/// <item><description><b>Memory Allocation:</b> <c>Sanitize</c>는 입력 HTML을 DOM으로 파싱한다 — 입력 크기의 수 배.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 작업.</description></item>
/// </list>
/// </remarks>
public static class HtmlAllowlist
{
    public static IReadOnlySet<string> AllowedTags { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "ul", "ol", "li", "pre", "code", "span", "div",
        "em", "strong", "del", "sup", "a", "img", "table", "thead", "tbody", "tr", "th", "td", "input",
    };

    public static IReadOnlySet<string> AllowedAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "href", "src", "alt", "title", "id", "class", "type", "checked", "disabled",
    };

    private static readonly string[] MarkdigClasses = ["contains-task-list", "task-list-item", "footnotes", "footnote-ref", "footnote-back-ref"];

    public static HtmlSanitizer Create()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags) sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in AllowedAttributes) sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" }) sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.AllowedCssProperties.Clear(); // style 속성 자체를 허용하지 않지만 이중으로 비운다
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowDataAttributes = false;

        // 클래스 허용 목록: 하이라이터가 쓰는 스타일 이름 + 언어 컨테이너 이름 + Markdig 확장이 붙이는 것. 비어 있으면 "모든 클래스 허용"이 되므로 반드시 채운다.
        foreach (var style in StyleDictionary.DefaultLight)
        {
            if (!string.IsNullOrEmpty(style.ReferenceName)) sanitizer.AllowedClasses.Add(style.ReferenceName);
        }
        foreach (var language in Languages.All) sanitizer.AllowedClasses.Add(language.CssClassName);
        foreach (var name in MarkdigClasses) sanitizer.AllowedClasses.Add(name);

        // input은 작업 목록의 비활성 체크박스로만 남긴다.
        sanitizer.PostProcessNode += static (_, e) =>
        {
            if (e.Node is IElement { LocalName: "input" } input && input.GetAttribute("type") != "checkbox") input.Remove();
        };
        return sanitizer;
    }
}
```

```csharp
// MarkdownRenderer.cs
using System.Text;
using Ganss.Xss;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>마크다운을 URL 정책·허용 목록 정제를 거친 안전한 HTML로 변환한다. 공개 페이지와 미리보기가 이 하나를 공유한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 파이프라인과 정제기는 생성 후 바꾸지 않으며 호출마다 새 문서·렌더러·DOM을 만든다. 싱글턴으로 등록한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 입력 크기에 비례(AST + 중간 HTML + 정제용 DOM + 출력 문자열). 반환 문자열의 소유권은 호출자.</description></item>
/// <item><description><b>Blocking:</b> 호출 스레드에서 동기 실행되는 CPU 작업이며 취소할 수 없다(160KB·코드블록 1,500개에 약 200ms). 호출부가 입력 크기와 동시 실행 수를 제한한다.</description></item>
/// </list>
/// HTML을 저장하지 않고 요청마다 렌더링하므로 이 클래스의 보안 수정은 과거 글 전체에 즉시 적용된다.
/// </remarks>
public sealed class MarkdownRenderer
{
    /// <summary>입력 상한(UTF-8 바이트). DB CHECK(<c>CK_Posts_Content_Size</c>)·글 검증과 같은 값.</summary>
    public const int MaxInputBytes = 204_800;

    // MarkdownPipeline: Build() 이후 불변이라 스레드 간 공유가 안전하다. 확장은 허용 목록으로만 켠다 —
    // 임의 속성({#id .class}), 미디어 임베드, raw HTML은 끄거나 아예 등록하지 않는다.
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseTaskLists()
        .UseFootnotes()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .Build();

    // HtmlSanitizer: Sanitize 호출마다 독립된 AngleSharp DOM을 만들기 때문에 구성만 고정돼 있으면 공유 인스턴스를 동시에 쓸 수 있다.
    private readonly HtmlSanitizer _sanitizer = HtmlAllowlist.Create();

    public string Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (Encoding.UTF8.GetByteCount(markdown) > MaxInputBytes)
        {
            throw new ArgumentException($"마크다운 입력은 UTF-8 {MaxInputBytes}바이트 이하여야 합니다. 호출부가 먼저 검증해야 합니다.", nameof(markdown));
        }

        var document = Markdown.Parse(markdown, _pipeline);
        ApplyUrlPolicy(document);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        if (renderer.ObjectRenderers.FindExact<CodeBlockRenderer>() is { } builtIn) renderer.ObjectRenderers.Remove(builtIn);
        renderer.ObjectRenderers.Add(new HighlightingCodeBlockRenderer());
        renderer.Render(document);
        writer.Flush();
        return _sanitizer.Sanitize(writer.ToString());
    }

    /// <summary>정책 밖 링크·이미지를 AST에서 푼다. 자식(링크 글자·이미지 대체 텍스트)은 그 자리에 남기고 링크 노드만 없앤다.</summary>
    private static void ApplyUrlPolicy(MarkdownDocument document)
    {
        // ToList(): 순회 중 트리를 고치므로 먼저 목록을 고정한다.
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            var allowed = link.IsImage ? UrlPolicy.IsAllowedImage(link.Url) : UrlPolicy.IsAllowedLink(link.Url);
            if (allowed) continue;
            // ReplaceBy(new LiteralInline(...))로 텍스트를 다시 만들면 글자가 두 번 나온다(스파이크에서 확인). 자식을 옮기고 노드만 지운다.
            link.MoveChildrenAfter(link);
            link.Remove();
        }
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
            if (!UrlPolicy.IsAllowedLink(url)) autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }
    }
}
```

`Program.cs`: `builder.Services.AddAppRateLimiting();` 다음 줄에 `builder.Services.AddSingleton<MarkdownRenderer>();`(using `PortfolioBlog.Api.Infrastructure.Markdown`).

- [ ] **Step 6: 통과 확인**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~MarkdownRendererTests|FullyQualifiedName~UrlPolicyTests"`
Expected: MarkdownRenderer 40개(공격 Theory 32 + Fact 8) + UrlPolicy 42개 PASS.
어떤 공격 입력이 `AssertInert`를 통과하지 못하면 **어서션을 고치지 말고** 그 입력의 실제 출력 HTML을 보고서에 적은 뒤 파이프라인을 고친다(정책·허용 목록 중 어느 단계가 놓쳤는지 밝힌다). 정상 문서 테스트의 개수 단언(첫 문단의 링크 5개 등)이 Markdig의 실제 출력 구조와 달라 실패하면, 출력 HTML을 확인하고 **기능이 유지된다는 의미를 보존하는 범위에서** 선택자를 고친 뒤 이탈로 보고한다.

- [ ] **Step 7: 전체 회귀 · 커밋**

Run: `dotnet build PortfolioBlog.slnx -c Release && dotnet test PortfolioBlog.slnx -c Release` → 경고 0, 260개 통과(178 + 82). 0x00 검사 후:

```bash
git add -A
git commit -m "추가: 마크다운을 안전한 HTML로 바꾸는 3단 정제 파이프라인

- raw HTML 비활성·확장 허용 목록 → AST URL 정책 → 최종 HTML 허용 목록 정제
- 링크는 http·https·mailto·루트 상대·앵커만, 이미지는 자체 첨부 경로만 허용(외부 핫링크·추적 픽셀 차단)
- 코드 강조는 서버에서 CSS 클래스로만 출력(공개 페이지 CSP가 스크립트·인라인 스타일을 금지)
- 공격 코퍼스를 출력 DOM 재파싱으로 검증: 허용 목록 밖 태그·속성·URL이 0개

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: `/api/preview` 미리보기 엔드포인트

> ⚠️ 이 Task의 일부 코드·문장은 리뷰에서 결함으로 판정되어 구현에서 교정됐다 — 문서 끝 "구현 중 발견해 고친 계획 결함" 표를 따를 것.

**Files:**
- Create: `PortfolioBlog.Api/Contracts/PreviewDtos.cs`, `PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs`
- Modify: `PortfolioBlog.Api/Infrastructure/Access/AdminOptions.cs`, `Infrastructure/Access/StartupValidation.cs`, `Infrastructure/Web/RateLimitingExtensions.cs`, `Features/ApiEndpoints.cs`
- Modify(test): `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs`, `Infrastructure/AdminOptionsTests.cs`, `Features/AccessMatrixTests.cs`
- Test: `PortfolioBlog.Api.Tests/Features/PreviewEndpointsTests.cs`

**Interfaces:**
- Consumes: `MarkdownRenderer.Render`, `MarkdownRenderer.MaxInputBytes`(Task 2); `RateLimitMetadata`, `RateLimitPolicy.Preview`, `RateLimitingExtensions.Window/Concurrency`(Task 1); `TextRules`; 보호된 `/api` 그룹.
- Produces: `PreviewRequest(string? Markdown)`, `PreviewResponse(string Html)`; `POST /api/preview` → 200 `{html}` | 400 | 401 | 429; `AdminOptions.PreviewPerMinute = 60`, `AdminOptions.PreviewConcurrency = 2`; `PreviewEndpoints.MapPreviewEndpoints(this RouteGroupBuilder)`.

- [ ] **Step 1: 실패하는 테스트 작성** — `PortfolioBlog.Api.Tests/Features/PreviewEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>미리보기 엔드포인트 통합 테스트. 공개 페이지와 같은 렌더러를 쓰는지, 입력 제한과 속도 제한이 걸리는지 본다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 클래스 픽스처 팩토리를 공유하되 속도 제한 테스트는 격리된 팩토리를 만든다(제한기 상태가 다른 테스트를 막지 않게).</description></item>
/// <item><description><b>Memory Allocation:</b> 가장 큰 요청 본문은 약 200KB.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너(세션 검증)에 접속한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PreviewEndpointsTests(ApiFactory factory, PostgresContainerFixture pg) : IClassFixture<ApiFactory>
{
    /// <summary>마크다운을 렌더링해 돌려주고, 위험한 입력은 공개 페이지와 똑같이 중화된다.</summary>
    [Fact]
    public async Task Preview_RendersSanitizedHtml()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("# 제목\n\n[x](javascript:alert(1)) <script>alert(1)</script>"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = (await res.Content.ReadFromJsonAsync<PreviewResponse>(TestJson.Options))!.Html;
        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>누락·NUL·크기 초과는 필드 키 <c>markdown</c>의 400이다(500이 아니다).</summary>
    [Fact]
    public async Task Preview_InvalidInput_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        object[] bodies = [new { }, new PreviewRequest("본문\0"), new PreviewRequest(new string('가', 70_000))]; // '가' 3바이트 × 70,000 > 204,800
        foreach (var body in bodies)
        {
            using var res = await client.PostAsJsonAsync("/api/preview", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options);
            Assert.Contains("markdown", problem!.Errors.Keys);
        }
    }

    /// <summary>빈 본문은 허용한다(에디터를 막 열었을 때).</summary>
    [Fact]
    public async Task Preview_EmptyMarkdown_ReturnsEmptyHtml()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest(""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(string.Empty, (await res.Content.ReadFromJsonAsync<PreviewResponse>(TestJson.Options))!.Html.Trim());
    }

    /// <summary>세션이 없으면 401 — 렌더러는 CPU를 쓰므로 익명으로 열어 두지 않는다.</summary>
    [Fact]
    public async Task Preview_WithoutSession_Returns401()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("x"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>분당 한도를 넘으면 429 + Retry-After. 경로 변형으로 우회되지 않는다(엔드포인트 메타데이터로 판정).</summary>
    [Fact]
    public async Task Preview_IsRateLimited_AndPathVariantsShareTheBudget()
    {
        using var limited = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:PreviewPerMinute"] = "2" });
        using var client = await limited.CreateLoggedInClientAsync();
        using var first = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("a"));
        using var second = await client.PostAsJsonAsync("/API/Preview/", new PreviewRequest("b"));
        using var third = await client.PostAsJsonAsync("/api/preview", new PreviewRequest("c"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.True(third.Headers.Contains("Retry-After"));
    }
}
```

`AdminOptionsTests.Defaults_MatchShippedProductionValues`에 두 줄 추가: `Assert.Equal(60, options.PreviewPerMinute);`, `Assert.Equal(2, options.PreviewConcurrency);`(그 테스트의 지역 변수 이름에 맞춘다).

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~PreviewEndpointsTests"` → 컴파일 오류.

- [ ] **Step 2: 옵션 · 제한기 · 테스트 기본값**
  - `AdminOptions.cs`에 추가: `/// <summary>미리보기 렌더링의 분당 전역 한도(스펙 3.7).</summary> public int PreviewPerMinute { get; set; } = 60;`, `/// <summary>동시에 실행할 수 있는 미리보기 렌더링 수(렌더링은 CPU 바운드이며 취소할 수 없다).</summary> public int PreviewConcurrency { get; set; } = 2;`
  - `StartupValidation.cs`의 "1 이상" 검사에 `admin.PreviewPerMinute < 1 || admin.PreviewConcurrency < 1`를 추가하고 예외 메시지에 두 키 이름을 넣는다.
  - `RateLimitingExtensions.cs`의 `CreateChained(...)` 인수 끝에 추가:

```csharp
                ,
                Window(RateLimitPolicy.Preview, _ => "preview-global", admin.PreviewPerMinute),
                // 렌더링은 동기 CPU 작업이라 요청 취소로 멈추지 않는다. 동시에 도는 수를 직접 묶는다.
                Concurrency(RateLimitPolicy.Preview, "preview-concurrency", admin.PreviewConcurrency)
```

  - `ApiFactory.cs`의 기본값 블록에 추가: `builder.UseSetting("Admin:PreviewPerMinute", "1000");`, `builder.UseSetting("Admin:PreviewConcurrency", "64");`

- [ ] **Step 3: 계약 · 엔드포인트 구현**

```csharp
// PortfolioBlog.Api/Contracts/PreviewDtos.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary>미리보기 요청. 누락을 필드별 400으로 돌려주려고 nullable로 받는다.</summary>
/// <param name="Markdown">에디터의 현재 본문(UTF-8 204,800바이트 이하).</param>
public sealed record PreviewRequest(string? Markdown);

/// <summary>미리보기 응답.</summary>
/// <param name="Html">정제된 HTML. 관리 SPA는 이것을 React DOM에 직접 넣지 않고 <c>sandbox=""</c> iframe의 <c>srcdoc</c>에만 넣는다.</param>
public sealed record PreviewResponse(string Html);
```

```csharp
// PortfolioBlog.Api/Features/Preview/PreviewEndpoints.cs
using System.Text;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Features.Preview;

/// <summary>에디터 미리보기. 공개 페이지와 <b>같은</b> <see cref="MarkdownRenderer"/>를 써서 "미리보기와 발행 결과가 다르다"를 없앤다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러. 렌더러는 Thread-safe 싱글턴.</description></item>
/// <item><description><b>Memory Allocation:</b> 요청 본문(≤200KB) + 렌더링 중간 산출물.</description></item>
/// <item><description><b>Blocking:</b> 렌더링은 요청 스레드에서 동기 실행된다(최대 수백 ms). 분당 한도와 동시 실행 제한(<see cref="RateLimitPolicy.Preview"/>)이 스레드 풀 고갈을 막는다 — 프런트의 디바운스는 UX일 뿐 방어가 아니다.</description></item>
/// </list>
/// 본문은 로그에 남기지 않는다.
/// </remarks>
public static class PreviewEndpoints
{
    public static void MapPreviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/preview", Render)
            .WithMetadata(new RateLimitMetadata(RateLimitPolicy.Preview))
            .WithName("PreviewMarkdown");
    }

    private static IResult Render(PreviewRequest request, MarkdownRenderer renderer)
    {
        var errors = new ValidationErrors();
        if (request.Markdown is null) errors.Add("markdown", "markdown은 필수입니다(빈 문자열은 허용).");
        else if (TextRules.ContainsNul(request.Markdown)) errors.Add("markdown", TextRules.NulMessage);
        else if (Encoding.UTF8.GetByteCount(request.Markdown) > MarkdownRenderer.MaxInputBytes)
            errors.Add("markdown", $"본문은 UTF-8 기준 {MarkdownRenderer.MaxInputBytes / 1024}KB 이하여야 합니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        return TypedResults.Ok(new PreviewResponse(renderer.Render(request.Markdown!)));
    }
}
```

`Features/ApiEndpoints.cs`: `api.MapTagEndpoints();` 다음 줄에 `api.MapPreviewEndpoints();`(using `PortfolioBlog.Api.Features.Preview`).
`Features/AccessMatrixTests.cs`: 라우트 수 단언 `targets.Count >= 15`를 `>= 16`으로 올린다(미리보기 1개 추가).

- [ ] **Step 4: 통과 확인 · 커밋**

Run: `dotnet test PortfolioBlog.slnx -c Release` → 경고 0, 265개 통과(260 + 5). `AccessMatrixTests`가 새 엔드포인트를 자동으로 검사한다(외부 IP 403·헤더 누락 403·Origin 누락 403·공개 호스트 404·세션 없음 401).
`Preview_IsRateLimited…`에서 두 번째 요청(`/API/Preview/`)이 404면 라우팅이 끝 슬래시를 받지 않는 것이다 → 그 경로를 `/API/PREVIEW`로 바꾸고 이탈로 보고한다(핵심은 "변형 경로가 같은 예산을 쓴다"이다).

```bash
git add -A
git commit -m "추가: 공개 페이지와 같은 렌더러를 쓰는 /api/preview

- 미리보기와 발행 결과가 달라지지 않도록 MarkdownRenderer 하나를 공유
- 렌더링은 취소할 수 없는 CPU 작업이라 분당 한도와 동시 실행 수를 서버에서 제한(프런트 디바운스는 방어가 아님)
- 누락·NUL·200KB 초과는 필드별 400

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---
### Task 4: 이미지 시그니처 판정 · 메타데이터 제거 (순수 함수)

> ⚠️ 이 Task의 일부 코드·문장은 리뷰에서 결함으로 판정되어 구현에서 교정됐다 — 문서 끝 "구현 중 발견해 고친 계획 결함" 표를 따를 것.

**Files:**
- Create: `PortfolioBlog.Api/Infrastructure/Storage/{ImageKind,ImageSignature,MetadataStripper}.cs`
- Modify: `PortfolioBlog.Api.Tests/PortfolioBlog.Api.Tests.csproj`(픽스처 복사)
- Test: `PortfolioBlog.Api.Tests/Infrastructure/{ImageSignatureTests,MetadataStripperTests}.cs`
- 이미 있음: `PortfolioBlog.Api.Tests/Fixtures/Images/{exif-gps.jpg, exif-text.png, exif-xmp.webp, comment-animated.gif, progressive-trailing.jpg, make-fixtures.py}`

**Interfaces:**
- Consumes: 없음.
- Produces:
  - `enum ImageKind { Png, Jpeg, Gif, WebP }`
  - `ImageSignature.HeaderLength = 12`, `ImageSignature.Detect(ReadOnlySpan<byte> header) : ImageKind?`, `ImageSignature.Extension(ImageKind) : string`(`"png"|"jpg"|"gif"|"webp"`), `ImageSignature.ContentType(ImageKind) : string`
  - `MetadataStripper.Strip(ImageKind kind, Stream input, Stream output)` — `input`은 현재 위치부터 읽고, `output`은 **seek 가능**해야 한다(WebP의 RIFF 크기 재기록). 구조가 깨졌거나 잘린 파일은 `InvalidDataException`.

- [ ] **Step 1: 픽스처를 테스트 출력 폴더로 복사** — 테스트 csproj에 추가:

```xml
  <ItemGroup>
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

픽스처의 내용(생성 스크립트 `make-fixtures.py` 참조): `exif-gps.jpg`는 EXIF(Make=`SpikeCam`, DateTime, GPS 4개 태그)와 COM 세그먼트(`secret comment`), `exif-text.png`는 `eXIf`·`tEXt`(Author=`secret author`)·`iTXt`(Location=`Seoul`), `exif-xmp.webp`는 `EXIF`·`XMP `(`<x:xmpmeta>secret…`) 청크와 VP8X 플래그, `comment-animated.gif`는 주석 확장(`secret gif comment`)과 `NETSCAPE2.0` 반복 확장이 든 2프레임 애니메이션, `progressive-trailing.jpg`는 EXIF·COM이 든 프로그레시브 JPEG(스캔 10개) 뒤에 ZIP 시그니처와 `secret trailing payload`를 덧붙인 폴리글랏이다.

- [ ] **Step 2: 실패하는 테스트 작성**

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/ImageSignatureTests.cs
using System.Text;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>파일 시그니처 판정 단위 테스트. 업로드된 파일 이름·Content-Type은 믿지 않고 첫 바이트들만 본다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 픽스처 파일을 통째로 읽지만 각 5KB 미만이다.</description></item>
/// <item><description><b>Blocking:</b> 동기 파일 읽기만. Docker 불필요.</description></item>
/// </list>
/// </remarks>
public sealed class ImageSignatureTests
{
    private static byte[] Head(string fixture) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", fixture))[..ImageSignature.HeaderLength];

    /// <summary>네 가지 허용 형식의 실제 파일을 알아보고, 확장자와 Content-Type을 시그니처에서 정한다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg", ImageKind.Jpeg, "jpg", "image/jpeg")]
    [InlineData("exif-text.png", ImageKind.Png, "png", "image/png")]
    [InlineData("exif-xmp.webp", ImageKind.WebP, "webp", "image/webp")]
    [InlineData("comment-animated.gif", ImageKind.Gif, "gif", "image/gif")]
    public void Detect_KnownFormats(string fixture, ImageKind expected, string extension, string contentType)
    {
        var kind = ImageSignature.Detect(Head(fixture));
        Assert.Equal(expected, kind);
        Assert.Equal(extension, ImageSignature.Extension(kind!.Value));
        Assert.Equal(contentType, ImageSignature.ContentType(kind.Value));
    }

    /// <summary>SVG·HTML·빈 입력·너무 짧은 입력은 거부한다.</summary>
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">")]
    [InlineData("<!DOCTYPE html><html>")]
    [InlineData("")]
    [InlineData("GIF8")]
    public void Detect_RejectsText(string content) =>
        Assert.Null(ImageSignature.Detect(Encoding.ASCII.GetBytes(content)));

    /// <summary>실행 파일 헤더, "RIFF이지만 WEBP가 아닌" 컨테이너, 세 번째 바이트가 없는 JPEG SOI도 거부한다.
    /// 0x00이 든 입력은 문자열 리터럴이 아니라 바이트 배열로 만든다(계획 Global Constraints의 NUL 표기 규칙).</summary>
    [Fact]
    public void Detect_RejectsBinaryLookalikes()
    {
        Assert.Null(ImageSignature.Detect([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00])); // MZ (PE 실행 파일)
        Assert.Null(ImageSignature.Detect([0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45])); // RIFF....WAVE
        Assert.Null(ImageSignature.Detect([0xFF, 0xD8]));
    }
}
```

```csharp
// PortfolioBlog.Api.Tests/Infrastructure/MetadataStripperTests.cs
using System.Buffers.Binary;
using System.Text;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>메타데이터 제거기 단위 테스트. 서버는 이미지를 디코딩하지 않으므로 "메타데이터 바이트가 사라졌는가"와 "컨테이너 구조가 온전한가"를 본다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자체 <see cref="MemoryStream"/>을 쓴다. 공유 상태 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 픽스처는 각 5KB 미만.</description></item>
/// <item><description><b>Blocking:</b> 동기 메모리 I/O만. Docker 불필요.</description></item>
/// </list>
/// 제거 후에도 <b>디코딩한 픽셀이 같고 프레임 수가 유지된다</b>는 사실은 계획 단계에서 PIL로 확인했다(<c>Fixtures/Images/make-fixtures.py</c>의 설명 참조).
/// 이 테스트 프로젝트에는 디코더가 없으므로 같은 사실을 구조 수준에서 고정한다.
/// </remarks>
public sealed class MetadataStripperTests
{
    private static readonly string[] Secrets = ["secret", "SpikeCam", "Exif", "xmpmeta", "Seoul", "Author", "Location"];

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name));

    private static byte[] Strip(byte[] data)
    {
        var kind = ImageSignature.Detect(data.AsSpan(0, Math.Min(ImageSignature.HeaderLength, data.Length)))!.Value;
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        MetadataStripper.Strip(kind, input, output);
        return output.ToArray();
    }

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;

    /// <summary>픽스처에는 실제로 메타데이터가 들어 있고(테스트의 전제), 제거 후에는 하나도 남지 않으며 파일은 더 작아지고 같은 형식으로 판정된다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    [InlineData("progressive-trailing.jpg")]
    public void Strip_RemovesEveryMetadataMarker(string fixture)
    {
        var original = Fixture(fixture);
        Assert.Contains(Secrets, s => Contains(original, s)); // 전제: 원본에는 비밀이 있다

        var stripped = Strip(original);

        Assert.All(Secrets, s => Assert.False(Contains(stripped, s), $"{fixture}: '{s}'가 남아 있다"));
        Assert.True(stripped.Length < original.Length);
        Assert.Equal(ImageSignature.Detect(original.AsSpan(0, 12)), ImageSignature.Detect(stripped.AsSpan(0, 12)));
    }

    /// <summary>이미 깨끗한 파일에 다시 적용해도 바이트가 같다(멱등) — 같은 이미지는 항상 같은 SHA-256, 같은 저장 경로가 된다.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    [InlineData("progressive-trailing.jpg")]
    public void Strip_IsIdempotent(string fixture)
    {
        var once = Strip(Fixture(fixture));
        Assert.Equal(once, Strip(once));
    }

    /// <summary>JPEG: JFIF(APP0)와 화상 데이터는 그대로, EXIF(APP1)와 주석(COM)만 빠진다. 파일은 SOI로 시작해 EOI로 끝난다.</summary>
    [Fact]
    public void Strip_Jpeg_KeepsStructure()
    {
        var stripped = Strip(Fixture("exif-gps.jpg"));
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], stripped[..4]);
        Assert.True(Contains(stripped, "JFIF"));
        Assert.Equal([0xFF, 0xD9], stripped[^2..]);
        Assert.False(Contains(stripped, "Exif"));
    }

    /// <summary>프로그레시브 JPEG(스캔 여러 개)의 모든 스캔이 남고, EOI 뒤에 덧붙인 페이로드(ZIP 시그니처 + 문자열)는 버려진다 —
    /// SOS 이후를 "끝까지 그대로 복사"하면 통과해 버리는 폴리글랏·숨긴 데이터를 막는다.</summary>
    [Fact]
    public void Strip_Jpeg_Progressive_KeepsAllScans_AndDropsBytesAfterEoi()
    {
        var original = Fixture("progressive-trailing.jpg");
        var stripped = Strip(original);

        static int Count(byte[] data, byte marker)
        {
            var count = 0;
            for (var i = 0; i + 1 < data.Length; i++) if (data[i] == 0xFF && data[i + 1] == marker) count++;
            return count;
        }
        Assert.True(Count(original, 0xDA) > 1, "전제: 픽스처는 스캔이 여러 개인 프로그레시브 JPEG다");
        Assert.Equal(Count(original, 0xDA), Count(stripped, 0xDA));
        Assert.Equal([0xFF, 0xD9], stripped[^2..]);
        Assert.True(stripped.AsSpan().IndexOf<byte>([0x50, 0x4B, 0x03, 0x04]) < 0, "EOI 뒤의 ZIP 시그니처가 남아 있다");
    }

    /// <summary>PNG: 청크 열이 IHDR로 시작해 IEND로 끝나고, 남은 청크는 전부 허용 목록에 있다.</summary>
    [Fact]
    public void Strip_Png_KeepsOnlyAllowlistedChunks()
    {
        var stripped = Strip(Fixture("exif-text.png"));
        var types = new List<string>();
        for (var offset = 8; offset < stripped.Length;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(stripped.AsSpan(offset, 4));
            types.Add(Encoding.ASCII.GetString(stripped, offset + 4, 4));
            offset += 12 + (int)length;
        }
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.Contains("IDAT", types);
        Assert.DoesNotContain(types, t => t is "eXIf" or "tEXt" or "iTXt" or "zTXt" or "tIME");
    }

    /// <summary>WebP: RIFF 크기 필드가 실제 길이와 맞고, VP8X의 EXIF·XMP 플래그가 꺼져 있다(플래그만 남으면 디코더가 없는 청크를 찾는다).</summary>
    [Fact]
    public void Strip_WebP_RewritesRiffSize_AndClearsFlags()
    {
        var stripped = Strip(Fixture("exif-xmp.webp"));
        Assert.Equal((uint)(stripped.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(stripped.AsSpan(4, 4)));
        Assert.Equal("VP8X", Encoding.ASCII.GetString(stripped, 12, 4));
        Assert.Equal(0, stripped[20] & 0x0C);
        Assert.False(Contains(stripped, "EXIF"));
        Assert.False(Contains(stripped, "XMP "));
    }

    /// <summary>GIF: 주석은 빠지고 애니메이션 반복 확장(NETSCAPE2.0)과 두 프레임(이미지 구분자 0x2C 두 번 이상)은 남는다. 트레일러로 끝난다.</summary>
    [Fact]
    public void Strip_Gif_KeepsAnimation()
    {
        var stripped = Strip(Fixture("comment-animated.gif"));
        Assert.True(Contains(stripped, "NETSCAPE2.0"));
        Assert.Equal(0x3B, stripped[^1]);
        Assert.True(stripped.Length > 4000); // 프레임 데이터가 통째로 남아 있다(원본 4,613바이트에서 주석만 빠진다)
    }

    /// <summary>잘린 파일은 <see cref="InvalidDataException"/>으로 끝난다 — 무한 루프·범위 밖 읽기·다른 예외 없이.</summary>
    [Theory]
    [InlineData("exif-gps.jpg")]
    [InlineData("exif-text.png")]
    [InlineData("exif-xmp.webp")]
    [InlineData("comment-animated.gif")]
    public void Strip_Truncated_ThrowsInvalidData(string fixture)
    {
        var data = Fixture(fixture);
        foreach (var cut in new[] { 13, data.Length / 3, data.Length / 2 })
        {
            Assert.Throws<InvalidDataException>(() => Strip(data[..cut]));
        }
    }

    /// <summary>선언 길이가 터무니없는 세그먼트·청크는 메모리를 할당하지 않고 거부한다.</summary>
    [Fact]
    public void Strip_LyingLengthField_ThrowsInvalidData()
    {
        var png = Fixture("exif-text.png");
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), 0x7FFFFFF0); // IHDR 길이를 2GB로
        Assert.Throws<InvalidDataException>(() => Strip(png));
    }
}
```

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~ImageSignatureTests|FullyQualifiedName~MetadataStripperTests"` → 컴파일 오류.

- [ ] **Step 3: 구현** — `PortfolioBlog.Api/Infrastructure/Storage/`

```csharp
// ImageKind.cs
namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>업로드를 허용하는 이미지 형식. SVG는 스크립트를 담을 수 있어 제외한다.</summary>
public enum ImageKind
{
    /// <summary>PNG(APNG 포함).</summary>
    Png,
    /// <summary>JPEG(JFIF·Exif).</summary>
    Jpeg,
    /// <summary>GIF87a·GIF89a.</summary>
    Gif,
    /// <summary>WebP(RIFF 컨테이너).</summary>
    WebP,
}
```

```csharp
// ImageSignature.cs
namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>파일의 첫 바이트들로 형식을 판정한다. 업로드된 파일 이름과 Content-Type은 공격자가 고를 수 있으므로 쓰지 않는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(스팬 비교, UTF-8 리터럴은 정적 데이터).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// 시그니처 일치는 "정상 이미지"를 보증하지 않는다. 구조 검증은 <see cref="MetadataStripper"/>가 컨테이너를 끝까지 읽으며 수행하고,
/// 응답에는 <c>X-Content-Type-Options: nosniff</c>와 여기서 정한 Content-Type만 쓴다.
/// </remarks>
public static class ImageSignature
{
    /// <summary>판정에 필요한 최소 바이트 수(WebP의 <c>RIFF….WEBP</c>).</summary>
    public const int HeaderLength = 12;

    public static ImageKind? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageKind.Png;
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ImageKind.Jpeg;
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return ImageKind.Gif;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return ImageKind.WebP;
        return null;
    }

    public static string Extension(ImageKind kind) => kind switch
    {
        ImageKind.Png => "png",
        ImageKind.Jpeg => "jpg",
        ImageKind.Gif => "gif",
        ImageKind.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string ContentType(ImageKind kind) => kind switch
    {
        ImageKind.Png => "image/png",
        ImageKind.Jpeg => "image/jpeg",
        ImageKind.Gif => "image/gif",
        ImageKind.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
```

```csharp
// MetadataStripper.cs
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>이미지를 <b>디코딩하지 않고</b> 컨테이너 구조만 따라가며 메타데이터(EXIF·GPS·XMP·IPTC·주석·텍스트)를 버린다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 상태는 호출이 넘긴 스트림뿐이다(스트림 자체는 호출자가 독점해야 한다).</description></item>
/// <item><description><b>Memory Allocation:</b> 파일 크기와 무관하게 64KB 풀 버퍼 하나 + 스택 버퍼. 선언된 길이만큼 미리 할당하지 않으므로 "길이를 속인 청크"로 메모리를 부풀릴 수 없다.</description></item>
/// <item><description><b>Blocking:</b> 동기 스트림 I/O. 호출부는 임시 파일 스트림을 넘기며 10MB 이하임을 먼저 보장한다.</description></item>
/// </list>
/// 디코더를 쓰지 않는 이유: 이미지 디코더는 그 자체가 큰 공격 표면이고, 재인코딩은 화질을 바꾼다. 컨테이너 파싱은 "길이 필드를 읽고 건너뛰거나 복사"뿐이다.
/// 출력이 멱등이라(깨끗한 파일을 다시 넣으면 같은 바이트) 그 SHA-256을 저장 경로로 쓸 수 있다.
/// 구조가 어긋나면 <see cref="InvalidDataException"/> — 호출부는 이를 "지원하지 않는 이미지"(415)로 바꾼다.
/// </remarks>
public static class MetadataStripper
{
    private const int CopyBufferSize = 64 * 1024; // 85,000바이트 미만: LOH에 올라가지 않는다

    // PNG에서 남기는 청크: 화상·팔레트·투명도·색 공간·물리 해상도·APNG. 그 밖의 보조 청크(eXIf, tEXt, zTXt, iTXt, tIME, 알 수 없는 것)는 버린다.
    private static readonly HashSet<string> PngKeep = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "hIST", "sPLT",
        "acTL", "fcTL", "fdAT", "cICP", "mDCv", "cLLi",
    };

    public static void Strip(ImageKind kind, Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        switch (kind)
        {
            case ImageKind.Jpeg: StripJpeg(input, output); break;
            case ImageKind.Png: StripPng(input, output); break;
            case ImageKind.WebP: StripWebP(input, output); break;
            case ImageKind.Gif: StripGif(input, output); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // JPEG: SOI, 이어서 세그먼트 = FFxx + 길이(2바이트 빅엔디언, 자기 자신 포함) + 페이로드. SOS(FFDA) 뒤에는 엔트로피 부호화 데이터가 오고,
    // 그 구간은 "다음 진짜 마커"까지 따라간다 — SOS 이후를 끝까지 그대로 복사하면 (1) EOI 뒤에 덧붙인 데이터(폴리글랏·숨긴 메타데이터)와
    // (2) 프로그레시브 JPEG의 스캔 사이 세그먼트가 걸러지지 않는다(스파이크에서 10개 스캔짜리 파일과 ZIP을 덧붙인 파일로 확인).
    private static void StripJpeg(Stream input, Stream output)
    {
        Span<byte> b = stackalloc byte[2];
        ReadExact(input, b);
        if (b[0] != 0xFF || b[1] != 0xD8) throw new InvalidDataException("JPEG SOI가 없다.");
        output.Write(b);
        var sawScan = false;
        var marker = ReadJpegMarker(input);
        while (true)
        {
            if (marker == 0xD9)
            {
                if (!sawScan) throw new InvalidDataException("JPEG에 화상 데이터(SOS)가 없다.");
                output.WriteByte(0xFF); output.WriteByte(0xD9);
                return; // EOI 뒤의 바이트는 버린다
            }
            if (marker is (>= 0xD0 and <= 0xD7) or 0x01) // 길이 없는 마커
            {
                output.WriteByte(0xFF); output.WriteByte(marker);
                marker = ReadJpegMarker(input);
                continue;
            }
            ReadExact(input, b);
            var length = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (length < 2) throw new InvalidDataException("JPEG 세그먼트 길이가 잘못됐다.");
            var payload = length - 2;
            // 버림: APP1(Exif·XMP), APP3~APP13·APP15(APP13 = IPTC/Photoshop), COM.
            // 남김: APP0(JFIF), APP2(ICC 프로파일 — 없으면 색이 달라진다), APP14(Adobe 색 변환 — 없으면 CMYK/YCCK가 깨진다), 그 밖의 모든 화상 세그먼트.
            var drop = marker == 0xFE || marker == 0xE1 || (marker is >= 0xE3 and <= 0xEF && marker != 0xEE);
            if (drop)
            {
                Skip(input, payload);
                marker = ReadJpegMarker(input);
                continue;
            }
            output.WriteByte(0xFF); output.WriteByte(marker); output.Write(b);
            CopyExact(input, output, payload);
            if (marker == 0xDA)
            {
                sawScan = true;
                marker = CopyJpegEntropyData(input, output);
            }
            else
            {
                marker = ReadJpegMarker(input);
            }
        }
    }

    private static byte ReadJpegMarker(Stream input)
    {
        if (ReadByte(input) != 0xFF) throw new InvalidDataException("JPEG 마커가 아니다.");
        var marker = ReadByte(input);
        while (marker == 0xFF) marker = ReadByte(input); // 채움 바이트
        return marker;
    }

    // 엔트로피 부호화 데이터 안에서 FF는 항상 이스케이프된다: FF00(바이트 채움)과 FFD0~FFD7(재시작 마커)은 데이터의 일부이고,
    // 그 밖의 FFxx는 스캔을 끝내는 진짜 마커다. 그 마커를 돌려준다. 파일이 EOI 없이 끝나면 ReadByte가 InvalidDataException을 던진다.
    // 바이트 단위 읽기지만 호출부가 넘기는 FileStream은 64KB 버퍼를 가지므로 시스템 호출은 버퍼 단위로만 일어난다.
    private static byte CopyJpegEntropyData(Stream input, Stream output)
    {
        while (true)
        {
            var value = ReadByte(input);
            if (value != 0xFF)
            {
                output.WriteByte(value);
                continue;
            }
            var next = ReadByte(input);
            while (next == 0xFF) next = ReadByte(input);
            if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
            {
                output.WriteByte(0xFF); output.WriteByte(next);
                continue;
            }
            return next;
        }
    }

    // PNG: 시그니처 8바이트, 이어서 청크 = 길이(4, 빅엔디언) + 종류(4) + 데이터 + CRC(4).
    private static void StripPng(Stream input, Stream output)
    {
        Span<byte> signature = stackalloc byte[8];
        ReadExact(input, signature);
        output.Write(signature);
        Span<byte> header = stackalloc byte[8];
        while (true)
        {
            ReadExact(input, header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (length > int.MaxValue) throw new InvalidDataException("PNG 청크 길이가 잘못됐다.");
            var type = Encoding.ASCII.GetString(header.Slice(4, 4));
            var total = (long)length + 4; // 데이터 + CRC
            if (PngKeep.Contains(type)) { output.Write(header); CopyExact(input, output, total); }
            else Skip(input, total);
            if (type == "IEND") return; // IEND 뒤에 덧붙은 바이트는 버린다
        }
    }

    // WebP: "RIFF" + 크기(4, 리틀엔디언) + "WEBP", 이어서 청크 = FourCC(4) + 크기(4) + 데이터(+홀수면 패딩 1). VP8X 플래그: 0x08 EXIF, 0x04 XMP.
    private static void StripWebP(Stream input, Stream output)
    {
        if (!output.CanSeek) throw new ArgumentException("WebP 출력 스트림은 seek 가능해야 한다(RIFF 크기를 다시 쓴다).", nameof(output));
        Span<byte> riff = stackalloc byte[12];
        ReadExact(input, riff);
        var start = output.Position;
        output.Write(riff);
        var declaredEnd = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(riff.Slice(4, 4));
        Span<byte> chunk = stackalloc byte[8];
        long consumed = 12;
        var sawImage = false;
        while (consumed + 8 <= declaredEnd)
        {
            ReadExact(input, chunk);
            consumed += 8;
            var fourCc = Encoding.ASCII.GetString(chunk[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
            var padded = (long)size + (size & 1);
            if (consumed + padded > declaredEnd + 1) throw new InvalidDataException("WebP 청크가 RIFF 범위를 넘는다.");
            if (fourCc is "EXIF" or "XMP ")
            {
                Skip(input, padded);
            }
            else if (fourCc == "VP8X")
            {
                if (size < 10) throw new InvalidDataException("VP8X 길이가 잘못됐다.");
                output.Write(chunk);
                output.WriteByte((byte)(ReadByte(input) & ~0x0C)); // EXIF·XMP 플래그 해제
                CopyExact(input, output, padded - 1);
            }
            else
            {
                sawImage |= fourCc is "VP8 " or "VP8L" or "ANMF";
                output.Write(chunk);
                CopyExact(input, output, padded);
            }
            consumed += padded;
        }
        if (!sawImage) throw new InvalidDataException("WebP에 화상 청크가 없다.");
        var end = output.Position;
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, checked((uint)(end - start - 8)));
        output.Position = start + 4;
        output.Write(sizeBytes);
        output.Position = end;
    }

    // GIF: 헤더(6) + 논리 화면 기술자(7) [+ 전역 색상표], 이어서 블록 = 0x21 확장 | 0x2C 이미지 | 0x3B 트레일러.
    private static void StripGif(Stream input, Stream output)
    {
        Span<byte> header = stackalloc byte[13];
        ReadExact(input, header);
        output.Write(header);
        if ((header[10] & 0x80) != 0) CopyExact(input, output, 3L << ((header[10] & 0x07) + 1));
        Span<byte> application = stackalloc byte[11];
        Span<byte> descriptor = stackalloc byte[9];
        var sawImage = false;
        while (true)
        {
            var introducer = ReadByte(input);
            if (introducer == 0x3B)
            {
                if (!sawImage) throw new InvalidDataException("GIF에 이미지가 없다.");
                output.WriteByte(0x3B);
                return; // 트레일러 뒤에 덧붙은 바이트는 버린다
            }
            if (introducer == 0x2C)
            {
                sawImage = true;
                output.WriteByte(0x2C);
                ReadExact(input, descriptor);
                output.Write(descriptor);
                if ((descriptor[8] & 0x80) != 0) CopyExact(input, output, 3L << ((descriptor[8] & 0x07) + 1));
                output.WriteByte(ReadByte(input)); // LZW 최소 코드 크기
                CopySubBlocks(input, output, keep: true);
                continue;
            }
            if (introducer != 0x21) throw new InvalidDataException("GIF 블록 구분자가 잘못됐다.");
            var label = ReadByte(input);
            if (label == 0xFE) { CopySubBlocks(input, output, keep: false); continue; } // 주석 확장
            if (label == 0xFF)
            {
                if (ReadByte(input) != 11) throw new InvalidDataException("GIF 애플리케이션 확장 길이가 잘못됐다.");
                ReadExact(input, application);
                var id = Encoding.ASCII.GetString(application);
                var keep = id is "NETSCAPE2.0" or "ANIMEXTS1.0"; // 반복 횟수만 남긴다. XMP 등 다른 애플리케이션 데이터는 버린다
                if (keep) { output.WriteByte(0x21); output.WriteByte(0xFF); output.WriteByte(11); output.Write(application); }
                CopySubBlocks(input, output, keep);
                continue;
            }
            output.WriteByte(0x21); output.WriteByte(label); // 그래픽 제어(0xF9)·일반 텍스트(0x01)
            CopySubBlocks(input, output, keep: true);
        }
    }

    private static void CopySubBlocks(Stream input, Stream output, bool keep)
    {
        while (true)
        {
            var size = ReadByte(input);
            if (keep) output.WriteByte(size);
            if (size == 0) return;
            if (keep) CopyExact(input, output, size); else Skip(input, size);
        }
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new InvalidDataException("파일이 예기치 않게 끝났다.") : (byte)value;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length) throw new InvalidDataException("파일이 예기치 않게 끝났다.");
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length) throw new InvalidDataException("파일이 예기치 않게 끝났다.");
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        CopyExact(stream, Stream.Null, count);
    }

    private static void CopyExact(Stream input, Stream output, long count)
    {
        // ArrayPool<byte>.Shared: 스레드별 캐시(TLS 슬롯)를 먼저 확인하는 버킷 풀이라 같은 스레드에서 빌리고 돌려주면 힙 할당이 없다.
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (count > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new InvalidDataException("파일이 예기치 않게 끝났다.");
                output.Write(buffer, 0, read);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
```

- [ ] **Step 4: 통과 확인 · 커밋**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~ImageSignatureTests|FullyQualifiedName~MetadataStripperTests"`
Expected: ImageSignature 9개(Theory 4 + Theory 4 + Fact 1) + MetadataStripper 20개(Theory 5 + 5 + 4, Fact 6) PASS.
- `Strip_Truncated_ThrowsInvalidData`는 네 형식 모두 세 절단 위치에서 예외가 나야 한다(JPEG도 엔트로피 구간을 마커 단위로 따라가므로 EOI 없이 끝나면 실패한다).
- `Strip_LyingLengthField`가 `InvalidDataException`이 아닌 다른 예외(`OutOfMemoryException`, `IOException`)로 끝나면 구현 결함이다 — 고친다.

Run: `dotnet build PortfolioBlog.slnx -c Release && dotnet test PortfolioBlog.slnx -c Release` → 경고 0, 294개 통과(265 + 29). 0x00 검사(**`Fixtures/Images/` 아래 이미지 파일은 제외** — 이진 파일이다) 후:

```bash
git add -A
git commit -m "추가: 디코딩 없이 컨테이너 구조만 읽는 이미지 판정·메타데이터 제거

- 형식은 파일 이름·Content-Type이 아니라 시그니처로 판정(PNG·JPEG·GIF·WebP, SVG 제외)
- EXIF·GPS·XMP·IPTC·주석·텍스트 청크를 버리고 색 프로파일·애니메이션은 유지
- 64KB 풀 버퍼로 스트리밍해 길이를 속인 청크로 메모리를 부풀릴 수 없고, 잘린 파일은 InvalidDataException
- 출력이 멱등이라 제거 후 바이트의 SHA-256을 저장 경로로 쓸 수 있다

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: 첨부 저장소 · 마이그레이션 · 관리 API · 공개 GET

> ⚠️ 이 Task의 일부 코드·문장은 리뷰에서 결함으로 판정되어 구현에서 교정됐다 — 문서 끝 "구현 중 발견해 고친 계획 결함" 표를 따를 것.

**Files:**
- Create: `PortfolioBlog.Api/Domain/Attachment.cs`, `Contracts/AttachmentDtos.cs`, `Infrastructure/Storage/{AttachmentOptions,FileSystemAttachmentStore}.cs`, `Features/Attachments/{AttachmentEndpoints,PublicAttachmentEndpoints}.cs`, `Infrastructure/Data/Migrations/*_AddAttachments.cs`(생성)
- Modify: `Infrastructure/Data/AppDbContext.cs`, `Infrastructure/Access/StartupValidation.cs`, `Features/ApiEndpoints.cs`, `Program.cs`, `appsettings.json`, `appsettings.Development.json`, `.gitignore`, `plan/tech_blog_0920.md`, `README.md`
- Modify(test): `PortfolioBlog.Api.Tests/Infrastructure/ApiFactory.cs`, `Features/AccessMatrixTests.cs`, `Features/StartupValidationTests.cs`
- Test: `PortfolioBlog.Api.Tests/Features/AttachmentEndpointsTests.cs`

**Interfaces:**
- Consumes: `ImageSignature`, `MetadataStripper`, `ImageKind`(Task 4); `TextRules`; `DbConflict.UniqueViolation`; `DbClock`; 보호된 `/api` 그룹; `AccessMatrixTests.PublicAllowlist`(Task 1).
- Produces:
  - `Attachment { Guid Id; string FileName; string ContentType; long SizeBytes; string StoragePath; string Sha256; DateTimeOffset CreatedAt; }`, `AppDbContext.Attachments`.
  - `AttachmentOptions { string RootPath; }` 섹션 `"Attachments"`, 상수 `AttachmentOptions.MaxBytes = 10_485_760`.
  - `FileSystemAttachmentStore`(싱글턴): `Task<StoredImage> SaveAsync(Stream upload, CancellationToken ct)`, `string PhysicalPath(string storagePath)`, `bool TryDelete(string storagePath)`; `sealed record StoredImage(ImageKind Kind, string Sha256, long SizeBytes, string StoragePath)`; 예외 `AttachmentTooLargeException`, `UnsupportedImageException`.
  - `AttachmentDto(Guid Id, string Url, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAt)`, `PagedAttachmentsDto(AttachmentDto[] Items, int Total)`.
  - 관리: `POST /api/attachments`(multipart 필드 `file`) → 201 | 200(같은 내용 재업로드) | 400 | 413 | 415; `GET /api/attachments?skip=&take=`; `DELETE /api/attachments/{id:guid}` → 204 | 404.
  - 공개: `GET /attachments/{id:guid}/{fileName}` → 200 | 404. 조회는 `id`로만 하고 `fileName`은 파일 시스템 경로에 **절대 결합하지 않는다**.

- [ ] **Step 1: 실패하는 테스트 작성** — `PortfolioBlog.Api.Tests/Features/AttachmentEndpointsTests.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>첨부 업로드·목록·삭제(관리)와 공개 GET 통합 테스트.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 첨부는 내용 주소로 중복 제거되므로 같은 픽스처를 올리는 테스트끼리 서로의 결과(201/200)를 바꾼다.
/// 그래서 <b>테스트마다 격리된 <see cref="ApiFactory"/></b>(자체 DB + 자체 임시 첨부 폴더)를 만든다.</description></item>
/// <item><description><b>Memory Allocation:</b> 크기 초과 테스트가 10MB+1바이트 버퍼 하나를 만든다.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너와 로컬 임시 디렉터리를 쓴다. 팩토리는 <c>using</c>으로 해제되어 임시 폴더를 지운다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class AttachmentEndpointsTests(PostgresContainerFixture pg)
{
    private static readonly Dictionary<string, string?> NoOverrides = new();

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", name));

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string contentType = "application/octet-stream")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    private static async Task<AttachmentDto> UploadAsync(HttpClient client, byte[] bytes, string fileName, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var form = Form(bytes, fileName);
        using var res = await client.PostAsync("/api/attachments", form);
        Assert.Equal(expected, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AttachmentDto>(TestJson.Options))!;
    }

    private static async Task<HttpStatusCode> UploadStatusAsync(HttpClient client, MultipartFormDataContent form)
    {
        using (form)
        {
            using var res = await client.PostAsync("/api/attachments", form);
            return res.StatusCode;
        }
    }

    /// <summary>업로드하면 메타데이터가 제거된 파일이 저장되고, 공개 호스트에서 익명으로 받을 수 있으며, 응답 헤더가 스니핑·실행을 막는다.</summary>
    [Fact]
    public async Task Upload_StripsMetadata_AndServesPubliclyWithHardenedHeaders()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var original = Fixture("exif-gps.jpg");
        var dto = await UploadAsync(admin, original, "휴가 사진.JPG");

        Assert.Equal("image/jpeg", dto.ContentType);
        Assert.Equal("휴가 사진.jpg", dto.FileName); // 확장자는 시그니처에서 다시 정한다
        Assert.Equal(64, dto.Sha256.Length);
        Assert.StartsWith($"/attachments/{dto.Id}/", dto.Url, StringComparison.Ordinal);
        Assert.True(dto.SizeBytes < original.Length);

        using var visitor = factory.CreatePublicClient();
        using var res = await visitor.GetAsync(dto.Url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(res.Headers.CacheControl?.Public);
        Assert.Contains("immutable", res.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

        var served = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(dto.SizeBytes, served.Length);
        Assert.Equal(dto.Sha256, Convert.ToHexStringLower(SHA256.HashData(served)));
        foreach (var secret in new[] { "Exif", "SpikeCam", "secret" })
        {
            Assert.True(served.AsSpan().IndexOf(Encoding.ASCII.GetBytes(secret)) < 0, $"공개 응답에 '{secret}'가 남아 있다");
        }
    }

    /// <summary>네 형식 모두 받아들이고, Content-Type과 확장자는 시그니처에서 나온다(클라이언트가 보낸 이름·형식은 무시).</summary>
    [Theory]
    [InlineData("exif-text.png", "image/png", "png")]
    [InlineData("exif-xmp.webp", "image/webp", "webp")]
    [InlineData("comment-animated.gif", "image/gif", "gif")]
    [InlineData("progressive-trailing.jpg", "image/jpeg", "jpg")]
    public async Task Upload_AcceptsEachFormat(string fixture, string contentType, string extension)
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture(fixture), "wrong-name.exe");
        Assert.Equal(contentType, dto.ContentType);
        Assert.Equal("wrong-name." + extension, dto.FileName);
    }

    /// <summary>같은 내용을 다시 올리면 새로 만들지 않고 기존 첨부를 200으로 돌려준다. 이름이 달라도, 덧붙은 바이트만 달라도(제거 후 같아지므로) 같은 첨부다.</summary>
    [Fact]
    public async Task Upload_SameContent_ReturnsExisting()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var png = Fixture("exif-text.png");
        var first = await UploadAsync(admin, png, "a.png");
        var again = await UploadAsync(admin, png, "b.png", HttpStatusCode.OK);
        var padded = await UploadAsync(admin, [.. png, .. Encoding.ASCII.GetBytes("trailing bytes after IEND")], "c.png", HttpStatusCode.OK);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Id, padded.Id);
        Assert.Equal("a.png", again.FileName);
    }

    /// <summary>이미지가 아니거나 구조가 깨진 파일은 415, 빈 파일·필드 누락은 400, 10MB 초과는 413이다 — 어느 것도 500이 아니다.</summary>
    [Fact]
    public async Task Upload_RejectsBadInput_WithSpecificStatus()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var png = Fixture("exif-text.png");

        var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(svg, "x.svg", "image/svg+xml")));
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(html, "x.png", "image/png")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(png[..(png.Length / 2)], "cut.png")));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadStatusAsync(admin, Form([], "empty.png")));
        Assert.Equal(HttpStatusCode.BadRequest, await UploadStatusAsync(admin, new MultipartFormDataContent { { new StringContent("x"), "other" } }));

        var tooBig = new byte[AttachmentOptions.MaxBytes + 1];
        png.CopyTo(tooBig, 0); // 시그니처는 PNG다 — 크기 검사가 형식 검사보다 먼저여야 한다
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await UploadStatusAsync(admin, Form(tooBig, "big.png")));
    }

    /// <summary>업로드한 파일 이름은 경로 조각·제어문자를 걷어 낸 표시용 이름이 되고, 공개 URL의 파일 이름을 아무리 바꿔도 같은 파일이 나온다(경로에 쓰이지 않는다).</summary>
    [Fact]
    public async Task FileName_IsDisplayOnly_NeverAPath()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var dto = await UploadAsync(admin, Fixture("exif-xmp.webp"), "..\\..\\etc/pass\twd.webp");
        Assert.Equal("passwd.webp", dto.FileName);

        using var visitor = factory.CreatePublicClient();
        var expected = await visitor.GetByteArrayAsync(dto.Url);
        foreach (var name in new[] { "anything.webp", "..%2F..%2Fappsettings.json", "x" })
        {
            using var res = await visitor.GetAsync($"/attachments/{dto.Id}/{name}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(expected, await res.Content.ReadAsByteArrayAsync());
        }
        using var missing = await visitor.GetAsync($"/attachments/{Guid.NewGuid()}/x.webp");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>목록은 최신순 페이지네이션, 삭제는 DB 행과 디스크 파일을 함께 지우고 공개 URL은 404가 된다.</summary>
    [Fact]
    public async Task List_And_Delete()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        var older = await UploadAsync(admin, Fixture("exif-text.png"), "older.png");
        var newer = await UploadAsync(admin, Fixture("comment-animated.gif"), "newer.gif");

        var page = await admin.GetFromJsonAsync<PagedAttachmentsDto>("/api/attachments?skip=0&take=1", TestJson.Options);
        Assert.Equal(2, page!.Total);
        Assert.Equal(newer.Id, page.Items.Single().Id);
        using (var bad = await admin.GetAsync("/api/attachments?take=0")) Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        string physical;
        await using (var scope = factory.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Attachments.AsNoTracking().SingleAsync(a => a.Id == newer.Id);
            physical = scope.ServiceProvider.GetRequiredService<FileSystemAttachmentStore>().PhysicalPath(row.StoragePath);
        }
        Assert.True(File.Exists(physical));

        using (var res = await admin.DeleteAsync($"/api/attachments/{newer.Id}")) Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.False(File.Exists(physical));
        using (var again = await admin.DeleteAsync($"/api/attachments/{newer.Id}")) Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using var visitor = factory.CreatePublicClient();
        using (var gone = await visitor.GetAsync(newer.Url)) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var kept = await visitor.GetAsync(older.Url)) Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    /// <summary>공개 GET은 읽기 전용이다: 다른 메서드는 405이고 업로드 경로는 공개 호스트에 존재하지 않는다.</summary>
    [Fact]
    public async Task PublicSurface_IsReadOnly()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var visitor = factory.CreatePublicClient();
        using var form = Form(Fixture("exif-text.png"), "x.png");
        using var post = await visitor.PostAsync($"/attachments/{Guid.NewGuid()}/x.png", form);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await UploadStatusAsync(visitor, Form(Fixture("exif-text.png"), "x.png")));
    }

    /// <summary>임시 파일이 남지 않는다(성공·거부 어느 경로든).</summary>
    [Fact]
    public async Task Upload_LeavesNoTempFiles()
    {
        using var factory = new ApiFactory(pg, NoOverrides);
        using var admin = await factory.CreateLoggedInClientAsync();
        await UploadAsync(admin, Fixture("exif-gps.jpg"), "t.jpg");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await UploadStatusAsync(admin, Form(Encoding.UTF8.GetBytes("not an image"), "t.png")));

        var temp = Path.Combine(factory.AttachmentsRoot, ".tmp");
        Assert.True(!Directory.Exists(temp) || !Directory.EnumerateFiles(temp).Any());
    }
}
```

`FileName_IsDisplayOnly_NeverAPath`의 기대값 `"passwd.webp"`는 `DisplayName` 규칙의 결과다: 역슬래시를 슬래시로 바꾸고 마지막 세그먼트(`pass<TAB>wd.webp`)만 남긴 뒤 제어문자(TAB)를 지운다.

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AttachmentEndpointsTests"` → 컴파일 오류.

- [ ] **Step 2: 도메인 · DbContext · 마이그레이션**

```csharp
// PortfolioBlog.Api/Domain/Attachment.cs
namespace PortfolioBlog.Api.Domain;

/// <summary>업로드된 이미지의 메타 정보. 본체는 볼륨의 <see cref="StoragePath"/>에 있다(메타데이터 제거 후 바이트의 SHA-256이 곧 경로).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. DbContext 스코프 안 단일 스레드.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 1개(파일 내용은 들고 있지 않다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// </remarks>
public sealed class Attachment
{
    /// <summary>공개 URL 식별자(Guid v7). 글에 연결되지 않은 첨부도 이 값을 알면 읽을 수 있다.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>표시용 파일 이름(경로 조각·제어문자 제거, 확장자는 시그니처에서 다시 정함). 파일 시스템 경로에 쓰지 않는다.</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>시그니처에서 유도한 Content-Type(<c>image/png|jpeg|gif|webp</c>).</summary>
    public string ContentType { get; set; } = string.Empty;
    /// <summary>메타데이터 제거 후 크기(바이트).</summary>
    public long SizeBytes { get; set; }
    /// <summary>저장 루트 기준 상대 경로 <c>{sha[..2]}/{sha}.{ext}</c>. 서버가 만든 값만 들어간다.</summary>
    public string StoragePath { get; set; } = string.Empty;
    /// <summary>메타데이터 제거 후 바이트의 SHA-256(소문자 hex 64자). 유일.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>업로드 시각(UTC, 마이크로초 절삭).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
```

`AppDbContext.cs`: `public DbSet<Attachment> Attachments => Set<Attachment>();`(XML `<summary>` 포함)와 상수 `public const int FileNameMax = 255;`를 추가하고 `OnModelCreating`에:

```csharp
        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(FileNameMax);
            e.Property(x => x.ContentType).HasMaxLength(20);
            e.Property(x => x.StoragePath).HasMaxLength(80);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => x.Sha256).IsUnique();
            e.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, false);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Attachments_Size", "\"SizeBytes\" BETWEEN 1 AND 10485760");
                t.HasCheckConstraint("CK_Attachments_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("CK_Attachments_ContentType", "\"ContentType\" IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')");
                t.HasCheckConstraint("CK_Attachments_FileName_NotBlank", "length(btrim(\"FileName\")) > 0");
            });
        });
```

(DB CHECK의 정규식은 PostgreSQL 쪽 문자열이다. 앱은 SHA-256을 직접 계산하므로 같은 패턴을 .NET 정규식으로 공유하지 않는다 — 규칙 2.)

```bash
dotnet ef migrations add AddAttachments --project PortfolioBlog.Api --output-dir Infrastructure/Data/Migrations
```
Expected: `Attachments` 테이블, CHECK 4개, 유일 인덱스 `IX_Attachments_Sha256`, 인덱스 `IX_Attachments_CreatedAt_Id`. 기존 테이블 변경 없음.

- [ ] **Step 3: 옵션 · 저장소**

```csharp
// PortfolioBlog.Api/Infrastructure/Storage/AttachmentOptions.cs
namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>설정 섹션 <c>Attachments</c>.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    /// <summary>업로드 한도(10MB). Kestrel·multipart 한도는 이보다 1MB 크게 잡아 "한도 초과"를 앱이 413으로 답하게 한다.</summary>
    public const int MaxBytes = 10_485_760;
    /// <summary>첨부 저장 루트. 상대 경로면 콘텐츠 루트 기준. 정적 파일 루트 밖이어야 한다. 비어 있으면 시작 실패.</summary>
    public string RootPath { get; set; } = string.Empty;
}
```

```csharp
// PortfolioBlog.Api/Infrastructure/Storage/FileSystemAttachmentStore.cs
using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>업로드가 한도를 넘었다(413).</summary>
public sealed class AttachmentTooLargeException() : Exception("첨부 크기 한도를 넘었다.");

/// <summary>허용 형식이 아니거나 구조가 깨진 이미지다(415).</summary>
public sealed class UnsupportedImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>저장 결과.</summary>
/// <param name="Kind">시그니처로 판정한 형식.</param>
/// <param name="Sha256">메타데이터 제거 후 바이트의 SHA-256(소문자 hex).</param>
/// <param name="SizeBytes">메타데이터 제거 후 크기.</param>
/// <param name="StoragePath">루트 기준 상대 경로(<c>ab/abcdef….png</c>, 구분자는 항상 <c>/</c>).</param>
public sealed record StoredImage(ImageKind Kind, string Sha256, long SizeBytes, string StoragePath);

/// <summary>첨부를 로컬 볼륨에 내용 주소 방식으로 저장한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 필드는 생성 후 불변이고, 같은 내용의 동시 업로드는 "먼저 옮긴 쪽이 이기고 나머지는 자기 임시 파일을 지운다"로 수렴한다(<c>File.Move(overwrite: false)</c>).</description></item>
/// <item><description><b>Memory Allocation:</b> 업로드 크기와 무관하게 64KB 풀 버퍼. 10MB를 메모리에 올리지 않는다(LOH 회피).</description></item>
/// <item><description><b>Blocking:</b> 업로드 수신은 비동기 I/O. 메타데이터 제거와 해시는 임시 파일에 대한 동기 I/O다(최대 10MB, 관리 표면 전용).</description></item>
/// </list>
/// 경로는 전부 서버가 만든다: 임시 파일 이름은 Guid, 최종 경로는 SHA-256. 업로드된 파일 이름은 이 클래스에 들어오지도 않는다.
/// </remarks>
public sealed class FileSystemAttachmentStore
{
    private const int BufferSize = 64 * 1024;
    private readonly string _root;
    private readonly string _temp;

    public FileSystemAttachmentStore(IOptions<AttachmentOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("Attachments:RootPath 설정이 없습니다.");
        _root = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        _temp = Path.Combine(_root, ".tmp");
    }

    public string PhysicalPath(string storagePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, storagePath.Replace('/', Path.DirectorySeparatorChar)));
        // DB 값이 손상됐더라도 루트 밖을 가리키면 읽지 않는다.
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new InvalidOperationException("첨부 경로가 저장 루트를 벗어난다.");
    }

    public bool TryDelete(string storagePath)
    {
        try
        {
            File.Delete(PhysicalPath(storagePath));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task<StoredImage> SaveAsync(Stream upload, CancellationToken ct)
    {
        Directory.CreateDirectory(_temp);
        var rawPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".upload");
        var cleanPath = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".clean");
        try
        {
            await ReceiveAsync(upload, rawPath, ct);

            ImageKind kind;
            // FileStream(FileOptions.SequentialScan): OS 미리 읽기 힌트. 제거기는 앞에서 뒤로 한 번만 읽는다.
            using (var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize, FileOptions.SequentialScan))
            using (var clean = new FileStream(cleanPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize))
            {
                Span<byte> header = stackalloc byte[ImageSignature.HeaderLength];
                var read = raw.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                kind = ImageSignature.Detect(header[..read]) ?? throw new UnsupportedImageException("PNG·JPEG·GIF·WebP 이미지만 올릴 수 있습니다.");
                raw.Position = 0;
                try { MetadataStripper.Strip(kind, raw, clean); }
                catch (InvalidDataException ex) { throw new UnsupportedImageException("이미지 파일 구조가 손상됐습니다.", ex); }
            }

            string sha;
            long size;
            using (var clean = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                size = clean.Length;
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(clean, ct));
            }

            var relative = $"{sha[..2]}/{sha}.{ImageSignature.Extension(kind)}";
            var final = PhysicalPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (!File.Exists(final))
            {
                try { File.Move(cleanPath, final, overwrite: false); }
                catch (IOException) when (File.Exists(final)) { /* 같은 내용의 동시 업로드가 먼저 옮겼다 — 내용이 같으므로 그 파일을 쓴다 */ }
            }
            return new StoredImage(kind, sha, size, relative);
        }
        finally
        {
            File.Delete(rawPath);   // 없으면 아무 일도 하지 않는다
            File.Delete(cleanPath);
        }
    }

    private static async Task ReceiveAsync(Stream upload, string path, CancellationToken ct)
    {
        // ArrayPool<byte>.Shared: 스레드별 캐시를 먼저 보는 버킷 풀. 64KB는 LOH 임계(85,000바이트) 아래다.
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            long total = 0;
            int read;
            while ((read = await upload.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                total += read;
                if (total > AttachmentOptions.MaxBytes) throw new AttachmentTooLargeException();
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
```

- [ ] **Step 4: 계약 · 엔드포인트**

```csharp
// PortfolioBlog.Api/Contracts/AttachmentDtos.cs
namespace PortfolioBlog.Api.Contracts;

/// <summary>첨부 메타 정보.</summary>
/// <param name="Id">식별자.</param>
/// <param name="Url">마크다운에 그대로 넣을 수 있는 루트 상대 URL(<c>/attachments/{id}/{파일명}</c>, 파일명은 URL 인코딩됨).</param>
/// <param name="FileName">표시용 파일 이름.</param>
/// <param name="ContentType">시그니처에서 유도한 Content-Type.</param>
/// <param name="SizeBytes">메타데이터 제거 후 크기.</param>
/// <param name="Sha256">메타데이터 제거 후 바이트의 SHA-256.</param>
/// <param name="CreatedAt">업로드 시각(UTC).</param>
public sealed record AttachmentDto(Guid Id, string Url, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);

/// <summary>첨부 목록 페이지.</summary>
/// <param name="Items">최신순 항목.</param>
/// <param name="Total">전체 건수.</param>
public sealed record PagedAttachmentsDto(AttachmentDto[] Items, int Total);
```

```csharp
// PortfolioBlog.Api/Features/Attachments/AttachmentEndpoints.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Features.Attachments;

/// <summary>첨부 관리 API(업로드·목록·삭제). 보호된 <c>/api</c> 그룹 안에 있어 본문을 읽기 전에 호스트·IP·CSRF 헤더·Origin·세션 검사를 통과한 요청만 도달한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러. 저장소는 Thread-safe 싱글턴, DbContext는 요청 스코프.</description></item>
/// <item><description><b>Memory Allocation:</b> 업로드는 프레임워크가 64KB를 넘는 폼 파일을 디스크로 버퍼링하고, 저장소가 64KB 단위로 옮긴다. 10MB를 메모리에 올리지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 수신·DB는 비동기. 메타데이터 제거·해시는 임시 파일 동기 I/O.</description></item>
/// </list>
/// antiforgery: 최소 API는 <c>IFormFile</c> 바인딩에 프레임워크 antiforgery 검증을 요구한다. 이 앱의 CSRF 방어는 미들웨어의
/// <c>X-Requested-With</c> + <c>Origin</c> 검사와 <c>SameSite=Strict</c> 쿠키이므로 이 엔드포인트에서만 <c>DisableAntiforgery()</c>로 대체한다(전역 비활성화가 아니다).
/// </remarks>
public static class AttachmentEndpoints
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        var attachments = api.MapGroup("/attachments");
        attachments.MapGet("", ListAsync).WithName("ListAttachments");
        attachments.MapPost("", UploadAsync).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(AttachmentOptions.MaxBytes + 1_048_576))
            .WithName("UploadAttachment");
        attachments.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteAttachment");
    }

    internal static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, $"/attachments/{a.Id}/{Uri.EscapeDataString(a.FileName)}", a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.CreatedAt);

    private static async Task<IResult> ListAsync(AppDbContext db, int? skip, int? take, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (skip is < 0) errors.Add("skip", "skip은 0 이상이어야 합니다.");
        if (take is < 1 or > MaxTake) errors.Add("take", $"take는 1~{MaxTake}여야 합니다.");
        if (errors.Any) return TypedResults.ValidationProblem(errors.ToDictionary());

        var total = await db.Attachments.CountAsync(ct);
        var rows = await db.Attachments.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Skip(skip ?? 0).Take(take ?? DefaultTake).ToListAsync(ct);
        return TypedResults.Ok(new PagedAttachmentsDto(rows.Select(ToDto).ToArray(), total));
    }

    private static async Task<IResult> UploadAsync(IFormFile? file, AppDbContext db, FileSystemAttachmentStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["multipart 필드 'file'에 비어 있지 않은 이미지가 필요합니다."] });
        }
        if (file.Length > AttachmentOptions.MaxBytes) return TooLarge();

        StoredImage stored;
        try
        {
            await using var upload = file.OpenReadStream();
            stored = await store.SaveAsync(upload, ct);
        }
        catch (AttachmentTooLargeException) { return TooLarge(); }
        catch (UnsupportedImageException ex)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "지원하지 않는 이미지", detail: ex.Message);
        }

        var existing = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Sha256 == stored.Sha256, ct);
        if (existing is not null) return TypedResults.Ok(ToDto(existing));

        var attachment = new Attachment
        {
            FileName = DisplayName(file.FileName, stored.Kind),
            ContentType = ImageSignature.ContentType(stored.Kind),
            SizeBytes = stored.SizeBytes, StoragePath = stored.StoragePath, Sha256 = stored.Sha256, CreatedAt = DbClock.UtcNow(),
        };
        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: DbConflict.UniqueViolation })
        {
            // 같은 내용의 동시 업로드가 먼저 저장됐다. 파일은 같은 경로(같은 해시)이므로 그 행을 돌려준다.
            db.ChangeTracker.Clear();
            return TypedResults.Ok(ToDto(await db.Attachments.AsNoTracking().SingleAsync(a => a.Sha256 == stored.Sha256, ct)));
        }
        loggers.CreateLogger("PortfolioBlog.Api.Audit").LogInformation(
            "첨부 업로드. AttachmentId={AttachmentId} Sha256={Sha256} SizeBytes={SizeBytes}", attachment.Id, attachment.Sha256, attachment.SizeBytes);
        var dto = ToDto(attachment);
        return TypedResults.Created(dto.Url, dto);
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, FileSystemAttachmentStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null) return TypedResults.NotFound();
        db.Attachments.Remove(attachment);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return TypedResults.NotFound(); } // 다른 탭이 먼저 지웠다

        // DB와 파일 시스템은 한 트랜잭션이 아니다. 행을 먼저 지우면 실패해도 남는 것은 "참조 없는 파일"뿐이다(반대 순서는 깨진 링크를 만든다).
        var logger = loggers.CreateLogger("PortfolioBlog.Api.Audit");
        if (!store.TryDelete(attachment.StoragePath)) logger.LogWarning("첨부 파일 삭제 실패(고아 파일). AttachmentId={AttachmentId} Sha256={Sha256}", attachment.Id, attachment.Sha256);
        logger.LogInformation("첨부 삭제. AttachmentId={AttachmentId} Sha256={Sha256}", attachment.Id, attachment.Sha256);
        return TypedResults.NoContent();
    }

    /// <summary>업로드된 파일 이름을 표시용으로 정리한다: 경로 조각·제어문자(NUL 포함) 제거, 확장자는 시그니처 기준으로 교체, 길이 제한.</summary>
    internal static string DisplayName(string? uploaded, ImageKind kind)
    {
        var name = (uploaded ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Replace("..", string.Empty, StringComparison.Ordinal).Trim().Trim('.');
        var dot = name.LastIndexOf('.');
        var stem = (dot > 0 ? name[..dot] : name).Trim();
        if (stem.Length == 0) stem = "image";
        var extension = "." + ImageSignature.Extension(kind);
        var max = AppDbContext.FileNameMax - extension.Length;
        if (stem.Length > max) stem = stem[..max];
        return stem + extension;
    }

    private static IResult TooLarge() =>
        TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "첨부가 너무 큽니다", detail: $"이미지는 {AttachmentOptions.MaxBytes / 1_048_576}MB 이하여야 합니다.");
}
```

```csharp
// PortfolioBlog.Api/Features/Attachments/PublicAttachmentEndpoints.cs
using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Storage;

namespace PortfolioBlog.Api.Features.Attachments;

/// <summary>첨부 공개 GET. <c>/api</c> 밖에 있는 <b>읽기 전용</b> 엔드포인트다(공개 호스트의 글 본문과, 관리 호스트의 미리보기 iframe이 쓴다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 핸들러.</description></item>
/// <item><description><b>Memory Allocation:</b> DB 프로젝션 1행. 본체는 <c>PhysicalFile</c> 결과가 스트리밍한다(파일을 메모리에 올리지 않는다).</description></item>
/// <item><description><b>Blocking:</b> 비동기 DB 조회 + 비동기 파일 전송. 기본 인증 스킴이 없으므로 쿠키가 실린 요청이라도 세션 DB 조회는 일어나지 않는다.</description></item>
/// </list>
/// 조회 키는 <c>id</c>뿐이다. <c>fileName</c>은 URL을 읽기 좋게 하는 장식이며 어떤 값이 와도 경로에 결합하지 않는다.
/// 응답은 스니핑 금지 + 자체 CSP(<c>default-src 'none'; sandbox</c>)로, 설령 이미지로 위장한 콘텐츠가 저장돼 있어도 문서로 실행되지 않는다.
/// </remarks>
public static class PublicAttachmentEndpoints
{
    public const string Pattern = "/attachments/{id:guid}/{fileName}";

    public static void MapPublicAttachmentEndpoints(this WebApplication app)
    {
        app.MapGet(Pattern, GetAsync).AllowAnonymous().WithName("GetAttachment");
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, AppDbContext db, FileSystemAttachmentStore store, CancellationToken ct)
    {
        var row = await db.Attachments.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.StoragePath, a.ContentType }).SingleOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound();
        var path = store.PhysicalPath(row.StoragePath);
        if (!File.Exists(path)) return TypedResults.NotFound();

        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        headers.CacheControl = "public, max-age=31536000, immutable"; // 내용 주소: 같은 id의 내용은 바뀌지 않는다
        return TypedResults.PhysicalFile(path, row.ContentType);
    }
}
```

배선:
- `Features/ApiEndpoints.cs`: `api.MapPreviewEndpoints();` 다음 줄에 `api.MapAttachmentEndpoints();`(using `PortfolioBlog.Api.Features.Attachments`).
- `Program.cs`: 서비스 등록에 아래를 추가(using `Microsoft.AspNetCore.Http.Features`, `PortfolioBlog.Api.Infrastructure.Storage`, `PortfolioBlog.Api.Features.Attachments`)하고, `app.MapApiEndpoints();` 다음 줄에 `app.MapPublicAttachmentEndpoints();`.

```csharp
builder.Services.Configure<AttachmentOptions>(builder.Configuration.GetSection(AttachmentOptions.SectionName));
builder.Services.AddSingleton<FileSystemAttachmentStore>();
// multipart 한도를 첨부 한도보다 1MB 크게: 10MB를 조금 넘는 업로드는 앱이 413으로 답하고, 그보다 훨씬 큰 본문은 프레임워크가 읽다가 끊는다.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = AttachmentOptions.MaxBytes + 1_048_576);
```

- `StartupValidation.Validate`: `IOptions<AttachmentOptions>`를 읽어 `RootPath`가 비어 있으면 **모든 환경에서** `InvalidOperationException("설정 Attachments:RootPath 이(가) 필수입니다. …")`. 그리고 `services.GetRequiredService<FileSystemAttachmentStore>()`를 한 번 해석해 경로 계산 오류를 시작 시점에 드러낸다.
- `appsettings.json`: `"Attachments": { "RootPath": "" }`. `appsettings.Development.json`: `"Attachments": { "RootPath": ".data/attachments" }`.
- `.gitignore`의 프로젝트 커스텀 블록에 추가: `# 로컬 개발 데이터(첨부 등)` / `.data/` — Stop 훅이 `git add -A`를 하므로 **첨부를 올려 보기 전에** 먼저 넣는다.
- `ApiFactory.cs`: `public string AttachmentsRoot { get; } = Path.Combine(Path.GetTempPath(), "portfolioblog-tests", Guid.NewGuid().ToString("N"));`, 기본값 블록에 `builder.UseSetting("Attachments:RootPath", AttachmentsRoot);`, 그리고

```csharp
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(AttachmentsRoot)) Directory.Delete(AttachmentsRoot, recursive: true);
    }
```

- `StartupValidationTests`에 추가: `AnyEnvironment_MissingAttachmentsRoot_Fails` — `["Attachments:RootPath"] = ""` → 시작 실패, 메시지에 `Attachments:RootPath`.
- `AccessMatrixTests`: 라우트 수 단언을 `>= 19`로(미리보기 1 + 첨부 3), `PublicAllowlist`에 `PublicAttachmentEndpoints.Pattern`과 같은 문자열 `"/attachments/{id:guid}/{fileName}"`를 추가.

- [ ] **Step 5: 통과 확인**

Run: `dotnet test PortfolioBlog.Api.Tests --filter "FullyQualifiedName~AttachmentEndpointsTests|FullyQualifiedName~AccessMatrixTests|FullyQualifiedName~StartupValidationTests|FullyQualifiedName~DatabaseSchemaTests"`
Expected: 전부 PASS. 조사하고 고칠 가능성이 있는 지점(추측하지 말 것):
- **`IFormFile? file` 바인딩과 `DisableAntiforgery()`** — antiforgery 관련 예외(500)나 400이 나오면 최소 API의 폼 바인딩 요구 사항을 확인한다. 필드가 없을 때 400이 핸들러가 아니라 프레임워크에서 나와도(본문 없는 400) 테스트는 통과한다.
- **10MB+1 → 413** — TestServer는 `MaxRequestBodySize`를 강제하지 않으므로 413은 핸들러의 `file.Length` 검사에서 나와야 한다. `MultipartBodyLengthLimit`(11MB) 때문에 프레임워크가 먼저 400/500을 내면 한도 값을 확인한다.
- **`TypedResults.PhysicalFile` + 직접 쓴 헤더** — `Cache-Control`이 결과 실행 중 덮어써지면 헤더를 `http.Response.OnStarting`에서 쓴다.
- **공개 호스트에서의 `POST /attachments/...` → 405**, `POST /api/attachments` → 404(미들웨어의 호스트 검사).
- **Windows 파일 잠금** — 삭제 테스트에서 `File.Delete`가 실패하면 직전 GET의 응답 스트림이 아직 열려 있는 것이다(테스트가 응답을 `using`으로 닫는지 확인).

- [ ] **Step 6: 문서 갱신**
- `plan/tech_blog_0920.md` 3.5절의 "라이브러리 선정은 구현 계획의 스파이크에서 확정한다(후보: …)" 문장을 결정으로 교체: `ColorCode.HTML`(클래스 출력), 미지원 언어(bash·yaml·go·rust 등)는 일반 코드블록. 3.8절의 "구현 방식은 구현 계획에서 확정한다"를 결정으로 교체: 디코딩 없는 스트림 기반 컨테이너 파싱, JPEG는 APP0·APP2·APP14만 남기고 APP1·APP3~13·APP15·COM 제거 + 엔트로피 구간을 마커 단위로 따라가 EOI 뒤의 바이트를 버림, PNG는 청크 허용 목록, WebP는 EXIF·XMP 청크 제거 + VP8X 플래그 해제 + RIFF 크기 재기록, GIF는 주석·비애니메이션 애플리케이션 확장 제거. 8절 표에 Plan 2A 행 추가(파일 경로, 범위, "완료"), Plan 2의 나머지는 "Plan 2B(2A 완료 후 작성)".
- `README.md`: 로드맵 2단계 행을 "2A 완료(마크다운 파이프라인·미리보기·첨부) / 2B 예정(공개 페이지·검색·피드·보안 헤더)"로, 스택 표에 Markdig·ColorCode·HtmlSanitizer 추가. 공개 페이지가 아직 없다는 사실은 그대로 둔다.

- [ ] **Step 7: 전체 회귀 · 하네스 감사 · 커밋**

```bash
dotnet build PortfolioBlog.slnx -c Release
dotnet test PortfolioBlog.slnx -c Release
pwsh scripts/harness-audit.ps1
```
Expected: 경고 0·오류 0, 전부 통과(294 + 첨부 11 + 시작 검증 1 = 306 — 실제 수를 보고서에 적는다), 감사 8/8. 작업 트리에 `.data/`나 임시 첨부 폴더가 잡히지 않는지 `git status`로 확인. 0x00 검사(이미지 픽스처 제외) 후:

```bash
git add -A
git commit -m "추가: 메타데이터를 제거해 내용 주소로 저장하는 이미지 첨부

- 업로드는 64KB 단위로 임시 파일에 받고(10MB 초과 413), 시그니처 판정·메타데이터 제거 후 그 바이트의 SHA-256을 경로로 사용
- 같은 내용은 한 번만 저장되고 재업로드는 기존 첨부를 돌려준다
- 파일 이름은 표시용일 뿐 경로에 결합하지 않는다(공개 URL의 이름을 바꿔도 같은 파일)
- 공개 GET은 /api 밖의 읽기 전용 엔드포인트: nosniff + default-src 'none'; sandbox + immutable 캐시
- 접근 매트릭스의 공개 허용 목록에 명시적으로 등록

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 이 계획이 다루지 않는 것 (Plan 2B 이후)

| 항목 | 계획 |
|---|---|
| 공개 Razor 페이지(목록·글·태그·시리즈·검색), Atom·sitemap·robots, 코드 강조용 CSS | Plan 2B |
| 공개·관리 응답의 보안 헤더(CSP·HSTS·Referrer-Policy·Permissions-Policy), 공개 페이지·검색 속도 제한(`RateLimitPolicy`에 추가) | Plan 2B |
| 공개 읽기 전용 DbContext 등록과 `statement_timeout`, 공개용 프로젝션(`Version`을 노출하지 않는다) | Plan 2B |
| "앱 검증 ⊆ DB 제약" 테스트, 미사용 CHECK 제약 커버리지 | Plan 2B |
| 관리 API JSON 본문 256KB 한도(엔드포인트별 `RequestSizeLimit`) | Plan 2B |
| 첨부 고아 파일 정리 잡, 글↔첨부 참조 추적 | 스펙 7절(확장 포인트) |
| `DataProtection:KeysPath` 필수화, 첨부·키 볼륨 권한, Caddy `request_body` 한도, 배포 이미지에서 디자인 타임 어셈블리 제외 | Plan 4 |

## Self-Review 결과

**스펙 대비 점검(2A 범위)**

| 스펙 | 구현 Task |
|---|---|
| 3.5 확장 허용 목록·raw HTML 비활성, UrlPolicy(링크 스킴, 이미지는 자체 첨부만, 제어문자·공백 처리), 서버 측 하이라이팅(클래스만), 최종 HTML 허용 목록, 순수 함수 + 공격 코퍼스 단위 테스트 | Task 2 |
| 3.4 `POST /api/preview` — 공개 페이지와 동일 파이프라인 | Task 3 |
| 3.7 미리보기 60회/분·동시 2·본문 200KB, 업로드 10MB·접근 검사는 본문을 읽기 전 | Task 3, Task 5(접근 검사는 1단계 미들웨어가 이미 보장 — `AccessMatrixTests`가 새 엔드포인트를 자동 검사) |
| 3.8 시그니처 기반 형식·확장자·Content-Type, SVG 불가, 메타데이터 제거(디코딩 없이), 제거 **후** 바이트의 SHA-256, 내용 주소 경로, `overwrite:false` 이동, 64KB 버퍼, 저장 루트는 정적 루트 밖·서버 생성 경로만, 목록·삭제 API, 삭제 시 파일 실패는 로그 | Task 4, Task 5 |
| 3.6 첨부 응답 헤더(`default-src 'none'; sandbox`, nosniff, immutable 캐시) | Task 5 |
| 3.4 `GET /attachments/{id}/{fileName}` — 조회는 id로만 | Task 5 |
| 2.6 Codex ⑭ 중 수용분(EXIF 제거), ㉑(IFormFile과 antiforgery는 해당 엔드포인트에서만 대체) | Task 4, Task 5 |
| 1단계 최종 리뷰 권고: 중앙 패키지 버전, 텍스트 규칙 헬퍼, 속도 제한 이전·IP 키 정규화·Retry-After, 접근 매트릭스 닫힌 세계, 정책이 있을 때만 인증 | Task 1 |

**스펙과 다르게 정한 것:** 없음. 스펙이 "구현 계획에서 확정"으로 남긴 두 가지(하이라이팅 라이브러리, 메타데이터 제거 방식)를 스파이크로 확정했고 Task 5가 스펙 문서를 갱신한다.

**자리표시자·타입 일관성:** `TextRules.ContainsNul/NulMessage`, `ClientIp.PartitionKey`, `RateLimitMetadata(RateLimitPolicy)`, `RateLimitingExtensions.Window/Concurrency`, `MarkdownRenderer.Render/MaxInputBytes`, `HtmlAllowlist.Create/AllowedTags/AllowedAttributes`, `UrlPolicy.IsAllowedLink/IsAllowedImage`, `ImageSignature.Detect/Extension/ContentType/HeaderLength`, `MetadataStripper.Strip`, `FileSystemAttachmentStore.SaveAsync/PhysicalPath/TryDelete`, `AttachmentOptions.MaxBytes`, `PublicAttachmentEndpoints.Pattern`, `ApiFactory.AttachmentsRoot`는 정의한 Task와 쓰는 Task에서 이름·시그니처가 같다. 첨부 통합 테스트는 내용 주소 중복 제거 때문에 테스트마다 격리된 `ApiFactory`를 만든다.

---

## 구현 중 발견해 고친 계획 결함 (2026-09-21 실행 기록)

이 계획의 코드 블록을 그대로 옮긴 구현은 매번 첫 실행에 통과했다. 아래는 **계획 자체가 틀렸던 곳**이며, 작업별 리뷰(직접 공격·측정)에서 발견해 같은 브랜치에서 고쳤다. 위 본문의 해당 코드·문장은 아래 내용으로 **대체된 것**으로 읽는다(본문은 기록 보존을 위해 그대로 둔다).

| # | Task | 계획에 적힌 것 | 실제 | 고친 것 |
|---|------|----------------|------|---------|
| 1 | 1 | "기본 인증 스킴을 없애면 공개 경로에서 인증 비용이 0" | 스킴이 하나면 ASP.NET Core가 그것을 자동으로 기본 스킴으로 삼는다(리뷰어 실측). 목표 자체가 성립하지 않는다 | 명시적 기본 스킴 복원, 사실대로 쓴 주석, 손상된 쿠키 테스트. Task 5 공개 첨부 엔드포인트의 "세션 DB 조회가 일어나지 않는다" 문장도 같은 이유로 거짓 → 사실대로 수정 |
| 2 | 1 | 닫힌 세계 테스트가 `StartsWith("/api")`로 판정 | `/apix`, `/api-docs`가 `/api` 아래로 오인된다 | 세그먼트 경계 판정 `IsUnderApi` + Theory |
| 3 | 2 | "렌더링은 약 200ms", 크기 제한(200KB)이 비용 상한 | ColorCode 토크나이저는 줄 길이에 이차(200KB 한 줄 = 12분 25초), 닫히지 않은 `/*` 뒤 C 계열 코드에서 **지수**(1,320바이트 = 7초, 약 1,600바이트 = 60초 초과). Markdig 자동 제목 id는 중복 제목에 이차 | 줄 길이 예산 400자, 문서당 강조 예산 60,000자, **정규식 매치 타임아웃 250ms + 렌더당 강조 시간 예산 2,000ms**(ColorCode의 공개 확장 지점 `ILanguageCompiler`/`ILanguageParser` 사용), 선형 제목 id(`HeadingIds`). 최악 강조 시간 약 2.25초(실측 2,266ms) |
| 4 | 2 | 크기 초과만 `ArgumentException` | Markdig의 중첩 한도 128도 `ArgumentException`으로 나온다(129바이트 입력) | `MarkdownTooComplexException` + 저장·미리보기에서 필드 키 400. 저장 전에 한 번 렌더링해 "저장은 됐는데 공개 페이지가 영구 500"을 막음 |
| 5 | 2 | (1차 수정 지시) 블록당 강조 예산 20,000자 | 근거 측정이 "8KB **한 줄**"이었다. 약 285줄 넘는 평범한 소스 파일의 강조가 조용히 꺼졌다 | 블록 예산 제거(문서 예산 + 시간 예산이 실제 방어) |
| 6 | 2 | (1차 수정 지시) 제목 텍스트 = 모든 `LiteralInline` + 모든 `CodeInline` | 글자와 인라인 코드가 섞인 제목의 id가 문서 순서를 잃는다(`install--firstnpm`) | 단일 순서 순회 |
| 7 | 3 | 미리보기 Blocking 주석 "최대 수백 ms" | 강조 상한 약 2.25초 + Markdig 파서 잔여 비용(적대적 200KB에서 최대 약 8.5초) | 사실대로 수정. 분당 한도와 동시 실행 제한이 **둘 다** 필요한 이유 명시 |
| 8 | 4 | GIF: 주석·비애니메이션 애플리케이션 확장만 제거 / JPEG: APP0·APP2·APP14를 마커 번호로 유지 / WebP: EXIF·XMP만 제거 / PNG: 허용 목록 | GIF는 라벨 `0xFE`만 버리는 **거부 목록**(5MB 페이로드가 라벨만 바꿔 통과), JPEG `JFXX` 썸네일(자르기 전 원본) 잔존·알 수 없는 마커 통과, WebP 알 수 없는 청크 통과, PNG 시그니처+IEND(20바이트) 통과 | **네 형식 모두 기본 거부.** GIF: 그래픽 제어(정확히 4바이트)와 반복 횟수 블록만. JPEG: 구조 마커 허용 목록 + 식별자 확인된 APP0 `JFIF`/APP2 `ICC_PROFILE`/APP14 `Adobe`, 그 외 마커는 거부. WebP: 청크 허용 목록, `VP8X`=10·`ANIM`=6. PNG: IHDR 최초·단일, IDAT 1개 이상, 고정 크기 표, `sPLT` 제거 |
| 9 | 4 | 메모리 주석 "파일 크기와 무관" | 청크마다 `Encoding.ASCII.GetString` → 청크 수에 비례(43만 청크 PNG = 14MB 할당) | 바이트 비교로 교체, 할당 0바이트를 테스트로 고정 |
| 10 | 5 | `stem[..max]`로 파일 이름 자르기 | 서로게이트 쌍을 쪼개면 Npgsql이 INSERT에서 `EncoderFallbackException` → 500 + 고아 파일 | 코드 포인트 경계에서 자르고 짝 없는 서로게이트 제거. 점에서 잘려 `..`가 되살아나는 경우도 함께 수정 |
| 11 | 5 | 접근 매트릭스가 모든 보호 엔드포인트에 JSON 본문 전송 | `Accepts` 메타데이터가 있는 엔드포인트는 Content-Type이 안 맞으면 라우팅이 인가보다 **먼저** 415로 답한다(핸들러·바인딩은 실행되지 않음) | multipart 엔드포인트에는 multipart를 보내고, "잘못된 Content-Type도 접근 게이트를 우회하지 못한다"를 별도 테스트로 고정 |
| 12 | 5 | `File.Exists` → `PhysicalFile`, 저장 루트는 비어 있지 않은지만 검사 | 그 사이 삭제되면 500. 잘못 준비된 볼륨은 첫 업로드에서야 500 | `FileShare.Read\|Delete`로 직접 열어 스트림 응답(열기 실패 = 404, 서빙 중 삭제 가능), 강한 ETag(sha256)·HEAD 지원, 시작 시 절대 경로 요구 + 쓰기 확인 |
| 14 | 5 | 저장 루트 포함 검사 `full.StartsWith(_root + 구분자)` | `Attachments:RootPath`가 구분자로 끝나면(`/data/attachments/`) 시작 검증은 통과하고 모든 업로드·공개 GET이 500(최종 리뷰가 실제 Production 호스트에서 발견) | 루트를 `TrimEndingDirectorySeparator`로 정규화, 시작 시 `PhysicalPath` 자체 점검, 끝 구분자 루트 테스트 |
| 15 | 5 | HEAD 뒤 `File.Delete` 성공으로 "핸들 해제"를 증명 | 운영 핸들이 `FileShare.Delete`라 핸들이 새도 삭제가 성공한다 — 실패할 수 없는 테스트 | `FileShare.None` 배타 열기(짧은 재시도)로 교체, 304 경로에도 적용 |
| 16 | 전체 | 각 Task의 "N개 통과" 산식 | 기준선이 실행 중 계속 바뀌었다(178 → 186 → 284 → …) | 구현자가 실제 수를 보고하도록 지시. 최종: Release 빌드 경고 0, 테스트 412개 통과 |

### 다음 계획이 지켜야 할 규칙 (Plan 1 정오표 규칙 1~4에 이어서)

5. **비용 상한은 크기가 아니라 시간으로 건다.** 남의 정규식·파서를 적대적 입력에 돌릴 때 길이 제한은 상한이 아니다. 매치 타임아웃과 누적 시간 예산을 걸고, 초과 시 안전한 평문 경로로 떨어뜨린다.
6. **파서는 기본 거부.** "이것만 버린다"가 아니라 "이것만 남긴다"로 쓴다. 남기는 블록은 **모양(크기·서브블록 구조)**까지 검사한다. 디코딩 없이는 닫을 수 없는 표면은 문서에 잔여 표면으로 적고 실제 완화책(크기 상한, 시그니처 기반 content-type, `nosniff`, CSP sandbox)을 함께 적는다.
7. **주석의 성능·동작 주장은 측정한 것만 쓴다.** 이번 계획에서 거짓으로 드러난 주석: "약 200ms", "최대 수백 ms", "인증 비용 0", "파일 크기와 무관", "반복 횟수만 남긴다". 리뷰어에게 주석을 코드와 줄 단위로 대조하게 한다.
8. **테스트가 실패할 수 있는지 확인한다.** 이번에 걸러진 것: 픽스처에 없는 문자열의 부재를 단언, 허용 목록이라면서 거부 목록을 단언, 8초 상한이라 어느 구현이든 통과, 핸들러가 절대 내지 않는 문구의 부재를 단언.
9. **문자열을 자를 때는 코드 포인트 경계에서**, 그리고 자른 뒤에 앞 단계의 불변식(`..` 없음, 끝 점·공백 없음)이 다시 깨지지 않는지 본다.

### Plan 2B로 넘기는 항목

- 공개 페이지는 렌더링한 HTML을 **캐시**하거나 렌더 동시성을 제한해야 한다(렌더링은 동기·취소 불가, 강조 최대 약 2.25초 + Markdig 파서 잔여 비용: 적대적 200KB에서 인라인 약 8.5초, 블록 약 6.6초). 시작 시 렌더러 워밍업(첫 렌더 약 185ms).
- 사이트 CSS에 id 선택자를 쓰지 않는다(제목 id는 작성자 텍스트에서 나온다). 표 정렬이 필요하면 허용 클래스를 추가한다(지금은 sanitizer가 `style`을 지워 정렬이 사라진다).
- 관리 API JSON 본문 크기 한도(지금은 Kestrel 기본 30MB 뒤에 200KB 검증), 업로드 속도 제한, `RequestSizeLimit` 메타데이터가 최소 API에서 실제로 적용되는지 확인.
- 속도 제한 체인: 동시 실행 거부가 분당 허용량을 소모하고 `Retry-After: 60`을 돌려준다(로그인·미리보기 공통).
- 고아 파일 정리(중복 외 사유로 INSERT가 실패한 경우, 오래된 `.tmp`), 첨부 삭제가 캐시 사본을 회수하지 못한다는 운영 메모(Plan 4의 Caddy).
- 공개 호스트에서도 관리자 쿠키가 실린 수제 요청은 세션 DB 조회를 일으킨다 → 공개 속도 제한이 상한.
- 글 저장(`POST`/`PUT /api/posts`)도 렌더링을 하지만 동시성 제한이 없다 → 2B의 렌더 게이트/캐시가 저장 경로도 덮어야 한다(평범한 모양의 153KB 입력이 1.1~1.3초).
- 미리보기(`sandbox` iframe `srcdoc`)의 이미지는 **관리 오리진**에서 읽힌다. 첨부 응답에 `Cross-Origin-Resource-Policy: same-origin`을 붙이면 미리보기가 깨진다(`same-site`를 쓰거나 생략). 실제 브라우저에서 `img-src` 동작을 확인할 것(Plan 3).
- `AllowedHosts`가 Production에서 `*`다 → 설정된 두 호스트로 묶거나 시작 실패. `Server: Kestrel` 헤더 제거(`AddServerHeader=false`)는 보안 헤더 작업과 함께.
- 라우트 제약 실패(`/attachments/not-a-guid/x`)의 프레임워크 404에는 nosniff·CSP가 없다(본문이 없어 무해) → 전역 보안 헤더 미들웨어에서 해결.
- Plan 4: Caddy `request_body`는 앱 상한(10MB)이 아니라 **프레임워크 상한(11MB)**에 맞춘다. Release 출력에 EF 디자인 타임 어셈블리가 섞여 나간다(이미지에서 제거). 프레임워크의 multipart 버퍼링이 프로세스 임시 폴더에 쓴다(읽기 전용 루트 FS면 업로드 실패). 저장 볼륨은 앱 시작 전에 마운트돼야 한다(시작 시 쓰기 확인).
- 같은 내용의 삭제와 업로드가 교차하면 파일 없는 행이 남을 수 있다(미검증; 순차 재업로드로 복구됨) → 고아·정합성 점검.
- 시간 의존 테스트(`Render_ManyFastBlocks…`)는 약 6배 빠른 코어에서 거짓 실패한다 → 주입 가능한 시계로 교체.
