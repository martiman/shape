<?php

declare(strict_types=1);

namespace Shape\Notify;

/**
 * Keeps messages in memory instead of sending them. Used by tests and by `--driver=log` dry runs; set
 * `failFor` to simulate a transport failure for chosen recipients.
 */
final class CollectingMailer implements MailerInterface
{
    /** @var list<array{to:string, subject:string, text:string, html:?string}> */
    public array $sent = [];

    /** @param array<string, true> $failFor recipient => true makes send() throw */
    public function __construct(public array $failFor = [])
    {
    }

    public function send(string $to, string $subject, string $text, ?string $html = null): void
    {
        if (isset($this->failFor[$to])) {
            throw new \RuntimeException("simulated failure for {$to}");
        }
        $this->sent[] = ['to' => $to, 'subject' => $subject, 'text' => $text, 'html' => $html];
    }
}
