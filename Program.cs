namespace YaDiskFileSync;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string? updatedFrom = FlagValue(args, Updater.UpdatedFlag);
        string? rolledBack = FlagValue(args, Updater.RolledBackFlag);

        // после обновления старая копия отдаёт мьютекс не сразу — подождём её
        var mutex = new Mutex(false, @"Local\YaDiskFileSync.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(updatedFrom != null || rolledBack != null ? 30_000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { mutex.Dispose(); return; }

        UpdateHandoff? handoff;
        try
        {
            ApplicationConfiguration.Initialize();
            using (var app = new TrayApp(updatedFrom, rolledBack))
            {
                if (updatedFrom != null)
                {
                    // «готово» — только когда цикл сообщений реально заработал; старая версия ждёт этого, чтобы выйти
                    EventHandler? onIdle = null;
                    onIdle = (_, _) =>
                    {
                        Application.Idle -= onIdle;
                        try { Updater.SignalReady(); }
                        catch { /* не дождётся — старая версия откатит обновление, это лучше окна ошибки */ }
                    };
                    Application.Idle += onIdle;
                }
                Task.Run(Updater.Cleanup);
                Application.Run();
                handoff = app.Handoff;
            }
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }

        // значок убран, мьютекс и кнопки отпущены — новая версия может стартовать; ждём её отклика
        if (handoff != null) Updater.ConfirmOrRollback(handoff);
    }

    /// <summary>Значение после флага: "--updated 1.0.0" → "1.0.0"; флаг без значения → "".</summary>
    private static string? FlagValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i < 0 ? null : i + 1 < args.Length ? args[i + 1] : "";
    }
}
