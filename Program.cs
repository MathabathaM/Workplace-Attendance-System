using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Workplace.Api.Services;
using Workplace.Api.Middleware;
using Workplace.Data;
using Workplace.Data.Auditing;
using Workplace.Data.Auth;
using Workplace.Data.Entities;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

var platformPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(platformPort) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    if (!int.TryParse(platformPort, out var port) || port is < 1 or > 65535)
        throw new InvalidOperationException("The PORT environment variable must be a valid TCP port.");

    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// ---- Current user + audit ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<AuditInterceptor>();

// ---- Local database ----
var databasePath = builder.Configuration["Database:Path"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    databasePath = Path.Combine(localData, "Workplace", "workplace.db");
}

databasePath = Path.GetFullPath(databasePath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    ForeignKeys = true
}.ToString();

builder.Services.AddDbContext<AppDbContext>((sp, options) => options
    .UseSqlite(connectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));

// ---- ASP.NET Core Identity ----
builder.Services
    .AddIdentity<AppUser, AppRole>(options =>
    {
        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        // Keep the Identity schema compatible with the local tables created by EF.
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version2;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "workplace.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // This is an API: answer 401/403 instead of redirecting to a login page.
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// Re-check the security stamp every minute so password changes / deactivation take effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

// ---- Authorization: who may call what ----
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.ViewEmployees, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer, RoleNames.LineManager))
    .AddPolicy(Policies.ManageEmployees, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer))
    .AddPolicy(Policies.ViewAttendance, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer, RoleNames.LineManager, RoleNames.Auditor))
    .AddPolicy(Policies.ManageAttendance, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer, RoleNames.LineManager))
    .AddPolicy(Policies.ViewSchedules, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer))
    .AddPolicy(Policies.ManageSchedules, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer))
    .AddPolicy(Policies.ViewReports, p => p.RequireRole(
        RoleNames.SystemAdministrator, RoleNames.HrOfficer, RoleNames.LineManager, RoleNames.Auditor));

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS attendance_records (
            id TEXT NOT NULL PRIMARY KEY,
            employee_id TEXT NOT NULL,
            shift_date TEXT NOT NULL,
            scheduled_start TEXT NOT NULL,
            clock_in TEXT NULL,
            clock_out TEXT NULL,
            status TEXT NOT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            FOREIGN KEY (employee_id) REFERENCES employees(id) ON DELETE RESTRICT
        );
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS ix_attendance_records_employee_id_shift_date
        ON attendance_records (employee_id, shift_date);
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS ix_attendance_records_shift_date
        ON attendance_records (shift_date);
        """);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await DatabaseInitializer.SeedDevelopmentDataAsync(app.Services, app.Configuration);
}
else
{
    await DatabaseInitializer.BootstrapProductionAdministratorAsync(app.Services, app.Configuration);
    app.UseExceptionHandler();
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseMiddleware<TemporaryPasswordMiddleware>();
app.UseAuthorization();
app.MapControllers();

app.Run();
