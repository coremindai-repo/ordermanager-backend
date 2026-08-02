-- Standing test/dev account for exercising auth + role-gated endpoints during development.
-- Password: Test@Pilot2026! (documented in docs/infrastructure.md — dev/test use only).
-- Rotate or remove before onboarding real client users.

DECLARE @testUserId UNIQUEIDENTIFIER = NEWID();

INSERT INTO users (id, username, password_hash, first_name, last_name, mobile_no, active)
VALUES (
    @testUserId,
    'devadmin',
    '$2b$12$7lWXK4ZWRSmOVJ3mlEHn2.kzaRrQpCzY9GTuw3LwaxXimOBn4Dm86',
    'Dev',
    'Admin',
    '9999999999',
    1
);

INSERT INTO user_roles (user_id, role) VALUES (@testUserId, 'company_manager');
