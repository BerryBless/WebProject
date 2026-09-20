using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="AdminOptions"/>의 C# 기본값(설정에서 아무 키도 주어지지 않았을 때 실제로 적용되는 운영 기본값)을 고정한다.
/// 통합 테스트는 전부 <see cref="ApiFactory"/>가 로그인 한도를 크게 덮어써서 돌기 때문에, 출시되는 기본값 자체를 검증하는 테스트가 따로 없었다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 매 테스트가 독립된 <see cref="AdminOptions"/> 인스턴스를 생성하므로 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="AdminOptions"/> 인스턴스 1개.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class AdminOptionsTests
{
    /// <summary>
    /// 설정을 아무것도 주지 않은 <see cref="AdminOptions"/>의 분당 IP별·전역 로그인 한도, 동시 해시 검증 수, 세션 수명이
    /// 문서·스펙이 약속한 운영 기본값(5 / 20 / 2 / 12시간)과 같은지 검증한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="AdminOptions"/> 인스턴스만 사용한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="AdminOptions"/> 인스턴스 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Defaults_MatchShippedProductionValues()
    {
        var options = new AdminOptions();

        Assert.Equal(5, options.LoginPerIpPerMinute);
        Assert.Equal(20, options.LoginGlobalPerMinute);
        Assert.Equal(2, options.LoginConcurrency);
        Assert.Equal(12, options.SessionHours);
    }
}
