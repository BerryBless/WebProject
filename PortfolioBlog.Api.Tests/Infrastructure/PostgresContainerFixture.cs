using Testcontainers.PostgreSql;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 컬렉션 전체가 공유하는 PostgreSQL 컨테이너.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 시작 후 읽기 전용(연결 문자열)만 노출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 컨테이너 핸들 1개. 컬렉션 종료 시 Dispose로 컨테이너 제거.</description></item>
/// <item><description><b>Blocking:</b> InitializeAsync는 이미지 pull·기동을 비동기 대기(최초 수십 초).</description></item>
/// </list>
/// </remarks>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    // PostgreSqlContainer: Docker API로 컨테이너를 기동하고 컨테이너 안에서 pg_isready를 반복 실행하는 대기 전략으로
    // 준비 완료를 판정하므로 sleep 기반 폴링 없이 연결 가능한 시점을 정확히 얻는다.
    // 4.15.0에서 매개변수 없는 PostgreSqlBuilder()는 obsolete이므로 이미지를 생성자 인수로 준다(경고 0 유지).
    // max_connections=300: 테스트는 팩토리마다 DB를 새로 만들고 Npgsql은 연결 문자열(=DB)마다 풀을 따로 두므로, 팩토리 수십 개가
    // 남긴 유휴 연결이 기본 상한 100(예약 3 제외 97)에 닿으면 "too many clients"로 연결 열기가 간헐 실패한다(실측: 실행 중 최대 61개,
    // 412개 테스트 시점에 RelationalConnection.Open 간헐 실패 3회 관찰). 상한을 올리고, 팩토리 해제 시 풀도 비운다(ApiFactory.Dispose).
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    /// <summary>기동된 컨테이너의 Npgsql 연결 문자열.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>컨테이너 이미지를 내려받고 기동하여 <c>pg_isready</c>가 성공할 때까지 대기한다.</summary>
    /// <returns>컨테이너가 연결 가능한 상태가 되면 완료되는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> xUnit이 컬렉션당 1회만 호출하므로 동시 호출을 가정하지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Docker 클라이언트·컨테이너 핸들에 대한 관리형 할당만 발생한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking 대기. 최초 이미지 pull 시 수십 초가 걸릴 수 있다.</description></item>
    /// </list>
    /// </remarks>
    public Task InitializeAsync() => _container.StartAsync();

    /// <summary>컨테이너를 정지하고 제거한다.</summary>
    /// <returns>정리가 끝나면 완료되는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> xUnit이 컬렉션 종료 시 1회만 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 컨테이너 핸들을 해제하고 추가 할당은 없다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking 대기. Docker 데몬에 정지·제거 요청 후 응답을 기다린다.</description></item>
    /// </list>
    /// </remarks>
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>fixture <see cref="PostgresContainerFixture"/>를 <c>"postgres"</c> 컬렉션 이름에 등록해 컬렉션 내 테스트 클래스가 컨테이너를 공유하게 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> xUnit 내부 인프라가 생성·소유하며 사용자 코드는 인스턴스를 직접 다루지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 마커 클래스이며 별도 상태가 없다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음(인스턴스화되지 않는 컬렉션 정의 전용 타입).</description></item>
/// </list>
/// </remarks>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
