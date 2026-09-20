namespace PortfolioBlog.Api.Contracts;

/// <summary>미리보기 요청. 누락을 필드별 400으로 돌려주려고 nullable로 받는다.</summary>
/// <param name="Markdown">에디터의 현재 본문(UTF-8 204,800바이트 이하).</param>
public sealed record PreviewRequest(string? Markdown);

/// <summary>미리보기 응답.</summary>
/// <param name="Html">정제된 HTML. 관리 SPA는 이것을 React DOM에 직접 넣지 않고 <c>sandbox=""</c> iframe의 <c>srcdoc</c>에만 넣는다.</param>
public sealed record PreviewResponse(string Html);
