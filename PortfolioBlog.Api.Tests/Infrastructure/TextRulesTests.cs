using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>DB에 닿는 문자열의 공통 규칙(<see cref="TextRules"/>) 단위 테스트.</summary>
public sealed class TextRulesTests
{
    /// <summary>NUL이 어디에 있든 잡아내고, null·빈 문자열·다른 제어 문자는 통과시킨다(PostgreSQL text가 못 담는 것은 NUL뿐이다).</summary>
    [Theory]
    [InlineData("\0", true)]
    [InlineData("a\0b", true)]
    [InlineData("끝\0", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("탭\t개행\n은 허용", false)]
    public void ContainsNul_DetectsOnlyNul(string? value, bool expected) =>
        Assert.Equal(expected, TextRules.ContainsNul(value));
}
