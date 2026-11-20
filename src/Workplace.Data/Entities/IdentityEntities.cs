using Microsoft.AspNetCore.Identity;

namespace Workplace.Data.Entities;

public class AppUser : IdentityUser<Guid>
{
    /// <summary>The employee this login belongs to (null for non-employee accounts).</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>Deactivated users cannot sign in.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class AppRole : IdentityRole<Guid>
{
    public AppRole() { }
    public AppRole(string name) : base(name) { }
}
