build:
    dotnet build ./Telex.csproj -c Release
build_and_place:
    dotnet build ./Telex.csproj -c Release
    cp bin/Release/net48/*.dll "$CSII_MOD_DIRECTORY"

