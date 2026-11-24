<?php

declare(strict_types=1);

namespace Shape\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_CLASS)]
final class Entity
{
    public function __construct(
        public readonly string $table,
        public readonly bool $writable = true,
        public readonly ?string $description = null,
        public readonly ?string $writableMode = null,
    ) {}
}
