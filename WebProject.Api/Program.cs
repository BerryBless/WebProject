var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    // Random.Next(min, max) 는 상한 배타적이므로 MaxTemperatureC 를 포함하려면 +1 이 필요하다.
    var forecast = Enumerable.Range(1, WeatherForecast.ForecastDays).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(WeatherForecast.MinTemperatureC, WeatherForecast.MaxTemperatureC + 1),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapGet("/health", static () =>
    // DateTimeOffset.UtcNow: 로컬 타임존 변환(tzdata/레지스트리 조회)을 거치지 않고 시스템 UTC 틱을
    // 그대로 읽으므로 오프셋 0 이 보장되고 Now 보다 호출 비용이 낮다.
    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
    .WithName("GetHealth");

app.Run();

/// <summary>일별 기상 예보 응답 모델.</summary>
/// <param name="Date">예보 날짜</param>
/// <param name="TemperatureC">섭씨 온도(정수)</param>
/// <param name="Summary">체감 요약 문구. 없을 수 있다.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record 이며 <see cref="TemperatureF"/> 는 상태 없는 순수 계산이다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="TemperatureF"/> 는 Zero-allocation guaranteed. 정수 연산만 수행한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking).</description></item>
/// </list>
/// </remarks>
public record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    /// <summary>예보 일수. 응답 배열 길이와 같다.</summary>
    public const int ForecastDays = 5;

    /// <summary>샘플 예보의 섭씨 하한(포함).</summary>
    public const int MinTemperatureC = -20;

    /// <summary>샘플 예보의 섭씨 상한(포함).</summary>
    public const int MaxTemperatureC = 55;

    /// <summary>화씨 온도. F = C × 9/5 + 32 를 정수 연산으로 계산하며 소수부는 0 방향으로 버린다.</summary>
    /// <remarks>
    /// 템플릿 원본의 <c>TemperatureC / 0.5556</c> 근사는 100°C 를 211°F 로 계산하는 오차가 있어
    /// 정확한 9/5 비율로 교체했다. <c>TemperatureC * 9</c> 를 먼저 계산해 정수 나눗셈의 정밀도 손실을 최소화한다.
    /// </remarks>
    public int TemperatureF => 32 + TemperatureC * 9 / 5;
}

/// <summary>서비스 생존 여부를 알리는 <c>/health</c> 응답 모델.</summary>
/// <param name="Status">서비스 상태. 현 단계에서는 외부 의존성 점검 없이 상수 <c>"Healthy"</c> 를 반환한다.</param>
/// <param name="GeneratedAt">서버가 응답을 생성한 UTC 시각(오프셋 0).</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record 이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 요청당 record 인스턴스 1개 힙 할당. <paramref name="Status"/> 는 상수 문자열 인터닝으로 추가 할당 없음.
/// JSON 직렬화·응답 전송 버퍼는 프레임워크 소관이다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O·대기 없음.</description></item>
/// </list>
/// </remarks>
public record HealthResponse(string Status, DateTimeOffset GeneratedAt);

// WebApplicationFactory<Program> 기반 통합 테스트가 진입점 타입에 접근할 수 있도록 공개한다.
public partial class Program { }
