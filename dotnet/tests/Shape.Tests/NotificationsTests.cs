using System.Net;
using System.Net.Sockets;
using System.Text;
using Shape.Auth;
using Shape.Core;
using Shape.Notify;
using Xunit;

namespace Shape.Tests;

/// <summary>E-mail notifications — mirror of the PHP Notifications.phpt.</summary>
public sealed class NotificationsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"shape-notify-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly BatchExecutor _executor;
    private readonly LoginService _login;
    private long _now = 1_800_000_000;

    private static readonly Dictionary<string, ScopeConfig> Scopes = new(StringComparer.Ordinal)
    {
        ["owner_id"] = new ScopeConfig { Self = true, UnscopedRoles = new() { "admin" } }
    };

    public NotificationsTests()
    {
        var dbCfg = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_file}" };
        _db = new Database(dbCfg);
        _db.Exec("CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL, password_hash TEXT NOT NULL, display_name TEXT, role TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1, notify_email INTEGER NOT NULL DEFAULT 1)", new());
        _db.Exec("CREATE TABLE contracts (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id INTEGER NOT NULL, title TEXT NOT NULL, end_date TEXT NOT NULL)", new());
        _db.Exec("CREATE TABLE notification_outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, rule TEXT NOT NULL, recipient TEXT NOT NULL, dedupe_key TEXT NOT NULL, subject TEXT NOT NULL, body_text TEXT NOT NULL, body_html TEXT NULL, status TEXT NOT NULL DEFAULT 'pending', attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL, created_at TEXT NOT NULL, sent_at TEXT NULL)", new());
        foreach (var (e, r, active, notify) in new[]
        {
            ("alice@x.cz", "user", 1, 1), ("bob@x.cz", "user", 1, 1), ("root@x.cz", "admin", 1, 1),
            ("carol@x.cz", "user", 0, 1), ("evil@x.cz\nBcc: spy@x.cz", "user", 1, 1), ("dave@x.cz", "user", 1, 0)
        })
            _db.Exec("INSERT INTO users (email, password_hash, display_name, role, is_active, notify_email) VALUES (@e, 'x', @e, @r, @a, @o)",
                new() { ["e"] = e, ["r"] = r, ["a"] = active, ["o"] = notify });
        // ids: alice 1, bob 2, root 3, carol 4 (inactive), evil 5, dave 6 (opted out)
        _db.Exec("INSERT INTO contracts (owner_id, title, end_date) VALUES (1, 'A-lease', '2026-12-01'), (1, 'A-<b>support</b>', '2026-12-10'), (2, 'B-license', '2026-12-05'), (4, 'C-old', '2026-12-02'), (6, 'D-one', '2026-12-03')", new());

        const string baseSql = "SELECT c.id, c.title, c.end_date, u.email AS owner_email FROM contracts c JOIN users u ON u.id = c.owner_id WHERE c.end_date <= @until";
        var masks = new Dictionary<string, MaskConfig>
        {
            ["contracts.expiring"] = new()
            {
                Sql = baseSql + " AND c.owner_id = @__scope_owner_id ORDER BY c.end_date",
                SqlUnscoped = baseSql + " ORDER BY c.end_date",
                Params = new() { ["until"] = new ParamSchema { Type = "string", Required = true } },
                Roles = new() { "admin", "user" },
                RowScopes = new List<string> { "owner_id" },
                Description = "Contracts ending on or before a date."
            }
        };
        var auth = new AuthConfig
        {
            Driver = "jwt", JwtSecret = "test-secret-0123456789abcdef0123456789", JwtIssuer = "t", JwtAudience = "t",
            UsersTable = "users", Scopes = Scopes
        };
        _executor = new BatchExecutor(_db, new MaskRegistry(masks), new ValidatorRegistry(), null, Scopes);
        _login = new LoginService(dbCfg, auth, new JwtIssuer(auth));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_file); } catch { }
    }

    private Notifier Make(Dictionary<string, NotificationRule> rules, CollectingMailer? mailer = null) =>
        new(_db, _executor, _login, mailer ?? new CollectingMailer(),
            new NotificationsConfig { Rules = rules, MaxAttempts = 3 }, "users", () => _now);

    private sealed record Ob(string Rule, string Recipient, string Status, int Attempts, string Subject, string Text, string? Html);

    private List<Ob> Outbox() =>
        _db.Run("SELECT rule, recipient, status, attempts, subject, body_text, body_html FROM notification_outbox ORDER BY id", new()).Rows
            .Select(r => new Ob(Convert.ToString(r[0])!, Convert.ToString(r[1])!, Convert.ToString(r[2])!, Convert.ToInt32(r[3]),
                Convert.ToString(r[4])!, Convert.ToString(r[5])!, r[6] == null ? null : Convert.ToString(r[6]))).ToList();

    private static NotificationRule UserRule() => new()
    {
        Mask = "contracts.expiring",
        Params = new() { ["until"] = "2026-12-31" },
        To = new() { Users = new() { Roles = new() { "user" }, OptoutColumn = "notify_email" } },
        Subject = "Contracts: {count}\r\nBcc: spy@x.cz",
        Intro = "Ending soon:",
        Columns = new() { "title", "end_date" },
        Labels = new() { ["title"] = "Contract" }
    };

    [Fact]
    public void Users_mode_sends_each_person_only_what_they_may_see_and_dedupes()
    {
        var n = Make(new() { ["expiring"] = UserRule() });
        var s = n.Enqueue();
        Assert.Equal(2, s.Queued);                              // alice, bob
        Assert.Empty(s.Rules["expiring"].Errors);
        var ob = Outbox();
        Assert.Equal(new[] { "alice@x.cz", "bob@x.cz" }, ob.Select(o => o.Recipient).ToArray());
        Assert.Contains("A-lease", ob[0].Text);
        Assert.DoesNotContain("B-license", ob[0].Text);          // bob's contract never reaches alice
        Assert.Contains("B-license", ob[1].Text);
        Assert.DoesNotContain("A-lease", ob[1].Text);
        Assert.DoesNotContain("C-old", ob[0].Text + ob[1].Text); // inactive owner's row is nobody's
        Assert.Equal("Contracts: 2 Bcc: spy@x.cz", ob[0].Subject); // CR/LF removed: cannot inject a header
        Assert.Contains("&lt;b&gt;support&lt;/b&gt;", ob[0].Html);  // HTML escaped
        Assert.DoesNotContain("<b>support</b>", ob[0].Html);
        Assert.Contains("Contract", ob[0].Html);                 // label applied

        Assert.Equal(0, n.Enqueue().Queued);                     // same lists: nothing new
        _db.Exec("INSERT INTO contracts (owner_id, title, end_date) VALUES (2, 'B-new', '2026-12-20')", new());
        Assert.Equal(1, n.Enqueue().Queued);                     // only bob's list changed
        _now += 8 * 86400;
        var before = Outbox().Count;
        Assert.Equal(2, n.Enqueue(null, dryRun: true).Queued);   // dry run reports what WOULD be queued ...
        Assert.Equal(before, Outbox().Count);                    // ... and writes nothing
        Assert.Equal(2, n.Enqueue().Queued);                     // 7+ days later both lists repeat
    }

    [Fact]
    public void Unscoped_role_sees_every_row()
    {
        var n = Make(new()
        {
            ["all"] = new() { Mask = "contracts.expiring", Params = new() { ["until"] = "2026-12-31" }, To = new() { Users = new() { Roles = new() { "admin" } } }, Columns = new() { "title" } }
        });
        n.Enqueue();
        var all = Outbox();
        Assert.Single(all);
        Assert.Equal("root@x.cz", all[0].Recipient);
        foreach (var t in new[] { "A-lease", "B-license", "C-old", "D-one" }) Assert.Contains(t, all[0].Text);
    }

    [Fact]
    public void Column_and_emails_modes_run_as_explicit_context_and_bad_rules_do_not_stop_others()
    {
        var n = Make(new()
        {
            ["by_owner"] = new() { Mask = "contracts.expiring", Params = new() { ["until"] = "2026-12-31" }, To = new() { Column = "owner_email", RunAs = new() { ["role"] = "admin" } }, Columns = new() { "title" } },
            ["legal"] = new() { Mask = "contracts.expiring", Params = new() { ["until"] = "2026-12-04" }, To = new() { Emails = new() { "legal@x.cz", "bad\naddr" }, RunAs = new() { ["role"] = "admin" } }, Columns = new() { "title" } },
            ["no_run_as"] = new() { Mask = "contracts.expiring", Params = new() { ["until"] = "2026-12-31" }, To = new() { Emails = new() { "x@x.cz" } } },
            ["bad_mask"] = new() { Mask = "nope.nothing", To = new() { Users = new() } }
        });
        var s = n.Enqueue();
        var by = Outbox().Where(o => o.Rule == "by_owner").Select(o => o.Recipient).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "alice@x.cz", "bob@x.cz", "carol@x.cz", "dave@x.cz" }, by);
        Assert.Equal(new[] { "legal@x.cz" }, Outbox().Where(o => o.Rule == "legal").Select(o => o.Recipient).ToArray()); // injected address skipped
        Assert.Single(s.Rules["no_run_as"].Errors);              // misconfigured rule reported ...
        Assert.Single(s.Rules["bad_mask"].Errors);
        Assert.Equal(1, s.Rules["legal"].Queued);                // ... and does not stop the others
    }

    [Fact]
    public void Delivery_marks_sent_retries_then_fails_without_blocking_others()
    {
        var mailer = new CollectingMailer();
        mailer.FailFor.Add("bob@x.cz");
        var n = Make(new() { ["expiring"] = UserRule() }, mailer);
        n.Enqueue();
        Assert.Equal(new Notifier.DeliverSummary(1, 0, 1), n.Deliver());
        Assert.Equal(new[] { "alice@x.cz" }, mailer.Sent.Select(m => m.To).ToArray());
        Assert.Equal(new Notifier.DeliverSummary(0, 0, 1), n.Deliver());
        Assert.Equal(new Notifier.DeliverSummary(0, 1, 0), n.Deliver());   // third attempt: permanently failed
        var rows = Outbox();
        Assert.Equal(new[] { "sent", "failed" }, rows.Select(r => r.Status).ToArray());
        Assert.Equal(3, rows[1].Attempts);
        Assert.Equal(new Notifier.DeliverSummary(0, 0, 0), n.Deliver());   // nothing left to do
    }

    [Fact]
    public void Mime_refuses_header_injection_and_encodes_non_ascii()
    {
        Assert.Throws<InvalidOperationException>(() => MimeMessage.AssertAddress("a@b.cz\r\nBcc: x@y.cz"));
        Assert.Throws<InvalidOperationException>(() => MimeMessage.AssertAddress("Name <a@b.cz>"));
        var (headers, _) = MimeMessage.Build("from@x.cz", "Šéf", "to@x.cz", "Příliš žluťoučký\nBcc: z@z.cz", "text", "<p>html</p>");
        var h = headers.ToDictionary(x => x.Key, x => x.Value);
        Assert.StartsWith("=?UTF-8?B?", h["Subject"]);
        Assert.DoesNotContain('\n', h["Subject"]);
        Assert.StartsWith("multipart/alternative", h["Content-Type"]);
    }

    [Fact]
    public async Task Smtp_transport_speaks_to_a_local_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var log = new StringBuilder();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            async Task Say(string s) { var b = Encoding.ASCII.GetBytes(s + "\r\n"); await stream.WriteAsync(b); }
            await Say("220 fake ESMTP");
            var data = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                log.AppendLine(line);
                if (data) { if (line == ".") { data = false; await Say("250 queued"); } continue; }
                var cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line;
                if (cmd == "EHLO") { await Say("250-fake"); await Say("250 AUTH PLAIN"); }
                else if (cmd == "AUTH") await Say("235 ok");
                else if (cmd is "MAIL" or "RCPT") await Say("250 ok");
                else if (cmd == "DATA") { data = true; await Say("354 go"); }
                else if (cmd == "QUIT") { await Say("221 bye"); break; }
                else await Say("500 what");
            }
        });

        new SmtpMailer(new NotificationTransport { Host = "127.0.0.1", Port = port, Encryption = "none", User = "user", Pass = "secret", From = "noreply@x.cz", FromName = "Shape" })
            .Send("alice@x.cz", "Smlouvy: žluťoučký kůň", "Dobrý den\n.tečka na začátku řádku", "<p>Dobrý den</p>");
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        var text = log.ToString();
        Assert.Contains("RCPT TO:<alice@x.cz>", text);
        Assert.Contains("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0user\0secret")), text);
        Assert.Contains("Subject: =?UTF-8?B?", text);
        Assert.Contains("multipart/alternative", text);
    }
}
