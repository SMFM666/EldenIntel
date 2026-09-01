# Elden Intel Interact

Private Twitch video-overlay extension for EldenIntel. Viewers can submit
allowlisted effects, see the exact queue order, and track active-effect timing.
The broadcaster controls the queue from the Live Config page.

Extension Client ID: `uokh78i6q53c9ozc7fwex439qdx3li`

## Current 0.0.1 effects

- Shift Time
- Heal Player
- Life Steal (nonlethal, 15% maximum HP)
- Slow World (55% speed for 8 seconds)
- Random Effect (selects from the safe enabled pool)
- Summon a Boss (one Auto Balanced spawn, five-minute shared cooldown)
- Spawn Clone (a shared 20-second Help or Hurt vote with a three-minute cooldown)

Summon a Boss opens a viewer search popover backed by EldenIntel's verified
210-entry boss catalog. Viewers must choose an exact search result; arbitrary
names are rejected by both the relay and desktop app. Only one summon can be
queued or active at a time.

## Architecture

The hosted overlay sends Twitch-signed viewer requests to the EldenIntel relay.
The relay validates the Twitch JWT, role, effect allowlist, queue capacity,
cooldown, and per-viewer rate limit. EldenIntel maintains an authenticated local
consumer connection and performs the approved effect. The relay reports the
authoritative queue and armed state back to viewers.

The relay is bundled with the portable app and starts automatically. The relay
secret is stored for the Windows user with DPAPI and is never included in the
extension ZIP.

## Build and package

```powershell
dotnet test ..\tests\EnemyIntel.Tests\EnemyIntel.Tests.csproj -c Release
dotnet publish .\EldenIntel.Interact.Host.csproj -c Release -o ..\release\EldenIntelV1-portable\TwitchRelay
powershell -ExecutionPolicy Bypass -File .\scripts\Package-TwitchExtension.ps1
```

The upload artifact is written to `dist\EldenIntel-Interact-0.0.1.zip`.

## Pre-review requirements

The production relay is `https://ei-relay.somber-made.com`. Before review, add
that HTTPS/WSS origin to the Twitch Capabilities allowlist, rotate the Extension
secret, complete a Hosted Test from Twitch's CDN, and finish
`review\Submission-Checklist.md`.
