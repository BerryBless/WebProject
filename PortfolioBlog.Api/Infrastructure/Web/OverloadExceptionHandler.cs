using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>과부하로 포기한 요청을 500이 아니라 503 + Retry-After로 돌려준다: statement_timeout(57014), 잠금 대기 초과(55P03).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 클래스. <see cref="TryHandleAsync"/>는 요청마다 새로 전달되는 <see cref="HttpContext"/>·<see cref="Exception"/>만 다루므로 여러 요청 스레드에서 동시에 호출돼도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 처리하는 경우 <see cref="ErrorResponses.WriteAsync"/>가 만드는 응답 본문 버퍼 외에 추가 할당이 없다. <see cref="IsOverload"/>는 타입·필드 패턴 매칭만 하여 Zero-allocation이다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. ASP.NET Core의 예외 처리 파이프라인이 등록된 <see cref="IExceptionHandler"/> 목록을 순서대로 <c>await</c>하며 호출한다.</description></item>
/// </list>
/// </remarks>
public sealed class OverloadExceptionHandler : IExceptionHandler
{
    /// <summary>503 응답의 <c>Retry-After</c>(초).</summary>
    public const int RetryAfterSeconds = 5;

    /// <summary>예외가 <see cref="IsOverload"/>로 분류되면 503 + Retry-After를 쓰고 처리했다고 알린다.</summary>
    /// <param name="httpContext">현재 HTTP 요청 컨텍스트.</param>
    /// <param name="exception">파이프라인에서 잡힌 처리되지 않은 예외.</param>
    /// <param name="cancellationToken">요청 취소 토큰.</param>
    /// <returns>이 핸들러가 예외를 처리했으면 <see langword="true"/>, 아니면 다음 <see cref="IExceptionHandler"/>(또는 기본 500 처리)로 넘기도록 <see langword="false"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 인스턴스 필드가 없고 매개변수만으로 동작한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 처리하는 경우 <see cref="int.ToString(IFormatProvider?)"/> 호출로 <c>Retry-After</c> 헤더 문자열 1개와 <see cref="ErrorResponses.WriteAsync"/>의 응답 버퍼를 할당한다. 처리하지 않는 경우(<see cref="IsOverload"/>가 false) 추가 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. 응답 쓰기를 <c>await</c>로 대기한다.</description></item>
    /// </list>
    /// </remarks>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsOverload(exception)) return false;
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ErrorResponses.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable);
        return true;
    }

    // (미검증) 57014는 보통 statement_timeout이지만, 클라이언트 취소와 겹치는 경합에서 다른 경로로도 나올 가능성을 배제하지 않는다 —
    // 그 경우에도 503(Retry-After 포함)은 500보다 안전한 쪽의 오답이라 과부하로 분류해도 위험하지 않다.
    /// <summary>예외 자신 또는 <see cref="Exception.InnerException"/> 체인 어딘가에 통계 시간 초과(57014)·잠금 대기 초과(55P03)를 나타내는 <see cref="PostgresException"/>이 있는지 판정한다.</summary>
    /// <param name="exception">판정할 예외.</param>
    /// <returns>체인 안에 과부하로 분류되는 SqlState의 <see cref="PostgresException"/>이 있으면 <see langword="true"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="Exception.InnerException"/> 체인을 따라가며 타입·필드 패턴 매칭만 수행한다(새 컬렉션·델리게이트 없음).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// EF Core의 <c>SaveChangesAsync</c>는 공급자 예외를 <see cref="DbUpdateException"/>으로 감싸므로(이 저장소 규칙상 감싸이지 않은 채로 올라오는 것은
    /// <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>뿐이다), 최상위 예외 타입만 보면 저장 경로의 57014·55P03이 500이 되어 버린다.
    /// 체인을 끝까지 훑어야 어느 경로에서 와도 동일하게 503으로 매핑된다.
    /// </remarks>
    internal static bool IsOverload(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: "57014" or "55P03" }) return true;
        }
        return false;
    }
}
