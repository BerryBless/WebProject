using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Features;
using PortfolioBlog.Api.Features.Attachments;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Infrastructure.Markdown;
using PortfolioBlog.Api.Infrastructure.Storage;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Pages;

// CLI 경로: 웹 호스트를 만들지 않고 해시만 출력하고 끝낸다.
if (args is [HashPasswordCommand.Name])
{
    return HashPasswordCommand.Run(Console.In, Console.Out, Console.Error, interactive: !Console.IsInputRedirected);
}

var builder = WebApplication.CreateBuilder(args);

// Kestrel: 서버 제품명 헤더를 내지 않는다.
builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);
// 호스트 필터: 설정 파일의 AllowedHosts("*") 대신 설정된 두 origin의 호스트만 받는다(지연 바인딩 — Build 이후 첫 해석).
// 프레임워크의 기본 PostConfigure는 목록이 비어 있을 때만 "*"로 채우므로 이 값이 이긴다(실측).
builder.Services.AddOptions<HostFilteringOptions>().Configure<IOptions<SiteOptions>>((o, site) =>
{
    o.AllowedHosts = new[] { SiteOptions.HostOf(site.Value.PublicOrigin), SiteOptions.HostOf(site.Value.AdminOrigin) }
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    o.AllowEmptyHosts = false;
    // 기본값 true면 이 400에 334바이트짜리 프레임워크 HTML 본문이 실린다(실측). 그 응답은 앱 미들웨어보다 바깥에서 만들어져
    // CSP·nosniff가 붙지 않으므로, 본문을 아예 없애 "본문 있는 응답에는 보안 헤더가 있다"를 예외 없이 유지한다.
    o.IncludeFailureMessage = false;
});
builder.Services.AddExceptionHandler<OverloadExceptionHandler>();

builder.Services.AddOpenApi();
// ProblemDetails: 400/401/403/404/409/429 등 모든 오류 응답을 RFC 9457 형식으로 통일한다.
builder.Services.AddProblemDetails();
// 바인딩 실패(JSON 파싱 오류·잘못된 쿼리 값)를 예외(Development 기본값)가 아니라 항상 400으로 응답한다.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
builder.Services.AddAdminAccess(builder.Configuration);
builder.Services.AddAdminAuth();
builder.Services.Configure<PublicOptions>(builder.Configuration.GetSection(PublicOptions.SectionName));
// 관리(AppDbContext) + 공개 조회 전용(PublicDbContext) 두 컨텍스트를 등록한다. PublicOptions 등록보다 뒤에 있어도
// 문제 없다 — AddBlogData 내부 람다는 각 컨텍스트가 스코프에서 처음 해석될 때 실행되므로 지연 바인딩이다.
builder.Services.AddBlogData();
builder.Services.AddAppRateLimiting();
// 시스템 시계를 명시한다: 통합 테스트는 DI의 TimeProvider를 세션 만료용 가짜 시계로 바꾸는데, 렌더 시간 예산은 그 영향을 받으면 안 된다.
builder.Services.AddSingleton(_ => new MarkdownRenderer(TimeProvider.System));
builder.Services.Configure<RenderingOptions>(builder.Configuration.GetSection(RenderingOptions.SectionName));
builder.Services.AddSingleton<RenderGate>();
builder.Services.AddSingleton<RenderedPostCache>();
builder.Services.Configure<AttachmentOptions>(builder.Configuration.GetSection(AttachmentOptions.SectionName));
builder.Services.AddSingleton<FileSystemAttachmentStore>();
// multipart 한도를 첨부 한도보다 1MB 크게: 10MB를 조금 넘는 업로드는 앱이 413으로 답하고, 그보다 훨씬 큰 본문은 프레임워크가 읽다가 끊는다.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = AttachmentOptions.MaxBytes + 1_048_576);
builder.Services.AddSingleton<AttachmentJanitor>();
builder.Services.AddHostedService(static sp => sp.GetRequiredService<AttachmentJanitor>());
builder.Services.AddRazorPages();
// 규약은 설정(공개 호스트)이 필요하므로 옵션 지연 구성으로 단다(Build 이후 첫 해석).
builder.Services.AddOptions<RazorPagesOptions>().Configure<IOptions<SiteOptions>>((o, site) =>
    o.Conventions.Add(new PublicPageConvention(SiteOptions.HostOf(site.Value.PublicOrigin))));

var app = builder.Build();

// 설정 오류가 DB 접속 오류에 가려지지 않도록 마이그레이션보다 먼저 검증한다.
StartupValidation.Validate(app.Services, app.Environment);
// 첨부 저장 루트가 실제로 쓸 수 있는지 시작 시점에 확인한다(첫 업로드가 아니라). StartupValidation은 I/O가 없다는 계약을 지키므로
// 이 파일 시스템 검사는 별도 단계로 둔다.
app.Services.GetRequiredService<FileSystemAttachmentStore>().EnsureRootIsWritable();

// 단일 인스턴스 배포이므로 시작 시 마이그레이션을 적용한다(스펙 3.10).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// 워밍업: 첫 렌더에는 ColorCode 등의 정적 초기화(실측 약 185ms — 2A 단계 실측, plan/resume_guide_0921.md)가 붙는다. 첫 방문자가 아니라 시작 시점에 낸다.
app.Services.GetRequiredService<MarkdownRenderer>().Render("```csharp\nvar warm = 1;\n```\n");

// 앱 미들웨어 중 맨 앞. 프레임워크의 HostFiltering 시작 필터만 이보다 바깥이라 그 400에는 이 헤더들이 붙지 않는다
// (실측: HostFilteringTests.RejectedHost_400_HasNoBody_AndNoSecurityHeaders) — 그래서 위에서 IncludeFailureMessage = false로
// 그 400의 본문을 없앴다(헤더 없는 응답에 본문도 없다). Kestrel이 앱에 닿기 전에 스스로 거부하는 요청(요청 줄 길이 초과 등)도
// 같은 이유로 이 헤더들이 붙지 않는다 — 그쪽은 스펙 3.6 참조(이 저장소에서 측정하지는 않았다).
// 바로 다음 줄의 UseTrustedForwardedHeaders는 일반 앱 미들웨어라 이 줄 뒤에서 실행되고 자체적으로 400을 내지 않는다.
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseTrustedForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages(ErrorResponses.HandleStatusCodeAsync);
// 정적 파일은 wwwroot/css/site.css 하나뿐이다. 엔드포인트가 이미 매칭된 요청은 이 미들웨어가 건드리지 않는다.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = static ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600" });
app.UseMiddleware<AdminSurfaceMiddleware>();
app.UseRateLimiter();      // IP 검사 뒤: 외부 요청이 로그인 한도를 소진하지 못한다
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiBodyLimitMiddleware>(); // 인가 뒤: 세션 없는 요청은 크기와 무관하게 401이 먼저다

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", static () =>
    // DateTimeOffset.UtcNow: 로컬 타임존 변환(tzdata/레지스트리 조회)을 거치지 않고 시스템 UTC 틱을
    // 그대로 읽으므로 오프셋 0 이 보장되고 Now 보다 호출 비용이 낮다.
    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
    .WithName("GetHealth")
    .WithMetadata(new RateLimitMetadata(RateLimitPolicy.PublicAsset));
app.MapApiEndpoints();
app.MapPublicAttachmentEndpoints();
app.MapRazorPages();
app.MapPublicSiteEndpoints();

app.Run();
return 0;

/// <summary>서비스 생존 여부를 알리는 <c>/health</c> 응답 모델.</summary>
/// <param name="Status">서비스 상태. 현 단계에서는 외부 의존성 점검 없이 상수 <c>"Healthy"</c> 를 반환한다.</param>
/// <param name="GeneratedAt">서버가 응답을 생성한 UTC 시각(오프셋 0).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record 이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 요청당 record 인스턴스 1개 힙 할당. <paramref name="Status"/> 는 상수 문자열 인터닝으로 추가 할당 없음.
/// JSON 직렬화·응답 전송 버퍼는 프레임워크 소관이다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
/// </list>
/// </remarks>
public record HealthResponse(string Status, DateTimeOffset GeneratedAt);

// WebApplicationFactory<Program> 기반 통합 테스트가 진입점 타입에 접근할 수 있도록 공개한다.
public partial class Program { }
