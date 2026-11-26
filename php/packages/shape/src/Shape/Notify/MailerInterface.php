<?php

declare(strict_types=1);

namespace Shape\Notify;

/**
 * Sends one e-mail. Implementations throw \RuntimeException on failure so the notifier can retry.
 * Plug in your own (PHPMailer, a transactional-mail API) by implementing this interface and passing
 * the instance to NotifyCommand::run().
 */
interface MailerInterface
{
    /**
     * @throws \RuntimeException when the message could not be handed over
     */
    public function send(string $to, string $subject, string $text, ?string $html = null): void;
}
