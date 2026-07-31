<p align="center">
  <img
    src="docs/assets/meccha-camouflage-readme-banner-v151-1600w.jpg"
    alt="Meccha Camouflage demo"
    width="900"
  />
</p>

# Meccha Camouflage (unofficial)

Windows desktop helper for **MECCHA CHAMELEON** (Paint, Image Paint, ESP).

> **Unofficial fork** maintained here for builds that work under Windows 11
> **Smart App Control**. Not affiliated with or endorsed by upstream.
>
> Upstream: [acentrist/MecchaCamouflage](https://github.com/acentrist/MecchaCamouflage)  
> License: [GPL-3.0-or-later](LICENSE.txt) — original copyright Acentrist; modifications Drigotine / Luckisugar.

## Why this fork exists

On some Windows 11 PCs with **Smart App Control** enabled:

| Official single-file release | This fork |
|------------------------------|-----------|
| Host dies while unpacking under `%TEMP%\.net\` | **Loose folder** publish (no Temp extract) |
| `runtime-injector.exe` blocked on inject | **In-process inject** from the host (no second EXE) |

Details: [docs/windows-smart-app-control.md](docs/windows-smart-app-control.md)

## Features

- **Paint** — custom colors and materials  
- **Image Paint** — paint imported images onto the character  
- **ESP** — player locations / info overlay  

## Download

Use **Releases** on this repository for the prebuilt **loose** package when available.

Or build yourself (below). Prefer the **loose** layout under SAC; do not rely on a lone single-file `.exe` if Windows shows *Part of this app has been blocked*.

## Usage

1. Start MECCHA CHAMELEON.  
2. Start `meccha-camouflage.exe` from the **loose folder** (keep all DLLs next to it).  
3. Connect / inject as usual.

Logs:

```text
%LOCALAPPDATA%\MecchaCamouflage\versions\<version>\logs\
```

Optional Defender exclusion (does **not** replace SAC fixes):

```text
%LOCALAPPDATA%\MecchaCamouflage\
```

## Build (Windows)

Needs:

- Git + PowerShell  
- [.NET SDK 10](https://dotnet.microsoft.com/download)  
- VS 2022 Build Tools with **Desktop development with C++** (x64 `cl.exe`)

```powershell
git clone --recurse-submodules https://github.com/Luckisugar/meccha_auto_paint.git
cd meccha_auto_paint
git submodule update --init --recursive

# Loose self-contained (recommended under Smart App Control)
.\scripts\build.ps1 `
  -Version "v1.7.1-loose-inproc" `
  -BuildMode DevLooseSelfContained `
  -OutDir ".\dist\loose"
```

Output: `dist\loose\meccha-camouflage.exe` plus side-by-side runtime files.

Single-file (same packaging as upstream releases — often blocked by SAC):

```powershell
.\scripts\build.ps1 -Version "v1.7.1-local"
# -> .build\bin\meccha-camouflage.exe
```

With GNU Make: `make build` / `make build-dev` / `make run`.

## Changes vs upstream (this fork)

- **In-process direct inject** — `InProcessDirectInjector` + host path in `RuntimeBridgeService` so SAC cannot block `runtime-injector.exe` process start.  
- **Documented loose packaging** for SAC single-file extract failures.  
- Prior unofficial paint resilience work (see git history / older tags).

See [BRANDING.md](BRANDING.md): modified builds must not claim to be official upstream releases.

## Development

```powershell
.\scripts\build.ps1 -BuildMode DevLooseSelfContained -OutDir .build\bin-dev
# or
make run
```

## Security

Report security issues privately per [SECURITY.md](SECURITY.md) when present; otherwise prefer private disclosure to the maintainer.

## License

[GPL-3.0-or-later](LICENSE.txt)

- Original work: **Acentrist** — https://github.com/acentrist/MecchaCamouflage  
- Unofficial modifications: **Drigotine / Luckisugar** — this repository  
