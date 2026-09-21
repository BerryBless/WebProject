namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>설정 섹션 <c>Site</c>. 절대 URL 생성과 관리 호스트 판정의 고정 기준(요청 Host 헤더를 신뢰하지 않기 위함).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 프로퍼티는 public set을 가지지만, 싱글턴으로 등록되어 프로그램 시작 후 수정되지 않는다. 읽기만 Thread-safe.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로퍼티 할당은 문자열 참조만 변경. <see cref="HostOf"/>는 <see cref="Uri.TryCreate"/> 호출로 일시 객체 할당.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class SiteOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Site";

    /// <summary>예: <c>https://blog.example.com</c>. Plan 2의 canonical·Atom·sitemap이 쓴다.</summary>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>예: <c>https://admin.example.com</c>. <c>/api</c>의 Host·Origin 검사 기준.</summary>
    public string AdminOrigin { get; set; } = string.Empty;

    /// <summary>사이트 이름. <c>&lt;title&gt;</c>·머리글·Atom 피드 제목에 쓴다. 비울 수 없다.</summary>
    public string Title { get; set; } = "Blog";

    /// <summary>사이트 한 줄 소개. 첫 쪽의 meta description과 Atom subtitle. 비어 있으면 생략한다.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Atom 피드의 작성자 이름. 비어 있으면 <see cref="Title"/>을 쓴다.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>origin에서 호스트 이름만 뽑는다(포트·스킴 제외). 형식이 틀리면 <see cref="FormatException"/>.</summary>
    /// <param name="origin">검증할 origin 문자열(예: https://admin.example.com).</param>
    /// <returns>추출한 호스트 이름(예: admin.example.com).</returns>
    /// <exception cref="FormatException">origin 형식이 유효하지 않을 때.</exception>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Uri.TryCreate"/> 호출로 일시 객체 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string HostOf(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/"
            && uri.UserInfo.Length == 0 && origin == uri.GetLeftPart(UriPartial.Authority)
            ? uri.Host
            : throw new FormatException($"origin 형식이 아닙니다: '{origin}'. 'https://host[:port]' 형태여야 하며 경로·끝 슬래시·사용자 정보를 붙이지 않습니다.");
}
