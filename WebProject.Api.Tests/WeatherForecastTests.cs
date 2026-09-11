namespace WebProject.Api.Tests;

/// <summary>
/// <see cref="WeatherForecast.TemperatureF"/> 변환 공식의 단위 테스트.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 대상이 불변 record 의 순수 계산 프로퍼티라 병렬 테스트 실행에 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 테스트당 record 인스턴스 1개만 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
/// </list>
/// </remarks>
public sealed class WeatherForecastTests
{
    /// <summary>
    /// 회귀 방지: 템플릿 원본 공식 <c>32 + (int)(C / 0.5556)</c> 은 100°C 를 211°F 로 계산했다.
    /// 정확한 9/5 비율은 212°F 를 반환해야 한다.
    /// </summary>
    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    [InlineData(37, 98)]   // 98.6°F 의 정수부
    [InlineData(-20, -4)]
    [InlineData(55, 131)]
    public void TemperatureF_ConvertsCelsiusWithExactNineFifthsRatio(int celsius, int expectedFahrenheit)
    {
        var forecast = new WeatherForecast(new DateOnly(2026, 9, 11), celsius, null);

        Assert.Equal(expectedFahrenheit, forecast.TemperatureF);
    }
}
