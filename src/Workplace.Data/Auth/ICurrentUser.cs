using System.Net;

namespace Workplace.Data.Auth;

/// <summary>
/// Who is making the current request. Implemented by the API from the signed-in
/// principal; the data layer uses it for row scoping and audit logging.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? EmployeeId { get; }
    IReadOnlyCollection<string> Roles { get; }
    IPAddress? IpAddress { get; }

    bool IsInRole(string role) => Roles.Contains(role);
}
