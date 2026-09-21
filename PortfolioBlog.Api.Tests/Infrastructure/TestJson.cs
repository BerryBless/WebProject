using System.Text.Encodings.Web;
using System.Text.Json;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>테스트 전역에서 공유하는 <see cref="JsonSerializerOptions"/> 상수.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="JsonSerializerOptions"/>는 초기화 후(첫 사용 시) 불변으로 동작하도록 설계되어 여러 스레드에서 동시에 읽을 수 있다.</description></item>
/// <item><description><b>Memory Allocation:</b> 프로세스 생애주기 동안 인스턴스 2개만 할당한다(정적 readonly).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환. 필드 접근에 I/O가 없다.</description></item>
/// </list>
/// </remarks>
public static class TestJson
{
    // JsonSerializerOptions: 인스턴스 생성 비용(리플렉션 메타데이터 캐시 구축)이 커서 테스트마다 새로 만들면
    // 매 호출 힙 할당과 캐시 재구축이 반복된다. 정적 readonly 필드 하나를 프로세스 전체가 공유해 그 비용을 1회로 줄인다.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>비 ASCII 문자를 <c>\uXXXX</c>로 이스케이프하지 않는 전송용 옵션. 기본 인코더(<see cref="JavaScriptEncoder.Default"/>)는
    /// 한글 등 비 ASCII 코드포인트 1개를 6바이트 이스케이프 시퀀스로 부풀려, 브라우저의 <c>JSON.stringify</c>가 실제로 보내는 UTF-8 바이트 수와
    /// 최대 2배까지 달라진다 — 본문 크기 상한(스펙 3.7, 256KB) 경계를 검증하는 테스트가 이 부풀림 때문에 413로 오탐하지 않으려면 이 옵션을 쓴다.</summary>
    public static readonly JsonSerializerOptions RelaxedOptions = new(Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
