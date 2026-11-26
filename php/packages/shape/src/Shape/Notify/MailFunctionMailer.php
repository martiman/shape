<?php

declare(strict_types=1);

namespace Shape\Notify;

/**
 * PHP `mail()` transport — works on most shared hostings, which route it to their own sendmail.
 * Deliverability is the host's business; prefer SMTP with a real mailbox when mails land in spam.
 */
final class MailFunctionMailer implements MailerInterface
{
    public function __construct(
        private readonly string $from,
        private readonly string $fromName = '',
    ) {
    }

    public function send(string $to, string $subject, string $text, ?string $html = null): void
    {
        $m = MimeMessage::build($this->from, $this->fromName, $to, $subject, $text, $html);
        $headers = $m['headers'];
        unset($headers['To'], $headers['Subject']);   // mail() adds both itself
        $lines = [];
        foreach ($headers as $k => $v) {
            $lines[] = "{$k}: {$v}";
        }
        if (!@mail($m['headers']['To'], $m['headers']['Subject'], $m['body'], implode("\r\n", $lines))) {
            throw new \RuntimeException('mail() refused the message.');
        }
    }
}
