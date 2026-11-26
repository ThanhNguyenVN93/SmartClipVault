# SmartClipVault

![SmartClipVault](Assets/smartclipvault-cover.jpg)

**SmartClipVault** is a lightweight, privacy-first **Windows 11 Clipboard Manager, Prompt Vault, and Offline OCR Utility** built with .NET 10 and WPF. Everything runs 100% locally on your machine with SQLite storage—no cloud accounts, no telemetry, and no external runtime required.

---

## Key Features

- **2-Column Board UI & Windows 11 QuickPaste**: Browse recent clipboard captures alongside pinned favorites and paste with a single click.
- **Double-Copy to Star**: Copy any text twice quickly to star and pin it to the top of your board.
- **Prompt Vault (`{{clip}}` Template Injection)**: Save reusable AI prompt templates with `{{clip}}` and automatically inject your active clipboard content.
- **Offline Windows OCR Service**: Extract text from screenshots and images 100% offline using native Windows 10/11 OCR APIs.
- **Automatic SecretDetector**: Automatically flags sensitive tokens, API keys, and credentials in your clipboard history.
- **PIN Lock Screen**: Protect your local vault when stepping away from your PC.
- **TextTransformService**: Clean, format, and transform copied text on the fly before pasting.
- **Atomic Backup & Restore**: Export and restore your vault safely with built-in Zip-Slip and Zip-Bomb protection.
- **EN/VI Localization & Auto-Updater**: Full English and Vietnamese UI with a secure SHA-256 verified auto-updater in the system tray.

---

## Getting Started

1. Download `SmartClipVault-vX.Y.Z-win-x64.zip` from the [Releases](../../releases) page.
2. Extract `SmartClipVault.exe` and run it directly (single-file self-contained `.exe`, no installation needed).
3. Access SmartClipVault anytime from the system tray or QuickPaste shortcut.

---

## Building from Source

```powershell
dotnet test SelfTest/SelfTest.csproj -c Release
dotnet publish SmartClipVault.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

---

## License & Copyright

Copyright © 2026 ThanhNguyenVN93
