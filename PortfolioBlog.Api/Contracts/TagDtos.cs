namespace PortfolioBlog.Api.Contracts;

/// <summary>태그 요약. <c>NormalizedName</c>은 공개 URL <c>/tags/{tag}</c>의 키다(Plan 2).</summary>
/// <param name="Id">태그 고유 식별자.</param>
/// <param name="Name">표시용 이름(원문 대소문자 유지).</param>
/// <param name="NormalizedName">트림 + 연속 공백 1개 + NFC + 소문자로 정규화된 이름. 정렬·공개 URL 키로 쓰인다.</param>
/// <param name="PostCount">이 태그가 붙은 글 수.</param>
public sealed record TagDto(Guid Id, string Name, string NormalizedName, int PostCount);
