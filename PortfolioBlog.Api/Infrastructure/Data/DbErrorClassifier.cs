using MySqlConnector;

namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>호출부가 DB 오류 번호 대신 쓰는 의미 분류.</summary>
public enum DbErrorKind
{
    /// <summary>아래 어느 것에도 해당하지 않는다(500으로 남는다).</summary>
    Other,
    /// <summary>유니크 인덱스 위반(1062). 409.</summary>
    UniqueViolation,
    /// <summary>외래 키 위반(1451 부모 삭제, 1452 자식 삽입). 409.</summary>
    ForeignKeyViolation,
    /// <summary>CHECK 제약 위반(3819). 앱 검증 누락이므로 500.</summary>
    CheckViolation,
    /// <summary>실행 시간 상한 초과(3024, max_execution_time). 공개 세션의 메타데이터 잠금(MDL) 대기도 이 번호로 끝난다(스파이크 S3c). 503.</summary>
    QueryTimeout,
    /// <summary>잠금 대기 상한 초과(1205, 또는 앱의 <see cref="DbLockTimeoutException"/>). 503.</summary>
    LockTimeout,
    /// <summary>교착으로 인한 롤백(1213). 503.</summary>
    Deadlock,
    /// <summary>권한 거부(1142·1143·1044·1227). 공개 경로가 허용 밖 테이블에 닿았다는 뜻이므로 500.</summary>
    PermissionDenied,
    /// <summary>읽기 전용 트랜잭션에서의 쓰기(1792). 500.</summary>
    ReadOnly,
}

/// <summary>MySQL 오류 번호(<see cref="MySqlException.Number"/>, int)를 한곳에서 <see cref="DbErrorKind"/>로 바꾼다. 번호는 스펙 2.4절(스파이크 실측)과 같아야 한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(예외 체인을 읽기만 한다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class DbErrorClassifier
{
    /// <summary>오류 번호 하나를 분류한다.</summary>
    /// <param name="number"><see cref="MySqlException.Number"/> 값(int). 같은 예외의 <c>ErrorCode</c>는 <c>MySqlErrorCode</c> 열거형이라 쓰지 않는다.</param>
    /// <returns>분류. 모르는 번호는 <see cref="DbErrorKind.Other"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static DbErrorKind KindOf(int number) => number switch
    {
        1062 => DbErrorKind.UniqueViolation,
        1451 or 1452 => DbErrorKind.ForeignKeyViolation,
        3819 => DbErrorKind.CheckViolation,
        3024 => DbErrorKind.QueryTimeout,
        1205 => DbErrorKind.LockTimeout,
        1213 => DbErrorKind.Deadlock,
        1142 or 1143 or 1044 or 1227 => DbErrorKind.PermissionDenied,
        1792 => DbErrorKind.ReadOnly,
        _ => DbErrorKind.Other,
    };

    /// <summary>예외 자신과 <see cref="Exception.InnerException"/> 체인을 훑어 처음 만나는 DB 오류를 분류한다.</summary>
    /// <param name="exception">분류할 예외. null이면 <see cref="DbErrorKind.Other"/>.</param>
    /// <returns>분류.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 예외 객체를 읽기만 한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. SaveChanges 경로는 <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>이 감싸고 ExecuteUpdate·원시 SQL 경로는 감싸지 않으므로 체인을 끝까지 본다.</description></item>
    /// </list>
    /// </remarks>
    public static DbErrorKind Classify(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is DbLockTimeoutException) return DbErrorKind.LockTimeout;
            if (e is MySqlException mysql) return KindOf(mysql.Number);
        }
        return DbErrorKind.Other;
    }
}
