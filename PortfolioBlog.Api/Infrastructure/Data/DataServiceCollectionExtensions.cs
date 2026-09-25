using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>두 DbContext를 등록한다. 연결 문자열은 람다 안에서(= Build 이후 첫 해석 시점에) 읽는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 시작 시 1회 호출되는 등록 확장 메서드이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 등록 자체는 DI 서비스 기술자(descriptor)만 만든다. 실제 컨텍스트·연결 문자열 할당은 각 컨텍스트가 스코프에서 처음 해석될 때 일어난다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 등록 시점에는 DB에 연결하지 않는다.</description></item>
/// </list>
/// </remarks>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// EF 프로바이더(Pomelo)에 알려 주는 MySQL 서버 버전. 운영 이미지 <c>mysql:8.4.11</c>과 같은 고정값이며, 앱과 테스트의 모든 <c>UseMySql</c> 호출이 이 값을 쓴다.
    /// 프로바이더는 이 값으로 SQL 방언(내림차순 인덱스, CHECK, <c>RETURNING</c> 미지원 등)을 고른다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 정적 초기화 때 한 번 만들어지는 불변 객체를 모든 컨텍스트 옵션이 공유한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 프로세스당 1개(타입 초기화 시). 옵션을 만들 때 추가 할당 없음.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음 — <c>ServerVersion.AutoDetect</c>는 옵션을 만들 때 연결을 열어 서버에 묻기 때문에 금지한다
    /// (기동 순서가 DB 가용성에 묶이고, 옵션 빌드마다 왕복이 생기며, 테스트 팩토리가 DB 생성 전에 옵션을 만들면 실패한다).</description></item>
    /// </list>
    /// 서버를 올리면 이 값과 compose·CI 이미지 태그를 함께 바꾼다.
    /// </remarks>
    public static readonly ServerVersion ServerVersion = ServerVersion.Create(new Version(8, 4, 11), ServerType.MySql);

    /// <summary>풀에서 꺼낼 때마다 세션을 리셋하도록 <c>ConnectionReset=true</c>를 강제한 연결 문자열을 만든다.</summary>
    /// <param name="connectionString">설정의 연결 문자열.</param>
    /// <returns>정규화된 연결 문자열(풀 키).</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> 빌더 1개와 결과 문자열 1개. 컨텍스트 옵션을 만들 때만 호출된다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// 리셋이 필요한 이유는 세 가지다. ① 공개 세션 설정(read_only 등)이 같은 풀을 쓰는 관리 연결로 새지 않게 한다(Development는 Public이 없으면 Default를 공유한다).
    /// ② 해제에 실패한 <c>GET_LOCK</c>이 늦어도 같은 물리 연결의 다음 대여 때 풀린다(스파이크 S6b: 반납 직후 관측 0, 재대여 후 1 — 반납 시점 해제는 보장 아님). ③ 공개 세션이 스스로 끈 read_only가 다음 대여로 이어지지 않는다.
    /// 대가는 풀 대여마다 COM_RESET_CONNECTION 왕복 1회다.
    /// </remarks>
    // MySqlConnectionStringBuilder: MySqlConnector는 연결 문자열을 풀 키로 쓰므로, 빌더로 정규화하면 같은 설정은 항상 같은 풀(같은 리셋 동작)로 모인다.
    public static string WithSessionReset(string connectionString) =>
        new MySqlConnectionStringBuilder(connectionString) { ConnectionReset = true }.ConnectionString;

    /// <summary>관리 컨텍스트 <see cref="AppDbContext"/>와 공개 조회 전용 컨텍스트 <see cref="PublicDbContext"/>를 함께 등록한다.</summary>
    /// <param name="services">등록 대상 서비스 컬렉션.</param>
    /// <returns>체이닝을 위해 그대로 돌려주는 <paramref name="services"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 시작 시 1회만 호출되며 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> DI 서비스 기술자 등록만 한다(스코프별 실제 컨텍스트·연결 문자열 할당은 지연된다).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 두 <c>UseMySql</c> 람다는 각 컨텍스트가 스코프에서 처음 해석될 때까지 실행되지 않는다.</description></item>
    /// </list>
    /// 공개 컨텍스트는 <c>ConnectionStrings:Public</c>이 있으면 그 사용자로 접속한다(없으면 관리 연결로 물러난다 — Development·테스트 편의).
    /// 관리 컨텍스트에는 <see cref="ReadCommittedTransactionInterceptor"/>(격리 수준 고정)와 <see cref="PostVersionInterceptor"/>(행 버전)를,
    /// 공개 컨텍스트에는 <see cref="PublicSessionInterceptor"/>(세션 read_only·실행 시간 상한)를 붙인다. 두 연결 문자열 모두 <see cref="WithSessionReset"/>를 거친다.
    /// </remarks>
    public static IServiceCollection AddBlogData(this IServiceCollection services)
    {
        services.AddSingleton<ReadCommittedTransactionInterceptor>();
        services.AddSingleton<PostVersionInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseMySql(WithSessionReset(RequireConnectionString(sp)), ServerVersion)
            .AddInterceptors(sp.GetRequiredService<ReadCommittedTransactionInterceptor>(), sp.GetRequiredService<PostVersionInterceptor>()));
        services.AddDbContext<PublicDbContext>((sp, o) => o
            .UseMySql(WithSessionReset(PublicOrDefaultConnectionString(sp)), ServerVersion)
            .AddInterceptors(new PublicSessionInterceptor(sp.GetRequiredService<IOptions<PublicOptions>>().Value.StatementTimeoutMs))
            // 공개 경로는 추적할 이유가 없다: 변경 추적기 할당을 없애고, 실수로 엔티티를 고쳐도 저장 대상이 되지 않는다.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        return services;
    }

    /// <summary>공개 조회 컨텍스트가 쓸 연결 문자열. <c>ConnectionStrings:Public</c>(읽기 전용 사용자)이 있으면 그것을, 없으면 관리 연결을 쓴다.</summary>
    /// <param name="sp">연결 문자열을 읽을 서비스 프로바이더.</param>
    /// <returns>공개 연결의 연결 문자열(세션 설정은 <see cref="PublicSessionInterceptor"/>가 연결을 열 때마다 건다).</returns>
    /// <exception cref="InvalidOperationException">둘 다 비어 있을 때(<see cref="RequireConnectionString"/>).</exception>
    // 없을 때 관리 연결로 물러나는 것은 Development·테스트 편의다. Development가 아닌 환경에서는 StartupValidation이 Public을 필수로 요구한다.
    private static string PublicOrDefaultConnectionString(IServiceProvider sp)
    {
        var publicConnectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Public");
        return string.IsNullOrWhiteSpace(publicConnectionString) ? RequireConnectionString(sp) : publicConnectionString;
    }

    /// <summary>설정에서 관리 연결 문자열을 읽고, 비어 있으면 명확한 설정 오류로 즉시 실패한다.</summary>
    /// <param name="sp">연결 문자열을 읽을 서비스 프로바이더.</param>
    /// <returns><c>ConnectionStrings:Default</c> 값.</returns>
    /// <exception cref="InvalidOperationException"><c>ConnectionStrings:Default</c>가 없거나 공백뿐이다.</exception>
    // appsettings.json의 기본값이 빈 문자열이라 ??로는 걸러지지 않는다(Plan 1 정오표). 메시지는 ConnectionStringGuardTests가 고정한다.
    private static string RequireConnectionString(IServiceProvider sp)
    {
        var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default");
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("ConnectionStrings:Default 설정이 없습니다.")
            : connectionString;
    }
}
