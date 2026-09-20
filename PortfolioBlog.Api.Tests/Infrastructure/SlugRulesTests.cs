using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary><see cref="SlugRules.IsValid"/>가 .NET <see cref="System.Text.RegularExpressions.Regex"/>의 개행 앞 <c>$</c> 매칭 함정에 걸리지 않고 DB CHECK 제약(<c>CK_Posts_Slug_Format</c>)과 같은 판정을 내리는지 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 순수 함수 호출만 검증하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 케이스당 bool 결과 1개. 소스 생성된 정규식 매처는 프로세스 전체가 공유한다.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class SlugRulesTests
{
    /// <summary>형식·길이 규칙을 만족하는 slug가 true를 반환하는지 검증한다.</summary>
    /// <param name="slug">유효할 것으로 기대하는 slug.</param>
    [Theory]
    [InlineData("a")]
    [InlineData("my-first-post")]
    [InlineData("a1-b2")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 100자(SlugMax 경계)
    public void IsValid_AcceptsValidSlugs(string slug) => Assert.True(SlugRules.IsValid(slug));

    /// <summary>형식·길이 규칙을 어기는 slug가 false를 반환하는지 검증한다. <c>"abc\n"</c>은 .NET <see cref="System.Text.RegularExpressions.Regex"/>의 <c>$</c>가 후행 개행 앞에서도 매칭되는 함정을 노린다(DB CHECK는 이 값을 거부한다).</summary>
    /// <param name="slug">무효할 것으로 기대하는 slug.</param>
    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    [InlineData("\nabc")]
    [InlineData("Abc")]
    [InlineData("ab c")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("ab--c")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 101자(SlugMax 초과)
    public void IsValid_RejectsInvalidSlugs(string slug) => Assert.False(SlugRules.IsValid(slug));
}
