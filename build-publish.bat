dotnet publish ClassIsland.Desktop\ClassIsland.Desktop.csproj -c Release -r win-x64 --self-contained true -p:ClassIsland_SelfContained=true -p:ClassIsland_PlatformTarget=x64 -o out
del -f out *.pdb
powershell Compress-Archive -Path "out" -DestinationPath "out.zip"