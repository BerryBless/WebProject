using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>DATETIME(6)에 오프셋이 저장되지 않으므로 UTC만 받는 변환 규칙(스펙 D12)을 고정한다.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>픽스처:</b> 없음.</description></item>
/// <item><description><b>병렬 실행:</b> 안전.</description></item>
/// <item><description><b>외부 자원:</b> 없음.</description></item>
/// </list>
/// </remarks>
public sealed class UtcDateTimeOffsetConverterTests
{
    private static readonly UtcDateTimeOffsetConverter Converter = new();

    /// <summary>UTC 값은 마이크로초까지 왕복한다(DbClock이 절삭한 값 기준).</summary>
    [Fact]
    public void Utc_RoundTrips()
    {
        var value = DbClock.UtcNow();
        var stored = (DateTime)Converter.ConvertToProvider(value)!;
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(value, (DateTimeOffset)Converter.ConvertFromProvider(stored)!);
    }

    /// <summary>오프셋이 0이 아닌 값은 조용히 변환하지 않고 거부한다(시간대가 섞이면 정렬·캐시 키가 어긋난다).</summary>
    [Fact]
    public void NonUtcOffset_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => Converter.ConvertToProvider(new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.FromHours(9))));

    /// <summary>DB에서 읽은 값(Kind=Unspecified)을 UTC로 해석한다.</summary>
    [Fact]
    public void FromProvider_TreatsValueAsUtc() =>
        Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)Converter.ConvertFromProvider(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Unspecified))!).Offset);
}
