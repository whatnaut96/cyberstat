{
    description = "Dev shell for Cyberstat Mod";
    inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.11";
    outputs = { self, nixpkgs }:
      let
        pkgs = nixpkgs.legacyPackages.x86_64-linux;
      in {
        devShells.x86_64-linux.default = pkgs.mkShell {
          buildInputs = [
            pkgs.dotnet-sdk_8
            pkgs.mono
            pkgs.just
            pkgs.python3
            pkgs.openssl
          ];
          shellHook = ''
            export CSII_MANAGEDPATH="$HOME/.local/share/Steam/steamapps/common/Cities Skylines II/Cities2_Data/Managed"
            export CSII_USERDATAPATH="$HOME/.local/share/Steam/steamapps/compatdata/949230/pfx/drive_c/users/steamuser/AppData/LocalLow/Colossal Order/Cities Skylines II"
            export CSII_LOCALMODSPATH="$CSII_USERDATAPATH/.cache/Mods/local"
        '';
        };
      };
}
