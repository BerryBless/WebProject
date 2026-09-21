using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests;

/// <summary>
/// <c>/health</c> 최소 API 엔드포인트의 통합 테스트.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, <see cref="WebApplicationFactory{TEntryPoint}"/>가
/// 인메모리 TestServer를 구동하므로 실제 소켓 바인딩·TCP 핸드셰이크는 발생하지 않는다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유되어
/// 테스트마다 호스트를 재구동하는 힙 할당을 피한다. <see cref="JsonDocument"/>는 <c>using</c>으로 대여 버퍼를 풀에 반환한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리와 HttpClient는 Thread-safe이며 병렬 테스트 실행에 안전하다.</description></item>
/// <item><description><b>Blocking:</b> 멤버마다 다르다. <see cref="GetHealth_Returns200WithHealthyStatusAndUtcTimestamp"/>는
/// 요청 완료를 <c>await</c>로 비동기 대기하며 스레드를 점유하는 동기 블로킹이 없다.
/// <see cref="GetHealth_IsNamedGetHealth"/>는 동기 실행이며, 최초 <c>_factory.Services</c> 접근에는
/// 호스트 초기화 비용·대기가 포함될 수 있다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class HealthEndpointTests : IClassFixture<ApiFactory>
{
    // WebApplicationFactory<Program>: Program 진입점을 인메모리 TestServer로 호스팅해 커널 소켓·TCP 핸드셰이크 없이
    // 요청 파이프라인 전체(라우팅·미들웨어·직렬화)를 검증할 수 있어 통합 테스트 오버헤드가 가장 낮다.
    private readonly ApiFactory _factory;

    /// <summary>xUnit이 주입한 클래스 픽스처 팩토리를 보관한다.</summary>
    /// <param name="factory">클래스 단위로 1회 생성·공유되는 인메모리 호스트 팩토리</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> xUnit이 테스트마다 클래스 인스턴스를 새로 생성하며, 공유되는 상태는
    /// Thread-safe한 fixture 참조뿐이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 필드 대입만 수행하며 추가 할당이 없다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). 호스트 기동은 여기서 일어나지 않는다.</description></item>
    /// </list>
    /// </remarks>
    public HealthEndpointTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <c>GET /health</c>가 200과 <c>{"status":"Healthy","generatedAt":&lt;UTC ISO 8601&gt;}</c>를 반환하고,
    /// <c>generatedAt</c>이 오프셋 0이며 요청 시작~완료 구간 안의 시각인지 검증한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 테스트 전용 <c>HttpClient</c>·응답 객체만 사용하므로 다른 테스트와
    /// 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <c>HttpClient</c>·<c>HttpResponseMessage</c>·<see cref="JsonDocument"/>를
    /// <c>using</c>으로 Dispose하며, 이때 <see cref="JsonDocument"/>의 대여 버퍼를 풀에 반환한다.
    /// 응답 문자열 등 관리 객체는 GC 대상이다.</description></item>
    /// <item><description><b>Blocking:</b> 요청 완료를 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task GetHealth_Returns200WithHealthyStatusAndUtcTimestamp()
    {
        var startedAt = DateTimeOffset.UtcNow;

        // HttpClient: HttpMessageHandler가 TestServer 파이프라인에 직결되어 네트워크 스택·포트 점유 없이 요청을 전달한다.
        // CreatePublicClient: 기본 CreateClient()의 Host는 localhost라 HostFiltering(스펙 3.6)에 400으로 걸린다.
        using var client = _factory.CreatePublicClient();

        // HttpResponseMessage: 응답 콘텐츠 스트림과 내부 버퍼의 소유권을 가지므로 테스트 스코프 종료 시 즉시 반환한다.
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        // JsonDocument: UTF-8 원문을 ArrayPool<byte> 대여 버퍼에 보관하므로 using으로 반환해야 풀 누수·GC 압력이 없다.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        Assert.True(doc.RootElement.TryGetProperty("status", out var status), "응답에 status 속성이 없습니다.");
        Assert.Equal(JsonValueKind.String, status.ValueKind);
        Assert.Equal("Healthy", status.GetString());

        Assert.True(doc.RootElement.TryGetProperty("generatedAt", out var gen), "응답에 generatedAt 속성이 없습니다.");
        Assert.Equal(JsonValueKind.String, gen.ValueKind);

        // 요구사항의 응답 계약은 status·generatedAt 2개 필드뿐이므로, 제3 필드가 추가되는 회귀를 속성 개수로 잡는다.
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());

        var raw = gen.GetString();
        Assert.NotNull(raw);
        // 오프셋 표기가 없으면 System.Text.Json이 로컬 시각으로 해석해 UTC 환경에서 결함이 숨으므로
        // 파싱 전에 원시 문자열에서 오프셋 표기 존재를 먼저 확인한다.
        Assert.True(
            raw.EndsWith("Z", StringComparison.Ordinal) || raw.EndsWith("+00:00", StringComparison.Ordinal),
            $"generatedAt에 UTC 오프셋 표기가 없습니다: {raw}");

        Assert.True(gen.TryGetDateTimeOffset(out var generatedAt), $"generatedAt을 ISO 8601로 파싱할 수 없습니다: {raw}");
        Assert.Equal(TimeSpan.Zero, generatedAt.Offset);

        var finishedAt = DateTimeOffset.UtcNow;
        // InRange는 양끝 포함이므로 요청 시작·완료 시각과 동일한 순간도 허용된다.
        Assert.InRange(generatedAt, startedAt, finishedAt);
    }

    /// <summary>
    /// <c>/health</c> 엔드포인트가 <c>GetHealth</c> 이름으로 등록되어 라우팅 이름으로 경로를 역생성할 수 있는지 검증한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> DI 컨테이너에서 싱글턴 <see cref="LinkGenerator"/>를 조회해 읽기만 하며,
    /// 해당 서비스는 Thread-safe하다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 경로 문자열 등 조회 결과만 할당된다.
    /// 최초 호출 시 발생하는 호스트 초기화 할당은 fixture 소관이다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 실행. 최초 <c>_factory.Services</c> 접근에는 호스트 초기화 비용·대기가
    /// 포함될 수 있다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void GetHealth_IsNamedGetHealth()
    {
        var linkGenerator = _factory.Services.GetRequiredService<LinkGenerator>();

        // LinkGenerator는 IEndpointNameMetadata로 엔드포인트를 찾으므로 이름이 삭제·변경되면 null이 반환돼 실패한다.
        Assert.Equal("/health", linkGenerator.GetPathByName("GetHealth", values: null));
    }
}
