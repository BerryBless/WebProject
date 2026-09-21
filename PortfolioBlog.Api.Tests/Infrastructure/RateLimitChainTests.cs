using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using PortfolioBlog.Api.Infrastructure.Access;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>속도 제한 체인의 순서와 파티션을 호스트 없이 검증한다(동시 실행 거부를 HTTP로 결정적으로 재현할 수 없어서 체인을 직접 친다).</summary>
public sealed class RateLimitChainTests
{
    private static DefaultHttpContext Request(RateLimitPolicy policy, string ip = "203.0.113.9")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new RateLimitMetadata(policy)), policy.ToString()));
        return ctx;
    }

    /// <summary>동시 실행 거부가 분당 허용량을 쓰지 않는지 증명한다. 창이 먼저인 옛 순서에서는 거부 5회가 창 3을 다 써서 마지막 단언이 실패한다.</summary>
    [Fact]
    public void ConcurrencyRejection_DoesNotConsumeTheMinuteWindow()
    {
        using var chain = RateLimitingExtensions.BuildChain(
            new AdminOptions { PreviewConcurrency = 1, PreviewPerMinute = 3 }, new PublicOptions());

        var held = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
        Assert.True(held.IsAcquired);
        for (var i = 0; i < 5; i++)
        {
            using var rejected = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
            Assert.False(rejected.IsAcquired);
            Assert.Equal(RateLimitingExtensions.ConcurrencyRetryAfterSeconds, RateLimitingExtensions.RetryAfterSeconds(rejected));
        }
        held.Dispose();

        for (var i = 0; i < 2; i++)
        {
            using var ok = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
            Assert.True(ok.IsAcquired, $"창 허용량이 동시 실행 거부에 소모됐다(남은 {2 - i}회째에서 거부).");
        }
        using var overWindow = chain.AttemptAcquire(Request(RateLimitPolicy.Preview));
        Assert.False(overWindow.IsAcquired);
        Assert.InRange(RateLimitingExtensions.RetryAfterSeconds(overWindow), 1, 60);
    }

    /// <summary>창이 거부하면 앞에서 빌린 동시 실행 임대가 반납되는지 증명한다(반납되지 않으면 동시 1이 영구히 막힌다).</summary>
    [Fact]
    public void WindowRejection_ReturnsTheConcurrencyPermit()
    {
        using var chain = RateLimitingExtensions.BuildChain(
            new AdminOptions { UploadConcurrency = 1, UploadPerMinute = 1 }, new PublicOptions());
        chain.AttemptAcquire(Request(RateLimitPolicy.Upload)).Dispose();
        for (var i = 0; i < 3; i++)
        {
            using var rejected = chain.AttemptAcquire(Request(RateLimitPolicy.Upload));
            Assert.False(rejected.IsAcquired);
            Assert.InRange(RateLimitingExtensions.RetryAfterSeconds(rejected), 1, 60); // 매번 "창" 거부여야 한다(동시성 거부면 5)
        }
    }

    /// <summary>검색은 검색 창과 페이지 창에 둘 다 계산되고, 파티션은 IP별이다.</summary>
    [Fact]
    public void Search_CountsTowardThePageWindow_PerIp()
    {
        using var chain = RateLimitingExtensions.BuildChain(new AdminOptions(),
            new PublicOptions { PagePerIpPerMinute = 3, SearchPerIpPerMinute = 100, SearchConcurrency = 8 });
        for (var i = 0; i < 3; i++) Assert.True(Acquire(chain, RateLimitPolicy.Search));
        Assert.False(Acquire(chain, RateLimitPolicy.PublicPage));                          // 같은 IP: 페이지 창 소진
        Assert.True(Acquire(chain, RateLimitPolicy.PublicPage, ip: "198.51.100.7"));       // 다른 IP는 영향 없음
        Assert.True(Acquire(chain, RateLimitPolicy.PublicAsset));                          // 다른 정책은 영향 없음
    }

    /// <summary>검색 창 거부는 페이지 창을 쓰지 않는다(검색 창이 체인에서 앞이다).</summary>
    [Fact]
    public void SearchWindowRejection_DoesNotConsumeThePageWindow()
    {
        using var chain = RateLimitingExtensions.BuildChain(new AdminOptions(),
            new PublicOptions { PagePerIpPerMinute = 3, SearchPerIpPerMinute = 1, SearchConcurrency = 8 });
        Assert.True(Acquire(chain, RateLimitPolicy.Search));                               // 페이지 창 1 사용
        for (var i = 0; i < 5; i++) Assert.False(Acquire(chain, RateLimitPolicy.Search));
        Assert.True(Acquire(chain, RateLimitPolicy.PublicPage));
        Assert.True(Acquire(chain, RateLimitPolicy.PublicPage));
        Assert.False(Acquire(chain, RateLimitPolicy.PublicPage));
    }

    /// <summary>정책 메타데이터가 없는 요청은 어떤 제한기에도 걸리지 않는다.</summary>
    [Fact]
    public void RequestWithoutPolicy_IsNeverLimited()
    {
        using var chain = RateLimitingExtensions.BuildChain(new AdminOptions(), new PublicOptions { PagePerIpPerMinute = 1 });
        for (var i = 0; i < 50; i++)
        {
            using var lease = chain.AttemptAcquire(new DefaultHttpContext());
            Assert.True(lease.IsAcquired);
        }
    }

    private static bool Acquire(PartitionedRateLimiter<HttpContext> chain, RateLimitPolicy policy, string ip = "203.0.113.9")
    {
        using var lease = chain.AttemptAcquire(Request(policy, ip));
        return lease.IsAcquired;
    }
}
