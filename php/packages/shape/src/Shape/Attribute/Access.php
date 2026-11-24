<?php

declare(strict_types=1);

namespace Shape\Attribute;

use Attribute;

#[Attribute(Attribute::TARGET_CLASS)]
final class Access
{
    /**
     * @param list<string> $roles  All-operations roles (shorthand)
     * @param list<string> $read   Override roles for read masks
     * @param list<string> $write  Override roles for writable mask
     * @param list<string> $create Override roles for INSERT (falls back to write, then roles)
     * @param list<string> $update Override roles for UPDATE (falls back to write, then roles)
     * @param list<string> $delete Override roles for DELETE (falls back to write, then roles)
     */
    public function __construct(
        public readonly array $roles = [],
        public readonly array $read = [],
        public readonly array $write = [],
        public readonly array $create = [],
        public readonly array $update = [],
        public readonly array $delete = [],
    ) {}
}
