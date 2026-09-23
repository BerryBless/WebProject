using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    /// <summary>관리 컨텍스트 <see cref="AppDbContext"/>와 공개 조회 전용 컨텍스트 <see cref="PublicDbContext"/>를 함께 등록한다.</summary>
    /// <param name="services">등록 대상 서비스 컬렉션.</param>
    /// <returns>체이닝을 위해 그대로 돌려주는 <paramref name="services"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 시작 시 1회만 호출되며 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> DI 서비스 기술자 등록만 한다(스코프별 실제 컨텍스트·연결 문자열 할당은 지연된다).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 두 <c>UseNpgsql</c> 람다는 각 컨텍스트가 스코프에서 처음 해석될 때까지 실행되지 않는다.</description></item>
    /// </list>
    /// 공개 컨텍스트는 <c>ConnectionStrings:Public</c>이 있으면 그 롤로 접속한다(없으면 관리 연결로 물러난다 — Development·테스트 편의).
    /// </remarks>
    public static IServiceCollection AddBlogData(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>((sp, o) => o.UseNpgsql(RequireConnectionString(sp)));
        services.AddDbContext<PublicDbContext>((sp, o) => o
            .UseNpgsql(PublicDbContext.BuildConnectionString(PublicOrDefaultConnectionString(sp), sp.GetRequiredService<IOptions<PublicOptions>>().Value.StatementTimeoutMs))
            // 공개 경로는 추적할 이유가 없다: 변경 추적기 할당을 없애고, 실수로 엔티티를 고쳐도 저장 대상이 되지 않는다.
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        return services;
    }

    /// <summary>공개 조회 컨텍스트가 쓸 연결 문자열. <c>ConnectionStrings:Public</c>(읽기 전용 롤)이 있으면 그것을, 없으면 관리 연결을 쓴다.</summary>
    /// <param name="sp">연결 문자열을 읽을 서비스 프로바이더.</param>
    /// <returns>공개 연결의 바탕이 되는 연결 문자열(시작 옵션은 <see cref="PublicDbContext.BuildConnectionString"/>이 붙인다).</returns>
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
