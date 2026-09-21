using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using PortfolioBlog.Api.Pages;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>쪽 번호 파싱은 기본 거부다: ASCII 숫자 1~4자리, 1..max, 값 하나.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 순수 함수 호출만 검증하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 케이스당 <see cref="QueryCollection"/> 1개와 테스트 전용 <see cref="StringValues"/> 배열.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class PageNumberTests
{
    private static IQueryCollection Query(params string[] values) =>
        new QueryCollection(values.Length == 0
            ? new Dictionary<string, StringValues>()
            : new Dictionary<string, StringValues> { ["page"] = new StringValues(values) });

    /// <summary><c>page</c> 쿼리 값이 아예 없으면 1쪽으로 간주해 <c>true</c>를 반환하는지 검증한다(목록의 기본 진입 상태).</summary>
    [Fact]
    public void Absent_IsPageOne()
    {
        Assert.True(PageNumber.TryRead(Query(), 500, out var page));
        Assert.Equal(1, page);
    }

    /// <summary>ASCII 숫자 1~4자리이고 1..max 범위인 값이 그 정수로 정확히 파싱되는지 검증한다. <c>"007"</c>은 앞자리 0이 있어도 정수로 읽히는지 확인한다.</summary>
    /// <param name="raw">쿼리 문자열에 실릴 원본 값.</param>
    /// <param name="expected">파싱되어야 하는 쪽 번호.</param>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("500", 500)]
    [InlineData("007", 7)]
    public void Valid(string raw, int expected)
    {
        Assert.True(PageNumber.TryRead(Query(raw), 500, out var page));
        Assert.Equal(expected, page);
    }

    /// <summary>기본 거부 규칙을 어기는 값은 전부 거부되는지 검증한다: 빈 문자열, 0, 상한 밖(501), 부호(-1·+1), 소수점(1.0), 앞뒤 공백, 전각 숫자(<c>char.IsDigit</c>는 참이지만 ASCII가 아니므로 거부), 자릿수 초과(5자리).</summary>
    /// <param name="raw">거부되어야 하는 원본 값.</param>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("501")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("１")]      // 전각 숫자: char.IsDigit는 참이지만 받지 않는다
    [InlineData("99999")]
    public void Invalid(string raw) => Assert.False(PageNumber.TryRead(Query(raw), 500, out _));

    /// <summary>같은 이름의 쿼리 값이 두 번 오면(<c>?page=1&amp;page=2</c>) 값 하나만 허용하는 규칙을 어겨 거부되는지 검증한다.</summary>
    [Fact]
    public void RepeatedParameter_IsInvalid() => Assert.False(PageNumber.TryRead(Query("1", "2"), 500, out _));
}
