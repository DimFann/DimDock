using DimDock.SketchArchiveLib.Google;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using SketchArchiveLib.Google;
using System.Runtime.InteropServices;

string globalSettings;
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    globalSettings = @"C:\web\globalSettings.json";
else
    globalSettings = "/web/globalSettings.json";

if (string.IsNullOrWhiteSpace(globalSettings) || !File.Exists(globalSettings))
    throw new Exception("Missing globalSettings");

var configuration = new ConfigurationBuilder()
    .AddJsonFile(globalSettings)
    .Build();

var apiKey = configuration["DimDock:ApiKey"];
var urlGetFiles = configuration["DimDock:ApiUrlGetFiles"];
var rootFolderId = configuration["DimDock:RootId"];
var rootResourceKey = configuration["DimDock:ResourceKey"];

var logger = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
}).CreateLogger("Sandbox");

GDriveReader reader = new(apiKey, urlGetFiles);

var map = DriveMap.BuildMap(reader, rootFolderId, rootResourceKey, 300, logger);

//var map = DriveMap.LoadMap(@"C:\web\dimdock.drivemap.cache.json");
var jsonMap = JsonSerializer.Serialize(map, new JsonSerializerOptions()
{
    WriteIndented = true,
    IncludeFields = true,
});
Console.WriteLine(jsonMap);

DriveMap.SaveMap(@"C:\web\dimdock.drivemap.cache.json", map, logger);
