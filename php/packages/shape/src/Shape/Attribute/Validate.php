<?php

declare(strict_types=1);

namespace Shape\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_PROPERTY | Attribute::IS_REPEATABLE)]
final class Validate
{
    public function __construct(
        public readonly string $rule,
        /** @var list<string> */
        public readonly array $values = [],
    ) {}
}
