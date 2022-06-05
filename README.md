# DimDock
Server-side code for ricyart.net/gallery (originally used for dimdock.com)

The webapp looks for "globalSettings.json" under:
```
C:\web\globalSettings.json (Windows)
or
/web/globalSettings.json (Linux)
```

**globalSettings.json**
```
{
	"DimDock":{
		"Page":"https://www.dimdock.com/sketch/",
		"ApiUrlGetFiles":"https://www.googleapis.com/drive/v3/files",
		"ApiUrlGetFile":"https://www.googleapis.com/drive/v3/files/fileId",
		"ApiKey":"GOOGLE DRIVE API KEY GOES HERE",
		"RootId":"GDRIVE ID OF ROOT FOLDER",
		"ResourceKey":"RESOURCE KEY FOR ROOT FOLDER",
		"FilesTimeoutMs":6000,
		"FileIdTimeoutMs":3000,
	}
}
```
# How to deploy
Make sure you're running at least .NET 6.0, clone the repository anywhere you'd like, then create the global settings file,
making sure to fill in any field that's occupied by a placeholder, then build and run the project with
```
dotnet build
dotnet run
```
within the project directory.

This webapp is configured to work on the root directory of your domain by default. If you wish to change this
(for instance, you want this webapp to work on "https://example.domain/gallery"), open
```
DimDock.LinuxArchive/appsettings.json
```
with any text editor of your choice, then fill in the "Subdirectory" field with a subdirectory you want this
webapp to work on, following this example:
```
  "AppConfig": {
        "Subdirectory": "/gallery",
  }
```
