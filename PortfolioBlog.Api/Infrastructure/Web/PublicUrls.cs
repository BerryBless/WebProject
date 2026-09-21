namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>공개 사이트의 경로를 만든다. 절대 URL이 필요하면 <c>Site:PublicOrigin</c>을 앞에 붙인다(요청 Host를 쓰지 않는다).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스로 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 각 메서드가 결과 문자열 1개(<see cref="Tag"/>는 <see cref="Uri.EscapeDataString"/>의 중간 버퍼 포함)를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음.</description></item>
/// </list>
/// </remarks>
public static class PublicUrls
{
    /// <summary>글 상세 경로를 만든다.</summary>
    /// <param name="slug">글의 공개 URL 식별자.</param>
    /// <returns><c>/posts/{slug}</c>.</returns>
    /// <remarks>
    /// slug는 <c>[a-z0-9-]</c>뿐이라 인코딩이 필요 없다.
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드.</description></item>
    /// <item><description><b>Memory Allocation:</b> 문자열 연결 결과 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string Post(string slug) => "/posts/" + slug;

    /// <summary>시리즈 상세 경로를 만든다.</summary>
    /// <param name="slug">시리즈의 공개 URL 식별자.</param>
    /// <returns><c>/series/{slug}</c>.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드.</description></item>
    /// <item><description><b>Memory Allocation:</b> 문자열 연결 결과 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string Series(string slug) => "/series/" + slug;

    /// <summary>태그 쪽 경로. 정규화명을 경로 세그먼트 하나로 인코딩한다(<c>#</c>·<c>?</c>·<c>%</c>·공백·역슬래시 포함).</summary>
    /// <param name="normalizedName">태그의 정규화 이름(<see cref="Data.TagResolver.Normalize"/> 결과).</param>
    /// <returns>경로. 이름이 정확히 <c>"."</c> 또는 <c>".."</c>이면 <c>null</c> — 클라이언트와 서버의 점 세그먼트 정규화가 다른 쪽을 가리키게 만든다.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Uri.EscapeDataString"/>가 인코딩된 결과 문자열 1개를 할당한다(<c>null</c> 반환 경로는 할당 없음).</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    public static string? Tag(string normalizedName) =>
        normalizedName is "." or ".." ? null : "/tags/" + Uri.EscapeDataString(normalizedName);
}
