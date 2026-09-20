namespace PortfolioBlog.Api.Contracts;

/// <summary>목록용 글 요약. 본문을 싣지 않는다(목록 한 페이지가 200KB × N이 되지 않게).</summary>
/// <param name="Id">글 고유 식별자.</param>
/// <param name="Slug">공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="Summary">목록 발췌용 요약.</param>
/// <param name="Tags">정규화 이름 순으로 정렬된 태그 표시명 목록.</param>
/// <param name="SeriesId">소속 시리즈(없으면 <c>null</c>).</param>
/// <param name="SeriesOrder">시리즈 안 순서(없으면 <c>null</c>).</param>
/// <param name="CreatedAt">발행(생성) 시각.</param>
/// <param name="UpdatedAt">마지막 수정 시각.</param>
/// <param name="Version">낙관적 동시성 토큰(DB xmin).</param>
public sealed record PostSummaryDto(Guid Id, string Slug, string Title, string Summary, string[] Tags,
    Guid? SeriesId, int? SeriesOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

/// <summary>글 상세. 본문 전체를 포함한다.</summary>
/// <param name="Id">글 고유 식별자.</param>
/// <param name="Slug">공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="Summary">목록 발췌·meta description·OG·Atom 공용 요약.</param>
/// <param name="ContentMarkdown">마크다운 원문.</param>
/// <param name="Tags">정규화 이름 순으로 정렬된 태그 표시명 목록.</param>
/// <param name="SeriesId">소속 시리즈(없으면 <c>null</c>).</param>
/// <param name="SeriesOrder">시리즈 안 순서(없으면 <c>null</c>).</param>
/// <param name="CreatedAt">발행(생성) 시각.</param>
/// <param name="UpdatedAt">마지막 수정 시각.</param>
/// <param name="Version">낙관적 동시성 토큰(DB xmin).</param>
public sealed record PostDetailDto(Guid Id, string Slug, string Title, string Summary, string ContentMarkdown, string[] Tags,
    Guid? SeriesId, int? SeriesOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version);

/// <summary>페이지 단위 글 목록 응답.</summary>
/// <param name="Items">현재 페이지의 글 요약 목록.</param>
/// <param name="Total">필터 적용 후 전체 건수.</param>
public sealed record PagedPostsDto(PostSummaryDto[] Items, int Total);

/// <summary>생성·수정 공용 요청. 필수 필드도 nullable로 받아 누락을 바인딩 예외가 아닌 필드별 400으로 돌려준다.
/// <c>TagNames</c>·<c>SeriesId</c>·<c>SeriesOrder</c>는 연결을 통째로 교체한다(null = 없음). <c>Version</c>은 수정에서만 필수.</summary>
/// <param name="Slug">공개 URL 식별자(생성 후 불변).</param>
/// <param name="Title">글 제목.</param>
/// <param name="Summary">목록 발췌·meta description·OG·Atom 공용 요약.</param>
/// <param name="ContentMarkdown">마크다운 원문.</param>
/// <param name="TagNames">이 글에 연결할 태그 이름 전체 목록(교체 의미론).</param>
/// <param name="SeriesId">소속 시리즈(없으면 <c>null</c>).</param>
/// <param name="SeriesOrder">시리즈 안 순서(없으면 <c>null</c>).</param>
/// <param name="Version">수정·삭제 시 조회 때 받은 낙관적 동시성 토큰(생성 시에는 무시됨).</param>
public sealed record UpsertPostRequest(string? Slug, string? Title, string? Summary, string? ContentMarkdown,
    string[]? TagNames, Guid? SeriesId, int? SeriesOrder, uint? Version);
