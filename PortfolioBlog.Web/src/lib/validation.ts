import type { FieldErrors } from '../api/errors'
import type { UpsertPostRequest, UpsertSeriesRequest } from '../api/types'

// 서버 검증(PostValidation·SeriesValidation·TagResolver.Validate, AppDbContext의 상수)을 그대로 비춘 값이다.
// 이 검사는 편의(저장 전에 바로 알려 줌)일 뿐이고 권한 있는 판정은 서버가 한다 — 서버의 400도 같은 필드 키로 화면에 표시된다.
export const LIMITS = {
  slugMax: 100, titleMax: 200, summaryMax: 300, contentMaxBytes: 204_800,
  seriesDescriptionMax: 1000, tagMax: 50, maxTagsPerPost: 20, queryMax: 100,
  attachmentMaxBytes: 10_485_760,
} as const

export const SLUG_PATTERN = /^[a-z0-9]+(-[a-z0-9]+)*$/
export const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'] as const

const utf8 = new TextEncoder()
/** UTF-8 바이트 수. 서버·DB CHECK와 같은 단위다(글자 수가 아니다 — 한글 1자는 3바이트). */
export const utf8ByteLength = (value: string): number => utf8.encode(value).length

const hasNul = (value: string): boolean => value.includes(String.fromCharCode(0))
const NUL_MESSAGE = '제어 문자(NUL)는 포함할 수 없습니다.'

function add(errors: FieldErrors, field: string, message: string): void {
  (errors[field] ??= []).push(message)
}

/** 서버의 TagResolver.Display와 같은 표시 형태: 공백 정리 + NFC. */
export const displayTag = (raw: string): string => raw.split(/\s+/).filter(Boolean).join(' ').normalize('NFC')

function validateSlugAndTitle(errors: FieldErrors, slug: string, title: string): void {
  if (slug.length === 0) add(errors, 'slug', 'slug는 필수입니다.')
  else if (hasNul(slug)) add(errors, 'slug', NUL_MESSAGE)
  else if (slug.length > LIMITS.slugMax || !SLUG_PATTERN.test(slug)) add(errors, 'slug', `slug는 소문자·숫자·하이픈만 쓰고 ${LIMITS.slugMax}자 이하여야 합니다(예: my-first-post).`)
  if (title.trim().length === 0) add(errors, 'title', '제목은 비울 수 없습니다.')
  else if (hasNul(title)) add(errors, 'title', NUL_MESSAGE)
  else if (title.trim().length > LIMITS.titleMax) add(errors, 'title', `제목은 ${LIMITS.titleMax}자 이하여야 합니다.`)
}

export function validatePost(req: UpsertPostRequest): FieldErrors {
  const errors: FieldErrors = {}
  validateSlugAndTitle(errors, req.slug, req.title)
  if (hasNul(req.summary)) add(errors, 'summary', NUL_MESSAGE)
  else if (req.summary.trim().length > LIMITS.summaryMax) add(errors, 'summary', `요약은 ${LIMITS.summaryMax}자 이하여야 합니다.`)
  if (hasNul(req.contentMarkdown)) add(errors, 'contentMarkdown', NUL_MESSAGE)
  else if (utf8ByteLength(req.contentMarkdown) > LIMITS.contentMaxBytes) add(errors, 'contentMarkdown', `본문은 UTF-8 기준 ${LIMITS.contentMaxBytes / 1024}KB 이하여야 합니다.`)

  const distinct = new Set<string>()
  for (const raw of req.tagNames) {
    if (raw.trim().length === 0) continue
    const display = displayTag(raw)
    if (hasNul(display)) add(errors, 'tagNames', '태그는 제어 문자(NUL)를 포함할 수 없습니다.')
    else if (display.length > LIMITS.tagMax) add(errors, 'tagNames', `태그는 ${LIMITS.tagMax}자 이하여야 합니다: ${display.slice(0, 20)}…`)
    else if (display.includes('/')) add(errors, 'tagNames', `태그에 '/'를 쓸 수 없습니다: ${display}`)
    else distinct.add(display.toLowerCase())
  }
  if (distinct.size > LIMITS.maxTagsPerPost) add(errors, 'tagNames', `태그는 글당 ${LIMITS.maxTagsPerPost}개까지입니다.`)

  if ((req.seriesId === null) !== (req.seriesOrder === null)) add(errors, 'seriesOrder', '시리즈와 순서는 함께 정하거나 함께 비워야 합니다.')
  else if (req.seriesOrder !== null && (!Number.isInteger(req.seriesOrder) || req.seriesOrder <= 0)) add(errors, 'seriesOrder', '순서는 1 이상의 정수여야 합니다.')
  return errors
}

export function validateSeries(req: UpsertSeriesRequest): FieldErrors {
  const errors: FieldErrors = {}
  validateSlugAndTitle(errors, req.slug, req.title)
  if (hasNul(req.description)) add(errors, 'description', NUL_MESSAGE)
  else if (req.description.trim().length > LIMITS.seriesDescriptionMax) add(errors, 'description', `설명은 ${LIMITS.seriesDescriptionMax}자 이하여야 합니다.`)
  return errors
}

/** 업로드 전 편의 검사. 형식의 권한 있는 판정은 서버의 시그니처 검사다(file.type은 브라우저가 확장자로 추측한 값일 뿐이다). */
export function validateImageFile(file: { size: number; type: string }): string | null {
  if (file.size === 0) return '빈 파일은 올릴 수 없습니다.'
  if (file.size > LIMITS.attachmentMaxBytes) return `이미지는 ${LIMITS.attachmentMaxBytes / 1_048_576}MB 이하여야 합니다.`
  if (!(IMAGE_TYPES as readonly string[]).includes(file.type)) return 'PNG·JPEG·GIF·WebP 이미지만 올릴 수 있습니다.'
  return null
}

export const hasErrors = (errors: FieldErrors): boolean => Object.keys(errors).length > 0
