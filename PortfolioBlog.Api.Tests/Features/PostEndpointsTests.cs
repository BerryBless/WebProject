using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Domain;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary><c>/api/posts</c> 관리 엔드포인트의 계약·검증·태그 해석·낙관적 동시성을 실제 PostgreSQL 컨테이너로 검증한다.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer가
/// 실제 PostgreSQL 컨테이너에 TCP로 접속하므로 DB I/O는 실제 네트워크 왕복을 수반한다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// 각 테스트 메서드는 로그인 클라이언트·DI 스코프를 자체적으로 만들고 <c>using</c>/<c>await using</c>으로 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리·HttpClient는 Thread-safe하나, 스코프 안 <c>AppDbContext</c>는 단일 스레드 전용이며
/// 테스트 간에는 클래스별 고유 DB로 격리되어 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP·DB 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class PostEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static UpsertPostRequest Request(string slug, string title = "제목", string content = "본문", string[]? tags = null,
        Guid? seriesId = null, int? seriesOrder = null, uint? version = null, string? summary = "요약") =>
        new(slug, title, summary, content, tags, seriesId, seriesOrder, version);

    private static async Task<PostDetailDto> CreateAsync(HttpClient client, UpsertPostRequest request)
    {
        using var res = await client.PostAsJsonAsync("/api/posts", request);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options);
        return new Dictionary<string, string[]>(problem!.Errors);
    }

    private async Task<Guid> SeedSeriesAsync(string slug)
    {
        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var series = new Series { Slug = slug, Title = "시리즈" };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        return series.Id;
    }

    /// <summary>생성이 201과 <c>Location</c> 헤더를 돌려주고, 태그가 정규화 이름 순으로 중복 병합되어 정렬되며, 상세 조회 결과와 완전히 일치하는지 검증한다.</summary>
    [Fact]
    public async Task Create_Returns201_WithLocation_SortedTags_AndRoundTrips()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/posts", Request("create-ok", tags: ["  Zeta ", "alpha", "ALPHA", "C#"]));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;
        Assert.Equal($"/api/posts/{created.Id}", res.Headers.Location?.OriginalString);
        Assert.Equal(["alpha", "C#", "Zeta"], created.Tags);            // 정규화 이름 순, 중복("ALPHA")은 먼저 나온 표기로 합쳐진다
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.NotEqual(0u, created.Version);

        var fetched = (await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{created.Id}", TestJson.Options))!;
        Assert.Equal(created.Tags, fetched.Tags);
        Assert.Equal(created, fetched with { Tags = created.Tags });     // record의 배열 멤버는 참조 비교라 Tags를 맞춘 뒤 나머지 전체를 비교한다
    }

    /// <summary>slug 형식·제목 공백·요약 초과·본문 바이트 초과·태그 형식·시리즈 쌍 규칙 위반이 각각 필드 키를 가진 400으로 함께 보고되는지 검증한다.</summary>
    [Fact]
    public async Task Create_InvalidFields_Returns400_WithFieldKeys()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var tooBig = new string('가', 70_000); // '가'는 UTF-8 3바이트 → 210,000바이트 > 204,800
        using var res = await client.PostAsJsonAsync("/api/posts",
            new UpsertPostRequest("Bad Slug", "   ", new string('s', 301), tooBig, ["a/b"], null, 3, null));

        var errors = await ErrorsAsync(res);
        Assert.Contains("slug", errors.Keys);
        Assert.Contains("title", errors.Keys);
        Assert.Contains("summary", errors.Keys);
        Assert.Contains("contentMarkdown", errors.Keys);
        Assert.Contains("tagNames", errors.Keys);
        Assert.Contains("seriesOrder", errors.Keys); // seriesId 없이 seriesOrder만 있음
    }

    /// <summary>필수 필드가 전부 빠진 요청도 바인딩 예외가 아니라 필드별 400 <c>ValidationProblem</c>으로 돌아오는지 검증한다.</summary>
    [Fact]
    public async Task Create_MissingRequiredFields_Returns400_NotBindingException()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/posts", new { });
        var errors = await ErrorsAsync(res);
        Assert.Contains("slug", errors.Keys);
        Assert.Contains("title", errors.Keys);
        Assert.Contains("contentMarkdown", errors.Keys);
    }

    /// <summary>형식이 깨진 JSON 본문이 500이 아니라 400으로 처리되는지 검증한다.</summary>
    [Fact]
    public async Task Create_MalformedJson_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsync("/api/posts", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>존재하지 않는 시리즈 참조는 400, seriesOrder 0은 400, 유효한 시리즈+양수 순서는 성공하는지 검증한다.</summary>
    [Fact]
    public async Task Create_UnknownSeries_Returns400_AndSeriesOrderMustBePositive()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var unknown = await client.PostAsJsonAsync("/api/posts", Request("unknown-series", seriesId: Guid.NewGuid(), seriesOrder: 1));
        Assert.Contains("seriesId", (await ErrorsAsync(unknown)).Keys);

        var seriesId = await SeedSeriesAsync("series-for-order");
        using var zero = await client.PostAsJsonAsync("/api/posts", Request("zero-order", seriesId: seriesId, seriesOrder: 0));
        Assert.Contains("seriesOrder", (await ErrorsAsync(zero)).Keys);

        var ok = await CreateAsync(client, Request("in-series", seriesId: seriesId, seriesOrder: 2));
        Assert.Equal(seriesId, ok.SeriesId);
        Assert.Equal(2, ok.SeriesOrder);
    }

    /// <summary>같은 slug로 두 번째 글을 만들면 409 Conflict가 되는지 검증한다.</summary>
    [Fact]
    public async Task Create_DuplicateSlug_Returns409()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        await CreateAsync(client, Request("dup"));
        using var res = await client.PostAsJsonAsync("/api/posts", Request("dup"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    /// <summary>후행 개행이 붙은 slug가 500(DB CHECK 위반)이 아니라 필드 키 <c>slug</c>를 가진 400으로 거부되는지 검증한다.
    /// .NET <see cref="System.Text.RegularExpressions.Regex"/>의 <c>$</c>는 문자열 끝의 단일 <c>\n</c> 앞에서도 매칭되지만
    /// PostgreSQL <c>~</c> 연산자는 그렇지 않아, 앵커를 맞추지 않으면 형식 검증을 통과한 뒤 DB CHECK에서만 걸린다.</summary>
    [Fact]
    public async Task Create_SlugWithTrailingNewline_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/posts", Request("trailing-newline\n"));
        Assert.Contains("slug", (await ErrorsAsync(res)).Keys);
    }

    /// <summary>제목·본문·태그 이름에 NUL(U+0000)이 섞여 있으면 500(PostgreSQL <c>text</c>가 NUL을 저장할 수 없어 발생)이 아니라
    /// 해당 필드 키를 가진 400으로 거부되는지 검증한다. JSON은 유니코드 이스케이프로 NUL을 실어 나를 수 있어 입력에서 걸러야 한다.</summary>
    [Fact]
    public async Task Create_NulCharacter_Returns400_NotServerError()
    {
        using var client = await factory.CreateLoggedInClientAsync();

        using var titleRes = await client.PostAsJsonAsync("/api/posts", Request("nul-title", title: "제목\0"));
        Assert.Contains("title", (await ErrorsAsync(titleRes)).Keys);

        using var contentRes = await client.PostAsJsonAsync("/api/posts", Request("nul-content", content: "본문\0"));
        Assert.Contains("contentMarkdown", (await ErrorsAsync(contentRes)).Keys);

        using var tagRes = await client.PostAsJsonAsync("/api/posts", Request("nul-tag", tags: ["ta\0g"]));
        Assert.Contains("tagNames", (await ErrorsAsync(tagRes)).Keys);
    }

    /// <summary>수정이 필드·태그 연결을 교체하고 version을 바꾸며 CreatedAt은 그대로 유지하는지 검증한다.</summary>
    [Fact]
    public async Task Update_ReplacesFieldsAndTags_BumpsVersion_KeepsCreatedAt()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var created = await CreateAsync(client, Request("update-ok", tags: ["one", "two"]));

        using var res = await client.PutAsJsonAsync($"/api/posts/{created.Id}",
            Request("update-ok", title: "새 제목", content: "새 본문", tags: ["two", "three"], version: created.Version));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;

        Assert.Equal("새 제목", updated.Title);
        Assert.Equal("새 본문", updated.ContentMarkdown);
        Assert.Equal(["three", "two"], updated.Tags);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.NotEqual(created.Version, updated.Version);
    }

    /// <summary>slug 변경 시도는 400, version 누락은 400, 오래된 탭의 version으로 두 번째 저장을 시도하면 409이고 첫 번째 저장 내용이 유지되는지 검증한다.</summary>
    [Fact]
    public async Task Update_SlugChange_MissingVersion_Return400_StaleVersion_Returns409()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var created = await CreateAsync(client, Request("immutable-slug"));

        using var slugChange = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("other-slug", version: created.Version));
        Assert.Contains("slug", (await ErrorsAsync(slugChange)).Keys);

        using var noVersion = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("immutable-slug"));
        Assert.Contains("version", (await ErrorsAsync(noVersion)).Keys);

        using var first = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("immutable-slug", title: "탭 A", version: created.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var stale = await client.PutAsJsonAsync($"/api/posts/{created.Id}", Request("immutable-slug", title: "탭 B", version: created.Version));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{created.Id}", TestJson.Options);
        Assert.Equal("탭 A", current!.Title); // 오래된 탭이 덮어쓰지 못했다
    }

    /// <summary>삭제는 version이 필수이고, 오래된 version은 409, 맞는 version은 204이며, 삭제 후 재삭제는 404이고 태그 자체는 남는지 검증한다.</summary>
    [Fact]
    public async Task Delete_RequiresCurrentVersion_ThenReturns204_AndKeepsTags()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var created = await CreateAsync(client, Request("delete-me", tags: ["survivor-tag"]));

        using var noVersion = await client.DeleteAsync($"/api/posts/{created.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        using var stale = await client.DeleteAsync($"/api/posts/{created.Id}?version={created.Version + 1}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var ok = await client.DeleteAsync($"/api/posts/{created.Id}?version={created.Version}");
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        using var gone = await client.GetAsync($"/api/posts/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await client.DeleteAsync($"/api/posts/{created.Id}?version={created.Version}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Tags.AnyAsync(t => t.NormalizedName == "survivor-tag")); // 글 삭제는 링크만 지운다
    }

    /// <summary>목록이 최신순으로 페이지네이션되고, 제목·요약·본문을 함께 검색하며, 검색어의 <c>%</c>·<c>_</c>가 와일드카드가 아니라 리터럴로 매칭되는지 검증한다.</summary>
    [Fact]
    public async Task List_PagesNewestFirst_SearchesTitleSummaryContent_AndTreatsWildcardsLiterally()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var marker = "lst" + Guid.NewGuid().ToString("N")[..8];
        await CreateAsync(client, Request($"{marker}-1", title: $"{marker} 첫째", content: "abc"));
        await CreateAsync(client, Request($"{marker}-2", title: $"{marker} 둘째", content: "a_c 100% 리터럴"));
        await CreateAsync(client, Request($"{marker}-3", title: $"{marker} 셋째", content: "본문", summary: "특별한요약어"));

        var page = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={marker}&skip=0&take=2", TestJson.Options);
        Assert.Equal(3, page!.Total);
        Assert.Equal([$"{marker}-3", $"{marker}-2"], page.Items.Select(p => p.Slug)); // 최신순

        var second = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={marker}&skip=2&take=2", TestJson.Options);
        Assert.Equal([$"{marker}-1"], second!.Items.Select(p => p.Slug));

        var bySummary = await client.GetFromJsonAsync<PagedPostsDto>("/api/posts?q=특별한요약어", TestJson.Options);
        Assert.Equal([$"{marker}-3"], bySummary!.Items.Select(p => p.Slug));

        // "a_c"가 와일드카드였다면 "abc"(-1)도 걸린다. 리터럴로만 매칭되어야 한다.
        var literal = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={Uri.EscapeDataString("a_c")}", TestJson.Options);
        Assert.Equal([$"{marker}-2"], literal!.Items.Where(p => p.Slug.StartsWith(marker, StringComparison.Ordinal)).Select(p => p.Slug));
        var percent = await client.GetFromJsonAsync<PagedPostsDto>($"/api/posts?q={Uri.EscapeDataString("100%")}", TestJson.Options);
        Assert.Equal([$"{marker}-2"], percent!.Items.Where(p => p.Slug.StartsWith(marker, StringComparison.Ordinal)).Select(p => p.Slug));
    }

    /// <summary>잘못된 페이지네이션 값(take 비숫자·skip 음수·take 0·take 상한 초과)이 400으로 거부되는지 검증한다.</summary>
    /// <param name="url">잘못된 쿼리 문자열을 포함한 요청 경로.</param>
    [Theory]
    [InlineData("/api/posts?take=abc")]
    [InlineData("/api/posts?skip=-1")]
    [InlineData("/api/posts?take=0")]
    [InlineData("/api/posts?take=201")]
    public async Task List_InvalidPaging_Returns400(string url)
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>검색어가 최대 길이를 넘으면 400으로 거부되는지 검증한다.</summary>
    [Fact]
    public async Task List_QueryTooLong_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.GetAsync("/api/posts?q=" + new string('q', 101));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>같은 새 태그로 여섯 개의 글을 동시에 생성해도 전부 201이고 태그 행이 정확히 1개만 남는지 검증한다(ON CONFLICT DO NOTHING의 경합 흡수).</summary>
    [Fact]
    public async Task ConcurrentCreates_WithSameNewTag_BothSucceed_AndTagIsSingle()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var tag = "race-" + Guid.NewGuid().ToString("N")[..8];

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            client.PostAsJsonAsync("/api/posts", Request($"{tag}-{i}", tags: [tag]))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        foreach (var r in results) r.Dispose();

        await using var scope = factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Tags.CountAsync(t => t.NormalizedName == tag));
        Assert.Equal(6, await db.PostTags.CountAsync(pt => pt.Tag.NormalizedName == tag));
    }
}
