using Shape.Auth;
using Shape.Core;

namespace Shape.Notify;

/// <summary>
/// Wires event notifications into an executor: after a committed mutation the rules with an <c>On</c> trigger are
/// evaluated and mailed immediately (see <see cref="Notifier.OnChanges"/>). Created by <see cref="OpDispatcher"/>
/// when the config has any event rule.
/// </summary>
public sealed class NotificationsHook
{
    private readonly NotificationsConfig _cfg;
    private readonly DatabaseConfig _db;
    private readonly AuthConfig _auth;

    public NotificationsHook(NotificationsConfig cfg, DatabaseConfig db, AuthConfig auth)
    {
        _cfg = cfg;
        _db = db;
        _auth = auth;
    }

    public void Attach(BatchExecutor executor, Database database)
    {
        executor.SetChangeListener(changes =>
        {
            var notifier = new Notifier(
                database, executor, new LoginService(_db, _auth, new JwtIssuer(_auth)),
                NotifyRunner.FromConfig(_cfg.Transport), _cfg, _auth.UsersTable);
            notifier.OnChanges(changes);
        });
    }
}
