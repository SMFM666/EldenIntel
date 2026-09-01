# Elden Intel Interact — Review Walkthrough

Review channel: https://www.twitch.tv/seth_gets_gud

Version: 0.0.1

## Purpose

Elden Intel Interact is a Video Fullscreen Extension for the authorized
broadcaster's Elden Ring stream. Viewers open the INTERACT launcher, request a
safe game effect, and see the authoritative shared queue update in real time.
The broadcaster runs the companion EldenIntel desktop application locally.

## Reviewer walkthrough

1. Open the review channel and enable the video overlay.
2. Select the glowing INTERACT launcher in the lower-left corner.
3. Confirm the overlay reports `ELDENINTEL ONLINE` and `ARMED`.
4. Request HEAL PLAYER, SHIFT TIME, LIFE STEAL, SLOW WORLD, or RANDOM EFFECT.
5. Select SUMMON A BOSS, search for a boss, and choose one verified result.
6. Confirm the selected boss appears in EFFECT QUEUE with its queue position.
7. Confirm the active item moves to LIVE and its progress bar advances.
8. Select SPAWN CLONE and confirm the shared 20-second HELP OR HURT vote opens.
9. Cast one HELP vote and confirm the live HELP total increments immediately.
10. When HELP wins, confirm one `ALLY CLONE · HELP` action enters the queue and
    the ally clone appears in the live game.
11. After the shared cooldown expires, repeat the vote with HURT and confirm one
    `ENEMY CLONE · HURT` action enters the queue and the enemy clone appears.
12. Open the broadcaster Live Configuration view.
13. Select PAUSE INTERACTIONS and verify viewer effect cards become unavailable.
14. Select ARM INTERACTIONS and verify requests are available again.
15. Add a request and select CLEAR QUEUE to verify queued items are removed.

## Safety behavior

- Public requests require a valid Twitch Extension JWT.
- A private machine-local key protects desktop effect delivery.
- The queue holds at most eight effects.
- Duplicate, per-effect, and per-viewer cooldowns are enforced by the server.
- Requests are rejected while EldenIntel is offline or interactions are paused.
- SPAWN CLONE accepts one ballot per authenticated Twitch identity, resolves
  ties randomly, queues exactly one winning action, and has a shared
  three-minute cooldown.
- LIFE STEAL is clamped so it cannot reduce the player below one HP.
- SLOW WORLD restores normal game speed after eight seconds, including its
  exception recovery path.
- SUMMON A BOSS creates exactly one session-only, reward-suppressed enemy using
  Auto Balance. The shared five-minute cooldown is enforced by the relay and
  shown as a synchronized countdown directly on the viewer button.

## Review environment

The review channel will be live during review with Elden Ring launched through
Mod Engine, EldenIntel open, the relay online, and interactions armed. The
reviewer does not need to install the game or desktop application. Review
availability can be coordinated between 9:00 AM and 5:00 PM Pacific Time.

## 0.0.1 changelog

- Initial private-streamer release.
- Real-time WebSocket queue shared across viewers.
- HEAL PLAYER, SHIFT TIME, LIFE STEAL, SLOW WORLD, RANDOM EFFECT, and a
  single-spawn SUMMON A BOSS action with a shared five-minute cooldown.
- Broadcaster arm, pause, clear-queue, connection, and queue controls.
- Shared HELP OR HURT clone vote with live totals and ally/enemy outcomes.
- Responsive video-overlay interface with keyboard and reduced-motion support.
