namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>컨테이너 헬스체크용 CLI 경로(<c>dotnet PortfolioBlog.Api.dll healthcheck</c>). 같은 컨테이너의 <c>/health</c>를 한 번 부르고 종료 코드로 답한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 공유 상태가 없다. 헬스체크마다 새 프로세스로 1회 실행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="HttpClient"/> 1개와 요청·응답 객체. 웹 호스트는 만들지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 비동기. 최대 <see cref="TimeoutSeconds"/>초 뒤에는 반드시 끝난다.</description></item>
/// </list>
/// 이 명령이 필요한 이유: 운영 이미지(chiseled)에는 셸도 curl도 없고, 호스트 필터가 설정된 두 호스트만 받으므로
/// <c>Host: localhost</c>로는 본문 없는 400이 온다(스펙 3.10). 그래서 앱 자신이 공개 호스트 이름을 Host 헤더에 실어 부른다.
/// </remarks>
public static class HealthCheckCommand
{
    /// <summary>CLI 인수 이름.</summary>
    public const string Name = "healthcheck";

    /// <summary>요청 하나의 시간 상한(초). compose의 healthcheck <c>timeout</c>(5초)보다 짧아야 Docker가 아니라 이 명령이 먼저 실패를 보고한다.</summary>
    public const int TimeoutSeconds = 3;

    /// <summary><c>/health</c>를 부르고 2xx면 0, 그 밖의 모든 경우(설정 오류·연결 실패·시간 초과·비 2xx)는 1을 돌려준다.</summary>
    /// <param name="env">환경변수 조회 함수. <c>Site__PublicOrigin</c>(필수)과 <c>ASPNETCORE_HTTP_PORTS</c>(없으면 8080)를 읽는다.</param>
    /// <param name="handler">HTTP 전송 계층. 호출자가 소유권을 넘긴다 — 이 메서드가 해제한다.</param>
    /// <param name="error">실패 이유를 한 줄로 쓸 곳(<c>docker inspect</c>의 헬스 로그에 남는다). 비밀값은 쓰지 않는다.</param>
    /// <returns>프로세스 종료 코드: 0(정상) 또는 1.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 독립적이다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 요청·응답 객체와 URI 문자열. <paramref name="handler"/>는 반환 전에 해제된다.</description></item>
    /// <item><description><b>Blocking:</b> 비동기(Non-blocking). 예외를 밖으로 던지지 않는다 — 헬스체크의 실패는 종료 코드로만 말한다.</description></item>
    /// </list>
    /// </remarks>
    public static async Task<int> RunAsync(Func<string, string?> env, HttpMessageHandler handler, TextWriter error)
    {
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        try
        {
            if (!Uri.TryCreate(env("Site__PublicOrigin"), UriKind.Absolute, out var origin))
            {
                error.WriteLine("healthcheck: Site__PublicOrigin 이 없거나 절대 URI가 아닙니다.");
                return 1;
            }
            // ASPNETCORE_HTTP_PORTS는 "8080;8081"처럼 여러 개일 수 있다. 첫 번째만 쓴다.
            var port = (env("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "8080";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/health");
            request.Headers.Host = origin.Host;
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return 0;
            error.WriteLine($"healthcheck: /health 가 {(int)response.StatusCode} 을(를) 돌려줬습니다.");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            error.WriteLine($"healthcheck: {ex.GetType().Name}");
            return 1;
        }
    }
}
