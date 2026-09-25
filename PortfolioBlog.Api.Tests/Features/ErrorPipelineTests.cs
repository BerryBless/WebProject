using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>보안 헤더·과부하 매핑·오류 본문을 실제 미들웨어 조합으로 검증한다. DB·Docker가 필요 없다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 각 테스트가 <see cref="StartAsync"/>로 자기 전용의 최소 <see cref="WebApplication"/>을 새로 띄우므로 <see cref="ApiFactory"/> 같은 공유 픽스처와 무관하다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트마다 독립된 <see cref="WebApplication"/>·TestServer 인스턴스를 새로 만들고 <c>await using</c>으로 해제한다. 다른 테스트 클래스와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Concurrency:</b> DB·Docker·<c>MySqlContainerFixture</c>에 의존하지 않으므로 다른 컬렉션과 병렬로 실행할 수 있다.</description></item>
/// </list>
/// </remarks>
public sealed class ErrorPipelineTests
{
    /// <summary><see cref="SecurityHeadersMiddleware"/>·<see cref="OverloadExceptionHandler"/>·<see cref="ErrorResponses"/>만 얹은 최소 파이프라인을 구성하고 <paramref name="terminal"/>을 종단 델리게이트로 실행한다.</summary>
    /// <param name="terminal">라우팅 없이 파이프라인 맨 끝에서 직접 실행할 델리게이트(예외를 던지거나 상태 코드만 설정).</param>
    /// <returns>기동이 끝난 <see cref="WebApplication"/>. 호출자가 <c>await using</c>으로 해제해야 한다.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 <see cref="WebApplication"/>을 만들어 반환하므로 다른 호출과 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="WebApplication"/>·DI 컨테이너·TestServer 각 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <see cref="WebApplication.StartAsync"/>를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<WebApplication> StartAsync(RequestDelegate terminal)
    {
        // EnvironmentName 고정: 지정하지 않으면 호스트 프로세스의 ASPNETCORE_ENVIRONMENT를 그대로 물려받는다.
        // 이 미니 파이프라인은 UseDeveloperExceptionPage를 등록하지 않으므로 ASPNETCORE_ENVIRONMENT=Development로
        // 재실행해도 이 클래스의 테스트는 재현되지 않았지만(실측), 환경 변수 값에 따라 결과가 갈릴 수 있는 요인을
        // 아예 없애 이 클래스가 검증하려는 것과 무관한 흔들림을 방지한다.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<OverloadExceptionHandler>();
        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(ErrorResponses.HandleStatusCodeAsync);
        app.Run(terminal);
        await app.StartAsync();
        return app;
    }

    /// <summary>처리되지 않은 예외의 500에도 보안 헤더가 남는다(직접 헤더 설정이면 Response.Clear()에 지워져 실패한다).</summary>
    [Fact]
    public async Task UnhandledException_500_StillCarriesSecurityHeaders()
    {
        await using var app = await StartAsync(_ => throw new InvalidOperationException("boom"));
        using var res = await app.GetTestClient().GetAsync("/api/x");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(SecurityHeadersMiddleware.PublicCsp, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("boom", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>본문 없는 404: 공개 경로는 고정 HTML, /api는 ProblemDetails. 어느 쪽도 요청 경로를 본문에 반사하지 않는다.</summary>
    [Theory]
    [InlineData("/posts/%3Cscript%3Ealert(1)%3C/script%3E", "text/html")]
    [InlineData("/api/%3Cscript%3E", "application/problem+json")]
    public async Task EmptyStatus_GetsBodyByPath_WithoutReflectingInput(string path, string mediaType)
    {
        await using var app = await StartAsync(ctx => { ctx.Response.StatusCode = 404; return Task.CompletedTask; });
        using var res = await app.GetTestClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(mediaType, res.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("script", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>렌더 게이트가 가득 차 포기한 요청도 503 + Retry-After다.</summary>
    [Fact]
    public async Task RenderBusy_MapsTo503()
    {
        await using var app = await StartAsync(_ => throw new PortfolioBlog.Api.Infrastructure.Markdown.RenderBusyException());
        using var res = await app.GetTestClient().GetAsync("/api/preview");
        Assert.Equal(503, (int)res.StatusCode);
        Assert.True(res.Headers.Contains("Retry-After"));
    }

    /// <summary>다른 코드가 CSP를 먼저 넣어도(fail-open 방지) 더 엄격한 PublicCsp로 덮어쓴다. 첨부 핸들러의 SandboxCsp만 예외로 유지된다.</summary>
    [Theory]
    [InlineData("default-src *", SecurityHeadersMiddleware.PublicCsp)]
    [InlineData(SecurityHeadersMiddleware.SandboxCsp, SecurityHeadersMiddleware.SandboxCsp)]
    public async Task ExistingCsp_OnlySandboxCspSurvives_OthersAreOverwritten(string preset, string expected)
    {
        await using var app = await StartAsync(ctx => { ctx.Response.Headers.ContentSecurityPolicy = preset; return Task.CompletedTask; });
        using var res = await app.GetTestClient().GetAsync("/api/x");
        Assert.Equal(expected, res.Headers.GetValues("Content-Security-Policy").Single());
    }
}
