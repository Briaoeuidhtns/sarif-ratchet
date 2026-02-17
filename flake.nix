{
  description = "SARIF Ratchet - A tool for error ratcheting using SARIF files";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs = { self, nixpkgs, flake-utils }:
    flake-utils.lib.eachDefaultSystem (system:
      let
        pkgs = import nixpkgs {
          inherit system;
          config.allowUnfree = true;
        };

        dotnet-sdk = pkgs.dotnet-sdk_10;
        dotnet-runtime = pkgs.dotnet-runtime_10;

        sarif-ratchet = pkgs.buildDotnetModule {
          pname = "sarif-ratchet";
          version = "1.0.0";

          src = ./.;

          projectFile = "src/SarifRatchet/SarifRatchet.csproj";
          nugetDeps = ./deps.json;

          dotnet-sdk = dotnet-sdk;
          dotnet-runtime = dotnet-runtime;

          executables = [ "sarif-ratchet" ];

          meta = with pkgs.lib; {
            description = "A CLI tool to maintain a SARIF error ratchet";
            license = licenses.mit;
          };
        };
      in
      {
        packages.default = sarif-ratchet;
        apps.default = {
          type = "app";
          program = "${sarif-ratchet}/bin/sarif-ratchet";
        };

        devShells.default = pkgs.mkShell {
          buildInputs = with pkgs; [
            dotnet-sdk_10
            nodejs
            csharp-ls
          ];

          shellHook = ''
            export DOTNET_CLI_TELEMETRY_OPTOUT=1
            export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
            export DOTNET_NOLOGO=1
            echo "SARIF Ratchet Development Environment"
            dotnet --version
            node --version
            npm --version
          '';
        };
      }
    );
}
