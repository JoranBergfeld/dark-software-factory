.PHONY: restore build test pack publish

restore:
	cd dotnet && dotnet restore Dsf.sln --locked-mode

build: restore
	cd dotnet && dotnet build Dsf.sln --no-restore --configuration Release

test: build
	cd dotnet && dotnet test Dsf.sln --no-build --configuration Release

pack: build
	cd dotnet && dotnet pack src/Dsf.Cli/Dsf.Cli.csproj --no-build --configuration Release --output artifacts/release/nuget

publish: build
	cd dotnet && dotnet publish src/Dsf.Cli/Dsf.Cli.csproj --no-build --configuration Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o artifacts/release/linux-x64
