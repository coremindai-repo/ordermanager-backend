-- Reference data per CLAUDE.md §4 — adding a store later is a row insert, not a deploy.
INSERT INTO stores (name, location, active) VALUES
    ('Kochi', 'Kochi, Kerala', 1),
    ('Bangalore', 'Bangalore, Karnataka', 1);
