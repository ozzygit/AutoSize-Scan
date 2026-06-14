namespace AutoSizeScan.Models;

public class ScannerDevice
{
    public string Name { get; set; } = string.Empty;
    public bool IsReachable { get; set; }
    public string? StatusReason { get; set; }

    public string DisplayName => IsReachable
        ? Name
        : StatusReason == "in use"
            ? $"{Name}  (in use by another app)"
            : $"{Name}  ({StatusReason ?? "not reachable"})";

    public override string ToString() => Name;
}
