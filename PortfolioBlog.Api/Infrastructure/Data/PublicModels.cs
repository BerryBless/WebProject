namespace PortfolioBlog.Api.Infrastructure.Data;

/// <summary>공개 태그 프로젝션.</summary>
/// <param name="Name">표시용 이름(원문 대소문자 유지).</param>
/// <param name="NormalizedName">유일성 키이자 공개 URL 키(트림 + 공백 축소 + NFC + 소문자).</param>
public sealed record PublicTag(string Name, string NormalizedName);

/// <summary>공개 목록 한 줄(본문 미포함).</summary>
/// <param name="Slug">공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="Summary">목록 발췌·meta description 공용 요약.</param>
/// <param name="CreatedAt">발행(생성) 시각이자 정렬 기준.</param>
/// <param name="Tags">이 글에 연결된 태그 목록(정규화 이름 순).</param>
public sealed record PublicPostSummary(string Slug, string Title, string Summary, DateTimeOffset CreatedAt, PublicTag[] Tags);

/// <summary>공개 목록의 한 쪽.</summary>
/// <param name="Items">이 쪽의 항목.</param>
/// <param name="Page">1부터 시작하는 쪽 번호.</param>
/// <param name="Total">필터에 맞는 전체 건수.</param>
public sealed record PublicPage<T>(IReadOnlyList<T> Items, int Page, int Total)
{
    /// <summary>마지막 쪽 번호(항목이 없어도 1).</summary>
    public int LastPage => Math.Max(1, (Total + PublicQueries.PageSize - 1) / PublicQueries.PageSize);
}

/// <summary>다른 글·시리즈로의 가벼운 링크(제목 + 슬러그만).</summary>
/// <param name="Slug">대상의 공개 URL 식별자.</param>
/// <param name="Title">대상의 표시용 제목.</param>
public sealed record PublicLink(string Slug, string Title);

/// <summary>글 상세 페이지의 메타데이터(본문 제외). 본문은 캐시 미스일 때만 <see cref="PublicQueries.GetContentAsync"/>로 따로 읽는다.</summary>
/// <param name="Id">글의 내부 식별자. 캐시 키 조립과 <see cref="PublicQueries.GetContentAsync"/> 호출에 쓰며, 공개 URL에는 노출하지 않는다.</param>
/// <param name="Version">낙관적 동시성 토큰(앱이 관리하는 행 버전 <c>Posts.Version</c>). 캐시 키로만 쓰고 응답에는 노출하지 않는다.</param>
/// <param name="Slug">공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="Summary">목록 발췌·meta description 공용 요약.</param>
/// <param name="CreatedAt">발행(생성) 시각.</param>
/// <param name="UpdatedAt">마지막 수정 시각.</param>
/// <param name="Tags">이 글에 연결된 태그 목록(정규화 이름 순).</param>
/// <param name="Series">소속 시리즈(없으면 <see langword="null"/>).</param>
/// <param name="Previous">같은 시리즈의 이전 글(없으면 <see langword="null"/>).</param>
/// <param name="Next">같은 시리즈의 다음 글(없으면 <see langword="null"/>).</param>
public sealed record PublicPostMeta(Guid Id, uint Version, string Slug, string Title, string Summary, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    PublicTag[] Tags, PublicLink? Series, PublicLink? Previous, PublicLink? Next);

/// <summary>글 본문과 그 본문의 버전(캐시 키용).</summary>
/// <param name="Markdown">본문 원문 마크다운(최대 200KB).</param>
/// <param name="Version">낙관적 동시성 토큰(앱이 관리하는 행 버전 <c>Posts.Version</c>). 캐시 키로만 쓰고 응답에는 노출하지 않는다.</param>
public sealed record PublicContent(string Markdown, uint Version);

/// <summary>시리즈 목록 한 줄.</summary>
/// <param name="Order">시리즈 안 순서(양수, 중복 허용).</param>
/// <param name="Slug">글의 공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="CreatedAt">글의 발행(생성) 시각.</param>
public sealed record PublicSeriesEntry(int Order, string Slug, string Title, DateTimeOffset CreatedAt);

/// <summary>시리즈 상세(소속 글 전체 목록 포함).</summary>
/// <param name="Slug">시리즈의 공개 URL 식별자.</param>
/// <param name="Title">시리즈 제목.</param>
/// <param name="Description">시리즈 소개.</param>
/// <param name="Posts">소속 글 목록((SeriesOrder, CreatedAt, Id) 순, 최대 <see cref="PublicQueries.SeriesMax"/>건).</param>
public sealed record PublicSeries(string Slug, string Title, string Description, IReadOnlyList<PublicSeriesEntry> Posts);

/// <summary>Atom 피드 한 항목.</summary>
/// <param name="Id">글의 내부 식별자(피드 항목의 안정적 <c>id</c> 값 조립에 쓴다).</param>
/// <param name="Slug">공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="Summary">목록 발췌·meta description 공용 요약.</param>
/// <param name="CreatedAt">발행(생성) 시각.</param>
/// <param name="UpdatedAt">마지막 수정 시각.</param>
public sealed record PublicFeedEntry(Guid Id, string Slug, string Title, string Summary, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>sitemap.xml에 실을 URL 종류별 키 목록.</summary>
/// <param name="Posts">글 슬러그와 마지막 수정 시각(최신순, 최대 <see cref="PublicQueries.SitemapMax"/>건).</param>
/// <param name="TagKeys">글이 1건 이상 있는 태그의 정규화 이름 목록(정규화 이름 순).</param>
/// <param name="SeriesSlugs">시리즈 슬러그 목록(슬러그 순).</param>
public sealed record PublicSitemap(IReadOnlyList<(string Slug, DateTimeOffset UpdatedAt)> Posts, IReadOnlyList<string> TagKeys, IReadOnlyList<string> SeriesSlugs);
