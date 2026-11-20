# Workplace API (ASP.NET Core 10 + SQLite + Identity)

The backend provides **sign-in, the employee directory, attendance review,
shift patterns, recurring employee schedules, Line Manager scoping, and audited changes**. It uses an on-disk SQLite database,
so no PostgreSQL server or administrator installation is required.

## Requirements
- .NET 10 SDK (LTS)

## Run locally
From this `workplace-api` folder:

```powershell
dotnet user-secrets set "Seed:DevPassword" "Choose-a-unique-dev-password" --project .\src\Workplace.Api
dotnet run --project .\src\Workplace.Api --launch-profile http
```

The API creates the SQLite database and fictional development data on first
startup. The default database location is:

```text
%LOCALAPPDATA%\Workplace\workplace.db
```

The development seeder creates fictional sample accounts only when running in
Development. Set `Seed:DevPassword` in .NET user-secrets as shown above; never
put a real password in `appsettings.json` or commit it to GitHub. Sample account
emails are listed below, and all use the development password you configured.
The seeder does not run outside Development.

| Email | Role |
|---|---|
| `thabo.mokoena@example.com` | System Administrator |
| `thandiwe.khumalo@example.com` | HR Officer |
| `lerato.nkosi@example.com` | Line Manager |
| `security.officer@example.com` | Security Officer |
| `amara.patel@example.com` | Employee |
| `auditor@example.com` | Auditor |

Set `Database:Path` in `src/Workplace.Api/appsettings.json` or with the
`Database__Path` environment variable to use another writable location. No
connection string, PostgreSQL service, or database setup command is needed.

Open the frontend at **http://localhost:5080/**. Do not open `wwwroot/index.html`
as a `file://` page for API sign-in; the frontend and API share the
authentication cookie on the same origin. The OpenAPI document in Development
is at http://localhost:5080/openapi/v1.json.

## Host with Docker

The included Dockerfile publishes the API and frontend together. The app binds
to the host-provided `PORT` (default `8080`) and stores SQLite at
`/data/workplace.db`. Configure a persistent disk mounted at `/data`; without a
persistent disk, account and attendance data can be lost when the host replaces
the container.

On the hosting provider, configure these environment variables as secrets:

- `ASPNETCORE_ENVIRONMENT=Production`
- `Database__Path=/data/workplace.db`
- `BootstrapAdmin__Email` = the first administrator's sign-in email
- `BootstrapAdmin__Password` = a unique password meeting the 12-character minimum
- `PORT` = the port assigned by the hosting provider (or leave the Docker default `8080`)

On its first run with a new, empty database, the app creates the initial
System Administrator using those bootstrap secrets. After the first successful
deployment, remove `BootstrapAdmin__Email` and `BootstrapAdmin__Password` from
the hosting settings. They are never used to overwrite an existing account.
The host must provide HTTPS and persistent storage; do not use the development
sample accounts or seed password on a public deployment.

Build and run locally with Docker from this folder:

```powershell
docker build -t workplace-attendance .
docker volume create workplace-data
docker run --rm -p 8080:8080 `
  -e ASPNETCORE_ENVIRONMENT=Production `
  -e BootstrapAdmin__Email=admin@example.com `
  -e BootstrapAdmin__Password=Replace-With-A-Unique-Secret `
  -v workplace-data:/data workplace-attendance
```

Open http://localhost:8080/ for this local container. Replace the example
bootstrap values with your own, and do not commit them.

## Deploy to Vercel

Set the Vercel Framework Preset to **Container** and leave Root Directory at the
repository root. Vercel uses `Dockerfile.vercel`, which binds to the default
container port and stores SQLite under `/tmp`.

Add these Production environment variables in the Vercel project settings before
the first deployment:

- `BootstrapAdmin__Email` = the initial administrator's sign-in email
- `BootstrapAdmin__Password` = a unique password meeting the 12-character minimum

The Vercel container filesystem is temporary. `/tmp/workplace.db` is suitable
only for a disposable demo; attendance data can be lost when Vercel replaces
the container. Do not use this configuration for real attendance records.
Use a host with persistent disk storage or migrate the application to a managed
database before production use.

## Build and test

```powershell
dotnet build
dotnet test
```

## API endpoints

| Method | Route | Access |
|---|---|---|
| POST | `/api/auth/login`, `/logout`, `/change-password` | Anyone / signed in |
| GET | `/api/auth/me` | Signed in |
| GET | `/api/employees` (search, departmentId, status, sort, page, pageSize) | Admin, HR, Line Manager (scoped) |
| GET | `/api/employees/summary`, `/api/employees/{id}`, `/api/lookups` | Admin, HR, Line Manager (scoped) |
| POST | `/api/employees` (employee details plus `initialPassword`) | Admin, HR |
| PUT | `/api/employees/{id}` | Admin, HR |
| POST | `/api/employees/{id}/account` (`temporaryPassword`) | Admin, HR |
| GET | `/api/admin/roles`, `/api/admin/employees`, `/api/admin/users` | System Administrator |
| POST | `/api/admin/users` | System Administrator |
| GET, PUT | `/api/admin/users/{id}` | System Administrator |
| POST | `/api/admin/users/{id}/temporary-password` | System Administrator |
| GET | `/api/attendance` (date, search, departmentId, status, page, pageSize) | Admin, HR, Line Manager (scoped), Auditor |
| GET | `/api/attendance/reports` (fromDate, toDate, departmentId; maximum 366 days) | Admin, HR, Auditor, Line Manager (scoped) |
| PUT | `/api/attendance/{id}` | Admin, HR, Line Manager (scoped) |
| GET | `/api/me/portal`, `/api/me/attendance` (page, pageSize) | Employee (own data only) |
| GET | `/api/audit-logs` (fromDate, toDate, entityType, search, page, pageSize; maximum 366 days) | System Administrator, Auditor |
| GET | `/api/shifts`, `/api/shifts/{id}`, `/api/shifts/assignments` | Admin, HR |
| POST, PUT, DELETE | `/api/shifts`, `/api/shifts/{id}` | Admin, HR |
| PUT | `/api/shifts/assignments/{employeeId}` | Admin, HR |

## Design notes
- Authorization is enforced by the API. Line Managers see only their direct
  reports through `EmployeeQueries.VisibleTo`, including in attendance reports
  and department filters.
- `AuditInterceptor` writes an audit row in the same save transaction as each
  employee or shift-assignment change.
- Authentication uses an HttpOnly, `SameSite=Strict` cookie. Accounts lock for
  15 minutes after five failed attempts; security-stamp checks apply password
  changes and deactivation within about a minute.
- Creating an employee also creates their Employee login with the administrator-
  supplied temporary password. Employees must choose a new password before
  using any other API. Existing profiles can be provisioned or given a new
  temporary password by editing the employee and entering a temporary password,
  or by calling `POST /api/employees/{id}/account`. Share temporary passwords
  with employees securely; email delivery is not configured.
- System Administrators can create accounts, assign existing roles, activate or
  deactivate accounts, and reset temporary passwords from User management.
  Employee and Line Manager accounts must be linked to an employee record.
  Role and account changes are audited; the last active System Administrator
  cannot be removed or deactivated.
- Shift changes keep history: the current assignment is closed before a new one
  is opened.
- Attendance records are created for the development sample data and stored in
  SQLite. Attendance review changes require a reason and create an audit-log
  row in the same save operation.
- Auditors have read-only access to attendance, date-range attendance reports,
  and the paged audit trail. Audit entries show the actor, action, affected
  record, saved changes, reason, and source IP. Access and screening events are
  currently simulated in the security preview and are not persisted for audit.
- Line Managers can review and generate date-range reports for their direct
  reports only. Attendance adjustments remain limited to those same employees
  and require a reason that is added to the audit trail.
- Attendance dates use the North Campus site timezone (`Africa/Johannesburg`).
  Existing SQLite databases receive the attendance table automatically.
- Employee accounts can view their assigned shift, weekly recorded hours, and
  their own paged attendance history. The employee portal is read-only; clock-in
  and clock-out remain tied to workplace devices, and personal access history
  will require access-event/device integration.
- HR Officers, System Administrators, and Auditors can generate date-range
  attendance reports with daily trends and per-employee totals, optionally
  filtered by department. The frontend exports the complete report as CSV.
- Shift pattern changes and employee recurring assignments are audited. An
  employee assignment change takes effect today; previous assignments are
  retained through their effective dates. A shift cannot be deactivated while
  employees remain assigned to it.
- Terminated employees are hidden unless `status=terminated` is requested.
- `database/schema.sql` and `database/seed.sql` are retained as legacy
  PostgreSQL reference files; the running SQLite application does not execute
  them. Its schema is created from the EF Core model on first startup.

The Access & screening page remains a preview; its API is not implemented yet.
