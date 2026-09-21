using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PortfolioBlog.Api.Infrastructure.Data;

namespace PortfolioBlog.Api.Infrastructure.Storage;

/// <summary>청소 한 번의 결과.</summary>
/// <param name="TempFilesDeleted">지운 오래된 임시 파일 수.</param>
/// <param name="OrphanFilesDeleted">지운 참조 없는 첨부 파일 수.</param>
/// <param name="RowsMissingFiles">파일이 없는 행 수(고치지 못한다 — 같은 내용을 다시 올리면 복구된다).</param>
public sealed record SweepResult(int TempFilesDeleted, int OrphanFilesDeleted, int RowsMissingFiles);

/// <summary>시작 직후와 <see cref="Interval"/>마다: 죽은 업로드가 남긴 임시 파일과, 행 삽입이 실패했거나 파일 삭제가 실패해 남은 참조 없는 파일을 지운다.</summary>
/// <param name="scopes">스윕마다 <see cref="AppDbContext"/>를 새 스코프로 여는 팩토리(BackgroundService는 싱글턴이라 스코프 DbContext를 직접 주입받을 수 없다).</param>
/// <param name="store">지울 파일을 찾고 지우는 저장소.</param>
/// <param name="options">스윕 실행 여부(<see cref="AttachmentOptions.JanitorEnabled"/>)를 읽는 옵션.</param>
/// <param name="clock">"지금"을 얻는 시계(테스트가 대체할 수 있다). 스윕 로직 자체는 <see cref="SweepOnceAsync"/>에 전달된 시각만 쓴다.</param>
/// <param name="logger">스윕 결과·실패를 남기는 로거.</param>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="BackgroundService.ExecuteAsync"/>는 호스트가 시작할 때 단 한 번 시작하는 단일 루프다. <see cref="SweepOnceAsync"/>는 그 루프와 테스트 양쪽에서(동시에는 아니고 각자) 호출될 수 있도록 상태를 인스턴스에 두지 않고 매 호출마다 새 DB 스코프를 연다.</description></item>
/// <item><description><b>Memory Allocation:</b> 스윕 1회당 임시·저장 파일 경로 문자열들과 DB 조회 결과만큼 할당한다. 전체 목록을 배열로 모으지 않고 <see cref="FileSystemAttachmentStore.EnumerateTempFiles"/>/<see cref="FileSystemAttachmentStore.EnumerateStoredFiles"/> 스트리밍 열거를 그대로 소비한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="ExecuteAsync"/>는 시작 시 <see cref="Task.Yield"/>로 호스트 시작을 막지 않는다. <see cref="SweepOnceAsync"/> 내부의 파일 시스템 호출은 동기 I/O이지만(<see cref="File.GetLastWriteTimeUtc(string)"/>·<see cref="File.Delete(string)"/>), 이 클래스는 항상 백그라운드 실행 또는 테스트에서만 호출되어 요청 처리 스레드를 막지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class AttachmentJanitor(IServiceScopeFactory scopes, FileSystemAttachmentStore store, IOptions<AttachmentOptions> options,
    TimeProvider clock, ILogger<AttachmentJanitor> logger) : BackgroundService
{
    /// <summary>이보다 새 파일은 건드리지 않는다: 진행 중인 업로드의 임시 파일·방금 놓인 파일일 수 있다. 업로드 한 번은 이 시간 안에 끝난다(10MB).</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    /// <summary>실행 간격.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>호스트가 실행 중인 동안 <see cref="Interval"/>마다 <see cref="SweepOnceAsync"/>를 돌리는 배경 루프. <see cref="AttachmentOptions.JanitorEnabled"/>가 꺼져 있으면 즉시 반환한다.</summary>
    /// <param name="stoppingToken">호스트 종료 시 신호되는 취소 토큰.</param>
    /// <returns>호스트가 멈출 때(또는 <see cref="AttachmentOptions.JanitorEnabled"/>가 꺼져 있으면 시작 직후) 완료되는 태스크.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> .NET 제네릭 호스트가 시작할 때 스레드 풀 스레드에서 호출해 "실행 중(fire-and-forget)"으로 둔다 — 이 메서드가 끝나야 <c>StartAsync</c>가 끝나는 것이 아니라, 첫 <c>await</c>(아래 <see cref="Task.Yield"/>) 지점까지만 동기로 기다린다.</description></item>
    /// <item><description><b>Memory Policy:</b> <see cref="PeriodicTimer"/> 1개만 살아 있는 동안 유지한다. 스윕 결과는 매번 버려진다(로그로만 남는다).</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking. 스윕 한 번이 실패해도(<see cref="OperationCanceledException"/> 제외) 예외를 삼키고 다음 주기에 다시 시도한다 — 한 번의 실패가 호스트를 끌고 내려가지 않는다.</description></item>
    /// </list>
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JanitorEnabled) return;
        await Task.Yield(); // 호스트 시작을 막지 않는다(첫 await 전까지는 StartAsync가 기다린다)
        // PeriodicTimer: 틱 사이에 스레드를 점유하지 않는 비동기 타이머. 실행이 길어져도 틱이 겹치지 않는다(다음 WaitForNextTickAsync에서야 다시 돈다).
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var result = await SweepOnceAsync(clock.GetUtcNow(), stoppingToken);
                logger.LogInformation("첨부 청소. Temp={Temp} Orphans={Orphans} MissingFiles={Missing}", result.TempFilesDeleted, result.OrphanFilesDeleted, result.RowsMissingFiles);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "첨부 청소 실패. 다음 주기에 다시 시도한다.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary><paramref name="now"/> 기준으로 한 번 청소한다: <see cref="MinimumAge"/>보다 오래된 임시 파일을 지우고, 내용 주소 모양이면서 오래됐고 참조하는 행이 없는 파일을 지우고, 파일이 없는 행 수를 센다.</summary>
    /// <param name="now">"지금"으로 쓸 시각(테스트가 원하는 시점을 직접 넣을 수 있게 매개변수로 받는다 — 배경 루프는 <see cref="TimeProvider.GetUtcNow"/>를 넘긴다).</param>
    /// <param name="ct">취소 토큰. 저장 파일 열거 도중(반복마다) 확인한다.</param>
    /// <returns>이번 스윕에서 지운 임시 파일 수·지운 고아 파일 수·파일이 없는 행 수.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 배경 루프(스레드 풀) 또는 테스트 스레드에서 호출된다. 매 호출마다 <see cref="IServiceScopeFactory.CreateAsyncScope"/>로 전용 <see cref="AppDbContext"/>를 연다 — 배경 루프와 요청 파이프라인이 DbContext를 공유하지 않는다.</description></item>
    /// <item><description><b>Memory Policy:</b> 저장 파일·임시 파일 목록을 배열로 모으지 않고 스트리밍 열거한다. 고아로 확정된 파일마다 <see cref="AttachmentLock"/> 잠금 키 문자열 1개를 추가로 할당한다.</description></item>
    /// <item><description><b>Concurrency:</b> 고아 판정을 받은 각 파일은 삭제 직전 <see cref="AttachmentLock"/> 세션 잠금 안에서 "참조 없음"을 다시 확인한다 — 열거 시점과 잠금 획득 사이에 같은 내용의 업로드가 행을 넣었을 수 있기 때문이다(그 업로드는 파일이 이미 있어 옮기지 않고 행만 넣었다). 파일 삭제(<see cref="FileSystemAttachmentStore.TryDelete"/>)는 존재하지 않는 파일에도 <see langword="true"/>를 반환하므로, 열거와 삭제 사이에 다른 요청이 같은 파일을 이미 지웠다면(그 요청도 이 파일을 삭제한 것이므로) 이 스윕의 <see cref="SweepResult.OrphanFilesDeleted"/> 카운트에 함께 잡힌다 — 파일 자체는 어느 쪽이 지웠든 이미 없으므로 수치가 중복 집계될 뿐 안전 문제는 아니다(미검증: 이 경쟁을 재현하는 테스트는 만들지 않았다).</description></item>
    /// </list>
    /// </remarks>
    public async Task<SweepResult> SweepOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.UtcDateTime - MinimumAge;
        var temp = 0;
        foreach (var file in store.EnumerateTempFiles())
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff && TryDeleteFile(file)) temp++;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphans = 0;
        foreach (var (storagePath, sha) in store.EnumerateStoredFiles())
        {
            ct.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(store.PhysicalPath(storagePath)) >= cutoff) continue;
            // Sha256에는 UNIQUE 인덱스가 있다(StoragePath에는 없다) — 파일 수만큼 도는 조회라 인덱스를 타야 한다.
            if (await db.Attachments.AnyAsync(a => a.Sha256 == sha, ct)) continue;
            await using (await AttachmentLock.HoldAsync(db, sha, ct))
            {
                // 잠금 안에서 다시 확인: 방금 같은 내용의 업로드가 행을 넣었을 수 있다(그 업로드는 이 파일을 "이미 있음"으로 보고 옮기지 않았다).
                if (!await db.Attachments.AnyAsync(a => a.Sha256 == sha, ct) && store.TryDelete(storagePath)) orphans++;
            }
        }

        var missing = 0;
        await foreach (var path in db.Attachments.AsNoTracking().OrderBy(a => a.Id).Select(a => a.StoragePath).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!store.Exists(path)) missing++;
        }
        if (missing > 0) logger.LogWarning("파일이 없는 첨부 행 {Count}건. 같은 이미지를 다시 올리면 복구된다.", missing);
        return new SweepResult(temp, orphans, missing);
    }

    // private 헬퍼: 상용구 remarks 없이 판단 근거만 인라인으로 남긴다 — 임시 파일 삭제 실패(잠김·권한)는 다음 스윕에서 다시 시도하면 되므로 예외로 스윕 전체를 멈추지 않는다.
    private bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("임시 파일 정리 실패. TempFile={TempFile}", Path.GetFileName(path));
            return false;
        }
    }
}
