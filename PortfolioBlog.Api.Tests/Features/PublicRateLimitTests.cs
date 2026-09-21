using System.Net;
using System.Net.Http.Headers;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>공개·업로드 속도 제한이 실제 파이프라인에 걸려 있는지 검증한다. 테스트마다 격리된 팩토리(자체 제한기 상태)를 만든다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <c>[Collection("postgres")]</c>로 같은 컬렉션의 다른 테스트 클래스와 <see cref="PostgresContainerFixture"/>(컨테이너 자체)를 공유하지만, 테스트마다 새 <see cref="ApiFactory"/>를 만들어 자체 DB(고유 데이터베이스명)와 자체 속도 제한기 상태(체인이 요청 스코프가 아니라 팩토리별로 새로 등록됨)를 가지므로 테스트 간 데이터·한도 간섭이 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 팩토리·HttpClient는 <c>using</c>으로 해제.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 실제 PostgreSQL 컨테이너에 접속한다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PublicRateLimitTests(PostgresContainerFixture pg)
{
    /// <summary>공개 자산 한도는 IP별이다: 같은 IP의 3번째는 429 + Retry-After, 다른 IP는 통과.</summary>
    [Fact]
    public async Task PublicAsset_IsLimitedPerIp()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:AssetPerIpPerMinute"] = "2" });
        using var client = factory.CreatePublicClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var third = await client.GetAsync("/health");
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.InRange(int.Parse(third.Headers.GetValues("Retry-After").Single()), 1, 60);

        using var other = factory.CreatePublicClient();
        other.DefaultRequestHeaders.Remove(RemoteIpStartupFilter.HeaderName);
        other.DefaultRequestHeaders.Add(RemoteIpStartupFilter.HeaderName, "198.51.100.200");
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/health")).StatusCode);
    }

    /// <summary>첨부 GET(없는 id의 404도)이 자산 한도에 계산된다 — 404를 무한히 두드려 DB 조회를 일으킬 수 없다.</summary>
    [Fact]
    public async Task AttachmentGet_CountsTowardTheAssetLimit_EvenWhen404()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:AssetPerIpPerMinute"] = "1" });
        using var client = factory.CreatePublicClient();
        var url = $"/attachments/{Guid.NewGuid()}/x.png";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.GetAsync(url)).StatusCode);
    }

    /// <summary>업로드 분당 한도: 2번째 업로드는 본문 처리 전에 429.</summary>
    [Fact]
    public async Task Upload_IsLimitedPerMinute()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Admin:UploadPerMinute"] = "1" });
        using var client = await factory.CreateLoggedInClientAsync();
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images", "exif-text.png"));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/attachments", Form(bytes))).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await client.PostAsync("/api/attachments", Form(bytes))).StatusCode);
    }

    /// <summary>한도 설정이 0이면 시작이 실패한다.</summary>
    [Fact]
    public void ZeroLimit_FailsStartup()
    {
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["Public:SearchConcurrency"] = "0" });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Public:", ex.ToString(), StringComparison.Ordinal);
    }

    private static MultipartFormDataContent Form(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { part, "file", "a.png" } };
    }
}
