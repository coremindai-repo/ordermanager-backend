-- Epic 2 — Workflow engine core.
--
-- Templates are versioned config, not code: a process change means inserting a
-- new row with a higher version and flipping `active`, never editing template_json
-- in place. The filtered unique indexes below enforce at most one active template
-- per client per type, so the loader can never be ambiguous about which applies.
--
-- NOTE: the running app caches the active template for the lifetime of the worker
-- instance. Changing a template row requires a redeploy to take effect — see
-- Lib/Workflow/TemplateProvider.cs and CLAUDE.md §5.

-- Required for the filtered indexes below (and for any later INSERT into these
-- tables). Some clients — notably the ODBC-era sqlcmd — default this OFF.
SET QUOTED_IDENTIFIER ON;
GO

CREATE TABLE process_templates (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    client_id UNIQUEIDENTIFIER NOT NULL,
    version INT NOT NULL,
    active BIT NOT NULL DEFAULT 0,
    template_json NVARCHAR(MAX) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_process_templates_client_version UNIQUE (client_id, version)
);

CREATE UNIQUE INDEX UX_process_templates_one_active_per_client
    ON process_templates (client_id) WHERE active = 1;

CREATE TABLE production_step_templates (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    client_id UNIQUEIDENTIFIER NOT NULL,
    version INT NOT NULL,
    active BIT NOT NULL DEFAULT 0,
    template_json NVARCHAR(MAX) NOT NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_production_step_templates_client_version UNIQUE (client_id, version)
);

CREATE UNIQUE INDEX UX_production_step_templates_one_active_per_client
    ON production_step_templates (client_id) WHERE active = 1;

-- Orders and line items are created here so the transition endpoints have
-- something to act on. Order *capture* (POST /orders, SOHO call, billing/shipping,
-- materials) belongs to Epic 3 — no creation endpoints are built in this epic.

CREATE TABLE orders (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    order_number NVARCHAR(100) NOT NULL UNIQUE,
    order_type NVARCHAR(20) NOT NULL CHECK (order_type IN ('customer', 'stock')),
    soho_order_ref NVARCHAR(100) NULL,
    current_status NVARCHAR(50) NOT NULL,
    store_id UNIQUEIDENTIFIER NULL REFERENCES stores(id),
    created_by UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE order_line_items (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    order_id UNIQUEIDENTIFIER NOT NULL REFERENCES orders(id),
    item_name NVARCHAR(200) NOT NULL,
    description NVARCHAR(1000) NULL,
    current_status NVARCHAR(50) NOT NULL,
    method NVARCHAR(20) NULL CHECK (method IN ('factory', 'outsource', 'import')),
    current_step NVARCHAR(100) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_order_line_items_order_id ON order_line_items (order_id);

-- Append-only history. Never UPDATE or DELETE these rows — they are the record
-- that makes future reporting possible (CLAUDE.md §4).

CREATE TABLE order_status_history (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    order_id UNIQUEIDENTIFIER NOT NULL REFERENCES orders(id),
    from_status NVARCHAR(50) NULL,
    to_status NVARCHAR(50) NOT NULL,
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    notes NVARCHAR(2000) NULL,
    photo_urls NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_order_status_history_order_id ON order_status_history (order_id, created_at);

CREATE TABLE line_item_status_history (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    line_item_id UNIQUEIDENTIFIER NOT NULL REFERENCES order_line_items(id),
    from_status NVARCHAR(50) NULL,
    to_status NVARCHAR(50) NOT NULL,
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    notes NVARCHAR(2000) NULL,
    photo_urls NVARCHAR(MAX) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_line_item_status_history_line_item_id ON line_item_status_history (line_item_id, created_at);
