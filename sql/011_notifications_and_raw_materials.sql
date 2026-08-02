-- Epic 5 — store manager flow: notification routing, notification log, raw materials.

SET QUOTED_IDENTIFIER ON;
GO

-- Who gets told about what. Configurable rather than hard-coded because "the
-- accountant" is not a role in this system (CLAUDE.md §8 names them, but user_roles
-- has only salesperson / factory_supervisor / store_manager / company_manager).
-- Routing by data means designating a different person later is a row insert, not a
-- deploy.
--
-- A recipient is EITHER a role (everyone holding it) OR one specific user, never
-- both — enforced by the XOR check below.
--
-- ⚠ If an event type has no active rows here, nobody is notified. The app logs a
-- warning in that case rather than failing silently; see NotificationService.
CREATE TABLE notification_recipients (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    client_id UNIQUEIDENTIFIER NOT NULL,
    -- Matches the push payload types in API-INTERFACE-CONTRACT.md §11.
    event_type NVARCHAR(50) NOT NULL
        CHECK (event_type IN ('order_status_changed', 'invoice_ready', 'raw_material_received', 'item_assigned')),
    recipient_role NVARCHAR(50) NULL
        CHECK (recipient_role IN ('salesperson', 'factory_supervisor', 'store_manager', 'company_manager')),
    recipient_user_id UNIQUEIDENTIFIER NULL REFERENCES users(id),
    active BIT NOT NULL DEFAULT 1,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_notification_recipients_role_xor_user
        CHECK ((CASE WHEN recipient_role IS NULL THEN 0 ELSE 1 END)
             + (CASE WHEN recipient_user_id IS NULL THEN 0 ELSE 1 END) = 1)
);

CREATE INDEX IX_notification_recipients_event
    ON notification_recipients (client_id, event_type) WHERE active = 1;

-- Append-only record of every notification the system decided to send, logged
-- regardless of delivery outcome (CLAUDE.md §7) — the in-app notification list reads
-- from here, independent of whether the OS push was ever seen.
--
-- sent_at records when the app dispatched it, not when it was delivered.
CREATE TABLE notifications_log (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    type NVARCHAR(50) NOT NULL,
    order_id UNIQUEIDENTIFIER NULL REFERENCES orders(id),
    line_item_id UNIQUEIDENTIFIER NULL REFERENCES order_line_items(id),
    title NVARCHAR(200) NOT NULL,
    body NVARCHAR(1000) NULL,
    -- Stamped only when a push actually reached the device via the Expo Push API.
    -- NULL means recorded but undelivered — no device registered, Expo unreachable,
    -- or the token was dead. Lets "decided to notify" be told apart from "pushed".
    dispatched_at DATETIME2 NULL,
    sent_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_notifications_log_user ON notifications_log (user_id, sent_at DESC);

-- Raw material procurement. Per contract §6 this is a FIXED sub-process, deliberately
-- NOT templatized: requested → sent_to_supplier → order_placed → order_accepted →
-- received. Supplier contact happens on WhatsApp or the phone; the app only records
-- the resulting status.
CREATE TABLE raw_material_requests (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    requested_by UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    items NVARCHAR(MAX) NOT NULL,
    status NVARCHAR(30) NOT NULL DEFAULT 'requested'
        CHECK (status IN ('requested', 'sent_to_supplier', 'order_placed', 'order_accepted', 'received')),
    supplier NVARCHAR(MAX) NULL,
    notes NVARCHAR(2000) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_raw_material_requests_status ON raw_material_requests (status, created_at DESC);

-- Append-only history, same discipline as the order/line-item history tables.
CREATE TABLE raw_material_request_history (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    request_id UNIQUEIDENTIFIER NOT NULL REFERENCES raw_material_requests(id),
    from_status NVARCHAR(30) NULL,
    to_status NVARCHAR(30) NOT NULL,
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    notes NVARCHAR(2000) NULL,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_raw_material_request_history_request
    ON raw_material_request_history (request_id, created_at);
GO

-- Seed routing for the pilot client. store_manager is chosen because contract §3
-- already lists "invoicing" under that role; company_manager is included because §3
-- says they see everything store_manager sees. Swap for a specific recipient_user_id
-- if a dedicated accountant is appointed.
DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

INSERT INTO notification_recipients (client_id, event_type, recipient_role) VALUES
    (@clientId, 'invoice_ready', 'store_manager'),
    (@clientId, 'invoice_ready', 'company_manager'),
    (@clientId, 'raw_material_received', 'store_manager'),
    (@clientId, 'raw_material_received', 'factory_supervisor');
