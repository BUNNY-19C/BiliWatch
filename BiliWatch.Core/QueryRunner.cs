namespace BiliWatch.Core;

public sealed class QueryRunner(IBiliApi api)
{
    public static void Begin(IEnumerable<Anchor> rows)
    {
        foreach (var row in rows) { row.State = QueryState.Pending; row.Error = ""; row.Refresh(); }
    }
    public async Task RunAsync(IEnumerable<Anchor> rows, Guid run, Action changed, CancellationToken token)
    {
        foreach (var row in rows.ToArray())
        {
            token.ThrowIfCancellationRequested();
            row.State = QueryState.Running; row.Error = ""; row.Refresh(); changed();
            try
            {
                var result = await api.WatchAsync(row.Uid, token);
                row.LastSeconds = result.Seconds; row.UpdatedAt = DateTimeOffset.Now; row.SuccessRun = run;
                if (!string.IsNullOrEmpty(result.Name)) row.Name = result.Name;
                row.State = QueryState.Success;
            }
            catch (OperationCanceledException) { row.State = QueryState.Pending; throw; }
            catch (ApiException e)
            {
                row.State = QueryState.Failed; row.Error = e.Message;
                if (e.MustPause) throw;
            }
            finally { row.Refresh(); changed(); }
        }
    }
}
