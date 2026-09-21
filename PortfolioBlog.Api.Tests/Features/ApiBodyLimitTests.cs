using System.Net;
using System.Text;
using PortfolioBlog.Api.Infrastructure.Web;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>관리 JSON 본문 256KB 상한(스펙 3.7). TestServer는 Kestrel의 MaxRequestBodySize를 집행하지 않으므로 앱 미들웨어가 직접 센다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, 대부분의 케이스가 <see cref="ApiFactory.CreateLoggedInClientAsync"/>로 실제 로그인 왕복을 거친 클라이언트를 만든다. <see cref="NoSession_Gets401_NotA413"/>만 로그인하지 않은 <see cref="ApiFactory.CreateAdminClient"/> 클라이언트를 쓴다(세션 부재를 검증하는 케이스라 로그인하면 전제가 깨진다).</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 클래스 픽스처로 1회 생성·공유된다. 300,000자 본문 문자열은 각 케이스가 <see cref="Json"/>으로 새로 만들어 GC 대상이 된다.</description></item>
/// <item><description><b>Concurrency:</b> <c>postgres</c> 컬렉션에 속해 같은 컬렉션의 다른 테스트 클래스와 순차 실행된다. 케이스마다 새 로그인 세션을 쓰므로 서로 간섭하지 않는다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class ApiBodyLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string Json(int markdownChars) => "{\"markdown\":\"" + new string('a', markdownChars) + "\"}";

    /// <summary>Content-Length가 상한을 넘으면 본문을 읽지 않고 413.</summary>
    [Fact]
    public async Task DeclaredLength_OverLimit_Is413()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/preview", new StringContent(Json(300_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>길이를 선언하지 않은(chunked) 본문도 읽는 도중 상한에서 끊긴다 — Content-Length 검사만 있으면 이 테스트가 실패한다.</summary>
    [Fact]
    public async Task UndeclaredLength_OverLimit_Is413()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(Json(300_000))));
        content.Headers.ContentType = new("application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/preview") { Content = content };
        req.Headers.TransferEncodingChunked = true;
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    /// <summary>상한 바로 아래는 통과한다(200KB 본문 검증이 여전히 도달 가능하다): 210,000자는 200KB 검증에 걸려 400, 413이 아니다.</summary>
    [Fact]
    public async Task UnderLimit_ReachesValidation()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/preview", new StringContent(Json(210_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True(Json(210_000).Length < ApiBodyLimitMiddleware.JsonLimitBytes);
    }

    /// <summary>세션이 없으면 큰 본문이어도 401이 먼저다(접근 계약이 본문 크기 검사보다 앞).</summary>
    [Fact]
    public async Task NoSession_Gets401_NotA413()
    {
        using var client = factory.CreateAdminClient();
        using var res = await client.PostAsync("/api/preview", new StringContent(Json(300_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>매칭되는 엔드포인트가 없는 <c>/api</c> 경로는 본문 크기와 무관하게 404다 — 라우팅이 본문을 읽지 않으므로 413이 되어서는 안 된다.</summary>
    [Fact]
    public async Task NoMatchingEndpoint_Gets404_NotA413()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/no-such-endpoint", new StringContent(Json(300_000), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>직렬화 후 정확히 <see cref="ApiBodyLimitMiddleware.JsonLimitBytes"/>바이트인 요청은 413이 아니다(두 크기 검사가 <c>&gt;</c>이지 <c>&gt;=</c>가 아님을 고정한다) — 200KB 마크다운 검증에 걸려 400이 된다.</summary>
    [Fact]
    public async Task ExactlyAtLimit_IsNot413()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var overhead = Json(0).Length; // 마크다운 값이 없을 때의 JSON 틀(접두사·접미사) 바이트 수. 전부 ASCII라 문자 수 = 바이트 수다.
        var body = Json((int)ApiBodyLimitMiddleware.JsonLimitBytes - overhead);
        Assert.Equal(ApiBodyLimitMiddleware.JsonLimitBytes, Encoding.UTF8.GetByteCount(body));

        using var res = await client.PostAsync("/api/preview", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // HttpClient가 길이를 미리 알 수 없게 하는 스트림: CanSeek을 false로 고정해 HttpClient가 Content-Length를 미리 계산하지 못하게 하고
    // Length 접근을 막아 청크 전송 인코딩(Transfer-Encoding: chunked) 경로를 강제로 타게 한다.
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
