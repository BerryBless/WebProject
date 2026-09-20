using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="SiteOptions.HostOf"/>가 엄격한 origin 형식(스킴 http/https · 경로 없음 · 사용자 정보 없음)만 받아들이는지 검증한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 각 테스트는 정적 메서드를 독립적으로 호출하므로 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> 호출로 케이스마다 일시 객체 할당.</description></item>
/// <item><description><b>Blocking:</b> 모든 테스트는 즉시 반환한다. I/O 또는 비동기 호출이 없다.</description></item>
/// </list>
/// </remarks>
public sealed class SiteOptionsTests
{
    /// <summary>
    /// 스킴(http/https) + 호스트[:포트]만 있고 경로·쿼리·사용자 정보가 없는 origin은 호스트 이름을 반환한다.
    /// </summary>
    /// <param name="origin">유효한 origin 문자열.</param>
    /// <param name="expectedHost">기대하는 추출 호스트 이름.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Theory의 각 케이스는 독립적으로 <see cref="SiteOptions.HostOf"/>를 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Uri"/> 파싱에 따른 일시 객체 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("https://admin.test", "admin.test")]
    [InlineData("https://localhost:7198", "localhost")]
    [InlineData("http://localhost:5055", "localhost")]
    public void HostOf_ValidOrigin_ReturnsHost(string origin, string expectedHost) =>
        Assert.Equal(expectedHost, SiteOptions.HostOf(origin));

    /// <summary>
    /// 빈 문자열, 끝 슬래시, 경로, 쿼리, 사용자 정보(userinfo), 미지원 스킴(ftp), 상대 경로는
    /// 전부 <see cref="FormatException"/>으로 거부되는지 검증한다.
    /// 사용자 정보 케이스는 결함 회귀 테스트다: 이전에는 <c>https://user@host</c>가 <c>uri.GetLeftPart(UriPartial.Authority)</c>에
    /// 사용자 정보를 포함시켜 원본 문자열과 그대로 일치해 형식 검사를 통과했다.
    /// </summary>
    /// <param name="origin">거부되어야 하는 잘못된 origin 문자열.</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Theory의 각 케이스는 독립적으로 <see cref="SiteOptions.HostOf"/>를 호출한다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 실패 시 <see cref="FormatException"/> 인스턴스 1개 할당.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(예외 발생).</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("https://admin.test/")]
    [InlineData("https://admin.test/path")]
    [InlineData("https://admin.test?query=1")]
    [InlineData("https://user@admin.test")]
    [InlineData("ftp://admin.test")]
    [InlineData("admin.test")]
    public void HostOf_InvalidOrigin_ThrowsFormatException(string origin) =>
        Assert.Throws<FormatException>(() => SiteOptions.HostOf(origin));
}
