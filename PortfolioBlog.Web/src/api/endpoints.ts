import { request } from './client'
import type {
  Attachment, AuthStatus, PagedAttachments, PagedPosts, PostDetail, PreviewResponse,
  Series, SeriesDetail, Tag, UpsertPostRequest, UpsertSeriesRequest,
} from './types'

// 경로의 id는 서버가 준 Guid만 들어오지만, 경로 조립은 항상 encodeURIComponent를 거친다(경로 주입 방지).
const id = (value: string) => encodeURIComponent(value)

export const auth = {
  me: (signal?: AbortSignal) => request<AuthStatus>('GET', '/api/auth/me', { signal }),
  login: (password: string) => request<void>('POST', '/api/auth/login', { json: { password } }),
  logout: () => request<void>('POST', '/api/auth/logout'),
}

export const posts = {
  list: (q: string, skip: number, take: number, signal?: AbortSignal) =>
    request<PagedPosts>('GET', '/api/posts', { query: { q, skip, take }, signal }),
  get: (postId: string, signal?: AbortSignal) => request<PostDetail>('GET', `/api/posts/${id(postId)}`, { signal }),
  create: (body: UpsertPostRequest) => request<PostDetail>('POST', '/api/posts', { json: body }),
  update: (postId: string, body: UpsertPostRequest) => request<PostDetail>('PUT', `/api/posts/${id(postId)}`, { json: body }),
  remove: (postId: string, version: number) => request<void>('DELETE', `/api/posts/${id(postId)}`, { query: { version } }),
}

export const series = {
  list: (signal?: AbortSignal) => request<Series[]>('GET', '/api/series', { signal }),
  get: (seriesId: string, signal?: AbortSignal) => request<SeriesDetail>('GET', `/api/series/${id(seriesId)}`, { signal }),
  create: (body: UpsertSeriesRequest) => request<Series>('POST', '/api/series', { json: body }),
  update: (seriesId: string, body: UpsertSeriesRequest) => request<Series>('PUT', `/api/series/${id(seriesId)}`, { json: body }),
  remove: (seriesId: string) => request<void>('DELETE', `/api/series/${id(seriesId)}`),
}

export const tags = {
  list: (signal?: AbortSignal) => request<Tag[]>('GET', '/api/tags', { signal }),
  remove: (tagId: string) => request<void>('DELETE', `/api/tags/${id(tagId)}`),
}

export const attachments = {
  list: (skip: number, take: number, signal?: AbortSignal) =>
    request<PagedAttachments>('GET', '/api/attachments', { query: { skip, take }, signal }),
  /** multipart 필드 이름은 서버 계약상 'file'이다. 같은 내용이면 서버가 기존 첨부(200)를 돌려준다. */
  upload: (file: Blob, fileName: string) => {
    const form = new FormData()
    form.append('file', file, fileName)
    return request<Attachment>('POST', '/api/attachments', { form })
  },
  remove: (attachmentId: string) => request<void>('DELETE', `/api/attachments/${id(attachmentId)}`),
}

export const preview = {
  render: (markdown: string, signal?: AbortSignal) => request<PreviewResponse>('POST', '/api/preview', { json: { markdown }, signal }),
}
