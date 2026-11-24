<?php

declare(strict_types=1);

spl_autoload_register(static function (string $class): void {
    $map = [
        'ShapeRuntime\\' => __DIR__ . '/shaperuntime/src/ShapeRuntime/',
        'Shape\\' => __DIR__ . '/shape/src/Shape/',
    ];
    foreach ($map as $prefix => $baseDir) {
        $len = strlen($prefix);
        if (strncmp($class, $prefix, $len) === 0) {
            $file = $baseDir . str_replace('\\', '/', substr($class, $len)) . '.php';
            if (is_file($file)) {
                require_once $file;
            }
        }
    }
});
