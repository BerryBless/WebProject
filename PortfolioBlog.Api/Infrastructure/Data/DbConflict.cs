using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>"검증 → 저장" 사이의 경쟁으로 DB 제약에 걸렸는지 판정한다: 유니크(23505, 같은 slug 동시 생성)와 FK(23503, 검증 직후 시리즈가 삭제됨).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Problem"/> 호출 시 <see cref="ProblemHttpResult"/> 1개를 할당한다. <see cref="IsConstraintRace"/>는 추가 할당이 없다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// 단일 작성자라도 탭 두 개로 발생한다. 서버가 재시도하지 않는 이유: 실패한 SaveChanges 뒤 변경 추적기 정리가 복잡하고 클라이언트 재시도가 더 투명하다.
/// </remarks>
public static class DbConflict
{
    /// <summary>PostgreSQL 유니크 제약 위반 SQLSTATE.</summary>
    public const string UniqueViolation = "23505";

    /// <summary>PostgreSQL 외래키 제약 위반 SQLSTATE.</summary>
    public const string ForeignKeyViolation = "23503";

    /// <summary><paramref name="ex"/>가 유니크 또는 외래키 위반으로 인한 저장 실패인지 판정한다.</summary>
    /// <param name="ex"><c>SaveChangesAsync</c>가 던진 예외.</param>
    /// <returns>검증 뒤 발생한 동시성 경쟁으로 볼 수 있으면 <c>true</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 전달받은 예외 인스턴스만 읽는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. 패턴 매칭만 수행한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static bool IsConstraintRace(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation or ForeignKeyViolation };

    /// <summary>409 Conflict <see cref="ProblemDetails"/> 응답을 만든다.</summary>
    /// <param name="detail">사용자에게 보여줄 상세 설명.</param>
    /// <returns>상태 코드 409로 설정된 <see cref="ProblemHttpResult"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 매개변수만으로 새 결과 객체를 만든다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="ProblemHttpResult"/> 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
    /// </list>
    /// </remarks>
    public static ProblemHttpResult Problem(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "충돌", detail: detail);
}
