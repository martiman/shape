<?php

declare(strict_types=1);

namespace Bookshop\Entity;

use Shape\Attribute\{Entity, Column, Id, AutoIncrement, FK, Validate, Access};

#[Entity('books')]
#[Access(roles: ['admin', 'user'])]
class Book
{
    #[Id, AutoIncrement]
    public int $id;

    #[Column(type: 'varchar', length: 255)]
    #[Validate('notEmpty')]
    public string $title;

    #[FK(entity: Author::class)]
    public int $author_id;

    #[Column(type: 'decimal', precision: 8, scale: 2)]
    #[Validate('positive')]
    public float $price;

    #[Column(type: 'varchar', length: 13)]
    public ?string $isbn = null;

    public int $stock = 0;
}
