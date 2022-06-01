# DimDock
server side code for dimdock.com

webapp looks for "glabalSettings.json" under:
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
