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

    [Fact]
    public void Absent_IsPageOne()
    {
        Assert.True(PageNumber.TryRead(Query(), 500, out var page));
        Assert.Equal(1, page);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("500", 500)]
    [InlineData("007", 7)]
    public void Valid(string raw, int expected)
    {
        Assert.True(PageNumber.TryRead(Query(raw), 500, out var page));
        Assert.Equal(expected, page);
    }

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

    [Fact]
    public void RepeatedParameter_IsInvalid() => Assert.False(PageNumber.TryRead(Query("1", "2"), 500, out _));
}
