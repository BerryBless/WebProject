using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary><see cref="TagResolver"/>의 순수 함수(정규화·표시용 정리·형식 검증)를 DB 없이 검증한다. DB를 쓰는 <c>ResolveIdsAsync</c>의 동시 생성 경합은 <see cref="TagResolverConcurrencyTests"/>(반대 순서 다중 태그)와 <c>PostEndpointsTests</c>(HTTP 동시 생성)에서 통합 테스트로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 순수 함수 호출만 검증하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 케이스당 문자열·<see cref="ValidationErrors"/> 인스턴스 소수.</description></item>
/// <item><description><b>Blocking:</b> 동기 실행. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class TagResolverTests
{
    /// <summary>원본 태그 입력이 트림 + 연속 공백 축소 + 소문자화 + NFC 정규화를 거쳐 유일성 키가 되는지 검증한다.</summary>
    /// <param name="raw">정규화 전 원본 태그 문자열.</param>
    /// <param name="expected">기대하는 정규화 결과.</param>
    [Theory]
    [InlineData("  ASP.NET   Core ", "asp.net core")]
    [InlineData("C#", "c#")]
    [InlineData("cafe\u0301", "caf\u00e9")]           // NFC: 결합 문자 → 단일 코드 포인트
    public void Normalize_TrimsCollapsesLowercasesAndComposes(string raw, string expected) =>
        Assert.Equal(expected, TagResolver.Normalize(raw));

    /// <summary>표시용 정리는 대소문자를 보존하되 앞뒤 공백을 트림하고 연속 공백을 한 칸으로 축소하는지 검증한다.</summary>
    [Fact]
    public void Display_KeepsCase_ButCollapsesWhitespace() =>
        Assert.Equal("ASP.NET Core", TagResolver.Display("  ASP.NET \t Core "));

    /// <summary>슬래시 포함·길이 초과·개수 초과가 각각 별도 필드 오류로 누적되는지 검증한다.</summary>
    [Fact]
    public void Validate_ReportsSlashTooLongAndTooMany()
    {
        var errors = new ValidationErrors();
        TagResolver.Validate(["ok", "a/b", new string('x', 51)], errors, "tagNames");
        TagResolver.Validate(Enumerable.Range(0, 21).Select(i => $"t{i}"), errors, "many");

        var dict = errors.ToDictionary();
        Assert.Equal(2, dict["tagNames"].Length);
        Assert.Single(dict["many"]);
    }

    /// <summary>공백뿐인 항목은 무시되고, 같은 정규화 이름으로 겹치는 항목은 1개로 계산되어 개수 상한에 걸리지 않는지 검증한다.</summary>
    [Fact]
    public void Validate_IgnoresBlankEntries_AndCountsDistinctNormalizedNames()
    {
        var errors = new ValidationErrors();
        TagResolver.Validate(Enumerable.Repeat("Same", 30).Append("  "), errors, "tagNames");
        Assert.False(errors.Any);
    }
}
