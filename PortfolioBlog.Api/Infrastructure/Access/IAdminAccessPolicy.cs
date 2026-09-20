using Microsoft.AspNetCore.Http;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>현재 요청의 원본 IP가 관리 표면에 접근할 수 있는지 판정한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 요청 파이프라인 스레드에서 미들웨어가 호출한다. 요청 본문을 읽기 전에 실행된다.</description></item>
/// <item><description><b>Memory Policy:</b> 구현은 판정 경로에서 힙 할당을 하지 않아야 한다(IPv4-mapped 정규화 1건 제외).</description></item>
/// <item><description><b>Concurrency:</b> 구현은 Thread-safe(불변)여야 한다. 싱글턴으로 등록된다. 즉시 반환(Non-blocking), I/O 금지.</description></item>
/// </list>
/// </remarks>
public interface IAdminAccessPolicy
{
    /// <summary>요청의 원본 IP가 관리 표면에 접근할 수 있는지 판정한다.</summary>
    /// <param name="context">ForwardedHeaders 미들웨어가 원본 IP를 보정한 뒤의 요청 컨텍스트.</param>
    /// <returns>허용 CIDR 안이면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 구현은 Thread-safe해야 하며, 싱글턴으로 등록되어 모든 요청 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 일반적으로 Zero-allocation. IPv4-mapped IPv6는 1개 <see cref="System.Net.IPAddress"/> 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    bool IsAllowed(HttpContext context);
}
