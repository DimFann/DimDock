# DimDock
server side code for dimdock.com

webapp looks for "globalSettings.json" under:
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
		"FileIdTimeoutMs": 3000,
		"HiddenFeatures":{
			"DriveMap": {
				"Password": "REBUILD DRIVE MAP PASSWORD, POST TO /Sketch?hf=rebuildDriveMap&password=PASSWORD",
				"EnableRefresh": true,
				"ApiDelayMs": 500,
				"RefreshIntervalMinutes": 1440,
				"CacheFile": "Will be set to /web/dimdock.drivemap.cache.json, if not specified. Must have ReadWrite access."
			}
		}
	}
}
```
