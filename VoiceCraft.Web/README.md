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
No changes to the server, the add-ons or the protocol (only the optional
[global channel](#global-channel-server-option) needs a server built from this fork).

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

Events: `state`, `bindingkey`, `linked`, `bound`, `description`, `title`, `speaking`, `level`,
`peers`, `muted`, `deafened`, `serverMuted`, `serverDeafened`, `global`, `microphone`, `speaker`,
`error`. Methods: `start()`, `stop()`, `setMuted()`, `setDeafened()`, `setVolume(0..2)`,
`setInputVolume(0..2)`, `setSensitivity(0..1)`, `setProcessing({...})`, `setGlobal({talk, listen})`,
`setMicrophone(deviceId)`, `setSpeaker(deviceId)`, and the static `VoiceCraftWeb.forgetDevice()` and
`VoiceCraftWeb.listDevices()`. `vc.stats` has frame counters for diagnostics.

### Microphone level and processing

- **Input volume** (`inputVolume` option, `setInputVolume(0..2)`) is a gain in the browser, before the
  level meter and before the bridge's voice activation, so both see what the others will hear.
- **Voice activation** (`sensitivity` option, `setSensitivity(0..1)`, default 0.04): the bridge sends
  your voice while a 20 ms frame's peak reaches it. `level {rms, peak}` reports both for each frame, so a
  page can draw the threshold on its meter (on a −60…0 dB scale, for example).
- **Browser processing** (`processing` option, `setProcessing({echoCancellation, noiseSuppression,
  autoGainControl})`, all on by default): changing it reopens the microphone.
- **Output volume** (`volume` option, `setVolume(0..2)`) is applied by the bridge.

### Choosing the microphone and speaker

`VoiceCraftWeb.listDevices()` returns `{microphones, speakers}` as `{deviceId, label}` (labels need
microphone permission, so call it after `start()`). Pass `microphoneId`/`speakerId` in the options, or
call `setMicrophone()`/`setSpeaker()` at any time; `''` is the system default. The microphone switches
live, and if it's unplugged the library carries on with the default. The speaker uses
`AudioContext.setSinkId`, so it only works where `VoiceCraftWeb.canChooseSpeaker` is true (Chrome and
Edge); elsewhere the browser plays to the system's output. `microphone {deviceId, label, fallback}` and
`speaker {deviceId, fallback}` report the device in use; `fallback` means the chosen one wasn't
available.

### Remembered devices

The page keeps a random `ServerUserGuid` per browser in `localStorage`. VoiceCraft sends that id
only to the server and the Minecraft add-on, never to other clients, so an add-on can use it to
remember a device after its first `/vcbind` and bind it automatically afterwards. When an add-on
does, it describes a known-but-waiting device as `Linked to player <name>. Waiting for them to join.
Your binding key is <key>`; the library reports that as `linked {name}` and
`bindingkey {key, linkedName}`. `forgetDevice()` drops the ids, so the next connection is a new device. For testing without a microphone, the example page takes `?tone=1`
(sends a 440 Hz tone) and `?codec=pcm16` (forces the fallback).

## Global channel (server option)

A server built from this fork can run a global voice channel next to proximity chat. Set
`"GlobalChannelBitmask": 16` in `config/ServerProperties.json`: a talk/listen bit that no audio effect
uses (the default effects use 1, 2, 4 and 8). The server's `GlobalChannelSystem` then manages every
client's talk and listen bitmasks:

- **Talking on the channel** (`setGlobal({talk: true})`): everyone listening to it hears you at full
  volume, at any distance and in any world, without effects. You still hear players near you.
- **Listening** (on by default; `setGlobal({listen: false})` turns it off): hear whoever talks on it.
  Someone who turned it off doesn't hear a global talker even next to them.
- **Only linked players** (an entity with a world id, which the Minecraft add-on sets while the player
  is in the game) get the channel, so a device that hasn't bound can't listen in.

Clients ask for it through two entity properties, `GlobalChannel:Talk` and `GlobalChannel:Listen`, which
the server lets clients set on themselves even with server positioning. The native apps don't set them,
so their players talk with proximity and hear the channel. The server echoes the accepted values back,
and the library reports them as `global {talk, listen}`, so a page can offer the choice only when the
server supports it. With the option off (the default), or on an unmodified server, nothing changes.

## WebSocket protocol

Text frames are JSON `{"t": "<type>", …}`; binary frames are one 20 ms audio frame.

- **Browser → bridge:** `hello {codec: "opus"|"pcm16", user, server, locale}` (first message;
  `user`/`server` are GUIDs the page keeps in `localStorage`), `mute {value}`, `deafen {value}`,
  `volume {value}`, `inputVolume {value}`, `sensitivity {value}`, `global {talk?, listen?}`. Audio: an
  Opus packet (48 kHz mono) or 320 little-endian int16 samples (16 kHz).
- **Bridge → browser:** `state {state: connecting|connected|disconnected, reason?}`,
  `description {text}` (contains the binding key, then "Bound to player …"), `title {text}`,
  `speaking`/`muted`/`deafened`/`serverMuted`/`serverDeafened {value}`,
  `peers {list: [{id, name, speaking, global}]}` (visible entities, sent when it changes),
  `global {talk, listen}` (what the server applied; see [Global channel](#global-channel-server-option)).
  Audio: an Opus packet (48 kHz stereo) or 320 int16 samples (16 kHz mono).

## Limitations

- **iPhone/iPad:** iOS pauses the microphone when Safari goes to the background, so someone
  playing Minecraft on the same iPhone can't use the web client. A phone next to a console or PC
  works.
- **Android:** Chrome keeps the microphone running in the background (with a notification); the
  page also holds a screen wake lock while connected.
- **Client positioning:** if the server uses client-sided positioning (McWss), web clients have no
  way to send positions. Use server positioning (McHttp/McTcp add-ons).
