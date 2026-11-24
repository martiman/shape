<?php

declare(strict_types=1);

namespace Bookshop\Entity;

use Shape\Attribute\{Entity, Column, Id, AutoIncrement, Validate, Access};

#[Entity('authors')]
#[Access(roles: ['admin', 'user'])]
class Author
{
    #[Id, AutoIncrement]
    public int $id;

    #[Column(type: 'varchar', length: 120)]
    #[Validate('notEmpty')]
    public string $name;

    #[Column(type: 'varchar', length: 190)]
    #[Validate('email')]
    public ?string $email = null;

    #[Column(type: 'text')]
    public ?string $bio = null;
}
