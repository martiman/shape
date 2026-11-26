<?php

declare(strict_types=1);

namespace Shape\Notify;

/**
 * Builds RFC 5322 / MIME messages (UTF-8, base64 bodies) and refuses header injection. Shared by the
 * `mail()` and SMTP mailers.
 */
final class MimeMessage
{
    /** Reject CR/LF and anything that is not a plain address — a recipient is never trusted. */
    public static function assertAddress(string $address): string
    {
        $address = trim($address);
        if ($address === '' || preg_match('/[\r\n<>,;"]/', $address) === 1 || filter_var($address, FILTER_VALIDATE_EMAIL) === false) {
            throw new \RuntimeException('Invalid e-mail address.');
        }
        return $address;
    }

    /** Header value without line breaks (a subject must never smuggle extra headers). */
    public static function cleanHeader(string $value): string
    {
        return trim((string)preg_replace('/[\r\n\x00]+/', ' ', $value));
    }

    /** RFC 2047 encoded-word for any non-ASCII header text. */
    public static function encodeHeader(string $value): string
    {
        $value = self::cleanHeader($value);
        return preg_match('/[^\x20-\x7E]/', $value) === 1
            ? '=?UTF-8?B?' . base64_encode($value) . '?='
            : $value;
    }

    /**
     * @return array{headers: array<string, string>, body: string} headers (without To/Subject handling by the
     *         caller's transport) and the encoded body
     */
    public static function build(string $from, string $fromName, string $to, string $subject, string $text, ?string $html): array
    {
        $from = self::assertAddress($from);
        $to = self::assertAddress($to);
        $fromHeader = $fromName !== '' ? self::encodeHeader($fromName) . " <{$from}>" : $from;

        $headers = [
            'Date' => date('r'),
            'From' => $fromHeader,
            'To' => $to,
            'Subject' => self::encodeHeader($subject),
            'Message-ID' => '<' . bin2hex(random_bytes(12)) . '@' . (substr(strrchr($from, '@') ?: '@localhost', 1) ?: 'localhost') . '>',
            'MIME-Version' => '1.0',
            'Auto-Submitted' => 'auto-generated',
        ];

        $enc = static fn(string $s): string => rtrim(chunk_split(base64_encode($s), 76, "\r\n"), "\r\n");
        if ($html === null) {
            $headers['Content-Type'] = 'text/plain; charset=UTF-8';
            $headers['Content-Transfer-Encoding'] = 'base64';
            return ['headers' => $headers, 'body' => $enc($text)];
        }

        $boundary = '=_shape_' . bin2hex(random_bytes(8));
        $headers['Content-Type'] = "multipart/alternative; boundary=\"{$boundary}\"";
        $body = "--{$boundary}\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" . $enc($text) . "\r\n"
            . "--{$boundary}\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" . $enc($html) . "\r\n"
            . "--{$boundary}--";
        return ['headers' => $headers, 'body' => $body];
    }
}
