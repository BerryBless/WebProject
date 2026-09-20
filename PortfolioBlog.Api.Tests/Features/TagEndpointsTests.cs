using System.Net;
using System.Net.Http.Json;
using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Tests.Infrastructure;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary><c>/api/tags</c> 목록·삭제 엔드포인트를 실제 PostgreSQL 컨테이너로 검증한다.</summary>
/// <param name="factory">컬렉션이 공유하는 컨테이너를 바탕으로 클래스 전용 DB를 갖는 <see cref="ApiFactory"/> 클래스 픽스처.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. <see cref="ApiFactory"/>가 호스팅하는 인메모리 TestServer가
/// 실제 PostgreSQL 컨테이너에 TCP로 접속하므로 DB I/O는 실제 네트워크 왕복을 수반한다.</description></item>
/// <item><description><b>Memory Policy:</b> 팩토리는 <see cref="IClassFixture{TFixture}"/>로 클래스 단위 1회 생성·공유된다.
/// 각 테스트 메서드는 로그인 클라이언트를 자체적으로 만들고 <c>using</c>으로 해제한다.</description></item>
/// <item><description><b>Concurrency:</b> 팩토리·HttpClient는 Thread-safe하나, 테스트 간에는 클래스별 고유 DB로 격리되어 데이터 간섭이 없다.</description></item>
/// <item><description><b>Blocking:</b> 모든 HTTP 접근은 <c>await</c>로 비동기 대기하며 동기 블로킹이 없다.</description></item>
/// </list>
/// </remarks>
[Collection("postgres")]
public sealed class TagEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>목록이 <c>NormalizedName</c> 순으로 정렬되고 각 태그의 글 수(<c>PostCount</c>)가 정확하며,
    /// 태그 삭제가 글 행은 남기고 <c>PostTag</c> 링크만 지우는지(이미 지운 태그의 재삭제는 404) 검증한다.</summary>
    [Fact]
    public async Task List_ReturnsTagsSortedByNormalizedName_WithPostCounts_AndDeleteRemovesLinksOnly()
    {
        using var client = await factory.CreateLoggedInClientAsync();
        using var a = await client.PostAsJsonAsync("/api/posts", new UpsertPostRequest("tag-post-a", "A", "", "본문", ["Zebra", "apple"], null, null, null));
        using var b = await client.PostAsJsonAsync("/api/posts", new UpsertPostRequest("tag-post-b", "B", "", "본문", ["apple"], null, null, null));
        var postA = (await a.Content.ReadFromJsonAsync<PostDetailDto>(TestJson.Options))!;

        var tags = (await client.GetFromJsonAsync<TagDto[]>("/api/tags", TestJson.Options))!;
        Assert.Equal(["apple", "zebra"], tags.Select(t => t.NormalizedName));
        Assert.Equal("Zebra", tags[1].Name);
        Assert.Equal([2, 1], tags.Select(t => t.PostCount));

        using var deleted = await client.DeleteAsync($"/api/tags/{tags[0].Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var after = await client.GetFromJsonAsync<PostDetailDto>($"/api/posts/{postA.Id}", TestJson.Options);
        Assert.Equal(["Zebra"], after!.Tags);                 // 글은 남고 링크만 사라진다
        using var again = await client.DeleteAsync($"/api/tags/{tags[0].Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }
}
