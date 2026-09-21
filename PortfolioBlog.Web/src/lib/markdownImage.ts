import { hasControlChar } from './safeNext'

/** 파일 이름에서 마크다운 이미지의 대체 텍스트를 만든다. 대괄호·괄호·제어 문자는 문법을 깨므로 뺀다. */
export function altTextOf(fileName: string): string {
  const stem = fileName.replace(/\.[^.]*$/, '')
  let out = ''
  for (const ch of stem) if (!'[]()'.includes(ch) && !hasControlChar(ch)) out += ch
  return out.trim().slice(0, 100) || 'image'
}
