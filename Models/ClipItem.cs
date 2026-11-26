using System;

namespace SmartClipVault.Models;

public enum ClipContentType
{
    Text,
    Image,
    Files
}

public class ClipItem
{
    public long Id { get; set; }
    public ClipContentType ContentType { get; set; }
    public string? TextContent { get; set; }
    public string? ImagePath { get; set; }
    public string? OcrText { get; set; }
    public bool IsSensitive { get; set; }
    public bool IsStarred { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long? PinBoardId { get; set; }
}
