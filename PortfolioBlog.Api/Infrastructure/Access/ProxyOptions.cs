namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>Proxy</c>. 관리자 허용 CIDR과는 목적이 다른 별도 설정이다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 프로퍼티는 public set을 가지지만, 싱글턴으로 등록되어 프로그램 시작 후 수정되지 않는다. 읽기만 Thread-safe.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로퍼티는 문자열 참조만 저장.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class ProxyOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Proxy";

    /// <summary>X-Forwarded-For를 믿을 단 하나의 프록시(Caddy 컨테이너 고정 IP). 비어 있으면 헤더를 전혀 믿지 않는다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 문자열은 불변이므로 Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 할당 없음. 참조만 반환.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public string TrustedIp { get; set; } = string.Empty;
}
