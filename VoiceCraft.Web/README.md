# VoiceCraft.Web: join from a browser

`VoiceCraft.Web` lets players join a VoiceCraft server from a web page, with nothing to install.
That covers phones next to a console (Xbox, PlayStation, Switch) and PCs, where the native apps
aren't available or are a hassle to sideload.

## How it works

```
browser ──wss──▶ reverse proxy (HTTPS) ──▶ VoiceCraft.Web ──UDP──▶ VoiceCraft.Server
  mic (Opus/PCM) ─────────────────────────▶ headless VoiceCraftClient per browser
  speakers ◀── one mixed stereo stream ◀── client.Read(): proximity, direction, muffle…
```

For each browser, the bridge runs a headless `LiteNetVoiceCraftClient`, the same client class the
native apps use. To the server and the Minecraft add-on it is an ordinary client: it gets an entity,
a binding key (`/vcbind <key>`), and all the usual effects. The client's own `AudioEffectSystem`
does the mixing on the bridge, so the browser only records the microphone and plays one stream.
No changes to the server, the add-ons or the protocol.

- **Audio:** Opus through WebCodecs where the browser supports it (32 kbps up, 64 kbps stereo
  down), with a fallback to 16 kHz 16-bit PCM (about 256 kbps each way, mono). The browser applies
  its own echo cancellation, noise suppression and auto gain.
- **Pacing:** one clock thread ticks every 20 ms for all sessions. Silence isn't sent. The browser
  plays through a jitter buffer (60 ms prefill, capped at 200 ms so delay can't build up).
- **Reconnects:** if the VoiceCraft server restarts, the page reconnects by itself with backoff.
  The binding key changes on every connection, as it does in the native apps.

## Running it

```bash
VoiceCraft.Web --listen http://127.0.0.1:9060/ --server-host 127.0.0.1 --server-port 9050
```

| Option | Default | |
|---|---|---|
| `--listen`, `-l` | `http://127.0.0.1:9060/` | HTTP prefix. WebSocket at `/ws`, status at `/health`. |
| `--server-host`, `-sh` | `127.0.0.1` | VoiceCraft server to join. |
| `--server-port`, `-sp` | `9050` | Its UDP port. |
| `--max-sessions`, `-m` | `20` | Simultaneous browsers. Each one is also a client on the server, so keep the server's `MaxClients` above this. |
| `--allowed-origin`, `-o` | any | Only accept WebSockets from these `Origin`s (repeatable). |
| `--no-client` | off | Don't serve the bundled page; only `/ws` and `/health`. |

Browsers only allow the microphone on HTTPS (or `localhost`), so put a TLS reverse proxy in front.
Caddy example, serving the bridge under `/voz/`:

```
example.com {
	handle_path /voz/* {
		reverse_proxy 127.0.0.1:9060
	}
}
```

The single-file build extracts its native Opus library on first start. Under a hardened systemd
unit, point `DOTNET_BUNDLE_EXTRACT_BASE_DIR` at a writable directory.

## Using the client library on your own page

The bridge serves `voicecraft-web.js` (an ES module) and `voicecraft-worklet.js`. `index.html` is
a minimal example page. To build your own:

```js
import { VoiceCraftWeb } from '/voz/voicecraft-web.js';

const vc = new VoiceCraftWeb({ url: 'wss://example.com/voz/ws' });
vc.addEventListener('bindingkey', (e) => showCommand(`/vcbind ${e.detail.key}`));
vc.addEventListener('bound', (e) => showLinked(e.detail.name));
vc.addEventListener('state', (e) => showState(e.detail.state, e.detail.reason));
button.onclick = () => vc.start(); // from a click: browsers need a gesture for the mic and audio
```

Events: `state`, `bindingkey`, `bound`, `description`, `title`, `speaking`, `level`, `peers`,
`muted`, `deafened`, `serverMuted`, `serverDeafened`, `error`. Methods: `start()`, `stop()`,
`setMuted()`, `setDeafened()`, `setVolume(0..2)`, `setSensitivity(0..1)`. `vc.stats` has frame
counters for diagnostics. For testing without a microphone, the example page takes `?tone=1`
(sends a 440 Hz tone) and `?codec=pcm16` (forces the fallback).

## WebSocket protocol

Text frames are JSON `{"t": "<type>", …}`; binary frames are one 20 ms audio frame.

- **Browser → bridge:** `hello {codec: "opus"|"pcm16", user, server, locale}` (first message;
  `user`/`server` are GUIDs the page keeps in `localStorage`), `mute {value}`, `deafen {value}`,
  `volume {value}`, `inputVolume {value}`, `sensitivity {value}`. Audio: an Opus packet (48 kHz mono)
  or 320 little-endian int16 samples (16 kHz).
- **Bridge → browser:** `state {state: connecting|connected|disconnected, reason?}`,
  `description {text}` (contains the binding key, then "Bound to player …"), `title {text}`,
  `speaking`/`muted`/`deafened`/`serverMuted`/`serverDeafened {value}`,
  `peers {list: [{id, name, speaking}]}` (visible entities, sent when it changes). Audio: an Opus
  packet (48 kHz stereo) or 320 int16 samples (16 kHz mono).

## Limitations

- **iPhone/iPad:** iOS pauses the microphone when Safari goes to the background, so someone
  playing Minecraft on the same iPhone can't use the web client. A phone next to a console or PC
  works.
- **Android:** Chrome keeps the microphone running in the background (with a notification); the
  page also holds a screen wake lock while connected.
- **Client positioning:** if the server uses client-sided positioning (McWss), web clients have no
  way to send positions. Use server positioning (McHttp/McTcp add-ons).
