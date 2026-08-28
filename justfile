build:
    dotnet build ./Cyberstat.csproj -c Release

build_and_place:
    dotnet build ./Cyberstat.csproj -c Release
    mkdir -p "$CSII_LOCALMODSPATH/Cyberstat"
    cp bin/Release/net48/Cyberstat.dll "$CSII_LOCALMODSPATH/Cyberstat/"
    cp bin/Release/net48/Cyberstat.pdb "$CSII_LOCALMODSPATH/Cyberstat/"
    cp mod.json "$CSII_LOCALMODSPATH/Cyberstat/"

dummy_sink:
    python3 scripts/dummy_sink.py --output-dir sink-payloads
