using Workplace.Data.Auth;
using Workplace.Data.Entities;

namespace Workplace.Data.Queries;

public static class EmployeeQueries
{
    /// <summary>
    /// Restricts an employee query to the rows the current user may see.
    /// Apply this to EVERY employee query that serves a user request.
    ///  - System Administrator / HR Officer: everyone
    ///  - Line Manager: their direct reports (employees.manager_id = their employee id)
    ///  - anyone else: nobody
    /// </summary>
    public static IQueryable<Employee> VisibleTo(this IQueryable<Employee> query, ICurrentUser user)
    {
        if (user.IsInRole(RoleNames.SystemAdministrator) || user.IsInRole(RoleNames.HrOfficer))
            return query;

        if (user.IsInRole(RoleNames.LineManager) && user.EmployeeId is Guid managerId)
            return query.Where(e => e.ManagerId == managerId);

        return query.Where(e => false);
    }
}
