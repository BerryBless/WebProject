import ts from 'typescript'

/** 주석 하나의 범위(UTF-16 코드 유닛 오프셋, TypeScript AST의 pos/end와 같은 단위). end는 배타적이다. */
export interface CommentRange { pos: number; end: number }

/**
 * 소스에서 실제 주석의 범위만 찾는다. 정규식으로 `//`·`/*`를 훑지 않는다 — TypeScript 파서로 소스를 실제로
 * 분석해서, 문자열 리터럴·정규식 리터럴·JSX 텍스트 안에 있는 `//`·`/*`가 토큰(문자열·정규식·JsxText)으로 이미
 * 분류된 뒤이므로 주석으로 오인하지 않는다.
 * 모든 AST 노드에 대해 `getFullStart()`에서 `ts.getLeadingCommentRanges`와 `ts.getTrailingCommentRanges`를 **둘 다**,
 * `getEnd()`에서도 `ts.getTrailingCommentRanges`를 모은다. 컴파일러 API의 `getLeadingCommentRanges(text, pos)`는
 * `pos`가 0이 아니면 **첫 개행 전까지 있는 주석을 모으지 않는다**(실측 — 소스: `getCommentRanges`의
 * `collecting = trailing || pos === 0`). 그래서 여는 괄호와 **같은 줄**에 있는 주석(예: `catch { /* c *\/ }`,
 * `for (...) { // c\n stmt }`)은 리딩만으로는 못 찾는다 — `getTrailingCommentRanges`는 `pos`와 무관하게 항상
 * 모으므로 같은 위치에서 트레일링도 같이 불러 이 구멍을 막는다. 같은 [pos,end)가 여러 방법에서 중복 수집되는
 * 경우가 있어 `pos:end` 키로 한 번만 담는다.
 * 자식이 있는 노드는 이렇게 잡히지만, **자식 노드가 아예 없는 빈 컨테이너**는(예: `catch { /* c *\/ }`처럼 문장이
 * 하나도 없는 블록, `{/* c *\/}`처럼 식이 없는 JsxExpression) 위 순회가 방문할 자식이 없어 안쪽 주석을 못 찾는다 —
 * `statements.length === 0`인 Block과 `expression`이 없는 JsxExpression을 만나면 여는 괄호 바로 다음 위치에서
 * 리딩·트레일링을 한 번 더 부른다. 다른 종류의 빈 컨테이너(빈 객체 리터럴 `{}`, 빈 배열 `[]` 등)는 이 저장소의
 * 26개 소스 파일을 실제로 훑어 나온 것만 다뤘다 — 그 안에 그런 패턴이 없었다(아래 자기 검사가 새로 생기면 잡는다).
 */
export function collectCommentRanges(text: string, fileName = 'x.tsx'): CommentRange[] {
  const sourceFile = ts.createSourceFile(fileName, text, ts.ScriptTarget.Latest, /* setParentNodes */ true, ts.ScriptKind.TSX)
  const ranges: CommentRange[] = []
  const seen = new Set<string>()
  const add = (found: readonly ts.CommentRange[] | undefined) => {
    if (!found) return
    for (const r of found) {
      const key = `${r.pos}:${r.end}`
      if (!seen.has(key)) { seen.add(key); ranges.push({ pos: r.pos, end: r.end }) }
    }
  }
  // 빈 컨테이너(자식이 없어 안쪽을 훑을 노드가 없는 경우)의 여는 괄호 바로 다음 위치에서 리딩·트레일링을 둘 다 부른다.
  const checkInsideEmptyBrackets = (node: ts.Node) => {
    add(ts.getTrailingCommentRanges(text, node.getStart(sourceFile) + 1))
    add(ts.getLeadingCommentRanges(text, node.getStart(sourceFile) + 1))
  }
  const visit = (node: ts.Node): void => {
    add(ts.getLeadingCommentRanges(text, node.getFullStart()))
    add(ts.getTrailingCommentRanges(text, node.getFullStart()))
    add(ts.getTrailingCommentRanges(text, node.getEnd()))
    if (ts.isJsxExpression(node) && !node.expression) checkInsideEmptyBrackets(node)
    if (ts.isBlock(node) && node.statements.length === 0) checkInsideEmptyBrackets(node)
    node.forEachChild(visit)
  }
  visit(sourceFile)
  return ranges.sort((a, b) => a.pos - b.pos)
}

/**
 * 주석을 뺀다. 각 주석 범위(`collectCommentRanges`)를 같은 길이의 공백으로 치환한다 — 범위 안의 개행(`\n`·`\r`)은
 * 그대로 두므로 출력의 줄 수·각 줄의 나머지 문자의 열 위치는 입력과 같다.
 * 보장: 출력 길이는 입력과 같다. 주석이 아닌 문자는 그대로 남는다(`collectCommentRanges`가 찾은 범위 밖은 손대지
 * 않는다). 이 파일을 쓰는 검사(source-guards.test.ts)는 이 불변식을 파일마다 직접 확인한다.
 * 보장하지 않음: 문법 오류가 있는 입력에서 TypeScript 파서가 어떻게 복구하는지는 검사하지 않았다(미검증) —
 * 이 함수가 훑는 대상은 빌드를 통과하는(`tsc -b` 0 오류) .ts/.tsx 파일뿐이라 실전에서 부딪힐 가능성은 낮다고
 * 본다(추론).
 */
export function stripComments(text: string, fileName = 'x.tsx'): string {
  const ranges = collectCommentRanges(text, fileName)
  let result = ''
  let cursor = 0
  for (const { pos, end } of ranges) {
    result += text.slice(cursor, pos)
    result += blank(text.slice(pos, end))
    cursor = end
  }
  result += text.slice(cursor)
  return result
}

function blank(segment: string): string {
  let out = ''
  for (let i = 0; i < segment.length; i++) out += (segment[i] === '\n' || segment[i] === '\r') ? segment[i] : ' '
  return out
}
