<?php

declare(strict_types=1);

namespace ShapeRuntime\Auth;

interface AuthGuard
{
    /**
     * @param array<string, string> $headers
     * @return array<string, mixed>
     */
    public function authorize(array $headers): array;
}
