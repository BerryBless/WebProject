using PortfolioBlog.Api.Infrastructure.Web;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>XML 1.0에 넣을 수 없는 문자만 버린다. 제어 문자는 소스에 이스케이프로 쓰지 않고 (char) 캐스트로 만든다(선행 규칙 4의 연장).</summary>
public sealed class XmlTextTests
{
    /// <summary>모든 문자가 유효하면 원본 참조를 그대로 돌려주는지(할당 없음) 검증한다: 탭·줄바꿈·서로게이트 쌍 이모지·XML 특수문자·CDATA 종료 시퀀스가 섞여도 통과해야 한다.</summary>
    [Fact]
    public void CleanInput_IsReturnedAsIs()
    {
        const string text = "탭\t줄바꿈\n이모지😀 <&\"'> ]]>";
        Assert.Same(text, XmlText.Clean(text));
    }

    /// <summary>C0 제어 문자(U+0001·U+001F)·U+FFFE·문자열 중간과 끝의 짝 없는 서로게이트가 제거되고, 올바른 서로게이트 쌍은 보존되는지 검증한다.</summary>
    [Fact]
    public void InvalidCharacters_AreDropped()
    {
        var high = ((char)0xD83D).ToString();
        var low = ((char)0xDE00).ToString();
        Assert.Equal("ab", XmlText.Clean("a" + (char)1 + "b"));
        Assert.Equal("ab", XmlText.Clean("a" + (char)0x1F + "b"));
        Assert.Equal("ab", XmlText.Clean("a" + (char)0xFFFE + "b"));
        Assert.Equal("ab", XmlText.Clean("a" + high + "b"));        // 짝 없는 상위 서로게이트
        Assert.Equal("ab", XmlText.Clean("a" + low + "b"));         // 짝 없는 하위 서로게이트
        Assert.Equal("ab", XmlText.Clean("ab" + high));             // 문자열 끝의 상위 서로게이트
        Assert.Equal("a" + high + low + "b", XmlText.Clean("a" + high + low + "b"));
    }

    /// <summary>빈 문자열, 문자열 맨 끝의 완전한 서로게이트 쌍, 인덱스 0의 짝 없는 하위 서로게이트, 연속된 두 무효 문자가 각각 올바르게
    /// 처리되는지 경계에서 검증한다(모두 브리프의 표본 테스트가 직접 다루지 않는 경계).</summary>
    [Fact]
    public void BoundaryCases_EmptyString_TrailingPair_LeadingLowSurrogate_AndConsecutiveInvalid()
    {
        var high = ((char)0xD83D).ToString();
        var low = ((char)0xDE00).ToString();
        Assert.Same(string.Empty, XmlText.Clean(string.Empty));
        Assert.Equal("a" + high + low, XmlText.Clean("a" + high + low)); // 문자열 끝에 완전한 쌍이 걸쳐 있어도 보존된다
        Assert.Equal("b", XmlText.Clean(low + "b"));                     // 인덱스 0의 짝 없는 하위 서로게이트
        Assert.Equal("ab", XmlText.Clean("a" + (char)1 + (char)2 + "b")); // 연속된 두 무효 문자
    }

    /// <summary>정리한 결과는 XmlWriter가 예외 없이 받는다(정리 함수와 작성기의 판정이 어긋나면 실패). U+1..U+FFFF를 코드 단위 순서대로
    /// 전부 이어붙여, 상위 서로게이트 구간이 끝나고 하위 서로게이트 구간이 시작하는 경계(D800..DBFF 다음 DC00..DFFF)까지 함께 지나간다.</summary>
    [Fact]
    public void CleanedText_IsAcceptedByXmlWriter()
    {
        var every = string.Concat(Enumerable.Range(1, 0xFFFF).Select(i => (char)i));
        var sb = new System.Text.StringBuilder();
        Assert.Null(Record.Exception(() =>
        {
            using var xml = System.Xml.XmlWriter.Create(sb);
            xml.WriteElementString("t", XmlText.Clean(every));
        }));
    }
}
