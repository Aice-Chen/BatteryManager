using System.Text.RegularExpressions;

// This process is built only by the isolated installer test. Its filename matches the production image
// solely so the same native image-path check can be exercised. It never starts the actual application.
if (args.Length != 1 || !Regex.IsMatch(args[0], @"\ALocal\\BatteryManager\.App\.Test\.[0-9a-f]{32}\z")) return 2;
var directory = Path.GetFullPath(AppContext.BaseDirectory);
if (!directory.Contains(Path.Combine(".artifacts", "tests", "installer-handoff") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
using var mutex = new Mutex(false, args[0]);
File.WriteAllText(Path.Combine(directory, "fixture.ready"), Environment.ProcessId.ToString());
var start = Environment.TickCount64;
while (!File.Exists(Path.Combine(directory, "fixture.stop")) && Environment.TickCount64 - start < 60000)
    await Task.Delay(25);
return 0;
