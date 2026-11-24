<?php

declare(strict_types=1);

namespace Shape\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_CLASS | Attribute::IS_REPEATABLE)]
final class Unique
{
    /**
     * @param list<string> $columns
     */
    public function __construct(
        public readonly array $columns = [],
    ) {}
}
