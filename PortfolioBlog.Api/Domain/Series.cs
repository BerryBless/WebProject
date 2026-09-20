namespace PortfolioBlog.Api.Domain;

/// <summary>연재 묶음. 글은 0~1개 시리즈에 속한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="Infrastructure.Data.AppDbContext"/> 스코프 안에서 단일 스레드로만 사용한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 할당 1개 + <see cref="Posts"/> 컬렉션.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 순수 데이터 컨테이너이며 자체 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public sealed class Series
{
    /// <summary>기본 키. 시간 정렬 가능한 UUIDv7로 생성한다.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>공개 URL 식별자. <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, 최대 100자, 생성 후 불변.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>시리즈 제목(최대 200자, 공백만으로는 불가).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>시리즈 소개(최대 1000자).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>이 시리즈에 속한 글 목록. 지연 로드 없이 명시적 <c>Include</c>로만 채워진다.</summary>
    public List<Post> Posts { get; } = new();
}
