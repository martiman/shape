<?php

declare(strict_types=1);

namespace Shape\Notify;

/** Writes "to + subject" to the PHP error log instead of sending (staging, first runs). Bodies are never logged. */
final class LogMailer implements MailerInterface
{
    public function send(string $to, string $subject, string $text, ?string $html = null): void
    {
        @error_log('[shape-notify] would send to ' . MimeMessage::assertAddress($to) . ': ' . MimeMessage::cleanHeader($subject));
    }
}
