-- Reference photos, finish, and structured dimensions on line items.
--
-- DIMENSIONS ARE STORED CANONICALLY IN CENTIMETRES. The client works in metres and
-- centimetres; the salesperson picks either at entry and the BACKEND converts, so the
-- conversion happens in exactly one place. Mobile must not pre-convert — if a released
-- app version got it wrong the data would be permanently corrupt with no record of what
-- was actually entered.
--
-- dimension_unit_entered preserves what the salesperson chose, purely so the value can
-- be displayed back the way they typed it. Aggregates read the _cm columns directly and
-- need no grouping or conversion:
--
--     SELECT AVG(dimension_length_cm) FROM order_line_items   -- always meaningful
--
-- Axes are length / breadth / HEIGHT. The original spec said "width", which is a synonym
-- of breadth and left furniture without a real third axis.
--
-- Migration: all columns nullable, and the table is empty at time of writing, so there
-- is nothing to backfill.

SET QUOTED_IDENTIFIER ON;
GO

ALTER TABLE order_line_items
    ADD -- Blob paths only, never URLs or SAS tokens, same discipline as
        -- order_line_item_steps.photo_urls. JSON array of strings.
        reference_photo_urls NVARCHAR(MAX) NULL,

        finish NVARCHAR(100) NULL,

        -- Canonical centimetres. Written by the backend after conversion.
        dimension_length_cm DECIMAL(10, 2) NULL,
        dimension_breadth_cm DECIMAL(10, 2) NULL,
        dimension_height_cm DECIMAL(10, 2) NULL,

        -- What the salesperson chose, for display fidelity only.
        dimension_unit_entered NVARCHAR(5) NULL
            CHECK (dimension_unit_entered IN ('m', 'cm'));
GO

-- A dimension with no unit cannot be displayed back correctly, and a unit with no
-- dimension is meaningless. Neither is a state worth allowing into a reporting table.
ALTER TABLE order_line_items
    ADD CONSTRAINT CK_order_line_items_dimension_unit CHECK (
        (dimension_length_cm IS NULL
         AND dimension_breadth_cm IS NULL
         AND dimension_height_cm IS NULL
         AND dimension_unit_entered IS NULL)
        OR
        (dimension_unit_entered IS NOT NULL
         AND (dimension_length_cm IS NOT NULL
              OR dimension_breadth_cm IS NOT NULL
              OR dimension_height_cm IS NOT NULL))
    );
GO

-- Dimension range queries are a stated reporting goal, so index the axis most likely to
-- be filtered on. Add the others if querying patterns show they are needed.
CREATE INDEX IX_order_line_items_dimension_length
    ON order_line_items (dimension_length_cm) WHERE dimension_length_cm IS NOT NULL;
