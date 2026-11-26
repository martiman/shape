namespace Shape.Notify;

/// <summary>
/// Sends one e-mail; throws on failure so the notifier can retry. Plug in your own transport (a mail API,
/// MailKit, …) by implementing this and passing it to <see cref="NotifyRunner"/>.
/// </summary>
public interface IMailer
{
    void Send(string to, string subject, string text, string? html = null);
}

/// <summary>Keeps messages in memory (tests, dry runs); <see cref="FailFor"/> simulates a transport failure.</summary>
public sealed class CollectingMailer : IMailer
{
    public List<(string To, string Subject, string Text, string? Html)> Sent { get; } = new();
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Send(string to, string subject, string text, string? html = null)
    {
        if (FailFor.Contains(to)) throw new InvalidOperationException($"simulated failure for {to}");
        Sent.Add((to, subject, text, html));
    }
}

/// <summary>Prints "to + subject" instead of sending (staging, first runs). Bodies are never logged.</summary>
public sealed class LogMailer : IMailer
{
    private readonly Action<string> _out;
    public LogMailer(Action<string>? output = null) => _out = output ?? Console.WriteLine;

    public void Send(string to, string subject, string text, string? html = null) =>
        _out("[shape-notify] would send to " + MimeMessage.AssertAddress(to) + ": " + MimeMessage.CleanHeader(subject));
}
