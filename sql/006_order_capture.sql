-- Epic 3 — Order capture.

SET QUOTED_IDENTIFIER ON;
GO

-- bill_to / ship_to are stored as JSON because the data model (CLAUDE.md §4)
-- specifies them as json blobs — the address field list is client-facing and
-- captured wholesale by the mobile app.
CREATE TABLE billing_shipping_details (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    order_id UNIQUEIDENTIFIER NOT NULL UNIQUE REFERENCES orders(id),
    bill_to NVARCHAR(MAX) NULL,
    ship_to NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- `details` is JSON for the same reason: CLAUDE.md §4 defines materials as
-- "material details (as captured on the Add Material screen)", and that screen's
-- field list has not been shared. Storing the captured object whole avoids
-- inventing a column layout that would have to be migrated once it is.
CREATE TABLE materials (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    line_item_id UNIQUEIDENTIFIER NOT NULL REFERENCES order_line_items(id),
    details NVARCHAR(MAX) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_materials_line_item_id ON materials (line_item_id);

-- Stock order numbers: STK-{yyMM}-{seq:D4}, e.g. STK-2608-0042.
-- The sequence is continuous and never resets — the yyMM segment is for human
-- readability, uniqueness comes from the sequence alone. Letting the database
-- hand out the number means two concurrent submissions cannot collide.
CREATE SEQUENCE seq_stock_order_number AS BIGINT START WITH 1 INCREMENT BY 1;
