using System.Net;
using Microsoft.AspNetCore.Diagnostics;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>본문 없는 오류 응답의 본문을 경로에 따라 쓴다: 기계가 읽는 표면은 ProblemDetails, 사람이 보는 공개 표면은 고정 HTML.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 무상태 정적 클래스. <see cref="MachinePrefixes"/>는 시작 시 1회 초기화되는 불변 배열이라 여러 요청 스레드가 동시에 읽어도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> <c>/api</c> 등 경로는 <see cref="ProblemDetailsContext"/> 1개를 할당한다. 그 밖의 경로는 <see cref="Html"/>가 반환하는 보간 문자열 1개를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> 비동기 Non-blocking. 응답 본문 쓰기를 <c>await</c>로 대기한다.</description></item>
/// </list>
/// </remarks>
public static class ErrorResponses
{
    private static readonly PathString[] MachinePrefixes = [new("/api"), new("/attachments"), new("/health"), new("/openapi")];

    /// <summary><c>UseStatusCodePages</c> 콜백.</summary>
    /// <param name="context">상태 코드 페이지 컨텍스트(응답에 상태 코드만 설정되고 본문은 비어 있는 상태).</param>
    /// <returns><see cref="WriteAsync"/>가 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드로 공유 상태가 없다. 매 호출이 독립적이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 이 메서드 자체는 추가 할당이 없다. <see cref="WriteAsync"/>로 그대로 위임한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking.</description></item>
    /// </list>
    /// </remarks>
    public static Task HandleStatusCodeAsync(StatusCodeContext context) =>
        WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode);

    /// <summary>상태 코드에 맞는 본문을 쓴다. 요청의 어떤 값도 본문에 넣지 않는다(반사 없음).</summary>
    /// <param name="http">현재 HTTP 요청 컨텍스트.</param>
    /// <param name="status">응답할 HTTP 상태 코드.</param>
    /// <returns>본문 쓰기가 완료되면 끝나는 작업.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. <paramref name="http"/>는 요청마다 새로 전달되며 이 메서드가 공유 상태를 만들지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <c>/api</c>·<c>/attachments</c>·<c>/health</c>·<c>/openapi</c> 경로는 <see cref="IProblemDetailsService"/>가 만드는 직렬화 버퍼를, 그 밖의 경로는 <see cref="Html"/>의 보간 문자열 1개를 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기 Non-blocking. <see cref="IProblemDetailsService"/> DI 조회는 동기이지만 즉시 반환되고, 실제 쓰기는 <c>await</c>한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task WriteAsync(HttpContext http, int status)
    {
        if (MachinePrefixes.Any(p => http.Request.Path.StartsWithSegments(p)))
        {
            await http.RequestServices.GetRequiredService<IProblemDetailsService>()
                .TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = { Status = status } });
            return;
        }
        http.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsHead(http.Request.Method)) return;
        await http.Response.WriteAsync(Html(status));
    }

    // 상태 코드별 고정 안내 문구를 HTML로 감싼다. status는 정수, message는 아래 switch의 상수뿐이라 사용자 입력이 섞일 경로가 없다.
    private static string Html(int status)
    {
        var message = status switch
        {
            400 => "요청을 이해할 수 없습니다.",
            404 => "페이지를 찾을 수 없습니다.",
            405 => "허용되지 않는 요청 방식입니다.",
            429 => "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.",
            503 => "서버가 바쁩니다. 잠시 후 다시 시도해 주세요.",
            _ => "요청을 처리하지 못했습니다.",
        };
        // status는 정수, message는 위 상수뿐이다. HtmlEncode는 "나중에 누가 입력값을 넣어도" 안전하도록 남긴 이중 방어다.
        return $"""
            <!doctype html>
            <html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex"><title>{status}</title><link rel="stylesheet" href="/css/site.css"></head>
            <body><main class="error-page"><h1>{status}</h1><p>{WebUtility.HtmlEncode(message)}</p><p><a href="/">처음으로</a></p></main></body></html>
            """;
    }
}
