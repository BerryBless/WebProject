namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>설정 섹션 <c>Attachments</c>.</summary>
public sealed class AttachmentOptions
{
    /// <summary>이 옵션이 바인딩되는 설정 섹션 이름.</summary>
    public const string SectionName = "Attachments";
    /// <summary>업로드 한도(10MB). Kestrel·multipart 한도는 이보다 1MB 크게 잡아 "한도 초과"를 앱이 413으로 답하게 한다.</summary>
    public const int MaxBytes = 10_485_760;
    /// <summary>첨부 저장 루트. 상대 경로면 콘텐츠 루트 기준. 정적 파일 루트 밖이어야 한다. 비어 있으면 시작 실패.</summary>
    public string RootPath { get; set; } = string.Empty;
}
