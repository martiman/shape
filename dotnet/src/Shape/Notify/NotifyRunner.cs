using Shape.Auth;
using Shape.Core;

namespace Shape.Notify;

/// <summary>
/// Entry point for e-mail notifications — run it from cron / a scheduled task:
/// <code>
/// // Program.cs: the one-call host handles it —
/// await ShapeApp.Run(config, args);        // dotnet run -- --notify [--rule=NAME] [--dry-run] [--enqueue-only] [--send-only]
///
/// // or directly:
/// return NotifyRunner.Run(config, args);
/// </code>
/// Exit code 0 = all fine, 1 = a rule failed or a mail permanently failed.
/// </summary>
public static class NotifyRunner
{
    public static bool IsNotifyMode(string[] args) =>
        args.Any(a => string.Equals(a, "--notify", StringComparison.OrdinalIgnoreCase));

    public static int Run(ShapeConfig config, string[] args, IMailer? mailer = null, Action<string>? output = null)
    {
        var @out = output ?? Console.WriteLine;
        string? rule = null;
        bool dryRun = false, enqueueOnly = false, sendOnly = false;
        foreach (var a in args)
        {
            if (a.StartsWith("--rule=", StringComparison.Ordinal)) rule = a["--rule=".Length..];
            else if (a == "--dry-run") dryRun = true;
            else if (a == "--enqueue-only") enqueueOnly = true;
            else if (a == "--send-only") sendOnly = true;
        }

        if (config.Notifications == null)
        {
            @out("No 'Notifications' block in the config.");
            return 1;
        }

        try
        {
            var dispatcher = new OpDispatcher(config.Db, config.Auth, config.Masks, config.Validators, config.Commands,
                config.Runtime.AuditLogTable, config.CommandRoles, config.AuditDiff);
            using var db = dispatcher.CreateDatabase();
            var executor = dispatcher.CreateExecutor(db);
            var login = new LoginService(config.Db, config.Auth, new JwtIssuer(config.Auth));
            mailer ??= FromConfig(config.Notifications.Transport, @out);
            var notifier = new Notifier(db, executor, login, mailer, config.Notifications, config.Auth.UsersTable);

            var failed = false;
            if (!sendOnly)
            {
                var s = notifier.Enqueue(rule, dryRun);
                foreach (var (name, r) in s.Rules)
                {
                    @out($"{name}: {r.Queued} queued, {r.Skipped} skipped (already sent)");
                    foreach (var e in r.Errors) { @out($"  ERROR {name}: {e}"); failed = true; }
                }
            }
            if (!enqueueOnly)
            {
                var d = notifier.Deliver(dryRun);
                @out($"delivery: {d.Sent} {(dryRun ? "would be sent" : "sent")}, {d.Retry} to retry, {d.Failed} failed");
                failed = failed || d.Failed > 0;
            }
            return failed ? 1 : 0;
        }
        catch (Exception e)
        {
            @out("notify failed: " + e.Message);
            return 1;
        }
    }

    public static IMailer FromConfig(NotificationTransport t, Action<string>? output = null) => t.Driver switch
    {
        "log" => new LogMailer(output),
        "smtp" => new SmtpMailer(t),
        _ => throw new ArgumentException($"Unknown notifications transport driver '{t.Driver}' (log, smtp).")
    };
}
