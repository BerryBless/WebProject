using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Infrastructure.Data;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary><c>/api/series</c> 관리 엔드포인트의 계약·검증·정렬·삭제 시 글 보존을 실제 MySQL 컨테이너로 검증한다.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer가
/// 실제 MySQL 컨테이너에 TCP로 접속하므로 DB I/O는 실제 네트워크 왕복을 수반한다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// 각 테스트 메서드는 로그인 클라이언트를 자체적으로 만들고 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리·HttpClient는 Thread-safe하나, 테스트 간에는 클래스별 고유 DB로 격리되어 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("mysql")]
public sealed class SeriesEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>시리즈를 생성하고 201·<c>Location</c> 헤더를 검증한 뒤 생성된 <see cref="SeriesDto"/>를 돌려준다.</summary>
    /// <param name="client">로그인된 관리 클라이언트.</param>
    /// <param name="slug">생성할 시리즈의 slug.</param>
    /// <param name="title">생성할 시리즈의 제목(기본값 "연재").</param>
    /// <returns>생성 응답 본문을 역직렬화한 <see cref="SeriesDto"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 요청·응답 JSON 버퍼와 <see cref="SeriesDto"/> 1개를 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: HTTP 왕복을 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<SeriesDto> CreateSeriesAsync(HttpClient client, string slug, string title = "연재")
    {
        using var res = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest(slug, title, "설명"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        return (await res.Content.ReadFromJsonAsync<SeriesDto>(TestJson.Options))!;
    }

    /// <summary>지정한 시리즈에 속한 글을 생성하고 201을 검증한 뒤 생성된 <see cref="PostDetailDto"/>를 돌려준다.</summary>
    /// <param name="client">로그인된 관리 클라이언트.</param>
    /// <param name="slug">생성할 글의 slug(제목으로도 재사용).</param>
    /// <param name="seriesId">글이 속할 시리즈의 Id.</param>
    /// <param name="order">시리즈 안 순서.</param>
    /// <returns>생성 응답 본문을 역직렬화한 <see cref="PostDetailDto"/>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 호출된다.</description></item>
    /// <item><description><b>Memory Policy:</b> 요청·응답 JSON 버퍼와 <see cref="PostDetailDto"/> 1개를 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> Thread-safe. Non-blocking: HTTP 왕복을 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    private static async Task<PostDetailDto> CreatePostAsync(HttpClient client, string slug, Guid seriesId, int order)
    {
        using var res = await client.PostAsJsonAsync("/api/posts", new UpsertPostRequest(slug, slug, "", "본문", null, seriesId, order, null));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;
    }

    /// <summary>시리즈 생성·조회·목록 왕복이 성공하고, 목록의 <c>PostCount</c>가 소속 글 수를 정확히 반영하는지 검증한다.</summary>
    [Fact]
    public async Task Create_Get_List_RoundTrip_WithPostCount()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-round-trip");
        await CreatePostAsync(client, "srt-post-1", series.Id, 1);

        var list = await client.GetFromJsonAsync<SeriesDto[]>("/api/series", TestJson.Options);
        Assert.Equal(1, list!.Single(s => s.Id == series.Id).PostCount);
    }

    /// <summary>시리즈 상세의 소속 글 목록이 <c>SeriesOrder</c>, 그다음 <c>CreatedAt</c> 순으로 안정 정렬되고, 같은 순서값이 있어도 나중에 만든 글이 뒤에 오는지 검증한다.</summary>
    [Fact]
    public async Task Detail_OrdersPostsBySeriesOrder_ThenCreatedAt_AllowingDuplicateOrders()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-ordering");
        await CreatePostAsync(client, "so-third", series.Id, 3);
        await CreatePostAsync(client, "so-first", series.Id, 1);
        await CreatePostAsync(client, "so-dup-a", series.Id, 2);
        await CreatePostAsync(client, "so-dup-b", series.Id, 2); // 같은 순서값 허용: 나중에 만든 글이 뒤

        var detail = await client.GetFromJsonAsync<SeriesDetailDto>($"/api/series/{series.Id}", TestJson.Options);
        Assert.Equal(["so-first", "so-dup-a", "so-dup-b", "so-third"], detail!.Posts.Select(p => p.Slug));
    }

    /// <summary>slug·제목·설명이 모두 형식을 위반하면 필드별 키를 가진 400을 함께 돌려주고, 이미 쓰인 slug로 생성하면 409를 돌려주는지 검증한다.</summary>
    [Fact]
    public async Task Create_Invalid_Returns400_Duplicate_Returns409()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var invalid = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("Bad Slug", " ", new string('d', 1001)));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options))!.Errors;
        Assert.Contains("slug", errors.Keys);
        Assert.Contains("title", errors.Keys);
        Assert.Contains("description", errors.Keys);

        await CreateSeriesAsync(client, "series-dup");
        using var dup = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("series-dup", "다른 제목", ""));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    /// <summary>수정이 제목·설명은 바꾸지만 slug는 불변으로 거부하고(400), 존재하지 않는 시리즈 수정은 404를 돌려주는지 검증한다.</summary>
    [Fact]
    public async Task Update_ChangesTitleAndDescription_ButNotSlug()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-update");

        using var ok = await client.PutAsJsonAsync($"/api/series/{series.Id}", new UpsertSeriesRequest("series-update", "새 제목", "새 설명"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var updated = (await ok.Content.ReadFromJsonAsync<SeriesDto>(TestJson.Options))!;
        Assert.Equal("새 제목", updated.Title);
        Assert.Equal("새 설명", updated.Description);

        using var slugChange = await client.PutAsJsonAsync($"/api/series/{series.Id}", new UpsertSeriesRequest("series-renamed", "새 제목", ""));
        Assert.Equal(HttpStatusCode.BadRequest, slugChange.StatusCode);

        using var missing = await client.PutAsJsonAsync($"/api/series/{Guid.NewGuid()}", new UpsertSeriesRequest("whatever", "제목", ""));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>시리즈 삭제가 소속 글은 남기고 <c>SeriesId</c>·<c>SeriesOrder</c>를 한 트랜잭션에서 함께 비우며(CHECK 제약 유지),
    /// 글 행 자체가 갱신되어 <c>Version</c>이 바뀌고, 삭제된 시리즈 재조회·재삭제가 각각 404가 되는지 검증한다.</summary>
    [Fact]
    public async Task Delete_KeepsPosts_AndClearsBothSeriesFields()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-delete");
        var post = await CreatePostAsync(client, "sd-post", series.Id, 1);

        using var res = await client.DeleteAsync($"/api/series/{series.Id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var after = await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{post.Id}", TestJson.Options);
        Assert.Null(after!.SeriesId);
        Assert.Null(after.SeriesOrder);                      // FK SET NULL만 썼다면 CK_Posts_Series_Pair 위반으로 삭제 자체가 실패했을 것이다
        Assert.NotEqual(post.Version, after.Version);        // 글 행이 바뀌었으므로 열려 있던 에디터 탭의 저장은 409가 된다

        using var gone = await client.GetAsync($"/api/series/{series.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await client.DeleteAsync($"/api/series/{series.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>제목·설명에 NUL(U+0000) 문자가 있으면 500이 아니라 해당 필드 키를 가진 400을 돌려주는지 검증한다.
    /// JSON은 유니코드 이스케이프로 NUL을 실어 나를 수 있으므로, MySQL이 NUL을 저장할 수 있어도 검증 단계에서 걸러야 한다.</summary>
    [Fact]
    public async Task Create_NulCharacter_Returns400_NotServerError()
    {
        using var client = await factory.CreateLoggedInClientAsync();

        using var titleNul = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("series-nul-title", "제목\0", "설명"));
        Assert.Equal(HttpStatusCode.BadRequest, titleNul.StatusCode);
        var titleErrors = (await titleNul.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options))!.Errors;
        Assert.Contains("title", titleErrors.Keys);

        using var descNul = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("series-nul-desc", "제목", "설명\0"));
        Assert.Equal(HttpStatusCode.BadRequest, descNul.StatusCode);
        var descErrors = (await descNul.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options))!.Errors;
        Assert.Contains("description", descErrors.Keys);
    }

    /// <summary>slug 끝에 개행(<c>\n</c>)이 있으면 400을 돌려주는지 검증한다. .NET <see cref="System.Text.RegularExpressions.Regex"/>의 <c>$</c>는
    /// 문자열 끝의 단일 개행 앞에서도 매칭되므로, 글·시리즈가 공유하는 <c>SlugRules</c>가 <c>\A</c>/<c>\z</c> 앵커로 DB CHECK(<c>REGEXP_LIKE</c>, ICU)와의 이 차이를 없앤다.</summary>
    [Fact]
    public async Task Create_SlugWithTrailingNewline_Returns400()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var res = await client.PostAsJsonAsync("/api/series", new UpsertSeriesRequest("series-newline\n", "제목", "설명"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var errors = (await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options))!.Errors;
        Assert.Contains("slug", errors.Keys);
    }

    /// <summary>같은 시리즈를 참조하는 글 저장 12건과 그 시리즈 삭제 1건을 동시에 실행해도 500이 전혀 나오지 않고,
    /// 글 저장은 201(삭제보다 먼저 커밋), 409(삭제와 경합), 또는 <c>seriesId</c> 키의 400(삭제가 먼저 커밋된 뒤 검증 — 느린 러너에서 실제로 관찰됨) 중 하나이며,
    /// 삭제가 204로 성공하면 그 시리즈를 참조하는 글이 하나도 남지 않는지 검증한다(삭제 트랜잭션의 <c>FOR UPDATE</c> 선점 + FK 위반의 409 방어를 함께 증명).</summary>
    [Fact]
    public async Task Delete_ConcurrentWithPostSaves_NeverReturns500_AndLeavesNoDanglingReference()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        var series = await CreateSeriesAsync(client, "series-race");

        // 글 저장 12건(고유 slug, seriesOrder 1)을 먼저 만들고, 인덱스 6에 삭제 요청을 끼워 넣는다(요청 사양대로).
        var tasks = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 12; i++)
        {
            var slug = $"race-post-{i}";
            tasks.Add(client.PostAsJsonAsync("/api/posts", new UpsertPostRequest(slug, slug, "", "본문", null, series.Id, 1, null)));
        }
        tasks.Insert(6, client.DeleteAsync($"/api/series/{series.Id}"));

        var responses = await Task.WhenAll(tasks);
        try
        {
            var deleteResponse = responses[6];
            Assert.True(deleteResponse.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Conflict,
                $"삭제 응답은 204 또는 409여야 하는데 {(int)deleteResponse.StatusCode}였다.");

            for (var i = 0; i < responses.Length; i++)
            {
                if (i == 6) continue; // 삭제 응답은 위에서 별도 검증
                if (responses[i].StatusCode == HttpStatusCode.BadRequest)
                {
                    // 삭제가 먼저 커밋된 뒤 이 저장의 검증이 돌면 "존재하지 않는 시리즈"가 정답이다. 다른 이유의 400은 허용하지 않는다.
                    var problem = await responses[i].Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options);
                    Assert.Equal(["seriesId"], problem!.Errors.Keys);
                    continue;
                }
                Assert.True(responses[i].StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                    $"글 저장 응답(인덱스 {i})은 201, 409 또는 seriesId 키의 400이어야 하는데 {(int)responses[i].StatusCode}였다.");
            }

            if (deleteResponse.StatusCode == HttpStatusCode.NoContent)
            {
                await using var scope = factory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(0, await db.Posts.CountAsync(p => p.SeriesId == series.Id));
                Assert.False(await db.Series.AnyAsync(s => s.Id == series.Id));
            }
        }
        finally
        {
            foreach (var res in responses) res.Dispose();
        }
    }
}
