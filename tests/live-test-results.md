# 🔬 AZOA Live API Test Results

- **Base URL:** `http://localhost:5000`
- **Started:** 2026-06-08T00:45:37.6401181Z
- **Completed:** 2026-06-08T00:45:46.9318328Z
- **Duration:** 36339ms

## 📊 Summary

| Metric | Value |
|--------|-------|
| Suites | 19 |
| Cases  | 806 |
| ✅ Passed | 195 |
| ❌ Failed | 611 |
| ⏭️ Skipped | 0 |

## 🗂️ AvatarController_Malicious

- **Total:** 40 | **Passed:** 25 | **Failed:** 15 | **Skipped:** 0
- **Duration:** 6456ms

### ❌ sqli_username

- **Description:** SQL injection in username field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2182ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-d0b5d5e7e3aaa39cbe663145b03e9867-806ebf236916bbee-01"}
```
</details>

### ❌ sqli_email

- **Description:** SQL injection in email field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 12ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' is not a valid email address."]},"traceId":"00-c289e65a89c04b00b56b32eba09b2c79-d44abd865802b4bb-01"}
```
</details>

### ❌ sqli_password

- **Description:** SQL injection in password field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 7ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Password":["Password must contain a digit."]},"traceId":"00-e645bea3ed0bea982c11e8eff9267633-b7f8bbd4d451a29c-01"}
```
</details>

### ❌ sqli_login_email

- **Description:** SQL injection in login email
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 400
- **Duration:** 4ms
- **Error:** Expected status 401, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' is not a valid email address."]},"traceId":"00-a0c789b2771225364a92d8502c0c8ccc-815fc30dcaa5517d-01"}
```
</details>

### ✅ sqli_login_password

- **Description:** SQL injection in login password
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 401
- **Duration:** 37ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Invalid credentials.","result":null,"detail":null}
```
</details>

### ❌ sqli_blind_union

- **Description:** Blind UNION-based SQLi
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' is not a valid email address."]},"traceId":"00-94de86fbea4b9ed8d8ce09dfb5f4796a-2f026c359c670dfd-01"}
```
</details>

### ❌ xss_username

- **Description:** XSS in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-0ab2d21625aeb266ce657a041eca8efd-60003bc494b34f1b-01"}
```
</details>

### ✅ xss_email

- **Description:** XSS in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 465ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"39cfaece-54af-4bbf-b23b-ceac2d1a01ef","username":"xssuser","email":"<img src=x onerror=alert(1)>@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:39.9307409Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ xss_title

- **Description:** XSS in title field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 245ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"3d8282a0-34cb-45d0-bd95-477e24781498","username":"xsstitle","email":"xss2@mal.azoa","title":"<svg onload=alert(1)>","firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:40.4056743Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ❌ xss_firstname

- **Description:** XSS in first name
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 195ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealAvatarStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealAvatarStore.UpsertAsync(IAvatar avatar, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealAvatarStore.cs:line 91","inner":null}}
```
</details>

### ❌ xss_encoded

- **Description:** HTML-encoded XSS payload
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-2b73924d23b35f87510f9f9abc2ee715-b47a5f2df9bc7d02-01"}
```
</details>

### ❌ oversized_username_10k

- **Description:** 10,000 character username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["'Username' must be between 3 and 50 characters. You entered 500 characters."]},"traceId":"00-16b8f6b428bfcd998204847219bae733-de642b1ad0082e60-01"}
```
</details>

### ✅ oversized_email_1k

- **Description:** 1,000 character email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 235ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"dc75b118-6492-4f9d-b5d8-3326533a62fa","username":"bigemail","email":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:40.8445377Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ oversized_title

- **Description:** Very long title field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 222ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"0a2effda-b303-40fc-808f-06d7b37a1963","username":"bigtitle","email":"bigtitle@mal.azoa","title":"Professor Doctor Sir Lord Admiral General Chancellor Vice-President Executive Senior Chief Principal Lead Head Master Grand Supreme Ultimate Almighty Omnipotent Omniscient Transcendent Eternal Immortal Divine Sacred Holy Blessed Sanctified Consecrated Hallowed Venerated Exalted Elevated Ennobled Dignified Illustrious Eminent Prominent Notable Renowned Celebrated Acclaimed Esteemed Respected Honored Revered Worshipped Adored Glorified Magnified Extoled Praised Lauded Commended Applauded Saluted Cheers Hooray Bravo WellDone GoodJob NiceWork Excellent Outstanding superb fantastic amazing incredible unbelievable phenomenal extraordinary remarkable exceptional stupendous tremendous wondrous marvelous spectacular breathtaking awe-inspiring jaw-dropping mind-blowing earth-shattering groundbreaking revolutionary innovative cutting-edge state-of-the-art world-class top-tier first-rate premium elite exclusive luxury deluxe grand majestic magnificent splendid glorious radiant brilliant luminous dazzling shining gleaming glowing blazing flaming fiery intense powerful mighty strong forceful potent vigorous robust sturdy solid firm hard tough durable resilient flexible adaptable versatile capable competent skilled talented gifted brilliant genius prodigy virtuoso master expert specialist professional authority pundit sage wizard magician sorcerer enchanter charmer captivator fascinator mesmerizer hypnotizer spellbinder storyteller narrator chronicler historian biographer autobiographer memoirist diarist journalist reporter correspondent columnist commentator critic reviewer analyst evaluator assessor appraiser estimator calculator mathematician statistician actuary accountant bookkeeper auditor inspector examiner investigator researcher scientist scholar academic intellectual philosopher theorist thinker ideologue ideologistologist"
... [truncated]
```
</details>

### ✅ null_username

- **Description:** Null username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 6ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["The Username field is required.","'Username' must not be empty."]},"traceId":"00-091e159480b59be8bb2fd5f8dee9adaf-a806ba09b9f0a137-01"}
```
</details>

### ✅ numeric_username

- **Description:** Number instead of string for username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 8ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.username":["The JSON value could not be converted to System.String. Path: $.username | LineNumber: 0 | BytePositionInLine: 17."]},"traceId":"00-a33af7b2b369d4a77ddd21fc50ccbdbf-881c8716a5255a59-01"}
```
</details>

### ✅ array_email

- **Description:** Array instead of string for email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 44ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.email":["The JSON value could not be converted to System.String. Path: $.email | LineNumber: 0 | BytePositionInLine: 33."]},"traceId":"00-5bbc407a3847edc5ac79112341e7d689-78d67ff6fb4f4955-01"}
```
</details>

### ✅ boolean_password

- **Description:** Boolean instead of string for password
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 43ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.password":["The JSON value could not be converted to System.String. Path: $.password | LineNumber: 0 | BytePositionInLine: 63."]},"traceId":"00-70512f7b208224cf22a7b4c49f1485ed-d98cf5417013c375-01"}
```
</details>

### ✅ object_instead_of_string

- **Description:** Object instead of primitive fields
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 32ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.username":["The JSON value could not be converted to System.String. Path: $.username | LineNumber: 0 | BytePositionInLine: 13."]},"traceId":"00-41203aac2e1a8b697ad555fd04c6b20d-8648ac66eda95f06-01"}
```
</details>

### ✅ empty_json

- **Description:** Completely empty body
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 68ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' must not be empty.","'Email' is not a valid email address."],"Password":["'Password' must not be empty.","The length of 'Password' must be at least 8 characters. You entered 0 characters.","Password must contain an uppercase letter.","Password must contain a lowercase letter.","Password must contain a digit."],"Username":["'Username' must not be empty.","'Username' must be between 3 and 50 characters. You entered 0 characters.","Username must contain only letters, numbers, and underscores."]},"traceId":"00-13a6950f9fd8452e71b4378f46f2546c-b47bf23f467b3e66-01"}
```
</details>

### ✅ missing_required

- **Description:** Missing password field entirely
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 58ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Password":["'Password' must not be empty.","The length of 'Password' must be at least 8 characters. You entered 0 characters.","Password must contain an uppercase letter.","Password must contain a lowercase letter.","Password must contain a digit."]},"traceId":"00-7c2f0c935a0f578610dea8050a5bab5d-dd8b84d948422973-01"}
```
</details>

### ✅ path_traversal_get

- **Description:** Path traversal in avatar ID
- **Method:** `GET`
- **Path:** `/api/avatar/../../../etc/passwd`
- **Status:** 404
- **Duration:** 31ms

### ❌ null_byte_injection

- **Description:** Null byte in path
- **Method:** `GET`
- **Path:** `/api/avatar/550e8400-e29b-41d4-a716-446655440000%00`
- **Status:** 400
- **Duration:** 36ms
- **Error:** Expected status 404, got 400.

### ✅ double_encoding

- **Description:** Double-encoded path segment
- **Method:** `GET`
- **Path:** `/api/avatar/%25550e8400-e29b-41d4-a716-446655440000`
- **Status:** 404
- **Duration:** 58ms

### ❌ rtl_override

- **Description:** Right-to-left override in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 45ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-4ed6c32ac44e99e1eb934524c6573707-6d340ff9320a532d-01"}
```
</details>

### ❌ zero_width_chars

- **Description:** Zero-width joiners in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 35ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-e35fda371585dc01a69f8998ef4f1e36-462115911e0e6843-01"}
```
</details>

### ❌ emoji_username

- **Description:** Emoji-only username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 27ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-08a6d1840c868df458e7f827ba50ebc6-fab264aab25362f4-01"}
```
</details>

### ✅ bidi_attack

- **Description:** Bidirectional text attack in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 237ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"b641f1aa-c35f-49d0-8d6a-f965104a8a1e","username":"bidi","email":"admin‮@mal.azoa‬","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:41.8336841Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ deeply_nested_body

- **Description:** Deeply nested JSON object
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 30ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.username":["The JSON value could not be converted to System.String. Path: $.username | LineNumber: 0 | BytePositionInLine: 13."]},"traceId":"00-936cdf4a7c7433da43c4c18425525799-67f2496198929b86-01"}
```
</details>

### ✅ massive_array

- **Description:** Massive array in body
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 254ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"0d626f83-cb86-460f-bea2-c2fce08bf9a3","username":"arraytest","email":"array@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:42.1185831Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ❌ header_injection_content_type

- **Description:** Newline injection in Content-Type
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 0
- **Duration:** 0ms
- **Error:** Exception: FormatException: The format of value 'application/json
X-Injected: evil' is invalid.

### ✅ header_injection_auth

- **Description:** Newline injection in Authorization
- **Method:** `GET`
- **Path:** `/api/avatar/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 63ms

### ✅ rapid_register_1

- **Description:** Rapid registration 1
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 302ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"b832575a-73ac-4771-b220-689c95104c69","username":"rapid1","email":"rapid1@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:42.4791476Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ rapid_register_2

- **Description:** Rapid registration 2
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 212ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"0523d3f1-03d3-47ec-ac50-87065bc27c4e","username":"rapid2","email":"rapid2@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:42.6804294Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ rapid_register_3

- **Description:** Rapid registration 3
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 284ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"58c726f5-6dc2-4491-84a0-1eaf82c51e25","username":"rapid3","email":"rapid3@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:42.9300207Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ rapid_register_4

- **Description:** Rapid registration 4
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 214ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"4beffba6-f863-477d-8400-4f72ffa41766","username":"rapid4","email":"rapid4@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:43.1981699Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ rapid_register_5

- **Description:** Rapid registration 5
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 255ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"afe257fe-5283-4a87-a688-fff717ca0826","username":"rapid5","email":"rapid5@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:43.4182928Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ negative_karma_if_exposed

- **Description:** Negative karma value (if exposed via update)
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 262ms
- **Extracted:**
  - `negAvatar.id` = `c99cfade-ce06-485d-b254-78e647c10fd9`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"c99cfade-ce06-485d-b254-78e647c10fd9","username":"negative","email":"negative@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:43.6852483Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_negative_avatar

- **Description:** Login negative test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 173ms
- **Extracted:**
  - `negAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJjOTljZmFkZS1jZTA2LTQ4NWQtYjI1NC03OGU2NDdjMTBmZDkiLCJlbWFpbCI6Im5lZ2F0aXZlQG1hbC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJuZWdhdGl2ZSIsImp0aSI6IjY4MDFhZjlmLWUyYzgtNDU2ZS1iOWQ4LWEzZjE1NjdkNWFjNSIsImV4cCI6MTc4MDk2NTk0NCwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.KU2nQ3Fp_M9XFnrj7QzPAnpNIidm305Z85WXapTwv_w`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJjOTljZmFkZS1jZTA2LTQ4NWQtYjI1NC03OGU2NDdjMTBmZDkiLCJlbWFpbCI6Im5lZ2F0aXZlQG1hbC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJuZWdhdGl2ZSIsImp0aSI6IjY4MDFhZjlmLWUyYzgtNDU2ZS1iOWQ4LWEzZjE1NjdkNWFjNSIsImV4cCI6MTc4MDk2NTk0NCwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.KU2nQ3Fp_M9XFnrj7QzPAnpNIidm305Z85WXapTwv_w","detail":null}
```
</details>

### ❌ cleanup_negative

- **Description:** Cleanup negative test avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/c99cfade-ce06-485d-b254-78e647c10fd9`
- **Status:** 404
- **Duration:** 41ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ AvatarController_QA

- **Total:** 35 | **Passed:** 17 | **Failed:** 18 | **Skipped:** 0
- **Duration:** 5198ms

### ✅ register_minimal

- **Description:** Register with only required fields
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 2376ms
- **Extracted:**
  - `minimalAvatar.id` = `1a11c017-b129-41a6-baa8-e1f86bed12af`
  - `minimalAvatar.email` = `min@qa.azoa`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"1a11c017-b129-41a6-baa8-e1f86bed12af","username":"min","email":"min@qa.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:39.871266Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ register_full_profile

- **Description:** Register with all profile fields populated
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 409ms
- **Extracted:**
  - `fullAvatar.id` = `b980507b-f002-45b5-b069-88e10f00c9e6`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"b980507b-f002-45b5-b069-88e10f00c9e6","username":"fullprofile","email":"full@qa.azoa","title":"Dr.","firstName":"Full","lastName":"Profile","createdDate":"2026-06-08T00:45:40.1445962Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ❌ register_unicode_username

- **Description:** Register with Unicode username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 4ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-0ded1f8469291a47d02b2ccab75a7121-a3ac3d741886dccb-01"}
```
</details>

### ✅ register_special_chars_email

- **Description:** Register with email containing plus alias
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 242ms
- **Extracted:**
  - `plusAvatar.id` = `f35c63ec-e24e-4ad9-b743-af9bcb0da07f`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"f35c63ec-e24e-4ad9-b743-af9bcb0da07f","username":"plusemail","email":"test+alias@qa.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:40.5423428Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ register_long_username

- **Description:** Register with 50-char username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 210ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"46d3a032-dc26-49d7-acc5-440c56265c11","username":"verylongusernamethatistotaloffiftycharacterslong","email":"longuser@qa.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:40.757662Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ❌ register_duplicate_username

- **Description:** Register duplicate username should succeed (no unique constraint on username)
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 34ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"This username is already taken.","result":null,"detail":null}
```
</details>

### ✅ register_duplicate_email

- **Description:** Register duplicate email should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 29ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"An account with this email already exists.","result":null,"detail":null}
```
</details>

### ✅ login_minimal

- **Description:** Login as minimal avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 184ms
- **Extracted:**
  - `minimalAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxYTExYzAxNy1iMTI5LTQxYTYtYmFhOC1lMWY4NmJlZDEyYWYiLCJlbWFpbCI6Im1pbkBxYS5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJtaW4iLCJqdGkiOiIxNjg5YjJiMy1lMTRmLTQ5ODktODkxNC1mNjAwMGRhZjU3YzMiLCJleHAiOjE3ODA5NjU5NDEsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.ou0-jjkR30eQSXK5VmNo0excTx-dCfPn_zfr8fDE0IM`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxYTExYzAxNy1iMTI5LTQxYTYtYmFhOC1lMWY4NmJlZDEyYWYiLCJlbWFpbCI6Im1pbkBxYS5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJtaW4iLCJqdGkiOiIxNjg5YjJiMy1lMTRmLTQ5ODktODkxNC1mNjAwMGRhZjU3YzMiLCJleHAiOjE3ODA5NjU5NDEsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.ou0-jjkR30eQSXK5VmNo0excTx-dCfPn_zfr8fDE0IM","detail":null}
```
</details>

### ✅ login_full

- **Description:** Login as full profile avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 178ms
- **Extracted:**
  - `fullAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJiOTgwNTA3Yi1mMDAyLTQ1YjUtYjA2OS04OGUxMGYwMGM5ZTYiLCJlbWFpbCI6ImZ1bGxAcWEub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiZnVsbHByb2ZpbGUiLCJqdGkiOiJmOWFhNTNjNy03M2ExLTQzZGEtYWRkYi0wNDFlZDI5NDcxOTAiLCJleHAiOjE3ODA5NjU5NDEsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.McqiT-B_S-jWac4GKqz_4k3hAPBxYpNBZEvWp37jIDE`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJiOTgwNTA3Yi1mMDAyLTQ1YjUtYjA2OS04OGUxMGYwMGM5ZTYiLCJlbWFpbCI6ImZ1bGxAcWEub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiZnVsbHByb2ZpbGUiLCJqdGkiOiJmOWFhNTNjNy03M2ExLTQzZGEtYWRkYi0wNDFlZDI5NDcxOTAiLCJleHAiOjE3ODA5NjU5NDEsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.McqiT-B_S-jWac4GKqz_4k3hAPBxYpNBZEvWp37jIDE","detail":null}
```
</details>

### ❌ login_unicode

- **Description:** Login as unicode avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 401
- **Duration:** 32ms
- **Error:** Expected status 200, got 401.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Invalid credentials.","result":null,"detail":null}
```
</details>

### ✅ login_wrong_password

- **Description:** Login with wrong password
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 401
- **Duration:** 159ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Invalid credentials.","result":null,"detail":null}
```
</details>

### ✅ login_nonexistent

- **Description:** Login with non-existent email
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 401
- **Duration:** 36ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Invalid credentials.","result":null,"detail":null}
```
</details>

### ✅ login_empty_body

- **Description:** Login with empty body
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 400
- **Duration:** 57ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' must not be empty.","'Email' is not a valid email address."],"Password":["'Password' must not be empty."]},"traceId":"00-c7aeb00e8e6962fdfdcee2d515f0d7ce-cd2b500d6d4fc276-01"}
```
</details>

### ❌ get_minimal_avatar

- **Description:** Get minimal avatar by ID
- **Method:** `GET`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 404
- **Duration:** 45ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ get_full_avatar

- **Description:** Get full profile avatar by ID
- **Method:** `GET`
- **Path:** `/api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6`
- **Status:** 404
- **Duration:** 62ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ get_nonexistent_avatar

- **Description:** Get non-existent avatar returns 404
- **Method:** `GET`
- **Path:** `/api/avatar/00000000-0000-0000-0000-000000000000`
- **Status:** 404
- **Duration:** 49ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ get_all_avatars

- **Description:** List all avatars should include seeded ones
- **Method:** `GET`
- **Path:** `/api/avatar`
- **Status:** 200
- **Duration:** 36ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[{"id":"0028eb26-9d88-4b02-b979-04c491996d8d","username":"blockchaintest","email":"blockchain@test.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:41.3544257Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"0a2effda-b303-40fc-808f-06d7b37a1963","username":"bigtitle","email":"bigtitle@mal.azoa","title":"Professor Doctor Sir Lord Admiral General Chancellor Vice-President Executive Senior Chief Principal Lead Head Master Grand Supreme Ultimate Almighty Omnipotent Omniscient Transcendent Eternal Immortal Divine Sacred Holy Blessed Sanctified Consecrated Hallowed Venerated Exalted Elevated Ennobled Dignified Illustrious Eminent Prominent Notable Renowned Celebrated Acclaimed Esteemed Respected Honored Revered Worshipped Adored Glorified Magnified Extoled Praised Lauded Commended Applauded Saluted Cheers Hooray Bravo WellDone GoodJob NiceWork Excellent Outstanding superb fantastic amazing incredible unbelievable phenomenal extraordinary remarkable exceptional stupendous tremendous wondrous marvelous spectacular breathtaking awe-inspiring jaw-dropping mind-blowing earth-shattering groundbreaking revolutionary innovative cutting-edge state-of-the-art world-class top-tier first-rate premium elite exclusive luxury deluxe grand majestic magnificent splendid glorious radiant brilliant luminous dazzling shining gleaming glowing blazing flaming fiery intense powerful mighty strong forceful potent vigorous robust sturdy solid firm hard tough durable resilient flexible adaptable versatile capable competent skilled talented gifted brilliant genius prodigy virtuoso master expert specialist professional authority pundit sage wizard magician sorcerer enchanter charmer captivator fascinator mesmerizer hypnotizer spellbinder storyteller narrator chronicler historian biographer autobiographer memoirist diarist journalist reporter correspondent columnist commentator c
... [truncated]
```
</details>

### ❌ update_title

- **Description:** Update only title field
- **Method:** `PUT`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 400
- **Duration:** 65ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ update_email

- **Description:** Update email address
- **Method:** `PUT`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 400
- **Duration:** 116ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ update_multiple_fields

- **Description:** Update multiple fields at once
- **Method:** `PUT`
- **Path:** `/api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6`
- **Status:** 400
- **Duration:** 28ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ update_nonexistent

- **Description:** Update non-existent avatar
- **Method:** `PUT`
- **Path:** `/api/avatar/00000000-0000-0000-0000-000000000000`
- **Status:** 400
- **Duration:** 91ms
- **Error:** Expected status 404, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ add_algorand_wallet

- **Description:** Add Algorand wallet to avatar
- **Method:** `POST`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets`
- **Status:** 404
- **Duration:** 199ms
- **Error:** Expected status 200, got 404.

### ❌ add_solana_wallet

- **Description:** Add Solana wallet to avatar
- **Method:** `POST`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets`
- **Status:** 404
- **Duration:** 104ms
- **Error:** Expected status 200, got 404.

### ❌ add_second_algorand_wallet

- **Description:** Add second Algorand wallet (same avatar)
- **Method:** `POST`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets`
- **Status:** 404
- **Duration:** 57ms
- **Error:** Expected status 200, got 404.

### ❌ get_wallets

- **Description:** Get all wallets for avatar
- **Method:** `GET`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ remove_solana_wallet

- **Description:** Remove Solana wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets/{{solWallet.walletId}}`
- **Status:** 404
- **Duration:** 66ms
- **Error:** Expected status 200, got 404.

### ✅ remove_nonexistent_wallet

- **Description:** Remove non-existent wallet returns 404
- **Method:** `DELETE`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets/00000000-0000-0000-0000-000000000000`
- **Status:** 404
- **Duration:** 34ms

### ❌ add_wallet_to_other_avatar

- **Description:** Add wallet to another avatar (should succeed - manager allows any avatarId)
- **Method:** `POST`
- **Path:** `/api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6/wallets`
- **Status:** 404
- **Duration:** 40ms
- **Error:** Expected status 200, got 404.

### ✅ get_without_auth

- **Description:** Get avatar without auth returns 401
- **Method:** `GET`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 401
- **Duration:** 23ms

### ✅ get_with_malformed_auth

- **Description:** Get avatar with malformed Bearer token
- **Method:** `GET`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 401
- **Duration:** 3ms

### ❌ delete_minimal_avatar

- **Description:** Delete minimal avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 404
- **Duration:** 33ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ delete_full_avatar

- **Description:** Delete full profile avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6`
- **Status:** 404
- **Duration:** 31ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ delete_unicode_avatar

- **Description:** Delete unicode avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{unicodeAvatar.id}}`
- **Status:** 404
- **Duration:** 28ms
- **Error:** Expected status 200, got 404.

### ✅ verify_deleted

- **Description:** Verify deleted avatar returns 404
- **Method:** `GET`
- **Path:** `/api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af`
- **Status:** 404
- **Duration:** 53ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ delete_nonexistent

- **Description:** Delete non-existent avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/00000000-0000-0000-0000-000000000000`
- **Status:** 404
- **Duration:** 66ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ AvatarController

- **Total:** 11 | **Passed:** 5 | **Failed:** 6 | **Skipped:** 0
- **Duration:** 3008ms

### ✅ register_avatar

- **Description:** Register a new avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 2357ms
- **Extracted:**
  - `avatar1.avatarId` = `2f025fdb-94f5-454b-9136-e3baa3074bd2`
  - `avatar1.email` = `live@test.azoa`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"2f025fdb-94f5-454b-9136-e3baa3074bd2","username":"livetester","email":"live@test.azoa","title":"Tester","firstName":"Live","lastName":"Test","createdDate":"2026-06-08T00:45:39.835042Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ register_duplicate

- **Description:** Register duplicate email should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 66ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"An account with this email already exists.","result":null,"detail":null}
```
</details>

### ✅ login_avatar

- **Description:** Login with registered credentials
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 298ms
- **Extracted:**
  - `auth1.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIyZjAyNWZkYi05NGY1LTQ1NGItOTEzNi1lM2JhYTMwNzRiZDIiLCJlbWFpbCI6ImxpdmVAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJsaXZldGVzdGVyIiwianRpIjoiNWI5ZmJmYmEtM2RhYS00NTQ0LWI3NTItMzlkZWFiODU1ZGJkIiwiZXhwIjoxNzgwOTY1OTQwLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.wZbYdGiCXEEx-EOyxbstt7iBIuq1w5KDoGY5RN_L8Nk`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIyZjAyNWZkYi05NGY1LTQ1NGItOTEzNi1lM2JhYTMwNzRiZDIiLCJlbWFpbCI6ImxpdmVAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJsaXZldGVzdGVyIiwianRpIjoiNWI5ZmJmYmEtM2RhYS00NTQ0LWI3NTItMzlkZWFiODU1ZGJkIiwiZXhwIjoxNzgwOTY1OTQwLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.wZbYdGiCXEEx-EOyxbstt7iBIuq1w5KDoGY5RN_L8Nk","detail":null}
```
</details>

### ❌ get_avatar

- **Description:** Get avatar by extracted ID
- **Method:** `GET`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2`
- **Status:** 404
- **Duration:** 115ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ get_all_avatars

- **Description:** List all avatars
- **Method:** `GET`
- **Path:** `/api/avatar`
- **Status:** 200
- **Duration:** 40ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[{"id":"13251342-d296-49a1-a95f-bac80cf2fa50","username":"smoketest_rpc3","email":"smoke_rpc3@example.com","title":null,"firstName":"Smoke","lastName":"RPC3","createdDate":"2026-06-07T15:44:02.4001697Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"1a11c017-b129-41a6-baa8-e1f86bed12af","username":"min","email":"min@qa.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:39.871266Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"2f025fdb-94f5-454b-9136-e3baa3074bd2","username":"livetester","email":"live@test.azoa","title":"Tester","firstName":"Live","lastName":"Test","createdDate":"2026-06-08T00:45:39.835042Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"39cfaece-54af-4bbf-b23b-ceac2d1a01ef","username":"xssuser","email":"<img src=x onerror=alert(1)>@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:39.9307409Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"576ad632-1c83-4fd0-b57b-595f34dc0e28","username":"login_dbg","email":"login_dbg@example.com","title":null,"firstName":"L","lastName":"D","createdDate":"2026-06-07T15:53:57.224447Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"64ccf2dc-12b0-441d-b3e3-2c98dc6d1036","username":"smoketest_final","email":"smoke_final@example.com","title":null,"firstName":"Smoke","lastName":"Final","createdDate":"2026-06-07T15:51:24.6464831Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"86c4b239-b86e-4a9b-a7e4-dac3f2df9996","username":"algodev","email":"algo@devnet.azoa","title":null,"firstName":"Algo","lastName":"Devnet","createdDate":"2026-06-08T00:45:39.8938165Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},{"id":"a103ef82-188b-491b-91f1-f7
... [truncated]
```
</details>

### ❌ update_avatar

- **Description:** Update avatar first name
- **Method:** `PUT`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2`
- **Status:** 400
- **Duration:** 48ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ add_wallet

- **Description:** Add a wallet to the avatar
- **Method:** `POST`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ get_wallets

- **Description:** Get wallets for the avatar
- **Method:** `GET`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2/wallets`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ remove_wallet

- **Description:** Remove the wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2/wallets/{{wallet1.walletId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ delete_avatar

- **Description:** Delete the avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2`
- **Status:** 404
- **Duration:** 40ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ get_deleted_avatar

- **Description:** Get deleted avatar should 404
- **Method:** `GET`
- **Path:** `/api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2`
- **Status:** 404
- **Duration:** 34ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

## 🗂️ Blockchain_Devnet

- **Total:** 30 | **Passed:** 6 | **Failed:** 24 | **Skipped:** 0
- **Duration:** 3569ms

### ✅ algo_seed_avatar

- **Description:** Register avatar for Algorand devnet tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 2391ms
- **Extracted:**
  - `algoAvatar.id` = `86c4b239-b86e-4a9b-a7e4-dac3f2df9996`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"86c4b239-b86e-4a9b-a7e4-dac3f2df9996","username":"algodev","email":"algo@devnet.azoa","title":null,"firstName":"Algo","lastName":"Devnet","createdDate":"2026-06-08T00:45:39.8938165Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ algo_login

- **Description:** Login as Algorand devnet avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 330ms
- **Extracted:**
  - `algoAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI4NmM0YjIzOS1iODZlLTRhOWItYTdlNC1kYWMzZjJkZjk5OTYiLCJlbWFpbCI6ImFsZ29AZGV2bmV0Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6ImFsZ29kZXYiLCJqdGkiOiIyMmVhMjc5Yy0wYjI3LTQ1ODItOWNjOC0wNzJjN2M1NGYwMzciLCJleHAiOjE3ODA5NjU5NDAsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.-GMU3gZeJwwofQXuLI07fl_gel-55F2JS7yZr8rVsdo`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI4NmM0YjIzOS1iODZlLTRhOWItYTdlNC1kYWMzZjJkZjk5OTYiLCJlbWFpbCI6ImFsZ29AZGV2bmV0Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6ImFsZ29kZXYiLCJqdGkiOiIyMmVhMjc5Yy0wYjI3LTQ1ODItOWNjOC0wNzJjN2M1NGYwMzciLCJleHAiOjE3ODA5NjU5NDAsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.-GMU3gZeJwwofQXuLI07fl_gel-55F2JS7yZr8rVsdo","detail":null}
```
</details>

### ❌ algo_add_wallet

- **Description:** Add Algorand devnet wallet
- **Method:** `POST`
- **Path:** `/api/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996/wallets`
- **Status:** 404
- **Duration:** 50ms
- **Error:** Expected status 200, got 404.

### ✅ sol_seed_avatar

- **Description:** Register avatar for Solana devnet tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 227ms
- **Extracted:**
  - `solAvatar.id` = `93c61e14-c12a-483e-98fb-8ac4dd20d212`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"93c61e14-c12a-483e-98fb-8ac4dd20d212","username":"soldev","email":"sol@devnet.azoa","title":null,"firstName":"Solana","lastName":"Devnet","createdDate":"2026-06-08T00:45:40.5149068Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ sol_login

- **Description:** Login as Solana devnet avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 169ms
- **Extracted:**
  - `solAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI5M2M2MWUxNC1jMTJhLTQ4M2UtOThmYi04YWM0ZGQyMGQyMTIiLCJlbWFpbCI6InNvbEBkZXZuZXQub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoic29sZGV2IiwianRpIjoiYzY5MWU1MzgtYzg0YS00ODFkLTk0MmQtZGNhMzExYzhlMDNjIiwiZXhwIjoxNzgwOTY1OTQwLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.inr9MdMptzg3aqIBNP7RXTV6DgKDir8X83gHpecY38A`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI5M2M2MWUxNC1jMTJhLTQ4M2UtOThmYi04YWM0ZGQyMGQyMTIiLCJlbWFpbCI6InNvbEBkZXZuZXQub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoic29sZGV2IiwianRpIjoiYzY5MWU1MzgtYzg0YS00ODFkLTk0MmQtZGNhMzExYzhlMDNjIiwiZXhwIjoxNzgwOTY1OTQwLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.inr9MdMptzg3aqIBNP7RXTV6DgKDir8X83gHpecY38A","detail":null}
```
</details>

### ❌ sol_add_wallet

- **Description:** Add Solana devnet wallet
- **Method:** `POST`
- **Path:** `/api/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ algo_create_holon

- **Description:** Create holon for Algorand minting
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 75ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sol_create_holon

- **Description:** Create holon for Solana minting
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 28ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ algo_create_peer_holon

- **Description:** Create peer holon for exchange test
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 63ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ algo_mint_asa

- **Description:** Mint ASA on Algorand devnet
- **Method:** `POST`
- **Path:** `/api/holon/{{algoHolon.id}}/mint`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ algo_mint_small

- **Description:** Mint small amount on Algorand devnet
- **Method:** `POST`
- **Path:** `/api/holon/{{algoHolon.id}}/mint`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ algo_mint_large

- **Description:** Mint large amount on Algorand devnet
- **Method:** `POST`
- **Path:** `/api/holon/{{algoHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ sol_mint_spl

- **Description:** Mint SPL on Solana devnet
- **Method:** `POST`
- **Path:** `/api/holon/{{solHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ sol_mint_nft

- **Description:** Mint NFT on Solana devnet (amount=1)
- **Method:** `POST`
- **Path:** `/api/holon/{{solHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ algo_exchange

- **Description:** Exchange between Algorand holons
- **Method:** `POST`
- **Path:** `/api/holon/{{algoHolon.id}}/exchange`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ algo_exchange_reverse

- **Description:** Reverse exchange rate
- **Method:** `POST`
- **Path:** `/api/holon/{{algoPeerHolon.id}}/exchange`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ algo_get_op_by_id

- **Description:** Get Algorand mint operation by ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{algoMintOp.opId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ✅ algo_get_ops_by_avatar

- **Description:** Get all operations for Algorand avatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996`
- **Status:** 200
- **Duration:** 51ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ sol_get_op_by_id

- **Description:** Get Solana mint operation by ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{solMintOp.opId}}`
- **Status:** 404
- **Duration:** 3ms
- **Error:** Expected status 200, got 404.

### ✅ sol_get_ops_by_avatar

- **Description:** Get all operations for Solana avatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212`
- **Status:** 200
- **Duration:** 31ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ algo_verify_mint_status

- **Description:** Verify Algorand mint op status is Completed
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{algoMintOp.opId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ sol_verify_mint_status

- **Description:** Verify Solana mint op status is Completed
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{solMintOp.opId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ sol_get_algo_op

- **Description:** Solana avatar tries to get Algorand avatar's operation
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{algoMintOp.opId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ algo_cleanup_peer_holon

- **Description:** Delete Algorand peer holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{algoPeerHolon.id}}`
- **Status:** 404
- **Duration:** 9ms
- **Error:** Expected status 200, got 404.

### ❌ algo_cleanup_holon

- **Description:** Delete Algorand holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{algoHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ sol_cleanup_holon

- **Description:** Delete Solana holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{solHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ algo_cleanup_wallet

- **Description:** Remove Algorand wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996/wallets/{{algoWallet.walletId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ sol_cleanup_wallet

- **Description:** Remove Solana wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212/wallets/{{solWallet.walletId}}`
- **Status:** 404
- **Duration:** 5ms
- **Error:** Expected status 200, got 404.

### ❌ algo_cleanup_avatar

- **Description:** Delete Algorand devnet avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996`
- **Status:** 404
- **Duration:** 62ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ sol_cleanup_avatar

- **Description:** Delete Solana devnet avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212`
- **Status:** 404
- **Duration:** 41ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ BlockchainOperationController_Malicious

- **Total:** 20 | **Passed:** 14 | **Failed:** 6 | **Skipped:** 0
- **Duration:** 615ms

### ✅ seed_avatar

- **Description:** Register avatar for blockchain malicious tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 207ms
- **Extracted:**
  - `bcMalAvatar.id` = `451ddc6d-c668-4f63-9ecc-fb98fa235feb`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"451ddc6d-c668-4f63-9ecc-fb98fa235feb","username":"bcmal","email":"bcmal@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:40.7389794Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_avatar

- **Description:** Login as blockchain test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 170ms
- **Extracted:**
  - `bcMalAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI0NTFkZGM2ZC1jNjY4LTRmNjMtOWVjYy1mYjk4ZmEyMzVmZWIiLCJlbWFpbCI6ImJjbWFsQG1hbC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJiY21hbCIsImp0aSI6IjA5MDFkZjc0LTc2OGQtNDZlZi05MmQ1LTAwZjExODlhOTI2NCIsImV4cCI6MTc4MDk2NTk0MSwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.TIAEPr2jcp0iEhxGr-OLdt5lKUnEegcKxnkAErel68c`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI0NTFkZGM2ZC1jNjY4LTRmNjMtOWVjYy1mYjk4ZmEyMzVmZWIiLCJlbWFpbCI6ImJjbWFsQG1hbC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJiY21hbCIsImp0aSI6IjA5MDFkZjc0LTc2OGQtNDZlZi05MmQ1LTAwZjExODlhOTI2NCIsImV4cCI6MTc4MDk2NTk0MSwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.TIAEPr2jcp0iEhxGr-OLdt5lKUnEegcKxnkAErel68c","detail":null}
```
</details>

### ✅ get_op_sqli_guid

- **Description:** SQLi attempt in operation GUID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/' OR '1'='1`
- **Status:** 404
- **Duration:** 1ms

### ✅ get_op_path_traversal

- **Description:** Path traversal in operation endpoint
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/../../../etc/passwd`
- **Status:** 404
- **Duration:** 1ms

### ❌ get_op_null_byte

- **Description:** Null byte in operation ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/550e8400-e29b-41d4-a716-446655440000%00`
- **Status:** 400
- **Duration:** 4ms
- **Error:** Expected status 404, got 400.

### ✅ get_op_negative_numbers

- **Description:** Negative numbers in GUID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/-50e8400-e29b-41d4-a716-446655440000`
- **Status:** 404
- **Duration:** 9ms

### ✅ get_by_avatar_sqli

- **Description:** SQLi in avatar ID path param
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/' UNION SELECT * FROM avatars--`
- **Status:** 404
- **Duration:** 3ms

### ✅ get_by_avatar_path_traversal

- **Description:** Path traversal in getByAvatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/../../../etc/passwd`
- **Status:** 404
- **Duration:** 31ms

### ✅ get_by_avatar_xss

- **Description:** XSS payload in avatar ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/<script>alert(1)</script>`
- **Status:** 404
- **Duration:** 1ms

### ✅ get_by_avatar_very_long

- **Description:** Very long string as avatar ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA`
- **Status:** 404
- **Duration:** 1ms

### ✅ get_op_no_auth

- **Description:** Get operation without auth
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 8ms

### ✅ get_by_avatar_no_auth

- **Description:** Get by avatar without auth
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb`
- **Status:** 401
- **Duration:** 3ms

### ✅ get_op_empty_auth

- **Description:** Empty authorization header
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 2ms

### ✅ get_op_bearer_only

- **Description:** Bearer keyword only no token
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 1ms

### ✅ get_op_tampered_token

- **Description:** Tampered JWT token
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb`
- **Status:** 401
- **Duration:** 6ms

### ❌ header_injection_op

- **Description:** Header injection via auth header
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb`
- **Status:** 200
- **Duration:** 32ms
- **Error:** Expected status 401, got 200.

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ get_op_sqli_provider_type

- **Description:** SQLi in providerType query param
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb?providerType=' OR '1'='1`
- **Status:** 400
- **Duration:** 40ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"ProviderType":["The value '' OR '1'='1' is not valid for ProviderType."]},"traceId":"00-a1c50d0adb9aceb9d6523e86ff18aa3a-bbe62dcf83f3d4b6-01"}
```
</details>

### ❌ get_op_xss_provider_type

- **Description:** XSS in providerType query param
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb?providerType=<script>alert(1)</script>`
- **Status:** 400
- **Duration:** 32ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"ProviderType":["The value '<script>alert(1)</script>' is not valid for ProviderType."]},"traceId":"00-8989665875993f4049c48d7ef8b84e33-1866ec901fba43e3-01"}
```
</details>

### ❌ get_op_very_long_query

- **Description:** Very long query string
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb?customProviderKeys=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA`
- **Status:** 400
- **Duration:** 8ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"CustomProviderKeys[0]":["Each CustomProviderKey must not exceed 128 characters."]},"traceId":"00-62d6c5168efcdb2fa3a9d3649742cb17-f91fb28f0faa12d3-01"}
```
</details>

### ❌ cleanup_avatar

- **Description:** Delete test avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb`
- **Status:** 404
- **Duration:** 46ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ BlockchainOperationController_QA

- **Total:** 10 | **Passed:** 7 | **Failed:** 3 | **Skipped:** 0
- **Duration:** 817ms

### ✅ seed_avatar

- **Description:** Register avatar for blockchain operation tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 199ms
- **Extracted:**
  - `bcAvatar.id` = `da5b882e-4656-45a4-b983-18bc1fbb81ec`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"da5b882e-4656-45a4-b983-18bc1fbb81ec","username":"blockchainqa","email":"blockchain@qa.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:41.3004287Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_avatar

- **Description:** Login as blockchain test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 183ms
- **Extracted:**
  - `bcAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkYTViODgyZS00NjU2LTQ1YTQtYjk4My0xOGJjMWZiYjgxZWMiLCJlbWFpbCI6ImJsb2NrY2hhaW5AcWEub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiYmxvY2tjaGFpbnFhIiwianRpIjoiZTczN2I0OGItNDJlZi00MDY0LWEzNTItNjViZDE5NTkxMjU5IiwiZXhwIjoxNzgwOTY1OTQxLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.uerDaLH_qF2XVwM1BrF54Qx8ixKDNg_dR81lmsq8ar4`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkYTViODgyZS00NjU2LTQ1YTQtYjk4My0xOGJjMWZiYjgxZWMiLCJlbWFpbCI6ImJsb2NrY2hhaW5AcWEub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiYmxvY2tjaGFpbnFhIiwianRpIjoiZTczN2I0OGItNDJlZi00MDY0LWEzNTItNjViZDE5NTkxMjU5IiwiZXhwIjoxNzgwOTY1OTQxLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.uerDaLH_qF2XVwM1BrF54Qx8ixKDNg_dR81lmsq8ar4","detail":null}
```
</details>

### ❌ add_wallet

- **Description:** Add Algorand wallet
- **Method:** `POST`
- **Path:** `/api/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec/wallets`
- **Status:** 404
- **Duration:** 5ms
- **Error:** Expected status 200, got 404.

### ✅ get_ops_by_avatar_empty

- **Description:** Get operations for avatar with no operations
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec`
- **Status:** 200
- **Duration:** 77ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ✅ get_op_by_id_nonexistent

- **Description:** Get non-existent operation by ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/00000000-0000-0000-0000-000000000000`
- **Status:** 404
- **Duration:** 42ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Operation not found.","result":null,"detail":null}
```
</details>

### ✅ get_op_unauthorized

- **Description:** Get operation without auth
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 35ms

### ✅ get_ops_by_avatar_unauthorized

- **Description:** Get operations by avatar without auth
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec`
- **Status:** 401
- **Duration:** 36ms

### ✅ get_ops_wrong_avatar_id

- **Description:** Get operations with malformed avatar ID in path
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/not-a-guid`
- **Status:** 404
- **Duration:** 65ms

### ❌ cleanup_wallet

- **Description:** Remove seeded wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec/wallets/{{bcWallet.walletId}}`
- **Status:** 404
- **Duration:** 116ms
- **Error:** Expected status 200, got 404.

### ❌ cleanup_avatar

- **Description:** Delete seeded avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec`
- **Status:** 404
- **Duration:** 54ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ BlockchainOperationController

- **Total:** 6 | **Passed:** 3 | **Failed:** 3 | **Skipped:** 0
- **Duration:** 558ms

### ✅ seed_avatar

- **Description:** Register avatar for blockchain operation tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 203ms
- **Extracted:**
  - `bavatar.avatarId` = `0028eb26-9d88-4b02-b979-04c491996d8d`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"0028eb26-9d88-4b02-b979-04c491996d8d","username":"blockchaintest","email":"blockchain@test.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:41.3544257Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_seed

- **Description:** Login as blockchain test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 195ms
- **Extracted:**
  - `bauth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIwMDI4ZWIyNi05ZDg4LTRiMDItYjk3OS0wNGM0OTE5OTZkOGQiLCJlbWFpbCI6ImJsb2NrY2hhaW5AdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJibG9ja2NoYWludGVzdCIsImp0aSI6IjI4MWE1ZTkyLTg5YjYtNDE5MC1iNDgzLWU0MmU1ODcyMTEzOCIsImV4cCI6MTc4MDk2NTk0MSwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.OOF4zcPLtrNJJJjZJqHEFsY6B6rDp7xtRF2MG1Jk0sw`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIwMDI4ZWIyNi05ZDg4LTRiMDItYjk3OS0wNGM0OTE5OTZkOGQiLCJlbWFpbCI6ImJsb2NrY2hhaW5AdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJibG9ja2NoYWludGVzdCIsImp0aSI6IjI4MWE1ZTkyLTg5YjYtNDE5MC1iNDgzLWU0MmU1ODcyMTEzOCIsImV4cCI6MTc4MDk2NTk0MSwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.OOF4zcPLtrNJJJjZJqHEFsY6B6rDp7xtRF2MG1Jk0sw","detail":null}
```
</details>

### ❌ seed_wallet

- **Description:** Add a wallet for the avatar
- **Method:** `POST`
- **Path:** `/api/avatar/0028eb26-9d88-4b02-b979-04c491996d8d/wallets`
- **Status:** 404
- **Duration:** 14ms
- **Error:** Expected status 200, got 404.

### ✅ get_operations_by_avatar

- **Description:** Get operations by avatar (may be empty)
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/0028eb26-9d88-4b02-b979-04c491996d8d`
- **Status:** 200
- **Duration:** 63ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ cleanup_wallet

- **Description:** Remove the seeded wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/0028eb26-9d88-4b02-b979-04c491996d8d/wallets/{{bwallet.walletId}}`
- **Status:** 404
- **Duration:** 11ms
- **Error:** Expected status 200, got 404.

### ❌ cleanup_avatar

- **Description:** Remove the seeded avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/0028eb26-9d88-4b02-b979-04c491996d8d`
- **Status:** 404
- **Duration:** 69ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ CrossController_E2E

- **Total:** 47 | **Passed:** 12 | **Failed:** 35 | **Skipped:** 0
- **Duration:** 2942ms

### ✅ e2e1_register

- **Description:** E2E1: Register avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 241ms
- **Extracted:**
  - `e2eAvatar.id` = `f129f5f2-4c39-4b88-a9c6-dd87149920ce`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"f129f5f2-4c39-4b88-a9c6-dd87149920ce","username":"e2euser","email":"e2e@flow.azoa","title":"Trader","firstName":"End","lastName":"ToEnd","createdDate":"2026-06-08T00:45:41.9400416Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e1_login

- **Description:** E2E1: Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 329ms
- **Extracted:**
  - `e2eAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJmMTI5ZjVmMi00YzM5LTRiODgtYTljNi1kZDg3MTQ5OTIwY2UiLCJlbWFpbCI6ImUyZUBmbG93Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6ImUyZXVzZXIiLCJqdGkiOiI1MDFkZWU0Ni00NmMzLTRjM2ItYmYwNC0xMTQ1ZjNlOWU4NTIiLCJleHAiOjE3ODA5NjU5NDIsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.wtZi9sLV2gE3HoN54Kdztfz73q1zjUc66cUjMpJcgSo`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJmMTI5ZjVmMi00YzM5LTRiODgtYTljNi1kZDg3MTQ5OTIwY2UiLCJlbWFpbCI6ImUyZUBmbG93Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6ImUyZXVzZXIiLCJqdGkiOiI1MDFkZWU0Ni00NmMzLTRjM2ItYmYwNC0xMTQ1ZjNlOWU4NTIiLCJleHAiOjE3ODA5NjU5NDIsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.wtZi9sLV2gE3HoN54Kdztfz73q1zjUc66cUjMpJcgSo","detail":null}
```
</details>

### ❌ e2e1_add_algo_wallet

- **Description:** E2E1: Add Algorand wallet
- **Method:** `POST`
- **Path:** `/api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce/wallets`
- **Status:** 404
- **Duration:** 58ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_get_wallets

- **Description:** E2E1: Verify wallet exists
- **Method:** `GET`
- **Path:** `/api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce/wallets`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_create_source_holon

- **Description:** E2E1: Create source holon for minting
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 65ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e1_create_target_holon

- **Description:** E2E1: Create target holon for exchange
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 34ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e1_mint_source

- **Description:** E2E1: Mint asset on source holon
- **Method:** `POST`
- **Path:** `/api/holon/{{e2eSourceHolon.id}}/mint`
- **Status:** 404
- **Duration:** 40ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_exchange

- **Description:** E2E1: Exchange source -> target
- **Method:** `POST`
- **Path:** `/api/holon/{{e2eSourceHolon.id}}/exchange`
- **Status:** 404
- **Duration:** 23ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_interact_source

- **Description:** E2E1: Add metadata post-exchange
- **Method:** `POST`
- **Path:** `/api/holon/{{e2eSourceHolon.id}}/interact`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_get_mint_op

- **Description:** E2E1: Verify mint operation exists
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{e2eMintOp.mintOpId}}`
- **Status:** 404
- **Duration:** 3ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_get_exchange_op

- **Description:** E2E1: Verify exchange operation exists
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{e2eExchangeOp.exchangeOpId}}`
- **Status:** 404
- **Duration:** 36ms
- **Error:** Expected status 200, got 404.

### ✅ e2e1_get_ops_by_avatar

- **Description:** E2E1: List all ops for avatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce`
- **Status:** 200
- **Duration:** 55ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ✅ e2e1_query_holons

- **Description:** E2E1: Query holons by name
- **Method:** `GET`
- **Path:** `/api/holon?name=E2E`
- **Status:** 200
- **Duration:** 27ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ e2e1_update_avatar

- **Description:** E2E1: Update avatar profile post-activity
- **Method:** `PUT`
- **Path:** `/api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce`
- **Status:** 400
- **Duration:** 61ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e1_cleanup_holons

- **Description:** E2E1: Delete target holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2eTargetHolon.id}}`
- **Status:** 404
- **Duration:** 3ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_cleanup_source_holon

- **Description:** E2E1: Delete source holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2eSourceHolon.id}}`
- **Status:** 404
- **Duration:** 71ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_cleanup_wallet

- **Description:** E2E1: Remove wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce/wallets/{{e2eWallet.walletId}}`
- **Status:** 404
- **Duration:** 203ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_cleanup_avatar

- **Description:** E2E1: Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce`
- **Status:** 404
- **Duration:** 43ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e2_register

- **Description:** E2E2: Register ODK developer avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 207ms
- **Extracted:**
  - `e2e2Avatar.id` = `16d8637e-31b2-492c-98a5-f82178f6b178`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"16d8637e-31b2-492c-98a5-f82178f6b178","username":"odkdev","email":"odkdev@flow.azoa","title":null,"firstName":"ODK","lastName":"Developer","createdDate":"2026-06-08T00:45:43.2190367Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e2_login

- **Description:** E2E2: Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 181ms
- **Extracted:**
  - `e2e2Auth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxNmQ4NjM3ZS0zMWIyLTQ5MmMtOThhNS1mODIxNzhmNmIxNzgiLCJlbWFpbCI6Im9ka2RldkBmbG93Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6Im9ka2RldiIsImp0aSI6ImRiNzE4ZjFiLTc5NjItNGUxYS05MDc2LTQ2MmFlNDkzMmRkMSIsImV4cCI6MTc4MDk2NTk0MywiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.ZKsJsIvIH7mPpDHq_NZn4OmpYrqg7IUApy1EJcrgYgI`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxNmQ4NjM3ZS0zMWIyLTQ5MmMtOThhNS1mODIxNzhmNmIxNzgiLCJlbWFpbCI6Im9ka2RldkBmbG93Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6Im9ka2RldiIsImp0aSI6ImRiNzE4ZjFiLTc5NjItNGUxYS05MDc2LTQ2MmFlNDkzMmRkMSIsImV4cCI6MTc4MDk2NTk0MywiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.ZKsJsIvIH7mPpDHq_NZn4OmpYrqg7IUApy1EJcrgYgI","detail":null}
```
</details>

### ❌ e2e2_create_holon_a

- **Description:** E2E2: Create bound holon A
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 43ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e2_create_holon_b

- **Description:** E2E2: Create bound holon B
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 36ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e2_create_odk

- **Description:** E2E2: Create STAR ODK
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 400
- **Duration:** 65ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealStarStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealStarStore.UpsertAsync(ISTARODK odk, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealStarStore.cs:line 90","inner":null}}
```
</details>

### ❌ e2e2_generate_odk

- **Description:** E2E2: Generate dApp with bound holons
- **Method:** `POST`
- **Path:** `/api/starodk/{{e2e2ODK.id}}/generate`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_get_odk_post_gen

- **Description:** E2E2: Verify ODK has generated code
- **Method:** `GET`
- **Path:** `/api/starodk/{{e2e2ODK.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_deploy_odk

- **Description:** E2E2: Deploy generated dApp
- **Method:** `POST`
- **Path:** `/api/starodk/{{e2e2ODK.id}}/deploy`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_get_odk_post_deploy

- **Description:** E2E2: Verify ODK has deployment config
- **Method:** `GET`
- **Path:** `/api/starodk/{{e2e2ODK.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ✅ e2e2_get_all_odks

- **Description:** E2E2: List all ODKs
- **Method:** `GET`
- **Path:** `/api/starodk`
- **Status:** 200
- **Duration:** 61ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ e2e2_cleanup_odk

- **Description:** E2E2: Delete ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{e2e2ODK.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_cleanup_holon_a

- **Description:** E2E2: Delete holon A
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e2HolonA.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_cleanup_holon_b

- **Description:** E2E2: Delete holon B
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e2HolonB.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_cleanup_avatar

- **Description:** E2E2: Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/16d8637e-31b2-492c-98a5-f82178f6b178`
- **Status:** 404
- **Duration:** 32ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e3_register_alpha

- **Description:** E2E3: Register Alpha avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 212ms
- **Extracted:**
  - `alphaAvatar.id` = `921dfe0c-01ee-42a7-9cb3-4d6d53b8e42a`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"921dfe0c-01ee-42a7-9cb3-4d6d53b8e42a","username":"alpha","email":"alpha@iso.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:43.8482121Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e3_login_alpha

- **Description:** E2E3: Login Alpha
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 183ms
- **Extracted:**
  - `alphaAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI5MjFkZmUwYy0wMWVlLTQyYTctOWNiMy00ZDZkNTNiOGU0MmEiLCJlbWFpbCI6ImFscGhhQGlzby5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJhbHBoYSIsImp0aSI6IjA0Mjc4MWE4LWZjNmItNDI1OC1iM2VmLTc0N2UwMThkYWRmNiIsImV4cCI6MTc4MDk2NTk0NCwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.BqKiNxdenRP-O5IIsd-O-Dfz0efeXQq4UzS3aNR-Zec`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI5MjFkZmUwYy0wMWVlLTQyYTctOWNiMy00ZDZkNTNiOGU0MmEiLCJlbWFpbCI6ImFscGhhQGlzby5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJhbHBoYSIsImp0aSI6IjA0Mjc4MWE4LWZjNmItNDI1OC1iM2VmLTc0N2UwMThkYWRmNiIsImV4cCI6MTc4MDk2NTk0NCwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.BqKiNxdenRP-O5IIsd-O-Dfz0efeXQq4UzS3aNR-Zec","detail":null}
```
</details>

### ✅ e2e3_register_beta

- **Description:** E2E3: Register Beta avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 248ms
- **Extracted:**
  - `betaAvatar.id` = `60b33154-67c3-4fb8-9d1b-09d998a6455f`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"60b33154-67c3-4fb8-9d1b-09d998a6455f","username":"beta","email":"beta@iso.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:44.2594455Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e3_login_beta

- **Description:** E2E3: Login Beta
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 162ms
- **Extracted:**
  - `betaAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI2MGIzMzE1NC02N2MzLTRmYjgtOWQxYi0wOWQ5OThhNjQ1NWYiLCJlbWFpbCI6ImJldGFAaXNvLm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6ImJldGEiLCJqdGkiOiJiMzM0YzZjZi1mMTNlLTQ2OWQtYWFlYi1lNzhhNjI2MjE0YjUiLCJleHAiOjE3ODA5NjU5NDQsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.fwNMJq28pcJkftC0fxZb4DTAgDkqBeuEkDVYU_K_xww`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI2MGIzMzE1NC02N2MzLTRmYjgtOWQxYi0wOWQ5OThhNjQ1NWYiLCJlbWFpbCI6ImJldGFAaXNvLm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6ImJldGEiLCJqdGkiOiJiMzM0YzZjZi1mMTNlLTQ2OWQtYWFlYi1lNzhhNjI2MjE0YjUiLCJleHAiOjE3ODA5NjU5NDQsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.fwNMJq28pcJkftC0fxZb4DTAgDkqBeuEkDVYU_K_xww","detail":null}
```
</details>

### ❌ e2e3_alpha_create_holon

- **Description:** E2E3: Alpha creates private holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 35ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e3_beta_create_holon

- **Description:** E2E3: Beta creates private holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 35ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e3_alpha_get_own

- **Description:** E2E3: Alpha gets own holon
- **Method:** `GET`
- **Path:** `/api/holon/{{alphaHolon.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e3_beta_get_own

- **Description:** E2E3: Beta gets own holon
- **Method:** `GET`
- **Path:** `/api/holon/{{betaHolon.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e3_alpha_get_beta

- **Description:** E2E3: Alpha tries to get Beta's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{betaHolon.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e3_beta_get_alpha

- **Description:** E2E3: Beta tries to get Alpha's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{alphaHolon.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ✅ e2e3_query_all

- **Description:** E2E3: Query all holons - both should be visible
- **Method:** `GET`
- **Path:** `/api/holon`
- **Status:** 200
- **Duration:** 56ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ e2e3_cleanup_alpha_holon

- **Description:** E2E3: Delete Alpha's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{alphaHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ e2e3_cleanup_beta_holon

- **Description:** E2E3: Delete Beta's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{betaHolon.id}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e3_cleanup_alpha

- **Description:** E2E3: Delete Alpha avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/921dfe0c-01ee-42a7-9cb3-4d6d53b8e42a`
- **Status:** 404
- **Duration:** 28ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ e2e3_cleanup_beta

- **Description:** E2E3: Delete Beta avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/60b33154-67c3-4fb8-9d1b-09d998a6455f`
- **Status:** 404
- **Duration:** 34ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ E2E-Flows

- **Total:** 200 | **Passed:** 19 | **Failed:** 181 | **Skipped:** 0
- **Duration:** 4640ms

### ✅ e2e1_register

- **Description:** [E2E-1] Register avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 263ms
- **Extracted:**
  - `e2e1Avatar.avatarId` = `d1d8365d-6752-45a4-b495-1305166df7cc`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"d1d8365d-6752-45a4-b495-1305166df7cc","username":"e2eavatar","email":"e2e1@test.azoa","title":"E2E","firstName":"End","lastName":"ToEnd","createdDate":"2026-06-08T00:45:42.1771795Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e1_login

- **Description:** [E2E-1] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 161ms
- **Extracted:**
  - `e2e1Auth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkMWQ4MzY1ZC02NzUyLTQ1YTQtYjQ5NS0xMzA1MTY2ZGY3Y2MiLCJlbWFpbCI6ImUyZTFAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmVhdmF0YXIiLCJqdGkiOiJiN2VjZjgyMi1iZGU5LTQzZjUtYmQ0ZC04ZmVkNTZkYzgxNDAiLCJleHAiOjE3ODA5NjU5NDIsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.USZoWaBtzgpmHSfZmqFJPk1h61QHjs2vpGtGcnBjiIQ`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkMWQ4MzY1ZC02NzUyLTQ1YTQtYjQ5NS0xMzA1MTY2ZGY3Y2MiLCJlbWFpbCI6ImUyZTFAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmVhdmF0YXIiLCJqdGkiOiJiN2VjZjgyMi1iZGU5LTQzZjUtYmQ0ZC04ZmVkNTZkYzgxNDAiLCJleHAiOjE3ODA5NjU5NDIsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.USZoWaBtzgpmHSfZmqFJPk1h61QHjs2vpGtGcnBjiIQ","detail":null}
```
</details>

### ❌ e2e1_add_wallet

- **Description:** [E2E-1] Add wallet
- **Method:** `POST`
- **Path:** `/api/avatar/d1d8365d-6752-45a4-b495-1305166df7cc/wallets`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_create_holon

- **Description:** [E2E-1] Create parent holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 34ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e1_get_holon

- **Description:** [E2E-1] Verify holon ownership via GET
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e1ParentHolon.holonId}}`
- **Status:** 404
- **Duration:** 33ms
- **Error:** Expected status 200, got 404.

### ✅ e2e1_query_holons

- **Description:** [E2E-1] Query holons by name
- **Method:** `GET`
- **Path:** `/api/holon?name=E2EParentHolon`
- **Status:** 200
- **Duration:** 94ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ e2e1_update_holon

- **Description:** [E2E-1] Update holon metadata
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e1ParentHolon.holonId}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_interact_holon

- **Description:** [E2E-1] Interact adding metadata
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e1ParentHolon.holonId}}/interact`
- **Status:** 404
- **Duration:** 3ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_create_subholon

- **Description:** [E2E-1] Create child holon with parent
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 36ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.parentHolonId":["The JSON value could not be converted to System.Nullable`1[System.Guid]. Path: $.parentHolonId | LineNumber: 0 | BytePositionInLine: 121."]},"traceId":"00-258cd9271760d9c447cca85103fa7862-1912e4f34b403d93-01"}
```
</details>

### ❌ e2e1_get_subholon

- **Description:** [E2E-1] Get child holon
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e1SubHolon.holonId}}`
- **Status:** 404
- **Duration:** 29ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_delete_subholon

- **Description:** [E2E-1] Delete child holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e1SubHolon.holonId}}`
- **Status:** 404
- **Duration:** 28ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_delete_holon

- **Description:** [E2E-1] Delete parent holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e1ParentHolon.holonId}}`
- **Status:** 404
- **Duration:** 53ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_remove_wallet

- **Description:** [E2E-1] Remove wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/d1d8365d-6752-45a4-b495-1305166df7cc/wallets/{{e2e1Wallet.walletId}}`
- **Status:** 404
- **Duration:** 36ms
- **Error:** Expected status 200, got 404.

### ❌ e2e1_delete_avatar

- **Description:** [E2E-1] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/d1d8365d-6752-45a4-b495-1305166df7cc`
- **Status:** 404
- **Duration:** 70ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e2_register

- **Description:** [E2E-2] Register avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 235ms
- **Extracted:**
  - `e2e2Avatar.avatarId` = `d7393c26-1da3-401e-b606-5ab40bf60268`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"d7393c26-1da3-401e-b606-5ab40bf60268","username":"e2e2avatar","email":"e2e2@test.azoa","title":null,"firstName":"Star","lastName":"Flow","createdDate":"2026-06-08T00:45:42.994141Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e2_login

- **Description:** [E2E-2] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 171ms
- **Extracted:**
  - `e2e2Auth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkNzM5M2MyNi0xZGEzLTQwMWUtYjYwNi01YWI0MGJmNjAyNjgiLCJlbWFpbCI6ImUyZTJAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmUyYXZhdGFyIiwianRpIjoiOGFiOTcxNWEtMjYwNi00NTJkLWJlNzEtZWJlYjAwNjZhZjc1IiwiZXhwIjoxNzgwOTY1OTQzLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.IzzZmBqDBpe4dTWIRAE6aYPj4eXlJvt612CyXW8EkoI`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkNzM5M2MyNi0xZGEzLTQwMWUtYjYwNi01YWI0MGJmNjAyNjgiLCJlbWFpbCI6ImUyZTJAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmUyYXZhdGFyIiwianRpIjoiOGFiOTcxNWEtMjYwNi00NTJkLWJlNzEtZWJlYjAwNjZhZjc1IiwiZXhwIjoxNzgwOTY1OTQzLCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.IzzZmBqDBpe4dTWIRAE6aYPj4eXlJvt612CyXW8EkoI","detail":null}
```
</details>

### ❌ e2e2_create_holon

- **Description:** [E2E-2] Create holon to bind to ODK
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 49ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e2_create_odk

- **Description:** [E2E-2] Create STAR ODK linked to avatar
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 400
- **Duration:** 94ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealStarStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealStarStore.UpsertAsync(ISTARODK odk, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealStarStore.cs:line 90","inner":null}}
```
</details>

### ❌ e2e2_get_odk

- **Description:** [E2E-2] Get created ODK
- **Method:** `GET`
- **Path:** `/api/starodk/{{e2e2Odk.odkId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ e2e2_get_all_odks

- **Description:** [E2E-2] List all ODKs
- **Method:** `GET`
- **Path:** `/api/starodk`
- **Status:** 200
- **Duration:** 37ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ e2e2_generate_odk

- **Description:** [E2E-2] Generate dApp with bound holon
- **Method:** `POST`
- **Path:** `/api/starodk/{{e2e2Odk.odkId}}/generate`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_delete_odk

- **Description:** [E2E-2] Delete ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{e2e2Odk.odkId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_delete_holon

- **Description:** [E2E-2] Delete bound holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e2Holon.holonId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e2_delete_avatar

- **Description:** [E2E-2] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/d7393c26-1da3-401e-b606-5ab40bf60268`
- **Status:** 404
- **Duration:** 31ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e3_register_a

- **Description:** [E2E-3] Register avatar A
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 214ms
- **Extracted:**
  - `e2e3AvatarA.avatarId` = `ca74748e-ac3c-42f1-8bda-3f2cf1bbb9ca`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"ca74748e-ac3c-42f1-8bda-3f2cf1bbb9ca","username":"e2e3a","email":"e2e3a@test.azoa","title":null,"firstName":"Alice","lastName":null,"createdDate":"2026-06-08T00:45:43.5891666Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e3_login_a

- **Description:** [E2E-3] Login avatar A
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 167ms
- **Extracted:**
  - `e2e3AuthA.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJjYTc0NzQ4ZS1hYzNjLTQyZjEtOGJkYS0zZjJjZjFiYmI5Y2EiLCJlbWFpbCI6ImUyZTNhQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiZTJlM2EiLCJqdGkiOiI2OTMzMGQ1YS01YjkwLTQ3NjYtYTI5YS05ODY2MDZmNjFkOGYiLCJleHAiOjE3ODA5NjU5NDMsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.nUQtKxHe_NW69nHA65uUVa6QlKvcYOQOWhaJ6BWWsLs`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJjYTc0NzQ4ZS1hYzNjLTQyZjEtOGJkYS0zZjJjZjFiYmI5Y2EiLCJlbWFpbCI6ImUyZTNhQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiZTJlM2EiLCJqdGkiOiI2OTMzMGQ1YS01YjkwLTQ3NjYtYTI5YS05ODY2MDZmNjFkOGYiLCJleHAiOjE3ODA5NjU5NDMsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.nUQtKxHe_NW69nHA65uUVa6QlKvcYOQOWhaJ6BWWsLs","detail":null}
```
</details>

### ❌ e2e3_create_holon_a

- **Description:** [E2E-3] Avatar A creates holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 29ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ✅ e2e3_register_b

- **Description:** [E2E-3] Register avatar B
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 201ms
- **Extracted:**
  - `e2e3AvatarB.avatarId` = `ea1da1ed-a03a-44b3-804c-0ad7029eff7c`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"ea1da1ed-a03a-44b3-804c-0ad7029eff7c","username":"e2e3b","email":"e2e3b@test.azoa","title":null,"firstName":"Bob","lastName":null,"createdDate":"2026-06-08T00:45:44.0035804Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e3_login_b

- **Description:** [E2E-3] Login avatar B
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 193ms
- **Extracted:**
  - `e2e3AuthB.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJlYTFkYTFlZC1hMDNhLTQ0YjMtODA0Yy0wYWQ3MDI5ZWZmN2MiLCJlbWFpbCI6ImUyZTNiQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiZTJlM2IiLCJqdGkiOiIxNGI1MmFkZC1jM2YxLTRiZmEtOTc4NC0yYmIyNGQxYWM2ZmUiLCJleHAiOjE3ODA5NjU5NDQsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.K8L7eqXsLbNKryzCOKN1mysnd70M0RQxrtf_jb57fO0`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJlYTFkYTFlZC1hMDNhLTQ0YjMtODA0Yy0wYWQ3MDI5ZWZmN2MiLCJlbWFpbCI6ImUyZTNiQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiZTJlM2IiLCJqdGkiOiIxNGI1MmFkZC1jM2YxLTRiZmEtOTc4NC0yYmIyNGQxYWM2ZmUiLCJleHAiOjE3ODA5NjU5NDQsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.K8L7eqXsLbNKryzCOKN1mysnd70M0RQxrtf_jb57fO0","detail":null}
```
</details>

### ✅ e2e3_b_get_a_holon

- **Description:** [E2E-3] Avatar B tries to get A's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e3HolonA.holonId}}`
- **Status:** 404
- **Duration:** 1ms

### ✅ e2e3_b_update_a_holon

- **Description:** [E2E-3] Avatar B tries to update A's holon
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e3HolonA.holonId}}`
- **Status:** 404
- **Duration:** 0ms

### ✅ e2e3_b_delete_a_holon

- **Description:** [E2E-3] Avatar B tries to delete A's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e3HolonA.holonId}}`
- **Status:** 404
- **Duration:** 0ms

### ❌ e2e3_cleanup_holon_a

- **Description:** [E2E-3] Avatar A deletes her holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e3HolonA.holonId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e3_cleanup_avatar_a

- **Description:** [E2E-3] Delete avatar A
- **Method:** `DELETE`
- **Path:** `/api/avatar/ca74748e-ac3c-42f1-8bda-3f2cf1bbb9ca`
- **Status:** 404
- **Duration:** 31ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ e2e3_cleanup_avatar_b

- **Description:** [E2E-3] Delete avatar B
- **Method:** `DELETE`
- **Path:** `/api/avatar/ea1da1ed-a03a-44b3-804c-0ad7029eff7c`
- **Status:** 404
- **Duration:** 32ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e4_register

- **Description:** [E2E-4] Register avatar for blockchain flow
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 209ms
- **Extracted:**
  - `e2e4Avatar.avatarId` = `d440aa03-5dce-41ef-b272-43dab43c3793`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"d440aa03-5dce-41ef-b272-43dab43c3793","username":"e2e4bc","email":"e2e4@test.azoa","title":null,"firstName":"Block","lastName":"Chain","createdDate":"2026-06-08T00:45:44.4696749Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e4_login

- **Description:** [E2E-4] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 173ms
- **Extracted:**
  - `e2e4Auth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkNDQwYWEwMy01ZGNlLTQxZWYtYjI3Mi00M2RhYjQzYzM3OTMiLCJlbWFpbCI6ImUyZTRAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmU0YmMiLCJqdGkiOiJkOWYxMWU0Ny00NDVlLTQyNTMtOGU4Mi0xODlmYmE5NTk3ZDAiLCJleHAiOjE3ODA5NjU5NDQsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.lrZXhfpPnjqsTNlIPwGs-kcBbS2-M7I2IuNp_hZUQls`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkNDQwYWEwMy01ZGNlLTQxZWYtYjI3Mi00M2RhYjQzYzM3OTMiLCJlbWFpbCI6ImUyZTRAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmU0YmMiLCJqdGkiOiJkOWYxMWU0Ny00NDVlLTQyNTMtOGU4Mi0xODlmYmE5NTk3ZDAiLCJleHAiOjE3ODA5NjU5NDQsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.lrZXhfpPnjqsTNlIPwGs-kcBbS2-M7I2IuNp_hZUQls","detail":null}
```
</details>

### ❌ e2e4_add_wallet

- **Description:** [E2E-4] Add wallet for minting
- **Method:** `POST`
- **Path:** `/api/avatar/d440aa03-5dce-41ef-b272-43dab43c3793/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ e2e4_create_holon

- **Description:** [E2E-4] Create holon to mint from
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 39ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ e2e4_mint

- **Description:** [E2E-4] Mint tokens on holon
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e4Holon.holonId}}/mint`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ e2e4_get_operation

- **Description:** [E2E-4] Get blockchain operation by ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{e2e4Op.operationId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ e2e4_get_ops_by_avatar

- **Description:** [E2E-4] Get all operations for avatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/d440aa03-5dce-41ef-b272-43dab43c3793`
- **Status:** 200
- **Duration:** 34ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ e2e4_remove_wallet

- **Description:** [E2E-4] Remove wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/d440aa03-5dce-41ef-b272-43dab43c3793/wallets/{{e2e4Wallet.walletId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e4_delete_holon

- **Description:** [E2E-4] Delete holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e4Holon.holonId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ e2e4_delete_avatar

- **Description:** [E2E-4] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/d440aa03-5dce-41ef-b272-43dab43c3793`
- **Status:** 404
- **Duration:** 74ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e5_register

- **Description:** [E2E-5] Register stress-test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 221ms
- **Extracted:**
  - `e2e5Avatar.avatarId` = `b5c0c23e-db7b-4b56-b709-08ef346e70ed`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"b5c0c23e-db7b-4b56-b709-08ef346e70ed","username":"e2e5stress","email":"e2e5@test.azoa","title":null,"firstName":"Stress","lastName":"Test","createdDate":"2026-06-08T00:45:45.0106251Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ e2e5_login

- **Description:** [E2E-5] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 226ms
- **Extracted:**
  - `e2e5Auth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJiNWMwYzIzZS1kYjdiLTRiNTYtYjcwOS0wOGVmMzQ2ZTcwZWQiLCJlbWFpbCI6ImUyZTVAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmU1c3RyZXNzIiwianRpIjoiMTU1YWEyYTEtMmZkZi00MDFkLTgxOWYtMzQ4ZWJlYzRhYWYyIiwiZXhwIjoxNzgwOTY1OTQ1LCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.XKUxt7U6czUwpF92x1zN2OLWH2ceasVWDQv9c0z1Byo`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJiNWMwYzIzZS1kYjdiLTRiNTYtYjcwOS0wOGVmMzQ2ZTcwZWQiLCJlbWFpbCI6ImUyZTVAdGVzdC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJlMmU1c3RyZXNzIiwianRpIjoiMTU1YWEyYTEtMmZkZi00MDFkLTgxOWYtMzQ4ZWJlYzRhYWYyIiwiZXhwIjoxNzgwOTY1OTQ1LCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.XKUxt7U6czUwpF92x1zN2OLWH2ceasVWDQv9c0z1Byo","detail":null}
```
</details>

### ❌ e2e5_get_1

- **Description:** [E2E-5] Rapid GET #1
- **Method:** `GET`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 404
- **Duration:** 54ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_put_1

- **Description:** [E2E-5] Rapid PUT #1
- **Method:** `PUT`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 400
- **Duration:** 37ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_get_2

- **Description:** [E2E-5] Rapid GET #2
- **Method:** `GET`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 404
- **Duration:** 48ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_put_2

- **Description:** [E2E-5] Rapid PUT #2
- **Method:** `PUT`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 400
- **Duration:** 75ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_get_3

- **Description:** [E2E-5] Rapid GET #3
- **Method:** `GET`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 404
- **Duration:** 32ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_put_3

- **Description:** [E2E-5] Rapid PUT #3
- **Method:** `PUT`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 400
- **Duration:** 40ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_get_4

- **Description:** [E2E-5] Rapid GET #4
- **Method:** `GET`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 404
- **Duration:** 45ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_put_4

- **Description:** [E2E-5] Rapid PUT #4
- **Method:** `PUT`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 400
- **Duration:** 172ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_get_5

- **Description:** [E2E-5] Rapid GET #5
- **Method:** `GET`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 404
- **Duration:** 69ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ e2e5_delete

- **Description:** [E2E-5] Delete stress avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed`
- **Status:** 404
- **Duration:** 92ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ✅ e2e6_register

- **Description:** [E2E-6] Register holon-stress avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 312ms
- **Extracted:**
  - `e2e6Avatar.avatarId` = `05bb26c8-3b4a-45fa-b1e7-5ea72351cd02`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"05bb26c8-3b4a-45fa-b1e7-5ea72351cd02","username":"e2e6holon","email":"e2e6@test.azoa","title":null,"firstName":"Holon","lastName":"Stress","createdDate":"2026-06-08T00:45:46.1724654Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ❌ e2e6_login

- **Description:** [E2E-6] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 4ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_create

- **Description:** [E2E-6] Create holon for stress
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 2ms
- **Error:** Expected status 200, got 401.

### ❌ e2e6_interact_1

- **Description:** [E2E-6] Rapid interact #1
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_interact_2

- **Description:** [E2E-6] Rapid interact #2
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}/interact`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_interact_3

- **Description:** [E2E-6] Rapid interact #3
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_interact_4

- **Description:** [E2E-6] Rapid interact #4
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}/interact`
- **Status:** 429
- **Duration:** 13ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_interact_5

- **Description:** [E2E-6] Rapid interact #5
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}/interact`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_get_final

- **Description:** [E2E-6] Final GET to verify state
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_delete_holon

- **Description:** [E2E-6] Delete stress holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e6Holon.holonId}}`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e6_delete_avatar

- **Description:** [E2E-6] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/05bb26c8-3b4a-45fa-b1e7-5ea72351cd02`
- **Status:** 401
- **Duration:** 2ms
- **Error:** Expected status 200, got 401.

### ❌ e2e7_register

- **Description:** [E2E-7] Register persistent avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e7_login

- **Description:** [E2E-7] Login once
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e7_get_avatar

- **Description:** [E2E-7] Token reuse: Avatar GET
- **Method:** `GET`
- **Path:** `/api/avatar/{{e2e7Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e7_get_all_avatars

- **Description:** [E2E-7] Token reuse: Avatar list
- **Method:** `GET`
- **Path:** `/api/avatar`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e7_create_holon

- **Description:** [E2E-7] Token reuse: Holon create
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 5ms
- **Error:** Expected status 200, got 401.

### ❌ e2e7_get_holon

- **Description:** [E2E-7] Token reuse: Holon GET
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e7Holon.holonId}}`
- **Status:** 429
- **Duration:** 20ms
- **Error:** Expected status 200, got 429.

### ❌ e2e7_query_holon

- **Description:** [E2E-7] Token reuse: Holon query
- **Method:** `GET`
- **Path:** `/api/holon?name=PersistHolon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e7_get_all_odks

- **Description:** [E2E-7] Token reuse: STAR ODK list
- **Method:** `GET`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e7_get_bc_by_avatar

- **Description:** [E2E-7] Token reuse: Blockchain ops by avatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/{{e2e7Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e7_delete_holon

- **Description:** [E2E-7] Cleanup holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e7Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e7_delete_avatar

- **Description:** [E2E-7] Cleanup avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e7Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_register

- **Description:** [E2E-8] Register avatar for deploy flow
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_login

- **Description:** [E2E-8] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_create_holon

- **Description:** [E2E-8] Create holon to bind
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 32ms
- **Error:** Expected status 200, got 401.

### ❌ e2e8_create_odk

- **Description:** [E2E-8] Create STAR ODK
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 9ms
- **Error:** Expected status 200, got 401.

### ❌ e2e8_generate_odk

- **Description:** [E2E-8] Generate dApp
- **Method:** `POST`
- **Path:** `/api/starodk/{{e2e8Odk.odkId}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_deploy_odk

- **Description:** [E2E-8] Deploy generated dApp
- **Method:** `POST`
- **Path:** `/api/starodk/{{e2e8Odk.odkId}}/deploy`
- **Status:** 429
- **Duration:** 17ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_get_odk_after_deploy

- **Description:** [E2E-8] Get ODK after deploy
- **Method:** `GET`
- **Path:** `/api/starodk/{{e2e8Odk.odkId}}`
- **Status:** 429
- **Duration:** 3ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_delete_odk

- **Description:** [E2E-8] Delete ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{e2e8Odk.odkId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_delete_holon

- **Description:** [E2E-8] Delete bound holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e8Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e8_delete_avatar

- **Description:** [E2E-8] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e8Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 24ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_register

- **Description:** [E2E-9] Register avatar for exchange
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_login

- **Description:** [E2E-9] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 16ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_add_wallet

- **Description:** [E2E-9] Add wallet
- **Method:** `POST`
- **Path:** `/api/avatar/{{e2e9Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_create_source

- **Description:** [E2E-9] Create source holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e9_create_target

- **Description:** [E2E-9] Create target holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e9_mint_source

- **Description:** [E2E-9] Mint on source holon
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e9Source.holonId}}/mint`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_get_mint_op

- **Description:** [E2E-9] Verify mint operation
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{e2e9MintOp.operationId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_exchange

- **Description:** [E2E-9] Exchange source for target
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e9Source.holonId}}/exchange`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_get_exchange_op

- **Description:** [E2E-9] Verify exchange operation
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/{{e2e9ExchangeOp.operationId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_get_ops_by_avatar

- **Description:** [E2E-9] List all ops for avatar
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/{{e2e9Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_delete_source

- **Description:** [E2E-9] Delete source holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e9Source.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_delete_target

- **Description:** [E2E-9] Delete target holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e9Target.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_remove_wallet

- **Description:** [E2E-9] Remove wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e9Avatar.avatarId}}/wallets/{{e2e9Wallet.walletId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e9_delete_avatar

- **Description:** [E2E-9] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e9Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_register

- **Description:** [E2E-10] Register multi-wallet avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_login

- **Description:** [E2E-10] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_wallet_1

- **Description:** [E2E-10] Add wallet 1 (Algorand)
- **Method:** `POST`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_wallet_2

- **Description:** [E2E-10] Add wallet 2 (Solana)
- **Method:** `POST`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_wallet_3

- **Description:** [E2E-10] Add wallet 3 (Ethereum)
- **Method:** `POST`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_get_wallets

- **Description:** [E2E-10] Get all wallets
- **Method:** `GET`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_remove_wallet_2

- **Description:** [E2E-10] Remove middle wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets/{{e2e10Wallet2.walletId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_get_wallets_after

- **Description:** [E2E-10] Get wallets after removal
- **Method:** `GET`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_remove_wallet_1

- **Description:** [E2E-10] Remove wallet 1
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets/{{e2e10Wallet1.walletId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_remove_wallet_3

- **Description:** [E2E-10] Remove wallet 3
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}/wallets/{{e2e10Wallet3.walletId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e10_delete_avatar

- **Description:** [E2E-10] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e10Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_register

- **Description:** [E2E-11] Register bulk avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_login

- **Description:** [E2E-11] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_create_a

- **Description:** [E2E-11] Create holon A
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ e2e11_create_b

- **Description:** [E2E-11] Create holon B
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e11_create_c

- **Description:** [E2E-11] Create holon C
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e11_query_all

- **Description:** [E2E-11] Query all holons
- **Method:** `GET`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e11_query_batch

- **Description:** [E2E-11] Query by batch metadata via name
- **Method:** `GET`
- **Path:** `/api/holon?name=BulkHolon`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ e2e11_update_a

- **Description:** [E2E-11] Update holon A
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e11HolonA.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_interact_b

- **Description:** [E2E-11] Interact holon B
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e11HolonB.holonId}}/interact`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_delete_c

- **Description:** [E2E-11] Delete holon C
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e11HolonC.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_query_remaining

- **Description:** [E2E-11] Query after deletion
- **Method:** `GET`
- **Path:** `/api/holon?name=BulkHolon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e11_delete_a

- **Description:** [E2E-11] Delete holon A
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e11HolonA.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_delete_b

- **Description:** [E2E-11] Delete holon B
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e11HolonB.holonId}}`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e11_delete_avatar

- **Description:** [E2E-11] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e11Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e12_register

- **Description:** [E2E-12] Register disposable avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e12_login

- **Description:** [E2E-12] Login disposable avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e12_delete

- **Description:** [E2E-12] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e12Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e12_login_after_delete

- **Description:** [E2E-12] Login after deletion should fail
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 401, got 429.

### ❌ e2e12_reregister

- **Description:** [E2E-12] Re-register same email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e12_login_reregistered

- **Description:** [E2E-12] Login re-registered avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ e2e12_delete_reregistered

- **Description:** [E2E-12] Delete re-registered avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e12Avatar2.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_register

- **Description:** [E2E-13] Register nesting avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_login

- **Description:** [E2E-13] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_create_gp

- **Description:** [E2E-13] Create grandparent holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e13_create_parent

- **Description:** [E2E-13] Create parent holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ e2e13_create_child

- **Description:** [E2E-13] Create child holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e13_get_gp

- **Description:** [E2E-13] Get grandparent
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e13GP.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_get_parent

- **Description:** [E2E-13] Get parent
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e13Parent.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_get_child

- **Description:** [E2E-13] Get child
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e13Child.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_interact_reparent

- **Description:** [E2E-13] Interact to change child parent
- **Method:** `POST`
- **Path:** `/api/holon/{{e2e13Child.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_get_child_after

- **Description:** [E2E-13] Get child after reparent
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e13Child.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_delete_child

- **Description:** [E2E-13] Delete child
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e13Child.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_delete_parent

- **Description:** [E2E-13] Delete parent
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e13Parent.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_delete_gp

- **Description:** [E2E-13] Delete grandparent
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e13GP.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e13_delete_avatar

- **Description:** [E2E-13] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e13Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e14_register

- **Description:** [E2E-14] Register idempotency avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e14_login

- **Description:** [E2E-14] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e14_create_odk

- **Description:** [E2E-14] Create ODK v1
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e14_update_odk

- **Description:** [E2E-14] CreateOrUpdate ODK v2 (same name)
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e14_verify_same_id

- **Description:** [E2E-14] Verify same ID returned
- **Method:** `GET`
- **Path:** `/api/starodk/{{e2e14OdkV2.odkId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e14_delete_odk

- **Description:** [E2E-14] Delete ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{e2e14OdkV2.odkId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e14_delete_avatar

- **Description:** [E2E-14] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e14Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_register

- **Description:** [E2E-15] Register stress avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_login

- **Description:** [E2E-15] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_create

- **Description:** [E2E-15] Create holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e15_get_1

- **Description:** [E2E-15] Rapid GET #1
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_put_1

- **Description:** [E2E-15] Rapid PUT #1
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_get_2

- **Description:** [E2E-15] Rapid GET #2
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_put_2

- **Description:** [E2E-15] Rapid PUT #2
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_get_3

- **Description:** [E2E-15] Rapid GET #3
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_put_3

- **Description:** [E2E-15] Rapid PUT #3
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_get_4

- **Description:** [E2E-15] Rapid GET #4
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_put_4

- **Description:** [E2E-15] Rapid PUT #4
- **Method:** `PUT`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_get_5

- **Description:** [E2E-15] Rapid GET #5
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_delete

- **Description:** [E2E-15] Delete holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e15_get_deleted

- **Description:** [E2E-15] GET deleted should 404
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e15Holon.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 404, got 429.

### ❌ e2e15_delete_avatar

- **Description:** [E2E-15] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e15Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_reg_a

- **Description:** [E2E-16] Register avatar A
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_reg_b

- **Description:** [E2E-16] Register avatar B
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_reg_c

- **Description:** [E2E-16] Register avatar C
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_login_a

- **Description:** [E2E-16] Login A
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_login_b

- **Description:** [E2E-16] Login B
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_login_c

- **Description:** [E2E-16] Login C
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_holon_a

- **Description:** [E2E-16] Avatar A creates holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e16_holon_b

- **Description:** [E2E-16] Avatar B creates holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e16_holon_c

- **Description:** [E2E-16] Avatar C creates holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e16_query_all

- **Description:** [E2E-16] Query all holons (should see all)
- **Method:** `GET`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e16_a_get_b_holon

- **Description:** [E2E-16] A tries to get B's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e16HolonB.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 404, got 429.

### ❌ e2e16_b_get_c_holon

- **Description:** [E2E-16] B tries to get C's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e16HolonC.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 404, got 429.

### ❌ e2e16_c_get_a_holon

- **Description:** [E2E-16] C tries to get A's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{e2e16HolonA.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 404, got 429.

### ❌ e2e16_del_holon_a

- **Description:** [E2E-16] Delete A's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e16HolonA.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_del_holon_b

- **Description:** [E2E-16] Delete B's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e16HolonB.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_del_holon_c

- **Description:** [E2E-16] Delete C's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e16HolonC.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_del_avatar_a

- **Description:** [E2E-16] Delete avatar A
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e16AvatarA.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_del_avatar_b

- **Description:** [E2E-16] Delete avatar B
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e16AvatarB.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e16_del_avatar_c

- **Description:** [E2E-16] Delete avatar C
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e16AvatarC.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_register

- **Description:** [E2E-17] Register cascade avatar
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_login

- **Description:** [E2E-17] Login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_update_1

- **Description:** [E2E-17] Update username
- **Method:** `PUT`
- **Path:** `/api/avatar/{{e2e17Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_update_2

- **Description:** [E2E-17] Update email
- **Method:** `PUT`
- **Path:** `/api/avatar/{{e2e17Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_update_3

- **Description:** [E2E-17] Update title
- **Method:** `PUT`
- **Path:** `/api/avatar/{{e2e17Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_create_holon

- **Description:** [E2E-17] Create holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e17_query_by_avatar

- **Description:** [E2E-17] Query holons by avatarId
- **Method:** `GET`
- **Path:** `/api/holon?avatarId={{e2e17Avatar.avatarId}}`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ e2e17_get_avatar_final

- **Description:** [E2E-17] Get final avatar state
- **Method:** `GET`
- **Path:** `/api/avatar/{{e2e17Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_delete_holon

- **Description:** [E2E-17] Delete holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{e2e17Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ e2e17_delete_avatar

- **Description:** [E2E-17] Delete avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{e2e17Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

## 🗂️ Frontend

- **Total:** 9 | **Passed:** 9 | **Failed:** 0 | **Skipped:** 0
- **Duration:** 2240ms

### ✅ fe_root

- **Description:** Frontend root page loads (renders or redirects to a sign-in route)
- **Method:** `GET`
- **Path:** `http://localhost:3000/`
- **Status:** 200
- **Duration:** 2079ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/app/page-0b342f800c3a856f.js" async=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script src="/_next/static/chunks/app/layout-8465f0fad9d9adf2.js" async=""></script><title>AZOA Sleek</title><meta name="description" content="Avatar NFT &amp; Blockchain Platform"/><meta name="next-size-adjust"/><script src="/_next/static/chunks/polyfills-c67a75d1b6f99dc8.js" crossorigin="" noModule=""></script></head><body class="__variable_f367f3 font-sans antialiased"><div class="flex h-screen items-center justify-center bg-background"><div class="h-5 w-5 animate-spin rounded-full border-2 border-primary b
... [truncated]
```
</details>

### ✅ fe_login_page

- **Description:** Login page reachable
- **Method:** `GET`
- **Path:** `http://localhost:3000/login`
- **Status:** 200
- **Duration:** 9ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/1828-d8246b0f2d68f7a4.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/3434-c169ba738429bd16.js" async=""></script><script src="/_next/static/chunks/app/(auth)/login/page-dedb407ab7811e85.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script src="/_next/static/chunks/app/layout-8465f0fad9d9adf2.js" async=""></script><title>AZOA Sleek</title><meta name="description" content="Avatar NFT &amp; Blockchain Platform"/><meta name="next-size-adjust"/><script src="/_next/static/chunks/polyfills-c67a75d1b6f
... [truncated]
```
</details>

### ✅ fe_register_page

- **Description:** Register page reachable
- **Method:** `GET`
- **Path:** `http://localhost:3000/register`
- **Status:** 200
- **Duration:** 23ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/1828-d8246b0f2d68f7a4.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/3434-c169ba738429bd16.js" async=""></script><script src="/_next/static/chunks/app/(auth)/register/page-58cb2a587a4cc026.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script src="/_next/static/chunks/app/layout-8465f0fad9d9adf2.js" async=""></script><title>AZOA Sleek</title><meta name="description" content="Avatar NFT &amp; Blockchain Platform"/><meta name="next-size-adjust"/><script src="/_next/static/chunks/polyfills-c67a75d1
... [truncated]
```
</details>

### ✅ fe_overview

- **Description:** Overview page reachable (Next.js may redirect to /login when unauth)
- **Method:** `GET`
- **Path:** `http://localhost:3000/overview`
- **Status:** 200
- **Duration:** 24ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/4644-6d4f7d1c5c9f4644.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/198-1fe121df14094450.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/overview/page-f32516f97fe02c12.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/7447-84562ccc4a18b380.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/chunks/1014-d6951564f1c1dc35.js" async=""></script><script src="/_next/static/chunks/3434-c169ba738429bd16.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/layout-3651f506eecee4e3.js" async=""></script><script s
... [truncated]
```
</details>

### ✅ fe_avatars

- **Description:** Avatars page reachable
- **Method:** `GET`
- **Path:** `http://localhost:3000/avatars`
- **Status:** 200
- **Duration:** 20ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/1828-d8246b0f2d68f7a4.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/7447-84562ccc4a18b380.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/avatars/page-05792c072ec04e11.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/chunks/1014-d6951564f1c1dc35.js" async=""></script><script src="/_next/static/chunks/3434-c169ba738429bd16.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/layout-3651f506eecee4e3.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script sr
... [truncated]
```
</details>

### ✅ fe_wallets

- **Description:** Wallets page reachable
- **Method:** `GET`
- **Path:** `http://localhost:3000/wallets`
- **Status:** 200
- **Duration:** 19ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/1828-d8246b0f2d68f7a4.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/7447-84562ccc4a18b380.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/1939-213558b28bbf2165.js" async=""></script><script src="/_next/static/chunks/3981-daeda1ca856f0431.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/198-1fe121df14094450.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/wallets/page-f6fde0e1d6d5ed78.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/ch
... [truncated]
```
</details>

### ✅ fe_holons

- **Description:** Holons page reachable
- **Method:** `GET`
- **Path:** `http://localhost:3000/holons`
- **Status:** 200
- **Duration:** 21ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/1828-d8246b0f2d68f7a4.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/7447-84562ccc4a18b380.js" async=""></script><script src="/_next/static/chunks/1939-213558b28bbf2165.js" async=""></script><script src="/_next/static/chunks/3981-daeda1ca856f0431.js" async=""></script><script src="/_next/static/chunks/4175-1492e4c5141fbf95.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/198-1fe121df14094450.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/holons/page-c1b7efdb6c5aa200.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/ch
... [truncated]
```
</details>

### ✅ fe_api_keys

- **Description:** API keys management page reachable
- **Method:** `GET`
- **Path:** `http://localhost:3000/api-keys`
- **Status:** 200
- **Duration:** 12ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" crossorigin="" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js" crossorigin=""/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async="" crossorigin=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async="" crossorigin=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async="" crossorigin=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/1828-d8246b0f2d68f7a4.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/7447-84562ccc4a18b380.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/api-keys/page-248f716e4e061437.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/1585-d295f1eb847e5fb7.js" async=""></script><script src="/_next/static/chunks/1014-d6951564f1c1dc35.js" async=""></script><script src="/_next/static/chunks/3434-c169ba738429bd16.js" async=""></script><script src="/_next/static/chunks/app/(dashboard)/layout-3651f506eecee4e3.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script s
... [truncated]
```
</details>

### ✅ fe_404

- **Description:** Unknown route returns Next.js 404
- **Method:** `GET`
- **Path:** `http://localhost:3000/__does-not-exist__`
- **Status:** 404
- **Duration:** 28ms

<details>
<summary>Response body</summary>

```json
<!DOCTYPE html><html lang="en" class="dark"><head><meta charSet="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/><link rel="preload" href="/_next/static/media/e4af272ccee01ff0-s.p.woff2" as="font" crossorigin="" type="font/woff2"/><link rel="stylesheet" href="/_next/static/css/a182cc84ad5e553f.css" data-precedence="next"/><link rel="preload" as="script" fetchPriority="low" href="/_next/static/chunks/webpack-ce6d2ccf7dafb208.js"/><script src="/_next/static/chunks/fd9d1056-daf3eb0aa35d0696.js" async=""></script><script src="/_next/static/chunks/4938-a08e11f58b8b8107.js" async=""></script><script src="/_next/static/chunks/main-app-2f3800c6e4826db2.js" async=""></script><script src="/_next/static/chunks/7895-815176611b89f795.js" async=""></script><script src="/_next/static/chunks/6366-064e393ccb2b90e1.js" async=""></script><script src="/_next/static/chunks/3127-9135edd67742b3a1.js" async=""></script><script src="/_next/static/chunks/9901-314a93d35ab4ee85.js" async=""></script><script src="/_next/static/chunks/5037-e0640acd1698b3aa.js" async=""></script><script src="/_next/static/chunks/3371-56b2dde1af9a0b4e.js" async=""></script><script src="/_next/static/chunks/935-00694eae37c6bf8c.js" async=""></script><script src="/_next/static/chunks/3100-73771510b5b24bd0.js" async=""></script><script src="/_next/static/chunks/app/layout-8465f0fad9d9adf2.js" async=""></script><meta name="robots" content="noindex"/><title>404: This page could not be found.</title><title>AZOA Sleek</title><meta name="description" content="Avatar NFT &amp; Blockchain Platform"/><meta name="next-size-adjust"/><script src="/_next/static/chunks/polyfills-c67a75d1b6f99dc8.js" noModule=""></script></head><body class="__variable_f367f3 font-sans antialiased"><div style="font-family:system-ui,&quot;Segoe UI&quot;,Roboto,Helvetica,Arial,sans-serif,&quot;Apple Color Emoji&quot;,&quot;Segoe UI Emoji&quot;;height:100vh;text-align:center;display:flex;flex-direction:column;align-items:
... [truncated]
```
</details>

## 🗂️ HolonController_Malicious

- **Total:** 39 | **Passed:** 18 | **Failed:** 21 | **Skipped:** 0
- **Duration:** 904ms

### ✅ seed_target

- **Description:** Register avatar for malicious holon tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 228ms
- **Extracted:**
  - `malAvatar.id` = `ea04b3f4-85a8-4709-b39b-2b8b1edde3c6`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"ea04b3f4-85a8-4709-b39b-2b8b1edde3c6","username":"holonmal","email":"holonmal@mal.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:44.1311325Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_target

- **Description:** Login target avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 170ms
- **Extracted:**
  - `malAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJlYTA0YjNmNC04NWE4LTQ3MDktYjM5Yi0yYjhiMWVkZGUzYzYiLCJlbWFpbCI6ImhvbG9ubWFsQG1hbC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJob2xvbm1hbCIsImp0aSI6IjgyMDgwMzZmLWFjM2YtNGMwNC04ZWFmLWRlZTJiMmMwMmMzZCIsImV4cCI6MTc4MDk2NTk0NCwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0._2M6r8Z-AabUJZXVwhbQlyBwHjQ2ebhNscxTLsZ5o1Q`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJlYTA0YjNmNC04NWE4LTQ3MDktYjM5Yi0yYjhiMWVkZGUzYzYiLCJlbWFpbCI6ImhvbG9ubWFsQG1hbC5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJob2xvbm1hbCIsImp0aSI6IjgyMDgwMzZmLWFjM2YtNGMwNC04ZWFmLWRlZTJiMmMwMmMzZCIsImV4cCI6MTc4MDk2NTk0NCwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0._2M6r8Z-AabUJZXVwhbQlyBwHjQ2ebhNscxTLsZ5o1Q","detail":null}
```
</details>

### ❌ seed_holon

- **Description:** Create seed holon for attack tests
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 32ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sqli_holon_name

- **Description:** SQL injection in holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 26ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sqli_holon_description

- **Description:** SQL injection in holon description
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 26ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sqli_holon_provider

- **Description:** SQL injection in provider name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 27ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sqli_metadata_key

- **Description:** SQL injection in metadata key
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 33ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sqli_metadata_value

- **Description:** SQL injection in metadata value
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 54ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ sqli_chain_id

- **Description:** SQL injection in chain ID
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 29ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ xss_holon_name

- **Description:** XSS in holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 32ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ xss_holon_description

- **Description:** XSS in holon description
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 32ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ xss_metadata

- **Description:** XSS in metadata values
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 34ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ xss_interact

- **Description:** XSS via interact endpoint
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/interact`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ path_traversal_holon_get

- **Description:** Path traversal in holon ID
- **Method:** `GET`
- **Path:** `/api/holon/../../../etc/passwd`
- **Status:** 404
- **Duration:** 1ms

### ✅ invalid_guid_holon

- **Description:** Invalid GUID format
- **Method:** `GET`
- **Path:** `/api/holon/not-a-valid-guid`
- **Status:** 404
- **Duration:** 1ms

### ❌ empty_guid_holon

- **Description:** Empty GUID segment
- **Method:** `GET`
- **Path:** `/api/holon/`
- **Status:** 200
- **Duration:** 57ms
- **Error:** Expected status 404, got 200.

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ guid_with_null

- **Description:** GUID with null byte
- **Method:** `GET`
- **Path:** `/api/holon/550e8400-e29b-41d4-a716-446655440000%00`
- **Status:** 400
- **Duration:** 1ms
- **Error:** Expected status 404, got 400.

### ✅ negative_guid

- **Description:** Negative numbers in GUID
- **Method:** `GET`
- **Path:** `/api/holon/-50e8400-e29b-41d4-a716-446655440000`
- **Status:** 404
- **Duration:** 1ms

### ❌ oversized_holon_name

- **Description:** Extremely long holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 2ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Name":["The length of 'Name' must be 200 characters or fewer. You entered 520 characters."]},"traceId":"00-6f6ea14025bb6cd2f94f974a97b8f1bc-36b17f73cba30416-01"}
```
</details>

### ❌ oversized_metadata

- **Description:** Oversized metadata object
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 35ms
- **Error:** Expected status 2xx, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ✅ null_holon_name

- **Description:** Null holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Name":["The Name field is required.","'Name' must not be empty."]},"traceId":"00-886685fcac0b0de80d6f801c5359ef67-0b929ece3d3a00eb-01"}
```
</details>

### ✅ numeric_holon_name

- **Description:** Number as holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 5ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.name":["The JSON value could not be converted to System.String. Path: $.name | LineNumber: 0 | BytePositionInLine: 13."]},"traceId":"00-a9b4d080863d3d566aa82cf70fd057ab-6fd96c465ca55db5-01"}
```
</details>

### ✅ array_description

- **Description:** Array as description
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 3ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.description":["The JSON value could not be converted to System.String. Path: $.description | LineNumber: 0 | BytePositionInLine: 35."]},"traceId":"00-2c6fe3eb7306ee973c23bae485288cec-bc4c0a5f5f21b96c-01"}
```
</details>

### ✅ invalid_parent_id

- **Description:** Invalid parent holon ID format
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.parentHolonId":["The JSON value could not be converted to System.Nullable`1[System.Guid]. Path: $.parentHolonId | LineNumber: 0 | BytePositionInLine: 95."]},"traceId":"00-983a58a0778470c86fc9e0cb6b494b94-c362ac6f4ecc481d-01"}
```
</details>

### ✅ missing_provider

- **Description:** Missing required provider name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 3ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"ProviderName":["'Provider Name' must not be empty."]},"traceId":"00-4a1395ba55be16c167613a73cbce41a5-3251771eaa6aa90a-01"}
```
</details>

### ✅ empty_body

- **Description:** Empty body for holon create
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Name":["'Name' must not be empty."],"Description":["'Description' must not be empty."],"ProviderName":["'Provider Name' must not be empty."]},"traceId":"00-806fd959c667737226b8815c4398f185-caf7b7b07832cd47-01"}
```
</details>

### ❌ sqli_interact_metadata

- **Description:** SQLi via interact metadata
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/interact`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ xss_interact_metadata

- **Description:** XSS via interact metadata
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/interact`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ interact_invalid_parent

- **Description:** Set invalid parent via interact
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/interact`
- **Status:** 404
- **Duration:** 1ms

### ❌ interact_circular_parent

- **Description:** Set holon as its own parent
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/interact`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ interact_null_parent

- **Description:** Set null parent (should clear parent)
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/interact`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ✅ mint_negative_amount

- **Description:** Mint with negative amount
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms

### ✅ mint_zero_amount

- **Description:** Mint with zero amount
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms

### ✅ mint_invalid_wallet

- **Description:** Mint with non-existent wallet
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms

### ✅ mint_sqli_token_uri

- **Description:** SQL injection in token URI
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/mint`
- **Status:** 404
- **Duration:** 0ms

### ✅ exchange_invalid_target

- **Description:** Exchange with invalid target holon
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/exchange`
- **Status:** 404
- **Duration:** 1ms

### ✅ exchange_sqli_rate

- **Description:** SQL injection in exchange rate
- **Method:** `POST`
- **Path:** `/api/holon/{{targetHolon.id}}/exchange`
- **Status:** 404
- **Duration:** 1ms

### ❌ cleanup_target_holon

- **Description:** Delete target holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{targetHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ cleanup_avatar

- **Description:** Delete test avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/ea04b3f4-85a8-4709-b39b-2b8b1edde3c6`
- **Status:** 404
- **Duration:** 37ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ HolonController_QA

- **Total:** 35 | **Passed:** 10 | **Failed:** 25 | **Skipped:** 0
- **Duration:** 1790ms

### ✅ seed_avatar_a

- **Description:** Register avatar A for holon ownership tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 235ms
- **Extracted:**
  - `avatarA.id` = `e54a6d9d-8c78-485c-967f-3bbbce920510`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"e54a6d9d-8c78-485c-967f-3bbbce920510","username":"holonowner_a","email":"holona@qa.azoa","title":null,"firstName":"Owner","lastName":"Alpha","createdDate":"2026-06-08T00:45:44.8619577Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_avatar_a

- **Description:** Login avatar A
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 180ms
- **Extracted:**
  - `authA.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJlNTRhNmQ5ZC04Yzc4LTQ4NWMtOTY3Zi0zYmJiY2U5MjA1MTAiLCJlbWFpbCI6ImhvbG9uYUBxYS5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJob2xvbm93bmVyX2EiLCJqdGkiOiI4OGYzZjI0Ny00MzBiLTQ5NmQtYTkyNC1kNzY2MGMzN2E1ZGIiLCJleHAiOjE3ODA5NjU5NDUsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.T1YpWKuWao_c8Baleydo03upkcbxsqMnB97YtjjxmKE`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJlNTRhNmQ5ZC04Yzc4LTQ4NWMtOTY3Zi0zYmJiY2U5MjA1MTAiLCJlbWFpbCI6ImhvbG9uYUBxYS5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJob2xvbm93bmVyX2EiLCJqdGkiOiI4OGYzZjI0Ny00MzBiLTQ5NmQtYTkyNC1kNzY2MGMzN2E1ZGIiLCJleHAiOjE3ODA5NjU5NDUsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.T1YpWKuWao_c8Baleydo03upkcbxsqMnB97YtjjxmKE","detail":null}
```
</details>

### ✅ seed_avatar_b

- **Description:** Register avatar B for isolation tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 430ms
- **Extracted:**
  - `avatarB.id` = `182de96d-7460-4447-b8e9-04f48b6038a8`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"182de96d-7460-4447-b8e9-04f48b6038a8","username":"holonowner_b","email":"holonb@qa.azoa","title":null,"firstName":"Owner","lastName":"Beta","createdDate":"2026-06-08T00:45:45.4605887Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_avatar_b

- **Description:** Login avatar B
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 215ms
- **Extracted:**
  - `authB.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxODJkZTk2ZC03NDYwLTQ0NDctYjhlOS0wNGY0OGI2MDM4YTgiLCJlbWFpbCI6ImhvbG9uYkBxYS5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJob2xvbm93bmVyX2IiLCJqdGkiOiJjYjJkZGI5ZS1kOTljLTRlOGQtYWU0Yy03YjNiYzJhNGMxZWUiLCJleHAiOjE3ODA5NjU5NDUsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.Lpk7yrDOUWT3SQNL0rs42RQ91sq2d4oSbaFRQjQHXfU`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxODJkZTk2ZC03NDYwLTQ0NDctYjhlOS0wNGY0OGI2MDM4YTgiLCJlbWFpbCI6ImhvbG9uYkBxYS5vYXNpcyIsImh0dHA6Ly9zY2hlbWFzLnhtbHNvYXAub3JnL3dzLzIwMDUvMDUvaWRlbnRpdHkvY2xhaW1zL25hbWUiOiJob2xvbm93bmVyX2IiLCJqdGkiOiJjYjJkZGI5ZS1kOTljLTRlOGQtYWU0Yy03YjNiYzJhNGMxZWUiLCJleHAiOjE3ODA5NjU5NDUsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.Lpk7yrDOUWT3SQNL0rs42RQ91sq2d4oSbaFRQjQHXfU","detail":null}
```
</details>

### ❌ create_root_holon

- **Description:** Create root holon for avatar A
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 75ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ create_child_holon

- **Description:** Create child holon with parent reference
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 3ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.parentHolonId":["The JSON value could not be converted to System.Nullable`1[System.Guid]. Path: $.parentHolonId | LineNumber: 0 | BytePositionInLine: 111."]},"traceId":"00-f376355b3aa4e08985a7c87b80f449f5-545c3c5916c19108-01"}
```
</details>

### ❌ create_peer_holon

- **Description:** Create holon that will be a peer
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 71ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ create_chain_holon

- **Description:** Create holon on Algorand chain
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 104ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ get_root_holon

- **Description:** Get root holon by ID
- **Method:** `GET`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 26ms
- **Error:** Expected status 200, got 404.

### ❌ verify_avatar_id_set

- **Description:** Verify holon has correct avatarId from JWT
- **Method:** `GET`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ✅ query_by_name

- **Description:** Query holons by name filter
- **Method:** `GET`
- **Path:** `/api/holon?name=RootHolon`
- **Status:** 200
- **Duration:** 86ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ✅ query_no_filter

- **Description:** Query all holons (no filter)
- **Method:** `GET`
- **Path:** `/api/holon`
- **Status:** 200
- **Duration:** 47ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ✅ query_nonexistent

- **Description:** Query with name that doesn't match
- **Method:** `GET`
- **Path:** `/api/holon?name=NonExistentHolonXYZ`
- **Status:** 200
- **Duration:** 68ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ update_name

- **Description:** Update holon name only
- **Method:** `PUT`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ update_metadata

- **Description:** Update holon metadata
- **Method:** `PUT`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ update_chain

- **Description:** Update chain and asset type
- **Method:** `PUT`
- **Path:** `/api/holon/{{chainHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ update_nonexistent

- **Description:** Update non-existent holon
- **Method:** `PUT`
- **Path:** `/api/holon/00000000-0000-0000-0000-000000000000`
- **Status:** 400
- **Duration:** 43ms
- **Error:** Expected status 404, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Holon not found.","result":null,"detail":null}
```
</details>

### ❌ interact_add_peers

- **Description:** Add peer holons via interact
- **Method:** `POST`
- **Path:** `/api/holon/{{rootHolon.id}}/interact`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ interact_change_parent

- **Description:** Change parent holon via interact
- **Method:** `POST`
- **Path:** `/api/holon/{{childHolon.id}}/interact`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ interact_remove_metadata

- **Description:** Remove metadata keys via interact
- **Method:** `POST`
- **Path:** `/api/holon/{{rootHolon.id}}/interact`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ interact_remove_peers

- **Description:** Remove peer holons via interact
- **Method:** `POST`
- **Path:** `/api/holon/{{rootHolon.id}}/interact`
- **Status:** 404
- **Duration:** 3ms
- **Error:** Expected status 200, got 404.

### ❌ isolation_avatar_b_get_a_holon

- **Description:** Avatar B tries to get avatar A's holon
- **Method:** `GET`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 200, got 404.

### ❌ isolation_avatar_b_update_a_holon

- **Description:** Avatar B tries to update avatar A's holon
- **Method:** `PUT`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ isolation_avatar_b_delete_a_holon

- **Description:** Avatar B tries to delete avatar A's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ isolation_unauth_create

- **Description:** Create holon without auth
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms

### ❌ create_b_holon

- **Description:** Create holon for avatar B
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 26ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ delete_child_holon

- **Description:** Delete child holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{childHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ delete_peer_holon

- **Description:** Delete peer holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{peerHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ delete_chain_holon

- **Description:** Delete chain holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{chainHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ delete_root_holon

- **Description:** Delete root holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ delete_b_holon

- **Description:** Delete avatar B's holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{bHolon.id}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ verify_deleted_holon

- **Description:** Verify deleted holon returns 404
- **Method:** `GET`
- **Path:** `/api/holon/{{rootHolon.id}}`
- **Status:** 404
- **Duration:** 1ms

### ✅ delete_nonexistent_holon

- **Description:** Delete non-existent holon
- **Method:** `DELETE`
- **Path:** `/api/holon/00000000-0000-0000-0000-000000000000`
- **Status:** 404
- **Duration:** 60ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Holon not found.","result":false,"detail":null}
```
</details>

### ❌ cleanup_avatar_a

- **Description:** Delete avatar A
- **Method:** `DELETE`
- **Path:** `/api/avatar/e54a6d9d-8c78-485c-967f-3bbbce920510`
- **Status:** 404
- **Duration:** 32ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ cleanup_avatar_b

- **Description:** Delete avatar B
- **Method:** `DELETE`
- **Path:** `/api/avatar/182de96d-7460-4447-b8e9-04f48b6038a8`
- **Status:** 404
- **Duration:** 43ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ HolonController

- **Total:** 9 | **Passed:** 3 | **Failed:** 6 | **Skipped:** 0
- **Duration:** 743ms

### ✅ seed_avatar

- **Description:** Register a temporary avatar for holon tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 230ms
- **Extracted:**
  - `havatar.avatarId` = `b5422d85-1cda-413e-a1ea-4c18c42d8bc1`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"b5422d85-1cda-413e-a1ea-4c18c42d8bc1","username":"holontest","email":"holon@test.azoa","title":null,"firstName":"Holon","lastName":"Tester","createdDate":"2026-06-08T00:45:45.039415Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ login_seed

- **Description:** Login as temp avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 195ms
- **Extracted:**
  - `hauth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJiNTQyMmQ4NS0xY2RhLTQxM2UtYTFlYS00YzE4YzQyZDhiYzEiLCJlbWFpbCI6ImhvbG9uQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiaG9sb250ZXN0IiwianRpIjoiMThhMTg3MzQtNGU5Ni00MGMwLTk4MGMtMjRiOTEwZTY1NzM5IiwiZXhwIjoxNzgwOTY1OTQ1LCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.3cE6dL3mRSQ6zxTlOb98A4QZUAr7dxEG38zF5KaUNaE`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJiNTQyMmQ4NS0xY2RhLTQxM2UtYTFlYS00YzE4YzQyZDhiYzEiLCJlbWFpbCI6ImhvbG9uQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiaG9sb250ZXN0IiwianRpIjoiMThhMTg3MzQtNGU5Ni00MGMwLTk4MGMtMjRiOTEwZTY1NzM5IiwiZXhwIjoxNzgwOTY1OTQ1LCJpc3MiOiJPQVNJUy5XZWJBUEkiLCJhdWQiOiJPQVNJUy5DbGllbnQifQ.3cE6dL3mRSQ6zxTlOb98A4QZUAr7dxEG38zF5KaUNaE","detail":null}
```
</details>

### ❌ create_holon

- **Description:** Create a new holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 400
- **Duration:** 54ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: (no detail)","result":null,"detail":{"type":"Azoa.SurrealDb.Client.SurrealStatementException","message":"SurrealDB statement 1/1 returned ERR: (no detail)","stackTrace":"   at Azoa.SurrealDb.Client.SurrealResponse.EnsureAllOk() in /src/packages/Azoa.SurrealDb.Client/SurrealResponse.cs:line 98\n   at AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore.UpsertAsync(IHolon holon, CancellationToken ct) in /src/Providers/Stores/Surreal/SurrealHolonStore.cs:line 172","inner":null}}
```
</details>

### ❌ get_holon

- **Description:** Get the created holon
- **Method:** `GET`
- **Path:** `/api/holon/{{holon1.holonId}}`
- **Status:** 404
- **Duration:** 29ms
- **Error:** Expected status 200, got 404.

### ✅ query_holons

- **Description:** Query holons with filter
- **Method:** `GET`
- **Path:** `/api/holon?name=LiveHolon`
- **Status:** 200
- **Duration:** 91ms

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Success","result":[],"detail":null}
```
</details>

### ❌ update_holon

- **Description:** Update holon metadata
- **Method:** `PUT`
- **Path:** `/api/holon/{{holon1.holonId}}`
- **Status:** 404
- **Duration:** 32ms
- **Error:** Expected status 200, got 404.

### ❌ interact_holon

- **Description:** Interact with holon (set metadata)
- **Method:** `POST`
- **Path:** `/api/holon/{{holon1.holonId}}/interact`
- **Status:** 404
- **Duration:** 33ms
- **Error:** Expected status 200, got 404.

### ❌ delete_holon

- **Description:** Delete the holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{holon1.holonId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ cleanup_avatar

- **Description:** Clean up the temporary avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/b5422d85-1cda-413e-a1ea-4c18c42d8bc1`
- **Status:** 404
- **Duration:** 74ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

## 🗂️ MaliciousPayloads

- **Total:** 100 | **Passed:** 24 | **Failed:** 76 | **Skipped:** 0
- **Duration:** 1538ms

### ✅ mal_avatar_seed

- **Description:** Seed avatar for malicious tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 301ms
- **Extracted:**
  - `malAvatar.avatarId` = `7124964a-2fb0-4fc6-b963-73c49d33fe62`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"7124964a-2fb0-4fc6-b963-73c49d33fe62","username":"maltester","email":"mal@test.azoa","title":null,"firstName":"Mal","lastName":"icious","createdDate":"2026-06-08T00:45:45.2411061Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ mal_avatar_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 238ms
- **Extracted:**
  - `malAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI3MTI0OTY0YS0yZmIwLTRmYzYtYjk2My03M2M0OWQzM2ZlNjIiLCJlbWFpbCI6Im1hbEB0ZXN0Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6Im1hbHRlc3RlciIsImp0aSI6ImQ5YzY3YThlLWZiODYtNGY2OC1iMzI4LTI2MWUzOWYwMjdiMiIsImV4cCI6MTc4MDk2NTk0NSwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.uwAA92KJyXcyKYaF-E_IMZ8B299QjumRkJXfLex_U-8`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI3MTI0OTY0YS0yZmIwLTRmYzYtYjk2My03M2M0OWQzM2ZlNjIiLCJlbWFpbCI6Im1hbEB0ZXN0Lm9hc2lzIiwiaHR0cDovL3NjaGVtYXMueG1sc29hcC5vcmcvd3MvMjAwNS8wNS9pZGVudGl0eS9jbGFpbXMvbmFtZSI6Im1hbHRlc3RlciIsImp0aSI6ImQ5YzY3YThlLWZiODYtNGY2OC1iMzI4LTI2MWUzOWYwMjdiMiIsImV4cCI6MTc4MDk2NTk0NSwiaXNzIjoiT0FTSVMuV2ViQVBJIiwiYXVkIjoiT0FTSVMuQ2xpZW50In0.uwAA92KJyXcyKYaF-E_IMZ8B299QjumRkJXfLex_U-8","detail":null}
```
</details>

### ✅ mal_sql_username

- **Description:** SQLi in username field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 36ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-20ab609fe4cdd6897f8b839b3ce30d24-8560d0017a646bc4-01"}
```
</details>

### ❌ mal_sql_email

- **Description:** SQLi in email field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 245ms
- **Error:** Expected status 400, got 200.

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"6c54593d-0c30-47df-988b-5a47070580e0","username":"sqliuser","email":"' OR '1'='1' --@test.azoa","title":null,"firstName":null,"lastName":null,"createdDate":"2026-06-08T00:45:45.780859Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ❌ mal_sql_login_email

- **Description:** SQLi in login email
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 400
- **Duration:** 6ms
- **Error:** Expected status 401, got 400.

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' is not a valid email address."]},"traceId":"00-27842a6f93ab2ac312f8475c0bea93c8-f6d9bb0ec2c7e6bb-01"}
```
</details>

### ✅ mal_sql_login_pass

- **Description:** SQLi in login password
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 401
- **Duration:** 204ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Invalid credentials.","result":null,"detail":null}
```
</details>

### ✅ mal_xss_username

- **Description:** XSS in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-06b76a2f7586f2a56d0663abd0c10a0d-0e767df5e8c29558-01"}
```
</details>

### ✅ mal_xss_firstname

- **Description:** XSS in firstName
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 38ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"This username is already taken.","result":null,"detail":null}
```
</details>

### ✅ mal_xss_title

- **Description:** XSS in title field
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 93ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"This username is already taken.","result":null,"detail":null}
```
</details>

### ✅ mal_oversized_username

- **Description:** Extremely long username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 4ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["'Username' must be between 3 and 50 characters. You entered 512 characters."]},"traceId":"00-a5d9a626d19b1589aacd419547a4f1bd-3d50680e2aa064e9-01"}
```
</details>

### ✅ mal_oversized_email

- **Description:** Extremely long email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 34ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"This username is already taken.","result":null,"detail":null}
```
</details>

### ✅ mal_special_chars_username

- **Description:** Username with special chars
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 3ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-4992d46b76b5896f7460a06cbb2fd0f2-2af63efa21e08c1c-01"}
```
</details>

### ✅ mal_unicode_username

- **Description:** Username with unicode/emojis
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-d6dd40bdbf196612b62e5eb5a7e0e70e-752dad3d85af429f-01"}
```
</details>

### ✅ mal_null_bytes

- **Description:** Username with null bytes
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["Username must contain only letters, numbers, and underscores."]},"traceId":"00-3267c8eda8290c49498cb27a46be6fe9-d774f3f5718428ae-01"}
```
</details>

### ✅ mal_type_email_as_object

- **Description:** Email field sent as object
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 26ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.email":["The JSON value could not be converted to System.String. Path: $.email | LineNumber: 0 | BytePositionInLine: 32."]},"traceId":"00-c09e8fe58169a2caf238861094368b2f-f7b7a42f366696f3-01"}
```
</details>

### ✅ mal_type_password_as_array

- **Description:** Password field sent as array
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.password":["The JSON value could not be converted to System.String. Path: $.password | LineNumber: 0 | BytePositionInLine: 63."]},"traceId":"00-11f53952ab9141c8061e69281de830e6-249e532d285b34b4-01"}
```
</details>

### ✅ mal_type_username_as_number

- **Description:** Username field sent as number
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"model":["The model field is required."],"$.username":["The JSON value could not be converted to System.String. Path: $.username | LineNumber: 0 | BytePositionInLine: 17."]},"traceId":"00-d541b0ffbf6badcd9cd0dac5d1a68576-59b6a5f31ba289a4-01"}
```
</details>

### ✅ mal_null_username

- **Description:** Null username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["The Username field is required.","'Username' must not be empty."]},"traceId":"00-6a69d8072296b3cf39734f00f6618337-d09d572a18193fd6-01"}
```
</details>

### ✅ mal_null_email

- **Description:** Null email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 19ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["The Email field is required.","'Email' must not be empty."]},"traceId":"00-96707bcc2d8a640e0acab78a3e4f4842-6dc15312cf5ee50c-01"}
```
</details>

### ❌ mal_null_password

- **Description:** Null password
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 3ms
- **Error:** Expected status 400, got 429.

### ✅ mal_auth_no_bearer

- **Description:** Auth header without Bearer prefix
- **Method:** `GET`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 401
- **Duration:** 1ms

### ✅ mal_auth_expired_format

- **Description:** Auth with expired-looking JWT structure
- **Method:** `GET`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 401
- **Duration:** 2ms

### ✅ mal_auth_empty_token

- **Description:** Auth with empty bearer token
- **Method:** `GET`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 401
- **Duration:** 1ms

### ✅ mal_path_traversal

- **Description:** Path traversal in avatar ID
- **Method:** `GET`
- **Path:** `/api/avatar/../avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 404
- **Duration:** 34ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ mal_guid_zero

- **Description:** Get avatar with zeroed GUID
- **Method:** `GET`
- **Path:** `/api/avatar/00000000-0000-0000-0000-000000000000`
- **Status:** 404
- **Duration:** 30ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ mal_update_xss

- **Description:** Update avatar with XSS payload
- **Method:** `PUT`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 400
- **Duration:** 31ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ mal_update_sqli

- **Description:** Update avatar with SQLi payload
- **Method:** `PUT`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 400
- **Duration:** 31ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ mal_wallet_xss_address

- **Description:** Add wallet with XSS in address
- **Method:** `POST`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 400, got 404.

### ❌ mal_wallet_sqli_label

- **Description:** Add wallet with SQLi in label
- **Method:** `POST`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 400, got 404.

### ❌ mal_avatar_cleanup

- **Description:** Delete malicious test avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62`
- **Status:** 404
- **Duration:** 42ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ mal_holon_seed

- **Description:** Seed avatar for malicious holon tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ mal_holon_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ mal_holon_sql_name

- **Description:** SQLi in holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_sql_desc

- **Description:** SQLi in holon description
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_xss_name

- **Description:** XSS in holon name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_xss_metadata

- **Description:** XSS in holon metadata values
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_oversized_name

- **Description:** Holon name exceeding reasonable length
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_oversized_metadata

- **Description:** Holon with oversized metadata object
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_type_name_object

- **Description:** Holon name as object
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_type_peer_ids_string

- **Description:** peerHolonIds as string instead of array
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_type_metadata_array

- **Description:** Metadata as array instead of object
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_null_name

- **Description:** Holon with null name
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_null_provider

- **Description:** Holon with null providerName
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_holon_interact_sqli

- **Description:** Interact with SQLi in metadata
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/interact`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ mal_holon_interact_xss

- **Description:** Interact with XSS in metadata
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/interact`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ mal_holon_cleanup_avatar

- **Description:** Delete malicious holon avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{malHAvatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ mal_star_seed

- **Description:** Seed avatar for malicious STAR tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ mal_star_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ mal_star_sql_name

- **Description:** SQLi in STAR ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_star_xss_desc

- **Description:** XSS in STAR ODK description
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 400, got 401.

### ❌ mal_star_xss_pubkey

- **Description:** XSS in STAR ODK publicKey
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_star_type_avatarId_object

- **Description:** avatarId as object
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_star_type_name_array

- **Description:** name as array
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_star_gen_xss_config

- **Description:** Generate dApp with XSS in config values
- **Method:** `POST`
- **Path:** `/api/starodk/99999999-9999-9999-9999-999999999999/generate`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 404, got 401.

### ❌ mal_star_cleanup_avatar

- **Description:** Delete malicious STAR avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{malSAvatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ mal_bc_seed

- **Description:** Seed avatar for malicious blockchain tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ mal_bc_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ mal_bc_by_avatar_sqli

- **Description:** SQLi in avatarId path param for blockchain ops
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/' OR '1'='1`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 404, got 429.

### ❌ mal_bc_get_sqli

- **Description:** SQLi in blockchain operation ID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/' UNION SELECT * FROM BlockchainOperations --`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 404, got 429.

### ❌ mal_nosql_username

- **Description:** NoSQLi in username ($ne operator)
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_nosql_email

- **Description:** NoSQLi in email ($gt operator)
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_nosql_login

- **Description:** NoSQLi in login body
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_cmd_username

- **Description:** Command injection in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_cmd_email

- **Description:** Command injection in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_cmd_backtick

- **Description:** Backtick command injection
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_ldap_username

- **Description:** LDAP injection in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 400, got 429.

### ❌ mal_ldap_email

- **Description:** LDAP injection in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 400, got 429.

### ❌ mal_mass_assignment

- **Description:** Register with extra unauthorized fields
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_mass_holon

- **Description:** Create holon with extra fields
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_deep_nest

- **Description:** Register with deeply nested body
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_json_bomb_meta

- **Description:** Holon with large flat metadata
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 2ms
- **Error:** Expected status 400, got 401.

### ❌ mal_ssrf_tokenuri

- **Description:** Mint with SSRF tokenUri
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/mint`
- **Status:** 401
- **Duration:** 2ms
- **Error:** Expected status 404, got 401.

### ❌ mal_ssrf_localhost

- **Description:** Mint with localhost tokenUri
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/mint`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ mal_ssrf_file

- **Description:** Mint with file:// tokenUri
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/mint`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ mal_crlf_email

- **Description:** CRLF injection in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_header_inject

- **Description:** Header injection via title
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_fmt_string_user

- **Description:** Format string in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_fmt_string_email

- **Description:** Format string in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_log_inject

- **Description:** Newline log injection in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_proto_pollute

- **Description:** Prototype pollution in body
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_proto_constructor

- **Description:** Constructor pollution in body
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_negative_amount

- **Description:** Mint with negative amount
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/mint`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 404, got 401.

### ❌ mal_float_amount

- **Description:** Mint with float amount
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/mint`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ mal_max_int

- **Description:** Mint with max int amount
- **Method:** `POST`
- **Path:** `/api/holon/88888888-8888-8888-8888-888888888888/mint`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ mal_homograph_email

- **Description:** Homograph email using unicode
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_rtl_email

- **Description:** RTL override in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_zwj_username

- **Description:** Username with zero-width joiners
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_path_wallet

- **Description:** Path traversal in wallet address
- **Method:** `POST`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 400, got 404.

### ❌ mal_path_chaintype

- **Description:** Path traversal in chainType
- **Method:** `POST`
- **Path:** `/api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 400, got 404.

### ❌ mal_template_username

- **Description:** Template injection in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_template_email

- **Description:** Template injection in email
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_template_jinja

- **Description:** Jinja-like template injection
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_xml_username

- **Description:** XML entity in username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_xml_json_body

- **Description:** Send XML instead of JSON
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_array_body_register

- **Description:** Send array instead of object for register
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ mal_array_body_login

- **Description:** Send array instead of object for login
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_string_body_holon

- **Description:** Send string instead of object for holon create
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ mal_bool_username

- **Description:** Boolean as username
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_bool_password

- **Description:** Boolean as password
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ mal_bc_cleanup_avatar

- **Description:** Delete malicious blockchain avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{malBAvatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

## 🗂️ QA-EdgeCases

- **Total:** 99 | **Passed:** 13 | **Failed:** 86 | **Skipped:** 0
- **Duration:** 1182ms

### ✅ qa_avatar_seed

- **Description:** Seed avatar for QA avatar tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 200
- **Duration:** 246ms
- **Extracted:**
  - `qaAvatar.avatarId` = `dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Saved.","result":{"id":"dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4","username":"qaavatar","email":"qaavatar@test.azoa","title":"QA","firstName":"Edge","lastName":"Case","createdDate":"2026-06-08T00:45:45.785985Z","lastBeamedInDate":null,"isActive":true,"isVerified":false,"karma":0,"level":1},"detail":null}
```
</details>

### ✅ qa_avatar_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 200
- **Duration:** 244ms
- **Extracted:**
  - `qaAuth.token` = `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkY2M3YzA4ZC0yYTBkLTQ3YTEtOWUzZC1lMWRhOGYyOTUwYTQiLCJlbWFpbCI6InFhYXZhdGFyQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoicWFhdmF0YXIiLCJqdGkiOiJlMjk3ZTFjYi00YzYyLTRlZjQtYjRmMi02YjEzMzIzZTcyNDgiLCJleHAiOjE3ODA5NjU5NDYsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.pwgQ6eXpZZEI071dNSQyAF7Dw6_KpaU9POhe1s90m8A`

<details>
<summary>Response body</summary>

```json
{"isError":false,"message":"Login successful.","result":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJkY2M3YzA4ZC0yYTBkLTQ3YTEtOWUzZC1lMWRhOGYyOTUwYTQiLCJlbWFpbCI6InFhYXZhdGFyQHRlc3Qub2FzaXMiLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoicWFhdmF0YXIiLCJqdGkiOiJlMjk3ZTFjYi00YzYyLTRlZjQtYjRmMi02YjEzMzIzZTcyNDgiLCJleHAiOjE3ODA5NjU5NDYsImlzcyI6Ik9BU0lTLldlYkFQSSIsImF1ZCI6Ik9BU0lTLkNsaWVudCJ9.pwgQ6eXpZZEI071dNSQyAF7Dw6_KpaU9POhe1s90m8A","detail":null}
```
</details>

### ✅ qa_register_short_password

- **Description:** Register with short password should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 24ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Password":["The length of 'Password' must be at least 8 characters. You entered 2 characters.","Password must contain an uppercase letter.","Password must contain a lowercase letter."]},"traceId":"00-250c9caf42d95248a118623e9e597f90-48f6652a84158f82-01"}
```
</details>

### ✅ qa_register_no_username

- **Description:** Register without username should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 43ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Username":["'Username' must not be empty.","'Username' must be between 3 and 50 characters. You entered 0 characters.","Username must contain only letters, numbers, and underscores."]},"traceId":"00-c86a39146094f560a76845f8bb12ef89-49fb4e2807d6c6fe-01"}
```
</details>

### ✅ qa_register_no_email

- **Description:** Register without email should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 1ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' must not be empty.","'Email' is not a valid email address."]},"traceId":"00-6959f5b56fe8b22a0f3f3ac6af6a8734-dd6acf8abaad725f-01"}
```
</details>

### ✅ qa_register_no_password

- **Description:** Register without password should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 36ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Password":["'Password' must not be empty.","The length of 'Password' must be at least 8 characters. You entered 0 characters.","Password must contain an uppercase letter.","Password must contain a lowercase letter.","Password must contain a digit."]},"traceId":"00-7325e79751003c8582d41e46139a6315-14f32de5690a2a37-01"}
```
</details>

### ✅ qa_register_bad_email

- **Description:** Register with malformed email should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 400
- **Duration:** 2ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' is not a valid email address."]},"traceId":"00-589db181af2066135e8a769bae524588-cb02d4deb7fc1c8f-01"}
```
</details>

### ✅ qa_login_empty

- **Description:** Login with empty body should fail
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 400
- **Duration:** 1ms

<details>
<summary>Response body</summary>

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"Email":["'Email' must not be empty.","'Email' is not a valid email address."],"Password":["'Password' must not be empty."]},"traceId":"00-8d805757a5c9f79990865c219f536ab6-9e59c31e9dccaf6a-01"}
```
</details>

### ✅ qa_login_wrong_pass

- **Description:** Login with wrong password should 401
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 401
- **Duration:** 170ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Invalid credentials.","result":null,"detail":null}
```
</details>

### ❌ qa_login_missing_email

- **Description:** Login with non-existent email should 401
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 401, got 429.

### ✅ qa_get_random_guid

- **Description:** Get avatar with random GUID should 404
- **Method:** `GET`
- **Path:** `/api/avatar/11111111-1111-1111-1111-111111111111`
- **Status:** 404
- **Duration:** 47ms

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ qa_update_empty

- **Description:** Update avatar with empty body
- **Method:** `PUT`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 400
- **Duration:** 34ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ qa_update_single_field

- **Description:** Update only firstName
- **Method:** `PUT`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 400
- **Duration:** 32ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ✅ qa_get_no_auth

- **Description:** Get avatar without auth token should 401
- **Method:** `GET`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 401
- **Duration:** 0ms

### ✅ qa_get_bad_auth

- **Description:** Get avatar with malformed bearer token should 401
- **Method:** `GET`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 401
- **Duration:** 1ms

### ❌ qa_wallet_add_missing_chain

- **Description:** Add wallet without chainType should fail
- **Method:** `POST`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 400, got 404.

### ❌ qa_wallet_add_missing_address

- **Description:** Add wallet without address should fail
- **Method:** `POST`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets`
- **Status:** 404
- **Duration:** 2ms
- **Error:** Expected status 400, got 404.

### ❌ qa_wallet_add_valid

- **Description:** Add valid wallet
- **Method:** `POST`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ✅ qa_wallet_remove_random

- **Description:** Remove wallet with random GUID should 404
- **Method:** `DELETE`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets/22222222-2222-2222-2222-222222222222`
- **Status:** 404
- **Duration:** 1ms

### ❌ qa_wallet_remove_valid

- **Description:** Remove valid wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets/{{qaWallet.walletId}}`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ qa_holon_seed

- **Description:** Seed avatar for QA holon tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_empty_name

- **Description:** Create holon with empty name should fail
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ qa_holon_no_provider

- **Description:** Create holon without providerName should fail
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ qa_holon_empty_desc

- **Description:** Create holon with empty description (allowed)
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_rich_create

- **Description:** Create holon with metadata and peerHolonIds
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_get_random

- **Description:** Get holon with random GUID should 404
- **Method:** `GET`
- **Path:** `/api/holon/33333333-3333-3333-3333-333333333333`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ qa_holon_query_all

- **Description:** Query holons with no filters
- **Method:** `GET`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_query_name

- **Description:** Query holons by name filter
- **Method:** `GET`
- **Path:** `/api/holon?name=RichHolon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_query_nomatch

- **Description:** Query holons with non-matching name returns empty
- **Method:** `GET`
- **Path:** `/api/holon?name=NonExistentHolonXYZ`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_update_empty

- **Description:** Update holon with empty body
- **Method:** `PUT`
- **Path:** `/api/holon/{{qaHolon2.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_update_metadata

- **Description:** Update holon metadata only
- **Method:** `PUT`
- **Path:** `/api/holon/{{qaHolon2.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_interact_empty

- **Description:** Interact with holon using empty request
- **Method:** `POST`
- **Path:** `/api/holon/{{qaHolon2.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_interact_meta

- **Description:** Interact setting metadata keys
- **Method:** `POST`
- **Path:** `/api/holon/{{qaHolon2.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_delete

- **Description:** Delete holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{qaHolon1.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_get_deleted

- **Description:** Get deleted holon should 404
- **Method:** `GET`
- **Path:** `/api/holon/{{qaHolon1.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 404, got 429.

### ❌ qa_holon_cleanup_avatar

- **Description:** Delete QA holon avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{qaHAvatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star_seed

- **Description:** Seed avatar for QA STAR tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star_empty_name

- **Description:** Create STAR ODK with empty name should fail
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 400, got 401.

### ❌ qa_star_minimal

- **Description:** Create STAR ODK with minimal fields
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_star_no_avatar

- **Description:** Create STAR ODK without avatarId
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_star_get_random

- **Description:** Get STAR ODK with random GUID should 404
- **Method:** `GET`
- **Path:** `/api/starodk/44444444-4444-4444-4444-444444444444`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ qa_star_gen_empty_chain

- **Description:** Generate dApp with empty targetChain should fail
- **Method:** `POST`
- **Path:** `/api/starodk/{{qaOdk1.odkId}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ qa_star_deploy_random

- **Description:** Deploy random ODK should 404
- **Method:** `POST`
- **Path:** `/api/starodk/55555555-5555-5555-5555-555555555555/deploy`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ qa_star_delete

- **Description:** Delete ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{qaOdk2.odkId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star_get_deleted

- **Description:** Get deleted ODK should 404
- **Method:** `GET`
- **Path:** `/api/starodk/{{qaOdk2.odkId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 404, got 429.

### ❌ qa_star_cleanup_avatar

- **Description:** Delete QA STAR avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{qaSAvatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc_seed

- **Description:** Seed avatar for QA blockchain tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc_login

- **Description:** Login seeded avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc_get_random

- **Description:** Get blockchain op with random GUID should 404
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/66666666-6666-6666-6666-666666666666`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ qa_bc_by_avatar_random

- **Description:** Get operations by random avatar (empty list)
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/77777777-7777-7777-7777-777777777777`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_bc_by_avatar_empty

- **Description:** Get operations for valid avatar with no ops
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/avatar/{{qaBAvatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_register_dup_username

- **Description:** Register with duplicate username should fail
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 400, got 429.

### ❌ qa_login_email_case

- **Description:** Login with uppercase email should still work
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 401, got 429.

### ❌ qa_update_isactive_false

- **Description:** Update avatar IsActive to false
- **Method:** `PUT`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 400
- **Duration:** 30ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ qa_update_isactive_true

- **Description:** Update avatar IsActive back to true
- **Method:** `PUT`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 400
- **Duration:** 32ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ qa_update_empty_string

- **Description:** Update avatar firstName to empty string
- **Method:** `PUT`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 400
- **Duration:** 35ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ qa_update_long_name

- **Description:** Update avatar firstName with 100 chars
- **Method:** `PUT`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 400
- **Duration:** 41ms
- **Error:** Expected status 200, got 400.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":null,"detail":null}
```
</details>

### ❌ qa_wallet_default_true

- **Description:** Add default wallet
- **Method:** `POST`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets`
- **Status:** 404
- **Duration:** 1ms
- **Error:** Expected status 200, got 404.

### ❌ qa_wallet_default_false

- **Description:** Add non-default wallet
- **Method:** `POST`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ qa_wallet_cleanup_default

- **Description:** Remove default wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets/{{qaWallet2.walletId}}`
- **Status:** 404
- **Duration:** 0ms
- **Error:** Expected status 200, got 404.

### ❌ qa_avatar_cleanup

- **Description:** Delete QA avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4`
- **Status:** 404
- **Duration:** 60ms
- **Error:** Expected status 200, got 404.

<details>
<summary>Response body</summary>

```json
{"isError":true,"message":"Avatar not found.","result":false,"detail":null}
```
</details>

### ❌ qa_holon2_seed

- **Description:** Seed avatar for extended holon QA
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon2_login

- **Description:** Login extended holon avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_create_inactive

- **Description:** Create inactive holon
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_activate

- **Description:** Activate holon via update
- **Method:** `PUT`
- **Path:** `/api/holon/{{qaHolonInactive.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_deactivate

- **Description:** Deactivate holon via update
- **Method:** `PUT`
- **Path:** `/api/holon/{{qaHolonInactive.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_peer_create

- **Description:** Create holon with peerHolonIds
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_query_multi

- **Description:** Query holons with name and providerName
- **Method:** `GET`
- **Path:** `/api/holon?name=PeerHolon&providerName=InMemory`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_query_provider_only

- **Description:** Query holons with providerName only
- **Method:** `GET`
- **Path:** `/api/holon?providerName=InMemory`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_query_chainid

- **Description:** Query holons with non-matching chainId
- **Method:** `GET`
- **Path:** `/api/holon?chainId=nonexistent`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_holon_update_peers

- **Description:** Update holon peerHolonIds to empty
- **Method:** `PUT`
- **Path:** `/api/holon/{{qaHolonPeer.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_interact_peers

- **Description:** Interact adding peer back
- **Method:** `POST`
- **Path:** `/api/holon/{{qaHolonPeer.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_interact_remove_peers

- **Description:** Interact removing peer
- **Method:** `POST`
- **Path:** `/api/holon/{{qaHolonPeer.holonId}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_interact_parent

- **Description:** Interact setting parent holon
- **Method:** `POST`
- **Path:** `/api/holon/{{qaHolonPeer.holonId}}/interact`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon_interact_remove_meta

- **Description:** Interact removing metadata keys
- **Method:** `POST`
- **Path:** `/api/holon/{{qaHolonPeer.holonId}}/interact`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon2_cleanup_peer

- **Description:** Delete peer holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{qaHolonPeer.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon2_cleanup_inactive

- **Description:** Delete inactive holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{qaHolonInactive.holonId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_holon2_cleanup_avatar

- **Description:** Delete extended holon avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{qaH2Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star2_seed

- **Description:** Seed avatar for extended STAR QA
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star2_login

- **Description:** Login extended STAR avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star_create_update

- **Description:** Create STAR ODK then update via same name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ qa_star_update_via_create

- **Description:** CreateOrUpdate with same name updates
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ qa_star_gen_empty_bounds

- **Description:** Generate with empty boundHolonIds
- **Method:** `POST`
- **Path:** `/api/starodk/{{qaOdkUpdate.odkId}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star_gen_large_config

- **Description:** Generate with many config keys
- **Method:** `POST`
- **Path:** `/api/starodk/{{qaOdkUpdate.odkId}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star2_cleanup_odk

- **Description:** Delete extended STAR ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{qaOdkUpdate.odkId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_star2_cleanup_avatar

- **Description:** Delete extended STAR avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{qaS2Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc2_seed

- **Description:** Seed avatar for extended blockchain QA
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc2_login

- **Description:** Login extended blockchain avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc2_add_wallet

- **Description:** Add wallet for minting edge case
- **Method:** `POST`
- **Path:** `/api/avatar/{{qaB2Avatar.avatarId}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc2_create_holon

- **Description:** Create holon for mint edge case
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ qa_bc_mint_zero

- **Description:** Mint with zero amount
- **Method:** `POST`
- **Path:** `/api/holon/{{qaB2Holon.holonId}}/mint`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ qa_bc_mint_negative

- **Description:** Mint with negative amount
- **Method:** `POST`
- **Path:** `/api/holon/{{qaB2Holon.holonId}}/mint`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ qa_bc_exchange_self

- **Description:** Exchange with target same as source
- **Method:** `POST`
- **Path:** `/api/holon/{{qaB2Holon.holonId}}/exchange`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ qa_bc2_get_random

- **Description:** Get blockchain op with random GUID
- **Method:** `GET`
- **Path:** `/api/blockchainoperation/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ qa_bc2_cleanup_wallet

- **Description:** Remove extended blockchain wallet
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{qaB2Avatar.avatarId}}/wallets/{{qaB2Wallet.walletId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc2_cleanup_holon

- **Description:** Delete extended blockchain holon
- **Method:** `DELETE`
- **Path:** `/api/holon/{{qaB2Holon.holonId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ qa_bc2_cleanup_avatar

- **Description:** Delete extended blockchain avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{qaB2Avatar.avatarId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

## 🗂️ STARODKController_Malicious

- **Total:** 41 | **Passed:** 9 | **Failed:** 32 | **Skipped:** 0
- **Duration:** 50ms

### ❌ seed_avatar

- **Description:** Register avatar for STAR malicious tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ login_avatar

- **Description:** Login as STAR test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ seed_odk

- **Description:** Create seed ODK for attack tests
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ sqli_odk_name

- **Description:** SQLi in ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 401.

### ❌ sqli_odk_description

- **Description:** SQLi in ODK description
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 401.

### ❌ sqli_odk_public_key

- **Description:** SQLi in public key
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 401.

### ❌ sqli_generate_config

- **Description:** SQLi in generation config
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ xss_odk_name

- **Description:** XSS in ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 2xx, got 401.

### ❌ xss_odk_description

- **Description:** XSS in ODK description
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 401.

### ❌ xss_generate_config

- **Description:** XSS in generation config values
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ get_odk_path_traversal

- **Description:** Path traversal in ODK ID
- **Method:** `GET`
- **Path:** `/api/starodk/../../../etc/passwd`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 404, got 429.

### ❌ get_odk_invalid_guid

- **Description:** Invalid GUID for ODK
- **Method:** `GET`
- **Path:** `/api/starodk/not-a-guid`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 404, got 429.

### ❌ get_odk_null_byte

- **Description:** Null byte in ODK ID
- **Method:** `GET`
- **Path:** `/api/starodk/550e8400-e29b-41d4-a716-446655440000%00`
- **Status:** 400
- **Duration:** 0ms
- **Error:** Expected status 404, got 400.

### ❌ generate_invalid_odk

- **Description:** Generate for non-existent ODK
- **Method:** `POST`
- **Path:** `/api/starodk/00000000-0000-0000-0000-000000000000/generate`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ deploy_invalid_odk

- **Description:** Deploy non-existent ODK
- **Method:** `POST`
- **Path:** `/api/starodk/00000000-0000-0000-0000-000000000000/deploy`
- **Status:** 401
- **Duration:** 2ms
- **Error:** Expected status 404, got 401.

### ❌ delete_invalid_odk

- **Description:** Delete non-existent ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ oversized_odk_name

- **Description:** Extremely long ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 401.

### ❌ oversized_odk_description

- **Description:** Very long ODK description
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 2xx, got 401.

### ❌ oversized_generation_config

- **Description:** Oversized generation config
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 2ms
- **Error:** Expected status 200, got 429.

### ✅ null_odk_name

- **Description:** Null ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 2ms

### ✅ numeric_odk_name

- **Description:** Number as ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms

### ✅ array_description

- **Description:** Array as ODK description
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms

### ✅ invalid_avatar_id

- **Description:** Invalid avatar ID format
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms

### ✅ missing_name

- **Description:** Missing required name field
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms

### ✅ empty_body

- **Description:** Empty body for ODK create
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms

### ❌ generate_invalid_chain

- **Description:** Generate with invalid chain name
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ generate_sqli_chain

- **Description:** SQLi in target chain
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ generate_xss_chain

- **Description:** XSS in target chain
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ✅ generate_invalid_bound_holons

- **Description:** Generate with invalid bound holon IDs
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 0ms

### ✅ generate_null_chain

- **Description:** Generate with null target chain
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 0ms

### ❌ deploy_before_generate

- **Description:** Deploy ODK that was never generated
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/deploy`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ get_odk_unauth

- **Description:** Get ODK without auth
- **Method:** `GET`
- **Path:** `/api/starodk/{{targetODK.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ✅ create_odk_unauth

- **Description:** Create ODK without auth
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms

### ❌ generate_odk_unauth

- **Description:** Generate without auth
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/generate`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ❌ deploy_odk_unauth

- **Description:** Deploy without auth
- **Method:** `POST`
- **Path:** `/api/starodk/{{targetODK.id}}/deploy`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ❌ delete_odk_unauth

- **Description:** Delete without auth
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{targetODK.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ❌ unicode_odk_name

- **Description:** Unicode in ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 2xx, got 401.

### ❌ rtl_odk_name

- **Description:** RTL override in ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 2xx, got 401.

### ❌ zwc_odk_name

- **Description:** Zero-width characters in ODK name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 2xx, got 401.

### ❌ cleanup_target_odk

- **Description:** Delete target ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{targetODK.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ cleanup_avatar

- **Description:** Delete test avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{starMalAvatar.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

## 🗂️ STARODKController_QA

- **Total:** 25 | **Passed:** 1 | **Failed:** 24 | **Skipped:** 0
- **Duration:** 31ms

### ❌ seed_avatar

- **Description:** Register avatar for STAR ODK tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ login_avatar

- **Description:** Login as STAR test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ create_odk_basic

- **Description:** Create basic STAR ODK
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ create_odk_advanced

- **Description:** Create advanced STAR ODK with all fields
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ create_odk_unicode

- **Description:** Create ODK with Unicode name
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ get_odk_by_id

- **Description:** Get ODK by ID
- **Method:** `GET`
- **Path:** `/api/starodk/{{odkBasic.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ get_nonexistent_odk

- **Description:** Get non-existent ODK
- **Method:** `GET`
- **Path:** `/api/starodk/00000000-0000-0000-0000-000000000000`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 404, got 401.

### ❌ get_all_odks

- **Description:** List all ODKs
- **Method:** `GET`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ generate_odk_algorand

- **Description:** Generate dApp for Algorand
- **Method:** `POST`
- **Path:** `/api/starodk/{{odkBasic.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ generate_odk_solana

- **Description:** Generate dApp for Solana
- **Method:** `POST`
- **Path:** `/api/starodk/{{odkAdvanced.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ deploy_odk_basic

- **Description:** Deploy generated ODK
- **Method:** `POST`
- **Path:** `/api/starodk/{{odkBasic.id}}/deploy`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ deploy_odk_advanced

- **Description:** Deploy advanced ODK
- **Method:** `POST`
- **Path:** `/api/starodk/{{odkAdvanced.id}}/deploy`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ update_odk_via_upsert

- **Description:** Update existing ODK via CreateOrUpdate
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ get_odk_unauthorized

- **Description:** Get ODK without auth
- **Method:** `GET`
- **Path:** `/api/starodk/{{odkBasic.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ✅ create_odk_unauthorized

- **Description:** Create ODK without auth
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms

### ❌ delete_odk_unauthorized

- **Description:** Delete ODK without auth
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{odkBasic.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ❌ generate_odk_unauthorized

- **Description:** Generate without auth
- **Method:** `POST`
- **Path:** `/api/starodk/{{odkBasic.id}}/generate`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 401, got 429.

### ❌ create_fresh_odk

- **Description:** Create fresh ODK for deploy-before-generate test
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ deploy_before_generate

- **Description:** Deploy ODK that was never generated should fail
- **Method:** `POST`
- **Path:** `/api/starodk/{{odkFresh.id}}/deploy`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 400, got 429.

### ❌ delete_odk_basic

- **Description:** Delete basic ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{odkBasic.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ delete_odk_advanced

- **Description:** Delete advanced ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{odkAdvanced.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ delete_odk_unicode

- **Description:** Delete unicode ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{odkUnicode.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ delete_odk_fresh

- **Description:** Delete fresh ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{odkFresh.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ verify_deleted_odk

- **Description:** Verify deleted ODK returns 404
- **Method:** `GET`
- **Path:** `/api/starodk/{{odkBasic.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 404, got 429.

### ❌ cleanup_avatar

- **Description:** Delete test avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{starAvatar.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

## 🗂️ STARODKController

- **Total:** 8 | **Passed:** 0 | **Failed:** 8 | **Skipped:** 0
- **Duration:** 8ms

### ❌ seed_avatar

- **Description:** Register avatar for STAR ODK tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ login_seed

- **Description:** Login as STAR test avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ create_odk

- **Description:** Create a STAR ODK
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ get_odk

- **Description:** Get the created ODK
- **Method:** `GET`
- **Path:** `/api/starodk/{{odk1.odkId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ get_all_odks

- **Description:** List all ODKs
- **Method:** `GET`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ generate_odk

- **Description:** Generate dApp for the ODK
- **Method:** `POST`
- **Path:** `/api/starodk/{{odk1.odkId}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ delete_odk

- **Description:** Delete the ODK
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{odk1.odkId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ cleanup_avatar

- **Description:** Clean up the temp avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{savatar.avatarId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

## 🗂️ Stress_RapidOperations

- **Total:** 42 | **Passed:** 0 | **Failed:** 42 | **Skipped:** 0
- **Duration:** 50ms

### ❌ stress_seed_avatar

- **Description:** Register avatar for stress tests
- **Method:** `POST`
- **Path:** `/api/avatar/register`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_login

- **Description:** Login stress avatar
- **Method:** `POST`
- **Path:** `/api/avatar/login`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_holon_1

- **Description:** Stress: rapid holon create 1
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ stress_holon_2

- **Description:** Stress: rapid holon create 2
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ stress_holon_3

- **Description:** Stress: rapid holon create 3
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ stress_holon_4

- **Description:** Stress: rapid holon create 4
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ stress_holon_5

- **Description:** Stress: rapid holon create 5
- **Method:** `POST`
- **Path:** `/api/holon`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ stress_update_1

- **Description:** Stress: rapid update 1 on holon 1
- **Method:** `PUT`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_update_2

- **Description:** Stress: rapid update 2 on holon 1
- **Method:** `PUT`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_update_3

- **Description:** Stress: rapid update 3 on holon 1
- **Method:** `PUT`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_update_4

- **Description:** Stress: rapid update 4 on holon 1
- **Method:** `PUT`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_update_5

- **Description:** Stress: rapid update 5 on holon 1
- **Method:** `PUT`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_verify_holon_1

- **Description:** Stress: verify holon 1 final state
- **Method:** `GET`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_interact_1

- **Description:** Stress: rapid interact add peers
- **Method:** `POST`
- **Path:** `/api/holon/{{stressHolon1.id}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_interact_2

- **Description:** Stress: rapid interact add more peers
- **Method:** `POST`
- **Path:** `/api/holon/{{stressHolon1.id}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_interact_3

- **Description:** Stress: rapid interact remove peer
- **Method:** `POST`
- **Path:** `/api/holon/{{stressHolon1.id}}/interact`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_wallet_1

- **Description:** Stress: add wallet 1
- **Method:** `POST`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_wallet_2

- **Description:** Stress: add wallet 2
- **Method:** `POST`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_wallet_3

- **Description:** Stress: add wallet 3
- **Method:** `POST`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_get_wallets

- **Description:** Stress: verify all wallets exist
- **Method:** `GET`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_remove_wallet_1

- **Description:** Stress: remove wallet 1
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets/{{stressWallet1.walletId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_remove_wallet_2

- **Description:** Stress: remove wallet 2
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets/{{stressWallet2.walletId}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_remove_wallet_3

- **Description:** Stress: remove wallet 3
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets/{{stressWallet3.walletId}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_get_wallets_empty

- **Description:** Stress: verify wallets are empty
- **Method:** `GET`
- **Path:** `/api/avatar/{{stressAvatar.id}}/wallets`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_odk_1

- **Description:** Stress: create ODK 1
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ stress_odk_1_gen

- **Description:** Stress: generate ODK 1
- **Method:** `POST`
- **Path:** `/api/starodk/{{stressODK1.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_odk_1_deploy

- **Description:** Stress: deploy ODK 1
- **Method:** `POST`
- **Path:** `/api/starodk/{{stressODK1.id}}/deploy`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_odk_1_del

- **Description:** Stress: delete ODK 1
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{stressODK1.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_odk_2

- **Description:** Stress: create ODK 2
- **Method:** `POST`
- **Path:** `/api/starodk`
- **Status:** 401
- **Duration:** 1ms
- **Error:** Expected status 200, got 401.

### ❌ stress_odk_2_gen

- **Description:** Stress: generate ODK 2
- **Method:** `POST`
- **Path:** `/api/starodk/{{stressODK2.id}}/generate`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_odk_2_deploy

- **Description:** Stress: deploy ODK 2
- **Method:** `POST`
- **Path:** `/api/starodk/{{stressODK2.id}}/deploy`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

### ❌ stress_odk_2_del

- **Description:** Stress: delete ODK 2
- **Method:** `DELETE`
- **Path:** `/api/starodk/{{stressODK2.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_query_1

- **Description:** Stress: query holons 1
- **Method:** `GET`
- **Path:** `/api/holon?name=Stress`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ stress_query_2

- **Description:** Stress: query holons 2
- **Method:** `GET`
- **Path:** `/api/holon?name=StressHolon`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ stress_query_3

- **Description:** Stress: query holons 3 (no match)
- **Method:** `GET`
- **Path:** `/api/holon?name=NonExistentStress`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ stress_get_all_avatars

- **Description:** Stress: get all avatars
- **Method:** `GET`
- **Path:** `/api/avatar`
- **Status:** 401
- **Duration:** 0ms
- **Error:** Expected status 200, got 401.

### ❌ stress_del_holon_2

- **Description:** Stress: delete holon 2
- **Method:** `DELETE`
- **Path:** `/api/holon/{{stressHolon2.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_del_holon_3

- **Description:** Stress: delete holon 3
- **Method:** `DELETE`
- **Path:** `/api/holon/{{stressHolon3.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_del_holon_4

- **Description:** Stress: delete holon 4
- **Method:** `DELETE`
- **Path:** `/api/holon/{{stressHolon4.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_del_holon_5

- **Description:** Stress: delete holon 5
- **Method:** `DELETE`
- **Path:** `/api/holon/{{stressHolon5.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_del_holon_1

- **Description:** Stress: delete holon 1
- **Method:** `DELETE`
- **Path:** `/api/holon/{{stressHolon1.id}}`
- **Status:** 429
- **Duration:** 0ms
- **Error:** Expected status 200, got 429.

### ❌ stress_cleanup_avatar

- **Description:** Stress: delete stress avatar
- **Method:** `DELETE`
- **Path:** `/api/avatar/{{stressAvatar.id}}`
- **Status:** 429
- **Duration:** 1ms
- **Error:** Expected status 200, got 429.

## 🔥 Failure Rollup

- `AvatarController_Malicious/sqli_username` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/sqli_email` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/sqli_password` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/sqli_login_email` — POST /api/avatar/login => 400 — **Expected status 401, got 400.**
- `AvatarController_Malicious/sqli_blind_union` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/xss_username` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/xss_firstname` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/xss_encoded` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/oversized_username_10k` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/null_byte_injection` — GET /api/avatar/550e8400-e29b-41d4-a716-446655440000%00 => 400 — **Expected status 404, got 400.**
- `AvatarController_Malicious/rtl_override` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/zero_width_chars` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/emoji_username` — POST /api/avatar/register => 400 — **Expected status 2xx, got 400.**
- `AvatarController_Malicious/header_injection_content_type` — POST /api/avatar/register => 0 — **Exception: FormatException: The format of value 'application/json
X-Injected: evil' is invalid.**
- `AvatarController_Malicious/cleanup_negative` — DELETE /api/avatar/c99cfade-ce06-485d-b254-78e647c10fd9 => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/register_unicode_username` — POST /api/avatar/register => 400 — **Expected status 200, got 400.**
- `AvatarController_QA/register_duplicate_username` — POST /api/avatar/register => 400 — **Expected status 200, got 400.**
- `AvatarController_QA/login_unicode` — POST /api/avatar/login => 401 — **Expected status 200, got 401.**
- `AvatarController_QA/get_minimal_avatar` — GET /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/get_full_avatar` — GET /api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6 => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/update_title` — PUT /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af => 400 — **Expected status 200, got 400.**
- `AvatarController_QA/update_email` — PUT /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af => 400 — **Expected status 200, got 400.**
- `AvatarController_QA/update_multiple_fields` — PUT /api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6 => 400 — **Expected status 200, got 400.**
- `AvatarController_QA/update_nonexistent` — PUT /api/avatar/00000000-0000-0000-0000-000000000000 => 400 — **Expected status 404, got 400.**
- `AvatarController_QA/add_algorand_wallet` — POST /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/add_solana_wallet` — POST /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/add_second_algorand_wallet` — POST /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/get_wallets` — GET /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/remove_solana_wallet` — DELETE /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af/wallets/{{solWallet.walletId}} => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/add_wallet_to_other_avatar` — POST /api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/delete_minimal_avatar` — DELETE /api/avatar/1a11c017-b129-41a6-baa8-e1f86bed12af => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/delete_full_avatar` — DELETE /api/avatar/b980507b-f002-45b5-b069-88e10f00c9e6 => 404 — **Expected status 200, got 404.**
- `AvatarController_QA/delete_unicode_avatar` — DELETE /api/avatar/{{unicodeAvatar.id}} => 404 — **Expected status 200, got 404.**
- `AvatarController/get_avatar` — GET /api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2 => 404 — **Expected status 200, got 404.**
- `AvatarController/update_avatar` — PUT /api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2 => 400 — **Expected status 200, got 400.**
- `AvatarController/add_wallet` — POST /api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController/get_wallets` — GET /api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2/wallets => 404 — **Expected status 200, got 404.**
- `AvatarController/remove_wallet` — DELETE /api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2/wallets/{{wallet1.walletId}} => 404 — **Expected status 200, got 404.**
- `AvatarController/delete_avatar` — DELETE /api/avatar/2f025fdb-94f5-454b-9136-e3baa3074bd2 => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_add_wallet` — POST /api/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996/wallets => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_add_wallet` — POST /api/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212/wallets => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `Blockchain_Devnet/sol_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `Blockchain_Devnet/algo_create_peer_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `Blockchain_Devnet/algo_mint_asa` — POST /api/holon/{{algoHolon.id}}/mint => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_mint_small` — POST /api/holon/{{algoHolon.id}}/mint => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_mint_large` — POST /api/holon/{{algoHolon.id}}/mint => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_mint_spl` — POST /api/holon/{{solHolon.id}}/mint => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_mint_nft` — POST /api/holon/{{solHolon.id}}/mint => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_exchange` — POST /api/holon/{{algoHolon.id}}/exchange => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_exchange_reverse` — POST /api/holon/{{algoPeerHolon.id}}/exchange => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_get_op_by_id` — GET /api/blockchainoperation/{{algoMintOp.opId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_get_op_by_id` — GET /api/blockchainoperation/{{solMintOp.opId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_verify_mint_status` — GET /api/blockchainoperation/{{algoMintOp.opId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_verify_mint_status` — GET /api/blockchainoperation/{{solMintOp.opId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_get_algo_op` — GET /api/blockchainoperation/{{algoMintOp.opId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_cleanup_peer_holon` — DELETE /api/holon/{{algoPeerHolon.id}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_cleanup_holon` — DELETE /api/holon/{{algoHolon.id}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_cleanup_holon` — DELETE /api/holon/{{solHolon.id}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_cleanup_wallet` — DELETE /api/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996/wallets/{{algoWallet.walletId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_cleanup_wallet` — DELETE /api/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212/wallets/{{solWallet.walletId}} => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/algo_cleanup_avatar` — DELETE /api/avatar/86c4b239-b86e-4a9b-a7e4-dac3f2df9996 => 404 — **Expected status 200, got 404.**
- `Blockchain_Devnet/sol_cleanup_avatar` — DELETE /api/avatar/93c61e14-c12a-483e-98fb-8ac4dd20d212 => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController_Malicious/get_op_null_byte` — GET /api/blockchainoperation/550e8400-e29b-41d4-a716-446655440000%00 => 400 — **Expected status 404, got 400.**
- `BlockchainOperationController_Malicious/header_injection_op` — GET /api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb => 200 — **Expected status 401, got 200.**
- `BlockchainOperationController_Malicious/get_op_sqli_provider_type` — GET /api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb?providerType=' OR '1'='1 => 400 — **Expected status 2xx, got 400.**
- `BlockchainOperationController_Malicious/get_op_xss_provider_type` — GET /api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb?providerType=<script>alert(1)</script> => 400 — **Expected status 2xx, got 400.**
- `BlockchainOperationController_Malicious/get_op_very_long_query` — GET /api/blockchainoperation/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb?customProviderKeys=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA => 400 — **Expected status 2xx, got 400.**
- `BlockchainOperationController_Malicious/cleanup_avatar` — DELETE /api/avatar/451ddc6d-c668-4f63-9ecc-fb98fa235feb => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController_QA/add_wallet` — POST /api/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec/wallets => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController_QA/cleanup_wallet` — DELETE /api/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec/wallets/{{bcWallet.walletId}} => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController_QA/cleanup_avatar` — DELETE /api/avatar/da5b882e-4656-45a4-b983-18bc1fbb81ec => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController/seed_wallet` — POST /api/avatar/0028eb26-9d88-4b02-b979-04c491996d8d/wallets => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController/cleanup_wallet` — DELETE /api/avatar/0028eb26-9d88-4b02-b979-04c491996d8d/wallets/{{bwallet.walletId}} => 404 — **Expected status 200, got 404.**
- `BlockchainOperationController/cleanup_avatar` — DELETE /api/avatar/0028eb26-9d88-4b02-b979-04c491996d8d => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_add_algo_wallet` — POST /api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce/wallets => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_get_wallets` — GET /api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce/wallets => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_create_source_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e1_create_target_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e1_mint_source` — POST /api/holon/{{e2eSourceHolon.id}}/mint => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_exchange` — POST /api/holon/{{e2eSourceHolon.id}}/exchange => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_interact_source` — POST /api/holon/{{e2eSourceHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_get_mint_op` — GET /api/blockchainoperation/{{e2eMintOp.mintOpId}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_get_exchange_op` — GET /api/blockchainoperation/{{e2eExchangeOp.exchangeOpId}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_update_avatar` — PUT /api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e1_cleanup_holons` — DELETE /api/holon/{{e2eTargetHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_cleanup_source_holon` — DELETE /api/holon/{{e2eSourceHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_cleanup_wallet` — DELETE /api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce/wallets/{{e2eWallet.walletId}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e1_cleanup_avatar` — DELETE /api/avatar/f129f5f2-4c39-4b88-a9c6-dd87149920ce => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_create_holon_a` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e2_create_holon_b` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e2_create_odk` — POST /api/starodk => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e2_generate_odk` — POST /api/starodk/{{e2e2ODK.id}}/generate => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_get_odk_post_gen` — GET /api/starodk/{{e2e2ODK.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_deploy_odk` — POST /api/starodk/{{e2e2ODK.id}}/deploy => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_get_odk_post_deploy` — GET /api/starodk/{{e2e2ODK.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_cleanup_odk` — DELETE /api/starodk/{{e2e2ODK.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_cleanup_holon_a` — DELETE /api/holon/{{e2e2HolonA.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_cleanup_holon_b` — DELETE /api/holon/{{e2e2HolonB.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e2_cleanup_avatar` — DELETE /api/avatar/16d8637e-31b2-492c-98a5-f82178f6b178 => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_alpha_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e3_beta_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `CrossController_E2E/e2e3_alpha_get_own` — GET /api/holon/{{alphaHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_beta_get_own` — GET /api/holon/{{betaHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_alpha_get_beta` — GET /api/holon/{{betaHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_beta_get_alpha` — GET /api/holon/{{alphaHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_cleanup_alpha_holon` — DELETE /api/holon/{{alphaHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_cleanup_beta_holon` — DELETE /api/holon/{{betaHolon.id}} => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_cleanup_alpha` — DELETE /api/avatar/921dfe0c-01ee-42a7-9cb3-4d6d53b8e42a => 404 — **Expected status 200, got 404.**
- `CrossController_E2E/e2e3_cleanup_beta` — DELETE /api/avatar/60b33154-67c3-4fb8-9d1b-09d998a6455f => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_add_wallet` — POST /api/avatar/d1d8365d-6752-45a4-b495-1305166df7cc/wallets => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e1_get_holon` — GET /api/holon/{{e2e1ParentHolon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_update_holon` — PUT /api/holon/{{e2e1ParentHolon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_interact_holon` — POST /api/holon/{{e2e1ParentHolon.holonId}}/interact => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_create_subholon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e1_get_subholon` — GET /api/holon/{{e2e1SubHolon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_delete_subholon` — DELETE /api/holon/{{e2e1SubHolon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_delete_holon` — DELETE /api/holon/{{e2e1ParentHolon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_remove_wallet` — DELETE /api/avatar/d1d8365d-6752-45a4-b495-1305166df7cc/wallets/{{e2e1Wallet.walletId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e1_delete_avatar` — DELETE /api/avatar/d1d8365d-6752-45a4-b495-1305166df7cc => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e2_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e2_create_odk` — POST /api/starodk => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e2_get_odk` — GET /api/starodk/{{e2e2Odk.odkId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e2_generate_odk` — POST /api/starodk/{{e2e2Odk.odkId}}/generate => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e2_delete_odk` — DELETE /api/starodk/{{e2e2Odk.odkId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e2_delete_holon` — DELETE /api/holon/{{e2e2Holon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e2_delete_avatar` — DELETE /api/avatar/d7393c26-1da3-401e-b606-5ab40bf60268 => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e3_create_holon_a` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e3_cleanup_holon_a` — DELETE /api/holon/{{e2e3HolonA.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e3_cleanup_avatar_a` — DELETE /api/avatar/ca74748e-ac3c-42f1-8bda-3f2cf1bbb9ca => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e3_cleanup_avatar_b` — DELETE /api/avatar/ea1da1ed-a03a-44b3-804c-0ad7029eff7c => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e4_add_wallet` — POST /api/avatar/d440aa03-5dce-41ef-b272-43dab43c3793/wallets => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e4_create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e4_mint` — POST /api/holon/{{e2e4Holon.holonId}}/mint => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e4_get_operation` — GET /api/blockchainoperation/{{e2e4Op.operationId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e4_remove_wallet` — DELETE /api/avatar/d440aa03-5dce-41ef-b272-43dab43c3793/wallets/{{e2e4Wallet.walletId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e4_delete_holon` — DELETE /api/holon/{{e2e4Holon.holonId}} => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e4_delete_avatar` — DELETE /api/avatar/d440aa03-5dce-41ef-b272-43dab43c3793 => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e5_get_1` — GET /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e5_put_1` — PUT /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e5_get_2` — GET /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e5_put_2` — PUT /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e5_get_3` — GET /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e5_put_3` — PUT /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e5_get_4` — GET /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e5_put_4` — PUT /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 400 — **Expected status 200, got 400.**
- `E2E-Flows/e2e5_get_5` — GET /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e5_delete` — DELETE /api/avatar/b5c0c23e-db7b-4b56-b709-08ef346e70ed => 404 — **Expected status 200, got 404.**
- `E2E-Flows/e2e6_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_create` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e6_interact_1` — POST /api/holon/{{e2e6Holon.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_interact_2` — POST /api/holon/{{e2e6Holon.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_interact_3` — POST /api/holon/{{e2e6Holon.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_interact_4` — POST /api/holon/{{e2e6Holon.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_interact_5` — POST /api/holon/{{e2e6Holon.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_get_final` — GET /api/holon/{{e2e6Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_delete_holon` — DELETE /api/holon/{{e2e6Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e6_delete_avatar` — DELETE /api/avatar/05bb26c8-3b4a-45fa-b1e7-5ea72351cd02 => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e7_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e7_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e7_get_avatar` — GET /api/avatar/{{e2e7Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e7_get_all_avatars` — GET /api/avatar => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e7_create_holon` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e7_get_holon` — GET /api/holon/{{e2e7Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e7_query_holon` — GET /api/holon?name=PersistHolon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e7_get_all_odks` — GET /api/starodk => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e7_get_bc_by_avatar` — GET /api/blockchainoperation/avatar/{{e2e7Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e7_delete_holon` — DELETE /api/holon/{{e2e7Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e7_delete_avatar` — DELETE /api/avatar/{{e2e7Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_create_holon` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e8_create_odk` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e8_generate_odk` — POST /api/starodk/{{e2e8Odk.odkId}}/generate => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_deploy_odk` — POST /api/starodk/{{e2e8Odk.odkId}}/deploy => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_get_odk_after_deploy` — GET /api/starodk/{{e2e8Odk.odkId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_delete_odk` — DELETE /api/starodk/{{e2e8Odk.odkId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_delete_holon` — DELETE /api/holon/{{e2e8Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e8_delete_avatar` — DELETE /api/avatar/{{e2e8Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_add_wallet` — POST /api/avatar/{{e2e9Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_create_source` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e9_create_target` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e9_mint_source` — POST /api/holon/{{e2e9Source.holonId}}/mint => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_get_mint_op` — GET /api/blockchainoperation/{{e2e9MintOp.operationId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_exchange` — POST /api/holon/{{e2e9Source.holonId}}/exchange => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_get_exchange_op` — GET /api/blockchainoperation/{{e2e9ExchangeOp.operationId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_get_ops_by_avatar` — GET /api/blockchainoperation/avatar/{{e2e9Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_delete_source` — DELETE /api/holon/{{e2e9Source.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_delete_target` — DELETE /api/holon/{{e2e9Target.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_remove_wallet` — DELETE /api/avatar/{{e2e9Avatar.avatarId}}/wallets/{{e2e9Wallet.walletId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e9_delete_avatar` — DELETE /api/avatar/{{e2e9Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_wallet_1` — POST /api/avatar/{{e2e10Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_wallet_2` — POST /api/avatar/{{e2e10Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_wallet_3` — POST /api/avatar/{{e2e10Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_get_wallets` — GET /api/avatar/{{e2e10Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_remove_wallet_2` — DELETE /api/avatar/{{e2e10Avatar.avatarId}}/wallets/{{e2e10Wallet2.walletId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_get_wallets_after` — GET /api/avatar/{{e2e10Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_remove_wallet_1` — DELETE /api/avatar/{{e2e10Avatar.avatarId}}/wallets/{{e2e10Wallet1.walletId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_remove_wallet_3` — DELETE /api/avatar/{{e2e10Avatar.avatarId}}/wallets/{{e2e10Wallet3.walletId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e10_delete_avatar` — DELETE /api/avatar/{{e2e10Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_create_a` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e11_create_b` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e11_create_c` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e11_query_all` — GET /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e11_query_batch` — GET /api/holon?name=BulkHolon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e11_update_a` — PUT /api/holon/{{e2e11HolonA.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_interact_b` — POST /api/holon/{{e2e11HolonB.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_delete_c` — DELETE /api/holon/{{e2e11HolonC.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_query_remaining` — GET /api/holon?name=BulkHolon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e11_delete_a` — DELETE /api/holon/{{e2e11HolonA.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_delete_b` — DELETE /api/holon/{{e2e11HolonB.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e11_delete_avatar` — DELETE /api/avatar/{{e2e11Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e12_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e12_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e12_delete` — DELETE /api/avatar/{{e2e12Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e12_login_after_delete` — POST /api/avatar/login => 429 — **Expected status 401, got 429.**
- `E2E-Flows/e2e12_reregister` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e12_login_reregistered` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e12_delete_reregistered` — DELETE /api/avatar/{{e2e12Avatar2.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_create_gp` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e13_create_parent` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e13_create_child` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e13_get_gp` — GET /api/holon/{{e2e13GP.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_get_parent` — GET /api/holon/{{e2e13Parent.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_get_child` — GET /api/holon/{{e2e13Child.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_interact_reparent` — POST /api/holon/{{e2e13Child.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_get_child_after` — GET /api/holon/{{e2e13Child.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_delete_child` — DELETE /api/holon/{{e2e13Child.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_delete_parent` — DELETE /api/holon/{{e2e13Parent.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_delete_gp` — DELETE /api/holon/{{e2e13GP.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e13_delete_avatar` — DELETE /api/avatar/{{e2e13Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e14_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e14_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e14_create_odk` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e14_update_odk` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e14_verify_same_id` — GET /api/starodk/{{e2e14OdkV2.odkId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e14_delete_odk` — DELETE /api/starodk/{{e2e14OdkV2.odkId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e14_delete_avatar` — DELETE /api/avatar/{{e2e14Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_create` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e15_get_1` — GET /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_put_1` — PUT /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_get_2` — GET /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_put_2` — PUT /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_get_3` — GET /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_put_3` — PUT /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_get_4` — GET /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_put_4` — PUT /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_get_5` — GET /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_delete` — DELETE /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e15_get_deleted` — GET /api/holon/{{e2e15Holon.holonId}} => 429 — **Expected status 404, got 429.**
- `E2E-Flows/e2e15_delete_avatar` — DELETE /api/avatar/{{e2e15Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_reg_a` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_reg_b` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_reg_c` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_login_a` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_login_b` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_login_c` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_holon_a` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e16_holon_b` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e16_holon_c` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e16_query_all` — GET /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e16_a_get_b_holon` — GET /api/holon/{{e2e16HolonB.holonId}} => 429 — **Expected status 404, got 429.**
- `E2E-Flows/e2e16_b_get_c_holon` — GET /api/holon/{{e2e16HolonC.holonId}} => 429 — **Expected status 404, got 429.**
- `E2E-Flows/e2e16_c_get_a_holon` — GET /api/holon/{{e2e16HolonA.holonId}} => 429 — **Expected status 404, got 429.**
- `E2E-Flows/e2e16_del_holon_a` — DELETE /api/holon/{{e2e16HolonA.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_del_holon_b` — DELETE /api/holon/{{e2e16HolonB.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_del_holon_c` — DELETE /api/holon/{{e2e16HolonC.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_del_avatar_a` — DELETE /api/avatar/{{e2e16AvatarA.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_del_avatar_b` — DELETE /api/avatar/{{e2e16AvatarB.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e16_del_avatar_c` — DELETE /api/avatar/{{e2e16AvatarC.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_register` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_update_1` — PUT /api/avatar/{{e2e17Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_update_2` — PUT /api/avatar/{{e2e17Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_update_3` — PUT /api/avatar/{{e2e17Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_create_holon` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e17_query_by_avatar` — GET /api/holon?avatarId={{e2e17Avatar.avatarId}} => 401 — **Expected status 200, got 401.**
- `E2E-Flows/e2e17_get_avatar_final` — GET /api/avatar/{{e2e17Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_delete_holon` — DELETE /api/holon/{{e2e17Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `E2E-Flows/e2e17_delete_avatar` — DELETE /api/avatar/{{e2e17Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `HolonController_Malicious/seed_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController_Malicious/sqli_holon_name` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/sqli_holon_description` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/sqli_holon_provider` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/sqli_metadata_key` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/sqli_metadata_value` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/sqli_chain_id` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/xss_holon_name` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/xss_holon_description` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/xss_metadata` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/xss_interact` — POST /api/holon/{{targetHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_Malicious/empty_guid_holon` — GET /api/holon/ => 200 — **Expected status 404, got 200.**
- `HolonController_Malicious/guid_with_null` — GET /api/holon/550e8400-e29b-41d4-a716-446655440000%00 => 400 — **Expected status 404, got 400.**
- `HolonController_Malicious/oversized_holon_name` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/oversized_metadata` — POST /api/holon => 400 — **Expected status 2xx, got 400.**
- `HolonController_Malicious/sqli_interact_metadata` — POST /api/holon/{{targetHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_Malicious/xss_interact_metadata` — POST /api/holon/{{targetHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_Malicious/interact_circular_parent` — POST /api/holon/{{targetHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_Malicious/interact_null_parent` — POST /api/holon/{{targetHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_Malicious/cleanup_target_holon` — DELETE /api/holon/{{targetHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_Malicious/cleanup_avatar` — DELETE /api/avatar/ea04b3f4-85a8-4709-b39b-2b8b1edde3c6 => 404 — **Expected status 200, got 404.**
- `HolonController_QA/create_root_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController_QA/create_child_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController_QA/create_peer_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController_QA/create_chain_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController_QA/get_root_holon` — GET /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/verify_avatar_id_set` — GET /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/update_name` — PUT /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/update_metadata` — PUT /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/update_chain` — PUT /api/holon/{{chainHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/update_nonexistent` — PUT /api/holon/00000000-0000-0000-0000-000000000000 => 400 — **Expected status 404, got 400.**
- `HolonController_QA/interact_add_peers` — POST /api/holon/{{rootHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_QA/interact_change_parent` — POST /api/holon/{{childHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_QA/interact_remove_metadata` — POST /api/holon/{{rootHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_QA/interact_remove_peers` — POST /api/holon/{{rootHolon.id}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController_QA/isolation_avatar_b_get_a_holon` — GET /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/isolation_avatar_b_update_a_holon` — PUT /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/isolation_avatar_b_delete_a_holon` — DELETE /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/create_b_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController_QA/delete_child_holon` — DELETE /api/holon/{{childHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/delete_peer_holon` — DELETE /api/holon/{{peerHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/delete_chain_holon` — DELETE /api/holon/{{chainHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/delete_root_holon` — DELETE /api/holon/{{rootHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/delete_b_holon` — DELETE /api/holon/{{bHolon.id}} => 404 — **Expected status 200, got 404.**
- `HolonController_QA/cleanup_avatar_a` — DELETE /api/avatar/e54a6d9d-8c78-485c-967f-3bbbce920510 => 404 — **Expected status 200, got 404.**
- `HolonController_QA/cleanup_avatar_b` — DELETE /api/avatar/182de96d-7460-4447-b8e9-04f48b6038a8 => 404 — **Expected status 200, got 404.**
- `HolonController/create_holon` — POST /api/holon => 400 — **Expected status 200, got 400.**
- `HolonController/get_holon` — GET /api/holon/{{holon1.holonId}} => 404 — **Expected status 200, got 404.**
- `HolonController/update_holon` — PUT /api/holon/{{holon1.holonId}} => 404 — **Expected status 200, got 404.**
- `HolonController/interact_holon` — POST /api/holon/{{holon1.holonId}}/interact => 404 — **Expected status 200, got 404.**
- `HolonController/delete_holon` — DELETE /api/holon/{{holon1.holonId}} => 404 — **Expected status 200, got 404.**
- `HolonController/cleanup_avatar` — DELETE /api/avatar/b5422d85-1cda-413e-a1ea-4c18c42d8bc1 => 404 — **Expected status 200, got 404.**
- `MaliciousPayloads/mal_sql_email` — POST /api/avatar/register => 200 — **Expected status 400, got 200.**
- `MaliciousPayloads/mal_sql_login_email` — POST /api/avatar/login => 400 — **Expected status 401, got 400.**
- `MaliciousPayloads/mal_null_password` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_wallet_xss_address` — POST /api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets => 404 — **Expected status 400, got 404.**
- `MaliciousPayloads/mal_wallet_sqli_label` — POST /api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets => 404 — **Expected status 400, got 404.**
- `MaliciousPayloads/mal_avatar_cleanup` — DELETE /api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62 => 404 — **Expected status 200, got 404.**
- `MaliciousPayloads/mal_holon_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_holon_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_holon_sql_name` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_sql_desc` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_xss_name` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_xss_metadata` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_oversized_name` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_oversized_metadata` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_type_name_object` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_type_peer_ids_string` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_type_metadata_array` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_null_name` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_null_provider` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_holon_interact_sqli` — POST /api/holon/88888888-8888-8888-8888-888888888888/interact => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_holon_interact_xss` — POST /api/holon/88888888-8888-8888-8888-888888888888/interact => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_holon_cleanup_avatar` — DELETE /api/avatar/{{malHAvatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_star_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_star_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_star_sql_name` — POST /api/starodk => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_star_xss_desc` — POST /api/starodk => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_star_xss_pubkey` — POST /api/starodk => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_star_type_avatarId_object` — POST /api/starodk => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_star_type_name_array` — POST /api/starodk => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_star_gen_xss_config` — POST /api/starodk/99999999-9999-9999-9999-999999999999/generate => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_star_cleanup_avatar` — DELETE /api/avatar/{{malSAvatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_bc_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_bc_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `MaliciousPayloads/mal_bc_by_avatar_sqli` — GET /api/blockchainoperation/avatar/' OR '1'='1 => 429 — **Expected status 404, got 429.**
- `MaliciousPayloads/mal_bc_get_sqli` — GET /api/blockchainoperation/' UNION SELECT * FROM BlockchainOperations -- => 429 — **Expected status 404, got 429.**
- `MaliciousPayloads/mal_nosql_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_nosql_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_nosql_login` — POST /api/avatar/login => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_cmd_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_cmd_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_cmd_backtick` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_ldap_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_ldap_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_mass_assignment` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_mass_holon` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_deep_nest` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_json_bomb_meta` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_ssrf_tokenuri` — POST /api/holon/88888888-8888-8888-8888-888888888888/mint => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_ssrf_localhost` — POST /api/holon/88888888-8888-8888-8888-888888888888/mint => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_ssrf_file` — POST /api/holon/88888888-8888-8888-8888-888888888888/mint => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_crlf_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_header_inject` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_fmt_string_user` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_fmt_string_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_log_inject` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_proto_pollute` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_proto_constructor` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_negative_amount` — POST /api/holon/88888888-8888-8888-8888-888888888888/mint => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_float_amount` — POST /api/holon/88888888-8888-8888-8888-888888888888/mint => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_max_int` — POST /api/holon/88888888-8888-8888-8888-888888888888/mint => 401 — **Expected status 404, got 401.**
- `MaliciousPayloads/mal_homograph_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_rtl_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_zwj_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_path_wallet` — POST /api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets => 404 — **Expected status 400, got 404.**
- `MaliciousPayloads/mal_path_chaintype` — POST /api/avatar/7124964a-2fb0-4fc6-b963-73c49d33fe62/wallets => 404 — **Expected status 400, got 404.**
- `MaliciousPayloads/mal_template_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_template_email` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_template_jinja` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_xml_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_xml_json_body` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_array_body_register` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_array_body_login` — POST /api/avatar/login => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_string_body_holon` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `MaliciousPayloads/mal_bool_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_bool_password` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `MaliciousPayloads/mal_bc_cleanup_avatar` — DELETE /api/avatar/{{malBAvatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_login_missing_email` — POST /api/avatar/login => 429 — **Expected status 401, got 429.**
- `QA-EdgeCases/qa_update_empty` — PUT /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 400 — **Expected status 200, got 400.**
- `QA-EdgeCases/qa_update_single_field` — PUT /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 400 — **Expected status 200, got 400.**
- `QA-EdgeCases/qa_wallet_add_missing_chain` — POST /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets => 404 — **Expected status 400, got 404.**
- `QA-EdgeCases/qa_wallet_add_missing_address` — POST /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets => 404 — **Expected status 400, got 404.**
- `QA-EdgeCases/qa_wallet_add_valid` — POST /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets => 404 — **Expected status 200, got 404.**
- `QA-EdgeCases/qa_wallet_remove_valid` — DELETE /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets/{{qaWallet.walletId}} => 404 — **Expected status 200, got 404.**
- `QA-EdgeCases/qa_holon_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_empty_name` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `QA-EdgeCases/qa_holon_no_provider` — POST /api/holon => 401 — **Expected status 400, got 401.**
- `QA-EdgeCases/qa_holon_empty_desc` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_rich_create` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_get_random` — GET /api/holon/33333333-3333-3333-3333-333333333333 => 401 — **Expected status 404, got 401.**
- `QA-EdgeCases/qa_holon_query_all` — GET /api/holon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_query_name` — GET /api/holon?name=RichHolon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_query_nomatch` — GET /api/holon?name=NonExistentHolonXYZ => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_update_empty` — PUT /api/holon/{{qaHolon2.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_update_metadata` — PUT /api/holon/{{qaHolon2.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_interact_empty` — POST /api/holon/{{qaHolon2.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_interact_meta` — POST /api/holon/{{qaHolon2.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_delete` — DELETE /api/holon/{{qaHolon1.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_get_deleted` — GET /api/holon/{{qaHolon1.holonId}} => 429 — **Expected status 404, got 429.**
- `QA-EdgeCases/qa_holon_cleanup_avatar` — DELETE /api/avatar/{{qaHAvatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star_empty_name` — POST /api/starodk => 401 — **Expected status 400, got 401.**
- `QA-EdgeCases/qa_star_minimal` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_star_no_avatar` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_star_get_random` — GET /api/starodk/44444444-4444-4444-4444-444444444444 => 401 — **Expected status 404, got 401.**
- `QA-EdgeCases/qa_star_gen_empty_chain` — POST /api/starodk/{{qaOdk1.odkId}}/generate => 429 — **Expected status 400, got 429.**
- `QA-EdgeCases/qa_star_deploy_random` — POST /api/starodk/55555555-5555-5555-5555-555555555555/deploy => 401 — **Expected status 404, got 401.**
- `QA-EdgeCases/qa_star_delete` — DELETE /api/starodk/{{qaOdk2.odkId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star_get_deleted` — GET /api/starodk/{{qaOdk2.odkId}} => 429 — **Expected status 404, got 429.**
- `QA-EdgeCases/qa_star_cleanup_avatar` — DELETE /api/avatar/{{qaSAvatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc_get_random` — GET /api/blockchainoperation/66666666-6666-6666-6666-666666666666 => 401 — **Expected status 404, got 401.**
- `QA-EdgeCases/qa_bc_by_avatar_random` — GET /api/blockchainoperation/avatar/77777777-7777-7777-7777-777777777777 => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_bc_by_avatar_empty` — GET /api/blockchainoperation/avatar/{{qaBAvatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_register_dup_username` — POST /api/avatar/register => 429 — **Expected status 400, got 429.**
- `QA-EdgeCases/qa_login_email_case` — POST /api/avatar/login => 429 — **Expected status 401, got 429.**
- `QA-EdgeCases/qa_update_isactive_false` — PUT /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 400 — **Expected status 200, got 400.**
- `QA-EdgeCases/qa_update_isactive_true` — PUT /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 400 — **Expected status 200, got 400.**
- `QA-EdgeCases/qa_update_empty_string` — PUT /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 400 — **Expected status 200, got 400.**
- `QA-EdgeCases/qa_update_long_name` — PUT /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 400 — **Expected status 200, got 400.**
- `QA-EdgeCases/qa_wallet_default_true` — POST /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets => 404 — **Expected status 200, got 404.**
- `QA-EdgeCases/qa_wallet_default_false` — POST /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets => 404 — **Expected status 200, got 404.**
- `QA-EdgeCases/qa_wallet_cleanup_default` — DELETE /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4/wallets/{{qaWallet2.walletId}} => 404 — **Expected status 200, got 404.**
- `QA-EdgeCases/qa_avatar_cleanup` — DELETE /api/avatar/dcc7c08d-2a0d-47a1-9e3d-e1da8f2950a4 => 404 — **Expected status 200, got 404.**
- `QA-EdgeCases/qa_holon2_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon2_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_create_inactive` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_activate` — PUT /api/holon/{{qaHolonInactive.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_deactivate` — PUT /api/holon/{{qaHolonInactive.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_peer_create` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_query_multi` — GET /api/holon?name=PeerHolon&providerName=InMemory => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_query_provider_only` — GET /api/holon?providerName=InMemory => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_query_chainid` — GET /api/holon?chainId=nonexistent => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_holon_update_peers` — PUT /api/holon/{{qaHolonPeer.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_interact_peers` — POST /api/holon/{{qaHolonPeer.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_interact_remove_peers` — POST /api/holon/{{qaHolonPeer.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_interact_parent` — POST /api/holon/{{qaHolonPeer.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon_interact_remove_meta` — POST /api/holon/{{qaHolonPeer.holonId}}/interact => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon2_cleanup_peer` — DELETE /api/holon/{{qaHolonPeer.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon2_cleanup_inactive` — DELETE /api/holon/{{qaHolonInactive.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_holon2_cleanup_avatar` — DELETE /api/avatar/{{qaH2Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star2_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star2_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star_create_update` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_star_update_via_create` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_star_gen_empty_bounds` — POST /api/starodk/{{qaOdkUpdate.odkId}}/generate => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star_gen_large_config` — POST /api/starodk/{{qaOdkUpdate.odkId}}/generate => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star2_cleanup_odk` — DELETE /api/starodk/{{qaOdkUpdate.odkId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_star2_cleanup_avatar` — DELETE /api/avatar/{{qaS2Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc2_seed` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc2_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc2_add_wallet` — POST /api/avatar/{{qaB2Avatar.avatarId}}/wallets => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc2_create_holon` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `QA-EdgeCases/qa_bc_mint_zero` — POST /api/holon/{{qaB2Holon.holonId}}/mint => 429 — **Expected status 400, got 429.**
- `QA-EdgeCases/qa_bc_mint_negative` — POST /api/holon/{{qaB2Holon.holonId}}/mint => 429 — **Expected status 400, got 429.**
- `QA-EdgeCases/qa_bc_exchange_self` — POST /api/holon/{{qaB2Holon.holonId}}/exchange => 429 — **Expected status 400, got 429.**
- `QA-EdgeCases/qa_bc2_get_random` — GET /api/blockchainoperation/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa => 401 — **Expected status 404, got 401.**
- `QA-EdgeCases/qa_bc2_cleanup_wallet` — DELETE /api/avatar/{{qaB2Avatar.avatarId}}/wallets/{{qaB2Wallet.walletId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc2_cleanup_holon` — DELETE /api/holon/{{qaB2Holon.holonId}} => 429 — **Expected status 200, got 429.**
- `QA-EdgeCases/qa_bc2_cleanup_avatar` — DELETE /api/avatar/{{qaB2Avatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/seed_avatar` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/login_avatar` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/seed_odk` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_Malicious/sqli_odk_name` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/sqli_odk_description` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/sqli_odk_public_key` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/sqli_generate_config` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/xss_odk_name` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/xss_odk_description` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/xss_generate_config` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/get_odk_path_traversal` — GET /api/starodk/../../../etc/passwd => 429 — **Expected status 404, got 429.**
- `STARODKController_Malicious/get_odk_invalid_guid` — GET /api/starodk/not-a-guid => 429 — **Expected status 404, got 429.**
- `STARODKController_Malicious/get_odk_null_byte` — GET /api/starodk/550e8400-e29b-41d4-a716-446655440000%00 => 400 — **Expected status 404, got 400.**
- `STARODKController_Malicious/generate_invalid_odk` — POST /api/starodk/00000000-0000-0000-0000-000000000000/generate => 401 — **Expected status 404, got 401.**
- `STARODKController_Malicious/deploy_invalid_odk` — POST /api/starodk/00000000-0000-0000-0000-000000000000/deploy => 401 — **Expected status 404, got 401.**
- `STARODKController_Malicious/delete_invalid_odk` — DELETE /api/starodk/00000000-0000-0000-0000-000000000000 => 401 — **Expected status 404, got 401.**
- `STARODKController_Malicious/oversized_odk_name` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/oversized_odk_description` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/oversized_generation_config` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/generate_invalid_chain` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/generate_sqli_chain` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/generate_xss_chain` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/deploy_before_generate` — POST /api/starodk/{{targetODK.id}}/deploy => 429 — **Expected status 400, got 429.**
- `STARODKController_Malicious/get_odk_unauth` — GET /api/starodk/{{targetODK.id}} => 429 — **Expected status 401, got 429.**
- `STARODKController_Malicious/generate_odk_unauth` — POST /api/starodk/{{targetODK.id}}/generate => 429 — **Expected status 401, got 429.**
- `STARODKController_Malicious/deploy_odk_unauth` — POST /api/starodk/{{targetODK.id}}/deploy => 429 — **Expected status 401, got 429.**
- `STARODKController_Malicious/delete_odk_unauth` — DELETE /api/starodk/{{targetODK.id}} => 429 — **Expected status 401, got 429.**
- `STARODKController_Malicious/unicode_odk_name` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/rtl_odk_name` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/zwc_odk_name` — POST /api/starodk => 401 — **Expected status 2xx, got 401.**
- `STARODKController_Malicious/cleanup_target_odk` — DELETE /api/starodk/{{targetODK.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_Malicious/cleanup_avatar` — DELETE /api/avatar/{{starMalAvatar.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/seed_avatar` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/login_avatar` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/create_odk_basic` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_QA/create_odk_advanced` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_QA/create_odk_unicode` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_QA/get_odk_by_id` — GET /api/starodk/{{odkBasic.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/get_nonexistent_odk` — GET /api/starodk/00000000-0000-0000-0000-000000000000 => 401 — **Expected status 404, got 401.**
- `STARODKController_QA/get_all_odks` — GET /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_QA/generate_odk_algorand` — POST /api/starodk/{{odkBasic.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/generate_odk_solana` — POST /api/starodk/{{odkAdvanced.id}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/deploy_odk_basic` — POST /api/starodk/{{odkBasic.id}}/deploy => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/deploy_odk_advanced` — POST /api/starodk/{{odkAdvanced.id}}/deploy => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/update_odk_via_upsert` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_QA/get_odk_unauthorized` — GET /api/starodk/{{odkBasic.id}} => 429 — **Expected status 401, got 429.**
- `STARODKController_QA/delete_odk_unauthorized` — DELETE /api/starodk/{{odkBasic.id}} => 429 — **Expected status 401, got 429.**
- `STARODKController_QA/generate_odk_unauthorized` — POST /api/starodk/{{odkBasic.id}}/generate => 429 — **Expected status 401, got 429.**
- `STARODKController_QA/create_fresh_odk` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController_QA/deploy_before_generate` — POST /api/starodk/{{odkFresh.id}}/deploy => 429 — **Expected status 400, got 429.**
- `STARODKController_QA/delete_odk_basic` — DELETE /api/starodk/{{odkBasic.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/delete_odk_advanced` — DELETE /api/starodk/{{odkAdvanced.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/delete_odk_unicode` — DELETE /api/starodk/{{odkUnicode.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/delete_odk_fresh` — DELETE /api/starodk/{{odkFresh.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController_QA/verify_deleted_odk` — GET /api/starodk/{{odkBasic.id}} => 429 — **Expected status 404, got 429.**
- `STARODKController_QA/cleanup_avatar` — DELETE /api/avatar/{{starAvatar.id}} => 429 — **Expected status 200, got 429.**
- `STARODKController/seed_avatar` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `STARODKController/login_seed` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `STARODKController/create_odk` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController/get_odk` — GET /api/starodk/{{odk1.odkId}} => 429 — **Expected status 200, got 429.**
- `STARODKController/get_all_odks` — GET /api/starodk => 401 — **Expected status 200, got 401.**
- `STARODKController/generate_odk` — POST /api/starodk/{{odk1.odkId}}/generate => 429 — **Expected status 200, got 429.**
- `STARODKController/delete_odk` — DELETE /api/starodk/{{odk1.odkId}} => 429 — **Expected status 200, got 429.**
- `STARODKController/cleanup_avatar` — DELETE /api/avatar/{{savatar.avatarId}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_seed_avatar` — POST /api/avatar/register => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_login` — POST /api/avatar/login => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_holon_1` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_holon_2` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_holon_3` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_holon_4` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_holon_5` — POST /api/holon => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_update_1` — PUT /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_update_2` — PUT /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_update_3` — PUT /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_update_4` — PUT /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_update_5` — PUT /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_verify_holon_1` — GET /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_interact_1` — POST /api/holon/{{stressHolon1.id}}/interact => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_interact_2` — POST /api/holon/{{stressHolon1.id}}/interact => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_interact_3` — POST /api/holon/{{stressHolon1.id}}/interact => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_wallet_1` — POST /api/avatar/{{stressAvatar.id}}/wallets => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_wallet_2` — POST /api/avatar/{{stressAvatar.id}}/wallets => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_wallet_3` — POST /api/avatar/{{stressAvatar.id}}/wallets => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_get_wallets` — GET /api/avatar/{{stressAvatar.id}}/wallets => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_remove_wallet_1` — DELETE /api/avatar/{{stressAvatar.id}}/wallets/{{stressWallet1.walletId}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_remove_wallet_2` — DELETE /api/avatar/{{stressAvatar.id}}/wallets/{{stressWallet2.walletId}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_remove_wallet_3` — DELETE /api/avatar/{{stressAvatar.id}}/wallets/{{stressWallet3.walletId}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_get_wallets_empty` — GET /api/avatar/{{stressAvatar.id}}/wallets => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_odk_1` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_odk_1_gen` — POST /api/starodk/{{stressODK1.id}}/generate => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_odk_1_deploy` — POST /api/starodk/{{stressODK1.id}}/deploy => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_odk_1_del` — DELETE /api/starodk/{{stressODK1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_odk_2` — POST /api/starodk => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_odk_2_gen` — POST /api/starodk/{{stressODK2.id}}/generate => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_odk_2_deploy` — POST /api/starodk/{{stressODK2.id}}/deploy => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_odk_2_del` — DELETE /api/starodk/{{stressODK2.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_query_1` — GET /api/holon?name=Stress => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_query_2` — GET /api/holon?name=StressHolon => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_query_3` — GET /api/holon?name=NonExistentStress => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_get_all_avatars` — GET /api/avatar => 401 — **Expected status 200, got 401.**
- `Stress_RapidOperations/stress_del_holon_2` — DELETE /api/holon/{{stressHolon2.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_del_holon_3` — DELETE /api/holon/{{stressHolon3.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_del_holon_4` — DELETE /api/holon/{{stressHolon4.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_del_holon_5` — DELETE /api/holon/{{stressHolon5.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_del_holon_1` — DELETE /api/holon/{{stressHolon1.id}} => 429 — **Expected status 200, got 429.**
- `Stress_RapidOperations/stress_cleanup_avatar` — DELETE /api/avatar/{{stressAvatar.id}} => 429 — **Expected status 200, got 429.**

