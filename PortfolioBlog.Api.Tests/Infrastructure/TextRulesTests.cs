using PortfolioBlog.Api.Contracts;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>DB에 닿는 문자열의 공통 규칙(<see cref="TextRules"/>) 단위 테스트.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 각 테스트는 정적 무상태 함수만 호출하므로 다른 테스트와 공유하는 가변 상태가 없다. 외부 자원(DB·네트워크) 없음.</description></item>
/// <item><description><b>Memory Allocation:</b> Theory 케이스마다 입력 문자열 리터럴만 참조하며 추가 픽스처를 생성하지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 모든 테스트는 동기 즉시 반환. I/O·비동기 대기 없음.</description></item>
/// </list>
/// </remarks>
public sealed class TextRulesTests
{
    /// <summary>NUL이 어디에 있든 잡아내고, null·빈 문자열·다른 제어 문자는 통과시킨다(MySQL은 NUL을 저장할 수 있지만, 검색·로그·렌더 경로의 이상 입력을 막는 정책으로 거부한다).</summary>
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
