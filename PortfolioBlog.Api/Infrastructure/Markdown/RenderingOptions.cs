namespace PortfolioBlog.Api.Infrastructure.Markdown;

/// <summary>설정 섹션 <c>Rendering</c>. 마크다운 렌더링의 CPU·메모리 예산.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 프로퍼티는 public set을 가지지만, 싱글턴으로 등록되어 프로그램 시작 후 수정되지 않는다. 읽기만 Thread-safe.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로퍼티는 전부 값 형식(<c>int</c>)이라 인스턴스 자체 외에 추가 힙 할당이 없다.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
/// </list>
/// </remarks>
public sealed class RenderingOptions
{
    /// <summary>설정 섹션 이름.</summary>
    public const string SectionName = "Rendering";

    /// <summary>프로세스 전체에서 동시에 도는 렌더 수(미리보기·글 저장·공개 페이지 합산). 1~64.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>슬롯을 기다리는 최대 시간(밀리초). 넘으면 503. 1~60000.</summary>
    public int QueueTimeoutMs { get; set; } = 5000;

    /// <summary>렌더 결과 캐시의 메모리 상한(MB, HTML 문자열 기준). 1~1024.</summary>
    public int CacheMegabytes { get; set; } = 64;
}
