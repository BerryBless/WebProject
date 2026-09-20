using System.Net;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>공백으로 구분한 CIDR 목록. Caddy <c>remote_ip</c> 매처와 같은 문법이라 환경변수 하나를 두 계층이 공유할 수 있다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성 후 불변 배열만 읽는다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Contains"/>는 Zero-allocation. 단 IPv4-mapped IPv6 입력은 <c>MapToIPv4()</c>로 IPAddress 1개를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// 잘못된 항목을 조용히 무시하지 않는다: 하나라도 파싱에 실패하면 <see cref="FormatException"/>으로 시작을 막는다(설정 오타가 "아무도 못 들어옴" 또는 "의도보다 넓게 열림"으로 숨는 것을 방지).
/// </remarks>
public sealed class CidrList
{
    // System.Net.IPNetwork: readonly struct라 배열에 인라인 저장되어 캐시 지역성이 좋고, Contains()는 프리픽스 비트 비교만 수행한다.
    private readonly IPNetwork[] _networks;

    private CidrList(IPNetwork[] networks) => _networks = networks;

    /// <summary>CIDR 목록의 항목 수.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 배열의 길이를 읽을 뿐이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public int Count => _networks.Length;

    /// <summary>공백(스페이스, 탭, 개행)으로 구분한 CIDR 문자열을 파싱한다.</summary>
    /// <param name="spaceSeparated">CIDR 목록(예: "192.168.0.0/16 2001:db8::/32"). null이거나 공백만이면 빈 목록을 반환한다.</param>
    /// <returns>파싱된 CIDR 목록.</returns>
    /// <exception cref="FormatException">하나라도 CIDR 형식이 잘못되었을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 반환된 <see cref="CidrList"/>는 불변 배열을 캡슐화하므로 Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 배열 할당 및 <c>IPNetwork.TryParse</c> 내부 할당 발생.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환 또는 <see cref="FormatException"/> 던짐.</description></item>
    /// </list>
    /// </remarks>
    public static CidrList Parse(string? spaceSeparated)
    {
        if (string.IsNullOrWhiteSpace(spaceSeparated))
        {
            return new CidrList([]);
        }
        // separator null = 모든 공백 문자(스페이스·탭·개행)로 분리.
        var parts = spaceSeparated.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var networks = new IPNetwork[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!IPNetwork.TryParse(parts[i], out networks[i]))
            {
                throw new FormatException($"CIDR 형식이 아닙니다: '{parts[i]}'. 공백으로 구분한 CIDR만 허용합니다(예: \"203.0.113.0/24 2001:db8::/32\").");
            }
        }
        return new CidrList(networks);
    }

    /// <summary>주어진 IP 주소가 이 CIDR 목록에 포함되는지 판정한다.</summary>
    /// <param name="ip">검사할 IP 주소. null이면 false를 반환한다.</param>
    /// <returns>IP가 목록의 어떤 CIDR 범위에도 속하면 true.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 읽기만 수행한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 일반적으로 Zero-allocation. IPv4-mapped IPv6(예: ::ffff:192.168.1.1)는 <c>MapToIPv4()</c>로 1개 <see cref="IPAddress"/> 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public bool Contains(IPAddress? ip)
    {
        if (ip is null)
        {
            return false;
        }
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        foreach (var network in _networks)
        {
            if (network.Contains(ip))
            {
                return true;
            }
        }
        return false;
    }
}
