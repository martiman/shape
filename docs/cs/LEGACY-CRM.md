# Jak Shape pomáhá přepsat starý PHP CRM systém

> Tento dokument je v češtině záměrně — adresuje typický český CRM scénář (Pohoda/Money/ABRA integrace, GDPR, Entra ID SSO, vícejazyčnost). Ostatní dokumentace zůstává v angličtině.

## Krátká verze

Shape **nemusíte přepisovat data ani aplikaci najednou**. Stačí Shape namířit na vaši stávající databázi a okamžitě máte moderní REST / JSON / OData / MCP / OpenAPI surface postavený nad současným schématem. Legacy PHP aplikace běží dál — Shape jí jen postaví moderní API vrstvu nad těmi samými tabulkami. Migraci pak rozdělíte na fáze a nahrazujete UI a endpointy podle priority, ne podle velikosti databáze.

---

## Typický stav starého PHP CRM

Tento dokument předpokládá, že váš legacy CRM vypadá zhruba takto:

- Procedurální PHP nebo raná OOP, často z let 2010–2018
- SQL dotazy roztroušené napříč desítkami souborů (`mysqli_query` / `pdo->query` → `while ($row = ...)`)
- Vlastní autentizace: plaintext hesla, MD5/SHA1 hashe, žádné refresh tokeny, sessiony v `$_SESSION`
- Stránky míchají HTML, byznysovou logiku a SQL v jednom souboru
- Žádné API — frontend a backend jsou jeden monolit
- Validace ručně psaná na každém formuláři
- Senior, který kód napsal, dávno odešel; dokumentace prakticky neexistuje
- GDPR Článek 30 (záznamy o zpracování) řešený jen "občas si vezmu zálohu"

Přepsat to na Symfony / Laravel od nuly je obvykle projekt na 6–12 měsíců s vysokým rizikem, že se mezitím změní byznysové potřeby a rozpočet utrhne. Shape nabízí jiný přístup: **inkrementální koexistenci**.

---

## Co Shape přidá nad váš existující CRM

### 1. API se vygeneruje samo ze schématu databáze

Shape umí *database-first introspekci* — podívá se na vaši databázi (MySQL / PostgreSQL / SQLite), přečte tabulky, sloupce, FK, primární klíče a vygeneruje:

- **REST / JSON API** — `GET /api.php/customers/42`, `POST /api.php` pro mutace
- **OData query syntax** — `?$top=20&$skip=40&$orderby=created_at desc&$filter=country eq 'CZ'`
- **OpenAPI specifikace** — `?openapi`, naimportovatelná do Postmana / Swagger UI / SDK generátorů
- **MCP server** — pro Claude Desktop / Cursor / Cline (AI agenti vidí váš backend okamžitě)
- **LLM tool definice** — pro Anthropic / OpenAI agent integrace

Žádné entity třídy, žádné kontrolery, žádné DTO. Schéma je kontrakt.

### 2. Modernější autentizace bez migrace hesel

- **Lokální JWT** přes vaši stávající `users` tabulku — Shape umí pracovat s libovolným `password_hash` sloupcem, který akceptuje `password_verify()`. Pokud máte starší MD5/SHA1, stačí jednoduchý migrace-on-login skript: při příštím loginu uživatele rehashujete na bcrypt.
- **OIDC** pro Microsoft Entra ID / Azure AD, Auth0, Okta, Google, Cognito — když chcete SSO bez migrace hesel vůbec.
- Obě cesty koexistují podle `alg` headeru tokenu.
- **Audit log** — kdo co kdy změnil — pomocí konvence (existující nebo nově přidaná tabulka `audit_log`).
- **Role-based access** na každém endpointu — deklarováno jednou, vynucováno všude.

### 3. Konvence místo konfigurace

| Stávající stav databáze | Shape konvence | Přínos |
|---|---|---|
| Sloupec `deleted_at TIMESTAMP NULL` | Soft-delete automatický | DELETE = UPDATE; reads filtrují tombstoned řádky bez kódu |
| Sloupce `created_at` / `created_by` / `updated_at` / `updated_by` | Auto-stamp na insertu/updatu | GDPR audit "kdo to udělal kdy" zdarma |
| Tabulka `audit_log` (s konvenčním schématem) | Tier-1 audit trail | Každá mutace zaznamenána; built-in admin masky |
| FK sloupec `customer_id` v `orders` | Automatický `orders.byCustomerId` mask | "Najdi objednávky zákazníka" bez psaní SQL |
| `NOT NULL VARCHAR(190)` | Auto `notEmpty` + `maxLen:190` validátor | Validace pro každé pole zdarma |

Tyto konvence stačí přidat do schématu — žádný PHP kód.

### 4. Postupná migrace, ne big-bang

Klíčový rozdíl proti Symfony/Laravel přepisu od nuly:

- **Den 1**: Shape namířen na stávající databázi. Nový frontend (React / Vue / Next.js / Svelte) volá Shape API. Legacy PHP UI běží dál na stejných datech.
- **Týden 1–2**: Replikujete v novém frontendu nejčastěji používané obrazovky CRM. Shape má všechny CRUD operace připravené; vy jen stavíte UI komponenty.
- **Měsíc 1–3**: Speciální reporty a workflows píšete jako Shape *masky* (ad-hoc SQL s rolemi a popisem) nebo *commands* (PHP closure pro byznysovou logiku). Legacy UI se postupně vypíná, jak nový frontend pokrývá víc funkcionality.
- **Měsíc 6+**: Legacy aplikace je vypnuta. Shape API zůstává a podporuje libovolný počet frontendů (web, mobilní app, partner integrace, AI agenti).

V žádné fázi neztrácíte data; obě aplikace pracují nad stejným schématem.

---

## Konkrétní CRM use-cases

### Zákaznická tabulka s GDPR požadavky

Stávající CRM má `customers (id, name, email, phone, deleted_at, ...)`. Shape automaticky:

- `GET /api.php/customers` — list s `$top` / `$skip` / `$filter` / `$orderby`
- `GET /api.php/customers/42` — detail s ETag headerem (CDN-cacheable)
- `POST /api.php` `{op:'mutation', payload:{mutations:[{table:'customers', action:'update', filter:{id:42}, data:{phone:'...'}}]}}` — update s automatickým audit logem
- DELETE → soft-delete (díky `deleted_at`); GDPR "právo být zapomenut" je buď nastavení `deleted_at = CURRENT_TIMESTAMP`, nebo samostatný příkaz `customers.purge`, který data fyzicky smaže.

### Reporting endpoint pro management

```php
'masks' => [
    'reports.topCustomersByRevenue' => [
        'sql' => 'SELECT c.id, c.name, SUM(o.total) AS revenue
                  FROM customers c JOIN orders o ON o.customer_id = c.id
                  WHERE o.created_at >= :since
                  GROUP BY c.id ORDER BY revenue DESC LIMIT 20',
        'params' => ['since' => ['type' => 'string', 'required' => true]],
        'roles' => ['admin', 'manager'],
        'description' => 'Top 20 zákazníků podle obratu od daného data.',
        'cache_control' => 'private, max-age=300',
    ],
],
```

To je 8 řádků konfigurace. Stávající legacy reporting page má pravděpodobně 200+ řádků mixu PHP/SQL/HTML. Mask se navíc objeví v manifestu, OpenAPI specu i MCP nástrojích — pro AI agenta i Postman uživatele najednou.

### File uploady (smlouvy, přílohy, sken faktury)

```php
'uploads' => [
    'contracts' => [
        'storage'    => ['driver' => 'local', 'path' => '/var/uploads/contracts', 'public_url' => '/uploads/contracts'],
        'roles'      => ['admin', 'manager'],
        'max_size'   => 10485760,                 // 10 MB
        'mime_types' => ['application/pdf'],
    ],
],
```

`POST /api.php/upload/contracts` s `multipart/form-data` vrací URL. Pak běžná mutace navazuje URL na `customers.contract_url` nebo `invoices.attachment_url`. Mime type detekován z obsahu souboru (ne z client headeru) — žádné nahrávání spustitelných souborů s falešným `.pdf` headerem.

### Sync s českým ekosystémem (Pohoda, Money, ABRA, ARES)

Pro netriviální byznysovou logiku — třeba "vystavit fakturu a odeslat ji do Pohoda XML interface" — používáte Shape commands:

```php
'commands' => [
    'invoice.issue' => function (array $params, CommandContext $ctx): array {
        // 1. Insert invoice row do databáze (s audit logem)
        // 2. Vygenerovat PDF
        // 3. Zavolat Pohoda XML API přes externí klienta
        // 4. Vrátit invoice_id + URL PDF
        // 5. Případný rollback při selhání kroku 3 (transakční)
    },
],
```

Příkaz se objevuje v manifestu, OpenAPI specu, MCP nástrojích — automaticky. Frontend ho vidí, AI agent ho vidí, partner API ho může volat.

---

## Co Shape neřeší (buďme přímí)

- **UI vrstvu** — Shape je pouze backend. Frontend si stavíte sami (React, Vue, Next.js, …).
- **Workflow engine** ve smyslu Sugar CRM / SuiteCRM — pokud máte vizuální workflow editor, ten musíte rebuildovat ručně jako commands.
- **Migrace dat** — nedělá. Vaše data zůstávají v legacy schématu (což je *cíl* — neriskujete data ani downtime).
- **E-mailové kampaně, push notifikace, SMS** — to je doménová logika, kterou píšete jako commands nebo externí mikroslužby.

Pro tyto věci doporučujeme: nový frontend + Shape backend + samostatné mikroslužby pro email / notifikace, integrované přes Shape commands.

---

## Proč to funguje pro českou realitu

- **GDPR Článek 30 (záznamy o zpracování)**: Shape audit log konvence (`audit_log` tabulka detekovaná automaticky) zaznamenává každou mutaci s `actor` / `table` / `row_id` / `changed_columns`. Splňuje compliance bez extra práce.
- **EU / Czech autentizace (Entra ID, případně NIA brána přes proxy)**: OIDC integrace s Microsoft Entra ID je out-of-the-box přes preset `'preset' => 'entra'`.
- **Časté integrace s českým ekosystémem (Money, Pohoda, ABRA, ARES)**: Shape commands jsou natural fit — máte plně typovanou, audit-logovanou cestu pro volání externích API. ARES dotazy můžete cachovat přes `cache_control` na masku.
- **Vícejazyčnost**: schéma je schéma; Shape data nepřekládá. Překlady řešíte buď na frontendu, nebo přes lokalizované sloupce (`name_cs`, `name_en`).
- **DPH / fakturační compliance**: ne přímo — ale Shape commands + audit log dají vašemu invoicing modulu kompletní účetní stopu, kterou pak jednoduše vyexportujete pro auditora.

---

## Doporučený postup migrace

| Fáze | Co uděláte | Časový rámec |
|---|---|---|
| **0** | `composer require shape/shape` v novém adresáři vedle legacy app | 1 hodina |
| **1** | `Shape\ShapeApp::run(__DIR__ . '/..')` v `public/api.php`. Namířit přes `config/database.php` na CRM databázi. Otestovat `?openapi` v Postmanu | 1 den |
| **2** | Vybrat 2–3 obrazovky z legacy UI s nejvyšší bolestí (často "vyhledávání zákazníka", "detail zákazníka", "list aktivit") a postavit je v moderním frontendu proti Shape API | 1 týden |
| **3** | Pokrýt 50 % nejpoužívanějších obrazovek. Začít měřit, kolik uživatelů ještě sahá na legacy UI | 1 měsíc |
| **4** | Přidat custom masky pro reporty, commands pro workflow, uploads pro přílohy. Legacy aplikaci postupně vypínat | 3–6 měsíců |
| **5** | Legacy app jde offline. Shape zůstává a podporuje libovolný počet frontendů | — |

V žádné fázi nepřerušíte produkční provoz; legacy a Shape sdílí databázi celou dobu.

---

## Co tedy získáte

- Moderní REST / JSON / OData / MCP / OpenAPI API z vaší stávající databáze **za den**.
- JWT + OIDC auth **bez migrace hesel**.
- Audit log + soft-delete + audit columns na základě konvencí (**žádný kód navíc**).
- Postupnou migraci v souběhu s legacy aplikací (**žádný big-bang risk**).
- AI-friendly povrch (`AGENTS.md`, MCP server, OpenAPI) pro budoucí integrace s Claude / ChatGPT / Cursor — váš tým bude moct používat AI agenta, který umí váš backend bez doučování.

**Začněte s** `composer require shape/shape` **a** `Shape\ShapeApp::run(__DIR__ . '/..')`. Za den máte API; za týden máte první obrazovky; za půl roku máte legacy aplikaci vypnutou.

---

📖 Hlubší dokumentace: [`docs/shape/DB-FIRST.md`](../shape/DB-FIRST.md), [`docs/shape/AUTH.md`](../shape/AUTH.md), [`docs/shape/MCP.md`](../shape/MCP.md)
🛣️ Co je naplánováno: [`STATUS.md`](../../STATUS.md)
🤖 Pro AI agenty: [`AGENTS.md`](../../AGENTS.md) — jeden 5k-token soubor, který naučí Claude / ChatGPT / Cursor pracovat se Shape
