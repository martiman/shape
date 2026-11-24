<?php

declare(strict_types=1);

namespace ShapeRuntime\Auth;

final class NullGuard implements AuthGuard
{
    public function authorize(array $headers): array
    {
        return [];
    }
}
