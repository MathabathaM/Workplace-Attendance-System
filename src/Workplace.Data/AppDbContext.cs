using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Net;
using Workplace.Data.Entities;

namespace Workplace.Data;

/// <summary>
/// Maps workplace data and Identity to the relational database. Column names
/// are snake_case via UseSnakeCaseNamingConvention (set by the API).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeShiftAssignment> EmployeeShiftAssignments => Set<EmployeeShiftAssignment>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // ---- Identity tables -> our table names ----
        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
            e.HasIndex(u => u.Email).IsUnique();
        });
        b.Entity<AppRole>().ToTable("roles");
        b.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        b.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        b.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        b.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        // ---- Organisation ----
        b.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
        });
        b.Entity<Department>(e =>
        {
            e.ToTable("departments");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
        });
        b.Entity<Shift>(e =>
        {
            e.ToTable("shifts");
            e.HasIndex(x => x.Name).IsUnique();
        });

        // ---- People ----
        b.Entity<Employee>(e =>
        {
            e.ToTable("employees");
            e.Property(x => x.FullName)
                .HasComputedColumnSql("first_name || ' ' || last_name", stored: true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
            e.HasIndex(x => x.EmployeeNumber).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.DepartmentId);
            e.HasIndex(x => x.ManagerId);
            e.HasIndex(x => x.EmploymentStatus);

            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId);
            e.HasMany(x => x.ShiftAssignments).WithOne(a => a.Employee).HasForeignKey(a => a.EmployeeId);
        });

        b.Entity<EmployeeShiftAssignment>(e =>
        {
            e.ToTable("employee_shift_assignments");
            e.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId);
            e.HasIndex(x => x.EmployeeId).HasFilter("effective_to IS NULL").IsUnique();
        });

        b.Entity<AttendanceRecord>(e =>
        {
            e.ToTable("attendance_records");
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.EmployeeId, x.ShiftDate }).IsUnique();
            e.HasIndex(x => x.ShiftDate);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ---- Audit ----
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.IpAddress).HasConversion(new ValueConverter<IPAddress?, string?>(
                address => address == null ? null : address.ToString(),
                value => value == null ? null : IPAddress.Parse(value)));
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP").ValueGeneratedOnAdd();
        });
    }
}
