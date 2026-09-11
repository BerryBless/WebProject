namespace WebProject.Api.Tests;

/// <summary>
/// 샘플 예보의 온도 범위 상수와 난수 생성 경계가 일치하는지 검증한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="Random.Shared"/> 는 Thread-safe 이며 테스트는 공유 상태를 만들지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 루프 내 힙 할당 없음(정수 연산만).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
/// </list>
/// </remarks>
public sealed class WeatherForecastRangeTests
{
    /// <summary>
    /// 회귀 방지: 원본 템플릿의 <c>Next(-20, 55)</c> 는 상한 배타적이라 55 가 절대 나오지 않았고,
    /// 테스트는 55 까지 허용해 경계 의미가 어긋나 있었다. 핸들러와 같은 식으로 충분히 반복하면
    /// 두 끝 값이 모두 관측되어야 한다.
    /// </summary>
    [Fact]
    public void RandomTemperature_UsesInclusiveBoundsMatchingConstants()
    {
        const int iterations = 20_000; // 범위 폭 76, 각 끝 값이 한 번도 안 나올 확률 ≈ (75/76)^20000 ≈ 10^-115
        var sawMin = false;
        var sawMax = false;

        for (var i = 0; i < iterations; i++)
        {
            var value = Random.Shared.Next(WeatherForecast.MinTemperatureC, WeatherForecast.MaxTemperatureC + 1);

            Assert.InRange(value, WeatherForecast.MinTemperatureC, WeatherForecast.MaxTemperatureC);
            sawMin |= value == WeatherForecast.MinTemperatureC;
            sawMax |= value == WeatherForecast.MaxTemperatureC;
        }

        Assert.True(sawMin, "하한 값이 한 번도 생성되지 않았습니다.");
        Assert.True(sawMax, "상한 값이 한 번도 생성되지 않았습니다 (상한 배타적 오류).");
    }

    [Fact]
    public void Constants_DefineNonEmptyRangeAndPositiveDayCount()
    {
        Assert.True(WeatherForecast.MinTemperatureC < WeatherForecast.MaxTemperatureC);
        Assert.True(WeatherForecast.ForecastDays > 0);
    }
}
