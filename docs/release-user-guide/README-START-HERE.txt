Meccha Camouflage unofficial package (drigotine)
================================================

IMPORTANT if you downloaded this from GitHub/browser:

1.  Extract the WHOLE folder somewhere local (Desktop is fine).
2.  Right-click START.bat -> Properties -> []Unblock -> check it.
3.  Double left-click START.bat (or run START.ps1 in powershell).
3.5 This procedure should remove "Mark of the Web" so Smart App Control (SAC) is less likely to block files.
4.  A black terminal should open, along with a .NET icon window with the Meccha Camouflage app in it.
5.  Start MECCHA CHAMELEON.
6.  Enjoy.

Launch order inside START.ps1:
  - Unblock every file in this folder
  - Prefer: Microsoft-signed  C:\Program Files\dotnet\dotnet.exe  exec meccha-camouflage.dll
  - Fallback: meccha-camouflage.exe

Requirements:
  - Windows 10/11 x64
  - .NET 10 Desktop Runtime (WindowsDesktop.App 10.x)
    https://dotnet.microsoft.com/download/dotnet/10.0

Source: https://github.com/Luckisugar/meccha_auto_paint
Upstream: https://github.com/acentrist/MecchaCamouflage
License: GPL-3.0-or-later (unofficial modified build)
