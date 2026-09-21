// 서버 DTO(PortfolioBlog.Api/Contracts/*.cs)의 JSON 모양. ASP.NET Core 기본 직렬화라 속성은 camelCase,
// Guid·DateTimeOffset은 문자열, uint Version은 number(최대 4,294,967,295 — Number.MAX_SAFE_INTEGER 안)다.

export interface AuthStatus { authenticated: boolean }

export interface PostSummary {
  id: string; slug: string; title: string; summary: string; tags: string[]
  seriesId: string | null; seriesOrder: number | null
  createdAt: string; updatedAt: string; version: number
}
export interface PostDetail extends PostSummary { contentMarkdown: string }
export interface PagedPosts { items: PostSummary[]; total: number }

/** 생성·수정 공용 요청. 연결(tagNames·seriesId·seriesOrder)은 통째로 교체된다. version은 수정에서만 필수. */
export interface UpsertPostRequest {
  slug: string; title: string; summary: string; contentMarkdown: string
  tagNames: string[]; seriesId: string | null; seriesOrder: number | null; version?: number
}

export interface Series { id: string; slug: string; title: string; description: string; postCount: number }
export interface SeriesPost { id: string; slug: string; title: string; seriesOrder: number }
export interface SeriesDetail { series: Series; posts: SeriesPost[] }
export interface UpsertSeriesRequest { slug: string; title: string; description: string }

export interface Tag { id: string; name: string; normalizedName: string; postCount: number }

export interface Attachment {
  id: string; url: string; fileName: string; contentType: string; sizeBytes: number; sha256: string; createdAt: string
}
export interface PagedAttachments { items: Attachment[]; total: number }

export interface PreviewResponse { html: string }
