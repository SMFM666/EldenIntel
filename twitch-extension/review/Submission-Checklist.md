# Twitch Review Submission Checklist

## Required before submission

- [ ] Rotate the Twitch Extension secret and update the DPAPI-protected local copy.
- [x] Replace the temporary stress-test tunnel with the stable production relay URL (`https://ei-relay.somber-made.com`).
- [ ] Add the stable HTTPS and WSS relay origins to Capabilities.
- [ ] Confirm the Streamer Allowlist contains only the broadcaster's numeric Twitch ID.
- [ ] Verify the Testing Account Allowlist contains all intended testers.
- [ ] Add and verify the author email.
- [ ] Add the public support email.
- [ ] Set General Category to Extension for Games.
- [ ] Set Game Category to Elden Ring.
- [ ] Upload the 100×100 logo.
- [ ] Upload the 300×200 discovery image.
- [ ] Upload at least one 1024×768, 4:3 screenshot under 10 MB.
- [ ] Set the Privacy Policy URL to the hosted `privacy.html` page.
- [ ] Set the Terms/EULA URL to the hosted `terms.html` page.
- [x] Rebuild the single canonical `dist\EldenIntel-Interact-0.0.1.zip` from
      current source, including SPAWN CLONE voting.
- [ ] Upload the final ZIP and verify all paths from Twitch CDN.
- [ ] Run the complete reviewer walkthrough from a testing account on desktop.
- [ ] Repeat the Hosted Test on the Twitch mobile surface in portrait and
      landscape.
- [ ] Confirm HELP and HURT each deliver exactly one winning clone action in the
      live game.
- [ ] Keep the review channel live with Elden Ring launched through Mod Engine,
      EldenIntel open, the relay online, and interactions armed during review.
- [ ] Replace the submission screenshot with a current 1024×768 capture that
      shows the SPAWN CLONE vote.
- [ ] Confirm Bits and subscription support remain disabled for 0.0.1.
- [ ] Submit using `Walkthrough-and-Changelog.md`.
