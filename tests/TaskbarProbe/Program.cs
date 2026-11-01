using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.Win32;
using Ven4Tools.Services;

// Запуск в сеансе пользователя ВМ: TaskbarProbe.exe <папка для отчёта>
var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "TaskbarProbe");
Directory.CreateDirectory(outDir);
var log = new StringBuilder();
int pass = 0, fail = 0;
void Log(string s) { var line = $"{DateTime.Now:HH:mm:ss} {s}"; log.AppendLine(line); Console.WriteLine(line); File.WriteAllText(Path.Combine(outDir, "log.txt"), log.ToString(), Encoding.UTF8); }
void Check(bool ok, string what) { if (ok) pass++; else fail++; Log((ok ? "OK   " : "FAIL ") + what); }

void Shot(string name)
{
    Thread.Sleep(2500);
    try
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        using var bmp = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        bmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
    }
    catch (Exception ex) { Log("снимок не снят: " + ex.Message); }
}

// Снимок затронутых ключей реестра «как есть» (значение или его отсутствие).
Dictionary<string, string> RegState()
{
    var d = new Dictionary<string, string>();
    foreach (var s in TaskbarStartService.All.Where(s => s.Key != ""))
    {
        using var k = Registry.CurrentUser.OpenSubKey(s.Key);
        var v = k?.GetValue(s.Name);
        d[s.Key + "\\" + s.Name] = v == null ? "<нет>" : $"{k!.GetValueKind(s.Name)}:{v}";
    }
    return d;
}

var undoPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "taskbar_undo.json");
if (File.Exists(undoPath)) File.Move(undoPath, undoPath + ".before-probe", true);
var svc = TaskbarStartService.Default;
var os = Environment.OSVersion.Version;
Log($"Windows {os} (сборка {os.Build}), Windows 11: {svc.IsWindows11}");
Log("Доступные настройки: " + string.Join(", ", svc.Available.Select(s => s.Id)));

var original = RegState();
var originalAutoHide = svc.Current(TaskbarStartService.All.First(s => s.Id == TaskbarStartService.AutoHideId));
Shot("00_исходно");

// 1. Каждая настройка — каждое значение; после каждого прохода откат.
foreach (var s in svc.Available)
{
    foreach (var (value, label) in s.Choices)
    {
        var r = svc.Apply(new Dictionary<string, int> { [s.Id] = value });
        if (r.Blocked.Count > 0) { Log($"BLOCK {s.Id}={value} ({label}) — Windows не дала изменить (ожидаемо для защищённых)"); continue; }
        Check(svc.Current(s) == value, $"{s.Id}={value} ({label}) применилось");
    }
    var rv = svc.RevertAll();
    var now = RegState();
    var diff = original.Where(kv => now[kv.Key] != kv.Value).Select(kv => $"{kv.Key}: было {kv.Value}, стало {now[kv.Key]}").ToList();
    Check(diff.Count == 0 && rv.Blocked.Count == 0, $"{s.Id}: откат вернул реестр побайтно" + (diff.Count > 0 ? " — " + string.Join("; ", diff) : ""));
}
Check(svc.Current(TaskbarStartService.All.First(s => s.Id == TaskbarStartService.AutoHideId)) == originalAutoHide, "автоскрытие вернулось");

// 2. Повторное «Применить» не затирает «как было».
var any = svc.Available.First(s => s.Id == "TaskView");
svc.Apply(new Dictionary<string, int> { [any.Id] = 0 });
svc.Apply(new Dictionary<string, int> { [any.Id] = 1 });
svc.Apply(new Dictionary<string, int> { [any.Id] = 0 });
svc.RevertAll();
Check(RegState().SequenceEqual(original), "после трёх применений подряд откат вернул исходное");

// 3. Готовые наборы: применить, снять экран, откатить.
int i = 1;
foreach (var p in TaskbarStartService.Presets)
{
    var r = svc.Apply(p.Values);
    Log($"набор «{p.Title}»: заблокировано {r.Blocked.Count} ({string.Join(", ", r.Blocked)}), нужен перезапуск проводника: {r.NeedsExplorerRestart}");
    Shot($"{i++:00}_набор_{p.Title}");
    svc.RevertAll();
    Check(RegState().SequenceEqual(original), $"набор «{p.Title}» откатился побайтно");
}

// 4. Перезапуск проводника: оболочка поднимается и панель на месте.
svc.Apply(new Dictionary<string, int> { ["Seconds"] = 1 });
var sw = Stopwatch.StartNew();
svc.RestartExplorer();
bool back = false;
for (int t = 0; t < 30 && !back; t++) { Thread.Sleep(500); back = Process.GetProcessesByName("explorer").Length > 0; }
Log($"проводник поднялся за {sw.Elapsed.TotalSeconds:0.0} с");
Check(back, "проводник после перезапуска запущен");
Thread.Sleep(4000);
Shot("10_секунды_после_перезапуска");
svc.RevertAll();
svc.RestartExplorer();
Thread.Sleep(5000);
Check(RegState().SequenceEqual(original), "после отката с перезапуском реестр как был");
Shot("11_итог_как_было");

Check(!File.Exists(undoPath) || File.ReadAllText(undoPath).Trim() == "{}", "файл отката пуст после полного отката");
Log($"ИТОГ: прошло {pass}, не прошло {fail}");
Log("done");
return fail == 0 ? 0 : 1;
