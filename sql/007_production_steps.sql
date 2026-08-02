-- Epic 4 — Factory production flow.

SET QUOTED_IDENTIFIER ON;
GO

-- The per-item production plan: which steps this item actually requires, in order,
-- and how far each has got. Which steps exist at all comes from the client's active
-- production_step_template; which of them a given item needs is chosen on the
-- "This item will require" checklist (contract §5).
--
-- photo_urls holds blob *paths* only (e.g. orderId/lineItemId/stepId/{guid}.jpg),
-- never full URLs and never SAS tokens. Read URLs are minted fresh and short-lived
-- at response time — see Lib/Photos/PhotoStorage.cs.
CREATE TABLE order_line_item_steps (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    line_item_id UNIQUEIDENTIFIER NOT NULL REFERENCES order_line_items(id),
    step_name NVARCHAR(100) NOT NULL,
    sequence INT NOT NULL,
    status NVARCHAR(20) NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'started', 'complete')),
    assigned_names NVARCHAR(MAX) NULL,
    photo_urls NVARCHAR(MAX) NULL,
    started_at DATETIME2 NULL,
    completed_at DATETIME2 NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    -- One row per step per item: re-planning replaces rows rather than duplicating them.
    CONSTRAINT UQ_order_line_item_steps_item_step UNIQUE (line_item_id, step_name)
);

CREATE INDEX IX_order_line_item_steps_line_item_id
    ON order_line_item_steps (line_item_id, sequence);
