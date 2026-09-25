using System.Net;
using System.Text;
using System.Text.Json;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary>앱 검증이 허용하는 입력 집합이 DB 제약이 허용하는 집합 안에 있는지(⊆) 경계값으로 검증한다. 어떤 입력도 500이 되어서는 안 된다.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며, 각 케이스가 <see cref="ApiFactory.CreateLoggedInClientAsync"/>로 실제 로그인 왕복을 거친 클라이언트를 만든다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="factory"/>는 클래스 픽스처로 1회 생성·공유된다. 경계값 본문 문자열(최대 약 68,268자)은 각 케이스가 <see cref="Json"/>으로 새로 만들어 GC 대상이 된다.</description></item>
/// <item><description><b>Concurrency:</b> <c>mysql</c> 컬렉션에 속해 같은 컬렉션의 다른 테스트 클래스와 순차 실행된다. 케이스마다 새 로그인 세션·고유 slug를 쓰므로 서로 간섭하지 않는다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class ValidationWithinDbConstraintsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // Interlocked.Increment: 여러 [Theory] 케이스가 xUnit에 의해 병렬로 실행될 수 있어(클래스 안에서는 기본 순차이지만
    // 같은 프로세스의 다른 클래스와 동시에 돈다) 단순 증가 대신 원자적 증가로 slug 충돌(IX_Posts_Slug 유니크 위반 → 409)을 막는다.
    private static int _slug;

    /// <summary>이 클래스의 모든 요청이 공유하는 slug 접두사에 원자적으로 증가하는 일련번호를 붙여 유니크 slug를 만든다.</summary>
    private static string NextSlug() => $"bound-{Interlocked.Increment(ref _slug):000}";

    // TestJson.RelaxedOptions: 기본 인코더(JavaScriptEncoder.Default)는 한글 등 비 ASCII 코드포인트 1개를 \uXXXX(6바이트)로
    // 이스케이프해 200KB 한글 본문이 관리 JSON 상한(256KB, ApiBodyLimitMiddleware.JsonLimitBytes)을 넘긴다.
    // 브라우저의 JSON.stringify는 이스케이프하지 않으므로, 실제 클라이언트와 같은 바이트 수로 보내려면 이 옵션을 쓴다.
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body, TestJson.RelaxedOptions), Encoding.UTF8, "application/json");

    /// <summary><paramref name="unit"/>을 <paramref name="count"/>번 이어붙인다(경계값 길이의 문자열을 만드는 용도).</summary>
    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600); // UTF-16 2단위, MySQL(CHAR_LENGTH) 1문자, UTF-8 4바이트(utf8mb4)

    /// <summary>앱 검증을 통과해야 하는(=201) 경계값 입력 목록.</summary>
    /// <returns>라벨과 요청 본문 오버라이드 쌍의 이론 데이터.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit이 테스트 실행 전 이론 데이터 수집 단계에서 호출한다(테스트 스레드와 별개).</description></item>
    /// <item><description><b>Memory Policy:</b> 호출마다 새 <see cref="TheoryData{T1, T2}"/> 인스턴스와 케이스 수만큼의 익명 객체(요청 본문 오버라이드)를 할당한다. 캐시하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static TheoryData<string, object> Accepted() => new()
    {
        { "제목 200자(한글)", new { title = Repeat("가", 200) } },
        { "제목 이모지 100개(UTF-16 200단위)", new { title = Repeat(Emoji, 100) } },
        { "제목 앞뒤 공백", new { title = "   앞뒤 공백   " } },
        { "제목이 폭 없는 공백 하나(.NET 기준 공백 아님)", new { title = ((char)0x200B).ToString() } },
        { "요약 300자", new { title = "t", summary = Repeat("가", 300) } },
        { "본문 정확히 204,800바이트(한글 3바이트)", new { title = "t", contentMarkdown = Repeat("가", 68_266) + "aa" } },
        { "slug 100자", new { title = "t", slug = new string('a', 100) } },
        { "태그 50자 × 20개", new { title = "t", tagNames = Enumerable.Range(0, 20).Select(i => Repeat("가", 48) + i.ToString("00")).ToArray() } },
        { "태그 안의 전각 공백", new { title = "t", tagNames = new[] { "a" + (char)0x3000 + "b" } } },
    };

    /// <summary>앱 검증이 거부해야 하는(=400, 500 금지) 경계값 입력 목록.</summary>
    /// <returns>라벨과 요청 본문 오버라이드 쌍의 이론 데이터.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit이 테스트 실행 전 이론 데이터 수집 단계에서 호출한다(테스트 스레드와 별개).</description></item>
    /// <item><description><b>Memory Policy:</b> 호출마다 새 <see cref="TheoryData{T1, T2}"/> 인스턴스와 케이스 수만큼의 익명 객체(요청 본문 오버라이드)를 할당한다. 캐시하지 않는다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    public static TheoryData<string, object> Rejected() => new()
    {
        { "제목 201자", new { title = Repeat("가", 201) } },
        { "제목이 NEL 하나(.NET 기준 공백)", new { title = ((char)0x85).ToString() } },
        { "제목에 NUL", new { title = "a" + '\0' + "b" } },
        { "요약 301자", new { title = "t", summary = Repeat("가", 301) } },
        { "본문 204,801바이트", new { title = "t", contentMarkdown = Repeat("가", 68_266) + "aaa" } },
        { "slug 101자", new { title = "t", slug = new string('a', 101) } },
        { "slug 끝 개행", new { title = "t", slug = "abc\n" } },
        { "태그 51자", new { title = "t", tagNames = new[] { Repeat("가", 51) } } },
        { "태그 21개", new { title = "t", tagNames = Enumerable.Range(0, 21).Select(i => "tag" + i).ToArray() } },
    };

    /// <summary>기본 필드에 <paramref name="overrides"/>를 덮어써 로그인 클라이언트로 <c>POST /api/posts</c>를 보낸다.</summary>
    private async Task<HttpStatusCode> PostAsync(object overrides)
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var body = new Dictionary<string, object?> { ["slug"] = NextSlug(), ["title"] = "t", ["summary"] = "", ["contentMarkdown"] = "본문" };
        foreach (var property in overrides.GetType().GetProperties()) body[property.Name] = property.GetValue(overrides);
        using var res = await client.PostAsync("/api/posts", Json(body));
        return res.StatusCode;
    }

    /// <summary>앱 검증을 통과하는 입력이 실제로 DB에 닿아 201(생성됨)로 끝나는지 확인한다(500이면 앱 검증 ⊆ DB 제약 가설이 깨진 것).</summary>
    [Theory]
    [MemberData(nameof(Accepted))]
    public async Task InputsTheAppAccepts_AreAcceptedByTheDatabase(string label, object overrides) =>
        Assert.True(HttpStatusCode.Created == await PostAsync(overrides), label);

    /// <summary>앱 검증이 거부하는 입력이 400으로 끝나고 500이 되지 않는지 확인한다.</summary>
    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task InputsOutsideTheLimits_Are400_Never500(string label, object overrides) =>
        Assert.True(HttpStatusCode.BadRequest == await PostAsync(overrides), label);

    /// <summary>짝 없는 서로게이트는 JSON 바인딩이 거부한다(DB에 닿으면 드라이버가 인코딩 예외로 500을 낼 수 있다 — 2A 정오표 #10). 원시 JSON으로 보낸다.</summary>
    [Fact]
    public async Task LoneSurrogate_InJson_Is400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var raw = "{\"slug\":\"" + NextSlug() + "\",\"title\":\"a\\uD800b\",\"summary\":\"\",\"contentMarkdown\":\"x\"}";
        using var res = await client.PostAsync("/api/posts", new StringContent(raw, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>시리즈도 같은 경계: 설명 1000자는 되고 1001자는 400.</summary>
    [Fact]
    public async Task Series_DescriptionBoundary()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var ok = await client.PostAsync("/api/series", Json(new { slug = NextSlug(), title = Repeat("가", 200), description = Repeat("가", 1000) }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        using var tooLong = await client.PostAsync("/api/series", Json(new { slug = NextSlug(), title = "t", description = Repeat("가", 1001) }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
}
