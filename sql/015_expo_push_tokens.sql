-- Epic 7 prep — device_tokens holds Expo push tokens.
--
-- The mobile app ships via Expo, so Expo brokers delivery to FCM/APNs and the backend
-- only ever handles an Expo token. Firebase/Apple credentials are configured in the
-- mobile repo via EAS and never reach this repo (CLAUDE.md §7).
--
-- Expo tokens look like:  ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]
-- (Expo also issues the legacy ExpoPushToken[...] prefix, so both are accepted.)
--
-- ⚠ NOTE THE ESCAPING: in T-SQL LIKE, '[' opens a character class, so the pattern
-- 'ExponentPushToken[%]' matches NOTHING — it reads as the literal text followed by a
-- class containing '%'. A literal bracket must be written '[[]'. Getting this wrong
-- silently inverts every match: the CHECK would reject all valid tokens, and the
-- cleanup DELETE below would remove all of them.
--
-- The CHECK is a backstop, not the primary validation — POST /api/auth/register-device
-- rejects the wrong shape first, with a message that explains what went wrong. Its
-- value is catching anything written by a route that bypasses the endpoint, because a
-- bare FCM/APNs token here would fail at Expo's end rather than ours, and Expo reports
-- that per-token inside a 200 response — easy to miss.

SET QUOTED_IDENTIFIER ON;
GO

-- No existing rows to migrate: device registration has only ever been exercised with
-- throwaway test tokens, all of which were removed with their test users.
DELETE FROM device_tokens
WHERE push_token NOT LIKE 'ExponentPushToken[[]%]'
  AND push_token NOT LIKE 'ExpoPushToken[[]%]';
GO

ALTER TABLE device_tokens
    ADD CONSTRAINT CK_device_tokens_expo_format
    CHECK (push_token LIKE 'ExponentPushToken[[]%]' OR push_token LIKE 'ExpoPushToken[[]%]');
