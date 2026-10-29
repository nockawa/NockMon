namespace NockMon.Core.Model;

/// <summary>Process identity: PIDs are reused, (PID, CreateTime) is not.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime);
