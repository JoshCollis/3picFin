using System.Reflection;

// Render tests consume the actual candidate DLL, never a second copy of its JS.
if (args.Length != 2) throw new ArgumentException("Usage: ContinuousScrollAssets DLL OUTPUT");
var assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
Directory.CreateDirectory(args[1]);
const string prefix = "Rowan.Jellyfin.Plugin.Web.";
foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix)))
{
    using var input = assembly.GetManifestResourceStream(name)!;
    using var output = File.Create(Path.Combine(args[1], name[prefix.Length..]));
    input.CopyTo(output);
}
