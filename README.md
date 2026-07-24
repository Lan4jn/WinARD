# WinARD

WinARD is a Windows remote-desktop client project focused on a secure, maintainable remote access experience.

## Status

The solution scaffold is in place. Application and desktop functionality are not implemented yet.

## Prerequisites

- .NET SDK 8.0 or later (the repository rolls forward from 8.0.100)

## Build and test

```powershell
dotnet restore WinARD.sln
dotnet build WinARD.sln -warnaserror
dotnet test WinARD.sln
```

## Repository hygiene

Do not commit secrets, certificates, private keys, vault files, or visual/design sketches. Keep such material outside the repository or in an approved secure store.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
