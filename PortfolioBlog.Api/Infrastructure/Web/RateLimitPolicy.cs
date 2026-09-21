namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>엔드포인트에 붙여 어떤 제한기 묶음을 적용할지 고르는 정책 이름.</summary>
public enum RateLimitPolicy
{
    /// <summary>비밀번호 로그인: IP별·전역 고정 창 + 해시 검증 동시 실행 제한.</summary>
    Login,

    /// <summary>마크다운 미리보기: 전역 고정 창 + 렌더링 동시 실행 제한.</summary>
    Preview,

    /// <summary>공개 HTML 페이지·Atom·sitemap: IP별 고정 창. <see cref="Search"/> 요청도 이 창에 함께 계산된다.</summary>
    PublicPage,

    /// <summary>첨부 GET·<c>/health</c>·<c>robots.txt</c>·<c>highlight.css</c>: IP별 고정 창(페이지보다 넉넉하다).</summary>
    PublicAsset,

    /// <summary>공개 검색: IP별 고정 창 + 전역 동시 실행 제한, 그리고 <see cref="PublicPage"/> 창.</summary>
    Search,

    /// <summary>첨부 업로드: 전역 고정 창 + 동시 실행 제한(메타데이터 제거·해시가 동기 I/O다).</summary>
    Upload,
}

/// <summary>엔드포인트 메타데이터 마커. 제한기는 <b>원시 경로가 아니라</b> 라우팅이 선택한 엔드포인트의 이 메타데이터로 파티션을 고른다
/// (경로 문자열 비교는 끝 슬래시·대소문자 변형으로 우회된 전례가 있다).</summary>
/// <param name="Policy">이 엔드포인트에 적용할 속도 제한 정책.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record이며, 라우팅이 엔드포인트 메타데이터 컬렉션에 담아 요청 스레드 간에 읽기 전용으로 공유한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 앱 시작 시 라우트 등록 단계에서 인스턴스 1개만 할당된다(엔드포인트당 1회). 요청마다 새로 만들지 않는다.</description></item>
/// <item><description><b>Blocking:</b> 해당 없음(데이터만 담는 불변 record).</description></item>
/// </list>
/// 경로 문자열 비교(<c>ctx.Request.Path.Equals(...)</c>)는 라우팅이 끝 슬래시·대소문자를 정규화해 같은 엔드포인트로 매칭하는 변형(<c>/login/</c>, <c>/LOGIN</c> 등)을 다시 구분해 버려
/// 속도 제한을 우회당한다. <c>WebApplication</c>은 라우팅(엔드포인트 선택)이 <see cref="Microsoft.AspNetCore.RateLimiting.RateLimiterApplicationBuilderExtensions.UseRateLimiter"/>보다 먼저 실행되어
/// <c>ctx.GetEndpoint()</c>가 이미 선택된 엔드포인트를 가리키므로, 메타데이터로 판정하면 실제로 어떤 핸들러가 실행될지와 정확히 일치한다.
/// </remarks>
public sealed record RateLimitMetadata(RateLimitPolicy Policy);
