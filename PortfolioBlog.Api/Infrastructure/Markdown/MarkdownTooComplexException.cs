namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>마크다운 구조가 Markdig 1.4.0의 중첩 한도(128단계 — 대괄호·인용·강조 등을 그만큼 깊이 겹치면)를 넘어
/// <see cref="MarkdownRenderer.Render"/>가 파싱·렌더링을 끝낼 수 없을 때 던진다. 원인은 Markdig의 일반 <see cref="ArgumentException"/>이며
/// <see cref="Exception.InnerException"/>으로 보존한다. 호출부(글 저장 API)는 이 예외를 필드 키(<c>contentMarkdown</c>)가 있는 400으로 바꿔야 한다 — 500이 되어서는 안 된다.</summary>
/// <param name="message">사용자·로그에 보여줄 한국어 메시지.</param>
/// <param name="inner">Markdig가 던진 원본 <see cref="ArgumentException"/>.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 예외 인스턴스이며 공유 가변 상태가 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> 예외 인스턴스 1개(+ 스택 트레이스). 예외 경로에서만 할당되고 정상 렌더링 경로의 비용은 0이다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음. 예외 타입 자체는 코드를 실행하지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class MarkdownTooComplexException(string message, Exception inner) : Exception(message, inner);
