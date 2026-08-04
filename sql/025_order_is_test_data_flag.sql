-- orders.is_test_data — lets dev/test verification data be hidden from normal use
-- without ever touching the append-only history tables (order_status_history,
-- line_item_status_history explicitly say "never UPDATE or DELETE" — CLAUDE.md §4).
--
-- Came up when cleaning up ~20 orders created while verifying the NEW -> IN_PRODUCTION
-- fix (v7 rollout): deleting them would have meant deleting their history rows too,
-- which the schema forbids. Tagging instead of deleting respects that fully and is
-- reusable — any future dev/test verification data gets the same tag via a manual
-- UPDATE rather than raising this question again.
--
-- Server-side only: never returned in any API response, no contract/mobile impact.

SET QUOTED_IDENTIFIER ON;
GO

ALTER TABLE orders ADD is_test_data BIT NOT NULL DEFAULT 0;
GO

-- Backfill: every order created via the devsales dev/test account, identified by
-- creator rather than by item-name pattern — devsales has never been used for
-- anything other than this session's verification work, so this is the reliable
-- signal, not a coincidental one.
UPDATE orders
SET is_test_data = 1
WHERE created_by = (SELECT id FROM users WHERE username = 'devsales');
