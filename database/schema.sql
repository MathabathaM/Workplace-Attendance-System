-- =====================================================================
-- Legacy PostgreSQL schema reference (v1 draft). The running app now uses
-- EF Core's SQLite schema, created automatically in the user's local app data.
-- Do not run this PostgreSQL-only script to set up the SQLite database.
-- Derived from the prototype (app.js / index.html).
-- Sections: 1 Org structure | 2 People | 3 Users & roles | 4 Devices
--           5 Attendance    | 6 Access & screening | 7 Audit
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS "pgcrypto";   -- gen_random_uuid()
CREATE EXTENSION IF NOT EXISTS "citext";     -- case-insensitive email

-- Keep updated_at honest
CREATE OR REPLACE FUNCTION set_updated_at() RETURNS trigger AS $$
BEGIN NEW.updated_at = now(); RETURN NEW; END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------------
-- 1. ORG STRUCTURE
-- ---------------------------------------------------------------------
CREATE TABLE sites (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text NOT NULL UNIQUE,              -- "North Campus"
  city        text,                              -- "Johannesburg"
  country     char(2) NOT NULL DEFAULT 'ZA',
  timezone    text NOT NULL DEFAULT 'Africa/Johannesburg',
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE departments (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text NOT NULL UNIQUE,              -- Operations, Finance, ...
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now()
);

-- A shift defines its own start/end/break. Different start times = different
-- shifts, e.g. "Day 08:00" and "Day 08:30" (both category 'day'). Employees are
-- assigned to a shift; the times come from the shift.
CREATE TABLE shifts (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name              text NOT NULL UNIQUE,        -- 'Day 08:00', 'Day 08:30', 'Early 06:00'
  category          text NOT NULL CHECK (category IN ('day','early','late')),  -- label shown in the UI
  start_time        time NOT NULL,
  end_time          time NOT NULL,
  break_minutes     int  NOT NULL DEFAULT 30 CHECK (break_minutes >= 0),
  grace_minutes     int  NOT NULL DEFAULT 5  CHECK (grace_minutes >= 0), -- "late" only after this
  crosses_midnight  boolean NOT NULL DEFAULT false,
  is_active         boolean NOT NULL DEFAULT true
);

-- ---------------------------------------------------------------------
-- 2. PEOPLE
-- ---------------------------------------------------------------------
CREATE TABLE employees (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_number   text NOT NULL UNIQUE,        -- "EMP-1042"
  first_name        text NOT NULL,
  last_name         text NOT NULL,
  full_name         text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED,
  email             citext NOT NULL UNIQUE,
  job_title         text NOT NULL,
  site_id           uuid NOT NULL REFERENCES sites(id),
  department_id     uuid NOT NULL REFERENCES departments(id),
  manager_id        uuid REFERENCES employees(id),  -- drives "Line Manager" team scope
  employment_status text NOT NULL DEFAULT 'active'
                    CHECK (employment_status IN ('active','on_leave','terminated')),
  hire_date         date,
  termination_date  date,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CHECK (manager_id IS NULL OR manager_id <> id)
);
CREATE INDEX idx_employees_department ON employees(department_id);
CREATE INDEX idx_employees_manager    ON employees(manager_id);
CREATE INDEX idx_employees_status     ON employees(employment_status);
CREATE INDEX idx_employees_name_search
  ON employees USING gin (to_tsvector('simple', full_name || ' ' || employee_number || ' ' || job_title));
CREATE TRIGGER trg_employees_updated BEFORE UPDATE ON employees
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Which shift an employee works, and when. History is kept: to move someone
-- to another shift, close the current row (effective_to) and open a new one.
CREATE TABLE employee_shift_assignments (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id      uuid NOT NULL REFERENCES employees(id) ON DELETE CASCADE,
  shift_id         uuid NOT NULL REFERENCES shifts(id),
  effective_from   date NOT NULL,
  effective_to     date,                           -- NULL = current
  CHECK (effective_to IS NULL OR effective_to >= effective_from)
);
CREATE INDEX idx_shift_assign_emp ON employee_shift_assignments(employee_id, effective_from DESC);
-- Only one open-ended (current) assignment per employee
CREATE UNIQUE INDEX uq_shift_assign_current
  ON employee_shift_assignments(employee_id) WHERE effective_to IS NULL;

-- How someone is identified at a gate. Raw fingerprints / face images are NEVER
-- stored here: templates stay on the device (ZKTeco, Hikvision, Suprema...) and
-- we keep only the vendor's reference ID. PINs are stored as a salted slow hash
-- (argon2/bcrypt) in secret_hash. Note: a vendor_reference still links a person
-- to a biometric record, so POPIA consent / lawful-basis rules still apply.
CREATE TABLE employee_credentials (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id      uuid NOT NULL REFERENCES employees(id) ON DELETE CASCADE,
  credential_type  text NOT NULL
                   CHECK (credential_type IN ('fingerprint','face','rfid_card','barcode','pin')),
  vendor_reference text,                           -- e.g. 'ZKTeco-FP-39485', 'CARD-10025'
  secret_hash      text,                           -- PIN hash only
  status           text NOT NULL DEFAULT 'active' CHECK (status IN ('active','revoked','lost')),
  issued_at        timestamptz NOT NULL DEFAULT now(),
  revoked_at       timestamptz,
  UNIQUE (credential_type, vendor_reference),
  CHECK (
    (credential_type = 'pin' AND secret_hash IS NOT NULL AND vendor_reference IS NULL) OR
    (credential_type <> 'pin' AND vendor_reference IS NOT NULL AND secret_hash IS NULL)
  )
);
CREATE INDEX idx_credentials_employee ON employee_credentials(employee_id);

-- ---------------------------------------------------------------------
-- 3. USERS & ROLES  (ASP.NET Core Identity compatible)
-- Column names match what Identity + EFCore.NamingConventions (snake_case)
-- expect, so Identity runs directly on these tables.
-- ---------------------------------------------------------------------
CREATE TABLE roles (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name              text NOT NULL,
  normalized_name   text NOT NULL UNIQUE,
  concurrency_stamp text NOT NULL DEFAULT gen_random_uuid()::text
);
INSERT INTO roles (name, normalized_name) VALUES
  ('System Administrator', 'SYSTEM ADMINISTRATOR'),
  ('HR Officer',           'HR OFFICER'),
  ('Line Manager',         'LINE MANAGER'),
  ('Security Officer',     'SECURITY OFFICER'),
  ('Employee',             'EMPLOYEE'),
  ('Auditor',              'AUDITOR');

CREATE TABLE users (
  id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id            uuid UNIQUE REFERENCES employees(id),  -- most users are employees
  email                  citext NOT NULL UNIQUE,
  user_name              text NOT NULL,                          -- we use the email address
  normalized_user_name   text NOT NULL UNIQUE,
  normalized_email       text NOT NULL,
  email_confirmed        boolean NOT NULL DEFAULT false,
  password_hash          text,                                   -- NULL until a password is set
  security_stamp         text NOT NULL DEFAULT gen_random_uuid()::text,
  concurrency_stamp      text NOT NULL DEFAULT gen_random_uuid()::text,
  phone_number           text,
  phone_number_confirmed boolean NOT NULL DEFAULT false,
  two_factor_enabled     boolean NOT NULL DEFAULT false,
  lockout_end            timestamptz,
  lockout_enabled        boolean NOT NULL DEFAULT true,
  access_failed_count    int NOT NULL DEFAULT 0,
  is_active              boolean NOT NULL DEFAULT true,
  last_login_at          timestamptz,
  created_at             timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_users_normalized_email ON users(normalized_email);

CREATE TABLE user_roles (
  user_id  uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role_id  uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
  PRIMARY KEY (user_id, role_id)
);

-- Standard Identity tables (unused for now, but Identity queries them at sign-in)
CREATE TABLE user_claims (
  id          serial PRIMARY KEY,
  user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  claim_type  text,
  claim_value text
);
CREATE TABLE role_claims (
  id          serial PRIMARY KEY,
  role_id     uuid NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
  claim_type  text,
  claim_value text
);
CREATE TABLE user_logins (
  login_provider        text NOT NULL,
  provider_key          text NOT NULL,
  provider_display_name text,
  user_id               uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  PRIMARY KEY (login_provider, provider_key)
);
CREATE TABLE user_tokens (
  user_id        uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  login_provider text NOT NULL,
  name           text NOT NULL,
  value          text,
  PRIMARY KEY (user_id, login_provider, name)
);

-- ---------------------------------------------------------------------
-- 4. ACCESS POINTS & DEVICES
-- ---------------------------------------------------------------------
CREATE TABLE access_points (
  id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  site_id   uuid NOT NULL REFERENCES sites(id),
  name      text NOT NULL,                          -- Main entrance, East Gate
  UNIQUE (site_id, name)
);

CREATE TABLE devices (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  access_point_id  uuid NOT NULL REFERENCES access_points(id),
  device_type      text NOT NULL
                   CHECK (device_type IN ('biometric_scanner','barcode_scanner','breathalyser','metal_detector')),
  label            text NOT NULL,
  serial_number    text UNIQUE,
  status           text NOT NULL DEFAULT 'online' CHECK (status IN ('online','offline')),
  last_seen_at     timestamptz,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_devices_access_point ON devices(access_point_id);

-- ---------------------------------------------------------------------
-- 5. ATTENDANCE
-- ---------------------------------------------------------------------
-- One row per employee per working day. Scheduled times are SNAPSHOTS so
-- later shift changes don't rewrite history.
CREATE TABLE attendance_records (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id       uuid NOT NULL REFERENCES employees(id),
  work_date         date NOT NULL,
  shift_id          uuid REFERENCES shifts(id),
  scheduled_start   timestamptz,
  scheduled_end     timestamptz,
  clock_in_at       timestamptz,
  clock_out_at      timestamptz,
  clock_in_point_id  uuid REFERENCES access_points(id),
  clock_out_point_id uuid REFERENCES access_points(id),
  worked_minutes    int GENERATED ALWAYS AS (
                      CASE WHEN clock_in_at IS NOT NULL AND clock_out_at IS NOT NULL
                           THEN (EXTRACT(EPOCH FROM (clock_out_at - clock_in_at)) / 60)::int END
                    ) STORED,
  status            text NOT NULL DEFAULT 'absent'
                    CHECK (status IN ('on_time','late','absent','incomplete','approved_exception','on_leave','holiday')),
  is_manually_adjusted boolean NOT NULL DEFAULT false,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  UNIQUE (employee_id, work_date),
  CHECK (clock_out_at IS NULL OR clock_in_at IS NULL OR clock_out_at >= clock_in_at)
);
CREATE INDEX idx_attendance_date        ON attendance_records(work_date);
CREATE INDEX idx_attendance_date_status ON attendance_records(work_date, status);
CREATE TRIGGER trg_attendance_updated BEFORE UPDATE ON attendance_records
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- Every manual change: who, why, before/after. Reason is mandatory (>= 8 chars,
-- matching the prototype's validation).
CREATE TABLE attendance_adjustments (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  attendance_id     uuid NOT NULL REFERENCES attendance_records(id),
  adjusted_by       uuid NOT NULL REFERENCES users(id),
  reason            text NOT NULL CHECK (char_length(btrim(reason)) >= 8),
  previous_values   jsonb NOT NULL,
  new_values        jsonb NOT NULL,
  adjusted_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_adjustments_attendance ON attendance_adjustments(attendance_id);

-- ---------------------------------------------------------------------
-- 6. ACCESS & SCREENING
-- ---------------------------------------------------------------------
CREATE TABLE access_events (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id      uuid REFERENCES employees(id),         -- NULL if identity failed
  credential_id    uuid REFERENCES employee_credentials(id),  -- which credential was presented
  access_point_id  uuid NOT NULL REFERENCES access_points(id), -- gate (site comes via access_point)
  device_id        uuid REFERENCES devices(id),                -- device that read the credential
  occurred_at      timestamptz NOT NULL,
  direction        text NOT NULL CHECK (direction IN ('entry','exit')),
  identity_result  text NOT NULL CHECK (identity_result IN ('verified','inconclusive','failed')),
  decision         text NOT NULL CHECK (decision IN ('granted','denied','needs_review')),
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_access_events_time     ON access_events(occurred_at DESC);
CREATE INDEX idx_access_events_employee ON access_events(employee_id, occurred_at DESC);
CREATE INDEX idx_access_events_review   ON access_events(occurred_at DESC) WHERE decision = 'needs_review';

-- Screening results live in their own table so they can be locked down to
-- Security Officers / Admins only ("restricted information" in the UI).
-- Exits typically have no screening rows (shown as "—").
CREATE TABLE screening_results (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  access_event_id  uuid NOT NULL REFERENCES access_events(id) ON DELETE CASCADE,
  screening_type   text NOT NULL CHECK (screening_type IN ('breathalyser','metal_detector','bag_search')),
  result           text NOT NULL CHECK (result IN ('clear','inconclusive','alert')),
  device_id        uuid REFERENCES devices(id),   -- automated screening
  officer_id       uuid REFERENCES users(id),     -- manual screening (e.g. bag search)
  screened_at      timestamptz NOT NULL DEFAULT now(),
  UNIQUE (access_event_id, screening_type)
);

-- Human review of a flagged event. An inconclusive result is NOT a violation;
-- a person records the outcome. Multiple rows allowed (e.g. escalation).
CREATE TABLE access_reviews (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  access_event_id  uuid NOT NULL REFERENCES access_events(id),
  reviewed_by      uuid NOT NULL REFERENCES users(id),
  outcome          text NOT NULL CHECK (outcome IN ('granted_after_review','denied_after_review','escalated')),
  finding          text CHECK (finding IN ('cleared','warning_issued','violation_confirmed','further_investigation')),
  notes            text NOT NULL CHECK (char_length(btrim(notes)) >= 8),
  reviewed_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_access_reviews_event ON access_reviews(access_event_id, reviewed_at DESC);

-- ---------------------------------------------------------------------
-- 7. AUDIT LOG  (append-only)
-- ---------------------------------------------------------------------
CREATE TABLE audit_logs (
  id           bigserial PRIMARY KEY,
  actor_id     uuid REFERENCES users(id),
  action       text NOT NULL,                  -- 'employee.update', 'attendance.adjust', ...
  entity_type  text NOT NULL,
  entity_id    text NOT NULL,
  changes      jsonb,
  reason       text,
  ip_address   inet,
  created_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_audit_entity ON audit_logs(entity_type, entity_id);
CREATE INDEX idx_audit_actor  ON audit_logs(actor_id, created_at DESC);
CREATE INDEX idx_audit_time   ON audit_logs(created_at DESC);

-- Block edits/deletes on the audit trail
CREATE OR REPLACE FUNCTION audit_logs_immutable() RETURNS trigger AS $$
BEGIN RAISE EXCEPTION 'audit_logs is append-only'; END;
$$ LANGUAGE plpgsql;
CREATE TRIGGER trg_audit_immutable BEFORE UPDATE OR DELETE ON audit_logs
  FOR EACH ROW EXECUTE FUNCTION audit_logs_immutable();
