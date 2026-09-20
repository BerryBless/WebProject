namespace PortfolioBlog.Api.Contracts;

/// <summary>시리즈 목록·요약. 소속 글 수만 담고 글 목록 자체는 담지 않는다.</summary>
/// <param name="Id">시리즈 고유 식별자.</param>
/// <param name="Slug">공개 URL 식별자.</param>
/// <param name="Title">시리즈 제목.</param>
/// <param name="Description">시리즈 소개.</param>
/// <param name="PostCount">이 시리즈에 속한 글 수.</param>
public sealed record SeriesDto(Guid Id, string Slug, string Title, string Description, int PostCount);

/// <summary>시리즈 상세에 실리는 소속 글 1건. 글 상세(본문 등)는 담지 않는다.</summary>
/// <param name="Id">글 고유 식별자.</param>
/// <param name="Slug">글의 공개 URL 식별자.</param>
/// <param name="Title">글 제목.</param>
/// <param name="SeriesOrder">시리즈 안 순서.</param>
public sealed record SeriesPostDto(Guid Id, string Slug, string Title, int SeriesOrder);

/// <summary>시리즈 상세. 시리즈 정보와 <c>SeriesOrder</c>, 그다음 <c>CreatedAt</c> 순으로 정렬된 소속 글 목록을 함께 담는다.</summary>
/// <param name="Series">시리즈 요약 정보.</param>
/// <param name="Posts">순서대로 정렬된 소속 글 목록.</param>
public sealed record SeriesDetailDto(SeriesDto Series, SeriesPostDto[] Posts);

/// <summary>생성·수정 공용 요청. slug는 글과 같은 규칙이며 생성 후 불변이다(공개 URL <c>/series/{slug}</c>).</summary>
/// <param name="Slug">공개 URL 식별자(생성 후 불변).</param>
/// <param name="Title">시리즈 제목.</param>
/// <param name="Description">시리즈 소개.</param>
public sealed record UpsertSeriesRequest(string? Slug, string? Title, string? Description);
