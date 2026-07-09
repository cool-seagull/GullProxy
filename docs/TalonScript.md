# TalonScript

TalonScript is a small scripting language built into **GullProxy → Talon**. You embed it in a
request written in [TalonFormat](#talonformat-recap) to compute values, read data out of
responses, and chain requests together (e.g. log in, capture a token, reuse it). It has its own
hand-written interpreter — no JavaScript, no external engine.

You write TalonScript inside script blocks in the Talon editor:

```
< {%   … pre-request script …   %}
> {%   … post-response script …  %}
```

- `< {% %}` runs **before** the request is sent. It can read/modify `request` and set `vars`.
- `> {% %}` runs **after** the response arrives. It can read `response` and set `vars`.

Variables you set persist for the rest of your Talon session, so the next request can use them.

---

## A quick example

```
@host = https://api.example.com

POST {{host}}/login
Content-Type: application/json

{ "user": "alice", "pass": "hunter2" }

> {%
  # pull the token out of the JSON response and save it
  token = response.json().access_token
  log "logged in, token =", token
%}
```

Then in your next request:

```
GET {{host}}/me
Authorization: Bearer {{token}}
```

`{{token}}` is filled in from the variable the script set.

---

## Getting a captured request into a script

In **Live Capture**, right-click any request and choose **Copy as TalonScript**. You get the
statements that rebuild it on the `request` object, ready to paste into a `< {% … %}` block:

```
request.method = "POST"
request.url = "https://api.example.com/login"
request.headers["Content-Type"] = "application/json"
request.body = "{\"user\":\"alice\"}"
```

(There's also **Copy as TalonFormat** for the plain declarative form, and **Send to Talon** to
open it in the editor directly.)

## Language reference

### Comments
```
# line comment
// line comment
```

### Values
`null`, booleans (`true` / `false`), numbers (`42`, `3.14`), strings (`"hi"` or `'hi'`),
lists (`[1, 2, 3]` come from JSON), and objects (from JSON). Numbers are 64-bit floats.

### Variables & assignment
Bare names read and write **session variables** (the `vars` bag):

```
count = 5
name  = "alice"
count = count + 1
```

You can also assign into objects and lists:

```
request.url = request.url + "?debug=1"
request.headers["X-Trace"] = uuid()
vars.token = "abc"          # same as: token = "abc"
```

### Operators
```
+  -  *  /  %             arithmetic  ( + also concatenates strings )
==  !=  <  >  <=  >=      comparison
and  or  not              logical
```
`"Bearer " + token` concatenates. `and` / `or` short-circuit.

### if / else
```
if response.status == 200 {
  log "ok"
} else if response.status >= 500 {
  log "server error"
} else {
  log "other:", response.status
}
```

### Loops
```
for item in [1, 2, 3] {      # iterate a list, object keys, or string chars
  log item
}

while count < 10 {
  count += 1
  if count == 5 { continue }
  if count > 8 { break }
}

repeat 3 { log "tick" }       # fixed count
```

### List & object literals
```
nums = [1, 2, 3]
user = { name: "alice", roles: ["admin", "dev"] }
log user.name, user.roles[0]
```

### Compound assignment
```
total += 5      #  also  -=  *=  /=
```

### Member & index access
```
response.json().data.items[0].id
response.headers["Content-Type"]
mylist[2]
```

### Function calls
```
response.json()
upper("hi")
```

---

## Built-in objects

### `request` (available in pre-request scripts)
| Field | Meaning |
|-------|---------|
| `request.method` | method string, e.g. `"POST"` |
| `request.url` | full URL (before `{{var}}` substitution) |
| `request.headers` | object of header name → value (assignable) |
| `request.body` | request body string |

Anything you change here is used for the actual request.

### `response` (available in post-response scripts)
| Field | Meaning |
|-------|---------|
| `response.status` | numeric status code, e.g. `200` |
| `response.statusText` | reason phrase |
| `response.headers` | object of response headers |
| `response.body` | raw body string |
| `response.json()` | parse `body` as JSON into objects/lists |

### `vars`
The variable bag. `vars.foo` and bare `foo` are the same thing. Values persist across sends in
the current Talon session, and are what `{{foo}}` substitutes in the request.

---

## Built-in functions

**Math** — `abs floor ceil round(x[,digits]) trunc sqrt pow(a,b) exp ln log10 sign
sin cos tan atan atan2 min(…) max(…) clamp(x,lo,hi) random() randomInt(a,b) parseInt parseFloat`,
plus constants `PI`, `E`, `TAU`.

**Strings & regex** — `upper lower trim len contains startsWith endsWith indexOf replace
split(s,sep) join(list,sep) substring(s,a[,b]) repeat(s,n) padStart(s,n[,ch]) padEnd chars
regexTest(s,re) regexMatch(s,re) regexReplace(s,re,to) regexAll(s,re) urlEncode urlDecode`.

**Crypto & encoding** — `md5 sha1 sha256 hmacSha256(msg,key) base64 base64decode base64url hex`.

**Collections** — `list(…) range(a,b) push(l,x) first last reverse sort sum slice(l,a[,b])
keys values has(o,k) get(o,k[,def]) merge(a,b) object()`.

**Data & util** — `str num bool type(x) isNull(x) default(x,y) json(s) jsonStringify(x)`.

**Time & misc** — `now() timestamp() timestampMs() uuid() env(name)`.

---

## More examples

**Add an auth header only when a token exists**
```
< {%
  if not (token == null) {
    request.headers["Authorization"] = "Bearer " + token
  }
%}
```

**Build a request body in code**
```
< {%
  request.body = str({ "id": uuid(), "ts": now() })
  request.headers["Content-Type"] = "application/json"
%}
```

**Extract a nested value and a header**
```
> {%
  vars.userId = response.json().data.user.id
  vars.rateLimit = response.headers["X-RateLimit-Remaining"]
  log "user", userId, "remaining", rateLimit
%}
```

`log` output appears in Talon's **Console** tab.

---

## Notes & limits

- Scripts are sandboxed to your machine's network stack (they only run HTTP through Talon's
  Send). There is an operation limit to stop runaway loops.
- There are no user-defined functions in this version — keep scripts short and linear.
- `response.json()` returns `null` if the body isn't valid JSON.

## TalonFormat recap

A request document is: optional `@name = value` variables, a `METHOD url` line, header lines,
a blank line, then the body — plus any `< {% %}` / `> {% %}` script blocks. See the app's
built-in template (the Talon tab) for a working starting point.
