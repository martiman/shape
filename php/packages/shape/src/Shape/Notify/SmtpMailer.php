<?php

declare(strict_types=1);

namespace Shape\Notify;

/**
 * Minimal SMTP client (no dependencies): implicit TLS (`ssl`, port 465), STARTTLS (`tls`, port 587) or
 * plain (`none`, local relays only), with AUTH PLAIN / LOGIN. One connection per message — fine for
 * a cron job that sends tens of mails; use a library or a mail API for bulk sending.
 */
final class SmtpMailer implements MailerInterface
{
    public function __construct(
        private readonly string $host,
        private readonly int $port,
        private readonly string $encryption,   // 'tls' | 'ssl' | 'none'
        private readonly ?string $user,
        private readonly ?string $pass,
        private readonly string $from,
        private readonly string $fromName = '',
        private readonly int $timeout = 15,
    ) {
    }

    public function send(string $to, string $subject, string $text, ?string $html = null): void
    {
        $m = MimeMessage::build($this->from, $this->fromName, $to, $subject, $text, $html);
        $data = '';
        foreach ($m['headers'] as $k => $v) {
            $data .= "{$k}: {$v}\r\n";
        }
        $data .= "\r\n" . $m['body'] . "\r\n";
        $data = (string)preg_replace('/^\./m', '..', $data);   // dot-stuffing

        $remote = ($this->encryption === 'ssl' ? 'ssl://' : 'tcp://') . $this->host . ':' . $this->port;
        $fp = @stream_socket_client($remote, $errno, $errstr, $this->timeout);
        if ($fp === false) {
            throw new \RuntimeException("SMTP connect to {$this->host}:{$this->port} failed: {$errstr} ({$errno})");
        }
        stream_set_timeout($fp, $this->timeout);

        try {
            $this->expect($fp, [220]);
            $ehlo = gethostname() ?: 'localhost';
            $this->command($fp, "EHLO {$ehlo}", [250]);
            if ($this->encryption === 'tls') {
                $this->command($fp, 'STARTTLS', [220]);
                if (@stream_socket_enable_crypto($fp, true, STREAM_CRYPTO_METHOD_TLS_CLIENT) !== true) {
                    throw new \RuntimeException('SMTP STARTTLS handshake failed.');
                }
                $this->command($fp, "EHLO {$ehlo}", [250]);
            }
            if ($this->user !== null && $this->user !== '') {
                $this->authenticate($fp);
            }
            $this->command($fp, 'MAIL FROM:<' . MimeMessage::assertAddress($this->from) . '>', [250]);
            $this->command($fp, 'RCPT TO:<' . MimeMessage::assertAddress($to) . '>', [250, 251]);
            $this->command($fp, 'DATA', [354]);
            fwrite($fp, $data . ".\r\n");
            $this->expect($fp, [250]);
            @fwrite($fp, "QUIT\r\n");
        } finally {
            @fclose($fp);
        }
    }

    /** @param resource $fp */
    private function authenticate($fp): void
    {
        $plain = base64_encode("\0{$this->user}\0{$this->pass}");
        try {
            $this->command($fp, "AUTH PLAIN {$plain}", [235]);
        } catch (\RuntimeException) {
            $this->command($fp, 'AUTH LOGIN', [334]);
            $this->command($fp, base64_encode((string)$this->user), [334]);
            $this->command($fp, base64_encode((string)$this->pass), [235]);
        }
    }

    /**
     * @param resource $fp
     * @param list<int> $ok
     */
    private function command($fp, string $line, array $ok): string
    {
        fwrite($fp, $line . "\r\n");
        return $this->expect($fp, $ok);
    }

    /**
     * Read a (possibly multi-line) reply and check its code.
     *
     * @param resource $fp
     * @param list<int> $ok
     */
    private function expect($fp, array $ok): string
    {
        $reply = '';
        while (($line = fgets($fp, 1024)) !== false) {
            $reply .= $line;
            if (strlen($line) < 4 || $line[3] !== '-') {
                break;
            }
        }
        $code = (int)substr($reply, 0, 3);
        if (!in_array($code, $ok, true)) {
            // Never echo AUTH payloads; the server's reply text is what a human needs.
            throw new \RuntimeException('SMTP error: ' . trim($reply === '' ? 'no reply (timeout?)' : $reply));
        }
        return $reply;
    }
}
