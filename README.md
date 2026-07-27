<p align="center">
  <img
    src="docs/assets/meccha-camouflage-readme-banner-v151-1600w.jpg"
    alt="Meccha Camouflage demo"
    width="900"
  />
</p>

<p align="center">
  <a href="https://github.com/acentrist/MecchaCamouflage/releases/latest">
    <img
      src="https://img.shields.io/github/v/release/acentrist/MecchaCamouflage"
      alt="Latest release"
    />
  </a>
  <a href="https://github.com/acentrist/MecchaCamouflage/releases">
    <img
      src="https://img.shields.io/github/downloads/acentrist/MecchaCamouflage/total"
      alt="Total downloads"
    />
  </a>
  <a href="https://github.com/acentrist/MecchaCamouflage">
    <img
      src="https://img.shields.io/github/stars/acentrist/MecchaCamouflage"
      alt="GitHub stars"
    />
  </a>
    <a href="LICENSE.txt">
    <img
      src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue.svg"
      alt="License: GPL-3.0-or-later"
    />
  </a>
</p>

<h1>
  <img
    src="resources/app-icons/icon.png"
    alt="Meccha Camouflage icon"
    width="36"
  />
  Meccha Camouflage
</h1>

A Windows desktop app for MECCHA CHAMELEON.

## Features

- **Paint**: Paint a player character with custom colors and materials.
- **Image Paint**: Paint imported images onto a player character.
- **ESP**: Show player locations and information in game.

## Download

Download the latest <code>meccha-camouflage.exe</code> from <a href="https://github.com/acentrist/MecchaCamouflage/releases/latest">GitHub Releases</a>.

## Usage

1. Start MECCHA CHAMELEON.
2. Start `meccha-camouflage.exe`.

Logs are written under:

```text
%LOCALAPPDATA%\MecchaCamouflage\versions\<version>\logs\
```

If Windows asks, approve the UAC prompt at startup to add this Microsoft
Defender exclusion:

```text
%LOCALAPPDATA%\MecchaCamouflage\
```

## Development

```bash
git clone --recurse-submodules https://github.com/acentrist/MecchaCamouflage.git
cd MecchaCamouflage
make run
```

## Contributors

[![Contributors](https://contrib.rocks/image?repo=acentrist/MecchaCamouflage)](https://github.com/acentrist/MecchaCamouflage/graphs/contributors)

## Contributing

Pull requests welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for setup, code style, and the PR process.

## Security

Follow the disclosure process in [SECURITY.md](SECURITY.md).

## License

[GPL-3.0-or-later](LICENSE.txt) © Acentrist
