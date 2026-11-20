using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cage.GuestClipboard;

// Keep paths stable: Claude and the host's workspace history remember drive roots.
// Never recycle another project's letter just because its current mount is closed.
internal sealed class ProjectDriveLetters(string file, Func<char, bool> occupied)
{
    private readonly object gate = new();
    private readonly HashSet<char> leased = [];

    internal Lease Acquire(string id)
    {
        if (!Regex.IsMatch(id, "\\A[a-f0-9]{16}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("project_grant_invalid");
        lock (gate)
        {
            RejectReparse(file + ".lock");
            // Another guest helper must not concurrently rewrite the persistent map.
            using var exclusive = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            RejectReparse(file);
            var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(file))
            {
                if (new FileInfo(file).Length > 16384) throw new InvalidDataException("project_drive_map_invalid");
                try { mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? throw new InvalidDataException("project_drive_map_invalid"); }
                catch (JsonException error) { throw new InvalidDataException("project_drive_map_invalid", error); }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, value) in mappings)
                    if (!Regex.IsMatch(key, "\\A[a-f0-9]{16}\\z", RegexOptions.CultureInvariant) ||
                        value is null || value.Length != 1 || value[0] < 'D' || value[0] > 'Z' || !seen.Add(value))
                        throw new InvalidDataException("project_drive_map_invalid");
            }
            if (!mappings.TryGetValue(id, out var selected))
            {
                for (char candidate = 'Z'; candidate >= 'D'; candidate--)
                    if (!mappings.ContainsValue(candidate.ToString()) && !leased.Contains(candidate) && !occupied(candidate))
                    { selected = candidate.ToString(); break; }
                if (selected is null) throw new InvalidDataException("project_drive_letters_exhausted");
                mappings.Add(id, selected);
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var writer = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { JsonSerializer.Serialize(writer, mappings); writer.Flush(true); }
                    File.Move(temporary, file, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var letter = selected[0];
            if (leased.Contains(letter) || occupied(letter)) throw new InvalidDataException("project_drive_letter_busy");
            leased.Add(letter);
            return new Lease(this, letter);
        }
    }

    private static void RejectReparse(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("project_drive_map_invalid");
    }

    internal sealed class Lease(ProjectDriveLetters owner, char letter) : IDisposable
    {
        private int disposed;
        internal string Root => letter + @":\";
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (owner.gate) owner.leased.Remove(letter);
        }
    }
}
