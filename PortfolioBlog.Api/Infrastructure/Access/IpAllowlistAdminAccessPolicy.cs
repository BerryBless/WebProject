using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary><c>Admin:AllowedCidrs</c>에 원본 IP가 속할 때만 허용한다. 생성자에서 CIDR을 파싱하므로 설정 오류는 첫 해석 시점에 예외가 된다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 생성자에서 불변 <see cref="CidrList"/>를 캡슐화하므로 Thread-safe. 싱글턴으로 등록되어 모든 요청 스레드에서 안전하게 호출된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 생성자에서 <see cref="CidrList.Parse"/> 호출로 배열 할당. <see cref="IsAllowed"/> 호출은 일반적으로 Zero-allocation. IPv4-mapped는 1개 <see cref="System.Net.IPAddress"/> 할당.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class IpAllowlistAdminAccessPolicy(IOptions<AdminOptions> options) : IAdminAccessPolicy
{
    // CidrList.Parse는 생성자에서 호출되므로 설정 오류는 DI 등록 시점에 예외로 발생한다(런타임 오류가 아님).
    private readonly CidrList _allowed = CidrList.Parse(options.Value.AllowedCidrs);

    /// <summary>요청의 원본 IP가 허용 CIDR 목록에 속하는지 판정한다.</summary>
    /// <param name="context">ForwardedHeaders 미들웨어가 원본 IP를 보정한 뒤의 요청 컨텍스트.</param>
    /// <returns>허용 CIDR 안이면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 요청 파이프라인 스레드에서 호출되지만, <see cref="CidrList"/>는 불변이므로 Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 일반적으로 Zero-allocation. IPv4-mapped IPv6는 1개 <see cref="System.Net.IPAddress"/> 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public bool IsAllowed(HttpContext context) => _allowed.Contains(context.Connection.RemoteIpAddress);
}
