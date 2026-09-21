using System.Net;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>속도 제한 파티션 키 정규화 단위 테스트. 같은 클라이언트가 표기만 바꿔 예산을 여러 개 얻지 못해야 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 각 테스트는 정적 무상태 함수만 호출하므로 다른 테스트와 공유하는 가변 상태가 없다. 외부 자원(DB·네트워크) 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> 각 테스트는 <see cref="IPAddress"/> 파싱 결과와 반환 키 문자열만 보유한다.</description></item>
/// <item><description><b>Blocking:</b> 모든 테스트는 동기 즉시 반환. I/O·비동기 대기 없음.</description></item>
/// </list>
/// </remarks>
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
