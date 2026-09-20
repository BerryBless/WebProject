using System.Text.RegularExpressions;

namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>마크다운의 링크·이미지 URL을 허용 목록으로 판정한다. 통과하지 못한 URL은 렌더러가 링크를 풀어 텍스트만 남긴다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 함수.</description></item>
/// <item><description><b>Memory Allocation:</b> 절대 URL 판정 시 <see cref="Uri"/> 1개. 그 외 경로는 할당 없음.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 네트워크·DNS 조회 없음.</description></item>
/// </list>
/// 차단 목록이 아니라 허용 목록이다: 브라우저마다 다른 URL 정규화(공백·제어문자 제거, 백슬래시를 슬래시로 취급)를 흉내 내지 않고,
/// 그런 문자가 하나라도 있으면 거부한다. 이미지를 자체 첨부로 한정하는 것은 CSP <c>img-src 'self'</c>와 이중 방어이자 추적 픽셀·핫링크 차단이다.
/// </remarks>
public static partial class UrlPolicy
{
    // \A…\z: .NET의 $는 끝의 개행 앞에서도 매칭된다(선행 계획 정오표 규칙 2). 파일명 세그먼트는 '/', '?', '#', '\', 공백·제어문자를 뺀 1자 이상.
    [GeneratedRegex(@"\A/attachments/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/[^/?#\\\s\p{Cc}]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPath();

    public static bool IsAllowedLink(string? url)
    {
        if (!IsClean(url)) return false;
        if (url![0] == '#') return true;
        if (url[0] == '/') return url.Length == 1 || url[1] != '/'; // 루트 상대. "//host"(프로토콜 상대)는 거부
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
    }

    public static bool IsAllowedImage(string? url) =>
        IsClean(url) && !url!.Contains("..", StringComparison.Ordinal) && !url.Contains("%2e", StringComparison.OrdinalIgnoreCase) && AttachmentPath().IsMatch(url);

    /// <summary>비어 있지 않고 공백·제어문자·백슬래시가 없는가.</summary>
    private static bool IsClean(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        foreach (var c in url)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') return false;
        }
        return true;
    }
}
