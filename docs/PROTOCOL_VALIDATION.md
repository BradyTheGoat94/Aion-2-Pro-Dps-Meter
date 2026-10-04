# Protocol validation status

This file tracks what the current Global profile has actually demonstrated in captured traffic. It is intentionally stricter than "the meter displayed something": a row is considered validated only when live packet evidence and a regression fixture support the interpretation.

The profile must remain `verified: false` until the required meter paths below are covered across multiple live sessions and transitions.

| Area | Status | Current evidence / remaining work |
| --- | --- | --- |
| Normal damage amount | Capture-proven | Multiple exact 04 38 fixtures across Templar skills and hostile NPC attacks. |
| Damage flags/direction | Capture-proven for observed layouts | Direct/Crit/Perfect/Back/Frontal/Parry/Double plus the category-6 variable auxiliary layout have exact fixtures. Continue collecting new layouts. |
| Multi-hit recovery | Capture-proven for observed five-hit layout | Firestorm fifth-hit ordinal recovery has an exact live fixture. More classes/summons still need coverage. |
| Player self identity | Capture-proven | Current 33 36 self-info resolves entity, nickname and job/class metadata. |
| Other-player identity | Capture-proven | Current 45 36 accepts observed 07 and 17 name markers and preserves job/class metadata. |
| Two-character player names | Capture-proven | Exact 45 36 fixture for Qi. |
| Cross-adapter player identity | Capture-proven | Exact entity identities are shared across duplicate Npcap adapters/sibling sockets. |
| Late player promotion | Capture-proven | Hidden Actor damage becomes visible after trusted identity; live 6456 -> ThotHokage pattern is regressed. |
| Session/stable self bridge | Capture-proven for local character | Live 3920 -> 304076 -> 9640 sequence; reverse promotion is restricted to trusted self identity. |
| Party roster identity/class | Capture-proven for observed layout | Current job-code ranges 5-40 implemented. Needs more live party transitions/reconnects. |
| NPC identity | Capture-proven for observed spawn/HP paths | MobSpawn/TargetHp identities are kept out of player DPS and shared across capture streams. |
| Summon ownership | Partial | Exact observed summon parent/owner layouts exist. Needs broader pet/summon class coverage. |
| DoT/heal | Partial | Decoding exists and live DoTs are observed; controlled player-class fixtures remain incomplete. |
| Skill names | Partial | Public game data resolves many player skills. Numeric `Skill ####` remains when no trustworthy English mapping is available; do not guess. |
| Target English names | Partial | Known public NPC catalog + observed TargetHp names work. Unknown targets retain safe placeholders. |
| Encounter inactivity | Regression-validated | Current fight closes at 8 seconds idle and Previous/Overall separation is tested. |
| Zone / explicit CombatEnd | Engine-validated only | CombatEngine closes immediately when given these events, but the current packet dispatcher does not yet have a capture-verified packet source for them. |
| Boss death boundary | Not live-verified | Do not claim live boss-death completion until a trusted boss flag/entity signal is wired from packet evidence. |
| Controlled multiplayer totals | Regression-validated | Four confirmed players, one unresolved actor and one NPC are tested for exact inclusion/exclusion and late promotion. Needs a real 2-4 player controlled live comparison. |
| Long-session/reconnect stability | In progress | Duplicate adapters, reconnect IDs and local session transitions have coverage; long-duration live validation is still required. |

## Verification exit criteria

Before changing the Global profile to `verified: true`, require live validation for player/source identity, target identity, exact damage amounts and flags, healing/DoTs, summon ownership, encounter boundaries, and a controlled multi-player comparison where per-player totals agree with independently observed combat results. Unknown identities, skills, or targets must continue to use explicit safe fallbacks instead of inferred names.
