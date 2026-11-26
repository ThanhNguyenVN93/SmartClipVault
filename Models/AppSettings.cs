using System;
using System.Collections.Generic;

namespace SmartClipVault.Models;

public sealed class AppSettings
{
    public bool AutoStartWithWindows { get; set; }
    public bool StartHiddenToTray { get; set; }
    public bool DoubleClickTrayToToggle { get; set; } = true;
    public bool CaptureHistory { get; set; } = true;
    public List<string> ExcludedApps { get; set; } = ["KeePass", "Bitwarden", "1Password"];
    public int AutoClearDays { get; set; }
    public bool AutoStarOnDoubleCopy { get; set; }
    public double UiFontScale { get; set; } = 1.0;
    public bool IncludeImagesInBackup { get; set; } = true;
    public bool AutoBackupBeforeRestore { get; set; } = true;
    public string? PinHash { get; set; }
    public string? PinSalt { get; set; }
}
