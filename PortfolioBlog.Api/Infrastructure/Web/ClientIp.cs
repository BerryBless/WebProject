using System.Net;
using System.Net.Sockets;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>속도 제한 파티션 키로 쓸 클라이언트 주소 정규화.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 키 문자열 1개(IPv6는 16바이트 스택 버퍼를 거친다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// 접근 판정(<c>IAdminAccessPolicy</c>)과 로그에는 원래 주소를 그대로 쓴다 — 이 정규화는 파티션 키 전용이다.
/// </remarks>
public static class ClientIp
{
    /// <summary><paramref name="ip"/>를 속도 제한 파티션 키로 정규화한다: IPv4-mapped IPv6는 IPv4 표기로 접고, IPv6는 앞 64비트(라우팅 접두사)만 남겨 <c>/64</c> 표기를 붙인다.</summary>
    /// <param name="ip">정규화할 원본 IP 주소. <c>null</c>이면 빈 문자열을 반환한다.</param>
    /// <returns>정규화된 파티션 키 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수만 읽고 새 문자열을 반환한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> IPv4-mapped 입력은 <see cref="IPAddress.MapToIPv4"/> 호출로 <see cref="IPAddress"/> 1개를 추가 할당한다.
    /// IPv6 경로는 <c>stackalloc</c> 16바이트 버퍼를 써서 힙에 중간 배열을 만들지 않고, 결과 <see cref="IPAddress"/> 1개와 최종 키 문자열 1개만 힙에 남는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// IPv4-mapped 정규화 없이 같은 클라이언트가 듀얼스택 소켓 때문에 <c>a.b.c.d</c>와 <c>::ffff:a.b.c.d</c> 두 표기로 요청을 섞으면 파티션 키가 둘로 나뉘어
    /// IP별 로그인 시도 한도가 사실상 두 배로 늘어난다. IPv6를 <c>/64</c>로 묶는 이유: 한 가입자에게 보통 <c>/64</c> 전체가 할당되므로, 주소 하위 비트만 바꿔 가며
    /// 요청을 보내는 우회를 막으려면 상위 64비트(라우팅 접두사)까지만 키로 써야 한다.
    /// </remarks>
    public static string PartitionKey(IPAddress? ip)
    {
        if (ip is null) return string.Empty;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        // stackalloc: IPv6 주소 16바이트를 스택에 잡아 ArrayPool 대여나 힙 배열 할당 없이 접두사만 잘라낸다(수명이 이 메서드 호출 안에서 끝나는 임시 버퍼이므로 적합).
        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        bytes[8..].Clear(); // 뒤 64비트(인터페이스 ID)를 버린다
        return new IPAddress(bytes).ToString() + "/64";
    }
}
