# First deployment on shared hosting (PHP + MySQL)

Checklist for deploying a Shape app (example: bookshop) to a typical shared host.
Not yet verified on a real shared host: do the first run on a throwaway site.

## Before you upload

- [ ] Host runs **PHP 8.2+** with `pdo_mysql`, `mbstring`, `json`, `openssl` (check `php -m` or the host's PHP selector).
- [ ] A MySQL/MariaDB database and user exist; note host, name, user, password. The host is often not `localhost`.
- [ ] HTTPS is enabled for the domain (tokens travel in headers).
- [ ] No Composer needed: `packages/autoload.php` is bundled.

## Layout

Keep code **outside** the web root; only `public/` is exposed.

```
/home/you/shape-app/
  packages/                 <- not web-accessible
  apps/bookshop/
    bootstrap.php, config/, entities/, database/
    public/                 <- point the domain's document root here
```

- [ ] If you cannot change the document root, upload only the contents of `public/` into `public_html/` and edit the `require_once` path in `api.php` to the real `bootstrap.php` location.
- [ ] `config/`, `packages/`, `database/` are not reachable by URL (test: request `/config/database.php`, expect 403/404).
- [ ] Never upload `.sqlite` files, `.env*`, or test data.

## Database

- [ ] Import the schema: `mysql -u USER -p DBNAME < apps/bookshop/database/schema.mysql.sql` (or phpMyAdmin, Import).
- [ ] Create the first user. Hash the password locally with `php -r 'echo password_hash("CHOOSE-A-PASSWORD", PASSWORD_DEFAULT);'` and insert it into `users` with role `admin`.
- [ ] Delete the sample rows if this is not a demo.

## Config

- [ ] Copy `config/env.example.php` to `config/env.local.php` **on the server** and fill in:
  - `db.dsn` = `mysql:host=HOST;dbname=DBNAME;charset=utf8mb4`, plus `user` / `pass`
  - `auth.jwt_secret` = a long random value (`php -r 'echo bin2hex(random_bytes(32));'`). Set it explicitly; do not rely on the auto-generated secret.
  - `jwt_issuer` / `jwt_audience` for your domain
- [ ] `env.local.php` is not in version control and not world-readable (chmod 640 or 600 if the host allows).

## Writable paths

- [ ] The schema cache goes to `sys_get_temp_dir()/shape_cache`. Confirm the temp dir is writable, or configure the cache path explicitly.
- [ ] If the app writes `<project>/.shape/` (e.g. a generated secret), that directory must be writable and not web-accessible. With an explicit `jwt_secret` this matters less.

## Smoke test (in this order)

- [ ] The manifest endpoint returns JSON, not a PHP error page or a blank page.
- [ ] Login returns an access token; a wrong password returns 401.
- [ ] Authenticated `GET` on a table returns rows; decimals such as `price` are numbers, not strings.
- [ ] Create, update, delete one row.
- [ ] Violate a constraint (duplicate unique value, bad foreign key): expect HTTP 409 `CONSTRAINT_VIOLATION`, not 500.
- [ ] A request without a token returns 401, not data.
- [ ] `display_errors` is **off**; errors go to the host's log.

## Hardening

- [ ] Change or remove sample users and demo credentials.
- [ ] Decide roles per mask/table; do not leave tables open to the default role unintentionally.
- [ ] Restrict CORS to your own frontend origin.
- [ ] Back up the database before and after the first deploy; know how to restore.
- [ ] Rotate any secret that was ever pasted into chat, email or a repository.

## Common failures

| Symptom | Likely cause |
|---|---|
| Blank page / 500 | Wrong PHP version, missing extension, or wrong `require_once` path |
| `could not find driver` | `pdo_mysql` not enabled |
| 404 on `/api.php/...` paths | Server does not pass PATH_INFO; add a rewrite rule |
| Login works, next request 401 | `jwt_secret` changes between requests (not set, or `.shape/` unwritable) |
| Authorization header missing | Host strips it (common with CGI/FastCGI): add `SetEnvIf Authorization "(.*)" HTTP_AUTHORIZATION=$1` to `.htaccess` |
| Stale schema after altering tables | Clear `shape_cache` in the temp dir |
