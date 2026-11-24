<?php

declare(strict_types=1);

namespace ShapeRuntime\Auth;

use ShapeRuntime\Core\ApiException;

final class JwtIssuer
{
    /** @param array<string, mixed> $config */
    public function __construct(private readonly array $config)
    {
    }

    /**
     * @param array<string, mixed> $claims
     */
    public function issue(array $claims, int $ttlSeconds): string
    {
        $secret = (string)($this->config['jwt_secret'] ?? '');
        if ($secret === '') {
            throw new ApiException('AUTH_CONFIG_ERROR', 'JWT secret is not configured.', 500);
        }

        if ($ttlSeconds < 60) {
            $ttlSeconds = 60;
        }

        $now = time();
        $header = [
            'alg' => 'HS256',
            'typ' => 'JWT',
        ];

        $payload = $claims;
        $payload['iat'] = $now;
        $payload['nbf'] = $now;
        $payload['exp'] = $now + $ttlSeconds;

        $issuer = $this->config['jwt_issuer'] ?? null;
        if (is_string($issuer) && $issuer !== '') {
            $payload['iss'] = $issuer;
        }

        $aud = $this->config['jwt_audience'] ?? null;
        if (is_string($aud) && $aud !== '') {
            $payload['aud'] = $aud;
        }

        $h64 = $this->base64UrlEncodeJson($header);
        $p64 = $this->base64UrlEncodeJson($payload);

        $sig = hash_hmac('sha256', $h64 . '.' . $p64, $secret, true);
        $s64 = rtrim(strtr(base64_encode($sig), '+/', '-_'), '=');

        return $h64 . '.' . $p64 . '.' . $s64;
    }

    /**
     * @param array<string, mixed> $payload
     */
    private function base64UrlEncodeJson(array $payload): string
    {
        $json = json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        if (!is_string($json)) {
            throw new ApiException('AUTH_CONFIG_ERROR', 'Failed to encode JWT payload.', 500);
        }
        return rtrim(strtr(base64_encode($json), '+/', '-_'), '=');
    }
}
