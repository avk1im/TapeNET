Get-ChildItem -Recurse -Include *.cs, *.csproj, *.sln | 
    Select-Object @{Name="Path"; Expression={$_.FullName.Replace((Get-Item .).FullName + "\", "")}} | 
    Out-File "tree_cs_csproj_only.txt"
