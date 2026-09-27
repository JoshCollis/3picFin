using System.Reflection;
using System.IO.Compression;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("Usage: VerifyAssembly DLL ZIP VERSION");
var expected = Version.Parse(args[2]);
if (args[2].Split('.').Length != 4) throw new ArgumentException("Expected four-component version");
var actual = AssemblyName.GetAssemblyName(args[0]).Version;
if (actual != expected) throw new InvalidDataException($"Assembly version {actual} != {expected}");
using var zip = ZipFile.OpenRead(args[1]);
using var document = JsonDocument.Parse(zip.GetEntry("meta.json")?.Open() ?? throw new InvalidDataException("Missing meta.json"));
var metaVersion = document.RootElement.GetProperty("version").GetString();
if (metaVersion != args[2]) throw new InvalidDataException($"meta version {metaVersion} != {args[2]}");
Console.WriteLine($"Assembly and package metadata version: {actual}");
