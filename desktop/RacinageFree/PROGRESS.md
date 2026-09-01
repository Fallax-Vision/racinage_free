# Offline Progress in Racinage Free

Racinage Free keeps Progress private and offline. Open **Manage > Progress** to see the local score, level, current and longest weekly streak, the last 12 weeks, earned and locked badges, and the interface display mode.

## What can earn local score

- A newly retained local person can earn 20 score, capped at four per week.
- A newly created local Calendar item can earn 20 score, capped at four per week.
- A reviewed, checksum-verified portable plugin installation can earn 25 score once per plugin.
- The first trusted action in a Monday-to-Sunday local week earns 25 additional score and qualifies that week.

Updates, deletions, navigation, logins, searches, imports, repeated resource identifiers, automatic jobs, and unreviewed plugin data do not earn score. Duplicate and over-cap candidates are kept as suppressed audit rows and are never backfilled.

## Privacy boundary

There are no aliases, public leaderboards, Canopy Points, rewards, plan boosts, hosted AI credits, or Progress synchronisation. Profile, event, weekly-rollup, and badge rows remain in the encrypted-at-rest local app directory under `%LOCALAPPDATA%\Racinage Free`. The SQLite database is never exposed to hosted services or companion jobs.

Background only hides Progress decoration. Subtle adds a small static level marker. Expressive adds static colour accents. None of these modes changes the local analytics.
