using Cage.GuestClipboard;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception(message);
}
var directory = Path.Combine(Path.GetTempPath(), "project-drive-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var file = Path.Combine(directory, "drives.json");
    var occupied = new HashSet<char> { 'Z', 'Y' };
    var allocator = new ProjectDriveLetters(file, occupied.Contains);
    var id = new string('a', 16);
    using (var first = allocator.Acquire(id))
    {
        Check(first.Root == @"X:\", "must skip occupied DOS devices");
        Reject(() => allocator.Acquire(id), "must reject a concurrent lease of the same project");
        using var second = allocator.Acquire(new string('b', 16));
        Check(second.Root == @"W:\", "must not share a reserved drive");
    }
    var restarted = new ProjectDriveLetters(file, occupied.Contains);
    using (var first = restarted.Acquire(id)) Check(first.Root == @"X:\", "mapping must survive restart");
    occupied.Add('X');
    Reject(() => restarted.Acquire(id), "must not steal an occupied remembered drive or silently remap it");
    occupied.Remove('X');
    using (var first = restarted.Acquire(id)) Check(first.Root == @"X:\", "failed acquisition must release state");
    Reject(() => restarted.Acquire("../invalid"), "invalid project IDs must not enter the mapping");
    var parallel = new ProjectDriveLetters(Path.Combine(directory, "parallel.json"), _ => false);
    var roots = new System.Collections.Concurrent.ConcurrentBag<string>();
    Parallel.For(0, 23, i => { using var lease = parallel.Acquire(i.ToString("x16")); roots.Add(lease.Root); });
    Check(roots.Distinct().Count() == 23, "distinct projects must keep distinct paths even after release");
    Reject(() => parallel.Acquire(new string('f', 16)), "exhausted mappings must fail rather than reuse another project path");
    var corrupt = Path.Combine(directory, "corrupt.json");
    File.WriteAllText(corrupt, "{\"aaaaaaaaaaaaaaaa\":\"C\"}");
    Reject(() => new ProjectDriveLetters(corrupt, _ => false).Acquire(id), "system drive must never be selected");
    File.WriteAllText(corrupt, "{\"aaaaaaaaaaaaaaaa\":\"Z\",\"bbbbbbbbbbbbbbbb\":\"Z\"}");
    Reject(() => new ProjectDriveLetters(corrupt, _ => false).Acquire(id), "duplicate mappings must fail closed");
    Console.WriteLine("Project drive allocation tests passed.");
}
finally { Directory.Delete(directory, true); }
