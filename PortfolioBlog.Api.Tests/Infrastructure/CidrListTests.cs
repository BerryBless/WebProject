using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// CIDR 목록 파서, 허용 정책 옵션, IP 화이트리스트 접근 제어 정책을 검증한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 각 테스트는 독립된 <see cref="CidrList"/> 또는 <see cref="IpAllowlistAdminAccessPolicy"/> 인스턴스를 생성하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 각 테스트는 <see cref="IPAddress"/>, <see cref="CidrList"/>, <see cref="IpAllowlistAdminAccessPolicy"/>, <see cref="DefaultHttpContext"/> 인스턴스를 필요에 따라 생성한다.</description></item>
/// <item><description><b>Blocking:</b> 모든 테스트는 즉시 반환한다. I/O 또는 비동기 호출이 없다.</description></item>
/// </list>
/// </remarks>
public sealed class CidrListTests
{
    /// <summary>
    /// CIDR 표기법의 IP 범위가 주어진 IP 주소를 포함하는지 정확하게 판정한다.
    /// </summary>
    /// <param name="cidrs">CIDR 표기법 네트워크(예: 192.168.0.0/16, ::1/128)</param>
    /// <param name="ip">검사할 IP 주소</param>
    /// <param name="expected">이 IP가 CIDR 범위에 속할 것으로 예상하는 결과</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Theory의 각 케이스는 독립된 <see cref="CidrList"/> 파싱과 검사를 수행한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="CidrList.Parse"/> 시에만 배열 할당. <see cref="CidrList.Contains"/>는 Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("192.168.0.0/16", "192.168.10.5", true)]
    [InlineData("192.168.0.0/16", "10.0.0.1", false)]
    [InlineData("203.0.113.7/32", "203.0.113.7", true)]
    [InlineData("203.0.113.7/32", "203.0.113.8", false)]
    [InlineData("203.0.113.0/24", "203.0.113.255", true)]   // 경계 주소
    [InlineData("203.0.113.0/24", "203.0.114.0", false)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("2001:db8::/32", "2001:db8:1::5", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    public void Contains_MatchesCidr(string cidrs, string ip, bool expected) =>
        Assert.Equal(expected, CidrList.Parse(cidrs).Contains(IPAddress.Parse(ip)));

    /// <summary>
    /// CIDR 목록을 스페이스, 탭, 개행을 포함한 모든 공백 문자로 분리한다. Caddy remote_ip 매처와 같은 문법을 유지한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 단일 <see cref="CidrList"/> 인스턴스에 대한 분석이므로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="CidrList.Parse"/>에서 배열 할당; 그 이후 읽기만.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Parse_SplitsOnAnyWhitespace_SameSyntaxAsCaddyRemoteIp()
    {
        var list = CidrList.Parse("  203.0.113.0/24 \t 2001:db8::/32\n::1/128 ");
        Assert.Equal(3, list.Count);
        Assert.True(list.Contains(IPAddress.Parse("::1")));
    }

    /// <summary>
    /// Kestrel 듀얼스택 소켓이 IPv4 클라이언트를 IPv4-mapped IPv6 주소(::ffff:a.b.c.d)로 보고할 때 IPv4 CIDR과 매칭된다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 독립된 <see cref="CidrList"/> 인스턴스에 대한 검사.</description></item>
    /// <item><description><b>Memory Allocation:</b> IPv4-mapped 정규화 시 1개 <see cref="IPAddress"/> 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Contains_Ipv4MappedIpv6_MatchesIpv4Cidr() =>
        // Kestrel 듀얼스택 소켓은 IPv4 클라이언트를 ::ffff:a.b.c.d 로 보고한다.
        Assert.True(CidrList.Parse("192.168.0.0/16").Contains(IPAddress.Parse("::ffff:192.168.1.20")));

    /// <summary>
    /// 비어 있거나 공백만 있는 CIDR 문자열은 빈 목록을 생성하며 모든 IP를 거부한다.
    /// </summary>
    /// <param name="raw">null, 빈 문자열, 또는 공백만 있는 문자열</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 각 케이스에서 독립된 <see cref="CidrList"/> 생성.</description></item>
    /// <item><description><b>Memory Allocation:</b> 빈 배열 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_DeniesEveryone(string? raw)
    {
        var list = CidrList.Parse(raw);
        Assert.Equal(0, list.Count);
        Assert.False(list.Contains(IPAddress.Loopback));
    }

    /// <summary>
    /// null IP 주소는 어떤 CIDR 목록에서도 거부된다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 단일 <see cref="CidrList"/> 인스턴스에 대한 null 검사.</description></item>
    /// <item><description><b>Memory Allocation:</b> 0.0.0.0/0 파싱 시 배열 할당, 검사는 Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Contains_Null_IsFalse() => Assert.False(CidrList.Parse("0.0.0.0/0").Contains(null));

    /// <summary>
    /// 잘못된 CIDR 형식의 항목은 <see cref="FormatException"/>을 발생시킨다. 설정 오류가 조용히 숨어서 보안 문제가 되는 것을 방지한다.
    /// </summary>
    /// <param name="raw">CIDR 형식을 위반하는 입력 문자열</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 파싱 실패는 동기로 예외를 던진다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 예외 객체 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(예외 발생).</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("203.0.113.0/24,198.51.100.0/24")] // 쉼표 구분은 허용하지 않는다(문법은 공백 하나로 통일)
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0/24 oops")]
    public void Parse_InvalidEntry_Throws(string raw) =>
        Assert.Throws<FormatException>(() => CidrList.Parse(raw));

    /// <summary>
    /// IP 허용 정책은 <see cref="AdminOptions.AllowedCidrs"/>를 파싱하고 요청의 원본 IP를 검사한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 각 검사마다 독립된 <see cref="HttpContext"/> 인스턴스와 정책 인스턴스를 생성한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 정책 생성 시 옵션 래퍼, <see cref="CidrList"/> 파싱 시 배열 할당. 검사는 Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Policy_UsesConnectionRemoteIp()
    {
        var policy = new IpAllowlistAdminAccessPolicy(Options.Create(new AdminOptions { AllowedCidrs = "203.0.113.0/24" }));
        var allowed = new DefaultHttpContext();
        allowed.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        var denied = new DefaultHttpContext();
        denied.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");

        Assert.True(policy.IsAllowed(allowed));
        Assert.False(policy.IsAllowed(denied));
        Assert.False(policy.IsAllowed(new DefaultHttpContext())); // RemoteIpAddress == null
    }
}
