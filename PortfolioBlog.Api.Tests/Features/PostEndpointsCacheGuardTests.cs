using PortfolioBlog.Api.Contracts;
using PortfolioBlog.Api.Features.Posts;

namespace PortfolioBlog.Api.Tests.Features;

/// <summary><see cref="PostEndpoints.CanCacheRenderedResult"/>가 저장 뒤 재조회 경쟁(A 커밋 → B가 A의 버전을 읽고 렌더·커밋 → A의 재조회가 B의 xmin을 읽는 경쟁)에서
/// 남의 본문을 자기 키에 선채움하지 않는지 단위 테스트로 검증한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행되며 DB·Docker·HTTP를 전혀 쓰지 않는 순수 단위 테스트다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트 메서드마다 독립된 <see cref="PostDetailDto"/> 인스턴스를 만든다. 공유 상태가 없다.</description></item>
/// <item><description><b>Concurrency:</b> 순수 함수만 호출하므로 다른 테스트 클래스와 병렬로 실행해도 안전하다.</description></item>
/// </list>
/// </remarks>
public sealed class PostEndpointsCacheGuardTests
{
    private static PostDetailDto Dto(string content) =>
        new(Guid.NewGuid(), "slug", "제목", "요약", content, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);

    /// <summary>재조회한 DTO의 본문이 이 요청이 렌더링한 본문과 서수 비교로 같으면 캐시해도 안전하다.</summary>
    [Fact]
    public void SameContent_ReturnsTrue() => Assert.True(PostEndpoints.CanCacheRenderedResult(Dto("본문"), "본문"));

    /// <summary>재조회한 DTO의 본문이 이 요청의 본문과 다르면(경쟁으로 남의 것을 읽었으면) 캐시하지 않는다.</summary>
    [Fact]
    public void DifferentContent_ReturnsFalse() => Assert.False(PostEndpoints.CanCacheRenderedResult(Dto("B의 본문"), "A의 본문"));

    /// <summary>재조회가 <see langword="null"/>(경쟁으로 이미 삭제됨 등)이면 캐시하지 않는다.</summary>
    [Fact]
    public void NullDto_ReturnsFalse() => Assert.False(PostEndpoints.CanCacheRenderedResult(null, "본문"));
}
