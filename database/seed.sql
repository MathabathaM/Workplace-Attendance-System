-- =====================================================================
-- Legacy PostgreSQL seed data (development only). The running SQLite app seeds
-- its own local database on first development start; do not run this script there.
-- Run AFTER schema.sql, once, on an empty database.
-- All people and events are fictional and mirror the prototype (app.js).
-- The six roles are inserted by schema.sql, so they are not repeated here.
--
-- To reset and reload during development, run this first:
--   TRUNCATE audit_logs, access_reviews, screening_results, access_events,
--            attendance_adjustments, attendance_records, devices, access_points,
--            user_roles, user_claims, role_claims, user_logins, user_tokens, users, employee_credentials, employee_shift_assignments,
--            employees, shifts, departments, sites RESTART IDENTITY CASCADE;
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- Org structure
-- ---------------------------------------------------------------------
INSERT INTO sites (name, city, country) VALUES ('North Campus', 'Johannesburg', 'ZA');

INSERT INTO departments (name) VALUES
  ('Operations'), ('Finance'), ('People & Culture'), ('Logistics');

INSERT INTO shifts (name, category, start_time, end_time, break_minutes, grace_minutes) VALUES
  ('Day 08:00',   'day',   '08:00', '16:30', 30, 5),
  ('Day 08:30',   'day',   '08:30', '17:00', 30, 5),
  ('Early 06:00', 'early', '06:00', '14:30', 30, 5),
  ('Late 09:00',  'late',  '09:00', '17:30', 30, 5);

-- ---------------------------------------------------------------------
-- Employees
-- ---------------------------------------------------------------------
INSERT INTO employees (employee_number, first_name, last_name, email, job_title,
                       site_id, department_id, employment_status, hire_date)
SELECT v.num, v.first_name, v.last_name, v.email, v.title,
       (SELECT id FROM sites WHERE name = 'North Campus'),
       (SELECT id FROM departments WHERE name = v.dept),
       v.status, DATE '2024-01-15'
FROM (VALUES
  ('EMP-1001', 'Thabo',    'Mokoena',  'thabo.mokoena@example.com',    'Operations',       'Systems Administrator', 'active'),
  ('EMP-1042', 'Lerato',   'Nkosi',    'lerato.nkosi@example.com',     'Operations',       'Operations Lead',       'active'),
  ('EMP-1087', 'Sipho',    'Dlamini',  'sipho.dlamini@example.com',    'Logistics',        'Warehouse Supervisor',  'active'),
  ('EMP-1103', 'Amara',    'Patel',    'amara.patel@example.com',      'Finance',          'Accounts Officer',      'active'),
  ('EMP-1128', 'Thandiwe', 'Khumalo',  'thandiwe.khumalo@example.com', 'People & Culture', 'HR Coordinator',        'active'),
  ('EMP-1156', 'Kabelo',   'Molefe',   'kabelo.molefe@example.com',    'Operations',       'Safety Technician',     'active'),
  ('EMP-1189', 'Nadia',    'Jacobs',   'nadia.jacobs@example.com',     'Finance',          'Payroll Analyst',       'on_leave'),
  ('EMP-1210', 'Bongani',  'Maseko',   'bongani.maseko@example.com',   'Logistics',        'Fleet Coordinator',     'active'),
  ('EMP-1234', 'Zanele',   'Ndlovu',   'zanele.ndlovu@example.com',    'Operations',       'Quality Inspector',     'active')
) AS v(num, first_name, last_name, email, dept, title, status);

-- Reporting lines (drives the "Line Manager" team view)
UPDATE employees SET manager_id = (SELECT id FROM employees WHERE employee_number = 'EMP-1042')
 WHERE employee_number IN ('EMP-1001', 'EMP-1156', 'EMP-1234');
UPDATE employees SET manager_id = (SELECT id FROM employees WHERE employee_number = 'EMP-1087')
 WHERE employee_number = 'EMP-1210';

-- Shift assignments (current, open-ended)
INSERT INTO employee_shift_assignments (employee_id, shift_id, effective_from)
SELECT e.id, s.id, DATE '2026-01-01'
FROM (VALUES
  ('EMP-1001', 'Day 08:00'),   ('EMP-1042', 'Day 08:00'),   ('EMP-1087', 'Day 08:00'),
  ('EMP-1103', 'Day 08:30'),   ('EMP-1128', 'Day 08:00'),   ('EMP-1156', 'Early 06:00'),
  ('EMP-1189', 'Day 08:30'),   ('EMP-1210', 'Late 09:00'),  ('EMP-1234', 'Early 06:00')
) AS v(num, shift_name)
JOIN employees e ON e.employee_number = v.num
JOIN shifts s    ON s.name = v.shift_name;

-- Credentials: a fingerprint (device-side reference only) and a barcode each.
INSERT INTO employee_credentials (employee_id, credential_type, vendor_reference)
SELECT id, 'fingerprint', 'ZKTeco-FP-' || right(employee_number, 4) FROM employees
UNION ALL
SELECT id, 'barcode',     'BC-'         || right(employee_number, 4) FROM employees;

-- ---------------------------------------------------------------------
-- Users and roles (password_hash left NULL; in Development the API sets a dev
-- password for these accounts on startup - see the API README)
-- ---------------------------------------------------------------------
INSERT INTO users (employee_id, email, user_name, normalized_user_name, normalized_email, email_confirmed)
SELECT e.id, v.email, v.email, upper(v.email), upper(v.email), true
FROM (VALUES
  ('EMP-1001', 'thabo.mokoena@example.com'),     -- System Administrator
  ('EMP-1128', 'thandiwe.khumalo@example.com'),  -- HR Officer
  ('EMP-1042', 'lerato.nkosi@example.com'),      -- Line Manager
  (NULL,       'security.officer@example.com'),  -- Security Officer
  ('EMP-1103', 'amara.patel@example.com'),       -- Employee
  (NULL,       'auditor@example.com')            -- Auditor
) AS v(num, email)
LEFT JOIN employees e ON e.employee_number = v.num;

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM (VALUES
  ('thabo.mokoena@example.com',    'System Administrator'),
  ('thandiwe.khumalo@example.com', 'HR Officer'),
  ('lerato.nkosi@example.com',     'Line Manager'),
  ('security.officer@example.com', 'Security Officer'),
  ('amara.patel@example.com',      'Employee'),
  ('auditor@example.com',          'Auditor')
) AS v(email, role_name)
JOIN users u ON u.email = v.email
JOIN roles r ON r.name  = v.role_name;

-- ---------------------------------------------------------------------
-- Access points and devices
-- ---------------------------------------------------------------------
INSERT INTO access_points (site_id, name)
SELECT (SELECT id FROM sites WHERE name = 'North Campus'), n
FROM (VALUES ('Main entrance'), ('East Gate'), ('Reception desk')) AS v(n);

INSERT INTO devices (access_point_id, device_type, label, serial_number, status, last_seen_at)
SELECT ap.id, v.dtype, v.label, v.serial, v.status, v.seen::timestamptz
FROM (VALUES
  ('Main entrance',  'biometric_scanner', 'Main entrance biometric scanner', 'SN-BIO-0001', 'online',  '2026-10-27 10:45+02'),
  ('Main entrance',  'breathalyser',      'Main entrance breathalyser',      'SN-BRE-0001', 'online',  '2026-10-27 10:45+02'),
  ('Main entrance',  'metal_detector',    'Main entrance metal detector',    'SN-MET-0001', 'online',  '2026-10-27 10:45+02'),
  ('Reception desk', 'barcode_scanner',   'Reception desk barcode scanner',  'SN-BAR-0001', 'online',  '2026-10-27 10:44+02'),
  ('East Gate',      'breathalyser',      'East Gate breathalyser',          'SN-BRE-0002', 'offline', '2026-10-27 10:27+02')
) AS v(ap_name, dtype, label, serial, status, seen)
JOIN access_points ap ON ap.name = v.ap_name;

-- ---------------------------------------------------------------------
-- Attendance for 27 Oct 2026 (times are South Africa Standard Time)
-- Scheduled start/end are copied from the employee's shift.
-- ---------------------------------------------------------------------
INSERT INTO attendance_records
  (employee_id, work_date, shift_id, scheduled_start, scheduled_end,
   clock_in_at, clock_out_at, clock_in_point_id, clock_out_point_id,
   status, is_manually_adjusted)
SELECT e.id, DATE '2026-10-27', s.id,
       (DATE '2026-10-27' + s.start_time) AT TIME ZONE 'Africa/Johannesburg',
       (DATE '2026-10-27' + s.end_time)   AT TIME ZONE 'Africa/Johannesburg',
       (v.cin::timestamp)  AT TIME ZONE 'Africa/Johannesburg',
       (v.cout::timestamp) AT TIME ZONE 'Africa/Johannesburg',
       CASE WHEN v.cin  IS NOT NULL THEN (SELECT id FROM access_points WHERE name = 'Main entrance') END,
       CASE WHEN v.cout IS NOT NULL THEN (SELECT id FROM access_points WHERE name = 'Main entrance') END,
       v.status, v.adjusted
FROM (VALUES
  ('EMP-1042', 'Day 08:00',   '2026-10-27 07:52', NULL,                'on_time',            false),
  ('EMP-1087', 'Day 08:00',   '2026-10-27 08:14', NULL,                'late',               false),
  ('EMP-1103', 'Day 08:30',   '2026-10-27 08:24', NULL,                'on_time',            false),
  ('EMP-1128', 'Day 08:00',   NULL,               NULL,                'absent',             false),
  ('EMP-1156', 'Early 06:00', '2026-10-27 05:54', '2026-10-27 10:31',  'incomplete',         false),
  ('EMP-1189', 'Day 08:30',   '2026-10-27 08:19', NULL,                'on_time',            false),
  ('EMP-1210', 'Late 09:00',  '2026-10-27 09:22', NULL,                'late',               false),
  ('EMP-1234', 'Early 06:00', '2026-10-27 05:51', NULL,                'approved_exception', true)
) AS v(num, shift_name, cin, cout, status, adjusted)
JOIN employees e ON e.employee_number = v.num
JOIN shifts s    ON s.name = v.shift_name;

-- One manual adjustment, with its reason, to exercise the audit trail
INSERT INTO attendance_adjustments (attendance_id, adjusted_by, reason, previous_values, new_values)
SELECT a.id,
       (SELECT id FROM users WHERE email = 'thandiwe.khumalo@example.com'),
       'Pre-approved early start for the quality inspection audit.',
       '{"status": "on_time"}'::jsonb,
       '{"status": "approved_exception"}'::jsonb
FROM attendance_records a
JOIN employees e ON e.id = a.employee_id
WHERE e.employee_number = 'EMP-1234' AND a.work_date = DATE '2026-10-27';

INSERT INTO audit_logs (actor_id, action, entity_type, entity_id, changes, reason)
SELECT (SELECT id FROM users WHERE email = 'thandiwe.khumalo@example.com'),
       'attendance.adjust', 'attendance_record', a.id::text,
       '{"status": {"from": "on_time", "to": "approved_exception"}}'::jsonb,
       'Pre-approved early start for the quality inspection audit.'
FROM attendance_records a
JOIN employees e ON e.id = a.employee_id
WHERE e.employee_number = 'EMP-1234' AND a.work_date = DATE '2026-10-27';

-- ---------------------------------------------------------------------
-- Access events (fixed IDs so screening results and reviews can reference them)
-- ---------------------------------------------------------------------
INSERT INTO access_events
  (id, employee_id, credential_id, access_point_id, device_id,
   occurred_at, direction, identity_result, decision)
SELECT v.id::uuid, e.id,
       (SELECT c.id FROM employee_credentials c
         WHERE c.employee_id = e.id AND c.credential_type = 'fingerprint'),
       (SELECT id FROM access_points WHERE name = 'Main entrance'),
       (SELECT id FROM devices WHERE label = 'Main entrance biometric scanner'),
       v.ts::timestamptz, v.direction, v.id_result, v.decision
FROM (VALUES
  ('00000000-0000-0000-0000-0000000000a1', 'EMP-1042', '2026-10-27 10:42:18+02', 'entry', 'verified',     'granted'),
  ('00000000-0000-0000-0000-0000000000a2', 'EMP-1087', '2026-10-27 10:38:05+02', 'exit',  'verified',     'granted'),
  ('00000000-0000-0000-0000-0000000000a3', 'EMP-1103', '2026-10-27 10:31:42+02', 'entry', 'verified',     'granted'),
  ('00000000-0000-0000-0000-0000000000a4', 'EMP-1156', '2026-10-27 10:24:09+02', 'entry', 'inconclusive', 'needs_review'),
  ('00000000-0000-0000-0000-0000000000a5', 'EMP-1189', '2026-10-27 10:17:36+02', 'entry', 'verified',     'needs_review'),
  ('00000000-0000-0000-0000-0000000000a6', 'EMP-1234', '2026-10-27 10:09:51+02', 'entry', 'verified',     'granted'),
  ('00000000-0000-0000-0000-0000000000a7', 'EMP-1210', '2026-10-27 09:22:40+02', 'entry', 'verified',     'granted')
) AS v(id, num, ts, direction, id_result, decision)
JOIN employees e ON e.employee_number = v.num;

-- Screening results (entries only; the exit has none)
INSERT INTO screening_results (access_event_id, screening_type, result, device_id, screened_at)
SELECT v.ev::uuid, v.stype, v.result, d.id, v.ts::timestamptz
FROM (VALUES
  ('00000000-0000-0000-0000-0000000000a1', 'breathalyser',   'clear',        'Main entrance breathalyser',   '2026-10-27 10:42:30+02'),
  ('00000000-0000-0000-0000-0000000000a1', 'metal_detector', 'clear',        'Main entrance metal detector', '2026-10-27 10:42:40+02'),
  ('00000000-0000-0000-0000-0000000000a3', 'breathalyser',   'clear',        'Main entrance breathalyser',   '2026-10-27 10:31:55+02'),
  ('00000000-0000-0000-0000-0000000000a3', 'metal_detector', 'clear',        'Main entrance metal detector', '2026-10-27 10:32:05+02'),
  ('00000000-0000-0000-0000-0000000000a4', 'breathalyser',   'inconclusive', 'Main entrance breathalyser',   '2026-10-27 10:24:20+02'),
  ('00000000-0000-0000-0000-0000000000a4', 'metal_detector', 'clear',        'Main entrance metal detector', '2026-10-27 10:24:30+02'),
  ('00000000-0000-0000-0000-0000000000a5', 'breathalyser',   'clear',        'Main entrance breathalyser',   '2026-10-27 10:17:48+02'),
  ('00000000-0000-0000-0000-0000000000a5', 'metal_detector', 'alert',        'Main entrance metal detector', '2026-10-27 10:17:58+02'),
  ('00000000-0000-0000-0000-0000000000a6', 'breathalyser',   'clear',        'Main entrance breathalyser',   '2026-10-27 10:10:03+02'),
  ('00000000-0000-0000-0000-0000000000a6', 'metal_detector', 'clear',        'Main entrance metal detector', '2026-10-27 10:10:13+02'),
  ('00000000-0000-0000-0000-0000000000a7', 'breathalyser',   'clear',        'Main entrance breathalyser',   '2026-10-27 09:22:55+02'),
  ('00000000-0000-0000-0000-0000000000a7', 'metal_detector', 'alert',        'Main entrance metal detector', '2026-10-27 09:23:05+02')
) AS v(ev, stype, result, device_label, ts)
JOIN devices d ON d.label = v.device_label;

-- A completed review: alert on Bongani's entry was cleared (so the review queue
-- shows both a pending state and a resolved example). Events a4 and a5 stay pending.
INSERT INTO access_reviews (access_event_id, reviewed_by, outcome, finding, notes, reviewed_at)
VALUES (
  '00000000-0000-0000-0000-0000000000a7',
  (SELECT id FROM users WHERE email = 'security.officer@example.com'),
  'granted_after_review', 'cleared',
  'Metal detector alert. Keys found in pocket during secondary screening; cleared.',
  '2026-10-27 09:31:00+02'
);

COMMIT;
