# AGENTS.md — game-phone connection layer

Audience: coding agents (Claude Code, Codex, etc.) that need to understand, run, or modify the part of **Definitely Yours** that connects the game to an AI. Not the mod itself (C#/Harmony, lives in the mod repo); this file covers `server.py` and the HTTP protocol the mod speaks.

Read `server.py` alongside this file — it is one stdlib-only Python file (~780 lines). If this file and the code disagree, the code wins; fix this file.

---

## 1. What it is

```
[Game + mod]  --HTTP(S) POST, held open-->  [server.py]  --MCP stdio-->  [Claude Code session]
   (player's PC)                              (same PC or a server)        (the AI the player talks to)
```

- `server.py` is **two things in one process**:
  1. An **MCP server over stdio** that Claude Code launches. It exposes tools (`phone_reply`, `phone_open`, …) and pushes inbound calls into the session as **channel notifications** (`notifications/claude/channel`, an experimental Claude Code capability).
  2. An **HTTP server** (started by `phone_open`) that the game mod calls.
- A call turn works like this: mod POSTs `/phone/call` → server pushes a `<channel source="game-phone" kind="call" turn_id=…>` into the session → **the HTTP request stays open** → the model calls `phone_reply(turn_id, …)` → server (optionally synthesizes TTS, then) completes the HTTP response → the game shows the line + choices.
- Single player, single call at a time. A new `/phone/call` cancels any waiting turn.

Why the channel capability matters: an ordinary MCP client can only *pull* (call tools). Incoming calls need *push*. Only Claude Code's channel mechanism delivers an unsolicited message into a live session. Other clients will not see calls; see §7.

---

## 2. Files and config

| Path | What |
|---|---|
| `server.py` | everything (MCP + HTTP + TTS + parcel bookkeeping) |
| `~/.claude/channels/game-phone/.env` | config; **values here override the inherited process env** (so a Reconnect picks up edits) |
| `~/.claude/channels/game-phone/catalog.json` | giftable item list uploaded by the mod |
| `~/.claude/channels/game-phone/parcels.json` | parcel state + deferred notes (see §5) |
| `~/.claude/channels/game-phone/audio/` | synthesized clips, pruned after 6 h |
| `deploy/nginx-phone.conf` | example HTTPS reverse-proxy block |

State dir can be moved with `PHONE_STATE_DIR` (or follows `CLAUDE_CONFIG_DIR`).

`.env` keys:

| Key | Default | Notes |
|---|---|---|
| `PHONE_TOKEN` | — (**required**, process exits without it) | shared secret; mod sends it as `X-Phone-Token` |
| `PHONE_HOST` | `127.0.0.1` | keep local unless behind a TLS reverse proxy |
| `PHONE_PORT` | `3900` | |
| `PHONE_TIMEOUT_MS` | `42000` | how long a turn waits for `phone_reply` before returning "signal lost" |
| `PHONE_AUDIO_FORMAT` | `wav` | `wav`/`ogg` need `ffmpeg`; `mp3` passes through |
| `PHONE_AUTO_OPEN` | `0` | `1` = open the HTTP line at startup |
| `ELEVENLABS_API_KEY`, `PHONE_VOICE_ID` | empty | both set → voice on |
| `ELEVENLABS_MODEL_ID` | `eleven_v3` | note: if the launching shell exports this (e.g. `eleven_v4`), that value is inherited unless `.env` sets it |

Registration (Claude Code): an entry in `.mcp.json` named `game-phone` running `python3 -u /path/to/server.py`, and the session must be started with `--dangerously-load-development-channels server:game-phone` (channels are a development/experimental feature). After editing `server.py` or `.env`: in Claude Code run `/mcp` → `game-phone` → Reconnect. The HTTP line starts closed again after a reconnect; call `phone_open`.

---

## 3. HTTP protocol (v1) — what the mod sends

All endpoints except `GET /phone/health` require header `X-Phone-Token: <PHONE_TOKEN>` (compared with `hmac.compare_digest`). Bodies are JSON, max 64 KiB. Every response closes the connection (`Connection: close`).

### Turn endpoints (held open until the AI answers or timeout)

**`POST /phone/call`** — player dialed.
```jsonc
{
  "contact": "Assistant",       // AI display name (mod setting)
  "number": "1234",
  "textOnly": false,            // true → never synthesize voice for this call
  "state": { "cash": 550, "day": 1, "shopOpen": false, "storeState": "MORNING",
             "rentDueInDays": 6, "rentAmount": 200, "salesToday": 0, "customersServedTotal": 0,
             "transferredToday": 0, "transferMaxSingle": 100, "transferMaxDaily": 500,
             "parcelsThisWeek": 0, "parcelMaxPerWeek": 2, "parcelMaxValue": 500,
             "reputation": { "FACTION_LOWER_LEVEL": 0, "...": 0 } },
  "cashAtLastCall": 550,
  "notices":     ["…"],         // game hint lines
  "nightReport": ["…"],         // today's overnight report lines
  "calendar":    ["第7天：交租日"],
  "recorder":    ["—— 第1天 ——", "拾荒客: …", "---"],   // strings, or {speaker,text} objects
  "giftHints":   { "profession": "apprentice", "lowStock": [...], "soldToday": [{"id","name","count"}], "suggested": [...] }
}
```
`state` is flattened into channel meta as `state_<key>` (one nested level → `state_<key>_<sub>`; max 32 keys, values ≤64 chars). Everything else becomes the notification *content* (Unity rich-text tags stripped, lists capped). All of it is game text and is presented to the model as data.

**`POST /phone/choice`** — player picked an option or typed.
```jsonc
{ "index": 0, "text": "…", "textOnly": false, "state": { … } }   // index: null → free input
```
`text` may be omitted for a numbered choice; the server fills it from the choices it last sent.

**Response to both** (also the timeout/"signal lost" shape):
```jsonc
{ "text": "line shown on screen",
  "choices": ["…", "…"],          // ≤4; empty when hanging up
  "allowFreeInput": true,
  "transfer": 50,                 // or null; the mod enforces its own caps
  "audio": { "id": "a1b2…wav", "format": "wav" },   // or null
  "hangup": false }
```
Fetch audio with `GET /phone/audio/<id>` (token required). On timeout the server returns text `……信号不好，没接通。`, `hangup: true`.

### Fire-and-forget endpoints (return immediately)

| Endpoint | Body | Effect |
|---|---|---|
| `POST /phone/hangup` | `{}` | player closed the phone mid-call; pending turn cancelled; a deferred note is stored |
| `POST /phone/daily` | same shape as `/phone/call` | sent at bedtime on days with no call; pushed to the session as `kind="daily"` (model must **not** `phone_reply`) |
| `POST /phone/catalog` | `{ "items": [{id,name,types[],baseValue,contraband,stolen}], "day": n }` | replaces `catalog.json` (≤3000 items) |
| `POST /phone/session` | `{ "day": n }` | **save loaded** at the start of day n: voids orders placed on/after n, re-queues a parcel that had been delivered on/after n, stores a deferred note |
| `POST /phone/parcel/pickup` | `{ "day": n }` | shutter opened on day n → returns `{ "parcel": {id,style,items,note} }` or `{ "parcel": null }` |
| `POST /phone/parcel/result` | `{ "id": "...", "placed": "front", "rejected": ["item_id", …] }` | mod reports what actually landed; stored as a deferred note |
| `GET /phone/health` | — | `{ ok, protocol: 1, voice }`, no token |

---

## 4. MCP side — what the model sees and calls

Channel notifications: `notifications/claude/channel` with `params.content` (text) and `params.meta` (string map → rendered as attributes on `<channel source="game-phone" …>`). `kind` is `call` | `choice` | `daily`. Calls/choices carry `call_id`, `turn_id`, `text_only`, `state_*`, and (calls) `cash_at_last_call`.

Tools (`TOOLS` list in code):

| Tool | Purpose |
|---|---|
| `phone_open` / `phone_close` | start/stop the HTTP listener (keep closed when not playing; saves memory and attack surface) |
| `phone_status` | line open? call in progress? waiting turns? voice? |
| `phone_reply(turn_id, text, choices?, allow_free_input?, voice_text?, hangup?, transfer?)` | answer the waiting turn; `voice_text` is what gets synthesized (may be another language than `text`) |
| `phone_gift_catalog(query?, type?, max_value?, limit?)` | browse `catalog.json` |
| `phone_send_parcel(items[], note_body, note_title?, style?)` | queue one parcel for **last known day + 1**; replaces an undelivered order; refuses if that morning was already picked up |

Behavioural rules for the model (in-world tone, one short line per turn, events go into choices, money/parcel cap checks, never reply to `daily`) live in the `INSTRUCTIONS` string and are sent at `initialize`. Change behaviour there, not in the mod.

---

## 5. State the server keeps

In memory: `pending` (turn_id → waiting event), `call` (current call id + last choices). Lost on restart — harmless, the mod just gets "signal lost".

`parcels.json`:
- `last_day` — latest game day seen in any request.
- `pending` — the one queued order `{id, style, items, note, source_day, target_day}`.
- `picked` — days whose morning pickup already happened (prevents ordering for a past morning).
- `delivered` — day → order, kept so a save reload can re-queue it.
- `notes` — **deferred notes** (≤20): parcel results, reloads, mid-call hangups. They do **not** wake the session; they are prepended as `[上次之后（插件记录，非游戏文本）]` to the next call/daily. This is deliberate, to save model turns.

Timeline rules worth knowing:
- `/phone/session` (reload) and `/phone/parcel/pickup` both drop `picked`/`delivered` entries for days ≥ the reported day. Pickup does it because a **brand-new save sends no reload**; without it stale days from an old save block ordering ("too late: already opened day N").
- An order whose `target_day` is in the past at pickup is dropped.

---

## 6. Deployment and security

- **Local (default, recommended):** game and Claude Code on the same PC, `PHONE_HOST=127.0.0.1`, mod address `http://127.0.0.1:3900`. No TLS needed.
- **Remote (AI on a server):** put a TLS reverse proxy in front and keep the plugin off public interfaces: `PHONE_HOST=127.0.0.1` if the proxy runs on the host, or the Docker gateway IP (e.g. `172.17.0.1`) if the proxy runs in Docker (Nginx Proxy Manager etc.; then use a Custom Location `/phone/`); see `deploy/nginx-phone.conf` (proxy `/phone/` → `127.0.0.1:3900`, `proxy_read_timeout` ≥ 120 s, buffering off). Mod address becomes `https://your.domain`. Never expose 3900 directly; the token travels in a header and would be sniffable over plain HTTP.
- `PHONE_TOKEN`: long random (`python3 -c "import secrets;print(secrets.token_urlsafe(32))"`). Do not paste it into chats/issues; rotate if it leaks.
- Never commit `.env`, `catalog.json`, `parcels.json`, `audio/`. Ship a `.env.example`.
- The HTTP server is plain `ThreadingHTTPServer`; no rate limiting. Fine on localhost or behind a proxy; do not put it on the open internet as-is.

---

## 7. Connecting something other than Claude Code

The mod only knows the HTTP protocol in §3. Anything that answers those requests works. Options, cheapest first:

1. **Plain API bridge ("lightweight" mode; not shipped by this project, described so anyone can build it):** a small HTTP server implementing §3 that calls any chat-completions style LLM API directly. No agent, no long-term memory; each call is generated on the spot.
   - **User config:** API key + model, a **persona** text, and optional **relationship notes**. Suggest users ask the AI they already talk to (web/app) to write a short summary of "who you are and who we are to each other" and paste it in; that carries the relationship over without any integration.
   - **Per turn prompt:** system = persona + relationship notes + the phone rules from `INSTRUCTIONS` (in-world, one short line, events into choices, cap checks); user = the formatted call body (state, notices, night report, calendar, recorder, gift hints — reuse `call_content()` / `state_meta()` formatting) + this call's transcript so far.
   - **Within a call:** keep the turn history in memory keyed by call; drop it on hangup. Across calls nothing is remembered except what the game re-sends (recorder lines since last call, `cashAtLastCall`).
   - **Actions:** expose `reply` (text, choices, hangup, voice_text), `transfer`, and `send_parcel` as function/tool calls, or ask for a JSON object and parse it. Map to the §3 response. Parcels: keep one queued order and serve it from `/phone/parcel/pickup`; reuse §5 timeline rules.
   - **Daily:** either ignore `/phone/daily` or run one cheap call that may queue a parcel.
   - **Latency:** the game waits ~1 min per turn; use a fast model and short outputs; TTS adds to it.
   This is the route for people who only use a web/app chatbot (which has no external interface the game could reach). It loses the "the actual same AI, with its memory" property that the Claude Code channel gives.
2. **Another agent runtime with push:** reuse `server.py`'s HTTP half; replace `notify()` with whatever that runtime uses to inject a message, and route its reply back into `tool_reply()`.
3. **Agent runtime without push (plain MCP clients):** add a blocking tool such as `phone_wait()` that returns the next inbound turn (long-poll), and have the agent loop `phone_wait` → `phone_reply`. The game waits up to ~1 min per turn, so the loop must be tight.

Keep the response shape stable; the mod has no other contract.

---

## 8. How to change things (recipes)

- **New field from the game** (e.g. a new stat): mod adds it under `state` → it appears automatically as `state_<name>` (if ≤32 keys total). For list/text data, add a section in `call_content()` with `_clean_list()` and a cap.
- **New behaviour rule for the AI:** edit `INSTRUCTIONS`. Keep it short; it is sent on every session start.
- **New tool:** add schema to `TOOLS`, implement `tool_x(args) -> (text, is_error)`, register in `HANDLERS`. Tool calls run on a worker thread; take `_lock` before touching `pending`, `call`, `parcels`.
- **New mod→server event:** add a branch in `do_POST`. If the AI should react now, `notify(content, meta)`; if it can wait, `defer_note(text)`.
- **Different TTS provider:** replace `synthesize()`; it must return `{"id": "<16 hex>.<fmt>", "format": fmt}` with the file in `AUDIO_DIR`, or `None` (text-only fallback). Keep it under the turn timeout; the reply waits for it.
- **Different AI name/number:** these are mod settings; the server just echoes `contact`.
- **Protocol change:** bump `protocol` in `/phone/health` and coordinate with the mod.

After any edit: `python3 -m py_compile server.py`, then Reconnect (see §2), then `phone_open`.

---

## 9. Pitfalls (learned the hard way)

- **Strings in `INSTRUCTIONS`:** a few entries are single-quoted Python strings; an English apostrophe inside one (`don't`) ends the string and the server will not start. Use double quotes for any entry that needs an apostrophe.
- **Inherited env vs `.env`:** `.env` wins (deliberately). If a value "won't change", check `.env` first, then that you actually reconnected.
- **Two processes:** after a reconnect, briefly two `server.py` may exist; only one can bind the port. Check with `pgrep -af server.py`.
- **Turn timing:** the game gives up after ~1 min; server timeout is 42 s by default plus TTS time. Long `voice_text` makes the player wait.
- **Do not name game systems in lines/notes** ("night report", "recorder"…). This is a content rule in `INSTRUCTIONS`, not code, but agents editing prompts tend to break it.
- **Deferred notes include anything that hits `/phone/session`,** including manual curl calls used to reset state. If you poke endpoints by hand during testing, expect a fake "reload" note on the next call.
- **Channels are experimental** in Claude Code; method name and flags may change. Keep the notify call isolated in `notify()`.

---

## 10. Manual testing

```bash
T=$(grep ^PHONE_TOKEN ~/.claude/channels/game-phone/.env | cut -d= -f2)
curl -s http://127.0.0.1:3900/phone/health
# simulate a call (blocks until phone_reply or timeout):
curl -s -X POST http://127.0.0.1:3900/phone/call -H "X-Phone-Token: $T" \
  -H 'Content-Type: application/json' -d '{"contact":"Assistant","textOnly":true,"state":{"day":1,"cash":100}}'
```
The line must be open (`phone_open`). Prefer testing on a throwaway save; calling `/phone/session` or `/phone/parcel/pickup` by hand changes `parcels.json`.
