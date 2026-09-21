// 글별 임시본(localStorage). 저장에 성공하면 지운다. 인증 정보는 절대 넣지 않는다 — 들어가는 것은 작성자 자신의 글 내용뿐이다.
// localStorage는 같은 출처의 스크립트가 고칠 수 있으므로 읽을 때 모양을 검사한다(깨진 값은 없는 것으로 본다).

export interface Draft {
  slug: string; title: string; summary: string; contentMarkdown: string
  tagNames: string[]; seriesId: string | null; seriesOrder: number | null
  /** 이 임시본이 바탕으로 삼은 서버 version. 새 글이면 null. 409 뒤 비교 화면에서 쓴다. */
  baseVersion: number | null
  /** 저장 시각(ISO 8601). */
  savedAt: string
}

export type DraftFields = Omit<Draft, 'baseVersion' | 'savedAt'>

const PREFIX = 'pb.draft.v1:'
export const NEW_POST_KEY = 'new'
const keyOf = (postId: string) => PREFIX + postId

const isString = (v: unknown): v is string => typeof v === 'string'

function isDraft(v: unknown): v is Draft {
  if (typeof v !== 'object' || v === null) return false
  const d = v as Record<string, unknown>
  return isString(d.slug) && isString(d.title) && isString(d.summary) && isString(d.contentMarkdown) && isString(d.savedAt)
    && Array.isArray(d.tagNames) && d.tagNames.every(isString)
    && (d.seriesId === null || isString(d.seriesId))
    && (d.seriesOrder === null || typeof d.seriesOrder === 'number')
    && (d.baseVersion === null || typeof d.baseVersion === 'number')
}

export function loadDraft(postId: string, storage: Storage = window.localStorage): Draft | null {
  try {
    const raw = storage.getItem(keyOf(postId))
    if (raw === null) return null
    const parsed: unknown = JSON.parse(raw)
    return isDraft(parsed) ? parsed : null
  } catch { return null }
}

/** 저장에 실패하면(용량 초과·비공개 모드) false. 임시본은 편의 기능이므로 실패해도 편집은 계속된다. */
export function saveDraft(postId: string, draft: Draft, storage: Storage = window.localStorage): boolean {
  try { storage.setItem(keyOf(postId), JSON.stringify(draft)); return true } catch { return false }
}

export function clearDraft(postId: string, storage: Storage = window.localStorage): void {
  try { storage.removeItem(keyOf(postId)) } catch { /* 지울 수 없으면 둔다 */ }
}

/** 임시본이 주어진 내용과 같은가(태그는 순서까지 비교한다 — 화면에 보이는 순서가 곧 입력이다). */
export function sameFields(a: DraftFields, b: DraftFields): boolean {
  return a.slug === b.slug && a.title === b.title && a.summary === b.summary && a.contentMarkdown === b.contentMarkdown
    && a.seriesId === b.seriesId && a.seriesOrder === b.seriesOrder
    && a.tagNames.length === b.tagNames.length && a.tagNames.every((t, i) => t === b.tagNames[i])
}
