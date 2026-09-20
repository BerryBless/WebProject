using System.Text;

namespace PortfolioBlog.Api.Infrastructure.Access;

/// <summary><c>dotnet run --project PortfolioBlog.Api -- hash-password</c>: 비밀번호를 에코 없이 입력받아 <c>Admin:PasswordHash</c>에 넣을 해시를 출력한다.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 단일 스레드 CLI 경로. 웹 호스트를 만들지 않는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 입력 버퍼와 해시 문자열. 비밀번호를 인수로 받지 않는 이유: 명령줄 인수는 셸 기록과 프로세스 목록에 남는다.</description></item>
/// <item><description><b>Blocking:</b> 콘솔 입력을 동기 대기한다.</description></item>
/// </list>
/// </remarks>
public static class HashPasswordCommand
{
    /// <summary>이 명령을 선택하는 첫 번째(유일한) CLI 인수 값.</summary>
    public const string Name = "hash-password";

    /// <summary>허용하는 비밀번호 최소 길이(문자).</summary>
    public const int MinLength = 12;

    /// <summary>표준 입력에서(또는 대화형이면 화면 에코 없이) 비밀번호를 읽어 PBKDF2 해시를 출력한다.</summary>
    /// <param name="input">비대화형 모드에서 비밀번호 한 줄을 읽어올 입력 스트림(파이프 입력 테스트용).</param>
    /// <param name="output">성공 시 해시 문자열 한 줄을 쓸 출력 스트림.</param>
    /// <param name="error">프롬프트·오류 메시지를 쓸 스트림(해시 자체는 여기 쓰지 않는다).</param>
    /// <param name="interactive"><c>true</c>이면 콘솔에서 키 입력을 직접 가로채 에코 없이 읽는다. <c>false</c>이면 <paramref name="input"/>에서 한 줄을 읽는다(파이프·테스트용).</param>
    /// <returns>성공하면 0, 비밀번호가 없거나 길이 제약을 벗어나면 1.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 단일 스레드 CLI 진입점에서만 호출된다. 공유 가변 상태가 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> 대화형 입력 시 <see cref="StringBuilder"/> 1개(비밀번호 길이만큼), 해시 문자열 1개.</description></item>
    /// <item><description><b>Blocking:</b> 동기 블로킹. 대화형이면 <see cref="Console.ReadKey(bool)"/>로 키 입력을, 비대화형이면 <paramref name="input"/>의 한 줄을 기다린다. 이후 <see cref="AdminCredential.Hash"/> 호출이 PBKDF2 CPU 연산으로 다시 수십 ms 블로킹한다.</description></item>
    /// </list>
    /// </remarks>
    public static int Run(TextReader input, TextWriter output, TextWriter error, bool interactive)
    {
        if (interactive) error.Write("새 관리자 비밀번호: ");
        var password = interactive ? ReadHidden() : input.ReadLine();
        if (interactive) error.WriteLine();
        if (password is null || password.Length < MinLength || password.Length > 256)
        {
            error.WriteLine($"비밀번호는 {MinLength}~256자여야 합니다.");
            return 1;
        }
        output.WriteLine(AdminCredential.Hash(password));
        return 0;
    }

    /// <summary>콘솔에서 키 입력을 하나씩 가로채 화면에 에코하지 않고 비밀번호 문자열을 조립한다(백스페이스 지원).</summary>
    /// <returns>Enter 키가 눌리기 전까지 입력된 문자열.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> 단일 스레드 CLI 경로에서만 호출된다. <see cref="Console"/>은 프로세스 전역 상태를 갖지만 이 CLI 경로는 웹 호스트 없이 단독 실행되어 경합이 없다.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="StringBuilder"/> 1개가 입력 길이만큼 내부 버퍼를 늘려간다.</description></item>
    /// <item><description><b>Blocking:</b> 동기 블로킹. Enter가 눌릴 때까지 <see cref="Console.ReadKey(bool)"/> 호출마다 콘솔 입력을 기다린다.</description></item>
    /// </list>
    /// </remarks>
    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return sb.ToString();
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }
}
