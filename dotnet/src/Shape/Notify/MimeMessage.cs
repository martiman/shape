using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shape.Notify;

/// <summary>Builds RFC 5322 / MIME messages (UTF-8, base64 bodies) and refuses header injection.</summary>
public static class MimeMessage
{
    private static readonly Regex Unsafe = new("[\\r\\n<>,;\"]", RegexOptions.Compiled);

    /// <summary>Reject CR/LF and anything that is not a plain address — a recipient is never trusted.</summary>
    public static string AssertAddress(string address)
    {
        address = (address ?? "").Trim();
        if (address.Length == 0 || Unsafe.IsMatch(address) || !IsEmail(address))
            throw new InvalidOperationException("Invalid e-mail address.");
        return address;
    }

    public static bool IsEmail(string a)
    {
        var at = a.IndexOf('@');
        if (at < 1 || at != a.LastIndexOf('@') || at == a.Length - 1) return false;
        if (a.Any(c => c <= ' ' || c > '~')) return false;   // ASCII only; no spaces
        var domain = a[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.') && !domain.Contains("..");
    }

    /// <summary>Header value without line breaks (a subject must never smuggle extra headers).</summary>
    public static string CleanHeader(string value) =>
        Regex.Replace(value ?? "", "[\\r\\n\\x00]+", " ").Trim();

    /// <summary>RFC 2047 encoded-word for any non-ASCII header text.</summary>
    public static string EncodeHeader(string value)
    {
        value = CleanHeader(value);
        return value.Any(c => c < ' ' || c > '~')
            ? "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?="
            : value;
    }

    private static string Base64Lines(string s)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder();
        for (var i = 0; i < b64.Length; i += 76)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append(b64, i, Math.Min(76, b64.Length - i));
        }
        return sb.ToString();
    }

    /// <summary>Headers (in order) plus the encoded body.</summary>
    public static (List<KeyValuePair<string, string>> Headers, string Body) Build(
        string from, string fromName, string to, string subject, string text, string? html)
    {
        from = AssertAddress(from);
        to = AssertAddress(to);
        var domain = from[(from.IndexOf('@') + 1)..];
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Date", DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)),
            new("From", fromName.Length > 0 ? $"{EncodeHeader(fromName)} <{from}>" : from),
            new("To", to),
            new("Subject", EncodeHeader(subject)),
            new("Message-ID", $"<{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}@{domain}>"),
            new("MIME-Version", "1.0"),
            new("Auto-Submitted", "auto-generated"),
        };

        if (html == null)
        {
            headers.Add(new("Content-Type", "text/plain; charset=UTF-8"));
            headers.Add(new("Content-Transfer-Encoding", "base64"));
            return (headers, Base64Lines(text));
        }

        var boundary = "=_shape_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        headers.Add(new("Content-Type", $"multipart/alternative; boundary=\"{boundary}\""));
        var body = $"--{boundary}\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{Base64Lines(text)}\r\n"
                 + $"--{boundary}\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{Base64Lines(html)}\r\n"
                 + $"--{boundary}--";
        return (headers, body);
    }
}
