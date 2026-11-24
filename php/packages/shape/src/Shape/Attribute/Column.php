<?php

declare(strict_types=1);

namespace Shape\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_PROPERTY)]
final class Column
{
    public function __construct(
        public readonly ?string $type = null,
        public readonly ?int $length = null,
        public readonly ?int $precision = null,
        public readonly ?int $scale = null,
        public readonly bool $unsigned = false,
        /** @var list<string>|null */
        public readonly ?array $values = null,
        public readonly ?string $description = null,
    ) {}
}
