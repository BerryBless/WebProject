namespace PortfolioBlog.Api.Domain;

/// <summary>블로그 글. 발행 상태가 없으며 저장 즉시 공개된다. <see cref="CreatedAt"/>이 발행일이자 정렬 기준이다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. <see cref="Infrastructure.Data.AppDbContext"/> 스코프 안에서 단일 스레드로만 사용한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 할당 1개 + <see cref="PostTags"/> 컬렉션. <see cref="ContentMarkdown"/>은 최대 200KB 문자열을 보유할 수 있으므로 목록 조회에는 프로젝션을 쓴다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 순수 데이터 컨테이너이며 자체 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public sealed class Post
{
    /// <summary>기본 키. 시간 정렬 가능한 UUIDv7로 생성한다.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>공개 URL 식별자. <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, 최대 100자, 생성 후 불변.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>글 제목(최대 200자, 공백만으로는 불가).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>목록 발췌·meta description·OG·Atom 공용 요약(최대 300자).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>마크다운 원문(UTF-8 200KB 이하). HTML은 저장하지 않고 요청 시 렌더링한다.</summary>
    public string ContentMarkdown { get; set; } = string.Empty;

    /// <summary>소속 시리즈 외래키(없으면 시리즈 미소속).</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>탐색 속성. 지연 로드 없이 명시적 <c>Include</c>로만 채워진다.</summary>
    public Series? Series { get; set; }

    /// <summary>시리즈 안 순서(양수, 중복 허용). <see cref="SeriesId"/>와 함께 있거나 함께 없어야 한다(DB CHECK).</summary>
    public int? SeriesOrder { get; set; }

    /// <summary>발행(생성) 시각이자 정렬 기준. <see cref="Infrastructure.Data.DbClock"/>으로 설정한다.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>마지막 수정 시각. <see cref="Infrastructure.Data.DbClock"/>으로 설정한다.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>낙관적 동시성 토큰. PostgreSQL 시스템 컬럼 xmin(행을 마지막으로 쓴 트랜잭션 ID)에 매핑된다.</summary>
    public uint Version { get; set; }

    /// <summary>이 글에 연결된 태그 연결 엔티티 목록(다대다 조인).</summary>
    public List<PostTag> PostTags { get; } = new();
}
