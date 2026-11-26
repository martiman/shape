using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Shape.Auth;
using Shape.Core;

namespace Shape.Notify;

/// <summary>
/// E-mail notifications: a rule is "run this mask, mail the rows". Two phases so a mail-server hiccup never loses
/// or duplicates a notification: <see cref="Enqueue"/> writes one <c>notification_outbox</c> row per recipient
/// (de-duplicated), <see cref="Deliver"/> sends the pending rows with retries.
///
/// Who sees what follows the API's rules: <c>To.Users</c> runs the mask AS each user with the claims their own
/// token would carry (row scopes, tenancy, mask roles), so a notification never contains a row that user could
/// not open. <c>To.Column</c> / <c>To.Emails</c> run once in the explicit <c>RunAs</c> context. Mirror of the PHP
/// <c>Shape\Notify\Notifier</c>.
/// </summary>
public sealed class Notifier
{
    private const int MaxRowsCap = 199;
    private static readonly Regex Ident = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly Database _db;
    private readonly BatchExecutor _executor;
    private readonly LoginService _login;
    private readonly IMailer _mailer;
    private readonly NotificationsConfig _cfg;
    private readonly string _usersTable;
    private readonly Func<long> _clock;

    public sealed record RuleSummary(int Queued, int Skipped, List<string> Errors);
    public sealed record EnqueueSummary(int Queued, int Skipped, Dictionary<string, RuleSummary> Rules);
    public sealed record DeliverSummary(int Sent, int Failed, int Retry);

    private sealed record Batch(string To, List<Dictionary<string, object?>> Rows, int Total, int More);

    public Notifier(Database db, BatchExecutor executor, LoginService login, IMailer mailer,
        NotificationsConfig cfg, string usersTable = "users", Func<long>? clock = null)
    {
        _db = db;
        _executor = executor;
        _login = login;
        _mailer = mailer;
        _cfg = cfg;
        _usersTable = usersTable;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public EnqueueSummary Enqueue(string? onlyRule = null, bool dryRun = false)
    {
        if (onlyRule != null && !_cfg.Rules.ContainsKey(onlyRule))
            throw new ArgumentException($"Unknown notification rule '{onlyRule}'. Known: {string.Join(", ", _cfg.Rules.Keys)}.");

        var rules = new Dictionary<string, RuleSummary>();
        int queued = 0, skipped = 0;
        foreach (var (name, rule) in _cfg.Rules)
        {
            if (onlyRule != null && name != onlyRule) continue;
            int q = 0, sk = 0;
            var errors = new List<string>();
            try
            {
                foreach (var batch in Batches(name, rule))
                {
                    var mail = Render(name, rule, batch.Rows, batch.Total, batch.More);
                    var key = DedupeKey(name, batch.To, rule, batch.Rows);
                    if (RecentlySent(name, batch.To, key, rule.RepeatDays)) { sk++; continue; }
                    if (!dryRun) InsertOutbox(name, batch.To, key, mail);
                    q++;
                }
            }
            catch (Exception e)
            {
                // One broken rule (bad mask, unknown param) must not stop the others.
                errors.Add(e is ApiException a ? a.ApiCode + ": " + a.Message : e.Message);
            }
            rules[name] = new RuleSummary(q, sk, errors);
            queued += q;
            skipped += sk;
        }
        return new EnqueueSummary(queued, skipped, rules);
    }

    public DeliverSummary Deliver(bool dryRun = false, int limit = 100)
    {
        int sent = 0, failed = 0, retry = 0;
        var maxAttempts = Math.Max(1, _cfg.MaxAttempts);
        var t = _db.QuoteIdentifier(Outbox());
        var res = _db.Run(
            $"SELECT id, recipient, subject, body_text, body_html, attempts FROM {t} WHERE status = 'pending' AND attempts < @max ORDER BY id",
            new() { ["max"] = maxAttempts });
        var n = 0;
        foreach (var row in res.Rows)
        {
            if (++n > limit) break;
            var id = row[0];
            var attempts = Convert.ToInt32(row[5], CultureInfo.InvariantCulture);
            if (dryRun) { sent++; continue; }
            try
            {
                _mailer.Send(Convert.ToString(row[1])!, Convert.ToString(row[2])!, Convert.ToString(row[3])!,
                    row[4] == null ? null : Convert.ToString(row[4]));
                _db.Exec($"UPDATE {t} SET status = 'sent', attempts = attempts + 1, sent_at = @now, last_error = NULL WHERE id = @id",
                    new() { ["now"] = Now(), ["id"] = id });
                sent++;
            }
            catch (Exception e)
            {
                var a = attempts + 1;
                var final = a >= maxAttempts;
                var msg = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                _db.Exec($"UPDATE {t} SET status = @st, attempts = @a, last_error = @err WHERE id = @id",
                    new() { ["st"] = final ? "failed" : "pending", ["a"] = a, ["err"] = msg, ["id"] = id });
                if (final) failed++; else retry++;
            }
        }
        return new DeliverSummary(sent, failed, retry);
    }

    // ── recipients ────────────────────────────────────────────────────

    private IEnumerable<Batch> Batches(string name, NotificationRule rule)
    {
        if (string.IsNullOrEmpty(rule.Mask))
            throw new ArgumentException($"Rule '{name}' needs a 'Mask'.");
        var maxRows = Math.Max(1, Math.Min(MaxRowsCap, rule.MaxRows));
        var to = rule.To;

        if (to.Users != null)
        {
            foreach (var email in UserEmails(to.Users))
            {
                var ctx = _login.AuthContextForEmail(email);
                if (ctx == null) continue;
                var (rows, more) = RunMask(rule.Mask, rule.Params, ctx, maxRows);
                if (rows.Count > 0) yield return new Batch(email, rows, rows.Count + more, more);
            }
            yield break;
        }

        if (to.RunAs == null || string.IsNullOrEmpty(to.RunAs.GetValueOrDefault("role")?.ToString()))
            throw new ArgumentException($"Rule '{name}': 'To.Column' / 'To.Emails' need 'RunAs' with a role (the context the mask runs in).");

        // The context is explicit config; the system id keeps created_by / audit stamps recognisable.
        var runAs = new Dictionary<string, object?>(to.RunAs);
        runAs.TryAdd("id", 0);
        runAs.TryAdd("sub", "notifications");
        runAs.TryAdd("email", "notifications@localhost");
        var (all, moreRows) = RunMask(rule.Mask, rule.Params, runAs, MaxRowsCap);

        if (!string.IsNullOrEmpty(to.Column))
        {
            var groups = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var row in all)
            {
                var addr = (row.GetValueOrDefault(to.Column)?.ToString() ?? "").Trim().ToLowerInvariant();
                if (addr.Length > 0 && MimeMessage.IsEmail(addr))
                {
                    if (!groups.TryGetValue(addr, out var g)) groups[addr] = g = new();
                    g.Add(row);
                }
            }
            foreach (var (addr, g) in groups)
            {
                var shown = g.Take(maxRows).ToList();
                yield return new Batch(addr, shown, g.Count, g.Count - shown.Count);
            }
            yield break;
        }

        if (to.Emails != null)
        {
            if (all.Count == 0) yield break;
            var shown = all.Take(maxRows).ToList();
            foreach (var addr in to.Emails)
            {
                var a = (addr ?? "").Trim();
                if (MimeMessage.IsEmail(a))
                    yield return new Batch(a, shown, all.Count + moreRows, all.Count + moreRows - shown.Count);
            }
            yield break;
        }

        throw new ArgumentException($"Rule '{name}': 'To' must be one of Users / Column / Emails.");
    }

    private List<string> UserEmails(UserRecipients cfg)
    {
        var where = new List<string> { "is_active = 1" };
        var p = new Dictionary<string, object?>();
        if (cfg.Roles.Count > 0)
        {
            var names = cfg.Roles.Select((r, i) => "@role" + i).ToList();
            where.Add($"role IN ({string.Join(", ", names)})");
            for (var i = 0; i < cfg.Roles.Count; i++) p["role" + i] = cfg.Roles[i];
        }
        if (!string.IsNullOrEmpty(cfg.OptoutColumn))
        {
            if (!Ident.IsMatch(cfg.OptoutColumn)) throw new ArgumentException($"Invalid OptoutColumn '{cfg.OptoutColumn}'.");
            where.Add(_db.QuoteIdentifier(cfg.OptoutColumn) + " <> 0");   // a user who switched it off is skipped
        }
        if (!Ident.IsMatch(_usersTable)) throw new ArgumentException($"Invalid users table '{_usersTable}'.");
        var res = _db.Run($"SELECT email FROM {_db.QuoteIdentifier(_usersTable)} WHERE {string.Join(" AND ", where)} ORDER BY id", p);
        return res.Rows.Select(r => (Convert.ToString(r[0]) ?? "").Trim()).Where(MimeMessage.IsEmail).ToList();
    }

    private (List<Dictionary<string, object?>> Rows, int More) RunMask(
        string mask, Dictionary<string, object?> parameters, Dictionary<string, object?> ctx, int top)
    {
        var outcome = _executor.Execute(
            new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ref"] = "notify",
                    ["sql"] = mask,
                    ["params"] = new Dictionary<string, object?>(parameters),
                    ["options"] = new Dictionary<string, object?> { ["top"] = top + 1 }
                }
            },
            ctx);
        var first = ((List<Dictionary<string, object?>>)outcome["results"]!)[0];
        var cols = (List<string>)((Dictionary<string, object?>)first["meta"]!)["columns"]!;
        var rows = new List<Dictionary<string, object?>>();
        foreach (var r in (List<object?[]>)first["rows"]!)
        {
            var o = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < cols.Count; i++) o[cols[i]] = r[i];
            rows.Add(o);
        }
        var more = Math.Max(0, rows.Count - top);
        return (rows.Take(top).ToList(), more);
    }

    // ── rendering, dedupe, outbox ────────────────────────────────────

    private sealed record Mail(string Subject, string Text, string Html);

    private static Mail Render(string name, NotificationRule rule, List<Dictionary<string, object?>> rows, int total, int more)
    {
        string Interp(string s) => s.Replace("{count}", total.ToString(CultureInfo.InvariantCulture)).Replace("{rule}", name);
        var subject = MimeMessage.CleanHeader(Interp(rule.Subject));
        var intro = Interp(rule.Intro);
        var outro = Interp(rule.Outro);

        var columns = rule.Columns is { Count: > 0 } ? rule.Columns : rows.FirstOrDefault()?.Keys.ToList() ?? new List<string>();
        string Label(string c) => rule.Labels != null && rule.Labels.TryGetValue(c, out var l) ? l : c;
        static string H(string s) => WebUtility.HtmlEncode(s);
        static string Br(string s) => s.Replace("\n", "<br>");

        var text = new StringBuilder();
        if (intro.Length > 0) text.Append(intro).Append("\n\n");
        var html = new StringBuilder("<p>" + (intro.Length > 0 ? Br(H(intro)) : "") + "</p><table border=\"1\" cellpadding=\"6\" cellspacing=\"0\" style=\"border-collapse:collapse\"><tr>");
        foreach (var c in columns) html.Append("<th align=\"left\">").Append(H(Label(c))).Append("</th>");
        html.Append("</tr>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            var cells = new List<string>();
            foreach (var c in columns)
            {
                var v = Convert.ToString(row.GetValueOrDefault(c), CultureInfo.InvariantCulture) ?? "";
                cells.Add(v);
                html.Append("<td>").Append(H(v)).Append("</td>");
            }
            html.Append("</tr>");
            text.Append("- ").Append(string.Join(" | ", cells)).Append('\n');
        }
        html.Append("</table>");
        if (more > 0)
        {
            text.Append("… +").Append(more).Append('\n');
            html.Append("<p>… +").Append(more).Append("</p>");
        }
        if (outro.Length > 0)
        {
            text.Append('\n').Append(outro).Append('\n');
            html.Append("<p>").Append(Br(H(outro))).Append("</p>");
        }
        return new Mail(subject, text.ToString(), html.ToString());
    }

    /// <summary>
    /// Stable fingerprint of "this list to this person": the same items again within <c>RepeatDays</c> are not
    /// re-sent, a changed list (new or removed item) is.
    /// </summary>
    private static string DedupeKey(string rule, string to, NotificationRule cfg, List<Dictionary<string, object?>> rows)
    {
        var ids = rows
            .Select(r => r.TryGetValue(cfg.Key, out var v) && v != null
                ? Convert.ToString(v, CultureInfo.InvariantCulture)!
                : Sha256(System.Text.Json.JsonSerializer.Serialize(r)))
            .OrderBy(x => x, StringComparer.Ordinal);
        return Sha256(rule + "|" + to.ToLowerInvariant() + "|" + string.Join(",", ids));
    }

    private bool RecentlySent(string rule, string to, string key, int repeatDays)
    {
        var t = _db.QuoteIdentifier(Outbox());
        var since = DateTimeOffset.FromUnixTimeSeconds(_clock() - Math.Max(0, repeatDays) * 86400L).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var r = _db.Run(
            $"SELECT COUNT(*) FROM {t} WHERE rule = @r AND recipient = @to AND dedupe_key = @k AND created_at >= @since",
            new() { ["r"] = rule, ["to"] = to, ["k"] = key, ["since"] = since });
        return Convert.ToInt64(r.Rows[0][0], CultureInfo.InvariantCulture) > 0;
    }

    private void InsertOutbox(string rule, string to, string key, Mail mail)
    {
        var t = _db.QuoteIdentifier(Outbox());
        _db.Exec(
            $"INSERT INTO {t} (rule, recipient, dedupe_key, subject, body_text, body_html, status, attempts, created_at) VALUES (@r, @to, @k, @s, @t, @h, 'pending', 0, @now)",
            new()
            {
                ["r"] = rule, ["to"] = to, ["k"] = key,
                ["s"] = mail.Subject.Length > 255 ? mail.Subject[..255] : mail.Subject,
                ["t"] = mail.Text, ["h"] = mail.Html, ["now"] = Now()
            });
    }

    private string Outbox()
    {
        if (!Ident.IsMatch(_cfg.OutboxTable)) throw new ArgumentException($"Invalid OutboxTable '{_cfg.OutboxTable}'.");
        return _cfg.OutboxTable;
    }

    private string Now() => DateTimeOffset.FromUnixTimeSeconds(_clock()).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
