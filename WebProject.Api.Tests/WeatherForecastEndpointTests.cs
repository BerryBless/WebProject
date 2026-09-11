using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebProject.Api.Tests;

/// <summary>
/// <c>/weatherforecast</c> 최소 API 엔드포인트의 통합 테스트.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, <see cref="WebApplicationFactory{TEntryPoint}"/>가
/// 인메모리 TestServer를 구동하므로 실제 소켓 바인딩은 발생하지 않는다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유되어
/// 테스트마다 호스트를 재구동하는 힙 할당을 피한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리와 HttpClient는 Thread-safe이며 병렬 테스트 실행에 안전하다.</description></item>
/// </list>
/// </remarks>
public sealed class WeatherForecastEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    // WebApplicationFactory<Program>: Program 진입점을 인메모리 TestServer로 호스팅해 커널 소켓 없이
    // 요청 파이프라인 전체(라우팅·미들웨어·직렬화)를 검증할 수 있어 통합 테스트 오버헤드가 가장 낮다.
    private readonly WebApplicationFactory<Program> _factory;

    public WeatherForecastEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetWeatherForecast_Returns200WithFiveItems()
    {
        // HttpClient: TestServer 핸들러에 직접 연결되어 네트워크 스택을 거치지 않으며 팩토리가 수명을 관리한다.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/weatherforecast");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var forecasts = await response.Content.ReadFromJsonAsync<WeatherForecastDto[]>();
        Assert.NotNull(forecasts);
        Assert.Equal(5, forecasts.Length);
        Assert.All(forecasts, f => Assert.InRange(f.TemperatureC, -20, 55));
        // 직렬화된 TemperatureF 가 도메인 공식(F = C × 9/5 + 32)과 일치하는지 응답 계약 수준에서 검증한다.
        Assert.All(forecasts, f => Assert.Equal(32 + f.TemperatureC * 9 / 5, f.TemperatureF));
    }

    private sealed record WeatherForecastDto(DateOnly Date, int TemperatureC, string? Summary, int TemperatureF);
}
