namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// <c>ConnectionStrings:Default</c>가 비어 있거나 공백뿐일 때 <c>Program.cs</c>가 명확한 설정 오류로 빠르게 실패하는지 검증한다.
/// </summary>
/// <param name="pg">컬렉션이 공유하는 PostgreSQL 컨테이너 fixture. 이 테스트는 연결 문자열을 의도적으로 덮어써 실제 DB에 접속하지 않으므로 컨테이너 자체는 사용하지 않지만, <see cref="ApiFactory"/> 생성에 필요하다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer 기동이
/// 이 테스트 안에서 즉시(생성자 안에서) 실패하도록 설정을 덮어쓴다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트 케이스마다 <see cref="ApiFactory"/>를 직접 생성·<c>using</c>으로 해제하여
/// 클래스 픽스처를 공유하지 않는다(설정 오버라이드가 케이스마다 다르므로).</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로 병렬 실행에 안전하다.</description></item>
/// <item><description><b>Blocking:</b> <c>factory.CreateClient()</c> 호출이 동기적으로 호스트를 기동하며, 구성 오류 시 그 자리에서
/// 예외를 던진다(비동기 I/O 대기 없음).</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class ConnectionStringGuardTests(PostgresContainerFixture pg)
{
    /// <summary>빈 문자열·공백뿐인 연결 문자열이 Npgsql 소켓 오류가 아니라 <c>ConnectionStrings:Default</c>를 언급하는 명확한 설정 오류로 실패하는지 검증한다.</summary>
    /// <param name="value">가드가 거부해야 하는 잘못된 연결 문자열 값(빈 문자열 또는 공백).</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 케이스마다 독립된 <see cref="ApiFactory"/>를 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ApiFactory"/>·설정 딕셔너리 각 1개.</description></item>
    /// <item><description><b>Blocking:</b> <c>CreateClient()</c>는 동기 호출이며 호스트 기동 실패를 그 자리에서 예외로 전파한다.</description></item>
    /// </list>
    /// <c>WebApplicationFactory</c>는 시작 예외를 <see cref="AggregateException"/> 등으로 감쌀 수 있으므로 예외 타입이 아니라
    /// <c>Assert.ThrowsAny&lt;Exception&gt;</c> 후 <c>ex.ToString()</c>에 키 이름이 포함되는지로 검증한다.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrBlankConnectionString_FailsFastWithClearMessage(string value)
    {
        // ConfigureWebHost의 설정 루프가 기본 연결 문자열 설정 이후에 실행되므로 여기서 덮어쓴 값이 최종적으로 적용된다.
        using var factory = new ApiFactory(pg, new Dictionary<string, string?> { ["ConnectionStrings:Default"] = value });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings:Default", ex.ToString(), StringComparison.Ordinal);
    }
}
