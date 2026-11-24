<?php

declare(strict_types=1);

namespace Shape\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_PROPERTY)]
final class FK
{
    public function __construct(
        public readonly string $entity,
        public readonly ?string $column = null,
        public readonly string $onDelete = 'cascade',
        public readonly string $onUpdate = 'cascade',
    ) {}
}
