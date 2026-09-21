import { hasControlChar } from './safeNext'

/** 파일 이름에서 마크다운 이미지의 대체 텍스트를 만든다. 대괄호·괄호·제어 문자는 문법을 깨므로 뺀다. */
export function altTextOf(fileName: string): string {
  const stem = fileName.replace(/\.[^.]*$/, '')
  let out = ''
  for (const ch of stem) if (!'[]()'.includes(ch) && !hasControlChar(ch)) out += ch
  // slice(0, 100)은 UTF-16 코드 유닛 단위라 서로게이트 쌍(예: 이모지)의 짝을 끊어 깨진 문자를 남길 수 있다.
  // 문자열 순회(for...of / 배열 스프레드)는 코드포인트 단위이므로 100개를 잘라도 짝이 갈라지지 않는다.
  return [...out.trim()].slice(0, 100).join('') || 'image'
}
