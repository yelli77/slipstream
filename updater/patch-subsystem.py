#!/usr/bin/env python3
"""
Patcht das PE-Subsystem-Flag einer .NET-Exe von Console (3) auf GUI/Windows (2).

Hintergrund: `dotnet publish` auf Linux (Cross-Compile fuer win-x64) kann den
generierten Apphost NICHT patchen (siehe Build-Warnung NETSDK1074), obwohl im
.csproj <OutputType>WinExe</OutputType> gesetzt ist. Der Apphost bleibt dadurch
im Console-Subsystem stecken - Windows 11 hostet solche Prozesse automatisch in
einem (dann leeren, weil wir nichts auf die Konsole schreiben) Windows-Terminal-
Fenster. Muss nach JEDEM `dotnet publish` auf dieser Linux-Buildumgebung erneut
ausgefuehrt werden, bevor der NSIS-Installer gepackt wird.

Usage: python3 patch-subsystem.py <path-to-exe>
"""
import sys

def patch(path):
    with open(path, 'r+b') as f:
        data = bytearray(f.read())
        pe_offset = int.from_bytes(data[0x3c:0x40], 'little')
        subsystem_offset = pe_offset + 0x5c
        current = int.from_bytes(data[subsystem_offset:subsystem_offset+2], 'little')
        if current == 2:
            print(f"{path}: bereits GUI-Subsystem (2), nichts zu tun.")
            return
        if current != 3:
            print(f"{path}: WARNUNG unerwarteter Subsystem-Wert {current}, breche ab.")
            sys.exit(1)
        data[subsystem_offset:subsystem_offset+2] = (2).to_bytes(2, 'little')
        f.seek(0)
        f.write(data)
        print(f"{path}: Subsystem gepatcht {current} -> 2 (GUI)")

if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("Usage: patch-subsystem.py <path-to-exe>")
        sys.exit(1)
    patch(sys.argv[1])
