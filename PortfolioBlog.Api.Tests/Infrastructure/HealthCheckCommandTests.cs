using System.Net;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>컨테이너 헬스체크 CLI의 계약: 어디로, 어떤 Host 헤더로 부르고, 무엇을 종료 코드 0으로 치는가.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 테스트마다 자기 핸들러·버퍼를 만든다. 공유 상태 없음 — 병렬 실행 안전.</description></item>
/// <item><description><b>Memory Allocation:</b> 테스트당 가짜 핸들러 1개와 <see cref="StringWriter"/> 1개.</description></item>
/// <item><description><b>Blocking:</b> 네트워크·DB를 쓰지 않는다(전송 계층을 가짜로 바꾼다).</description></item>
/// </list>
/// </remarks>
public sealed class HealthCheckCommandTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool hangForever = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            if (hangForever)
            {
                // 취소 토큰을 존중하며 무한 대기한다. Task.FromResult로 즉시 반환하는 기본 경로로는 HttpClient.Timeout이
                // 취소 토큰으로만 동작한다는 사실 때문에 시간 초과 자체를 재현할 수 없다.
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return respond(request);
        }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs) =>
        key => pairs.FirstOrDefault(p => p.Key == key).Value;

    /// <summary>요청은 루프백의 앱 포트로 가고 Host 헤더는 공개 호스트 이름이다 — 호스트 필터가 <c>localhost</c>를 400으로 거부하기 때문이다(스펙 3.10).</summary>
    [Fact]
    public async Task Healthy_ReturnsZero_AndCallsLoopbackWithThePublicHostHeader()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var error = new StringWriter();

        var code = await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), handler, error);

        Assert.Equal(0, code);
        Assert.Equal(new Uri("http://127.0.0.1:8080/health"), handler.Seen!.RequestUri);
        Assert.Equal("blog.example.test", handler.Seen.Headers.Host);
        Assert.Equal(string.Empty, error.ToString());
    }

    /// <summary><c>ASPNETCORE_HTTP_PORTS</c>가 여러 개면 첫 번째를 쓴다.</summary>
    [Fact]
    public async Task UsesTheFirstConfiguredHttpPort()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test"), ("ASPNETCORE_HTTP_PORTS", "9090;9091")), handler, new StringWriter());
        Assert.Equal(9090, handler.Seen!.RequestUri!.Port);
    }

    /// <summary>2xx가 아니면 1이다. 400은 Host 헤더가 틀렸을 때, 503은 과부하일 때 실제로 오는 값이다.</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task NonSuccessStatus_ReturnsOne(HttpStatusCode status)
    {
        var error = new StringWriter();
        var code = await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), new StubHandler(_ => new HttpResponseMessage(status)), error);
        Assert.Equal(1, code);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>연결 실패(앱이 아직 안 떴다)는 예외가 아니라 종료 코드 1이다 — Docker는 종료 코드만 본다.</summary>
    [Fact]
    public async Task ConnectionFailure_ReturnsOne_WithoutThrowing()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), handler, new StringWriter()));
    }

    /// <summary>서버가 <see cref="HealthCheckCommand.TimeoutSeconds"/> 안에 응답하지 않으면 예외 없이 종료 코드 1이다.
    /// 기존 스텁(즉시 반환)으로는 <c>HttpClient.Timeout</c>이 취소 토큰으로만 작동한다는 점 때문에 이 경로가 전혀 실행되지 않았다 —
    /// 무한 대기하는 스텁으로 실제 시간 초과를 재현한다(경과 시간은 느린 CI를 고려해 단언하지 않는다).</summary>
    [Fact]
    public async Task Timeout_ReturnsOne_WithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK), hangForever: true);
        var error = new StringWriter();
        Assert.Equal(1, await HealthCheckCommand.RunAsync(Env(("Site__PublicOrigin", "https://blog.example.test")), handler, error));
    }

    /// <summary>공개 origin 설정이 없거나 절대 URI가 아니거나, 절대 URI라도 스킴이 http/https가 아니어서 Host가 비게 되면 요청을 보내지 않고 1이다.
    /// <c>urn:example:blog</c>는 절대 URI이지만 호스트가 없어, 스킴·호스트를 함께 확인하지 않으면 빈 Host 헤더로 요청이 나가 버린다.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("blog.example.test")]
    [InlineData("urn:example:blog")]
    public async Task MissingOrRelativeOrigin_ReturnsOne_WithoutSending(string? origin)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var env = origin is null ? Env() : Env(("Site__PublicOrigin", origin));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(env, handler, new StringWriter()));
        Assert.Null(handler.Seen);
    }
}
