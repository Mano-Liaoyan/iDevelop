namespace IDevelop.Desktop.Execution;

/// <summary>
/// PlanWeave's status tones. Controls take them as the style classes running, complete, problem, waiting, which uses
/// PlanWeave's selected color, and warning, the state color of a result that needs updating (#90).
/// </summary>
public enum StatusTone { Neutral, Running, Complete, Problem, Waiting, Warning }
