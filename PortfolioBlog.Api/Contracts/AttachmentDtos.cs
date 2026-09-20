namespace PortfolioBlog.Api.Contracts;

/// <summary>첨부 메타 정보.</summary>
/// <param name="Id">식별자.</param>
/// <param name="Url">마크다운에 그대로 넣을 수 있는 루트 상대 URL(<c>/attachments/{id}/{파일명}</c>, 파일명은 URL 인코딩됨).</param>
/// <param name="FileName">표시용 파일 이름.</param>
/// <param name="ContentType">시그니처에서 유도한 Content-Type.</param>
/// <param name="SizeBytes">메타데이터 제거 후 크기.</param>
/// <param name="Sha256">메타데이터 제거 후 바이트의 SHA-256.</param>
/// <param name="CreatedAt">업로드 시각(UTC).</param>
public sealed record AttachmentDto(Guid Id, string Url, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset CreatedAt);

/// <summary>첨부 목록 페이지.</summary>
/// <param name="Items">최신순 항목.</param>
/// <param name="Total">전체 건수.</param>
public sealed record PagedAttachmentsDto(AttachmentDto[] Items, int Total);
