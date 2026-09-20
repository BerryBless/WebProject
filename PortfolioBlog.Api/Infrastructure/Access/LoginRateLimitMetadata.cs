namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary>로그인 엔드포인트에 <c>WithMetadata</c>로 붙이는 표식. 속도 제한 파티션 선택기가 요청 경로 문자열 대신
/// 실제로 선택된 엔드포인트에 이 표식이 있는지로 로그인 요청을 식별한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 필드가 없는 불변 마커 타입이며, 라우팅이 엔드포인트 메타데이터 컬렉션에 담아 요청 스레드 간에 읽기 전용으로 공유한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 앱 시작 시 라우트 등록 단계에서 인스턴스 1개만 할당된다(엔드포인트당 1회). 요청마다 새로 만들지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음(데이터 없는 마커 타입).</description></item>
/// </list>
/// 경로 문자열 비교(<c>ctx.Request.Path.Equals(...)</c>)는 라우팅이 끝 슬래시·대소문자를 정규화해 같은 엔드포인트로 매칭하는 변형(<c>/login/</c>, <c>/LOGIN</c> 등)을 다시 구분해 버려
/// 속도 제한을 우회당한다. WebApplication은 <c>UseRouting</c>이 <c>UseRateLimiter</c>보다 먼저 실행되어 <c>ctx.GetEndpoint()</c>가 이미 선택된 엔드포인트를 가리키므로,
/// 메타데이터로 판정하면 실제로 어떤 핸들러가 실행될지와 정확히 일치한다.
/// </remarks>
public sealed class LoginRateLimitMetadata;
