using Shape.Core;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Shape.Notify;

/// <summary>
/// Minimal dependency-free SMTP client: implicit TLS (<c>ssl</c>, port 465), STARTTLS (<c>tls</c>, 587) or plain
/// (<c>none</c>, local relays only), with AUTH PLAIN / LOGIN. One connection per message — fine for a cron job
/// that sends tens of mails; use a library or a mail API for bulk sending.
/// </summary>
public sealed class SmtpMailer : IMailer
{
    private readonly NotificationTransport _t;
    public SmtpMailer(NotificationTransport transport) => _t = transport;

    public void Send(string to, string subject, string text, string? html = null)
    {
        var (headers, body) = MimeMessage.Build(_t.From, _t.FromName, to, subject, text, html);
        var data = string.Concat(headers.Select(h => $"{h.Key}: {h.Value}\r\n")) + "\r\n" + body + "\r\n";
        data = Regex.Replace(data, "^\\.", "..", RegexOptions.Multiline);   // dot-stuffing

        using var tcp = new TcpClient();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _t.TimeoutSeconds));
        if (!tcp.ConnectAsync(_t.Host, _t.Port).Wait(timeout))
            throw new InvalidOperationException($"SMTP connect to {_t.Host}:{_t.Port} timed out.");
        tcp.ReceiveTimeout = tcp.SendTimeout = (int)timeout.TotalMilliseconds;

        Stream stream = tcp.GetStream();
        if (_t.Encryption == "ssl")
            stream = Tls(stream);

        var enc = new UTF8Encoding(false);
        string Read()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var line = ReadLine(stream);
                if (line == null) break;
                sb.Append(line).Append('\n');
                if (line.Length < 4 || line[3] != '-') break;
            }
            return sb.ToString();
        }
        string Expect(string reply, params int[] ok)
        {
            var code = reply.Length >= 3 && int.TryParse(reply.AsSpan(0, 3), out var c) ? c : 0;
            if (!ok.Contains(code))
                throw new InvalidOperationException("SMTP error: " + (reply.Trim().Length == 0 ? "no reply (timeout?)" : reply.Trim()));
            return reply;
        }
        string Cmd(string line, params int[] ok)
        {
            var b = enc.GetBytes(line + "\r\n");
            stream.Write(b, 0, b.Length);
            stream.Flush();
            return Expect(Read(), ok);
        }

        Expect(Read(), 220);
        var ehlo = Environment.MachineName.Length > 0 ? Environment.MachineName : "localhost";
        Cmd($"EHLO {ehlo}", 250);
        if (_t.Encryption == "tls")
        {
            Cmd("STARTTLS", 220);
            stream = Tls(stream);
            Cmd($"EHLO {ehlo}", 250);
        }
        if (!string.IsNullOrEmpty(_t.User))
        {
            try { Cmd("AUTH PLAIN " + Convert.ToBase64String(enc.GetBytes($"\0{_t.User}\0{_t.Pass}")), 235); }
            catch (InvalidOperationException)
            {
                Cmd("AUTH LOGIN", 334);
                Cmd(Convert.ToBase64String(enc.GetBytes(_t.User!)), 334);
                Cmd(Convert.ToBase64String(enc.GetBytes(_t.Pass ?? "")), 235);
            }
        }
        Cmd($"MAIL FROM:<{MimeMessage.AssertAddress(_t.From)}>", 250);
        Cmd($"RCPT TO:<{MimeMessage.AssertAddress(to)}>", 250, 251);
        Cmd("DATA", 354);
        var payload = enc.GetBytes(data + ".\r\n");
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
        Expect(Read(), 250);
        try { var q = enc.GetBytes("QUIT\r\n"); stream.Write(q, 0, q.Length); } catch { /* best effort */ }
    }

    private Stream Tls(Stream inner)
    {
        var ssl = new SslStream(inner, false);
        ssl.AuthenticateAsClient(_t.Host);
        return ssl;
    }

    private static string? ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (s.Read(buf, 0, 1) == 1)
        {
            if (buf[0] == (byte)'\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)buf[0]);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
