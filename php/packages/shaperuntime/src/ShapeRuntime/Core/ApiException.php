<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

use RuntimeException;

final class ApiException extends RuntimeException
{
    /**
     * @param array<string, mixed> $details Optional structured fix-hints attached
     *   to the error envelope. Conventional keys:
     *     - 'expected': schema fragment describing what was expected
     *     - 'example':  minimal valid value/object the agent could submit
     *     - 'hint':     one-line plain-English guidance
     *   Any other keys are passed through verbatim. Stay backwards-compatible —
     *   consumers should treat the whole map as additive on top of code+message.
     */
    public function __construct(
        private readonly string $apiCode,
        string $message,
        private readonly int $status = 400,
        private readonly ?string $ref = null,
        private readonly array $details = []
    ) {
        parent::__construct($message);
    }

    public function apiCode(): string
    {
        return $this->apiCode;
    }

    public function status(): int
    {
        return $this->status;
    }

    public function ref(): ?string
    {
        return $this->ref;
    }

    /** @return array<string, mixed> */
    public function details(): array
    {
        return $this->details;
    }
}
