// PortfolioBlog.Api.Tests/Infrastructure/LikePatternTests.cs
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary><see cref="LikePattern.Contains"/>가 LIKE/ILIKE 메타문자(<c>\ % _</c>)를 올바르게 이스케이프해 "포함" 패턴을 만드는지 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 순수 함수 호출만 검증하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 케이스당 결과 문자열 1개.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class LikePatternTests
{
    /// <summary><paramref name="term"/>을 감싼 결과가 <paramref name="expected"/>와 같은지, 특히 <c>\ % _</c>가 각각 <c>\\ \% \_</c>로 이스케이프되는지 검증한다.</summary>
    /// <param name="term">이스케이프 전 원본 검색어.</param>
    /// <param name="expected">앞뒤에 <c>%</c>를 붙이고 메타문자를 이스케이프한 기대 패턴.</param>
    [Theory]
    [InlineData("abc", "%abc%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_c", @"%a\_c%")]
    [InlineData(@"c:\temp", @"%c:\\temp%")]
    [InlineData(@"\%_", @"%\\\%\_%")]
    public void Contains_EscapesLikeMetacharacters(string term, string expected) =>
        Assert.Equal(expected, LikePattern.Contains(term));
}
