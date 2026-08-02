-- Epic 1 — Foundations: users, user_roles, stores, device_tokens
-- device_tokens is included here (ahead of Epic 7) because
-- POST /api/auth/register-device needs it in this epic.

CREATE TABLE users (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    username NVARCHAR(100) NOT NULL UNIQUE,
    password_hash NVARCHAR(200) NOT NULL,
    email NVARCHAR(200) NULL,
    first_name NVARCHAR(100) NOT NULL,
    last_name NVARCHAR(100) NOT NULL,
    mobile_no NVARCHAR(20) NULL,
    active BIT NOT NULL DEFAULT 1,
    created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE user_roles (
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    role NVARCHAR(50) NOT NULL CHECK (role IN ('salesperson', 'factory_supervisor', 'store_manager', 'company_manager')),
    PRIMARY KEY (user_id, role)
);

CREATE TABLE stores (
    id UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() PRIMARY KEY,
    name NVARCHAR(100) NOT NULL,
    location NVARCHAR(200) NULL,
    active BIT NOT NULL DEFAULT 1
);

CREATE TABLE device_tokens (
    user_id UNIQUEIDENTIFIER NOT NULL REFERENCES users(id),
    platform NVARCHAR(10) NOT NULL CHECK (platform IN ('ios', 'android')),
    push_token NVARCHAR(500) NOT NULL,
    updated_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (user_id, platform)
);
