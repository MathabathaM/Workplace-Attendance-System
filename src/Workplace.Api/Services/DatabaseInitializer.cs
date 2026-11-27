using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Workplace.Data;
using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Api.Services;

public static class DatabaseInitializer
{
    private static readonly (string Email, string Role, string? EmployeeNumber)[] DemoUsers =
    [
        ("thabo.mokoena@example.com", RoleNames.SystemAdministrator, "EMP-1001"),
        ("thandiwe.khumalo@example.com", RoleNames.HrOfficer, "EMP-1128"),
        ("lerato.nkosi@example.com", RoleNames.LineManager, "EMP-1042"),
        ("security.officer@example.com", RoleNames.SecurityOfficer, null),
        ("amara.patel@example.com", RoleNames.Employee, "EMP-1103"),
        ("auditor@example.com", RoleNames.Auditor, null)
    ];

    public static async Task SeedDevelopmentDataAsync(IServiceProvider services, IConfiguration config)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        var roles = provider.GetRequiredService<RoleManager<AppRole>>();
        var users = provider.GetRequiredService<UserManager<AppUser>>();

        foreach (var roleName in new[]
        {
            RoleNames.SystemAdministrator,
            RoleNames.HrOfficer,
            RoleNames.LineManager,
            RoleNames.SecurityOfficer,
            RoleNames.Employee,
            RoleNames.Auditor
        })
        {
            if (await roles.RoleExistsAsync(roleName)) continue;
            var result = await roles.CreateAsync(new AppRole(roleName));
            if (!result.Succeeded)
                throw new InvalidOperationException($"Could not create role '{roleName}': {FormatErrors(result.Errors)}");
        }

        if (!await db.Employees.AnyAsync())
            await SeedEmployeesAsync(db);

        await SeedAttendanceAsync(db);

        var password = config["Seed:DevPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:DevPassword is not set; demo accounts cannot sign in.");
            return;
        }

        foreach (var (email, roleName, employeeNumber) in DemoUsers)
        {
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                var employeeId = employeeNumber is null
                    ? null
                    : await db.Employees.Where(e => e.EmployeeNumber == employeeNumber)
                        .Select(e => (Guid?)e.Id).SingleAsync();
                user = new AppUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    EmployeeId = employeeId,
                    IsActive = true
                };
                var created = await users.CreateAsync(user, password);
                if (!created.Succeeded)
                    throw new InvalidOperationException($"Could not create demo user '{email}': {FormatErrors(created.Errors)}");
                logger.LogInformation("Created development demo user {Email}", email);
            }

            if (!await users.IsInRoleAsync(user, roleName))
            {
                var added = await users.AddToRoleAsync(user, roleName);
                if (!added.Succeeded)
                    throw new InvalidOperationException($"Could not assign role '{roleName}' to '{email}': {FormatErrors(added.Errors)}");
            }
        }
    }

    public static async Task BootstrapProductionAdministratorAsync(
        IServiceProvider services, IConfiguration config)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<AppDbContext>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        var roles = provider.GetRequiredService<RoleManager<AppRole>>();
        var users = provider.GetRequiredService<UserManager<AppUser>>();

        if (!await roles.RoleExistsAsync(RoleNames.SystemAdministrator))
        {
            var roleResult = await roles.CreateAsync(new AppRole(RoleNames.SystemAdministrator));
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(
                    $"Could not create the System Administrator role: {FormatErrors(roleResult.Errors)}");
        }

        var bootstrapEmail = config["BootstrapAdmin:Email"]?.Trim();
        var bootstrapPassword = config["BootstrapAdmin:Password"];
        if (!string.IsNullOrWhiteSpace(bootstrapEmail) &&
            !string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            var existingBootstrapAdmin = await users.FindByEmailAsync(bootstrapEmail);
            if (existingBootstrapAdmin is { IsActive: true } &&
                await users.IsInRoleAsync(existingBootstrapAdmin, RoleNames.SystemAdministrator) &&
                !await users.HasPasswordAsync(existingBootstrapAdmin))
            {
                var passwordResult = await users.AddPasswordAsync(existingBootstrapAdmin, bootstrapPassword);
                if (!passwordResult.Succeeded)
                    throw new InvalidOperationException(
                        $"Could not initialize the password for the existing System Administrator: {FormatErrors(passwordResult.Errors)}");

                logger.LogInformation(
                    "Initialized the password for existing passwordless System Administrator {Email}",
                    existingBootstrapAdmin.Email);
                return;
            }
        }

        var administrators = await users.GetUsersInRoleAsync(RoleNames.SystemAdministrator);
        if (administrators.Any(user => user.IsActive))
            return;

        if (await users.Users.AnyAsync())
        {
            logger.LogWarning(
                "No active System Administrator exists. Existing accounts were left unchanged; configure an administrator through the approved recovery process.");
            return;
        }

        var email = config["BootstrapAdmin:Email"]?.Trim();
        var password = config["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "A fresh production database requires BootstrapAdmin:Email and BootstrapAdmin:Password environment variables.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var administrator = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            IsActive = true
        };
        var created = await users.CreateAsync(administrator, password);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                $"Could not create the initial System Administrator: {FormatErrors(created.Errors)}");

        var assigned = await users.AddToRoleAsync(administrator, RoleNames.SystemAdministrator);
        if (!assigned.Succeeded)
            throw new InvalidOperationException(
                $"Could not assign the System Administrator role: {FormatErrors(assigned.Errors)}");

        await transaction.CommitAsync();
        logger.LogInformation("Created the initial production System Administrator account {Email}", email);
    }

    private static async Task SeedEmployeesAsync(AppDbContext db)
    {
        var site = new Site
        {
            Name = "North Campus",
            City = "Johannesburg",
            Country = "ZA",
            Timezone = "Africa/Johannesburg"
        };
        var departments = new[]
        {
            new Department { Name = "Operations" },
            new Department { Name = "Finance" },
            new Department { Name = "People & Culture" },
            new Department { Name = "Logistics" }
        };
        var shifts = new[]
        {
            new Shift { Name = "Day 08:00", Category = "day", StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 30) },
            new Shift { Name = "Day 08:30", Category = "day", StartTime = new TimeOnly(8, 30), EndTime = new TimeOnly(17, 0) },
            new Shift { Name = "Early 06:00", Category = "early", StartTime = new TimeOnly(6, 0), EndTime = new TimeOnly(14, 30) },
            new Shift { Name = "Late 09:00", Category = "late", StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 30) }
        };

        db.Sites.Add(site);
        db.Departments.AddRange(departments);
        db.Shifts.AddRange(shifts);
        await db.SaveChangesAsync();

        var departmentByName = departments.ToDictionary(item => item.Name);
        var shiftByName = shifts.ToDictionary(item => item.Name);
        var employeeData = new[]
        {
            ("EMP-1001", "Thabo", "Mokoena", "thabo.mokoena@example.com", "Systems Administrator", "Operations", "Day 08:00", EmploymentStatuses.Active),
            ("EMP-1042", "Lerato", "Nkosi", "lerato.nkosi@example.com", "Operations Lead", "Operations", "Day 08:00", EmploymentStatuses.Active),
            ("EMP-1087", "Sipho", "Dlamini", "sipho.dlamini@example.com", "Warehouse Supervisor", "Logistics", "Day 08:00", EmploymentStatuses.Active),
            ("EMP-1103", "Amara", "Patel", "amara.patel@example.com", "Accounts Officer", "Finance", "Day 08:30", EmploymentStatuses.Active),
            ("EMP-1128", "Thandiwe", "Khumalo", "thandiwe.khumalo@example.com", "HR Coordinator", "People & Culture", "Day 08:00", EmploymentStatuses.Active),
            ("EMP-1156", "Kabelo", "Molefe", "kabelo.molefe@example.com", "Safety Technician", "Operations", "Early 06:00", EmploymentStatuses.Active),
            ("EMP-1189", "Nadia", "Jacobs", "nadia.jacobs@example.com", "Payroll Analyst", "Finance", "Day 08:30", EmploymentStatuses.OnLeave),
            ("EMP-1210", "Bongani", "Maseko", "bongani.maseko@example.com", "Fleet Coordinator", "Logistics", "Late 09:00", EmploymentStatuses.Active),
            ("EMP-1234", "Zanele", "Ndlovu", "zanele.ndlovu@example.com", "Quality Inspector", "Operations", "Early 06:00", EmploymentStatuses.Active)
        };

        var employees = employeeData.Select(row => new Employee
        {
            Id = Guid.NewGuid(),
            EmployeeNumber = row.Item1,
            FirstName = row.Item2,
            LastName = row.Item3,
            Email = row.Item4,
            JobTitle = row.Item5,
            SiteId = site.Id,
            DepartmentId = departmentByName[row.Item6].Id,
            EmploymentStatus = row.Item8,
            HireDate = new DateOnly(2024, 1, 15)
        }).ToArray();

        var employeeByNumber = employees.ToDictionary(item => item.EmployeeNumber);
        employeeByNumber["EMP-1001"].ManagerId = employeeByNumber["EMP-1042"].Id;
        employeeByNumber["EMP-1156"].ManagerId = employeeByNumber["EMP-1042"].Id;
        employeeByNumber["EMP-1234"].ManagerId = employeeByNumber["EMP-1042"].Id;
        employeeByNumber["EMP-1210"].ManagerId = employeeByNumber["EMP-1087"].Id;

        db.Employees.AddRange(employees);
        await db.SaveChangesAsync();

        var effectiveFrom = new DateOnly(2026, 1, 1);
        db.EmployeeShiftAssignments.AddRange(employeeData.Select(row => new EmployeeShiftAssignment
        {
            EmployeeId = employeeByNumber[row.Item1].Id,
            ShiftId = shiftByName[row.Item7].Id,
            EffectiveFrom = effectiveFrom
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SeedAttendanceAsync(AppDbContext db)
    {
        var today = SiteClock.Today;
        var existingEmployeeIds = await db.AttendanceRecords
            .Where(record => record.ShiftDate == today)
            .Select(record => record.EmployeeId)
            .ToHashSetAsync();

        var examples = new (string EmployeeNumber, string Status, TimeOnly? ClockIn, TimeOnly? ClockOut)[]
        {
            ("EMP-1042", AttendanceStatuses.OnTime, new TimeOnly(7, 52), null),
            ("EMP-1087", AttendanceStatuses.Late, new TimeOnly(8, 14), null),
            ("EMP-1103", AttendanceStatuses.OnTime, new TimeOnly(8, 24), null),
            ("EMP-1128", AttendanceStatuses.Absent, null, null),
            ("EMP-1156", AttendanceStatuses.Incomplete, new TimeOnly(5, 54), new TimeOnly(10, 31)),
            ("EMP-1189", AttendanceStatuses.OnTime, new TimeOnly(8, 19), null),
            ("EMP-1210", AttendanceStatuses.Late, new TimeOnly(9, 22), null),
            ("EMP-1234", AttendanceStatuses.ApprovedException, new TimeOnly(5, 51), null)
        };
        var employeeNumbers = examples.Select(example => example.EmployeeNumber).ToArray();
        var employees = await db.Employees
            .Where(employee => employeeNumbers.Contains(employee.EmployeeNumber))
            .Include(employee => employee.ShiftAssignments.Where(assignment => assignment.EffectiveTo == null))
            .ThenInclude(assignment => assignment.Shift)
            .ToDictionaryAsync(employee => employee.EmployeeNumber);

        foreach (var example in examples)
        {
            if (!employees.TryGetValue(example.EmployeeNumber, out var employee) ||
                existingEmployeeIds.Contains(employee.Id))
                continue;

            var shift = employee.ShiftAssignments.FirstOrDefault()?.Shift;
            if (shift is null) continue;

            db.AttendanceRecords.Add(new AttendanceRecord
            {
                EmployeeId = employee.Id,
                ShiftDate = today,
                ScheduledStart = shift.StartTime,
                ClockIn = example.ClockIn,
                ClockOut = example.ClockOut,
                Status = example.Status
            });
        }

        await db.SaveChangesAsync();
    }

    private static string FormatErrors(IEnumerable<IdentityError> errors) =>
        string.Join("; ", errors.Select(error => error.Description));
}
