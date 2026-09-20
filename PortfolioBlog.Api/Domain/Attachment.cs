namespace PortfolioBlog.Api.Domain;

/// <summary>업로드된 이미지의 메타 정보. 본체는 볼륨의 <see cref="StoragePath"/>에 있다(메타데이터 제거 후 바이트의 SHA-256이 곧 경로).</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. DbContext 스코프 안 단일 스레드.</description></item>
/// <item><description><b>Memory Allocation:</b> 인스턴스당 힙 1개(파일 내용은 들고 있지 않다).</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환.</description></item>
/// </list>
/// </remarks>
public sealed class Attachment
{
    /// <summary>공개 URL 식별자(Guid v7). 글에 연결되지 않은 첨부도 이 값을 알면 읽을 수 있다.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>표시용 파일 이름(경로 조각·제어문자 제거, 확장자는 시그니처에서 다시 정함). 파일 시스템 경로에 쓰지 않는다.</summary>
    public string FileName { get; set; } = string.Empty;
    /// <summary>시그니처에서 유도한 Content-Type(<c>image/png|jpeg|gif|webp</c>).</summary>
    public string ContentType { get; set; } = string.Empty;
    /// <summary>메타데이터 제거 후 크기(바이트).</summary>
    public long SizeBytes { get; set; }
    /// <summary>저장 루트 기준 상대 경로 <c>{sha[..2]}/{sha}.{ext}</c>. 서버가 만든 값만 들어간다.</summary>
    public string StoragePath { get; set; } = string.Empty;
    /// <summary>메타데이터 제거 후 바이트의 SHA-256(소문자 hex 64자). 유일.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>업로드 시각(UTC, 마이크로초 절삭).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
