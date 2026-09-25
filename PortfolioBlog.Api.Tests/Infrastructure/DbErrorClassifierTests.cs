using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>MySQL 오류 번호와 예외 체인을 HTTP 매핑이 쓰는 의미 분류로 바꾸는 규칙을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음(순수 함수).</description></item>
/// <item><description><b>병렬 실행:</b> 안전. 공유 상태 없음.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class DbErrorClassifierTests
{
    /// <summary>스펙 2.4절 번호표가 그대로 분류되는지 확인한다. 표를 바꾸면 이 테스트가 먼저 깨진다.</summary>
    [Theory]
    [InlineData(1062, DbErrorKind.UniqueViolation)]
    [InlineData(1451, DbErrorKind.ForeignKeyViolation)]
    [InlineData(1452, DbErrorKind.ForeignKeyViolation)]
    [InlineData(3819, DbErrorKind.CheckViolation)]
    [InlineData(3024, DbErrorKind.QueryTimeout)]
    [InlineData(1205, DbErrorKind.LockTimeout)]
    [InlineData(1213, DbErrorKind.Deadlock)]
    [InlineData(1142, DbErrorKind.PermissionDenied)]
    [InlineData(1143, DbErrorKind.PermissionDenied)]
    [InlineData(1044, DbErrorKind.PermissionDenied)]
    [InlineData(1227, DbErrorKind.PermissionDenied)]
    [InlineData(1792, DbErrorKind.ReadOnly)]
    [InlineData(1064, DbErrorKind.Other)]
    [InlineData(0, DbErrorKind.Other)]
    public void KindOf_MapsTheSpecTable(int number, DbErrorKind expected) => Assert.Equal(expected, DbErrorClassifier.KindOf(number));

    /// <summary>앱이 직접 던지는 GET_LOCK 타임아웃은 감싸여 있어도 LockTimeout이다(OverloadExceptionHandler가 체인 어디서든 찾는다).</summary>
    [Fact]
    public void Classify_FindsLockTimeout_AnywhereInTheChain() =>
        Assert.Equal(DbErrorKind.LockTimeout, DbErrorClassifier.Classify(new InvalidOperationException("outer", new DbLockTimeoutException("wait"))));

    /// <summary>DB와 무관한 예외와 null은 Other다(과대 분류로 500이 503이 되지 않는다).</summary>
    [Fact]
    public void Classify_UnrelatedOrNull_IsOther()
    {
        Assert.Equal(DbErrorKind.Other, DbErrorClassifier.Classify(new InvalidOperationException("x")));
        Assert.Equal(DbErrorKind.Other, DbErrorClassifier.Classify(null));
    }
}
