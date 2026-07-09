# Meccha Auto Paint

**Unofficial modified build** derived from [Meccha Camouflage](https://github.com/acentrist/MecchaCamouflage) by Acentrist.

This repository is **not** an official Meccha Camouflage release and is **not** endorsed by the upstream project.

## What this is

A Windows desktop tool for MECCHA CHAMELEON camouflage experiments, based on the upstream GPL-licensed project, with local fixes including more resilient runtime triangle-coordinate handling during paint planning.

## License (required reading)

This program is free software under **GNU GPL v3 or later**. See [LICENSE.txt](LICENSE.txt).

- Original work: Copyright (C) 2026 Acentrist — <https://github.com/acentrist/MecchaCamouflage>
- Modifications: Copyright (C) 2026 Drigotine — this fork

You must:

1. Keep the GPL license with redistributed source and binaries  
2. Provide corresponding source for any binary you distribute  
3. **Not** present this as an official Meccha Camouflage release  

See [BRANDING.md](BRANDING.md).

## Download

If a GitHub Release is published for this fork, use the release asset from:

- https://github.com/Luckisugar/meccha_auto_paint/releases

Upstream official releases (unrelated to this fork):

- https://github.com/acentrist/MecchaCamouflage/releases/latest

## Usage

1. Start MECCHA CHAMELEON.  
2. Start the published executable (or a local build).  
3. Confirm the target process and bridge state in the app.  
4. Press the saved paint hotkey.

Logs (default layout from upstream):

```text
%LOCALAPPDATA%\MecchaCamouflage\versions\<version>\logs\
```

## Development

```bash
git clone https://github.com/Luckisugar/meccha_auto_paint.git
cd meccha_auto_paint
make run
```

Requirements:

- Windows x64  
- .NET SDK (see project files)  
- Visual Studio Build Tools with C++ workload  

References (from upstream layout):

- [Repository layout](docs/repository-layout.md)  
- [Runtime maintenance](docs/runtime-maintenance.md)  
- [Research tools](docs/research-tools.md)  
- [Release checklist](docs/release-checklist.md)  

## Source of binary releases

Under GPL-3.0-or-later, binary releases of this fork correspond to the source tagged on the same GitHub repository at the release tag.
