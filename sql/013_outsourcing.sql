-- Epic 6 — outsourcing / import flow.

SET QUOTED_IDENTIFIER ON;
GO

-- Predefined supplier list (the wireframes show a picker, not free text). Reference
-- data like stores: adding a supplier is a row insert, not a deploy.
--
-- ⚠ DELIBERATELY NOT SEEDED. The client's actual supplier list has not been shared,
-- and inventing plausible supplier names would put fake business relationships into
-- the database looking exactly like real ones. Must be populated before use — see the
-- go-live checklist in docs/infrastructure.md.
CREATE TABLE suppliers (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    name NVARCHAR(200) NOT NULL,
    contact NVARCHAR(MAX) NULL,
    -- Which routes this supplier is used for; a supplier may serve both.
    supports_outsource BIT NOT NULL DEFAULT 1,
    supports_import BIT NOT NULL DEFAULT 0,
    active BIT NOT NULL DEFAULT 1,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_suppliers_active ON suppliers (active, name);

-- Outsourcing / import requests. Like raw materials (contract §6) this is a FIXED
-- sub-process, deliberately not templatized:
--
--   placed → accepted → received_finished
--                    └→ received_semi_finished
--
-- Two terminal states rather than one: finished goods are done, semi-finished goods
-- still need factory work. Which one is reached decides where the linked line items
-- go next — see Lib/Outsourcing/OutsourcingStatusFlow.cs.
--
-- Supplier contact happens on WhatsApp or the phone; the app records the result only.
CREATE TABLE outsourcing_requests (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    requested_by UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    -- 'outsource' or 'import' — matches order_line_items.method, so a request cannot
    -- be raised down the wrong route for its items.
    method NVARCHAR(20) NOT NULL CHECK (method IN ('outsource', 'import')),
    supplier_id UNIQUEIDENTIFIER NULL REFERENCES suppliers(id),
    items NVARCHAR(MAX) NULL,
    status NVARCHAR(30) NOT NULL DEFAULT 'placed'
        CHECK (status IN ('placed', 'accepted', 'received_finished', 'received_semi_finished')),
    notes NVARCHAR(2000) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_outsourcing_requests_status ON outsourcing_requests (status, created_at DESC);

-- Real foreign keys rather than ids buried in the items JSON: receiving a request
-- moves its line items forward, so the link has to be queryable and enforced.
CREATE TABLE outsourcing_request_line_items (
    request_id UNIQUEIDENTIFIER NOT NULL REFERENCES outsourcing_requests(id),
    line_item_id UNIQUEIDENTIFIER NOT NULL REFERENCES order_line_items(id),
    PRIMARY KEY (request_id, line_item_id)
);

CREATE INDEX IX_outsourcing_request_line_items_item
    ON outsourcing_request_line_items (line_item_id);

CREATE TABLE outsourcing_request_history (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    request_id UNIQUEIDENTIFIER NOT NULL REFERENCES outsourcing_requests(id),
    from_status NVARCHAR(30) NULL,
    to_status NVARCHAR(30) NOT NULL,
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    notes NVARCHAR(2000) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_outsourcing_request_history_request
    ON outsourcing_request_history (request_id, created_at);
