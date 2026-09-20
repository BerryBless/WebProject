using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Access;

namespace PortfolioBlog.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="AdminCredential"/>의 해시 검증·지문 계산·CLI 해시 생성을 검증하는 단위 테스트. DB·호스트 없이 순수 값 검증만 한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> xUnit 테스트 스레드에서 실행된다. 호스트·DB 의존이 없어 순수 CPU 연산(PBKDF2)만 수행한다.</description></item>
/// <item><description><b>Memory Policy:</b> 테스트 메서드마다 <see cref="AdminCredential"/> 인스턴스와 해시 문자열 몇 개만 할당한다.</description></item>
/// <item><description><b>Concurrency:</b> 케이스 간 공유 가변 상태가 없으므로 병렬 실행에 안전하다.</description></item>
/// </list>
/// </remarks>
public sealed class AdminCredentialTests
{
    /// <summary>주어진 해시 문자열을 <c>Admin:PasswordHash</c>로 갖는 <see cref="AdminCredential"/>을 만든다.</summary>
    /// <param name="hash"><see cref="AdminOptions.PasswordHash"/>에 넣을 PBKDF2 해시 문자열(또는 빈 값).</param>
    /// <returns>테스트가 검증할 새 <see cref="AdminCredential"/> 인스턴스.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 호출마다 새 인스턴스를 반환하므로 다른 호출과 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="AdminCredential"/> 1개와 내부 지문 계산에 필요한 임시 버퍼만 할당한다.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. I/O 없음.</description></item>
    /// </list>
    /// </remarks>
    private static AdminCredential Create(string hash) => new(Options.Create(new AdminOptions { PasswordHash = hash }));

    /// <summary>올바른 비밀번호는 검증을 통과하고, 한 글자라도 다르거나 빈 문자열이면 거부되는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="AdminCredential"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 자격 증명 1개와 <see cref="AdminCredential.Verify"/> 호출당 PBKDF2 파생 버퍼.</description></item>
    /// <item><description><b>Blocking:</b> <see cref="AdminCredential.Verify"/>는 CPU 바운드 동기 호출이며 이 테스트 스레드에서 완료를 기다린다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Verify_CorrectPassword_True_WrongPassword_False()
    {
        var credential = Create(AdminCredential.Hash("correct horse battery staple"));
        Assert.True(credential.Verify("correct horse battery staple"));
        Assert.False(credential.Verify("correct horse battery stapl"));
        Assert.False(credential.Verify(""));
    }

    /// <summary>해시가 비어 있으면(설정 누락) 어떤 비밀번호를 넣어도 항상 검증에 실패하는지(fail closed) 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="AdminCredential"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 자격 증명 1개.</description></item>
    /// <item><description><b>Blocking:</b> 즉시 반환. 해시가 비어 있으면 PBKDF2 연산 자체를 건너뛴다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Verify_EmptyHash_AlwaysFalse() => Assert.False(Create("").Verify("anything"));

    /// <summary>같은 비밀번호를 두 번 해싱해도 매번 다른 솔트가 섞여 다른 해시 문자열이 나오는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 정적 메서드 <see cref="AdminCredential.Hash"/>만 두 번 호출하므로 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 해시 문자열 2개.</description></item>
    /// <item><description><b>Blocking:</b> <see cref="AdminCredential.Hash"/>는 CPU 바운드 동기 호출이며 이 테스트 스레드에서 완료를 기다린다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Hash_IsSalted_SamePasswordGivesDifferentHashes() =>
        Assert.NotEqual(AdminCredential.Hash("pw-123456"), AdminCredential.Hash("pw-123456"));

    /// <summary>지문이 해시 문자열이 바뀌면 달라지고, 같은 해시에서는 항상 같은 값이며, 해시 원문을 그대로 담지 않는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="AdminCredential"/> 인스턴스들만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 해시 문자열 2개와 자격 증명 3개(같은 해시로 만든 것 포함).</description></item>
    /// <item><description><b>Blocking:</b> <see cref="AdminCredential.Hash"/> 호출 2회는 CPU 바운드 동기 호출이며 이 테스트 스레드에서 완료를 기다린다.</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Fingerprint_ChangesWhenHashChanges_AndDoesNotRevealHash()
    {
        var hashA = AdminCredential.Hash("a-password");
        var a = Create(hashA);
        var b = Create(AdminCredential.Hash("b-password"));
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Fingerprint, Create(hashA).Fingerprint);
        Assert.Equal(32, a.Fingerprint.Length); // 16바이트 hex
        Assert.DoesNotContain(a.Fingerprint, hashA, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="HashPasswordCommand.Run"/>이 표준 입력으로 받은 비밀번호를 검증 가능한 해시로 출력하고, 최소 길이 미만은 거부하는지 검증한다.</summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 이 테스트 전용 <see cref="StringReader"/>/<see cref="StringWriter"/>만 사용하므로 다른 테스트와 공유하는 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 입출력 버퍼(<see cref="StringWriter"/>) 각 1개와 <see cref="AdminCredential"/> 1개.</description></item>
    /// <item><description><b>Blocking:</b> <see cref="HashPasswordCommand.Run"/>은 <c>input</c>에서 동기적으로 한 줄을 읽는다(콘솔 대기 없음, 이미 채워진 <see cref="StringReader"/>이므로 즉시 반환).</description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void HashPasswordCommand_PipedInput_PrintsVerifiableHash_AndRejectsShortPassword()
    {
        var output = new StringWriter();
        Assert.Equal(0, HashPasswordCommand.Run(new StringReader("a-long-enough-password\n"), output, TextWriter.Null, interactive: false));
        Assert.True(Create(output.ToString().Trim()).Verify("a-long-enough-password"));

        Assert.Equal(1, HashPasswordCommand.Run(new StringReader("short\n"), TextWriter.Null, new StringWriter(), interactive: false));
    }
}
