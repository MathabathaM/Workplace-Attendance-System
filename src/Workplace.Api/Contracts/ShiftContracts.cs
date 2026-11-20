using System.ComponentModel.DataAnnotations;

namespace Workplace.Api.Contracts;

/// <summary>Fields used to create or update a reusable shift pattern.</summary>
public sealed record ShiftRequest
{
    [Required, StringLength(100)]
    public required string Name { get; init; }

    [Required, RegularExpression("^(day|early|late|night)$")]
    public required string Category { get; init; }

    [Required, RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public required string StartTime { get; init; }

    [Required, RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public required string EndTime { get; init; }

    [Range(0, 240)]
    public int BreakMinutes { get; init; } = 30;

    [Range(0, 60)]
    public int GraceMinutes { get; init; } = 5;

    public bool CrossesMidnight { get; init; }

    public bool IsActive { get; init; } = true;
}

/// <summary>Assigns an active shift pattern as an employee's ongoing recurring shift.</summary>
public sealed record AssignShiftRequest
{
    [Required]
    public required Guid ShiftId { get; init; }
}

/// <summary>A shift definition and the count of employees currently assigned to it.</summary>
public sealed record ShiftResponse(
    Guid Id,
    string Name,
    string Category,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int BreakMinutes,
    int GraceMinutes,
    bool CrossesMidnight,
    bool IsActive,
    int AssignedEmployeeCount);

/// <summary>An employee and their current recurring shift assignment.</summary>
public sealed record EmployeeShiftResponse(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string Department,
    Guid? ShiftId,
    string? ShiftName,
    DateOnly? EffectiveFrom);
