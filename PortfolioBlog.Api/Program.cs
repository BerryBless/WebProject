using Microsoft.EntityFrameworkCore;
using PortfolioBlog.Api.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
// ProblemDetails: 400/401/403/404/409/429 등 모든 오류 응답을 RFC 9457 형식으로 통일한다.
builder.Services.AddProblemDetails();
// 바인딩 실패(JSON 파싱 오류·잘못된 쿼리 값)를 예외(Development 기본값)가 아니라 항상 400으로 응답한다.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
// 연결 문자열은 람다 안에서(=Build 이후 첫 해석 시점에) 읽는다. Global Constraints의 "설정은 Build 이후에만" 규칙.
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default 설정이 없습니다.")));

var app = builder.Build();

// 단일 인스턴스 배포이므로 시작 시 마이그레이션을 적용한다(스펙 3.10).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", static () =>
    // DateTimeOffset.UtcNow: 로컬 타임존 변환(tzdata/레지스트리 조회)을 거치지 않고 시스템 UTC 틱을
    // 그대로 읽으므로 오프셋 0 이 보장되고 Now 보다 호출 비용이 낮다.
    new HealthResponse("Healthy", DateTimeOffset.UtcNow))
    .WithName("GetHealth");

app.Run();

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
