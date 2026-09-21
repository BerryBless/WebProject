using System.Text;
using System.Xml;

namespace PortfolioBlog.Api.Infrastructure.Web;

/// <summary>XML 1.0 문서에 넣을 수 없는 문자를 버린다. 이스케이프(<c>&amp;lt;</c> 등)는 <see cref="XmlWriter"/>가 하므로 여기서 하지 않는다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 클래스이며 입력 문자열을 변경하지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Clean"/> 참조 — 유효한 입력은 할당 없음, 무효 문자가 있으면 새 문자열 1개.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음, CPU 연산만 수행한다.</description></item>
/// </list>
/// </remarks>
public static class XmlText
{
    /// <summary>스파이크 S9 실측: <see cref="XmlWriter"/>는 C0 제어 문자(예: U+0001)·U+FFFE·짝 없는 서로게이트를 만나면 <see cref="ArgumentException"/>을 던진다.
    /// 이 메서드는 그런 문자를 출력 직전에 제거해, 제목 하나의 제어 문자가 피드·sitemap 전체를 모든 방문자에게 영구 500으로 만드는 것을 막는다.</summary>
    /// <param name="value">정리할 원문 문자열.</param>
    /// <returns>전부 유효하면 <paramref name="value"/> 그대로(할당 없음). 아니면 유효한 문자만 남긴 새 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 무상태 정적 메서드이며 <paramref name="value"/>를 변경하지 않는다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 모든 문자가 유효하면 <paramref name="value"/> 참조를 그대로 돌려줘 <b>할당 없음</b>이다(빠른 경로, <c>Assert.Same</c>으로 검증됨).
    /// 무효 문자가 하나라도 있으면 <see cref="StringBuilder"/> 1개 + 결과 <see cref="string"/> 1개를 할당한다 — 이 경로는 <b>Zero-allocation이 아니다</b>.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환(Non-blocking). I/O 없음, 문자열 길이에 비례하는 동기 CPU 스캔뿐이다.</description></item>
    /// </list>
    /// </remarks>
    public static string Clean(string value)
    {
        var firstBad = IndexOfInvalid(value, 0);
        if (firstBad < 0) return value;

        // StringBuilder: 무효 문자를 건너뛰며 여러 유효 구간을 이어붙이는 동안, 구간마다 임시 부분 문자열을 만들지 않고
        // 내부에 미리 확보한 버퍼 하나에 누적한 뒤 마지막에 한 번만 문자열을 만든다(Substring + 연결 반복 대비 중간 할당이 적다).
        var sb = new StringBuilder(value.Length);
        var start = 0;
        for (var bad = firstBad; bad >= 0; bad = IndexOfInvalid(value, start))
        {
            sb.Append(value, start, bad - start);
            start = bad + 1;
        }
        return sb.Append(value, start, value.Length - start).ToString();
    }

    /// <summary><paramref name="from"/>부터 XML 1.0에 넣을 수 없는 첫 문자의 인덱스를 찾는다(짝을 이룬 서로게이트 쌍은 통째로 건너뛴다). 없으면 -1.</summary>
    private static int IndexOfInvalid(string value, int from)
    {
        for (var i = from; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++; // 올바른 쌍은 통째로 유효하다(쌍이 나타내는 코드포인트는 U+10000..U+10FFFF, XML 1.0 Char 생산 규칙이 이 구간 전체를 허용한다)
                continue;
            }
            // 여기까지 온 서로게이트는 짝이 없다. XmlConvert.IsXmlChar는 짝 없는 서로게이트·U+FFFE·C0 제어 문자에 false를 돌려준다(스파이크 S9 실측).
            if (!XmlConvert.IsXmlChar(c)) return i;
        }
        return -1;
    }
}
