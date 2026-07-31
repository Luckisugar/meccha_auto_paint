# Windows Smart App Control workarounds

Smart App Control (SAC) can break official single-file releases of Meccha
Camouflage on Windows 11 while older builds still run.

## Symptoms

1. **Single-file EXE does nothing / Bad Image**  
   Message like *Part of this app has been blocked* for  
   `%LOCALAPPDATA%\Temp\.net\...\meccha-camouflage.dll`

2. **GUI opens, inject fails**  
   ```text
   Bridge: direct injection failed: ... runtime-injector.exe ...
   An Application Control policy has blocked this file.
   ```

SAC is **not** the same as Defender folder exclusions. Excluding
`%LOCALAPPDATA%\MecchaCamouflage\` does not fix these.

## Fix A — Loose (folder) build (UI)

Publish without single-file packaging so the managed DLL is not extracted under
`Temp\.net`:

```powershell
.\scripts\build.ps1 -Version "v1.7.1-loose" -BuildMode DevLooseSelfContained -OutDir ".\dist\loose"
.\scripts\package-loose-launcher.ps1 -PackageDir ".\dist\loose" -Kind SelfContained
```

**Always start with `START.bat` after a browser/GitHub download.**  
Downloads get Mark of the Web (`Zone.Identifier`). SAC treats those as untrusted even when the same bytes built locally work. `START.ps1` runs `Unblock-File` on the whole folder, then prefers:

```text
"C:\Program Files\dotnet\dotnet.exe" exec meccha-camouflage.dll
```

(that host is Microsoft Authenticode-signed). Fallback: `meccha-camouflage.exe`.

### Framework-dependent package (stronger under SAC)

Smaller folder; requires [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
.\scripts\build.ps1 -Version "v1.7.1-fdd" -BuildMode DevLooseFrameworkDependent -OutDir ".\dist\fdd"
.\scripts\package-loose-launcher.ps1 -PackageDir ".\dist\fdd" -Kind FrameworkDependent
```

Do not ship only the small host EXE alone.

## Fix B — In-process inject (bridge)

This fork injects the bridge from the **already-running host process**
(`InProcessDirectInjector`) instead of `Process.Start` on
`runtime-injector.exe`. SAC often blocks the separate injector EXE even when
the host and bridge DLL are allowed.

Logs on success look like:

```text
Bridge detail: in-process injector detail=bridge_listening success=True ...
```

External `runtime-injector.exe` remains as a fallback if in-process throws.

## When you still need SAC Off

If SAC still blocks the host EXE or the staged bridge DLL inside the game
process, the only remaining user option is turning Smart App Control off
(Windows Security → App & browser control → Smart App Control). That setting is
often one-way on a given PC.

## Attribution

Upstream project: https://github.com/acentrist/MecchaCamouflage  
License: GPL-3.0-or-later
